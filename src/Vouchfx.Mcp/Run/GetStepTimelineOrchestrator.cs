using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-06's <c>get_step_timeline</c> pipeline: resolve a <c>runId</c> through the run registry, read
/// that run's JSON Lines event stream through the same bounded reader every other events-file tool
/// uses, parse it with the same <see cref="SuiteEventParser"/>, and return ONE step's complete attempt
/// timeline. Purely read + parse + project — it never re-runs anything, never spawns the engine CLI,
/// and never takes the run lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this tool exists: it inverts <c>explain_run</c>'s truncation order.</b> Plan §2.4's stated
/// rationale is that "today a long RETRY timeline is the first thing the tiers throw away" —
/// <c>ExplainRunOrchestrator</c>'s largest tier keeps ten notable steps with ten attempts each, so a
/// forty-attempt poll loop is cut to its first ten before a host ever sees it, and the tiers get
/// SMALLER under pressure (five, then zero). That is the right trade for a whole-run diagnosis and the
/// wrong one for the question "what did this one step actually do", which is what this tool answers.
/// So the budget order here is inverted: <b>per-attempt evidence TEXT is what shrinks, and the attempt
/// LIST is what is preserved</b> (<see cref="ObservedCharTiers"/>). The two tools read the SAME parsed
/// <see cref="SuiteRunSummary.AttemptsByStepId"/> — the story's "extracted from, not duplicated
/// alongside" criterion — and differ only in what they are prepared to drop from it.
/// </para>
/// <para>
/// <b>What that immunity is worth, MEASURED rather than asserted</b> (probe run 2026-09-05, .NET 8).
/// <b>Both figures below are PINNED as bands</b> by
/// <c>GetStepTimelineOrchestratorTests.TheMinimalTier_FitsFarMoreAttemptsThanExplainRunsLargestTier</c>
/// and <c>…UnderBudgetPressure_EvidenceTextShrinksAndTheAttemptListDoesNot</c>, whose failure messages
/// say to re-measure this paragraph — so a shape change that moves them fails a test rather than
/// silently making this comment false (a gatekeeper review's finding: they were comment-only, while
/// this text claimed the tests recomputed them). The story's forty-attempt Gherkin case, carrying a 1,000-character observation
/// on every attempt, comes back with ALL FORTY attempts at the compact tier — <b>12,114 B</b> against
/// the 32,768 B budget, with each attempt's evidence text cut to 200 characters and
/// <c>observedCapped</c> set. The list is never what shrinks. Carrying no evidence text at all,
/// <b>472 attempts</b> fit — <b>47x</b> <c>explain_run</c>'s largest tier of ten, and far beyond what
/// an exponential-backoff poll loop against a declared step timeout can produce. (The attempt figure was
/// 469 before <see cref="FitAttemptList"/> stopped halving on a near miss; the byte figure is unchanged.
/// The band the test pins is wide enough for both, and for the 470 an independent re-measurement found —
/// the claim being defended is the order of magnitude, not the last digit.)
/// </para>
/// <para>
/// <b>The <c>specPath</c> argument: validated, and then informational for a multi-suite run. This is an
/// adjudication, not an oversight.</b> Spec §5.10 takes <c>specPath</c> alongside <c>runId</c> and
/// <c>stepId</c>, which reads as though the three together select a timeline. They cannot, and the
/// reason is a documented property of this server's own events layout: a multi-suite <c>run_suite</c>
/// call (US-S3-02) runs each suite into its own part file and then CONCATENATES them into the run's
/// single stream, and <c>RunSuiteOrchestrator</c>'s own remarks record the cost — "a reader also cannot
/// tell from the file alone which suite a line came from". <see cref="SuiteEventParser"/> therefore
/// keys attempts by <c>stepId</c> alone, because that is all the file supports. Three options were
/// available and the third is taken:
/// <list type="number">
/// <item><description>
/// <b>Ignore <c>specPath</c> entirely.</b> Rejected: a host that names the wrong suite would get a
/// confident answer about a run that never touched it.
/// </description></item>
/// <item><description>
/// <b>Refuse any multi-suite run.</b> Rejected: it withdraws the tool from a legitimate and
/// increasingly common shape of call to avoid an ambiguity that usually is not present (two suites in
/// one run sharing a step ID is possible, not typical), and it would make the tool's availability
/// depend on how the caller happened to batch its suites.
/// </description></item>
/// <item><description>
/// <b>Validate it, then say what it did and did not establish.</b> Taken. The argument is checked
/// against the run's recorded <see cref="RunRegistryEntry.SpecPaths"/> and a suite the run never
/// covered is refused outright (<c>VFX-E-1509</c>) — so the wrong-suite mistake is caught. When the run
/// covered EXACTLY ONE suite, every event in the stream came from it and the attribution is a
/// certainty: <see cref="GetStepTimelineResult.SpecPathAttributed"/> is <see langword="true"/>. When it
/// covered several, the flag is <see langword="false"/>, the <c>conclusion</c> says in words that the
/// timeline is the run-wide one for that step id, and nothing pretends to have filtered.
/// </description></item>
/// </list>
/// Flagged for spec adjudication: §5.10's input shape implies a per-suite selection the v1 event
/// contract cannot support. Closing it properly needs a suite discriminator on the engine's own
/// events, which is an upstream ask, not something this server can invent.
/// </para>
/// <para>
/// <b>Spec §5.10's three awkward fields, each settled by MEASUREMENT rather than by inference</b>
/// (<c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c> — a real RETRY suite run against the pinned
/// engine, whose remarks carry the verbatim event lines). An earlier version of this paragraph said all
/// three "have no source in the v1 event stream"; that was an inference from the story's synthetic
/// fixtures, and it was wrong about two of them:
/// <list type="bullet">
/// <item><description>
/// <b><c>Attempt.at</c> IS sourced and IS populated.</b> Every event the pinned engine writes carries a
/// <c>ts</c> property — a 33-character ISO-8601 instant with offset — <c>step-attempt</c> included, so
/// <see cref="StepTimelineAttempt.At"/> is a real value in production and the relay path was already
/// correct. What it is NOT is a per-attempt instant: the engine stamps <c>ts</c> as it renders its
/// buffered report, so every event in one file shares a handful of identical values (measured: 15
/// events, 3 distinct <c>ts</c>). It is relayed verbatim, and <see cref="StepTimelineAttempt.TMs"/>
/// remains what orders the timeline.
/// </description></item>
/// <item><description>
/// <b><c>Attempt.delayMs</c> is genuinely absent, and the measurement STRENGTHENS the refusal to derive
/// it.</b> No inter-attempt delay is emitted, and the open question about what <c>tMs</c> measures is
/// now closed: it is PER-ATTEMPT duration, not cumulative elapsed — the probe run's eight attempts
/// report 6, 5, 6, 19, 18, 6, 6, 6 ms inside a ten-second polling window, which is non-monotonic and so
/// cannot be a running elapsed figure. Subtracting consecutive <c>tMs</c> values would not have
/// approximated a backoff even roughly. Explicit <see langword="null"/>, for the reason
/// <see cref="StepTimelineAttempt.DelayMs"/> records.
/// </description></item>
/// <item><description>
/// <b><c>timeoutMs</c> has a source, on an event type this build does not parse.</b> The engine's
/// <c>step-started</c> event carries both <c>timeoutMs</c> and the suite's DECLARED <c>verifyMode</c>
/// (measured: <c>{"type":"step-started",…,"verifyMode":"RETRY","timeoutMs":10000}</c>).
/// <see cref="SuiteEventParser"/> handles four event types and <c>step-started</c> is not among them, so
/// nothing in this server reads it today and the field is still reported as <see langword="null"/> — an
/// honest statement of what THIS build sources, not of what the contract offers. Sourcing it (and with
/// it a declared-<c>verifyMode</c> field, which would be a different fact from the run-evidenced
/// <see cref="GetStepTimelineResult.VerifyMode"/> this tool reports) is an available follow-up rather
/// than an upstream ask, and is deliberately not taken here: it changes what the shared parser collects
/// for three other tools as well.
/// </description></item>
/// </list>
/// Every remaining <see langword="null"/> is written explicitly with its reason on the field itself, and
/// every available fabrication — a synthesised instant, a subtraction of consecutive <c>tMs</c> values,
/// the largest <c>tMs</c> observed — is still refused for the same reason: the number would look
/// measured and would not be.
/// </para>
/// <para>
/// <b>Read-only and LOCK-FREE</b> (US-S3-04's AC-004, spec §4.6's "read-only tools are safe to call
/// concurrently"). Nothing here touches <see cref="IRunLock"/>, which <c>RunLockSourceGuardTests</c>
/// holds structurally by naming this file in its must-never-take-the-lock list. Nothing here writes,
/// either: the events file is opened for reading through <see cref="EventsFileReader"/> and no suite
/// file is opened at all.
/// </para>
/// <para>
/// <b>Secret hygiene:</b> the only engine-sourced text that reaches a result is an attempt's
/// <c>observation</c> and (if a future engine emits one) its <c>error</c>, both already redacted by the
/// engine — the sole redaction authority — and both already sanitised and capped at parse time by
/// <see cref="SuiteEventParser"/>. This type bounds them further and never re-redacts, never resolves a
/// <c>${secret:…}</c>, and never reads this process's environment. <c>RealSecretHygieneMcpTests</c>
/// sweeps this tool's real round trip alongside the others.
/// </para>
/// </remarks>
public sealed class GetStepTimelineOrchestrator
{
    /// <summary>
    /// The INTENDED cap on this tool's response size (UTF-8 JSON bytes), matching
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c> and
    /// <c>GetRunEventsOrchestrator.MaxResponseBytes</c>.
    /// </summary>
    /// <remarks>
    /// The same measured caveat those two record applies here unchanged and is not restated:
    /// <c>StructuredToolResult.Success</c> carries the payload twice and the text copy is an ESCAPED
    /// JSON string rather than a second verbatim one (measured at 2.213x, not 2x, in
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>'s remarks — the single authority on that
    /// number). Halving into <see cref="EffectiveTimelineBudgetBytes"/> is the same large-and-necessary
    /// but not-sufficient correction every other tool applies, and Sprint 4 owns the fleet-wide
    /// re-budget with a <c>resourceUri</c> hand-off rather than a raised cap.
    /// </remarks>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// The bare payload's own budget, measured against by <see cref="BuildTimeline"/> — half of
    /// <see cref="MaxResponseBytes"/>, for the reason that constant records.
    /// </summary>
    internal const int EffectiveTimelineBudgetBytes = MaxResponseBytes / 2;

    /// <summary>
    /// Maximum characters kept for ONE attempt's <c>observed</c> text, tried in order — rich, compact,
    /// none. <b>This list, and not the attempt count, is what shrinks under budget pressure</b>: see
    /// this type's remarks for why that inversion is the whole point of the tool.
    /// </summary>
    /// <remarks>
    /// <b>Neither of the first two figures is borrowed from another constant, and an earlier version
    /// of this comment claimed both were</b> (a gatekeeper review's minor finding). The real sizing,
    /// which is about what this tool caps rather than about matching anything:
    /// <list type="bullet">
    /// <item><description>
    /// <b>2,000 — the rich tier.</b> It is well BELOW <c>SuiteEventParser.MaxObservationCharsAtParse</c>
    /// (10,000), which is the bound an <c>observation</c> actually arrives under, so tier 0 is a real
    /// projection cap and not a restatement of the parse cap. (It coincides numerically with
    /// <c>MaxLabelCharsAtParse</c>, which the old comment named — but that constant bounds step ids and
    /// error labels, never an observation, so the coincidence justified nothing.) 2,000 characters is
    /// about as much of a diff or response excerpt as is readable in one attempt's worth of evidence.
    /// </description></item>
    /// <item><description>
    /// <b>200 — the compact tier.</b> It matches no <c>ExplainRunOrchestrator</c> tier: that type's
    /// compact tier is 300 characters for a STEP's observation and 100 for an ATTEMPT's. It sits
    /// between them on purpose, because the two tools are capping different quantities — explain_run
    /// caps text across at most ten attempts, this caps text across the WHOLE list, so the figure has
    /// to be sized against a several-hundred-entry timeline rather than against a ten-entry one. At
    /// 200 characters, the story's forty-attempt case lands at ~12 KB of the 32 KB budget (measured;
    /// see this type's remarks), which is the headroom the tier exists to buy.
    /// </description></item>
    /// <item><description>
    /// <b>0 — the minimal tier.</b> No evidence text at all, which is what makes its size analysable
    /// rather than merely small: with <c>observed</c> gone, an attempt's cost has a floor
    /// (<see cref="MinAttemptJsonBytes"/>) and therefore so does the whole payload.
    /// </description></item>
    /// </list>
    /// </remarks>
    private static readonly int[] ObservedCharTiers = [2_000, 200, 0];

    /// <summary>
    /// How many candidate list lengths <see cref="FitAttemptList"/> will measure before giving up and
    /// returning the attempt-free shape.
    /// </summary>
    /// <remarks>
    /// <b>A fixed, small number of serialisations, not a search that could run long.</b> The first
    /// candidate comes from the average attempt size the minimal-tier measurement already produced, so
    /// it is close; each subsequent one is re-estimated from the candidate that just missed, and is
    /// clamped to at most <c>keep - 1</c> and at least <c>keep / 2</c> — so every probe makes strict
    /// progress, the loop cannot cycle, and it converges in two or three measurements rather than
    /// discarding half the timeline per miss (see <see cref="FitAttemptList"/> for the measured
    /// regression that shape caused). This is the one place the response bound can shorten
    /// <see cref="GetStepTimelineResult.Attempts"/> at all, and it exists because "unreachable in
    /// practice" is not the same as "bounded": an events file is untrusted input, two million
    /// <c>step-attempt</c> lines for one step id is a legal file, and a tool that returned all of them
    /// would have no response bound. When it bites,
    /// <see cref="GetStepTimelineResult.OmittedAttemptCount"/> and
    /// <see cref="GetStepTimelineResult.Truncated"/> both say so — nothing is dropped silently.
    /// <para>
    /// Measuring rather than dividing by a worst-case per-attempt constant is deliberate: the worst case
    /// (every attempt carrying this server's longest <c>error</c> sentence) is roughly four times the
    /// ordinary case, so a constant sized for it would truncate timelines four times shorter than the
    /// budget actually allows. The cost of being accurate here is at most
    /// <see cref="MaxFitProbes"/> extra serialisations on a path no realistic timeline reaches.
    /// </para>
    /// </remarks>
    private const int MaxFitProbes = 8;

    /// <summary>
    /// Cap on a relayed <c>at</c> timestamp. The pinned engine's own <c>ts</c> is exactly 33
    /// characters (<c>2026-09-05T22:21:12.3829238+00:00</c> — measured, see
    /// <see cref="StepTimelineAttempt.At"/>), so this leaves a little headroom without admitting a
    /// field long enough to matter to <see cref="MinAttemptJsonBytes"/>.
    /// </summary>
    private const int MaxAtChars = 40;

    /// <summary>
    /// Cap on an attempt's <c>error</c> text — enough for a full sentence naming a token.
    /// </summary>
    /// <remarks>
    /// <b>Applied UNCONDITIONALLY, at every tier, and it must stay that way.</b> The name once said
    /// "AtMinimalTier", which read as though some richer tier lifted it — nothing ever did, and the
    /// reading was dangerous rather than merely untidy: this is the SOLE bound on the echo of an
    /// engine-supplied <c>error</c> string into the response (<c>SuiteEventParser</c> caps that field
    /// at its own 2,000-character label bound, which every attempt in a long timeline could carry).
    /// A later change that makes it tier-conditional — "the rich tier can afford the full 2,000" —
    /// would multiply that by the attempt count and hand the response budget a term it does not
    /// control. Renamed (a gatekeeper review's minor finding) so the constant's name states what the
    /// code does.
    /// </remarks>
    internal const int MaxErrorChars = 300;

    /// <summary>
    /// The smallest number of UTF-8 JSON bytes ONE attempt can occupy in a serialised timeline —
    /// measured, not estimated.
    /// </summary>
    /// <remarks>
    /// The minimal-tier shape is exactly
    /// <c>{"n":1,"at":null,"delayMs":null,"tMs":0,"outcome":"matched"}</c> — 60 bytes, with
    /// <c>observed</c> and <c>error</c> omitted by their <c>WhenWritingNull</c> conditions and
    /// <c>n</c>/<c>tMs</c> at their shortest. Every other attempt is larger: <c>unmatched</c> adds
    /// two bytes, <c>error</c> always carries an <c>error</c> sentence, a relayed <c>at</c> adds
    /// ~35, and the array's own separating comma adds one. So this is a genuine FLOOR, which is the
    /// only property <see cref="MaxFittableAttempts"/> needs of it.
    /// </remarks>
    private const int MinAttemptJsonBytes = 60;

    /// <summary>
    /// The largest number of attempts that could ever fit <see cref="EffectiveTimelineBudgetBytes"/>,
    /// however short each one is — <b>the bound on how much this type ever serialises</b>.
    /// </summary>
    /// <remarks>
    /// <b>Why a bound is needed at all, MEASURED</b> (a security review's MAJOR finding).
    /// <see cref="BuildTimeline"/> used to serialise the WHOLE attempt list at all three tiers before
    /// <see cref="FitAttemptList"/> could shorten it, so the work a call did was set by the events
    /// file rather than by the response budget: a 10,000-attempt timeline carrying 10,000-character
    /// observations cost <b>~1.9 GB of allocation and 2.3 s</b> to produce a 16 KB answer, all of it
    /// spent serialising candidates that could not possibly fit. They could not, and that is the
    /// insight this constant encodes: at <see cref="MinAttemptJsonBytes"/> a list longer than
    /// <see cref="EffectiveTimelineBudgetBytes"/>/60 = 546 attempts is over budget before its first
    /// character of evidence text, so measuring it establishes nothing. Every tier probe is therefore
    /// capped at this many attempts plus one — the extra keeps
    /// <see cref="GetStepTimelineResult.Truncated"/> and
    /// <see cref="GetStepTimelineResult.OmittedAttemptCount"/> honest at the boundary, since a
    /// timeline of exactly the fitting length must still be reported as complete — and
    /// <see cref="GetStepTimelineResult.OmittedAttemptCount"/> is computed against the FULL attempt
    /// count regardless, so nothing this cap skips is skipped silently.
    /// </remarks>
    private const int MaxFittableAttempts = EffectiveTimelineBudgetBytes / MinAttemptJsonBytes;

    /// <summary>Cap on the derived <c>conclusion</c> sentence.</summary>
    private const int MaxConclusionChars = 1_000;

    /// <summary>
    /// Cap on the echoed <c>specPath</c>, matching what <see cref="PathSafetyGuard"/> applies to a path
    /// it renders for display.
    /// </summary>
    private const int MaxSpecPathChars = 1_000;

    /// <summary>
    /// The label cap <see cref="SuiteEventParser"/> already applies to a <c>stepId</c>, restated here
    /// for <c>ValidateArguments</c>' bound and the stepId truncation (the parser's own constant is
    /// private, deliberately — its cap is a parse-time concern, not a contract; a security review
    /// removed a stale reference here to a <c>MaxShellBytes</c> constant that no longer exists).
    /// </summary>
    private const int SuiteEventParserLabelCap = 2_000;

    /// <summary>The tool's own name, from the factory that owns it (see <see cref="GetRunEventsOrchestrator"/>).</summary>
    private static readonly string ToolName = Tools.GetStepTimelineTool.Name;

    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    private readonly IRunRegistry _runRegistry;
    private readonly Workspace? _workspace;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the ONLY way a <c>runId</c> becomes an events-file path here, and the
    /// only source of the <c>specPaths</c> the caller's argument is checked against. Read, never
    /// written.
    /// </param>
    /// <param name="workspace">
    /// US-S3-08's workspace, or <see langword="null"/> when none was configured. Used to rebase a
    /// relative <c>specPath</c> before comparing it, and to containment-check the registry's own events
    /// path — the same "nothing is exempt from containment" rule <c>ExplainRunOrchestrator</c> records
    /// at length.
    /// </param>
    public GetStepTimelineOrchestrator(IRunRegistry runRegistry, Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        _runRegistry = runRegistry;
        _workspace = workspace;
    }

    /// <summary>Resolves the run, reads its stream, and projects one step's attempt timeline.</summary>
    public async Task<GetStepTimelineOutcome> GetAsync(
        GetStepTimelineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateArguments(request) is { } argumentError)
        {
            return argumentError;
        }

        var entry = _runRegistry.TryGetRun(request.RunId!);
        if (entry is null)
        {
            // The SHARED VFX-E-1505 message — one wording for one catalogued condition across every
            // run-lifecycle tool. See RunIdArgument.DescribeMissingRun.
            return new GetStepTimelineOutcome.RunNotFound(RunIdArgument.DescribeMissingRun(request.RunId!));
        }

        if (MatchSpecPath(request.SpecPath!, entry) is not { } matchedSpecPath)
        {
            return new GetStepTimelineOutcome.SpecPathNotInRun(DescribeSpecPathNotInRun(request.SpecPath!, entry));
        }

        // Already absolute (every registry mints an absolute path), and still checked — the same rule
        // ExplainRunOrchestrator and GetRunEventsOrchestrator both apply to the registry's own value.
        var resolvedPath = entry.EventsFilePath;
        var displayPath = PathSafetyGuard.CapAndSanitisePathForDisplay(resolvedPath);

        if (PathSafetyGuard.CheckLocalPath(resolvedPath, _workspace, displayPath) is { } pathError)
        {
            return new GetStepTimelineOutcome.InvalidPath(pathError.Message);
        }

        if (!File.Exists(resolvedPath))
        {
            return new GetStepTimelineOutcome.EventsFileNotFound(
                $"The run '{VfxCode.SanitiseForEcho(entry.RunId)}' is recorded in the registry, but its "
                + $"events file no longer exists: '{displayPath}'. The run's metadata outlives its event "
                + "stream when the file is deleted or the output directory is cleaned.");
        }

        var (content, eventsTruncated) = await EventsFileReader.TryReadBoundedAsync(resolvedPath, cancellationToken);
        if (content is null)
        {
            return new GetStepTimelineOutcome.EventsFileUnreadable(
                $"The events file could not be read: '{displayPath}'.");
        }

        // The SAME parse explain_run and diagnose_run run over the same file — not a second, narrower
        // scan of it. That is US-S3-06's "extracted from, not duplicated alongside" criterion held
        // structurally: there is one attempt-parsing implementation in this server and this is a
        // consumer of it.
        var summary = SuiteEventParser.Parse(content);

        // The caller's stepId is RAW; the parser stores every step id sanitised and capped (see
        // SuiteEventParser.SanitiseAndCapLabel). Comparing raw against stored would silently miss any
        // id containing a character the sanitiser escapes, so the caller's value is put through the
        // same transformation before the lookup. Ordinal throughout: a step id is matched by a machine.
        var stepId = TextSanitiser.SanitiseForDisplay(
            request.StepId!.Length > SuiteEventParserLabelCap
                ? request.StepId[..SuiteEventParserLabelCap]
                : request.StepId);

        var attempts = summary.AttemptsByStepId.TryGetValue(stepId, out var recorded) ? recorded : [];
        var step = summary.Steps.FirstOrDefault(s => string.Equals(s.StepId, stepId, StringComparison.Ordinal));

        if (attempts.Count == 0 && step is null)
        {
            // Deliberately an ERROR rather than an empty timeline. A step with no attempts is a real
            // and different state (an IMMEDIATE step whose attempt events were never emitted still has
            // a step-completed event), and returning `attempts: []` for a step id the run never
            // mentioned would make "this step did nothing" and "you asked about a step that is not in
            // this run" the same answer.
            return new GetStepTimelineOutcome.StepNotInRun(DescribeStepNotInRun(stepId, entry, summary));
        }

        return new GetStepTimelineOutcome.Found(
            BuildTimeline(matchedSpecPath, stepId, attempts, step, entry, eventsTruncated));
    }

    /// <summary>
    /// Applies every argument bound. Returns the refusal, or <see langword="null"/> when the arguments
    /// are acceptable.
    /// </summary>
    /// <remarks>
    /// Typed as the concrete <see cref="GetStepTimelineOutcome.InvalidArgument"/> rather than the base
    /// union for the reason <see cref="GetRunEventsOrchestrator.ValidateArguments"/> records: every
    /// refusal this method can produce IS one.
    /// </remarks>
    internal static GetStepTimelineOutcome.InvalidArgument? ValidateArguments(GetStepTimelineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The SHARED runId rule, not a fifth copy of it.
        if (RunIdArgument.Validate(request.RunId, ToolName) is { } runIdError)
        {
            return new GetStepTimelineOutcome.InvalidArgument(runIdError);
        }

        if (string.IsNullOrWhiteSpace(request.SpecPath))
        {
            return new GetStepTimelineOutcome.InvalidArgument(
                $"{ToolName} requires 'specPath' — one of the suite paths the run covered. Call "
                + "get_run_status with the same runId to see them.");
        }

        if (request.SpecPath.Length > MaxSpecPathChars)
        {
            return new GetStepTimelineOutcome.InvalidArgument(
                $"{ToolName}'s 'specPath' must be at most {MaxSpecPathChars} characters — longer than "
                + "any path the run registry would have recorded, so it could not match one.");
        }

        if (string.IsNullOrWhiteSpace(request.StepId))
        {
            return new GetStepTimelineOutcome.InvalidArgument(
                $"{ToolName} requires 'stepId' — the id of the step whose attempt timeline you want. "
                + "Call explain_run or get_run_events for the same run to see which step ids it recorded.");
        }

        return request.StepId.Length > SuiteEventParserLabelCap
            ? new GetStepTimelineOutcome.InvalidArgument(
                $"{ToolName}'s 'stepId' must be at most {SuiteEventParserLabelCap} characters — the same "
                + "bound this server applies to a step id read out of an event stream, so a longer value "
                + "could not match one.")
            : null;
    }

    /// <summary>
    /// The run's own recorded spec path that <paramref name="specPath"/> names, or
    /// <see langword="null"/> when it names none of them.
    /// </summary>
    /// <remarks>
    /// <b>Three comparisons, widening, and each earns its place.</b> First an ordinal match against the
    /// recorded string, which is what a host pasting a path straight out of <c>get_run_status</c> hits.
    /// Then a match after resolving both sides to full paths — the recorded value is absolute and
    /// workspace-rebased, so a caller naming the same file relatively, or with a <c>./</c> segment, is
    /// naming the same suite and should not be refused for spelling it differently. Both use
    /// <see cref="PathSafetyGuard.PathComparison"/>, which is case-insensitive on Windows and ordinal
    /// elsewhere, so the comparison follows the platform's own file-name semantics rather than
    /// inventing a rule.
    /// <para>
    /// The RECORDED path is returned rather than the caller's, so the echoed
    /// <see cref="GetStepTimelineResult.SpecPath"/> is the file this server actually ran — the same
    /// choice <c>SpecRunOutcome.Path</c> makes for the same reason.
    /// </para>
    /// <para>
    /// Nothing here touches the filesystem: a suite file deleted since the run is still a suite the run
    /// covered, and refusing the timeline over it would be a fact about today rather than about the
    /// run.
    /// </para>
    /// <para>
    /// <b>That is also what makes the caller's <c>specPath</c> safe to normalise without a containment
    /// check</b> (a security review's INFO finding, recorded so the next story does not inherit the
    /// exemption by accident). <see cref="SafeFullPath"/> resolves the caller's string only to COMPARE
    /// it, and this tool never opens it — the only file it opens is the registry's own events path,
    /// which IS containment-checked in <see cref="GetAsync"/>. The safety rests entirely on "never
    /// opened", not on anything the comparison itself establishes: a caller may name
    /// <c>../../etc/passwd</c> here and the worst that happens is a <c>VFX-E-1509</c> saying the run
    /// did not cover it. <b>Any future story that makes this tool READ the suite — to source
    /// <c>timeoutMs</c> or the declared <c>verifyMode</c>, the two obvious asks — must add
    /// <see cref="PathSafetyGuard.CheckLocalPath"/> at this seam before it opens anything</b>, exactly
    /// as <c>validate_suite</c> does for its own <c>path</c> argument.
    /// </para>
    /// </remarks>
    private string? MatchSpecPath(string specPath, RunRegistryEntry entry)
    {
        foreach (var recorded in entry.SpecPaths)
        {
            if (string.Equals(recorded, specPath, PathSafetyGuard.PathComparison))
            {
                return recorded;
            }
        }

        // Rebasing a relative caller path onto the workspace root is what the tool descriptions promise
        // of every path argument; with no workspace configured ResolveCallerPath returns its argument
        // untouched and this second pass differs from the first only by full-path normalisation.
        var resolved = SafeFullPath(PathSafetyGuard.ResolveCallerPath(specPath, _workspace));
        if (resolved is null)
        {
            return null;
        }

        foreach (var recorded in entry.SpecPaths)
        {
            if (SafeFullPath(recorded) is { } recordedFull
                && string.Equals(recordedFull, resolved, PathSafetyGuard.PathComparison))
            {
                return recorded;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> without its exceptions — a caller-supplied string is not
    /// necessarily a well-formed path, and "that is not a path this run covered" is the honest answer
    /// to one that is not, rather than an uncoded framework exception.
    /// </summary>
    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string DescribeSpecPathNotInRun(string specPath, RunRegistryEntry entry) =>
        $"The run '{VfxCode.SanitiseForEcho(entry.RunId)}' did not cover the suite "
        + $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(specPath)}'. It covered "
        + $"{entry.SpecPaths.Count} suite(s); call get_run_status with the same runId to see their "
        + "paths, and pass one of those. A relative path is resolved against the workspace root before "
        + "it is compared.";

    private static string DescribeStepNotInRun(string stepId, RunRegistryEntry entry, SuiteRunSummary summary) =>
        $"The run '{VfxCode.SanitiseForEcho(entry.RunId)}' recorded no step with id "
        + $"'{VfxCode.SanitiseForEcho(stepId)}' — neither an attempt nor a completion event names it. "
        + $"Its event stream recorded {summary.Steps.Count} completed step(s); call explain_run for a "
        + "diagnosis naming them, or get_run_events with types ['step-completed'] for the raw list. A "
        + "step id is matched exactly, and a step whose suite failed pre-flight validation never ran and "
        + "so never appears here at all.";

    /// <summary>
    /// Builds the timeline, guaranteeing the payload's serialised size stays within
    /// <see cref="EffectiveTimelineBudgetBytes"/> regardless of how many attempts the stream held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tries <see cref="ObservedCharTiers"/> in order — a FIXED, bounded number of attempts, never an
    /// open-ended trim-and-recheck loop — serialising a candidate at each and returning the first that
    /// fits. <b>Every tier keeps every attempt</b>; only the evidence text differs. That is the whole
    /// inversion this tool exists for (see this type's remarks), and it is why the tiers here are a
    /// single number rather than <c>explain_run</c>'s four-tuple.
    /// </para>
    /// <para>
    /// Only if the minimal tier — no <c>observed</c> text at all — STILL does not fit is the attempt
    /// list itself shortened, by <see cref="FitAttemptList"/>, and the result then says so twice
    /// (<see cref="GetStepTimelineResult.Truncated"/> and
    /// <see cref="GetStepTimelineResult.OmittedAttemptCount"/>). Every candidate on that path is
    /// MEASURED too, on the same "expected to fit is not verified to fit" principle
    /// <c>ExplainRunOrchestrator.BuildDiagnosis</c> records, and the final fallback is a shape with no
    /// per-attempt collection at all — a handful of individually capped scalars.
    /// </para>
    /// </remarks>
    private static GetStepTimelineResult BuildTimeline(
        string specPath,
        string stepId,
        IReadOnlyList<StepAttempt> attempts,
        StepOutcome? step,
        RunRegistryEntry entry,
        bool eventsTruncated)
    {
        var displaySpecPath = PathSafetyGuard.CapAndSanitisePathForDisplay(specPath);
        var attributed = entry.SpecPaths.Count == 1;

        GetStepTimelineResult Build(int observedChars, int maxAttempts) => BuildAtTier(
            displaySpecPath, stepId, attempts, step, attributed, eventsTruncated, observedChars, maxAttempts);

        // No probe ever serialises more attempts than could conceivably fit — see
        // MaxFittableAttempts for the measured cost of the version that did. Below the cap this is
        // attempts.Count and nothing about the tier walk changes; above it, the probes are bounded
        // and FitAttemptList (which clamps to the SAME figure) is reached by construction, because a
        // list longer than the cap is over budget even with no evidence text at all.
        var probeAttemptCount = Math.Min(attempts.Count, MaxFittableAttempts + 1);

        var minimalTierBytes = 0;
        foreach (var observedChars in ObservedCharTiers)
        {
            var candidate = Build(observedChars, probeAttemptCount);
            minimalTierBytes = SerialisedByteCount(candidate);
            if (minimalTierBytes <= EffectiveTimelineBudgetBytes)
            {
                return candidate;
            }
        }

        // Every tier's evidence text is already gone and the payload is still over budget, so the LIST
        // is what gives — the only path in this type that shortens it.
        return FitAttemptList(Build, probeAttemptCount, minimalTierBytes);
    }

    /// <summary>
    /// Finds a list length that fits <see cref="EffectiveTimelineBudgetBytes"/> at the minimal tier, in
    /// at most <see cref="MaxFitProbes"/> measured candidates — see that constant for why this is
    /// measured rather than divided out of a worst-case constant.
    /// </summary>
    /// <param name="build">Builds a candidate for a given observed-text cap and list length.</param>
    /// <param name="attemptCount">
    /// How many attempts the tier walk PROBED — <c>Math.Min(streamAttempts, MaxFittableAttempts + 1)</c>,
    /// not necessarily how many the stream held. It must be the probed figure, because
    /// <paramref name="minimalTierBytes"/> was measured over exactly that many: dividing that size by
    /// a larger count would understate the per-attempt cost and start the search at a length already
    /// known to be unfittable.
    /// </param>
    /// <param name="minimalTierBytes">The already-measured size of that probed list at the minimal tier.</param>
    private static GetStepTimelineResult FitAttemptList(
        Func<int, int, GetStepTimelineResult> build, int attemptCount, int minimalTierBytes)
    {
        // The shell is what a zero-attempt payload costs; every attempt above it is, on average,
        // (minimalTierBytes - shellBytes) / attemptCount. That average is the first guess and it is
        // close, because at the minimal tier the only per-attempt field that varies in length is the
        // capped `error` sentence.
        var shell = build(0, 0);
        var shellBytes = SerialisedByteCount(shell);
        var perAttemptBytes = Math.Max(1, (minimalTierBytes - shellBytes) / Math.Max(1, attemptCount));
        var keep = (int)Math.Clamp(
            (EffectiveTimelineBudgetBytes - (long)shellBytes) / perAttemptBytes, 0, attemptCount);

        for (var probe = 0; probe < MaxFitProbes && keep > 0; probe++)
        {
            var candidate = build(0, keep);
            var candidateBytes = SerialisedByteCount(candidate);
            if (candidateBytes <= EffectiveTimelineBudgetBytes)
            {
                return candidate;
            }

            // RE-ESTIMATED from this candidate's own measurement, not halved. Halving was the
            // original shape and it threw away half a timeline on a near miss: a candidate 0.4% over
            // budget was answered by returning half as many attempts. It only looked harmless while
            // the FIRST guess came from an average computed over the whole (much longer) attempt
            // list, which biased that guess low enough to fit on the first probe; once the tier walk
            // was bounded to MaxFittableAttempts + 1 the average got sharper, the first guess started
            // landing just above the budget, and the halving turned a two-attempt overshoot into a
            // 50% loss (measured: 469 attempts became 238).
            //
            // Scaling by the measured ratio lands within a percent or two instead. The clamp is what
            // keeps the loop honest in both directions: never above keep - 1, so it always makes
            // progress and cannot cycle; never below keep / 2, so a wildly wrong measurement cannot
            // collapse the list further than the old halving would have.
            var rescaled = (int)((long)keep * EffectiveTimelineBudgetBytes / candidateBytes);
            keep = Math.Clamp(rescaled, keep / 2, keep - 1);
        }

        // Nothing fitted: return the attempt-free shape, whose own size is bounded by the individual
        // field caps this type applies (the capped specPath and stepId echoes plus the capped
        // conclusion) rather than by anything the events file controls.
        return shell;
    }

    private static int SerialisedByteCount(GetStepTimelineResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    private static GetStepTimelineResult BuildAtTier(
        string displaySpecPath,
        string stepId,
        IReadOnlyList<StepAttempt> attempts,
        StepOutcome? step,
        bool attributed,
        bool eventsTruncated,
        int observedChars,
        int maxAttempts)
    {
        var observedCapped = false;
        var projected = new List<StepTimelineAttempt>(Math.Min(maxAttempts, attempts.Count));

        for (var i = 0; i < attempts.Count && i < maxAttempts; i++)
        {
            var attempt = attempts[i];
            var observed = CapText(attempt.Observation, observedChars);
            observedCapped |= attempt.Observation is not null
                && (observed is null || observed.Length < attempt.Observation.Length);

            var (outcome, error) = MapAttemptOutcome(attempt);

            projected.Add(new StepTimelineAttempt(
                N: attempt.Attempt,
                At: CapText(attempt.At, MaxAtChars),
                DelayMs: null,
                TMs: attempt.TMs,
                Outcome: outcome,
                Observed: observed,
                Error: CapText(error, MaxErrorChars)));
        }

        var omitted = Math.Max(0, attempts.Count - projected.Count);

        return new GetStepTimelineResult(
            SpecPath: displaySpecPath,
            StepId: stepId,
            VerifyMode: DeriveVerifyMode(attempts.Count),
            TimeoutMs: null,
            Attempts: projected,
            Conclusion: CapText(
                BuildConclusion(stepId, attempts, step, attributed, omitted), MaxConclusionChars)!,
            Truncated: eventsTruncated || omitted > 0,
            OmittedAttemptCount: omitted,
            ObservedCapped: observedCapped,
            SpecPathAttributed: attributed);
    }

    /// <summary>
    /// Maps ONE parsed attempt onto spec §5.10's three-value <see cref="StepAttemptOutcome"/>, and the
    /// accompanying <see cref="StepTimelineAttempt.Error"/> when the mapping lands on
    /// <see cref="StepAttemptOutcome.Error"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The single mapping site between the engine's verdict vocabulary and this tool's own</b> — see
    /// <c>GetStepTimelineModels.cs</c>'s header for the three vocabularies and why they must not be
    /// conflated. Each fold is a judgement and each is recorded:
    /// <list type="bullet">
    /// <item><description>
    /// <c>PASS</c> ⇒ <c>matched</c>. The attempt's assertion held: this poll found what the step was
    /// waiting for. That is exactly what "matched" names.
    /// </description></item>
    /// <item><description>
    /// <c>FAIL</c> ⇒ <c>unmatched</c>, and this is the fold most worth stating. A failing ATTEMPT under
    /// <c>verifyMode: RETRY</c> is not a failing step — it is the ordinary state of every poll before
    /// the last one, and the engine keeps going. Reporting it as anything failure-shaped would import
    /// the run-level taxonomy into a place where it means something different, which is precisely the
    /// conflation sprint-00-overview.md §5 treats as a story defect.
    /// </description></item>
    /// <item><description>
    /// <c>ENV_ERROR</c> ⇒ <c>error</c>. Infrastructure prevented the assertion from being evaluated, so
    /// there is no match/no-match determination to report. Note the asymmetry with the four-way
    /// taxonomy, which keeps EnvironmentError strictly apart from Fail: here BOTH indeterminate cases
    /// land on one token because §5.10's enum offers no third. The distinction is not lost — it is
    /// carried in the accompanying <c>error</c> text, which names what was read.
    /// </description></item>
    /// <item><description>
    /// <c>INCONCLUSIVE</c> ⇒ <c>error</c>. The engine could not determine correctness for this attempt,
    /// which is "no determination" rather than "determined not to match". Folding it to
    /// <c>unmatched</c> would assert the poll looked and did not find, which is a stronger claim than
    /// the stream supports.
    /// </description></item>
    /// <item><description>
    /// <b>No <c>outcome</c> property at all</b> ⇒ <c>error</c>. This is a real state (a mid-RETRY poll
    /// the stream records without a resolved outcome, or a run whose stream ends mid-step), and there
    /// is no honest way to call it matched or unmatched. The <c>error</c> text says exactly that.
    /// </description></item>
    /// <item><description>
    /// <b>An <c>outcome</c> token this build does not recognise</b> ⇒ <c>error</c>, with the token
    /// echoed verbatim in the <c>error</c> text. The v1 event contract is additive-frozen, so a token
    /// from a newer engine is a supported state rather than corruption; guessing which of the three it
    /// resembles would be a fabrication, and dropping the attempt would put a hole in the timeline this
    /// tool exists to deliver whole.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// A per-attempt <c>error</c> the EVENT itself carried always wins over this server's own sentence:
    /// the engine's account of why its own attempt failed is better evidence than this server's account
    /// of what it read. That field is measured absent at the pinned engine — see
    /// <see cref="StepAttempt.Error"/> — so the composed sentences below are what a host sees today.
    /// </para>
    /// <para>
    /// <b>An engine-supplied <c>error</c> on a <c>matched</c> or <c>unmatched</c> attempt is
    /// deliberately DROPPED</b>, not relayed: the two early returns above pass
    /// <see langword="null"/> unconditionally. That is consistent with the field's own contract —
    /// <see cref="StepTimelineAttempt.Error"/> is documented as populated "for exactly the attempts
    /// whose outcome is error and omitted for every other", and it reads as "why this attempt reached
    /// no determination", which an attempt that DID reach one has no answer to. A future engine that
    /// attached explanatory text to a decided attempt would need a differently-named field rather than
    /// a widening of this one, or a host would have to guess whether <c>error</c> means "undecided" or
    /// "decided, with a note".
    /// </para>
    /// </remarks>
    internal static (string Outcome, string? Error) MapAttemptOutcome(StepAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (string.Equals(attempt.Outcome, nameof(RunVerdict.Pass), StringComparison.Ordinal))
        {
            return (StepAttemptOutcome.Matched, null);
        }

        if (string.Equals(attempt.Outcome, nameof(RunVerdict.Fail), StringComparison.Ordinal))
        {
            return (StepAttemptOutcome.Unmatched, null);
        }

        // Everything below is an "error" outcome; only the explanation differs.
        var explanation = attempt.Outcome switch
        {
            nameof(RunVerdict.EnvironmentError) =>
                "The engine reported an environment error for this attempt: infrastructure prevented "
                + "the step's assertion from being evaluated, so the attempt neither matched nor failed "
                + "to match.",
            nameof(RunVerdict.Inconclusive) =>
                "The engine could not determine a result for this attempt, so it neither matched nor "
                + "failed to match.",
            _ when attempt.RawOutcome is null =>
                "The engine recorded no outcome for this attempt — it was still in flight, or the event "
                + "stream ends before its result was written.",
            _ =>
                $"The engine reported an outcome this server does not recognise for this attempt: "
                + $"'{VfxCode.SanitiseForEcho(attempt.RawOutcome)}'. The event contract is additive, so "
                + "a newer engine may report outcomes this build predates; the attempt is kept rather "
                + "than guessed at or dropped.",
        };

        // The engine's own account wins when it has one — see this method's remarks.
        return (StepAttemptOutcome.Error, attempt.Error ?? explanation);
    }

    /// <summary>
    /// <see cref="StepVerifyMode.Retry"/> for more than one recorded attempt,
    /// <see cref="StepVerifyMode.Once"/> for exactly one, and <see langword="null"/> for none — see
    /// <see cref="GetStepTimelineResult.VerifyMode"/> for why this is a statement about what the run
    /// evidenced rather than about what the suite declared.
    /// </summary>
    private static string? DeriveVerifyMode(int attemptCount) => attemptCount switch
    {
        0 => null,
        1 => StepVerifyMode.Once,
        _ => StepVerifyMode.Retry,
    };

    private static string BuildConclusion(
        string stepId,
        IReadOnlyList<StepAttempt> attempts,
        StepOutcome? step,
        bool attributed,
        int omitted)
    {
        var attribution = attributed
            ? string.Empty
            : " This run covered several suites and its event stream carries no per-suite attribution, "
              + "so this is the run-wide timeline for that step id — if two of the run's suites declare "
              + "a step with the same id, their attempts are interleaved here and cannot be separated.";

        var omission = omitted > 0
            ? $" {omitted} further attempt(s) were omitted to keep this response within its size budget."
            : string.Empty;

        if (step is null)
        {
            return attempts.Count == 0
                ? $"Step '{stepId}' has no recorded attempts and no completion event in this run."
                  + attribution
                : $"Step '{stepId}' recorded {attempts.Count} attempt(s) but no completion event, so the "
                  + "run's stream ends without a verdict for it — the run was cut short, or its events "
                  + "file was read only up to this server's size cap." + attribution + omission;
        }

        var attemptClause = attempts.Count switch
        {
            0 => "with no individual attempt events recorded",
            1 => "on its single recorded attempt",
            _ => $"after {attempts.Count} recorded attempts",
        };

        return $"Step '{stepId}' concluded {step.Verdict} {attemptClause} "
               + $"({step.DurationMs}ms total)." + attribution + omission;
    }

    /// <summary>Caps <paramref name="text"/> to <paramref name="maxChars"/>; a cap of zero drops it entirely.</summary>
    private static string? CapText(string? text, int maxChars)
    {
        if (text is null || maxChars <= 0)
        {
            return null;
        }

        return text.Length > maxChars ? text[..maxChars] : text;
    }
}
