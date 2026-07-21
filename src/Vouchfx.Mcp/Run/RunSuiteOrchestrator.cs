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
/// action is <see cref="SuiteValidator.CheckFastRejects"/> (missing file, UNC path), so a single call
/// here covers both the "fast reject" and "unparseable/schema-invalid" cases the spec lists
/// separately — an invalid suite never reaches the CLI handshake or a process spawn.
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
/// <b>Last-run tracking (REQ-007):</b> the injected <see cref="ILastRunTracker"/> is updated with the
/// events-file path and verdict of every ATTEMPTED run (both an ordinary completion and an
/// aborted/cancelled/timed-out one — anything that reaches <see cref="ExecuteRunAsync"/>), never for
/// a call rejected by an earlier gate (nothing was attempted, so there is nothing new to record).
/// <c>explain_run</c> reads this when its own caller omits <c>eventsPath</c>.
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
    private readonly ILastRunTracker _lastRunTracker;
    private int _runInProgress;

    public RunSuiteOrchestrator(CliPinVerifier cliPinVerifier, ISuiteRunner suiteRunner, ILastRunTracker lastRunTracker)
    {
        ArgumentNullException.ThrowIfNull(cliPinVerifier);
        ArgumentNullException.ThrowIfNull(suiteRunner);
        ArgumentNullException.ThrowIfNull(lastRunTracker);

        _cliPinVerifier = cliPinVerifier;
        _suiteRunner = suiteRunner;
        _lastRunTracker = lastRunTracker;
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

        // EDGE-003: the SAME isolated worker validate_suite uses. Its own first action is
        // SuiteValidator.CheckFastRejects (missing file, UNC path) — no worker spawn for those —
        // so this one call covers both "fast reject" and "unparseable/schema-invalid" without a
        // second, redundant check here.
        var validation = await ValidationWorkerClient.ValidateAsync(path, cancellationToken: cancellationToken);
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

        var eventsFilePath = Path.Combine(Path.GetTempPath(), $"vouchfx-mcp-events-{Guid.NewGuid():N}.jsonl");
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

        var outcome = processResult.Termination == RunTermination.Aborted
            ? BuildAbortedOutcome(processResult, timeoutSeconds, onProgress, eventsFilePath, cancellationToken)
            : await BuildCompletedOutcomeAsync(processResult, eventsFilePath, onProgress);

        // REQ-007: a single choke point recording EVERY attempted run (both an ordinary completion
        // and an aborted/cancelled/timed-out one — both funnel through RunSuiteOutcome.Completed, see
        // this type's remarks) so explain_run can default to it. A call rejected by an earlier gate
        // never reaches here at all — nothing was attempted, so there is nothing new to record.
        _lastRunTracker.RecordRun(outcome.Result.EventsFilePath, outcome.Result.Verdict);

        return outcome;
    }

    private static async Task<RunSuiteOutcome.Completed> BuildCompletedOutcomeAsync(
        SuiteProcessResult processResult, string eventsFilePath, Action<string>? onProgress)
    {
        var (eventsContent, eventsTruncated) = await EventsFileReader.TryReadBoundedAsync(eventsFilePath);
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
    /// Best-effort retention sweep for leftover <c>vouchfx-mcp-events-*.jsonl</c> temp files: this
    /// orchestrator is their sole owner (they are never deleted after a run, since a later
    /// <c>explain_run</c> call is expected to read one by its returned path), so without SOME
    /// cleanup they would accumulate in the OS temp directory indefinitely. Deletes any such file
    /// whose last-write time is older than <see cref="StaleEventsFileRetentionHours"/>. Every failure
    /// (a locked file, a permissions problem, the temp directory itself being briefly unavailable) is
    /// swallowed — this is housekeeping, not a correctness requirement, and must never affect the run
    /// it is called alongside.
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
