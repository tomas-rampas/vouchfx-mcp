using System.Text.Json;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Diagnosis;

/// <summary>
/// REQ-007's <c>explain_run</c> orchestration: resolve an events file (explicit path, or the last
/// run this session — EDGE-004's default), read it, and diagnose it — PURELY read + parse + diagnose,
/// never re-running anything (no CLI spawn, no validation worker, no container).
/// </summary>
/// <remarks>
/// <para>
/// <b>Path resolution and safety mirror <c>run_suite</c>'s own established patterns</b>, reused
/// rather than reinvented: <see cref="PathSafetyGuard.CheckLocalPath"/> rejects a UNC/network path
/// (the same forced-authentication threat <c>validate_suite</c>/<c>run_suite</c> already guard
/// against) before any filesystem call is made against it. Unlike those two tools, LOCAL path
/// traversal is deliberately still allowed here — <c>eventsPath</c> is agent-supplied, and reading an
/// arbitrary local file the caller names is this tool's whole job, exactly like
/// <c>validate_suite</c>'s own documented policy.
/// </para>
/// <para>
/// <b>Bounded read, shared with <c>run_suite</c>:</b> <see cref="EventsFileReader"/> caps the read at
/// <see cref="EventsFileReader.MaxEventsFileBytes"/> (50&#160;MB) — the SAME helper
/// <see cref="RunSuiteOrchestrator"/> uses, extracted specifically so both consumers of the
/// events-file contract share one bounded-read implementation. <see cref="SuiteEventParser"/> — also
/// shared, unmodified in its tolerance — parses whatever was read leniently: an unknown event TYPE or
/// an unknown FIELD (EDGE-004, the v1 event contract's additive-frozen guarantee) is simply skipped,
/// never treated as an error.
/// </para>
/// <para>
/// <b>The 64&#160;KB response cap</b> (<see cref="MaxDiagnosisResponseBytes"/>) is enforced by
/// <see cref="BuildDiagnosis"/> via three FIXED, DETERMINISTIC tiers of decreasing detail (rich,
/// compact, minimal), each MEASURED by actually serialising it and checking its real byte count —
/// never assumed to fit "by construction" — with a final hard-truncating fallback if even the
/// minimal tier's measured size somehow still exceeds the cap. See that method's remarks for the
/// full reasoning, including why every per-item field this type embeds (including <c>stepId</c>,
/// after a review found it was the one caller-influenced field that had been left uncapped) is
/// bounded at parse time in <see cref="SuiteEventParser"/>.
/// </para>
/// <para>
/// <b>No verdict computed here is ever "authoritative" in the way <c>run_suite</c>'s is</b> — this
/// tool has no CLI exit code to cross-check against (it never spawned anything), so
/// <see cref="ComputeEffectiveVerdict"/> is purely a best-effort read of whatever the events file
/// itself recorded: the SAME §12.1 elevation <see cref="RunVerdictExtensions.Elevate"/> already
/// implements, applied first to <c>scenario-completed</c> events (as <c>run_suite</c> does) and, only
/// if none exist, as a SECOND fallback to whatever individual STEP verdicts were recorded. Only if
/// NEITHER yields anything — and there is no <c>environment-error</c> event either — is the file
/// treated as containing nothing recognisable at all.
/// </para>
/// </remarks>
public sealed class ExplainRunOrchestrator
{
    /// <summary>
    /// The INTENDED cap on the <c>explain_run</c> response's serialised size (UTF-8 JSON bytes) —
    /// chosen so a diagnosis is always a small, fast, agent-friendly payload regardless of how large
    /// the SOURCE events file was (which can be up to
    /// <see cref="EventsFileReader.MaxEventsFileBytes"/>, 50&#160;MB). Read
    /// <see cref="EffectiveDiagnosisBudgetBytes"/>'s remarks before treating this number as a
    /// guarantee about the wire: it is not one today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This constant does NOT bound the real wire envelope, and has never done so</b> (a review
    /// fix that replaced an earlier, unmeasured claim that it did). <see cref="BuildDiagnosis"/>
    /// budgets each candidate against <see cref="EffectiveDiagnosisBudgetBytes"/> — half of this
    /// constant — on the theory that <c>StructuredToolResult.Success</c> carries the same payload
    /// TWICE (a text <c>Content</c> block plus <c>StructuredContent</c>), so halving covers the
    /// doubling. MEASURED, the real multiplier is not 2. It is <b>2.213</b>.
    /// </para>
    /// <para>
    /// <b>The measurement</b> (taken against the largest input the tiers still accept at tier 0: ten
    /// failing steps, ten attempts each, 450-character step observations and 190-character attempt
    /// observations — pinned as an executable regression by
    /// <c>ExplainRunOrchestratorTests.MaximalTierZeroDiagnosis_FitsTheBudgetButItsEnvelopeExceedsTheCap</c>):
    /// <list type="bullet">
    /// <item><description>bare <see cref="Diagnosis"/>: <b>32,229&#160;B</b> — under
    /// <see cref="EffectiveDiagnosisBudgetBytes"/> (32,768), so the tiering ACCEPTS it at tier 0 and
    /// reports no truncation;</description></item>
    /// <item><description>full <c>CallToolResult</c> envelope: <b>71,335&#160;B</b> — over this
    /// constant (65,536) by <b>5,799&#160;B</b>.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>The cause is the text copy's ESCAPING, and it is PRE-EXISTING</b> — not US-S1-02's
    /// <c>meta</c> stamp, and not new. The <c>Content</c> block carries the payload as a JSON
    /// <i>string</i>, so every <c>"</c> and <c>\</c> in it is re-escaped and the whole thing is
    /// quoted: that copy costs materially more than the <c>StructuredContent</c> copy of the same
    /// bytes, and the <c>CallToolResult</c> wrapper adds its own fields on top. Measured on the same
    /// input, the envelope WITHOUT any <c>meta</c> is <b>70,951&#160;B</b> — already 5,415&#160;B
    /// over the cap. <c>meta</c> contributes the remaining <b>384&#160;B</b>: <b>6.6%</b> of the
    /// overage, and it did not create the breach.
    /// </para>
    /// <para>
    /// <b>Not fixed here, deliberately.</b> US-S1-02's remit was to record a measured baseline, and
    /// shaving a tier constant would only move the discontinuity (the tiers fall from ~32&#160;KB to
    /// ~6.8&#160;KB in one step, so trimming tier 0 costs a great deal of evidence to buy a little
    /// headroom). Sprint 4 owns the re-budget. The sanctioned answer there is a <c>resourceUri</c>
    /// hand-off — return the large evidence as an MCP resource the host fetches on demand, so the
    /// inline response shrinks — and explicitly NOT raising this cap, which would export the cost to
    /// every host's context window.
    /// </para>
    /// <para>
    /// Absolute byte counts above were measured on one machine and move with <c>workspaceRoot</c>'s
    /// own length (it is part of <c>meta</c>); the RATIO, the ordering, and the direction of the
    /// breach do not, which is why the regression test asserts those rather than the literals.
    /// </para>
    /// </remarks>
    public const int MaxDiagnosisResponseBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The BARE <see cref="Diagnosis"/>'s own budget <see cref="BuildDiagnosis"/> actually measures
    /// against — half of <see cref="MaxDiagnosisResponseBytes"/>, since
    /// <c>StructuredToolResult.Success</c> serialises the diagnosis twice into the real wire envelope
    /// (see <see cref="MaxDiagnosisResponseBytes"/>'s own remarks). <see langword="internal"/> (not
    /// private) purely so tests can assert against the PRECISE value this type actually enforces,
    /// rather than only the looser, doubled public constant.
    /// </summary>
    internal const int EffectiveDiagnosisBudgetBytes = MaxDiagnosisResponseBytes / 2;

    /// <summary>
    /// Maximum characters of an agent-supplied <c>eventsPath</c> ever embedded in a response —
    /// applied BEFORE sanitising, and again AFTER (see <see cref="CapAndSanitisePathForDisplay"/>).
    /// A review found the error branches in <see cref="ExplainAsync"/> (missing/unreadable/
    /// invalid-path) echoed the FULL caller-supplied path with no length cap at all: an
    /// implausibly long path would yield an oversized tool ERROR response — undermining the
    /// 64&#160;KB envelope cap the SUCCESS path (<see cref="BuildDiagnosis"/>) already carefully
    /// enforces via its own measure-and-truncate tiers — while also doing unbounded sanitisation
    /// work over a value that, by definition, is never going to resolve to a real file anyway once
    /// it is this long.
    /// </summary>
    private const int MaxDisplayedPathChars = 1_000;

    /// <summary>
    /// The three fixed detail tiers <see cref="BuildDiagnosis"/> tries in order, each strictly
    /// smaller than the last: (max notable steps shown, max chars of a step's own observation, max
    /// attempts shown per step, max chars of a single attempt's own observation). The final
    /// (minimal) tier carries NO observation or attempt-timeline text at all — see
    /// <see cref="BuildDiagnosis"/>'s remarks for why that makes it a guaranteed floor.
    /// </summary>
    private static readonly (int MaxNotableSteps, int MaxStepObservationChars, int MaxAttempts, int MaxAttemptObservationChars)[] Tiers =
    [
        (10, 2000, 10, 500),
        (5, 300, 5, 100),
        (3, 0, 0, 0),
    ];

    private readonly ILastRunTracker _lastRunTracker;

    public ExplainRunOrchestrator(ILastRunTracker lastRunTracker)
    {
        ArgumentNullException.ThrowIfNull(lastRunTracker);
        _lastRunTracker = lastRunTracker;
    }

    /// <summary>Resolves, reads, and diagnoses an events file — see this type's remarks for the full gate ordering.</summary>
    /// <param name="eventsPath">
    /// Path to the events file to diagnose. <see langword="null"/> or whitespace-only defaults to the
    /// last run this session (via <see cref="ILastRunTracker"/>) — EDGE-004's documented default.
    /// </param>
    public async Task<ExplainRunOutcome> ExplainAsync(string? eventsPath, CancellationToken cancellationToken)
    {
        string resolvedPath;
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            var lastRun = _lastRunTracker.LastRun;
            if (lastRun is null)
            {
                return new ExplainRunOutcome.NoRunToExplain(
                    "No run to explain this session. Provide eventsPath, or run a suite with " +
                    "run_suite first.");
            }

            resolvedPath = lastRun.EventsFilePath;
        }
        else
        {
            resolvedPath = eventsPath;
        }

        // Capped THEN sanitised (mirroring SuiteEventParser's own cap-before-sanitise ordering) —
        // built ONCE and reused for every response below that needs to display the path, INCLUDING
        // the error branches (a review fix: they previously echoed the FULL, uncapped path).
        // resolvedPath itself stays the RAW, uncapped value right up to the two filesystem calls
        // further down, which need the genuine path, not a display-truncated one.
        var displayPath = CapAndSanitisePathForDisplay(resolvedPath);

        var pathError = PathSafetyGuard.CheckLocalPath(resolvedPath);
        if (pathError is not null)
        {
            // Built here, not by reusing pathError.Message verbatim: that message is constructed
            // from the RAW, uncapped path internally (PathSafetyGuard is shared with run_suite/
            // validate_suite and does not itself apply a display cap), which would reintroduce
            // exactly the oversized-response risk this fix exists to close.
            return new ExplainRunOutcome.InvalidPath(
                $"Path must be a local file path, not a network/UNC location: '{displayPath}'.");
        }

        if (!File.Exists(resolvedPath))
        {
            return new ExplainRunOutcome.EventsFileNotFound($"Events file not found: '{displayPath}'.");
        }

        var (content, truncated) = await EventsFileReader.TryReadBoundedAsync(resolvedPath, cancellationToken);
        if (content is null)
        {
            return new ExplainRunOutcome.EventsFileUnreadable($"The events file could not be read: '{displayPath}'.");
        }

        var summary = SuiteEventParser.Parse(content);

        var effectiveVerdict = ComputeEffectiveVerdict(summary);
        if (effectiveVerdict is null)
        {
            return new ExplainRunOutcome.NoRecognisableEvents(
                "The events file contains no recognisable vouchfx events (empty, or entirely " +
                "unparseable content).");
        }

        var diagnosis = BuildDiagnosis(summary, effectiveVerdict.Value, displayPath, truncated);
        return new ExplainRunOutcome.Diagnosed(diagnosis);
    }

    /// <summary>Caps <paramref name="path"/> to <see cref="MaxDisplayedPathChars"/> BEFORE sanitising, then AGAIN afterwards.</summary>
    /// <remarks>
    /// Two-stage cap, mirroring <c>CliPinVerifier</c>'s identical rationale: capping the RAW text
    /// first keeps sanitisation itself cheap regardless of how long an agent-supplied path is;
    /// sanitising can EXPAND length (each non-printable character becomes a 6-character
    /// <c>\uXXXX</c> escape), so a SECOND cap applied to the ALREADY-sanitised result is what
    /// actually bounds what ends up in the response — the first cap alone would not.
    /// </remarks>
    private static string CapAndSanitisePathForDisplay(string path)
    {
        var rawCapped = path.Length > MaxDisplayedPathChars ? path[..MaxDisplayedPathChars] : path;
        var sanitised = TextSanitiser.SanitiseForDisplay(rawCapped);
        return sanitised.Length > MaxDisplayedPathChars ? sanitised[..MaxDisplayedPathChars] : sanitised;
    }

    /// <summary>
    /// The SAME §12.1 elevation logic <c>run_suite</c> uses over <c>scenario-completed</c> events,
    /// with a further best-effort fallback this tool needs precisely because — unlike
    /// <c>run_suite</c> — it has no CLI exit code to fall back to: elevating over the individual
    /// STEP verdicts <see cref="SuiteRunSummary.Steps"/> recorded, AND ALWAYS ALSO folding in
    /// <see cref="RunVerdict.EnvironmentError"/> when at least one <c>environment-error</c> event
    /// exists — never early-returning the step-derived verdict without consulting it first (see
    /// this method's remarks on why that ordering matters). <see langword="null"/> only when
    /// NEITHER source yielded anything at all.
    /// </summary>
    /// <remarks>
    /// A review found an EARLIER version of this method returned the step-elevated verdict
    /// immediately, before ever looking at <see cref="SuiteRunSummary.EnvironmentErrors"/> — so a
    /// perfectly realistic abort sequence (a step fails, then the container running it dies,
    /// aborting the run before <c>scenario-completed</c> is ever emitted) was misclassified as
    /// <c>Fail</c> even though an <c>environment-error</c> event was RIGHT THERE in the same file.
    /// §12.1's precedence ranks <c>EnvironmentError</c> ABOVE <c>Fail</c> specifically so this kind
    /// of abort is never reported as a product defect — conflating the two is the exact failure
    /// mode the whole four-outcome taxonomy exists to prevent. Fixed by ALWAYS elevating the
    /// step-derived result (if any) with <see cref="RunVerdict.EnvironmentError"/> when at least one
    /// environment-error event is present, using the SAME <see cref="RunVerdictExtensions.Elevate"/>
    /// precedence rather than a bespoke ordering.
    /// </remarks>
    private static RunVerdict? ComputeEffectiveVerdict(SuiteRunSummary summary)
    {
        if (summary.AggregateVerdict is { } aggregate)
        {
            return aggregate;
        }

        RunVerdict? fromSteps = null;
        foreach (var step in summary.Steps)
        {
            if (Enum.TryParse<RunVerdict>(step.Verdict, out var stepVerdict))
            {
                fromSteps = fromSteps is { } current ? RunVerdictExtensions.Elevate(current, stepVerdict) : stepVerdict;
            }
        }

        if (summary.EnvironmentErrors.Count > 0)
        {
            return fromSteps is { } stepsResult
                ? RunVerdictExtensions.Elevate(stepsResult, RunVerdict.EnvironmentError)
                : RunVerdict.EnvironmentError;
        }

        return fromSteps;
    }

    /// <summary>
    /// Builds the diagnosis, guaranteeing the FULL wire envelope's serialised size stays within
    /// <see cref="MaxDiagnosisResponseBytes"/> regardless of how much source data
    /// <paramref name="summary"/> holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tries <see cref="Tiers"/> in order — rich, then compact, then minimal — serialising a
    /// CANDIDATE diagnosis at each tier and returning the first one that fits. This is a bounded,
    /// FIXED number of attempts, never an open-ended trim-and-recheck loop.
    /// </para>
    /// <para>
    /// <b>Measured against <see cref="EffectiveDiagnosisBudgetBytes"/> (HALF of
    /// <see cref="MaxDiagnosisResponseBytes"/>), not the full constant</b>:
    /// <c>StructuredToolResult.Success</c> serialises the bare <see cref="Diagnosis"/> TWICE into the
    /// real wire envelope (a text <c>Content</c> block plus <c>StructuredContent</c>), so a diagnosis
    /// tuned to fit just under the full 64&#160;KB would make the REAL response at least double that.
    /// Halving is therefore a large and necessary correction — but, MEASURED, it is not a sufficient
    /// one, and the earlier claim here that "budgeting each candidate against half the constant is
    /// what makes that claim true of the FULL response" was false. The real envelope-to-bare
    /// multiplier is <b>2.213</b>, not 2, because the text copy is an ESCAPED JSON string rather than
    /// a second verbatim copy; a diagnosis that exactly fills this halved budget produces a
    /// <b>71,335&#160;B</b> envelope against a 65,536&#160;B cap. See
    /// <see cref="MaxDiagnosisResponseBytes"/>'s remarks for the full measurement, why the breach
    /// predates US-S1-02's <c>meta</c> stamp (which is 6.6% of it), and why Sprint 4 rather than this
    /// method is the right place to close it.
    /// </para>
    /// <para>
    /// <b>The minimal tier's own size is ACTUALLY MEASURED, never merely assumed.</b> A review
    /// correctly rejected an earlier version's claim that the minimal tier was "a guaranteed floor
    /// by construction" as unverified — every per-item field this type embeds IS now capped at parse
    /// time (<c>SuiteEventParser.MaxLabelCharsAtParse</c>/<c>MaxObservationCharsAtParse</c>, which as
    /// of a review fix also covers <c>stepId</c>) and every per-tier LIST is capped by
    /// <see cref="BuildDiagnosisAtTier"/>, so the minimal tier is EXPECTED to fit — but "expected to"
    /// is not the same guarantee as "verified to", and a future field added to <see cref="Diagnosis"/>
    /// without a matching cap would silently break that expectation. This method therefore measures
    /// the minimal tier's serialised size exactly like every earlier tier, and if it STILL exceeds
    /// the (halved) budget for any reason this method's own author did not anticipate, falls through
    /// to <see cref="BuildEmergencyMinimalDiagnosis"/> — a shape with NO per-item collections at all,
    /// only fixed-length scalar fields, whose own worst-case size is small enough (roughly 2&#160;KB
    /// — see that method's remarks) to verify by simple arithmetic rather than by further
    /// measure-and-fall-back layers, and comfortably fits even the halved budget.
    /// </para>
    /// </remarks>
    private static Diagnosis BuildDiagnosis(SuiteRunSummary summary, RunVerdict verdict, string eventsFilePath, bool eventsTruncated)
    {
        Diagnosis candidate = BuildDiagnosisAtTier(summary, verdict, eventsFilePath, eventsTruncated, Tiers[0], responseTruncated: false);

        for (var tierIndex = 1; tierIndex < Tiers.Length; tierIndex++)
        {
            if (SerialisedByteCount(candidate) <= EffectiveDiagnosisBudgetBytes)
            {
                return candidate;
            }

            candidate = BuildDiagnosisAtTier(summary, verdict, eventsFilePath, eventsTruncated, Tiers[tierIndex], responseTruncated: true);
        }

        // The final (minimal) tier's size is CHECKED here too — the real backstop the earlier
        // "guaranteed by construction" comment claimed to be but never actually verified.
        if (SerialisedByteCount(candidate) <= EffectiveDiagnosisBudgetBytes)
        {
            return candidate;
        }

        return BuildEmergencyMinimalDiagnosis(candidate, verdict);
    }

    /// <summary>
    /// The genuine last resort, reached only if even the minimal tier's MEASURED size exceeded
    /// <see cref="EffectiveDiagnosisBudgetBytes"/> — hard-truncates to a shape with NO per-item
    /// collections at all: <see cref="Diagnosis.NotableSteps"/> and
    /// <see cref="Diagnosis.EnvironmentErrors"/> are both emptied, <see cref="Diagnosis.Summary"/>
    /// becomes a short fixed literal, and <see cref="Diagnosis.EventsFilePath"/> is capped to
    /// <see cref="MaxEmergencyPathChars"/> characters. Every remaining field is either a small
    /// enum-derived fixed string (<see cref="Diagnosis.Verdict"/>, <see cref="Diagnosis.CategoryMeaning"/>)
    /// or a plain integer count — none of which can grow with how much source data the events file
    /// held — so this shape's worst-case serialised size (a few KB at most) can be verified by
    /// simple arithmetic, not by yet another measure-and-fall-back layer.
    /// </summary>
    private static Diagnosis BuildEmergencyMinimalDiagnosis(Diagnosis oversized, RunVerdict verdict)
    {
        var path = oversized.EventsFilePath.Length > MaxEmergencyPathChars
            ? oversized.EventsFilePath[..MaxEmergencyPathChars]
            : oversized.EventsFilePath;

        return new Diagnosis(
            Verdict: verdict.ToString(),
            CategoryMeaning: CategoryMeaning(verdict),
            Summary: "The diagnosis was too large to include full detail even at the most compact " +
                     "tier; see the events file directly for the full breakdown.",
            TotalStepCount: oversized.TotalStepCount,
            PassedStepCount: oversized.PassedStepCount,
            NotableSteps: [],
            OmittedNotableStepCount: oversized.TotalStepCount - oversized.PassedStepCount,
            EnvironmentErrors: [],
            OmittedEnvironmentErrorCount: oversized.EnvironmentErrors.Count + oversized.OmittedEnvironmentErrorCount,
            EventsFilePath: path,
            EventsTruncated: oversized.EventsTruncated,
            ResponseTruncated: true);
    }

    /// <summary>Cap applied to <see cref="Diagnosis.EventsFilePath"/> in <see cref="BuildEmergencyMinimalDiagnosis"/>'s last-resort shape.</summary>
    private const int MaxEmergencyPathChars = 1_000;

    private static int SerialisedByteCount(Diagnosis diagnosis) =>
        JsonSerializer.SerializeToUtf8Bytes(diagnosis, SizeProbeOptions).Length;

    private static Diagnosis BuildDiagnosisAtTier(
        SuiteRunSummary summary,
        RunVerdict verdict,
        string eventsFilePath,
        bool eventsTruncated,
        (int MaxNotableSteps, int MaxStepObservationChars, int MaxAttempts, int MaxAttemptObservationChars) tier,
        bool responseTruncated)
    {
        var passedStepCount = summary.Steps.Count(s => s.Verdict == nameof(RunVerdict.Pass));
        var notableStepOutcomes = summary.Steps.Where(s => s.Verdict != nameof(RunVerdict.Pass)).ToList();
        var omittedNotableStepCount = Math.Max(0, notableStepOutcomes.Count - tier.MaxNotableSteps);

        var notableSteps = notableStepOutcomes
            .Take(tier.MaxNotableSteps)
            .Select(step => BuildStepDiagnosis(step, summary, tier))
            .ToList();

        // Capped by the SAME per-tier count as notable steps: an environment-error event is a
        // similar-scale "diagnostic record", and — like NotableSteps — a pathological events file
        // could otherwise carry an unbounded NUMBER of them even though each individual field is
        // already bounded at parse time (SuiteEventParser.MaxLabelCharsAtParse). Without this cap,
        // BuildDiagnosis's "the minimal tier is a guaranteed floor" claim would not actually hold.
        var omittedEnvironmentErrorCount = Math.Max(0, summary.EnvironmentErrors.Count - tier.MaxNotableSteps);
        var environmentErrors = summary.EnvironmentErrors
            .Take(tier.MaxNotableSteps)
            .Select(e => new EnvironmentErrorDiagnosis(e.ErrorKind, e.ResourceName, e.Detail))
            .ToList();

        // BuildSummary is handed the ALREADY-CAPPED lists (never the raw, potentially unbounded
        // notableStepOutcomes/summary.EnvironmentErrors) together with the omitted counts — a
        // summary joining an unbounded number of step ids would silently defeat the whole
        // response-size guarantee this method exists to provide, even at the minimal tier.
        return new Diagnosis(
            Verdict: verdict.ToString(),
            CategoryMeaning: CategoryMeaning(verdict),
            Summary: BuildSummary(
                verdict, summary.Steps.Count, passedStepCount,
                notableStepOutcomes.Take(tier.MaxNotableSteps).ToList(), omittedNotableStepCount,
                environmentErrors, omittedEnvironmentErrorCount),
            TotalStepCount: summary.Steps.Count,
            PassedStepCount: passedStepCount,
            NotableSteps: notableSteps,
            OmittedNotableStepCount: omittedNotableStepCount,
            EnvironmentErrors: environmentErrors,
            OmittedEnvironmentErrorCount: omittedEnvironmentErrorCount,
            EventsFilePath: eventsFilePath,
            EventsTruncated: eventsTruncated,
            ResponseTruncated: responseTruncated || omittedNotableStepCount > 0 || omittedEnvironmentErrorCount > 0);
    }

    private static StepDiagnosis BuildStepDiagnosis(
        StepOutcome step,
        SuiteRunSummary summary,
        (int MaxNotableSteps, int MaxStepObservationChars, int MaxAttempts, int MaxAttemptObservationChars) tier)
    {
        var observation = CapText(step.Observation, tier.MaxStepObservationChars);

        var allAttempts = summary.AttemptsByStepId.TryGetValue(step.StepId, out var attempts)
            ? attempts
            : [];
        var omittedAttemptCount = Math.Max(0, allAttempts.Count - tier.MaxAttempts);
        var attemptDiagnoses = allAttempts
            .Take(tier.MaxAttempts)
            .Select(a => new StepAttemptDiagnosis(a.Attempt, a.TMs, a.Outcome, CapText(a.Observation, tier.MaxAttemptObservationChars)))
            .ToList();

        return new StepDiagnosis(
            step.StepId, step.Verdict, step.DurationMs, step.AttemptCount, observation, attemptDiagnoses, omittedAttemptCount);
    }

    private static string? CapText(string? text, int maxChars)
    {
        if (text is null || maxChars <= 0)
        {
            return null;
        }

        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static string CategoryMeaning(RunVerdict verdict) => verdict switch
    {
        RunVerdict.Pass =>
            "Pass: all assertions held — the system under test behaved as expected.",
        RunVerdict.Fail =>
            "Fail: a test assertion did not hold — a genuine defect signal in the system under test.",
        RunVerdict.EnvironmentError =>
            "Environment error: an infrastructure or topology problem prevented the system under " +
            "test from being properly exercised. This is NOT a test defect — no conclusion about " +
            "the system under test's correctness can be drawn from it.",
        RunVerdict.Inconclusive =>
            "Inconclusive: the run could not reach a definitive verdict (a timeout, a partition " +
            "that outlasted its grace period, or an upstream capture that went unmet). Neither a " +
            "pass nor a defect is implied.",
        _ => "Unrecognised verdict.",
    };

    /// <summary>
    /// Builds the readable summary text. <paramref name="notableStepOutcomes"/> and
    /// <paramref name="environmentErrors"/> are the ALREADY tier-capped lists (never the raw,
    /// potentially unbounded source lists) — see this method's only call site — so joining every id
    /// in them stays bounded regardless of how much source data the events file held; the TRUE total
    /// counts (for phrasing like "N of M failed") are reconstructed from the omitted counts instead
    /// of the capped lists' own (possibly smaller) <c>Count</c>.
    /// </summary>
    private static string BuildSummary(
        RunVerdict verdict,
        int totalStepCount,
        int passedStepCount,
        IReadOnlyList<StepOutcome> notableStepOutcomes,
        int omittedNotableStepCount,
        IReadOnlyList<EnvironmentErrorDiagnosis> environmentErrors,
        int omittedEnvironmentErrorCount)
    {
        if (verdict == RunVerdict.Pass)
        {
            return totalStepCount == 0
                ? "The run passed, but recorded no individual step outcomes."
                : $"All {totalStepCount} step(s) passed.";
        }

        var trueNotableCount = notableStepOutcomes.Count + omittedNotableStepCount;
        var stepIds = string.Join(", ", notableStepOutcomes.Select(s => s.StepId));
        var moreStepsSuffix = omittedNotableStepCount > 0 ? $", and {omittedNotableStepCount} more" : string.Empty;

        if (verdict == RunVerdict.EnvironmentError)
        {
            if (environmentErrors.Count > 0)
            {
                var first = environmentErrors[0];
                var detailSuffix = string.IsNullOrWhiteSpace(first.Detail) ? string.Empty : $" ({first.Detail})";
                var moreErrorsSuffix = omittedEnvironmentErrorCount > 0
                    ? $" ({omittedEnvironmentErrorCount} more environment error(s) also occurred)"
                    : string.Empty;
                return $"Environment error on '{first.ResourceName}' ({first.ErrorKind}){detailSuffix} — " +
                       $"no test defect is implied.{moreErrorsSuffix}";
            }

            return trueNotableCount > 0
                ? $"Environment error on step(s): {stepIds}{moreStepsSuffix} — no test defect is implied."
                : "The run ended with an environment error — no test defect is implied.";
        }

        if (verdict == RunVerdict.Inconclusive)
        {
            return trueNotableCount > 0
                ? $"{trueNotableCount} step(s) were inconclusive: {stepIds}{moreStepsSuffix}. See each " +
                  "step's RETRY attempt timeline for what was observed before the run gave up."
                : "The run ended inconclusive (timeout, partition, or an unmet upstream capture).";
        }

        // Fail.
        return trueNotableCount > 0
            ? $"{trueNotableCount} of {totalStepCount} step(s) failed: {stepIds}{moreStepsSuffix}. " +
              $"{passedStepCount} step(s) passed."
            : "The run failed.";
    }
}
