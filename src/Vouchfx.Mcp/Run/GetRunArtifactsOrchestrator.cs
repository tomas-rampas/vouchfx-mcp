using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-07's <c>get_run_artifacts</c> pipeline: resolve a <c>runId</c> through the run registry and
/// report what this build can honestly say about that run's artefacts — the event stream the registry
/// recorded, and whatever environment identifiers that stream itself named. Purely read + parse +
/// project: it never re-runs anything, never spawns the engine CLI, never takes the run lock, and never
/// writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This tool is a projection of things that already exist, not a new subsystem.</b> Its whole
/// substance is the run registry (US-S3-01) plus the same <see cref="EventsFileReader"/> and
/// <see cref="SuiteEventParser"/> the other three events-file readers use — see
/// <c>GetRunArtifactsModels.cs</c>'s header for the measured inventory of what those two sources
/// actually yield, for why <c>logs</c> is empty and <c>reports.html</c>/<c>reports.junit</c> are
/// omitted, and for the one derivation (reading the suite's own <c>environment:</c> block) that was
/// available and is deliberately refused.
/// </para>
/// <para>
/// <b>Stance (b), and it never errors over a gap in derivable data.</b>
/// <c>sprint-00-overview.md</c> §3 gives this story the "data-returning surface with an upstream-gated
/// portion" stance: the call succeeds with <c>partial: true</c> and the derivable subset. That governs
/// one design decision worth naming, because it makes this type deliberately UNLIKE its siblings: a
/// swept or unreadable events file is reported as a RESULT here (<c>reports.events.available: false</c>
/// plus a <see cref="RunArtifactGap"/>), where <c>get_step_timeline</c> and <c>get_run_events</c> answer
/// <c>VFX-E-1004</c>/<c>VFX-E-1005</c> for the identical condition. The difference is what the file IS
/// to each tool: for those two it is the entire answer, so an unreadable one leaves nothing to return;
/// for an artefacts INVENTORY it is one input of three, and "the stream is gone, and here is where it
/// was" is a better answer than a refusal. Errors are reserved for what is genuinely wrong rather than
/// merely absent — a bad argument (<c>VFX-E-1006</c>), an unknown run (<c>VFX-E-1505</c>), or a path
/// that fails workspace containment (<c>VFX-E-1001</c>).
/// </para>
/// <para>
/// <b><c>container</c> and <c>tailLines</c> are accepted and validated although neither selects or
/// bounds anything today</b> (US-S3-07 AC-003). That is the point: a host coding against
/// <c>tailLines: 5001</c> being refused today gets the same refusal once U4 makes the parameter
/// functional, so the contract does not change under it a second time. Silently ignoring an
/// out-of-range value would teach a host a bound that does not exist, and refusing the parameter
/// outright would make the later landing a breaking change to the input shape — the two failure modes
/// stance (a) and stance (b) exist to avoid.
/// </para>
/// <para>
/// <b>Read-only and LOCK-FREE</b> (US-S3-04's AC-004, spec §4.6's "read-only tools are safe to call
/// concurrently"). Nothing here touches <see cref="IRunLock"/>, which <c>RunLockSourceGuardTests</c>
/// holds structurally by naming this file in its must-never-take-the-lock list. The only filesystem
/// operations are <see cref="File.Exists(string)"/> and a bounded read, both on the registry's own
/// events path and both AFTER <see cref="PathSafetyGuard.CheckLocalPath"/> has passed it.
/// </para>
/// <para>
/// <b>Secret hygiene.</b> The only engine-sourced text that reaches a result is an
/// <c>environment-error</c> event's <c>resourceName</c>, <c>errorKind</c> and <c>detail</c> — already
/// redacted by the engine (the sole redaction authority) and already sanitised and capped at parse time
/// by <see cref="SuiteEventParser"/>. This type bounds them further and never re-redacts, never
/// resolves a <c>${secret:…}</c>, and never reads this process's environment — the last of which is
/// worth stating explicitly for a tool whose output field is literally called <c>environment</c>: it
/// means the RUN's environment as the run's own events described it, and never this server process's.
/// <c>RealSecretHygieneMcpTests</c> sweeps this tool's real round trip alongside the others.
/// </para>
/// </remarks>
public sealed class GetRunArtifactsOrchestrator
{
    /// <summary>Spec §5.12's documented default for <c>tailLines</c>.</summary>
    public const int DefaultTailLines = 200;

    /// <summary>Spec §5.12's documented maximum for <c>tailLines</c>, named verbatim in the refusal.</summary>
    public const int MaxTailLines = 5_000;

    /// <summary>
    /// Longest <c>container</c> this tool will accept — comfortably above a Docker container's 64-hex
    /// id and any Aspire resource name a suite would declare.
    /// </summary>
    /// <remarks>
    /// A bound on a value that currently selects nothing is not pointless: it is caller-supplied text
    /// that gets retained and echoed, and every such value in this server is bounded at its boundary
    /// rather than trusted to be reasonable. Deliberately smaller than
    /// <see cref="RunLifecycleLimits.MaxRunIdChars"/>'s 2,000, which sizes a "do not carry a
    /// multi-megabyte argument into a message" guard rather than a plausible container name.
    /// </remarks>
    internal const int MaxContainerChars = 256;

    /// <summary>
    /// Longest <c>kind</c> this tool will even compare — the four accepted values are at most eleven
    /// characters, so anything past this cannot be one of them and is refused before it is echoed.
    /// </summary>
    internal const int MaxKindChars = 64;

    /// <summary>
    /// The INTENDED cap on this tool's response size (UTF-8 JSON bytes), matching
    /// <see cref="GetStepTimelineOrchestrator.MaxResponseBytes"/>,
    /// <c>GetRunEventsOrchestrator.MaxResponseBytes</c> and
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>.
    /// </summary>
    /// <remarks>
    /// The same measured caveat those three record applies here unchanged and is not restated:
    /// <c>StructuredToolResult.Success</c> carries the payload twice and the text copy is an ESCAPED
    /// JSON string rather than a second verbatim one (measured at 2.213x, not 2x — see
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>, the single authority on that number).
    /// Halving into <see cref="EffectiveArtifactsBudgetBytes"/> is the same large-and-necessary but
    /// not-sufficient correction every other tool applies.
    /// </remarks>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// The bare payload's own budget — half of <see cref="MaxResponseBytes"/>, for the reason that
    /// constant records, and numerically the same figure
    /// <see cref="GetStepTimelineOrchestrator.EffectiveTimelineBudgetBytes"/> gives.
    /// </summary>
    /// <remarks>
    /// Declared here rather than borrowed from the sibling (which is what an earlier version of this
    /// type's test did) so this tool's budget is this tool's own constant: the two are equal today
    /// because the fleet-wide cap is, not because one derives from the other, and Sprint 4's re-budget
    /// should be able to move one without silently moving the other.
    /// </remarks>
    internal const int EffectiveArtifactsBudgetBytes = MaxResponseBytes / 2;

    /// <summary>
    /// How many candidate resource-list lengths <see cref="FitWithinBudget"/> will measure before
    /// falling back to the resource-free shape. Mirrors
    /// <c>GetStepTimelineOrchestrator.MaxFitProbes</c>, and for its reason: a fixed, small number of
    /// serialisations rather than a search that could run long.
    /// </summary>
    private const int MaxFitProbes = 8;

    /// <summary>
    /// How many DISTINCT environment resources one result may carry BEFORE the measured fit runs. See
    /// <see cref="FitWithinBudget"/> for what actually guarantees the response bound, and
    /// <see cref="BuildEnvironment"/> for why a static figure alone cannot.
    /// </summary>
    /// <remarks>
    /// Far beyond any realistic run: this counts the distinct resources that FAILED, and a topology in
    /// which twenty-five separate services and dependencies each raised an environment error is one
    /// whose problem is not going to be diagnosed by reading a longer list. The cap exists because an
    /// events file is untrusted input — two million <c>environment-error</c> lines naming two million
    /// resources is a legal file — not because a real one is expected to approach it. It is a
    /// CHEAPNESS bound (it keeps the number of entries the fit ever has to serialise small), not the
    /// size guarantee; twenty-five entries of maximally-escaped text is measurably over budget on its
    /// own. When either bound bites, <see cref="RunEnvironmentArtifacts.OmittedResourceCount"/> and
    /// <see cref="RunEnvironmentArtifacts.Truncated"/> both say so.
    /// </remarks>
    internal const int MaxEnvironmentResources = 25;

    /// <summary>Cap on one reported resource id.</summary>
    /// <remarks>
    /// <see cref="SuiteEventParser"/> already caps it at its own 2,000-character label bound; this is
    /// the response-facing cap, sized against the arithmetic in <see cref="BuildEnvironment"/>. An
    /// Aspire resource name is a handful of characters, so this only ever bites on a pathological
    /// stream.
    /// </remarks>
    internal const int MaxResourceIdChars = 128;

    /// <summary>Cap on one reported <c>errorKind</c> — an <c>OrchestrationErrorKind</c> name, a word.</summary>
    internal const int MaxErrorKindChars = 64;

    /// <summary>Cap on one reported <c>detail</c> — enough for the engine's own one-line explanation.</summary>
    internal const int MaxDetailChars = 200;

    /// <summary>The tool's own name, from the factory that owns it (see <see cref="GetRunEventsOrchestrator"/>).</summary>
    private static readonly string ToolName = Tools.GetRunArtifactsTool.Name;

    /// <summary>The upstream ask every gated gap in this build names — see sprint-00-overview.md §3.</summary>
    private const string UpstreamAsk = "U4";

    /// <summary>
    /// The size probe, mirroring every sibling tool's so a measured figure here is comparable — and,
    /// more to the point, so it carries the same <c>JavaScriptEncoder.Default</c> the wire does. See
    /// <see cref="BuildEnvironment"/>'s remarks for what a probe with a different encoder would miss.
    /// </summary>
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    private readonly IRunRegistry _runRegistry;
    private readonly Workspace? _workspace;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the ONLY way a <c>runId</c> becomes an events-file path here. Read,
    /// never written.
    /// </param>
    /// <param name="workspace">
    /// US-S3-08's workspace, or <see langword="null"/> when none was configured. Used to
    /// containment-check the registry's own events path before this tool touches it — the same "nothing
    /// is exempt from containment" rule <c>ExplainRunOrchestrator</c> records at length.
    /// </param>
    public GetRunArtifactsOrchestrator(IRunRegistry runRegistry, Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        _runRegistry = runRegistry;
        _workspace = workspace;
    }

    /// <summary>Resolves the run and builds the artefact inventory for the requested section(s).</summary>
    public async Task<GetRunArtifactsOutcome> GetAsync(
        GetRunArtifactsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateArguments(request, out var kind, out var container, out var tailLines) is { } argumentError)
        {
            return argumentError;
        }

        var entry = _runRegistry.TryGetRun(request.RunId!);
        if (entry is null)
        {
            // The SHARED VFX-E-1505 message — one wording for one catalogued condition across every
            // run-lifecycle tool. See RunIdArgument.DescribeMissingRun.
            return new GetRunArtifactsOutcome.RunNotFound(RunIdArgument.DescribeMissingRun(request.RunId!));
        }

        // Already absolute (every registry mints an absolute path), and still checked — the same rule
        // explain_run, get_run_events and get_step_timeline all apply to the registry's own value, and
        // applied here BEFORE any filesystem probe rather than only before the read.
        //
        // UNCONDITIONAL, including for kind: "logs", which reads nothing at all — deliberate, and it is
        // the fail-closed direction. Three reasons, in order of weight: the path is still ECHOED into
        // the result on the reports section and into gap text, so an unchecked one would leave the
        // workspace by a different door than the read; making the check conditional on the section
        // selector would mean a caller could learn whether a run's recorded path passes containment by
        // toggling `kind`, which is a probe this tool should not offer; and a registry whose entry
        // points outside the workspace is a fact about the RUN, so answering "here is your inventory"
        // for it would be answering about a run this server has already decided it will not read.
        var resolvedPath = entry.EventsFilePath;
        var displayPath = PathSafetyGuard.CapAndSanitisePathForDisplay(resolvedPath);

        if (PathSafetyGuard.CheckLocalPath(resolvedPath, _workspace, displayPath) is { } pathError)
        {
            return new GetRunArtifactsOutcome.InvalidPath(pathError.Message);
        }

        var gaps = new List<RunArtifactGap>();
        var wantsReports = kind is RunArtifactKind.Reports or RunArtifactKind.All;
        var wantsLogs = kind is RunArtifactKind.Logs or RunArtifactKind.All;
        var wantsEnvironment = kind is RunArtifactKind.Environment or RunArtifactKind.All;

        // Probed once and shared: both the reports inventory and the environment derivation need to
        // know whether the stream is still there, and asking the filesystem twice could answer
        // differently within one call.
        var eventsFileExists = (wantsReports || wantsEnvironment) && File.Exists(resolvedPath);

        var reports = wantsReports ? BuildReports(displayPath, eventsFileExists, gaps) : null;
        var logs = wantsLogs ? BuildLogs(gaps) : null;
        var environment = wantsEnvironment
            ? await BuildEnvironmentAsync(resolvedPath, displayPath, eventsFileExists, gaps, cancellationToken)
            : null;

        var candidate = new GetRunArtifactsResult(
            // The REGISTRY's id, not the caller's string, and unsanitised because it is one this server
            // minted ('run-' plus 32 hex) — the same reasoning GetRunStatusOrchestrator records for why
            // only specPaths needs escaping on the way out.
            RunId: entry.RunId,
            Kind: kind,
            // COMPUTED, never hardcoded — see GetRunArtifactsModels.cs's header for why this tool
            // carries a payload-level marker at all, and why a hardcoded one would be dishonest even
            // while the U4-gated gaps make it true on every call.
            Partial: gaps.Count > 0,
            Reports: reports,
            Logs: logs,
            Environment: environment,
            Container: container,
            TailLines: tailLines,
            Gaps: gaps);

        // MEASURED, not assumed to fit — see FitWithinBudget.
        return new GetRunArtifactsOutcome.Found(FitWithinBudget(candidate));
    }

    /// <summary>
    /// Applies every argument bound, and normalises <c>kind</c>. Returns the refusal, or
    /// <see langword="null"/> when the arguments are acceptable.
    /// </summary>
    /// <remarks>
    /// Typed as the concrete <see cref="GetRunArtifactsOutcome.InvalidArgument"/> rather than the base
    /// union for the reason <see cref="GetRunEventsOrchestrator.ValidateArguments"/> records: every
    /// refusal this method can produce IS one.
    /// </remarks>
    internal static GetRunArtifactsOutcome.InvalidArgument? ValidateArguments(
        GetRunArtifactsRequest request, out string kind, out string? container, out int tailLines)
    {
        ArgumentNullException.ThrowIfNull(request);

        kind = RunArtifactKind.All;
        container = null;
        tailLines = DefaultTailLines;

        // The SHARED runId rule, not a sixth copy of it.
        if (RunIdArgument.Validate(request.RunId, ToolName) is { } runIdError)
        {
            return new GetRunArtifactsOutcome.InvalidArgument(runIdError);
        }

        // Omitted — or sent as the empty/blank string some hosts serialise an unset optional into —
        // means "every section", which is what spec §5.12's own "all" literal names. A blank is
        // deliberately NOT an error: it carries no intent to misread.
        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            if (request.Kind.Length > MaxKindChars)
            {
                return new GetRunArtifactsOutcome.InvalidArgument(
                    $"{ToolName}'s 'kind' must be at most {MaxKindChars} characters — longer than any "
                    + $"accepted value ({DescribeKinds()}), so it could not be one of them.");
            }

            // Trimmed and matched case-insensitively, then echoed back in its canonical lower-case
            // spelling. The vocabulary is this server's own closed set rather than something read out
            // of an engine file, so leniency here costs no ambiguity — unlike get_run_events' event
            // 'types', which are matched exactly because the engine, not this server, decides them.
            var requested = request.Kind.Trim();
            var matched = RunArtifactKind.AllValues.FirstOrDefault(
                value => string.Equals(value, requested, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                return new GetRunArtifactsOutcome.InvalidArgument(
                    $"{ToolName}'s 'kind' must be one of {DescribeKinds()}. Got: "
                    + $"'{VfxCode.SanitiseForEcho(requested)}'. Omit it for every section.");
            }

            kind = matched;
        }

        if (request.Container is { } requestedContainer && requestedContainer.Length > MaxContainerChars)
        {
            return new GetRunArtifactsOutcome.InvalidArgument(
                $"{ToolName}'s 'container' must be at most {MaxContainerChars} characters.");
        }

        // Refused rather than silently clamped, and the message names the maximum — US-S3-07's AC-003
        // and its Gherkin ("the VfxError states the maximum tailLines is 5000"). A clamp would let a
        // host believe it had asked for 10,000 lines and received all of them, which is the failure a
        // forward-compatible bound exists to prevent; get_run_events' 'limit' refuses out of range for
        // the same reason.
        if (request.TailLines is { } requestedTailLines
            && (requestedTailLines < 1 || requestedTailLines > MaxTailLines))
        {
            return new GetRunArtifactsOutcome.InvalidArgument(
                $"{ToolName}'s 'tailLines' must be between 1 and {MaxTailLines} (spec §5.12); the "
                + $"default is {DefaultTailLines}. Got: {requestedTailLines}. It is validated now even "
                + "though this build has no container log lines to tail, so the bound does not change "
                + "under you when full log access lands (upstream ask U4).");
        }

        tailLines = request.TailLines ?? DefaultTailLines;

        // TWO-STAGE cap — the rule PathSafetyGuard.CapAndSanitisePathForDisplay documents, applied to
        // the other caller-supplied value this tool echoes. An earlier version of this comment claimed
        // the sanitisation here "only ever normalises control characters, never shortens", and that is
        // measurably false in the direction that matters: sanitisation EXPANDS. Every character outside
        // 0x20-0x7E becomes a six-character \uXXXX escape, so the 256-character bound above admits a
        // 1,536-character echo (256 ESC bytes), which the JSON serialiser then escapes again. The first
        // cap keeps the sanitisation itself cheap regardless of what an agent sent; this second cap, on
        // the ALREADY-sanitised text, is what actually bounds what reaches the response.
        //
        // Truncating mid-escape is accepted, exactly as it is in CapAndSanitisePathForDisplay: the
        // result is display text whose worst outcome is a trailing partial '\u00', never an unescaped
        // control character (the cut can only remove characters from an already-safe string).
        container = request.Container is { Length: > 0 } echoed
            ? CapText(TextSanitiser.SanitiseForDisplay(echoed), MaxContainerChars)
            : null;

        return null;
    }

    private static string DescribeKinds() =>
        string.Join(", ", RunArtifactKind.AllValues.Select(value => $"'{value}'"));

    /// <summary>
    /// The reports inventory: the run's own event stream, plus a gap for each engine-written report
    /// this build cannot reach.
    /// </summary>
    private static RunReportArtifacts BuildReports(
        string displayPath, bool eventsFileExists, List<RunArtifactGap> gaps)
    {
        gaps.Add(new RunArtifactGap(
            "reports.html",
            "The engine writes its HTML report where its own flags direct; this server neither passes "
            + "those flags nor is told the resulting path. An engine-owned artifacts directory is "
            + "upstream ask U4.",
            UpstreamAsk));

        gaps.Add(new RunArtifactGap(
            "reports.junit",
            "The engine writes its JUnit XML report where its own flags direct; this server neither "
            + "passes those flags nor is told the resulting path. An engine-owned artifacts directory "
            + "is upstream ask U4.",
            UpstreamAsk));

        if (!eventsFileExists)
        {
            // Not an error — see this type's remarks for why this tool reports a swept stream where its
            // siblings refuse over one. `Awaits` is null: nothing upstream would bring the file back.
            gaps.Add(new RunArtifactGap(
                "reports.events",
                $"The run is recorded in the registry, but its events file no longer exists: "
                + $"'{displayPath}'. A run's metadata outlives its event stream when the file is deleted "
                + "or the output directory is cleaned.",
                Awaits: null));
        }

        return new RunReportArtifacts(
            Html: null,
            Junit: null,
            Events: new RunEventsArtifact(displayPath, eventsFileExists, ResourceUri: null));
    }

    /// <summary>
    /// The logs inventory: an EMPTY list and a gap saying why — never a fabricated line, and never an
    /// error (US-S3-07 AC-002 and its first Gherkin scenario).
    /// </summary>
    private static IReadOnlyList<RunLogArtifact> BuildLogs(List<RunArtifactGap> gaps)
    {
        gaps.Add(new RunArtifactGap(
            "logs",
            "This build has no container log access at all: the pinned engine exposes no flag for it "
            + "and this server never talks to a container runtime, so there are no lines to tail and "
            + "none are invented. Container log access is upstream ask U4. For what the run itself "
            + "recorded, read its events with get_run_events or explain_run.",
            UpstreamAsk));

        return [];
    }

    /// <summary>
    /// The environment inventory: whatever resources the run's own <c>environment-error</c> events
    /// named, and a gap for each field the v1 stream cannot source.
    /// </summary>
    private static async Task<RunEnvironmentArtifacts> BuildEnvironmentAsync(
        string resolvedPath,
        string displayPath,
        bool eventsFileExists,
        List<RunArtifactGap> gaps,
        CancellationToken cancellationToken)
    {
        gaps.Add(new RunArtifactGap(
            "environment.services",
            "The v1 event stream never declares a service: it names a resource only when that resource "
            + "FAILED, and an environment-error event does not say whether the name belongs to the "
            + "suite's environment.services or its environment.dependencies. Rather than guess, every "
            + "identifier is reported under 'resources' with role 'unclassified'. A live service "
            + "inventory (with image and ports) is upstream ask U4.",
            UpstreamAsk));

        gaps.Add(new RunArtifactGap(
            "environment.dependencies",
            "Empty for the same reason environment.services is: the stream carries no declaration of "
            + "either, only failure events naming an unclassified resource. See 'resources'.",
            UpstreamAsk));

        gaps.Add(new RunArtifactGap(
            "environment.resources[].health",
            "Live health needs a probe against a running environment, which this server has no channel "
            + "to make and would not make after the fact anyway — the run is over. A null health means "
            + "'not observed', never 'unhealthy'. Upstream ask U4.",
            UpstreamAsk));

        if (!eventsFileExists)
        {
            gaps.Add(new RunArtifactGap(
                "environment.resources",
                $"No environment identifier could be read: the run's events file no longer exists "
                + $"('{displayPath}'), and this server's only source for one is that stream's own "
                + "environment-error events.",
                Awaits: null));

            return Empty();
        }

        var (content, eventsTruncated) = await EventsFileReader.TryReadBoundedAsync(resolvedPath, cancellationToken);
        if (content is null)
        {
            gaps.Add(new RunArtifactGap(
                "environment.resources",
                $"No environment identifier could be read: the run's events file existed when this "
                + $"call checked, and could not be read ('{displayPath}') — a permissions problem, a "
                + "lock held by another process, a path this platform will not open, or a deletion "
                + "that happened between the check and the read.",
                Awaits: null));

            return Empty();
        }

        // The SAME parse explain_run, diagnose_run and get_step_timeline run over the same file — this
        // tool adds no second, narrower scan of it, and reads the one collection
        // SuiteEventParser already produces for environment errors.
        var summary = SuiteEventParser.Parse(content);

        return BuildEnvironment(summary.EnvironmentErrors, eventsTruncated);

        static RunEnvironmentArtifacts Empty() => new([], [], [], Truncated: false, OmittedResourceCount: 0);
    }

    /// <summary>
    /// Folds the run's <c>environment-error</c> events into one entry per distinct resource, in
    /// first-appearance order, bounded by <see cref="MaxEnvironmentResources"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>First-wins on the reason, and a count instead of a list.</b> A resource that raised several
    /// environment errors keeps the FIRST event's <c>errorKind</c> and <c>detail</c> — the failure that
    /// started the trouble, rather than the last one to be logged after it — and reports how many
    /// events named it. Keeping every event's text per resource would put an unbounded, largely
    /// repetitive payload behind one identifier; a host that wants the individual events has
    /// <c>get_run_events</c> with <c>types: ["environment-error"]</c>, which relays them raw and paged.
    /// </para>
    /// <para>
    /// <b>Deduplication is on the PARSER's id, and the caps are for DISPLAY only.</b> An earlier
    /// version keyed the fold on the already-capped id, so two distinct resources sharing their first
    /// <see cref="MaxResourceIdChars"/> characters merged silently into one entry with a doubled
    /// occurrence count — a wrong answer presented as a derived fact, and invisible in the output
    /// because nothing said a merge had happened. The key is now
    /// <see cref="EnvironmentErrorSummary.ResourceName"/> as the shared parser produced it (itself
    /// bounded at that type's own 2,000-character label cap), and the cap is applied once, on the way
    /// into the response.
    /// </para>
    /// <para>
    /// <b>Why the static per-field caps here are NOT the response-size guarantee</b> (a security
    /// review's MAJOR finding, measured). The previous arithmetic assumed the worst case was non-ASCII
    /// text and that JSON could at worst DOUBLE a field: <see cref="SuiteEventParser"/> renders a
    /// non-ASCII character as a six-character <c>\uXXXX</c> escape and the serialiser escapes the
    /// backslash again, giving 7/6. That reasoning missed the WIRE ENCODER.
    /// <c>StructuredToolResult.Options</c> — and every size probe in this server, which mirrors it —
    /// leaves <c>JavaScriptEncoder.Default</c> in place, and that encoder escapes five PRINTABLE ASCII
    /// characters (<c>+</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>"</c>) to a six-byte
    /// <c>\uXXXX</c> form. Those five pass <see cref="TextSanitiser"/> untouched — it only escapes
    /// outside <c>0x20</c>-<c>0x7E</c> — so they are never shortened by the caps below and expand
    /// <b>6x</b>, not 2x, on the wire. At 128/64/200 characters of <c>+</c> one entry is ~2,470 bytes,
    /// and <see cref="MaxEnvironmentResources"/> of them is ~61.8 KB: MEASURED at 64,830 bytes for the
    /// full payload, 1.98x over the 32,768-byte budget (and ~140 KB of wire once
    /// <c>StructuredToolResult</c>'s double carry is applied). The old "pathological" fixture missed it
    /// because its <c>é</c> filler was DEFEATED BY PRE-SANITISATION: the parser had already turned each
    /// <c>é</c> into printable ASCII before the cap counted characters.
    /// </para>
    /// <para>
    /// So the caps stay — they bound one entry's contribution cheaply, and they are what keeps the
    /// number of fit probes small — but the GUARANTEE is <see cref="FitWithinBudget"/>, which
    /// serialises the assembled payload and sheds resources until it fits. That is the same
    /// "expected to fit is not verified to fit" rule <c>ExplainRunOrchestrator.BuildDiagnosis</c> and
    /// <c>GetStepTimelineOrchestrator.FitAttemptList</c> already apply, and it is deliberately chosen
    /// over re-deriving lower static caps: the derivation would rest on an escape TABLE this file does
    /// not own (a future encoder change, or a sixth escaped character, silently invalidates it) and
    /// would have to be redone by hand for every field this payload grows.
    /// </para>
    /// </remarks>
    private static RunEnvironmentArtifacts BuildEnvironment(
        IReadOnlyList<EnvironmentErrorSummary> environmentErrors, bool eventsTruncated)
    {
        var ordered = new List<string>();
        var byId = new Dictionary<string, (string? ErrorKind, string? Detail, int Occurrences)>(StringComparer.Ordinal);

        foreach (var error in environmentErrors)
        {
            // The PARSER's id, uncapped — see this method's remarks for the silent merge that keying
            // on the capped one caused.
            var key = error.ResourceName;

            if (byId.TryGetValue(key, out var existing))
            {
                byId[key] = existing with { Occurrences = existing.Occurrences + 1 };
                continue;
            }

            ordered.Add(key);
            byId[key] = (CapText(error.ErrorKind, MaxErrorKindChars), CapText(error.Detail, MaxDetailChars), 1);
        }

        var kept = Math.Min(ordered.Count, MaxEnvironmentResources);
        var resources = new List<RunEnvironmentResource>(kept);

        for (var i = 0; i < kept; i++)
        {
            var key = ordered[i];
            var (errorKind, detail, occurrences) = byId[key];

            // The parser's placeholder for an event that named NO resource is recognised here rather
            // than relayed as an identity — see RunEnvironmentResourceSource.UnnamedEnvironmentErrorEvent.
            var unnamed = string.Equals(key, SuiteEventParser.UnnamedResourceSentinel, StringComparison.Ordinal);

            resources.Add(new RunEnvironmentResource(
                Id: unnamed ? null : CapText(key, MaxResourceIdChars),
                // Never a guess — see RunEnvironmentResourceRole.
                Role: RunEnvironmentResourceRole.Unclassified,
                // Explicit null rather than omitted: AC-002 names this field, and "not observed" is a
                // fact worth stating at the field a host would otherwise read as unhealthy.
                Health: null,
                ErrorKind: errorKind,
                Detail: detail,
                Occurrences: occurrences,
                Source: unnamed
                    ? RunEnvironmentResourceSource.UnnamedEnvironmentErrorEvent
                    : RunEnvironmentResourceSource.EnvironmentErrorEvent));
        }

        var omitted = ordered.Count - kept;

        return new RunEnvironmentArtifacts(
            // Both spec arrays stay empty until something can classify an identifier — see
            // GetRunArtifactsModels.cs's header.
            Services: [],
            Dependencies: [],
            Resources: resources,
            Truncated: eventsTruncated || omitted > 0,
            OmittedResourceCount: omitted);
    }

    /// <summary>
    /// Guarantees the assembled payload serialises within <see cref="EffectiveArtifactsBudgetBytes"/>,
    /// by MEASURING it and shedding environment resources from the end until it fits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response bound, and the only thing that actually is one.</b>
    /// <see cref="BuildEnvironment"/>'s remarks record why the static per-field caps are not: the wire
    /// encoder expands five printable ASCII characters 6x, which no character count here can see. This
    /// method sees it, because it asks the serialiser rather than an arithmetic model of it — and the
    /// probe uses the same <c>JsonSerializerDefaults.Web</c> options every sibling tool's probe uses,
    /// which carry the same <c>JavaScriptEncoder.Default</c> the wire does.
    /// </para>
    /// <para>
    /// <b>Resources are what gives, and nothing else.</b> The gap list, the section shapes and both
    /// echoes are individually capped and are what a host needs to interpret the answer at all; the
    /// resource list is the only part an untrusted events file can grow. Shedding it is reported twice
    /// — <see cref="RunEnvironmentArtifacts.OmittedResourceCount"/> counts what went and
    /// <see cref="RunEnvironmentArtifacts.Truncated"/> says it happened — so nothing is dropped
    /// silently, and the count is against the DISTINCT total the parse saw rather than against
    /// whatever survived <see cref="MaxEnvironmentResources"/>.
    /// </para>
    /// <para>
    /// Each probe re-estimates the keep count from the candidate's own measurement and is clamped to at
    /// most <c>keep - 1</c> and at least <c>keep / 2</c>, so the loop makes strict progress, cannot
    /// cycle, and converges in two or three measurements — the shape and the reasoning
    /// <c>GetStepTimelineOrchestrator.FitAttemptList</c> records after measuring what naive halving
    /// cost there. The final fallback is the resource-free shape, and its size is bounded
    /// independently of the fit: the two variable-length things left in it are the events-path echo
    /// (<see cref="PathSafetyGuard.MaxDisplayedPathChars"/> = 1,000 characters post-sanitisation, spliced
    /// into at most three places) and the <c>container</c> echo (256 post-sanitisation), so even at the
    /// encoder's 6x that shape is ~22 KB against a 32,768-byte budget. Nothing in it scales with the
    /// events file's contents at all.
    /// </para>
    /// <para>
    /// <b>MEASURED on the worst case</b> (<c>GetRunArtifactsOrchestratorTests</c>' fixture: 200
    /// distinct resources, every <c>id</c>/<c>errorKind</c>/<c>detail</c> past its cap and made
    /// entirely of the 6x-escaping <c>+</c>, plus a 256-byte ESC <c>container</c> echo): the
    /// unshed payload is 64,830 B, and the fit returns 12 resources at 31,747 B with
    /// <c>omittedResourceCount: 188</c>. On any realistic run — where a resource id is a word and a
    /// detail is a sentence — the first measurement fits and nothing is shed at all.
    /// </para>
    /// </remarks>
    private static GetRunArtifactsResult FitWithinBudget(GetRunArtifactsResult candidate)
    {
        var bytes = SerialisedByteCount(candidate);
        if (bytes <= EffectiveArtifactsBudgetBytes
            || candidate.Environment is not { Resources.Count: > 0 } environment)
        {
            return candidate;
        }

        // Every distinct resource the parse saw, including those MaxEnvironmentResources already
        // dropped — so the reported omission count stays absolute rather than restarting at this point.
        var distinctTotal = environment.Resources.Count + environment.OmittedResourceCount;
        var keep = environment.Resources.Count;

        for (var probe = 0; probe < MaxFitProbes && keep > 0; probe++)
        {
            var rescaled = (int)((long)keep * EffectiveArtifactsBudgetBytes / bytes);
            keep = Math.Clamp(rescaled, keep / 2, keep - 1);

            var shed = WithResourceCount(candidate, environment, distinctTotal, keep);
            bytes = SerialisedByteCount(shed);
            if (bytes <= EffectiveArtifactsBudgetBytes)
            {
                return shed;
            }
        }

        return WithResourceCount(candidate, environment, distinctTotal, keep: 0);
    }

    /// <summary>
    /// The same result with its environment resource list shortened to <paramref name="keep"/> entries,
    /// and both truncation reports restated against <paramref name="distinctTotal"/>.
    /// </summary>
    private static GetRunArtifactsResult WithResourceCount(
        GetRunArtifactsResult candidate,
        RunEnvironmentArtifacts environment,
        int distinctTotal,
        int keep) =>
        candidate with
        {
            Environment = environment with
            {
                Resources = environment.Resources.Take(keep).ToArray(),
                Truncated = true,
                OmittedResourceCount = distinctTotal - keep,
            },
        };

    private static int SerialisedByteCount(GetRunArtifactsResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    /// <summary>Caps <paramref name="text"/> to <paramref name="maxChars"/>; a null stays null.</summary>
    private static string? CapText(string? text, int maxChars)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length > maxChars ? text[..maxChars] : text;
    }
}
