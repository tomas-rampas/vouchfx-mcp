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
/// <b>Gated options (US-S3-02, and the cheapest gate of all — it reads two booleans).</b>
/// <c>wait: false</c> and <c>keepEnvironment: true</c> are accepted on the wire and refused here
/// with <c>VFX-E-1504</c>, naming upstream ask U4: <c>sprint-00-overview.md</c> §3's stance (a).
/// They are checked FIRST because a call this server cannot honour at all should not be
/// argument-checked, expanded, or validated on the way to being refused — and because the refusal
/// must not depend on whether the rest of the call happened to be well formed.
/// </description></item>
/// <item><description>
/// <b>Exactly one of <c>path</c>/<c>paths</c> (US-S3-02, <c>VFX-E-1503</c>).</b> Both, or neither,
/// does not identify a suite set — the same rule, and the same shape of rule,
/// <c>validate_suite</c>'s <c>VFX-E-1152</c> states for its own <c>path</c>/<c>yaml</c> pair.
/// </description></item>
/// <item><description>
/// <b><c>timeoutSeconds</c> first among the argument-safety rules</b>, because it is what defines the
/// budget every gate after it runs inside (see the EDGE-002 paragraph below). An out-of-range value
/// is rejected outright (documented bounds — see
/// <see cref="MinTimeoutSeconds"/>/<see cref="MaxTimeoutSeconds"/>) rather than silently clamped, so
/// a caller is never surprised by a budget it did not ask for.
/// </description></item>
/// <item><description>
/// Argument safety: a suite path beginning with <c>-</c> would be misread as a CLI option once
/// spliced into an argument list downstream (checked per entry, on the RAW token, by
/// <see cref="SuitePathExpander"/> — which also expands <c>paths</c>' globs and bounds the result);
/// every <c>tag</c> is validated the same way (no leading <c>-</c>, not null/empty/whitespace,
/// bounded count and length — see <see cref="MaxTagCount"/>/<see cref="MaxTagLength"/>), since a tag
/// is spliced into the CLI's own argument list exactly like the path is, and a malformed MCP payload
/// can legally put a null element inside a JSON string array regardless of this server's own
/// nullable-reference-type annotations; every <c>label</c> is bounded in count, key length and value
/// length and refused if it carries control characters (see <see cref="RunLabelRules"/>, the one
/// definition the storage layer applies too).
/// </description></item>
/// <item><description>
/// EDGE-003 pre-validation via <see cref="ValidationWorkerClient.ValidateAsync"/> — the SAME
/// isolated worker <c>validate_suite</c> uses, never the CLI and never a container. Its own first
/// action is <see cref="SuiteValidator.CheckFastRejects"/> (missing file, UNC path, and — when the
/// host launched this server with <c>--workspace</c> — US-S3-08 containment against that root), so a
/// single call here covers the "fast reject" and "unparseable/schema-invalid" cases the spec lists
/// separately — an invalid or escaping suite path never reaches the CLI handshake or a process spawn.
/// <b>Run per suite, for EVERY suite, and ALL-OR-NOTHING</b> (US-S3-02): one invalid suite in a
/// multi-suite call refuses the whole call and runs nothing. The alternative — skip the bad ones and
/// run the rest — would make a glob's meaning depend on which files happen to be authored correctly
/// today, and would report a run-wide verdict about a set the caller never chose. It also keeps the
/// single-path behaviour exactly as it was: a call whose suite does not validate spawns nothing.
/// </description></item>
/// <item><description>
/// Single-flight concurrency (spec §4.6): at most one run may be in progress PER WORKSPACE at a
/// time. Two layers, both non-blocking — an in-process
/// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> claim, and, when a workspace is
/// configured, US-S3-04's cross-process <see cref="IRunLock"/> on
/// <c>&lt;outputDir&gt;/.lock</c>. A second concurrent call is rejected immediately
/// (<c>VFX-E-1501 RunInProgress</c>, <c>retryable: true</c>, carrying the active run's id), never
/// queued. This gate's release (the <c>finally</c> in <see cref="RunAsync"/>) depends entirely on
/// the injected <see cref="ISuiteRunner"/> always returning — see
/// <see cref="VouchfxCliSuiteRunner"/>'s remarks on the BLOCKER (a relay drain that could hang
/// forever on a surviving child process) an earlier version of this had, which would have wedged
/// this very gate permanently.
/// </description></item>
/// <item><description>
/// REQ-008's CLI presence + version handshake (<see cref="CliPinVerifier"/>).
/// </description></item>
/// <item><description>The actual run, via the injected <see cref="ISuiteRunner"/>.</description></item>
/// </list>
/// <para>
/// <b>Why concurrency sits BEFORE the CLI handshake</b> (US-S3-04 moved it; it was between the
/// handshake and the run). <b>The original rationale for this was overstated on two counts, and is
/// restated here honestly</b> — the ORDER is kept, because it is adjudicated in the spec and pinned
/// by <c>RunAsync_WhenTheRunLockIsHeldElsewhere_IsRejectedBeforeTheCliHandshake</c>, but not for the
/// reasons first written down.
/// <list type="bullet">
/// <item><description>
/// <b>The spawn avoided is smaller than claimed, and this gate is not the first spawn anyway.</b>
/// <see cref="CliPinVerifier"/> caches only SUCCESS, so the <c>vouchfx --version</c> spawn this order
/// skips materialises exactly when the CLI is broken — i.e. on every poll of a server that cannot
/// run anything — and not at all on a healthy one, where the handshake after the first call is a
/// cached read. And the EDGE-003 pre-flight above already spawns a validation WORKER, a heavier
/// process than <c>--version</c>, before either gate is reached. So "a rejected call never pays for
/// a process spawn" was never true of this method; what this order buys is narrower: on a broken
/// server under contention, one spawn per poll instead of two.
/// </description></item>
/// <item><description>
/// <b>The trade is reachable, not "close to unreachable".</b> The original text argued that a server
/// with a missing CLI and a run in flight cannot really happen, since a run being in flight means the
/// handshake already passed. That holds only WITHIN one process. The lock is cross-process by
/// design: process A holds it having passed its own handshake, while process B — a different install,
/// a different <c>PATH</c>, an engine uninstalled since A started — genuinely lacks the CLI. B is
/// then told <c>VFX-E-1501 RunInProgress</c> when <c>VFX-E-1401 EngineCliUnavailable</c> is the more
/// useful answer. That is ACCEPTED rather than argued away: <c>VFX-E-1501</c> is <c>retryable: true</c>
/// and the retry surfaces <c>1401</c> truthfully the moment the lock clears, so the misleading answer
/// is transient and self-correcting, while the reverse order would make every contended poll on a
/// healthy server pay a spawn it cannot use.
/// </description></item>
/// </list>
/// The EDGE-003 pre-flight deliberately stays AHEAD of the claim: validation is the one gate whose
/// cost scales with caller input, and holding a workspace-wide lock across it would let one caller's
/// large suite block another caller's run for the length of a parse.
/// </para>
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
/// <b>That budget covers the WHOLE call, and since Sprint 3's review it genuinely does.</b> The
/// linked source is created at the top of <see cref="RunAsync(RunSuiteRequest, Action{string},
/// CancellationToken)"/>, before glob expansion, so every gate below spends from it: expansion (up to
/// <see cref="SuitePathExpander.MaxRequestedPaths"/> workspace walks), the per-suite pre-flight (up
/// to <see cref="SuitePathExpander.MaxExpandedPaths"/> sequential worker spawns of ten seconds each),
/// the CLI handshake (a 15-second version probe against a wedged CLI), and the run. It used to be
/// created immediately before the run instead, which meant a caller's declared five-second budget
/// could sit behind a quarter of an hour of pre-flight it had not agreed to — the documented claim
/// "covers the whole call, not each suite" was simply not true of the code. When it fires before a
/// run has been registered, the answer is <see cref="BuildAbortedBeforeStartOutcome"/>'s: the same
/// timed-out shape, with no events file and every resolved suite reported as not run. One thing is
/// still NOT interruptible — a single <see cref="Microsoft.Extensions.FileSystemGlobbing.Matcher"/>
/// walk, which exposes no cancellation; see <see cref="SuitePathExpander"/>, which states that bound
/// rather than implying one it cannot deliver.
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
/// <b>Multi-suite runs (US-S3-02): ONE run id, ONE claim, ONE registry entry, ONE events stream —
/// and N spawns.</b> A call naming several suites (or a glob that expands to several) is a single
/// run in every sense that matters to a host: it takes the workspace claim once, records one
/// registry entry carrying every spec path, and answers with one elevated verdict. The engine is
/// invoked once per suite, sequentially, inside that one claim — this server never runs two suites
/// concurrently, because the whole point of the claim is that one workspace hosts one topology at a
/// time. A suite that FAILS does not stop the ones after it (each suite gets its own outcome in
/// <see cref="RunSuiteResult.Specs"/>); a suite that is CANCELLED or TIMES OUT does, because the
/// budget it exhausted is the whole call's, and the suites after it are reported with a
/// <see langword="null"/> outcome — "not run" — rather than a verdict nobody reached.
/// </para>
/// <para>
/// <b>The events layout for a multi-suite run, and the trade it makes.</b> Each suite is run into
/// its OWN part file beside the run's events file (<c>&lt;events&gt;.part-001.jsonl</c>, …), parsed
/// there for that suite's own verdict and steps, then APPENDED to the run's single events stream and
/// deleted. The stream at <see cref="IRunRegistry.StartRun"/>'s minted path therefore stays exactly
/// what it has always been: one JSON Lines file per RUN, holding every event the run produced.
/// <list type="bullet">
/// <item><description>
/// <b>Why not one file per suite.</b> The registry mints exactly one events path per run and
/// <see cref="FileRunRegistry"/> rejects on read any entry whose <c>eventsFilePath</c> is not that
/// path (its minted-path trust anchor). Per-suite files would mean either widening the entry to an
/// array — a format change, a weakened anchor, and a new shape for every reader — or pointing the
/// entry at one arbitrary suite's file, which would make <c>explain_run</c>'s default silently
/// describe a fraction of the run. Concatenation keeps <c>explain_run</c> and <c>diagnose_run</c>
/// working unchanged and makes their answer cover the whole run, which is what a caller who asked
/// for one run expects.
/// </description></item>
/// <item><description>
/// <b>What it costs.</b> A multi-suite run copies each suite's stream once (bounded by
/// <see cref="EventsFileReader.MaxEventsFileBytes"/> — past that the remaining parts are parsed for
/// their verdicts and then DISCARDED rather than appended, and the result carries
/// <see cref="RunSuiteResult.EventsTruncated"/>, because an unbounded concatenation would let a
/// hundred-suite glob write an arbitrarily large file into the workspace). A reader also cannot tell
/// from the file alone which suite a line came from, beyond what the engine's own events carry —
/// per-suite attribution lives in <see cref="RunSuiteResult.Specs"/>, which is computed from the
/// parts BEFORE they are merged.
/// </description></item>
/// <item><description>
/// <b>A single-suite run is not copied at all.</b> With one suite the part path IS the run's events
/// path, so the engine writes straight into it and nothing is appended, deleted, or moved — the
/// exact bytes, the exact file, and the exact code path as before US-S3-02.
/// </description></item>
/// </list>
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

    /// <summary>The largest number of <c>labels</c> entries a single <c>run_suite</c> call accepts (US-S3-02).</summary>
    /// <remarks>
    /// <para>
    /// Labels are free-form host metadata persisted verbatim into the run registry, so this bound is
    /// the first thing standing between a caller and an arbitrarily large document on the operator's
    /// disk — <see cref="FileRunRegistry.MaxEntryFileBytes"/> would otherwise start SKIPPING the
    /// entry as oversized, which would lose the run's record entirely rather than refuse the call.
    /// Twenty keys of 64 characters with 256-character values is ~6 KB of ASCII, comfortably inside
    /// that 64 KB cap alongside a hundred ASCII spec paths.
    /// </para>
    /// <para>
    /// <b>"~6 KB" is the ASCII figure and does not bound the byte size on its own</b> (a
    /// gatekeeper/security review's finding). <c>JavaScriptEncoder.Default</c> escapes non-ASCII to
    /// six bytes per UTF-16 unit, so the same twenty labels of non-ASCII text serialise to ~38 KB.
    /// That is still inside the cap by itself, but not alongside a large spec-path set — which is why
    /// the cap is now enforced on the SERIALISED BYTES in <c>FileRunRegistry.Persist</c> rather than
    /// inferred from character counts here. These bounds remain the cheap, caller-facing first line;
    /// the byte check is the guarantee.
    /// </para>
    /// <para>
    /// The three constants are aliases of <see cref="RunLabelRules"/>'s, which is the single
    /// definition the STORAGE layer also validates against (see
    /// <see cref="RunRegistryCore.CreateStartedEntry"/>) — kept here under their established names so
    /// the documented tool bounds and the test suite keep one stable place to read them from.
    /// </para>
    /// </remarks>
    public const int MaxLabelCount = RunLabelRules.MaxCount;

    /// <summary>The largest length, in characters, a single label KEY may have.</summary>
    public const int MaxLabelKeyLength = RunLabelRules.MaxKeyLength;

    /// <summary>The largest length, in characters, a single label VALUE may have.</summary>
    public const int MaxLabelValueLength = RunLabelRules.MaxValueLength;

    /// <summary>
    /// How old (by last-write time) a leftover <c>vouchfx-mcp-events-*.jsonl</c> temp file must be
    /// before <see cref="SweepStaleEventsFilesBestEffort"/> deletes it.
    /// </summary>
    private const int StaleEventsFileRetentionHours = 24;

    // Mirrors Vouchfx.Cli.ExitCodes in the engine repo exactly (see that type's own remarks) — this
    // server never references the engine assembly, so the four values are duplicated here, by
    // reference to their source, rather than shared.
    /// <summary>
    /// The value <c>labels</c> takes when the caller omits it — one shared instance rather than a
    /// fresh allocation per call, since it is never mutated and the registry copies whatever it is
    /// handed (see <c>RunRegistryCore.CreateStartedEntry</c>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyLabels =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private const int ExitCodePass = 0;
    private const int ExitCodeFail = 1;
    private const int ExitCodeEnvironmentError = 3;
    private const int ExitCodeInconclusive = 4;

    private readonly CliPinVerifier _cliPinVerifier;
    private readonly ISuiteRunner _suiteRunner;
    private readonly IRunRegistry _runRegistry;
    private readonly Workspace? _workspace;
    private readonly IRunLock? _runLock;
    private int _runInProgress;

    /// <summary>
    /// The run id THIS process last minted, live for exactly as long as the claim that produced it —
    /// set the moment <see cref="IRunRegistry.StartRun"/> returns and cleared in
    /// <see cref="RunAsync"/>'s <c>finally</c> beside the lock release. Read by
    /// <see cref="TryFindActiveRunId"/> so a same-process rejection names the active run exactly,
    /// without a registry scan and without exposure to any of the staleness windows that scan has.
    /// Written and read through <see cref="Volatile"/> because the rejecting call runs on a different
    /// thread from the holder.
    /// </summary>
    private string? _activeRunId;

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
    /// <param name="runLock">
    /// US-S3-04's cross-process claim on <c>&lt;outputDir&gt;/.lock</c>, or <see langword="null"/>
    /// when no workspace is configured. <b><see langword="null"/> is the full-fidelity legacy mode,
    /// not a degraded one</b>: with no <c>--workspace</c> there is no output directory to put a lock
    /// file in, and inventing one would create files on a host that never asked for any — the exact
    /// failure US-S3-08's compatibility rule exists to prevent. The in-process claim below then
    /// remains the only guard, which is byte for byte what this server did before this story.
    /// </param>
    public RunSuiteOrchestrator(
        CliPinVerifier cliPinVerifier,
        ISuiteRunner suiteRunner,
        IRunRegistry runRegistry,
        Workspace? workspace = null,
        IRunLock? runLock = null)
    {
        ArgumentNullException.ThrowIfNull(cliPinVerifier);
        ArgumentNullException.ThrowIfNull(suiteRunner);
        ArgumentNullException.ThrowIfNull(runRegistry);

        _cliPinVerifier = cliPinVerifier;
        _suiteRunner = suiteRunner;
        _runRegistry = runRegistry;
        _workspace = workspace;
        _runLock = runLock;
    }

    /// <summary>
    /// The pre-US-S3-02 single-suite entry point, kept verbatim: <c>run_suite</c>'s original
    /// <c>path</c>/<c>tags</c>/<c>timeoutSeconds</c> arguments, forwarded to
    /// <see cref="RunAsync(RunSuiteRequest, Action{string}, CancellationToken)"/> unchanged.
    /// </summary>
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
    /// <remarks>
    /// An overload rather than a rewrite of every call site, and it earns its keep twice: it is what
    /// makes "a single-path call behaves exactly as it did" a property of the CODE rather than of a
    /// reviewer's reading, and it is the shape the whole pre-US-S3-02 orchestrator test suite already
    /// drives — those tests keep passing, unmodified, which is the regression evidence that matters.
    /// </remarks>
    public Task<RunSuiteOutcome> RunAsync(
        string path,
        IReadOnlyList<string>? tags,
        int? timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        return RunAsync(
            new RunSuiteRequest { Path = path, Tags = tags, TimeoutSeconds = timeoutSeconds },
            onProgress,
            cancellationToken);
    }

    /// <summary>Runs the full gate sequence described in this type's remarks, then the suite(s) themselves.</summary>
    /// <param name="request">
    /// The call's arguments exactly as the caller sent them — nothing here is pre-validated, because
    /// every rule that could reject them is one of this method's own gates and belongs in the
    /// documented order they run in.
    /// </param>
    /// <param name="onProgress"><inheritdoc cref="RunAsync(string, IReadOnlyList{string}, int?, Action{string}, CancellationToken)" path="/param[@name='onProgress']"/></param>
    /// <param name="cancellationToken">The caller's own cancellation, layered under the <c>timeoutSeconds</c> budget.</param>
    public async Task<RunSuiteOutcome> RunAsync(
        RunSuiteRequest request,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gate 1 (US-S3-02): the two gated options, checked before anything else — see this type's
        // remarks for why the cheapest gate is also the first, and VFX-E-1504's catalogue entry for
        // why each is refused rather than silently re-interpreted. `wait: true` and
        // `keepEnvironment: false` are the implemented behaviours and pass straight through, as does
        // omitting either.
        if (request.Wait == false)
        {
            return new RunSuiteOutcome.OptionUnavailable(
                "run_suite cannot start a run without waiting for it yet: asynchronous execution "
                + "(wait: false) awaits upstream ask U4 (stable engine run ids and a detached run "
                + "with its own status/cancel surface). Call with wait: true — the default — which "
                + "blocks until the run completes and returns its verdict.");
        }

        if (request.KeepEnvironment == true)
        {
            return new RunSuiteOutcome.OptionUnavailable(
                "run_suite cannot leave the environment up after a run yet: the pinned vouchfx CLI "
                + "exposes no such flag, so there is nothing for this server to pass through, and it "
                + "will not implement a teardown policy of its own (that is the engine's, per spec "
                + "§5.7). keepEnvironment awaits upstream ask U4. Call with keepEnvironment: false — "
                + "the default — and use explain_run or diagnose_run on the run's event stream for "
                + "post-mortem detail.");
        }

        // Gate 2 (US-S3-02): exactly one of `path`/`paths`. Settled before any argument is checked
        // individually, because a call that names no suite set (or two) has nothing for the rest of
        // the arguments to apply to — the same ordering, and the same reasoning,
        // Tools/ValidateSuiteInput records for validate_suite's path/yaml pair.
        if (request.Path is not null && request.Paths is not null)
        {
            return new RunSuiteOutcome.AmbiguousInput(
                "run_suite was given both 'path' and 'paths'. Supply exactly one: 'path' for a single "
                + "suite file, or 'paths' for one or more files and/or workspace-relative globs.");
        }

        if (request.Path is null && request.Paths is null)
        {
            return new RunSuiteOutcome.AmbiguousInput(
                "run_suite was given neither 'path' nor 'paths'. Supply exactly one: 'path' for a "
                + "single suite file, or 'paths' for one or more files and/or workspace-relative globs.");
        }

        // Gate 3a (US-S3-02 review fix): timeoutSeconds, checked BEFORE the suite set is resolved
        // rather than after, because it is what defines the budget every gate below now runs inside.
        // It is also the cheapest argument check there is — one integer comparison — so running it
        // first costs nothing and is consistent with this type's "cheapest gate first" ordering.
        //
        // The visible consequence, stated rather than discovered: a call carrying BOTH an
        // out-of-range timeoutSeconds and a bad path is now answered about the timeout. Both were
        // always VFX-E-1006 InvalidToolArgument, so the code a host branches on is unchanged; only
        // which of two simultaneous mistakes is named first has moved.
        var effectiveTimeoutSeconds = request.TimeoutSeconds ?? DefaultTimeoutSeconds;
        if (effectiveTimeoutSeconds < MinTimeoutSeconds || effectiveTimeoutSeconds > MaxTimeoutSeconds)
        {
            return new RunSuiteOutcome.InvalidArgument(
                $"timeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}; got " +
                $"{effectiveTimeoutSeconds}.");
        }

        // THE WHOLE CALL'S BUDGET, and it starts HERE (a gatekeeper/security review's MAJOR finding).
        // This source used to be created inside ExecuteRegisteredRunAsync — i.e. AFTER up to
        // MaxRequestedPaths glob walks over the workspace and up to MaxExpandedPaths sequential
        // validation-worker spawns, each with its own ten-second wall clock. `timeoutSeconds` is
        // documented, in this server's own tool description, as capping the WHOLE call; with the
        // source created that late, a call declaring a 5-second budget could legitimately spend
        // sixteen minutes in pre-flight before the budget it asked for even began to run. One linked
        // source, created before the first gate that touches the filesystem, is what makes the
        // documented claim true.
        //
        // Lifetime: disposed by this method's `using`, which cannot run until everything awaited below
        // — including the run itself — has completed, so no consumer can ever observe a disposed
        // source. Deliberately NOT disposed earlier (e.g. after the pre-flight): the run consumes this
        // same token, which is the entire point of hoisting it.
        using var callBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callBudget.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));
        var budgetToken = callBudget.Token;

        // Gate 3b: the suite set. SuitePathExpander owns the per-entry leading-'-' refusal, the
        // workspace rebase, glob expansion and both caps — see its remarks. The leading-'-' rule
        // guards TWO command lines, not just the engine CLI's: the obvious one is the `vouchfx run`
        // spawn below, where a leading '-' would be read as an option; the non-obvious one is the
        // validation worker, since the EDGE-003 pre-flight below calls ValidationWorkerClient
        // DIRECTLY, bypassing Tools.ValidateSuiteInput.TryResolve — so the VFX-E-1152 rejection of a
        // path literally named `--yaml-stdin` (ValidationWorkerProtocol.InlineYamlArgument, an
        // in-band discriminator in the same argument position as the path) never runs on this path.
        // That literal begins with a dash, which is what covers it here. See
        // ValidationWorkerProtocol.InlineYamlArgument's remarks: the two guards cover disjoint entry
        // points and neither is redundant.
        //
        // Globs are expanded for `paths` only — never for the legacy scalar `path`, whose meaning
        // must not change under a caller who did not opt into the new input. See SuitePathExpander.
        SuitePathExpansion expansion;
        try
        {
            expansion = SuitePathExpander.Expand(
                request.Paths ?? [request.Path!], _workspace, allowGlobs: request.Paths is not null, budgetToken);
        }
        catch (OperationCanceledException)
        {
            // The budget (or the caller) ended the call while the workspace was still being walked.
            // No suite set was resolved, so there is nothing to attribute an outcome to — see
            // BuildAbortedBeforeStartOutcome for why that is reported as a timed-out RESULT rather
            // than an escaping exception or an error code.
            return BuildAbortedBeforeStartOutcome(
                suitePaths: [], effectiveTimeoutSeconds, onProgress, cancellationToken);
        }

        if (expansion is SuitePathExpansion.Invalid invalidPaths)
        {
            return new RunSuiteOutcome.InvalidArgument(invalidPaths.Message);
        }

        if (expansion is SuitePathExpansion.NoMatches noMatches)
        {
            return new RunSuiteOutcome.NoSuitesMatched(noMatches.Message);
        }

        if (expansion is not SuitePathExpansion.Expanded expanded)
        {
            // Unreachable by construction (the union is closed), and refused rather than assumed:
            // the safe default for an unrecognised answer to "which suites should I run?" is none.
            return new RunSuiteOutcome.InvalidArgument(
                "The supplied suite paths could not be resolved into a run.");
        }

        var suitePaths = expanded.Paths;
        var effectiveTags = request.Tags ?? [];
        var effectiveLabels = request.Labels ?? EmptyLabels;

        // Gate 3c: the remaining argument-safety rules, in the order they have always run.
        var tagValidationError = ValidateTags(effectiveTags);
        if (tagValidationError is not null)
        {
            return new RunSuiteOutcome.InvalidArgument(tagValidationError);
        }

        var labelValidationError = ValidateLabels(effectiveLabels);
        if (labelValidationError is not null)
        {
            return new RunSuiteOutcome.InvalidArgument(labelValidationError);
        }

        // EDGE-003: the SAME isolated worker validate_suite uses. Its own first action is
        // SuiteValidator.CheckFastRejects (missing file, UNC path, and — with a workspace
        // configured — US-S3-08 containment) — no worker spawn for those — so this one call covers
        // both "fast reject" and "unparseable/schema-invalid" without a second, redundant check
        // here. Passing _workspace is also what keeps run_suite's gate identical to validate_suite's
        // rather than a laxer copy of it: an escaping path is refused before the engine CLI is ever
        // handed it — including one that arrived through a glob, which is expanded but never exempt.
        //
        // Every suite is validated BEFORE any is run (this type's remarks: all-or-nothing), and all
        // of it happens before the claim below, because validation is the one gate whose cost scales
        // with caller input and holding a workspace-wide lock across it would let one caller's large
        // suite set block another caller's run for the length of N parses.
        foreach (var suitePath in suitePaths)
        {
            ValidateSuiteResult validation;
            try
            {
                // The BUDGET token, not the caller's own: this loop is up to MaxExpandedPaths
                // sequential worker spawns of ten seconds each, which is exactly where an unbudgeted
                // pre-flight used to be able to run for a quarter of an hour under a five-second
                // declared timeout.
                validation = await ValidationWorkerClient.ValidateAsync(
                    suitePath, _workspace, cancellationToken: budgetToken);
            }
            catch (OperationCanceledException)
            {
                // The same normalisation RunOneSuiteAsync applies one layer down, for the same
                // reason: ValidationWorkerClient documents that a cancelled TOKEN (as opposed to its
                // own internal wall clock, which comes back as a structured validation-timeout
                // result) rethrows as an ordinary OperationCanceledException. Unhandled, that escaped
                // the tool handler uncoded the moment the hoisted budget above made it reachable.
                return BuildAbortedBeforeStartOutcome(
                    suitePaths, effectiveTimeoutSeconds, onProgress, cancellationToken);
            }

            if (!validation.Valid)
            {
                // The PATH travels with the result (a gatekeeper review's MAJOR finding): the
                // pre-flight is all-or-nothing across every suite, and ValidateSuiteResult names no
                // file, so without this a forty-suite glob's caller is told "a suite is invalid" and
                // cannot tell which one.
                return new RunSuiteOutcome.SuiteInvalid(validation, suitePath);
            }
        }

        // Single-flight, layer 1 of 2: this process. Free (one interlocked word, no I/O), and kept
        // even though the file lock below would also exclude a same-process second call — it is what
        // makes the no-workspace mode behave exactly as it always has, and it means the overwhelmingly
        // common rejection (one host, one server, two overlapping calls) costs no filesystem access
        // at all.
        if (Interlocked.CompareExchange(ref _runInProgress, 1, 0) != 0)
        {
            return BuildAlreadyRunningOutcome();
        }

        // Held for the whole run and released in the finally below — on completion, on cancellation,
        // and on any exception. A hard kill releases it too, but that is the OPERATING SYSTEM's doing
        // rather than this code's: see WorkspaceRunLock's remarks for why the handle, not the file, is
        // the lock.
        RunLockResult.Acquired? claim = null;
        try
        {
            if (_runLock is not null)
            {
                var acquisition = _runLock.TryAcquire();
                if (acquisition is RunLockResult.Acquired acquired)
                {
                    claim = acquired;
                }
                else if (acquisition is RunLockResult.Unavailable unavailable)
                {
                    // NOT a concurrency answer: the output directory itself refused the operation, so
                    // the very next thing this call would do (IRunRegistry.StartRun, into that same
                    // directory) is certain to fail the same way. Reported with the code that already
                    // means exactly that — VFX-E-1502, "the run could not be recorded before it
                    // started" — rather than as a RunInProgress the host would poll forever.
                    return new RunSuiteOutcome.RunNotRecorded(BuildRunNotRecordedMessage(unavailable.Failure));
                }
                else
                {
                    // HeldByAnotherRun — and, deliberately, any case a future IRunLock adds that this
                    // method has not been taught: the safe default for an unrecognised answer to "may
                    // I start a run?" is no.
                    return BuildAlreadyRunningOutcome();
                }
            }

            CliPinResult pinResult;
            try
            {
                // Also under the budget: a version probe against a wedged CLI has its own 15-second
                // ceiling, which a caller declaring a shorter whole-call budget has not agreed to.
                pinResult = await _cliPinVerifier.VerifyAsync(budgetToken);
            }
            catch (OperationCanceledException)
            {
                return BuildAbortedBeforeStartOutcome(
                    suitePaths, effectiveTimeoutSeconds, onProgress, cancellationToken);
            }

            if (pinResult is not CliPinResult.Ok)
            {
                return new RunSuiteOutcome.CliUnavailable(DescribeGateFailure(pinResult));
            }

            return await ExecuteRunAsync(
                suitePaths, effectiveTags, effectiveLabels, effectiveTimeoutSeconds, onProgress,
                budgetToken, cancellationToken);
        }
        finally
        {
            // Ordering matters: the cross-process claim is dropped BEFORE the in-process one. Both
            // orders are correct for a single server, but this one keeps the invariant "the file lock
            // is never held without the in-process flag also being held" true at every instant, so a
            // concurrent caller in this process can never observe a window in which the flag is free
            // while the file lock is not.
            claim?.Release.Dispose();

            // Cleared with the claim, never after it: TryFindActiveRunId prefers this field over a
            // registry scan, so a value outliving the run it names would make the NEXT rejection
            // report a finished run as active — the exact failure the field exists to prevent.
            Volatile.Write(ref _activeRunId, null);
            Volatile.Write(ref _runInProgress, 0);
        }
    }

    /// <summary>
    /// EDGE-002 for the window BEFORE a run is registered: the call's budget (or the caller's own
    /// cancellation) ended it during path expansion, the pre-flight, or the CLI handshake, so nothing
    /// was spawned and no run id was ever minted.
    /// </summary>
    /// <param name="suitePaths">
    /// The suites the call had resolved by then — empty when expansion itself was cut short.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why this is a RESULT and not an error code.</b> The taxonomy invariant is that a timeout is
    /// <c>Inconclusive</c>, never <c>Fail</c> and never an infrastructure error — and that is exactly
    /// what happened: the caller asked for a verdict within <c>timeoutSeconds</c> and this server
    /// could not produce one in that time. The shape is therefore the SAME cancelled/timed-out shape
    /// <see cref="BuildAbortedResult"/> produces when the budget expires mid-run, which is the shape
    /// a host already handles: <c>Inconclusive</c>, with <c>cancelled</c>/<c>timedOut</c>
    /// distinguishing which clock ran out and every suite reported with a <see langword="null"/>
    /// outcome, i.e. "not run".
    /// </para>
    /// <para>
    /// <b>What is different, and is stated on the fields rather than hidden.</b> No run was
    /// registered, so there is no events file and
    /// <see cref="RunSuiteResult.EventsFilePath"/> is EMPTY — the one case in which it is (see that
    /// field). Inventing a path would hand a host a file name that will never exist and that
    /// <c>explain_run</c> would then refuse; naming a run id would be worse still, since none was
    /// minted. The alternative shapes were considered and rejected: a tool error would report a
    /// timeout as an infrastructure failure the taxonomy says it is not, and a new
    /// <c>VFX-E-</c> code would mint a contract for a condition the existing <c>timedOut</c> flag
    /// already expresses exactly.
    /// </para>
    /// </remarks>
    private static RunSuiteOutcome.Completed BuildAbortedBeforeStartOutcome(
        IReadOnlyList<string> suitePaths,
        int timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        // The same discrimination BuildAbortedResult makes, for the same reason: only the ORIGINAL,
        // unlinked token can say whether the caller cancelled or the budget expired.
        var wasCallerCancelled = cancellationToken.IsCancellationRequested;

        onProgress?.Invoke(wasCallerCancelled
            ? "Run cancelled before any suite started."
            : $"Run timed out after {timeoutSeconds}s, before any suite started.");

        return new RunSuiteOutcome.Completed(new RunSuiteResult(
            Verdict: RunVerdict.Inconclusive.ToString(),
            ExitCode: null,
            Cancelled: wasCallerCancelled,
            TimedOut: !wasCallerCancelled,
            RemediationHint: wasCallerCancelled
                ? null
                : $"The {timeoutSeconds}s budget expired before any suite started — it was spent expanding "
                  + "the supplied paths and validating them. Nothing was run. Raise timeoutSeconds, or "
                  + "name fewer suites.",
            Steps: [],
            EventsFilePath: string.Empty,

            // Every suite the call had resolved, each "not run" — never Inconclusive, which would
            // claim the engine tried and could not decide (see SpecRunOutcome.Outcome).
            Specs: [.. suitePaths.Select(path => new SpecRunOutcome(path, null, []))],
            EventsTruncated: false));
    }

    /// <summary>
    /// Builds the <c>VFX-E-1501 RunInProgress</c> rejection, naming the active run when the registry
    /// can say which it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The active run id comes from the REGISTRY, not from the lock file</b> — see
    /// <see cref="WorkspaceRunLock"/>'s remarks for why a <see cref="FileShare.None"/> handle cannot
    /// carry a payload the rejected process is able to read, and why a second sibling file that could
    /// disagree with the registry was not worth minting. The registry is already the authority on
    /// "which runs exist and what state are they in" (<see cref="IRunRegistry"/>), so asking it here
    /// is the linkage the story wants rather than a workaround: the entry named in
    /// <c>details.runId</c> is exactly the entry sitting at <c>running</c> because of the lock this
    /// caller just failed to take.
    /// </para>
    /// <para>
    /// <b>When the newest running entry can be a stale one: THREE windows, not one.</b> An earlier
    /// version of this remark claimed a single sub-millisecond window that self-heals; a gatekeeper
    /// review showed that to be wrong on both counts. <see cref="IRunRegistry.ListRuns"/> is
    /// most-recent-first and every implementation makes <see cref="RunRegistryEntry.StartedAtUtc"/>
    /// strictly increasing — the file-backed one seeding its floor from the newest entry already on
    /// disk — so once the holder's <see cref="IRunRegistry.StartRun"/> has landed, its entry is ahead
    /// of any <c>running</c> entry left behind by a process that crashed earlier (FileRunRegistry
    /// documents that such orphans are never reaped). The exceptions are:
    /// <list type="number">
    /// <item><description>
    /// <b>The head window</b>, between the holder ACQUIRING the lock and its <c>StartRun</c> write
    /// landing. Brief in the common case, and it self-heals the instant the write lands — but note
    /// (a peer review's addition) that it CONTAINS the holder's CLI handshake, which sits between
    /// the two since the gate reorder: on the holder's first call, or against a broken CLI (up to
    /// the 15&#160;s version-probe timeout), a contender is told "another run is in progress" while
    /// the holder has not actually started one and has no entry to name. In the three windows below
    /// a run genuinely is (or just was) in flight and only the ID may be wrong; in this sub-case
    /// the claim itself overstates. Accepted with the reorder — the refusal is still correct (the
    /// workspace IS claimed), only its wording assumes a run that is a moment away.
    /// </description></item>
    /// <item><description>
    /// <b>The tail window</b>, between the holder's COMPLETING write and its release of the lock in
    /// <see cref="RunAsync"/>'s <c>finally</c>. Here the newest entry is already <c>completed</c>, so
    /// the scan below walks past it — and lands on an older orphan if one exists, naming a run that
    /// finished long ago as the reason for a refusal caused by a run that has just finished. Also
    /// brief, and also self-healing.
    /// </description></item>
    /// <item><description>
    /// <b>The cap window, which does NOT self-heal.</b> <see cref="FileRunRegistry"/> bounds its walk
    /// at <see cref="FileRunRegistry.MaxRunsScanned"/> run directories, and that cap is applied over
    /// the FILESYSTEM's enumeration order, before the most-recent-first sort. A workspace holding
    /// more than that many runs can therefore have the live holder's entry fall outside the scanned
    /// slice on every call, persistently, while an older <c>running</c> orphan inside it is named
    /// instead. Reaching that many runs already means the workspace needs a retention sweep (the cap
    /// is a denial-of-service bound, documented as such on <see cref="FileRunRegistry"/>), and the
    /// consequence here is a wrong <c>details.runId</c> on a refusal that is itself correct — but it
    /// is a permanent wrongness, not a race, and calling it self-healing was false.
    /// </description></item>
    /// </list>
    /// <see cref="TryFindActiveRunId"/> narrows all three as far as it can without changing the
    /// answer's shape: it takes the FIRST <c>running</c> entry in most-recent order and stops, so a
    /// stale orphan can only be named when the live entry is genuinely absent from what the registry
    /// returned. Blocking the rejection on a registry poll is still rejected as a remedy — it would
    /// turn a documented non-blocking answer into a wait — and no reaper can distinguish an orphan
    /// from a genuinely long run without exactly the liveness signal this lock is.
    /// </para>
    /// </remarks>
    private RunSuiteOutcome.AlreadyRunning BuildAlreadyRunningOutcome()
    {
        var activeRunId = TryFindActiveRunId();
        var scope = _runLock is null ? "on this server" : "against this workspace";

        var message = activeRunId is null
            ? $"Another run is already in progress {scope}; only one run may be active at a time. "
              + "Wait for it to finish before retrying."
            : $"Another run ('{activeRunId}') is already in progress {scope}; only one run may be "
              + "active at a time. Wait for it to finish before retrying.";

        return new RunSuiteOutcome.AlreadyRunning(message, activeRunId);
    }

    /// <summary>
    /// The run id of the run this rejection is about: the one THIS process minted when it is the
    /// holder, and otherwise the newest <see cref="RunRegistryStatus.Running"/> entry the registry
    /// reports — or <see langword="null"/> when neither can be established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same-process case needs no scan at all.</b> When this orchestrator is itself the holder
    /// — the overwhelmingly common rejection, one host and one server with two overlapping calls —
    /// the id was minted by <see cref="IRunRegistry.StartRun"/> a moment ago and is cached in
    /// <see cref="_activeRunId"/> until <see cref="RunAsync"/>'s <c>finally</c> clears it alongside
    /// the lock. Answering from that field is not merely faster than re-deriving it from the
    /// registry: it is EXACT, immune to all three of the staleness windows described above, and it
    /// removes a directory walk from a path whose whole point is to answer immediately. A
    /// cross-process rejection has no such field to consult and still scans.
    /// </para>
    /// <para>
    /// <b>The scan stops at the FIRST <c>running</c> entry and does not look past it.</b> If that
    /// entry's id is not well-formed, this returns <see langword="null"/> rather than continuing to an
    /// older one — deliberately, and it is the fix for a real defect (a gatekeeper review's finding).
    /// Skipping a malformed newest entry to name an older <c>running</c> one turns "I cannot name the
    /// active run" into "here is a DIFFERENT run's id", which a host will correlate, poll, and
    /// eventually act on. Refusing to name it beats naming the wrong one; the refusal itself is
    /// correct either way, and <c>VFX-E-1501</c>'s catalogue page documents the absent-<c>details</c>
    /// case.
    /// </para>
    /// <para>
    /// Shape-checked before it is returned even though every id in the registry was minted by this
    /// server: the file-backed registry reads documents off a directory this process does not
    /// exclusively own, and this value goes into a message and onto the wire. That is one string
    /// comparison against the same predicate the registry itself uses to name a run's directory, and
    /// it means no path exists by which a hand-written <c>run.json</c> can put arbitrary text into a
    /// <c>VfxError</c>. Every failure is swallowed to <see langword="null"/>: a rejection that cannot
    /// name the active run is still a correct rejection, and failing the call over unreadable
    /// bookkeeping would be strictly worse than the answer without the id.
    /// </para>
    /// </remarks>
    private string? TryFindActiveRunId()
    {
        if (Volatile.Read(ref _activeRunId) is { } mintedByThisProcess)
        {
            return RunRegistryCore.IsWellFormedRunId(mintedByThisProcess) ? mintedByThisProcess : null;
        }

        try
        {
            foreach (var entry in _runRegistry.ListRuns())
            {
                if (!string.Equals(entry.Status, RunRegistryStatus.Running, StringComparison.Ordinal))
                {
                    continue;
                }

                // First running entry wins, malformed or not — see this method's remarks on why a
                // malformed newest entry ends the search rather than advancing it.
                return RunRegistryCore.IsWellFormedRunId(entry.RunId) ? entry.RunId : null;
            }

            return null;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: this runs only
        // on a path that has ALREADY decided to reject the call. Any failure reading the registry
        // costs the rejection its `details.runId` and nothing else; letting it escape would replace a
        // correct, catalogued VFX-E-1501 with an uncoded exception.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
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
                // Capped, not just sanitised: this echo runs BEFORE the length check below, so it is
                // the one place a multi-megabyte tag could reach a message whole — the same shape a
                // security review closed for path echoes (SuitePathExpander), fixed here alongside.
                return $"Tag must not begin with '-': '{PathSafetyGuard.CapAndSanitisePathForDisplay(tag)}'. A leading " +
                       "'-' would be interpreted as a command-line option, not a tag value.";
            }

            if (tag.Length > MaxTagLength)
            {
                return $"Tag exceeds the {MaxTagLength}-character limit ({tag.Length} characters).";
            }
        }

        return null;
    }

    /// <summary>
    /// Rejects a <c>labels</c> map (US-S3-02) that exceeds <see cref="MaxLabelCount"/>, or whose key
    /// or value is null/blank, over-long, or carries a control character.
    /// </summary>
    /// <remarks>
    /// <b>The rules themselves live in <see cref="RunLabelRules"/></b>, which the STORAGE layer
    /// applies too (<see cref="RunRegistryCore.CreateStartedEntry"/>) — see that type for each rule's
    /// reasoning and for why there are deliberately two enforcers of one definition. This method is
    /// the tool-boundary half: it turns a violation into a message, which becomes a catalogued
    /// <c>VFX-E-1006</c> the caller can act on, rather than the exception the storage layer throws
    /// for the same map arriving where it should already have been refused.
    /// </remarks>
    private static string? ValidateLabels(IReadOnlyDictionary<string, string> labels) =>
        RunLabelRules.Validate(labels);

    /// <param name="budgetToken">
    /// The whole call's budget — the caller's own token linked with <c>timeoutSeconds</c>, created at
    /// the top of <see cref="RunAsync(RunSuiteRequest, Action{string}, CancellationToken)"/> so that
    /// expansion and the pre-flight spend from it too. This is what the run itself is cancelled by.
    /// </param>
    /// <param name="callerToken">
    /// The ORIGINAL, unlinked token, carried alongside for exactly one purpose: telling "the caller
    /// cancelled" from "the budget expired" after the fact. Nothing is cancelled by it directly.
    /// </param>
    private async Task<RunSuiteOutcome> ExecuteRunAsync(
        IReadOnlyList<string> suitePaths,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, string> labels,
        int timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken budgetToken,
        CancellationToken callerToken)
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
            // ALL the run's suites and the caller's labels, in one entry (US-S3-02): RunRegistryEntry
            // has been array-shaped and label-carrying since US-S3-01 precisely so this call needed no
            // format change when multi-suite runs and `labels` landed.
            registryEntry = _runRegistry.StartRun(suitePaths, labels);
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

        // The one point at which this process knows the active run's id first-hand. Published for
        // TryFindActiveRunId (see its remarks) and cleared by RunAsync's finally, so it is live for
        // exactly the interval during which a concurrent caller could be rejected because of it.
        Volatile.Write(ref _activeRunId, registryEntry.RunId);

        try
        {
            var outcome = await ExecuteRegisteredRunAsync(
                registryEntry.EventsFilePath, suitePaths, tags, timeoutSeconds, onProgress,
                budgetToken, callerToken);

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
            //
            // That deliberately includes ArgumentException, which ApplyStatusTransition throws for an
            // illegal transition and which IS a programming error rather than a disk fault (a peer
            // review's NIT: should it propagate?). It should not, and the asymmetry is the point.
            // Propagating it would destroy a verdict the engine already produced in order to report a
            // bug in this server's own transition rules — which is precisely the MAJOR finding this
            // arm was added to fix, arrived at from the other direction; the caller is an MCP host
            // that can do nothing with either the exception or the stack trace, and would lose the
            // one thing it asked for. Nor is the bug hidden: ReportCompletionNotRecorded names the
            // exception's TYPE on stderr, so `ArgumentException` shows up verbatim and is greppable,
            // and the transition rules themselves are pinned at the unit seam by RunRegistryTests,
            // where such a bug fails a test rather than someone's run.
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
    /// write was aimed at and the OS failure's TYPE, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>Two producers since US-S3-04, both in the same directory.</b> The original is
    /// <see cref="IRunRegistry.StartRun"/> refusing to record the run; the second is
    /// <see cref="IRunLock.TryAcquire"/> reporting <see cref="RunLockResult.Unavailable"/> — which is
    /// the same condition observed one step earlier, since <c>&lt;outputDir&gt;/.lock</c> and
    /// <c>&lt;outputDir&gt;/&lt;runId&gt;/run.json</c> live under the one directory. The wording is
    /// deliberately about the DIRECTORY rather than about which file was being written, so it stays
    /// true for both without either producer needing its own message or its own code.
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
    /// <remarks>
    /// <para>
    /// <b>One suite or many, one result.</b> Each suite is spawned in turn under the one claim
    /// <see cref="RunAsync(RunSuiteRequest, Action{string}, CancellationToken)"/> already holds, and
    /// under ONE timeout budget covering the whole call — <c>timeoutSeconds</c> has always meant "cap
    /// this tool call", and re-reading it as a per-suite budget would silently multiply a caller's
    /// declared bound by however many files their glob matched.
    /// </para>
    /// <para>
    /// <b>A Fail continues; an abort stops.</b> A suite that fails is a RESULT — the next suite still
    /// runs, and the failure shows up in that suite's own <see cref="SpecRunOutcome"/> and in the
    /// elevated verdict. A cancellation or timeout is not a result but the END of the call's budget,
    /// so the loop stops there: the aborted suite is reported <c>Inconclusive</c> (EDGE-002: never
    /// <c>Fail</c>) and every suite after it is reported with a <see langword="null"/> outcome.
    /// </para>
    /// <para>
    /// <b>The verdict is elevated across every suite that reached one</b>
    /// (<see cref="RunVerdictExtensions.Elevate"/>, §12.1 precedence), which has a consequence worth
    /// stating rather than discovering: a run where one suite genuinely FAILED and a later one timed
    /// out reports <c>Fail</c> with <c>timedOut: true</c>, because <c>Fail</c> outranks
    /// <c>Inconclusive</c>. That is not the taxonomy violation it can look like — nothing here
    /// reports a timeout AS a failure; it reports that a suite failed AND that the run was cut short,
    /// which are two true things. For a single-suite run the situation cannot arise, and the result
    /// is byte for byte what it was before US-S3-02.
    /// </para>
    /// </remarks>
    /// <param name="budgetToken"><inheritdoc cref="ExecuteRunAsync" path="/param[@name='budgetToken']"/></param>
    /// <param name="callerToken"><inheritdoc cref="ExecuteRunAsync" path="/param[@name='callerToken']"/></param>
    private async Task<RunSuiteOutcome.Completed> ExecuteRegisteredRunAsync(
        string eventsFilePath,
        IReadOnlyList<string> suitePaths,
        IReadOnlyList<string> tags,
        int timeoutSeconds,
        Action<string>? onProgress,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        // The budget is CONSUMED here, not created here (a gatekeeper/security review's MAJOR
        // finding). It was created at this point until Sprint 3's review, which meant the clock a
        // caller set for the whole call did not start until after expansion and the pre-flight had
        // already run — see RunAsync, where the one linked source now lives.

        // With exactly one suite the engine writes STRAIGHT INTO the run's events file and nothing is
        // ever copied, appended, or deleted — see this type's remarks on the events layout. The flag
        // is computed once so every branch below reads the same answer.
        var singleSuite = suitePaths.Count == 1;

        var specs = new List<SpecRunOutcome>(suitePaths.Count);
        var steps = new List<StepOutcome>();
        RunVerdict? aggregate = null;
        string? remediationHint = null;
        var eventsTruncated = false;
        int? lastExitCode = null;

        // WARN ONCE, not once per suite (a gatekeeper review's NIT). The events-cap warning describes
        // a property of the RUN's stream, not of the suite that happened to hit it, so a fifty-suite
        // glob past the cap emitted forty-eight identical progress notifications after the first.
        var eventsCapWarningIssued = false;

        for (var index = 0; index < suitePaths.Count; index++)
        {
            var suitePath = suitePaths[index];
            var partPath = singleSuite ? eventsFilePath : PartEventsFilePath(eventsFilePath, index);

            onProgress?.Invoke(singleSuite
                ? "Starting vouchfx CLI..."
                : $"Starting vouchfx CLI for suite {index + 1} of {suitePaths.Count}: "
                  + $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(suitePath)}'...");

            var processResult = await RunOneSuiteAsync(
                new SuiteRunSpec(suitePath, tags, partPath), onProgress, budgetToken);

            // Null means "this suite reached no summary", which happens on exactly one path: it was
            // aborted, either during the run or during the read of what it produced.
            SuiteSummary? summary = null;
            if (processResult.Termination != RunTermination.Aborted)
            {
                try
                {
                    summary = await SummariseSuiteAsync(processResult, partPath, onProgress, budgetToken);
                }
                catch (OperationCanceledException)
                {
                    // A cancellation/timeout DURING the events-file read/parse — NOT during the suite
                    // run itself, which already completed normally by this point. EventsFileReader
                    // lets a genuine cancellation propagate (a review fix) rather than silently
                    // degrading it to "could not be read", so this is handled as the SAME
                    // cancelled/timed-out abort EDGE-002 already uses for a cancellation DURING the
                    // run, rather than letting the exception crash the whole tool call.
                    processResult = processResult with { Termination = RunTermination.Aborted };
                }
            }

            // Merge and clean up the part BEFORE branching on the abort: a suite that was killed
            // mid-run may still have left a part behind, and leaving it on disk would be residue
            // inside the workspace nothing ever reaps.
            if (!singleSuite)
            {
                var merge = await AppendPartToRunStreamAsync(
                    partPath, eventsFilePath, eventsCapWarningIssued ? null : onProgress);

                // Both non-Merged outcomes mean the same thing to a caller, which is exactly what
                // EventsTruncated says: "what you can read of this stream is not all of it". The cap
                // case discarded the part deliberately; the failure case lost it to an I/O fault
                // (a gatekeeper review's finding — that arm used to report success, so a run whose
                // merge failed claimed a complete archived stream).
                eventsTruncated |= merge != PartMergeResult.Merged;
                eventsCapWarningIssued |= merge == PartMergeResult.CapReached;
            }

            if (summary is not { } suiteSummary)
            {
                return BuildAbortedResult(
                    processResult, index, suitePaths, specs, steps, aggregate, singleSuite,
                    timeoutSeconds, eventsFilePath, eventsTruncated, onProgress, callerToken);
            }

            eventsTruncated |= suiteSummary.EventsTruncated;
            specs.Add(new SpecRunOutcome(suitePath, suiteSummary.Verdict.ToString(), suiteSummary.Steps));
            steps.AddRange(suiteSummary.Steps);
            aggregate = aggregate is { } current
                ? RunVerdictExtensions.Elevate(current, suiteSummary.Verdict)
                : suiteSummary.Verdict;

            // FIRST hint wins: a hint names a specific environment failure ("could not pull image X"),
            // and the earliest one is the one that most likely explains the rest.
            remediationHint ??= suiteSummary.RemediationHint;

            // With one suite there is exactly one process, so its code is the run's; with several
            // there is no single code that describes the run (see RunSuiteResult.ExitCode).
            lastExitCode = processResult.ExitCode;

            onProgress?.Invoke(singleSuite
                ? $"Run finished: {suiteSummary.Verdict}."
                : $"Suite {index + 1} of {suitePaths.Count} finished: {suiteSummary.Verdict}.");
        }

        if (!singleSuite)
        {
            onProgress?.Invoke($"Run finished: {aggregate}.");
        }

        return new RunSuiteOutcome.Completed(new RunSuiteResult(
            // aggregate is non-null here by construction: the loop ran at least once (the expander
            // refuses an empty set) and every iteration that reaches the end assigns it.
            Verdict: (aggregate ?? RunVerdict.Inconclusive).ToString(),
            ExitCode: singleSuite ? lastExitCode : null,
            Cancelled: false,
            TimedOut: false,
            RemediationHint: remediationHint,
            Steps: steps,
            EventsFilePath: eventsFilePath,
            Specs: specs,
            EventsTruncated: eventsTruncated));
    }

    /// <summary>
    /// Runs one suite through the injected <see cref="ISuiteRunner"/>, normalising a runner that
    /// throws on its own cancellation into the <see cref="RunTermination.Aborted"/> report the rest
    /// of this type expects.
    /// </summary>
    private async Task<SuiteProcessResult> RunOneSuiteAsync(
        SuiteRunSpec spec, Action<string>? onProgress, CancellationToken linkedToken)
    {
        try
        {
            return await _suiteRunner.RunAsync(spec, line => onProgress?.Invoke(line), linkedToken);
        }
        catch (OperationCanceledException)
        {
            // Defensive: a runner that throws instead of reporting Aborted when ITS OWN passed
            // token fires is still handled as a bounded abort, not an unhandled exception escaping
            // the tool handler. The caller's-token-versus-budget question is answered later, by
            // re-reading the ORIGINAL token — this arm does not need to know which fired.
            return new SuiteProcessResult(null, RunTermination.Aborted);
        }
    }

    /// <summary>One suite's parsed result: everything <see cref="ExecuteRegisteredRunAsync"/> folds into the run.</summary>
    private readonly record struct SuiteSummary(
        RunVerdict Verdict,
        string? RemediationHint,
        IReadOnlyList<StepOutcome> Steps,
        bool EventsTruncated);

    /// <summary>
    /// Reads and parses ONE suite's events stream and classifies its verdict — the body that was
    /// <c>BuildCompletedOutcomeAsync</c> before US-S3-02, unchanged in every rule it applies and
    /// changed only in what it returns (a suite's summary rather than the whole run's result).
    /// </summary>
    private static async Task<SuiteSummary> SummariseSuiteAsync(
        SuiteProcessResult processResult,
        string eventsFilePath,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
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

        return new SuiteSummary(verdict, remediationHint, summary.Steps, eventsTruncated);
    }

    /// <summary>
    /// EDGE-002's cancelled/timed-out result, built at the point the loop stops: the aborted suite is
    /// <c>Inconclusive</c>, every suite after it has no outcome at all, and everything earlier keeps
    /// the verdict it genuinely reached.
    /// </summary>
    private static RunSuiteOutcome.Completed BuildAbortedResult(
        SuiteProcessResult processResult,
        int abortedIndex,
        IReadOnlyList<string> suitePaths,
        List<SpecRunOutcome> specs,
        List<StepOutcome> steps,
        RunVerdict? aggregate,
        bool singleSuite,
        int timeoutSeconds,
        string eventsFilePath,
        bool eventsTruncated,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        // Distinguishes "the CALLER's own token fired" from "the timeout budget layered on top of
        // it fired" purely by re-checking the ORIGINAL, unlinked token now that the run is over —
        // ISuiteRunner itself only ever saw the linked token, and never needed to know which of the
        // two caused it to fire.
        var wasCallerCancelled = cancellationToken.IsCancellationRequested;

        onProgress?.Invoke(wasCallerCancelled ? "Run cancelled." : $"Run timed out after {timeoutSeconds}s.");

        specs.Add(new SpecRunOutcome(suitePaths[abortedIndex], RunVerdict.Inconclusive.ToString(), []));
        for (var remaining = abortedIndex + 1; remaining < suitePaths.Count; remaining++)
        {
            // No outcome, not Inconclusive: this suite was never attempted, and claiming the engine
            // tried and could not decide would be a verdict nobody reached (see SpecRunOutcome).
            specs.Add(new SpecRunOutcome(suitePaths[remaining], null, []));
        }

        var elevated = aggregate is { } current
            ? RunVerdictExtensions.Elevate(current, RunVerdict.Inconclusive)
            : RunVerdict.Inconclusive;

        return new RunSuiteOutcome.Completed(new RunSuiteResult(
            Verdict: elevated.ToString(),
            ExitCode: singleSuite ? processResult.ExitCode : null,
            Cancelled: wasCallerCancelled,
            TimedOut: !wasCallerCancelled,
            RemediationHint: wasCallerCancelled
                ? null
                : $"The run did not complete within {timeoutSeconds}s and was terminated.",

            // Empty for a single-suite abort, exactly as before US-S3-02 (nothing was parsed); for a
            // multi-suite one it carries the steps the EARLIER suites genuinely produced, which
            // happened and should not be discarded because a later suite ran out of budget.
            Steps: steps,
            EventsFilePath: eventsFilePath,
            Specs: specs,
            EventsTruncated: eventsTruncated));
    }

    /// <summary>
    /// Where one suite of a MULTI-suite run writes its own events stream before it is merged into the
    /// run's single stream — <c>&lt;events-file-base&gt;.part-NNN.jsonl</c>, a sibling of the run's
    /// own events file. Never used for a single-suite run, which writes straight into that file.
    /// </summary>
    /// <remarks>
    /// The <c>.jsonl</c> extension is kept as the part's OWN suffix rather than appended after it
    /// (<c>events.jsonl.part-001</c>) because that shape is load-bearing in no-workspace mode:
    /// <see cref="SweepStaleEventsFilesBestEffort"/> reaps <c>vouchfx-mcp-events-*.jsonl</c> from the
    /// OS temp directory, and a part outside that glob would be residue nothing ever collects if the
    /// server died between writing it and merging it.
    /// </remarks>
    private static string PartEventsFilePath(string eventsFilePath, int index)
    {
        const string jsonLinesExtension = ".jsonl";

        var directory = Path.GetDirectoryName(eventsFilePath) ?? string.Empty;
        var baseName = Path.GetFileName(eventsFilePath);
        if (baseName.EndsWith(jsonLinesExtension, StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^jsonLinesExtension.Length];
        }

        return Path.Combine(directory, $"{baseName}.part-{index + 1:D3}{jsonLinesExtension}");
    }

    /// <summary>What <see cref="AppendPartToRunStreamAsync"/> did with one suite's part file.</summary>
    /// <remarks>
    /// Three cases rather than the bool this used to return, because two of them were being conflated
    /// (a gatekeeper review's finding): a merge that FAILED reported the same <see langword="false"/>
    /// an ordinary merge did, so a run whose archived stream lost a suite to an I/O fault came back
    /// claiming <c>eventsTruncated: false</c>. The caller needs to tell all three apart — to set
    /// <see cref="RunSuiteResult.EventsTruncated"/> for the latter two and to warn ONCE for the cap.
    /// </remarks>
    private enum PartMergeResult
    {
        /// <summary>Appended in full — or there was nothing to append, which reads the same to a caller.</summary>
        Merged,

        /// <summary>Discarded deliberately: the run's stream had already reached its byte cap.</summary>
        CapReached,

        /// <summary>Not merged, or only partly merged, because the copy itself failed.</summary>
        Failed,
    }

    /// <summary>
    /// Appends one suite's part file to the run's single events stream and deletes the part —
    /// see this type's remarks on the events layout for why the streams are merged at all.
    /// </summary>
    /// <param name="onProgress">
    /// <see langword="null"/> suppresses the cap warning, which the caller passes once it has already
    /// issued it — the warning is about the RUN's stream, so repeating it per suite says nothing new.
    /// </param>
    /// <remarks>
    /// Every failure is swallowed: the part has ALREADY been parsed into the suite's own outcome by
    /// the time this runs, so a merge that fails costs the run's archived stream some of its content
    /// and costs the caller's result nothing beyond the honest
    /// <see cref="RunSuiteResult.EventsTruncated"/> flag. Failing the tool call over it would discard
    /// a verdict the engine genuinely produced — the same reasoning the guarded registry writes in
    /// <see cref="ExecuteRunAsync"/> record.
    /// </remarks>
    private static async Task<PartMergeResult> AppendPartToRunStreamAsync(
        string partPath, string eventsFilePath, Action<string>? onProgress)
    {
        var copyCompleted = false;

        try
        {
            var part = new FileInfo(partPath);
            if (!part.Exists || part.Length == 0)
            {
                // A suite that produced no events at all (it crashed before writing, or was killed):
                // nothing to merge, and nothing left behind either.
                return PartMergeResult.Merged;
            }

            var stream = new FileInfo(eventsFilePath);
            var alreadyWritten = stream.Exists ? stream.Length : 0L;
            if (alreadyWritten + part.Length > EventsFileReader.MaxEventsFileBytes)
            {
                onProgress?.Invoke(
                    $"Warning: this run's events stream reached {EventsFileReader.MaxEventsFileBytes:N0} "
                    + "bytes, so later suites' events were not merged into it. Their verdicts are "
                    + "reported in full; only the archived stream is incomplete.");
                return PartMergeResult.CapReached;
            }

            await using var source = File.OpenRead(partPath);
            await using var destination = new FileStream(
                eventsFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);

            try
            {
                await source.CopyToAsync(destination);
                copyCompleted = true;
            }
            finally
            {
                TerminateLineBestEffort(source, destination, copyCompleted);
            }

            return PartMergeResult.Merged;
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: see this
        // method's remarks. The suite's verdict is already computed and returned regardless; a merge
        // failure must never become the thing the caller diagnoses.
        catch (Exception)
#pragma warning restore CA1031
        {
            return PartMergeResult.Failed;
        }
        finally
        {
            TryDeleteQuietly(partPath);
        }
    }

    /// <summary>
    /// Ensures the run's events stream ends on a newline after a part was appended to it, so the next
    /// part's first event cannot be spliced onto this part's last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// JSON Lines is line-delimited, so a part whose final line is not newline-terminated would merge
    /// two events into one unparseable line and lose BOTH to the parser's per-line tolerance. Checked
    /// on the SOURCE (seekable, and just copied) rather than assumed.
    /// </para>
    /// <para>
    /// <b>Written unconditionally when the copy did NOT complete</b> (a gatekeeper review's finding):
    /// a copy that threw part-way through has almost certainly left a partial line at the end of the
    /// stream, and that is precisely the case the terminator exists for — yet the original code
    /// reached the check only on the success path, so the one situation guaranteed to need a newline
    /// was the one that never got one. The source's own last byte says nothing about how much of it
    /// arrived, hence "unconditionally" rather than "checked".
    /// </para>
    /// <para>
    /// Every failure here is swallowed, and it runs from a <c>finally</c> that may have an exception
    /// in flight: a destination stream that has just failed a write will very likely fail this one
    /// too, and letting that escape would REPLACE the original fault with a secondary one. The
    /// caller's answer is <see cref="PartMergeResult.Failed"/> either way.
    /// </para>
    /// </remarks>
    private static void TerminateLineBestEffort(FileStream source, FileStream destination, bool copyCompleted)
    {
        try
        {
            if (copyCompleted)
            {
                source.Seek(-1, SeekOrigin.End);
                if (source.ReadByte() == '\n')
                {
                    return;
                }
            }

            destination.WriteByte((byte)'\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ObjectDisposedException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Best-effort deletion of a part file this orchestrator created itself — never a caller-named
    /// path, and never the run's own events file (a single-suite run does not produce parts at all).
    /// </summary>
    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
