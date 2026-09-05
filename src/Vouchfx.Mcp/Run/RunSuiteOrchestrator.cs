using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// REQ-006's <c>run_suite</c> orchestration: the full gate ordering (argument safety → pre-flight
/// validation → CLI handshake → single-flight concurrency → the actual run) plus EDGE-001's
/// environment-error classification and EDGE-002's cancellation/timeout handling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate ordering matters</b> (cheapest and safest checks first, so a bad call never reaches a
/// process spawn it did not need to):
/// </para>
/// <list type="number">
/// <item><description>
/// Argument safety: a <c>path</c> beginning with <c>-</c> would be misread as a CLI option once
/// spliced into an argument list downstream; every <c>tag</c> is validated the same way (no leading
/// <c>-</c>, not null/empty/whitespace, bounded count and length — see
/// <see cref="MaxTagCount"/>/<see cref="MaxTagLength"/>), since a tag is spliced into the CLI's own
/// argument list exactly like the path is, and a malformed MCP payload can legally put a null
/// element inside a JSON string array regardless of this server's own nullable-reference-type
/// annotations; an out-of-range <c>timeoutSeconds</c> is rejected outright (documented bounds — see
/// <see cref="MinTimeoutSeconds"/>/<see cref="MaxTimeoutSeconds"/>) rather than silently clamped, so
/// a caller is never surprised by a budget it did not ask for.
/// </description></item>
/// <item><description>
/// EDGE-003 pre-validation via <see cref="ValidationWorkerClient.ValidateAsync"/> — the SAME
/// isolated worker <c>validate_suite</c> uses, never the CLI and never a container. Its own first
/// action is <see cref="SuiteValidator.CheckFastRejects"/> (missing file, UNC path, and — when the
/// host launched this server with <c>--workspace</c> — US-S3-08 containment against that root), so a
/// single call here covers the "fast reject" and "unparseable/schema-invalid" cases the spec lists
/// separately — an invalid or escaping suite path never reaches the CLI handshake or a process spawn.
/// </description></item>
/// <item><description>
/// REQ-008's CLI presence + version handshake (<see cref="CliPinVerifier"/>) — unchanged from todo 6.
/// </description></item>
/// <item><description>
/// Single-flight concurrency: at most one run may be in progress on this server instance at a time.
/// The check-and-claim is a non-blocking <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// — a second concurrent call is rejected immediately, never queued. This gate's release (the
/// <c>finally</c> in <see cref="RunAsync"/>) depends entirely on the injected <see cref="ISuiteRunner"/>
/// always returning — see <see cref="VouchfxCliSuiteRunner"/>'s remarks on the BLOCKER (a relay drain
/// that could hang forever on a surviving child process) an earlier version of this had, which would
/// have wedged this very gate permanently.
/// </description></item>
/// <item><description>The actual run, via the injected <see cref="ISuiteRunner"/>.</description></item>
/// </list>
/// <para>
/// <b>EDGE-001 (environment error, never Fail):</b> the AUTHORITATIVE source is the events file's own
/// <c>scenario-completed</c>/<c>environment-error</c> events, elevated per §12.1 precedence (see
/// <see cref="RunVerdictExtensions.Elevate"/>). When the run failed so early that NO
/// <c>scenario-completed</c> event exists at all, the CLI's own exit code becomes the fallback — and
/// because <see cref="VouchfxCliSuiteRunner"/> always passes <c>--fail-on-env-error
/// --fail-on-inconclusive</c>, that exit code is a clean 1:1 map of all four verdicts (0=Pass, 1=Fail,
/// 3=EnvironmentError, 4=Inconclusive — see <c>Vouchfx.Cli.ExitCodes</c> in the engine repo). Any
/// OTHER exit code (a usage error, an unhandled crash before the CLI's own exit-code logic could even
/// run) is deliberately classified as <see cref="RunVerdict.EnvironmentError"/>, never
/// <see cref="RunVerdict.Fail"/> — the defensive default the spec requires.
/// </para>
/// <para>
/// <b>EDGE-002 (cancellation/timeout):</b> a bounded <c>timeoutSeconds</c> budget is layered on top
/// of the caller's own <see cref="CancellationToken"/> via a linked source. When either fires before
/// the run completes, <see cref="ISuiteRunner"/> reports <see cref="RunTermination.Aborted"/> (its own
/// termination has already run by the time it returns — see <see cref="VouchfxCliSuiteRunner"/>'s
/// remarks for exactly what that does and does not accomplish) and this type reports the result as
/// <c>Inconclusive</c>, distinguishing <c>Cancelled</c> (the caller's own token fired) from
/// <c>TimedOut</c> (the budget fired) purely by re-checking the ORIGINAL, unlinked token afterwards —
/// <see cref="ISuiteRunner"/> itself never needs to know which.
/// </para>
/// <para>
/// <b>Events file reading is bounded</b> (<see cref="EventsFileReader.MaxEventsFileBytes"/>, via the
/// shared <see cref="EventsFileReader"/> — also used by <c>ExplainRunOrchestrator</c>, REQ-007): the
/// file is agent-influenced content (step ids, <c>script.csharp</c> output, observation payloads from
/// a suite the caller supplied), and every other boundary in this server caps what it reads into
/// memory — this one is no exception. A file larger than the cap is read only up to that many bytes
/// and the result carries <see cref="RunSuiteResult.EventsTruncated"/><c> = true</c> rather than
/// throwing; whatever complete lines fit within the cap are still parsed normally.
/// </para>
/// <para>
/// <b>Run registry (REQ-007, US-S3-01):</b> the injected <see cref="IRunRegistry"/> is written at
/// THREE points inside <see cref="ExecuteRunAsync"/> — <see cref="IRunRegistry.StartRun"/> the moment
/// the run begins (which is also what MINTS the run id and the events-file path), and
/// <see cref="IRunRegistry.RecordStatusTransition"/> with the final verdict when it ends, on the
/// success path and in the catch arm respectively (see below for how each is guarded). Every
/// ATTEMPTED run is recorded, an ordinary completion and an aborted/cancelled/timed-out one alike;
/// a call rejected by an earlier gate is not, because nothing was attempted and there is no run to
/// have a status. <c>explain_run</c> reads the registry when its own caller omits <c>eventsPath</c>.
/// This replaces the session-scoped <c>ILastRunTracker</c>, which recorded only at completion and
/// only in memory.
/// </para>
/// <para>
/// <b>All THREE registry writes are guarded, and only the first of them may speak.</b> There are
/// three, not two: <see cref="IRunRegistry.StartRun"/>, the completing transition on the SUCCESS
/// path, and the completing transition in the catch arm.
/// <list type="number">
/// <item><description>
/// <see cref="IRunRegistry.StartRun"/> is the first thing here that touches the disk on the server's
/// own behalf, and it happens BEFORE anything is spawned — so a storage failure there is caught and
/// rendered as <see cref="RunSuiteOutcome.RunNotRecorded"/> (<c>VFX-E-1502</c>) rather than escaping
/// as an uncoded framework exception. It is the one write whose failure the CALLER hears about,
/// because it is the one whose failure means nothing ran.
/// </description></item>
/// <item><description>
/// The SUCCESS-path completing write happens after the engine has already produced a verdict. A
/// storage fault there used to rethrow through the outer catch and destroy that verdict — the caller
/// got an uncoded exception instead of the result the run genuinely reached, and the entry stayed
/// <c>running</c> either way (a peer review's MAJOR finding). It therefore sits in its own swallowing
/// try/catch and the outcome is returned regardless: a run that produced a verdict reports it even
/// when the bookkeeping behind it failed. The failure is not silent — it goes to stderr, naming the
/// run id and the exception's TYPE only, following <see cref="BuildRunNotRecordedMessage"/>'s policy.
/// </description></item>
/// <item><description>
/// The catch arm's completing write must never be allowed to speak at all, because the run has
/// already failed: it sits in its own swallowing try/catch so a bookkeeping failure cannot replace
/// the exception the caller actually needs to see, and the original is rethrown unconditionally.
/// </description></item>
/// </list>
/// The two completing writes are guarded for the same reason stated two ways: bookkeeping is never
/// allowed to become the thing the caller diagnoses.
/// </para>
/// </remarks>
public sealed class RunSuiteOrchestrator
{
    /// <summary>Used when the caller omits <c>timeoutSeconds</c>.</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>The smallest <c>timeoutSeconds</c> this server accepts.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>The largest <c>timeoutSeconds</c> this server accepts.</summary>
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>The largest number of tags a single <c>run_suite</c> call accepts.</summary>
    public const int MaxTagCount = 50;

    /// <summary>The largest length, in characters, a single tag may have.</summary>
    public const int MaxTagLength = 200;

    /// <summary>
    /// How old (by last-write time) a leftover <c>vouchfx-mcp-events-*.jsonl</c> temp file must be
    /// before <see cref="SweepStaleEventsFilesBestEffort"/> deletes it.
    /// </summary>
    private const int StaleEventsFileRetentionHours = 24;

    // Mirrors Vouchfx.Cli.ExitCodes in the engine repo exactly (see that type's own remarks) — this
    // server never references the engine assembly, so the four values are duplicated here, by
    // reference to their source, rather than shared.
    private const int ExitCodePass = 0;
    private const int ExitCodeFail = 1;
    private const int ExitCodeEnvironmentError = 3;
    private const int ExitCodeInconclusive = 4;

    private readonly CliPinVerifier _cliPinVerifier;
    private readonly ISuiteRunner _suiteRunner;
    private readonly IRunRegistry _runRegistry;
    private readonly Workspace? _workspace;
    private int _runInProgress;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the writer's half of the seam <c>explain_run</c> reads. Also the
    /// authority on WHERE this run's events file goes (see <see cref="IRunRegistry.StartRun"/>): with
    /// a workspace configured that is inside the workspace's own output directory, and without one it
    /// is the OS temp directory, exactly as before.
    /// </param>
    /// <param name="workspace">
    /// The workspace resolved at server start (US-S3-08), or <see langword="null"/> when none was
    /// configured. Reaches the suite path through the EDGE-003 pre-flight below — this orchestrator
    /// does not check containment a second time before the CLI spawn, because that spawn is
    /// unreachable unless the pre-flight already passed the same path.
    /// </param>
    public RunSuiteOrchestrator(
        CliPinVerifier cliPinVerifier,
        ISuiteRunner suiteRunner,
        IRunRegistry runRegistry,
        Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(cliPinVerifier);
        ArgumentNullException.ThrowIfNull(suiteRunner);
        ArgumentNullException.ThrowIfNull(runRegistry);

        _cliPinVerifier = cliPinVerifier;
        _suiteRunner = suiteRunner;
        _runRegistry = runRegistry;
        _workspace = workspace;
    }

    /// <summary>Runs the full gate sequence described in this type's remarks, then the suite itself.</summary>
    /// <param name="path">Path to the <c>.e2e.yaml</c> suite file.</param>
    /// <param name="tags">Zero or more tag filters; <see langword="null"/> or empty runs the whole suite.</param>
    /// <param name="timeoutSeconds">
    /// Overrides <see cref="DefaultTimeoutSeconds"/>. Must be within
    /// [<see cref="MinTimeoutSeconds"/>, <see cref="MaxTimeoutSeconds"/>] or the call is rejected as
    /// <see cref="RunSuiteOutcome.InvalidArgument"/> before anything is spawned.
    /// </param>
    /// <param name="onProgress">
    /// Invoked with a short human-readable message at each notable point: run start, each relayed
    /// child output line, and each event narrated once the run completes (see
    /// <see cref="SuiteEventParser"/>'s remarks on why the latter is a narration, not a live feed).
    /// <see langword="null"/> is accepted — a caller that does not want progress simply omits it.
    /// </param>
    public async Task<RunSuiteOutcome> RunAsync(
        string path,
        IReadOnlyList<string>? tags,
        int? timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var effectiveTags = tags ?? [];

        // Guards TWO command lines, not just the engine CLI's. The obvious one is the `vouchfx run`
        // spawn below, where a leading '-' would be read as an option. The non-obvious one is the
        // validation worker: this method's EDGE-003 pre-flight calls ValidationWorkerClient
        // DIRECTLY, bypassing Tools.ValidateSuiteInput.TryResolve — so the VFX-E-1152 rejection of a
        // path literally named `--yaml-stdin` (ValidationWorkerProtocol.InlineYamlArgument, an
        // in-band discriminator in the same argument position as the path) never runs on this path.
        // This check is what covers it here, since that literal begins with a dash. See
        // ValidationWorkerProtocol.InlineYamlArgument's remarks: the two guards cover disjoint entry
        // points and neither is redundant.
        if (path.StartsWith('-'))
        {
            return new RunSuiteOutcome.InvalidArgument(
                $"Path must not begin with '-': '{TextSanitiser.SanitiseForDisplay(path)}'. A leading " +
                "'-' would be interpreted as a command-line option, not a file path.");
        }

        var tagValidationError = ValidateTags(effectiveTags);
        if (tagValidationError is not null)
        {
            return new RunSuiteOutcome.InvalidArgument(tagValidationError);
        }

        var effectiveTimeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        if (effectiveTimeoutSeconds < MinTimeoutSeconds || effectiveTimeoutSeconds > MaxTimeoutSeconds)
        {
            return new RunSuiteOutcome.InvalidArgument(
                $"timeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}; got " +
                $"{effectiveTimeoutSeconds}.");
        }

        // US-S3-08 review fix: run_suite's `path` is documented "absolute or workspace-relative"
        // like every other, so a relative path is rebased onto the workspace root ONCE, here, and
        // the rebased value is what both the pre-flight below AND the engine CLI's argument list
        // receive. Rebasing only inside ValidationWorkerClient would have validated one file and run
        // another whenever the workspace root differs from this process's current directory. A no-op
        // with no workspace configured, and a no-op for an already-absolute path (idempotent, so the
        // pre-flight's own identical call has nothing left to do).
        //
        // Deliberately AFTER the leading-'-' check above: that check is about the RAW token a caller
        // sent, and rebasing would bury a leading dash mid-path where it no longer looks like one.
        path = PathSafetyGuard.ResolveCallerPath(path, _workspace);

        // EDGE-003: the SAME isolated worker validate_suite uses. Its own first action is
        // SuiteValidator.CheckFastRejects (missing file, UNC path, and — with a workspace
        // configured — US-S3-08 containment) — no worker spawn for those — so this one call covers
        // both "fast reject" and "unparseable/schema-invalid" without a second, redundant check
        // here. Passing _workspace is also what keeps run_suite's gate identical to validate_suite's
        // rather than a laxer copy of it: an escaping path is refused before the engine CLI is ever
        // handed it.
        var validation = await ValidationWorkerClient.ValidateAsync(path, _workspace, cancellationToken: cancellationToken);
        if (!validation.Valid)
        {
            return new RunSuiteOutcome.SuiteInvalid(validation);
        }

        var pinResult = await _cliPinVerifier.VerifyAsync(cancellationToken);
        if (pinResult is not CliPinResult.Ok)
        {
            return new RunSuiteOutcome.CliUnavailable(DescribeGateFailure(pinResult));
        }

        if (Interlocked.CompareExchange(ref _runInProgress, 1, 0) != 0)
        {
            return new RunSuiteOutcome.AlreadyRunning(
                "Another run_suite call is already in progress on this server; only one run may be " +
                "active at a time. Wait for it to finish before retrying.");
        }

        try
        {
            return await ExecuteRunAsync(path, effectiveTags, effectiveTimeoutSeconds, onProgress, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _runInProgress, 0);
        }
    }

    /// <summary>
    /// Rejects a tag that is null/empty/whitespace, begins with <c>-</c> (flag-injection into the
    /// CLI's own argument parser — the same threat <c>path</c>'s leading-<c>-</c> guard closes), or
    /// exceeds <see cref="MaxTagLength"/>; rejects the whole list if it exceeds
    /// <see cref="MaxTagCount"/>. A malformed MCP payload can legally place a JSON <c>null</c> inside
    /// a string array regardless of this server's own compile-time nullable-reference-type
    /// annotations, so the null check runs before anything else touches the tag.
    /// </summary>
    private static string? ValidateTags(IReadOnlyList<string> tags)
    {
        if (tags.Count > MaxTagCount)
        {
            return $"Too many tags: {tags.Count} supplied, at most {MaxTagCount} are accepted.";
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return "Tags must not be null, empty, or whitespace-only.";
            }

            if (tag.StartsWith('-'))
            {
                return $"Tag must not begin with '-': '{TextSanitiser.SanitiseForDisplay(tag)}'. A leading " +
                       "'-' would be interpreted as a command-line option, not a tag value.";
            }

            if (tag.Length > MaxTagLength)
            {
                return $"Tag exceeds the {MaxTagLength}-character limit ({tag.Length} characters).";
            }
        }

        return null;
    }

    private async Task<RunSuiteOutcome> ExecuteRunAsync(
        string path,
        IReadOnlyList<string> tags,
        int timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        SweepStaleEventsFilesBestEffort();

        // US-S3-01 write point 1 of 2: the run is recorded as `running` BEFORE anything is spawned,
        // and the registry — not this orchestrator — decides the events-file path, because where a
        // run's artefacts live is a property of the storage backend (see IRunRegistry.StartRun).
        // With a workspace configured that path is inside the workspace's output directory, which is
        // both what makes the record survive a restart and why explain_run's containment check now
        // passes over it naturally.
        RunRegistryEntry registryEntry;
        try
        {
            registryEntry = _runRegistry.StartRun([path]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Since US-S3-01 this call is the first thing in run_suite that TOUCHES THE DISK on the
            // server's own behalf — the file-backed registry creates the run's directory, writes a
            // temp document, and renames it, all before anything is spawned. A read-only workspace
            // root, an exhausted disk, or an ACL the server does not satisfy therefore failed the
            // whole tool call with a bare framework exception carrying a stack trace and no VFX code,
            // which is precisely the uncoded-failure hole the taxonomy exists to close. Rendered as a
            // catalogued, retryable error instead: disk conditions clear, and the identical call then
            // succeeds. The caught set is explicit rather than a blanket catch so a genuine
            // programming error (an ArgumentException from a malformed spec path, say) still surfaces
            // as the bug it is.
            return new RunSuiteOutcome.RunNotRecorded(BuildRunNotRecordedMessage(ex));
        }

        try
        {
            var outcome = await ExecuteRegisteredRunAsync(
                registryEntry.EventsFilePath, path, tags, timeoutSeconds, onProgress, cancellationToken);

            // US-S3-01 write point 2 of 3: a single choke point recording EVERY attempted run (an
            // ordinary completion and an aborted/cancelled/timed-out one alike — both funnel through
            // RunSuiteOutcome.Completed, see this type's remarks) so explain_run can default to it.
            // A call rejected by an earlier gate never reaches here at all — nothing was attempted,
            // so there is no run whose status could change.
            //
            // GUARDED, and that guard is the point (a peer review's MAJOR finding). By the time this
            // line runs the engine has ALREADY produced a verdict; unguarded, a storage fault here
            // rethrew through the outer catch below and the caller got an uncoded framework exception
            // in place of the result the run actually reached — while the entry stayed `running`
            // regardless, so nothing was even bought by failing. The outcome is returned either way.
            try
            {
                _runRegistry.RecordStatusTransition(
                    registryEntry.RunId, RunRegistryStatus.Completed, outcome.Result.Verdict);
            }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, and the whole
            // point of this arm: a run that produced a verdict must report it even when the
            // bookkeeping behind it failed. The set is not narrowed to the filesystem family the
            // StartRun arm names, because unlike that one this catch changes NOTHING the caller sees
            // — there is no outcome to choose and no code to mint, only a record that will be missing.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ReportCompletionNotRecorded(registryEntry.RunId, ex);
            }

            return outcome;
        }
        catch (Exception)
        {
            // US-S3-01 write point 3 of 3. An exception escaping the run means it reached NO verdict
            // — which is exactly what Inconclusive means (§12.1; never Fail, which would assert a
            // defect nobody observed).
            // Without this the entry would stay `running` forever and, being the most recent entry,
            // would make every later list_runs report a phantom in-flight run. The ORIGINAL exception
            // is what the caller gets either way: the bookkeeping write is wrapped in its own
            // try/catch below, and then rethrown unconditionally.
            try
            {
                _runRegistry.RecordStatusTransition(
                    registryEntry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Inconclusive));
            }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate, and the whole
            // point of this arm: a failed record must never mask the cause. The registry write is a
            // full-disk/permissions-prone filesystem operation on this path, so without this the
            // bookkeeping exception would REPLACE the exception that actually ended the run — leaving
            // the caller diagnosing storage instead of the real failure, and the entry stuck at
            // `running` regardless.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            throw;
        }
    }

    /// <summary>
    /// Announces, on stderr, that a run reached a verdict but its COMPLETING registry write failed —
    /// the one thing the caller is deliberately not told, since it gets the verdict instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>stderr, never stdout</b> — stdout is the JSON-RPC channel and a stray byte there corrupts
    /// every frame a connected agent reads (see <c>Program.cs</c>'s own header). Written directly
    /// rather than through <c>Log</c>'s source-generated <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/>
    /// delegates for a structural reason, not a stylistic one: this type is constructed EAGERLY by
    /// <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/>, outside the DI graph and before
    /// the host that owns the logging providers exists, so no <see cref="Microsoft.Extensions.Logging.ILogger"/>
    /// reaches it. Production logging is redirected to this same stream anyway
    /// (<c>Program.cs</c> sets <c>LogToStandardErrorThreshold = Trace</c>), so the channel is identical
    /// — only the formatting is.
    /// </para>
    /// <para>
    /// <b>Content policy is <see cref="BuildRunNotRecordedMessage"/>'s, unchanged:</b> the run id (a
    /// server-minted <c>run-</c> plus 32 hex characters — never caller text) and the exception's TYPE
    /// NAME, and nothing else. The exception's own <c>Message</c> is not forwarded, because BCL
    /// filesystem exceptions routinely embed a full path.
    /// </para>
    /// </remarks>
    private static void ReportCompletionNotRecorded(string runId, Exception failure)
    {
        Console.Error.WriteLine(
            $"vouchfx-mcp: run '{TextSanitiser.SanitiseForDisplay(runId)}' produced a verdict, but " +
            $"recording its completion in the run registry failed " +
            $"({TextSanitiser.SanitiseForDisplay(failure.GetType().Name)}). The verdict was returned " +
            "to the caller; the registry entry stays 'running'.");
    }

    /// <summary>
    /// The message <see cref="RunSuiteOutcome.RunNotRecorded"/> carries — names the directory the
    /// registry was writing into and the OS failure's TYPE, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The exception's own <c>Message</c> is deliberately not forwarded</b>, following
    /// <c>PinFailureReporting</c>'s standing policy: BCL filesystem exceptions routinely embed a full
    /// path (sometimes one the caller never named), and a control-character-only sanitiser would pass
    /// it straight through. The type name is the actionable part — a
    /// <see cref="UnauthorizedAccessException"/> and a full disk want different fixes — and the
    /// directory is stated from THIS server's own configuration rather than quoted back out of the
    /// exception. It goes through <see cref="PathSafetyGuard.CapAndSanitisePathForDisplay"/>, the same
    /// bounded rendering every path echoed into a message uses.
    /// <para>
    /// With no workspace configured the registry is <see cref="InMemoryRunRegistry"/>, which writes
    /// nothing and therefore cannot reach this path in production; the message says "the run
    /// registry's storage" rather than naming a directory in that case, instead of inventing one.
    /// </para>
    /// </remarks>
    private string BuildRunNotRecordedMessage(Exception failure)
    {
        var location = _workspace is null
            ? "the run registry's storage"
            : $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(_workspace.OutputDir)}'";

        return $"The run could not be recorded before it started: writing to {location} failed " +
               $"({TextSanitiser.SanitiseForDisplay(failure.GetType().Name)}). Nothing was run. Check " +
               "that the directory exists, is writable by the account running this server, and that " +
               "the volume is not full, then retry.";
    }

    /// <summary>
    /// The run itself, once <see cref="IRunRegistry.StartRun"/> has recorded it and decided where its
    /// events file goes — split out from <see cref="ExecuteRunAsync"/> so that method reads as
    /// exactly what it is: record, run, record the result, with a single catch that cannot let a run
    /// stay recorded as in-flight forever.
    /// </summary>
    private async Task<RunSuiteOutcome.Completed> ExecuteRegisteredRunAsync(
        string eventsFilePath,
        string path,
        IReadOnlyList<string> tags,
        int timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var spec = new SuiteRunSpec(path, tags, eventsFilePath);

        onProgress?.Invoke("Starting vouchfx CLI...");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        SuiteProcessResult processResult;
        try
        {
            processResult = await _suiteRunner.RunAsync(spec, line => onProgress?.Invoke(line), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Defensive: a runner that throws instead of reporting Aborted when ITS OWN passed
            // token fires is still handled as a bounded timeout, not an unhandled exception
            // escaping the tool handler.
            processResult = new SuiteProcessResult(null, RunTermination.Aborted);
        }

        RunSuiteOutcome.Completed outcome;
        if (processResult.Termination == RunTermination.Aborted)
        {
            outcome = BuildAbortedOutcome(processResult, timeoutSeconds, onProgress, eventsFilePath, cancellationToken);
        }
        else
        {
            try
            {
                outcome = await BuildCompletedOutcomeAsync(processResult, eventsFilePath, onProgress, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // A cancellation/timeout DURING the events-file read/parse — NOT during the suite
                // run itself, which already completed normally by this point. EventsFileReader now
                // lets a genuine cancellation propagate (a review fix) rather than silently
                // degrading it to "could not be read", so this maps it to the SAME structured
                // cancelled/timed-out Inconclusive outcome EDGE-002 already uses for a
                // cancellation DURING the run itself, rather than letting the exception crash the
                // whole tool call.
                outcome = BuildAbortedOutcome(processResult, timeoutSeconds, onProgress, eventsFilePath, cancellationToken);
            }
        }

        return outcome;
    }

    private static async Task<RunSuiteOutcome.Completed> BuildCompletedOutcomeAsync(
        SuiteProcessResult processResult, string eventsFilePath, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        // Bounded by the SAME linked timeout/caller token the suite run itself was — without this,
        // a large events-file read/parse could run past the caller's own declared timeout budget
        // (or an explicit MCP-level cancellation) unbounded, delaying shutdown under load even
        // though the read itself already supports cancellation (a review fix).
        var (eventsContent, eventsTruncated) = await EventsFileReader.TryReadBoundedAsync(eventsFilePath, cancellationToken);
        if (eventsTruncated)
        {
            onProgress?.Invoke(
                $"Warning: the events file exceeded {EventsFileReader.MaxEventsFileBytes:N0} bytes and " +
                "was truncated before parsing; the result below may be incomplete.");
        }

        var summary = SuiteEventParser.Parse(eventsContent ?? string.Empty, onProgress);

        var (verdict, remediationHint) = summary.AggregateVerdict is { } aggregateVerdict
            ? (aggregateVerdict, aggregateVerdict == RunVerdict.EnvironmentError
                ? BuildRemediationHintFromEnvironmentErrors(summary.EnvironmentErrors)
                : null)
            : ClassifyFallbackVerdict(processResult.ExitCode, processResult.StderrExcerpt ?? string.Empty);

        onProgress?.Invoke($"Run finished: {verdict}.");

        return new RunSuiteOutcome.Completed(new RunSuiteResult(
            Verdict: verdict.ToString(),
            ExitCode: processResult.ExitCode,
            Cancelled: false,
            TimedOut: false,
            RemediationHint: remediationHint,
            Steps: summary.Steps,
            EventsFilePath: eventsFilePath,
            EventsTruncated: eventsTruncated));
    }

    private static RunSuiteOutcome.Completed BuildAbortedOutcome(
        SuiteProcessResult processResult,
        int timeoutSeconds,
        Action<string>? onProgress,
        string eventsFilePath,
        CancellationToken cancellationToken)
    {
        // Distinguishes "the CALLER's own token fired" from "the timeout budget layered on top of
        // it fired" purely by re-checking the ORIGINAL, unlinked token now that the run is over —
        // ISuiteRunner itself only ever saw the linked token, and never needed to know which of the
        // two caused it to fire.
        var wasCallerCancelled = cancellationToken.IsCancellationRequested;

        onProgress?.Invoke(wasCallerCancelled ? "Run cancelled." : $"Run timed out after {timeoutSeconds}s.");

        return new RunSuiteOutcome.Completed(new RunSuiteResult(
            Verdict: RunVerdict.Inconclusive.ToString(),
            ExitCode: processResult.ExitCode,
            Cancelled: wasCallerCancelled,
            TimedOut: !wasCallerCancelled,
            RemediationHint: wasCallerCancelled
                ? null
                : $"The run did not complete within {timeoutSeconds}s and was terminated.",
            Steps: [],
            EventsFilePath: eventsFilePath));
    }

    /// <summary>
    /// Best-effort retention sweep for leftover <c>vouchfx-mcp-events-*.jsonl</c> temp files: they
    /// are never deleted after a run (a later <c>explain_run</c> call is expected to read one by its
    /// returned path), so without SOME cleanup they would accumulate in the OS temp directory
    /// indefinitely. Deletes any such file
    /// whose last-write time is older than <see cref="StaleEventsFileRetentionHours"/>. Every failure
    /// (a locked file, a permissions problem, the temp directory itself being briefly unavailable) is
    /// swallowed — this is housekeeping, not a correctness requirement, and must never affect the run
    /// it is called alongside.
    /// <para>
    /// <b>Since US-S3-01 this sweeps NO-WORKSPACE mode's artefacts only.</b> With a workspace
    /// configured, <see cref="FileRunRegistry"/> places a run's events file beside its metadata under
    /// the workspace's output directory, where nothing here can (or should) delete it: those files
    /// are the persistent record a later server process reads, and retiring them is the host's call,
    /// not this server's. The sweep still runs unconditionally because it also clears residue left by
    /// servers that predate the registry, and because it costs one directory enumeration that finds
    /// nothing when there is nothing to find.
    /// </para>
    /// </summary>
    private static void SweepStaleEventsFilesBestEffort()
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - TimeSpan.FromHours(StaleEventsFileRetentionHours);
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "vouchfx-mcp-events-*.jsonl"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: this is
        // best-effort housekeeping alongside a real run; any unexpected failure enumerating or
        // stat-ing the temp directory must never affect the run itself.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static (RunVerdict Verdict, string? RemediationHint) ClassifyFallbackVerdict(int? exitCode, string stderrExcerpt)
    {
        // No scenario-completed event was found at all — the exit code is the only signal left.
        // Because VouchfxCliSuiteRunner always passes --fail-on-env-error --fail-on-inconclusive,
        // this is a clean 1:1 map of the four verdicts for the three expected codes; anything else
        // (a usage error, or an unhandled crash before the CLI's own exit-code logic could even run)
        // is deliberately classified as EnvironmentError, never Fail (EDGE-001's defensive default).
        return exitCode switch
        {
            ExitCodePass => (RunVerdict.Pass, null),
            ExitCodeFail => (RunVerdict.Fail, null),
            ExitCodeEnvironmentError => (RunVerdict.EnvironmentError, BuildDockerRemediationHint(stderrExcerpt)),
            ExitCodeInconclusive => (RunVerdict.Inconclusive, null),
            _ => (RunVerdict.EnvironmentError, BuildDockerRemediationHint(stderrExcerpt)),
        };
    }

    private static string BuildDockerRemediationHint(string stderrExcerpt)
    {
        var mentionsDocker =
            stderrExcerpt.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
            stderrExcerpt.Contains("daemon", StringComparison.OrdinalIgnoreCase);

        return mentionsDocker
            ? "The run could not start because Docker appears to be unavailable. Ensure the Docker " +
              "daemon is running and reachable, then retry."
            : "The run ended with an environment error before completing. Check that Docker is " +
              "running and any required container images/registries are reachable, then retry.";
    }

    private static string BuildRemediationHintFromEnvironmentErrors(IReadOnlyList<EnvironmentErrorSummary> errors)
    {
        if (errors.Count == 0)
        {
            return "The run ended with an environment error. Check that Docker is running and any " +
                   "required container images/registries are reachable, then retry.";
        }

        var first = errors[0];
        var basis = first.ErrorKind switch
        {
            "ImagePull" => "could not pull a required container image",
            "HealthGate" => "a required container did not become healthy in time",
            "Discovery" => "a required endpoint could not be resolved",
            _ => "could not provision a required resource",
        };
        var detailSuffix = string.IsNullOrWhiteSpace(first.Detail) ? string.Empty : $" ({first.Detail})";

        return $"Environment error on '{first.ResourceName}': {basis}{detailSuffix}. Check that Docker " +
               "is running and the required images/registries are reachable, then retry.";
    }

    private static string DescribeGateFailure(CliPinResult result) => result switch
    {
        CliPinResult.NotFound notFound => notFound.Message,
        CliPinResult.VersionMismatch mismatch => mismatch.Message,
        CliPinResult.Unparseable unparseable => unparseable.Message,
        _ => "The vouchfx CLI could not be verified.",
    };
}
