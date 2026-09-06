using System.Collections.Frozen;

// Vouchfx.Mcp.Run is imported for the DOC COMMENTS, not for code: VerdictReason's remarks cref
// VerdictReasonClassifier.ClassifyStep(StepOutcome, SuiteRunSummary), whose parameter types both
// live there. A cref that cannot resolve is silently ignored — GenerateDocumentationFile is off, so
// nothing would have failed the build — which is exactly how the reference broke unnoticed when
// these types moved out of VerdictReasonClassifier.cs (a review finding).
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Diagnosis;

/// <summary>One RETRY attempt within a step's timeline, as REQ-007's diagnosis presents it.</summary>
/// <param name="Attempt">The one-based attempt counter.</param>
/// <param name="TMs">Elapsed wall-clock time for this attempt, in milliseconds.</param>
/// <param name="Outcome">This attempt's own resolved outcome, when the engine reported one; <see langword="null"/> for a mid-RETRY poll with no outcome yet.</param>
/// <param name="Observation">This attempt's own observation/diff evidence, capped for the response budget (see <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/>); <see langword="null"/> when none was carried or this tier omits it.</param>
public sealed record StepAttemptDiagnosis(int Attempt, long TMs, string? Outcome, string? Observation);

/// <summary>
/// One step the diagnosis calls out by name — every step whose OWN verdict is not <c>Pass</c>
/// (REQ-007: "for Fail, name the failing step(s) and the mismatch"; "for Inconclusive, the
/// timed-out step and its RETRY timeline").
/// </summary>
/// <param name="StepId">The step's own identifier.</param>
/// <param name="Verdict">One of <c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — never <c>Pass</c> (a passing step is never "notable").</param>
/// <param name="DurationMs">Total wall-clock duration of all attempts combined, in milliseconds.</param>
/// <param name="AttemptCount">How many attempts this step made in total (may exceed <see cref="Attempts"/>'s own count — see <see cref="OmittedAttemptCount"/>).</param>
/// <param name="Observation">The step's own final observation/diff evidence, capped for the response budget; <see langword="null"/> when none was carried or this tier omits it.</param>
/// <param name="Attempts">The RETRY attempt timeline, in order, capped for the response budget.</param>
/// <param name="OmittedAttemptCount">How many earlier attempts were left out of <see cref="Attempts"/> to fit the response budget; <c>0</c> when none were.</param>
/// <param name="Reason">
/// US-S4-02's structured classification of WHY this step is notable, from US-S4-01's rule table;
/// <see langword="null"/> when the table left the step unclassified (see
/// <see cref="VerdictReason"/>'s own remarks on why that is a fail-closed state rather than a gap).
/// <para>
/// <b>Not trimmed by tier.</b> Unlike <see cref="Observation"/> and <see cref="Attempts"/>, this
/// survives every <see cref="ExplainRunOrchestrator"/> tier including the floor — it is two short,
/// fixed-shape strings, not the bulk evidence the tiers exist to shed — though it IS measured as
/// part of each tier's serialised size like every other field. The one shape that drops it is
/// <c>BuildEmergencyMinimalDiagnosis</c>, which carries no per-item collections at all.
/// </para>
/// </param>
public sealed record StepDiagnosis(
    string StepId,
    string Verdict,
    long DurationMs,
    int AttemptCount,
    string? Observation,
    IReadOnlyList<StepAttemptDiagnosis> Attempts,
    int OmittedAttemptCount = 0,
    VerdictReason? Reason = null);

/// <summary>One <c>environment-error</c> event, as REQ-007's diagnosis presents it — always distinct from a step <c>Fail</c>.</summary>
/// <param name="ErrorKind">The <c>OrchestrationErrorKind</c> name the engine reported (e.g. <c>"ImagePull"</c>, <c>"Provision"</c>).</param>
/// <param name="ResourceName">The Aspire resource name the failure concerns.</param>
/// <param name="Detail">A trimmed summary of the underlying failure; <see langword="null"/> when the engine reported none.</param>
/// <param name="Reason">
/// US-S4-02's structured classification, from US-S4-01's rule table.
/// <para>
/// <b>Always populated for a record this diagnosis actually carries — unlike
/// <see cref="StepDiagnosis.Reason"/>, which may be <see langword="null"/>.</b> The asymmetry is not
/// an inconsistency: <c>VerdictReasonClassifier.ClassifyEnvironmentError</c> never returns
/// <see langword="null"/>, because US-S4-01's own Gherkin requires an unrecognised <c>errorKind</c>
/// to leave <see cref="VerdictReason.Kind"/> unset while its hint "still describes the raw errorKind
/// and detail verbatim". US-S4-02's criterion — "one hint per notable step and per environment-error
/// record THAT THE RULE TABLE CLASSIFIED" — is therefore read as <i>described</i> for this surface,
/// which is the only reading under which the two stories agree: an environment-error record always
/// carries a reason and always contributes its hint to
/// <see cref="Diagnosis.ClassificationHints"/>, even when the kind is null; a step contributes only
/// when the table classified it.
/// <para>
/// <b>Nullable in TYPE but with no DEFAULT, deliberately.</b> The type stays nullable because that is
/// the truthful wire shape — a consumer reading this JSON must be prepared for a null, and a future
/// caller may genuinely have nothing to put here. The positional parameter has no <c>= null</c>
/// though (unlike <see cref="StepDiagnosis.Reason"/>, where absence is a normal outcome), so every
/// construction site has to STATE what it is passing rather than inherit an omission that would
/// quietly contradict the "always populated" contract above.
/// </para>
/// </param>
public sealed record EnvironmentErrorDiagnosis(
    string ErrorKind,
    string ResourceName,
    string? Detail,
    VerdictReason? Reason);

/// <summary>
/// REQ-007's <c>explain_run</c> result: a taxonomy-faithful diagnosis of a (possibly historical)
/// suite run, built PURELY from its events file — no suite is ever re-run to produce this.
/// </summary>
/// <param name="Verdict">One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — <see cref="RunVerdict"/>'s own names.</param>
/// <param name="CategoryMeaning">
/// A short, fixed explanation of what <see cref="Verdict"/>'s CATEGORY means (§12.1) — e.g. that an
/// <c>EnvironmentError</c> is an infrastructure problem and explicitly NOT a test defect. Always
/// present, regardless of verdict.
/// </param>
/// <param name="Summary">A short, readable, one-paragraph summary of the whole diagnosis.</param>
/// <param name="TotalStepCount">How many steps the events file recorded a final outcome for.</param>
/// <param name="PassedStepCount">How many of those steps passed.</param>
/// <param name="NotableSteps">
/// Every step whose own verdict is not <c>Pass</c>, with full evidence (subject to the response
/// budget) — empty for an all-passing run.
/// </param>
/// <param name="OmittedNotableStepCount">
/// How many additional non-passing steps exist beyond <see cref="NotableSteps"/>, omitted to fit the
/// response budget; <c>0</c> when none were.
/// </param>
/// <param name="EnvironmentErrors">
/// Every <c>environment-error</c> event the run recorded, capped for the response budget.
/// </param>
/// <param name="OmittedEnvironmentErrorCount">
/// How many additional <c>environment-error</c> events exist beyond <see cref="EnvironmentErrors"/>,
/// omitted to fit the response budget; <c>0</c> when none were.
/// </param>
/// <param name="EventsFilePath">The events file this diagnosis was built from.</param>
/// <param name="EventsTruncated">
/// <see langword="true"/> when the events file itself exceeded
/// <see cref="Run.EventsFileReader.MaxEventsFileBytes"/> and was only read up to that many bytes — the
/// diagnosis may be based on incomplete source data.
/// </param>
/// <param name="ResponseTruncated">
/// <see langword="true"/> when embedding full evidence for every notable step would have exceeded
/// <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/>, so observations/attempt timelines/
/// step counts were progressively trimmed — the full detail still exists in
/// <see cref="EventsFilePath"/>.
/// </param>
/// <param name="ClassificationHints">
/// US-S4-02's flat digest: every hint carried by an item THIS RESPONSE ACTUALLY INCLUDES — one per
/// classified notable step and one per environment-error record — deduplicated, so a host can
/// summarise what went wrong without walking the two collections.
/// <para>
/// <b>Deduplication is exact-string and order-preserving (first occurrence wins)</b>, and the list is
/// drawn from <see cref="NotableSteps"/> and <see cref="EnvironmentErrors"/> in that order. Ten steps
/// failing the same assertion yield ONE hint, which is the whole point of a digest.
/// </para>
/// <para>
/// <b>Bounded by the tier's own item caps, deliberately.</b> Hints come only from the items the
/// chosen tier kept — never from the ones <see cref="OmittedNotableStepCount"/>/
/// <see cref="OmittedEnvironmentErrorCount"/> report — so this field cannot grow with the SOURCE
/// file's size. Sourcing it from the uncapped lists would make it unbounded and would defeat the
/// response-size guarantee the tiers exist to provide, no matter how carefully the rest of the
/// payload were capped.
/// </para>
/// </param>
public sealed record Diagnosis(
    string Verdict,
    string CategoryMeaning,
    string Summary,
    int TotalStepCount,
    int PassedStepCount,
    IReadOnlyList<StepDiagnosis> NotableSteps,
    int OmittedNotableStepCount,
    IReadOnlyList<EnvironmentErrorDiagnosis> EnvironmentErrors,
    int OmittedEnvironmentErrorCount,
    string EventsFilePath,
    bool EventsTruncated,
    bool ResponseTruncated,
    IReadOnlyList<string> ClassificationHints);

/// <summary>
/// The outcome of <see cref="ExplainRunOrchestrator.ExplainAsync"/> — a closed discriminated union (a
/// private constructor confines derivation to the cases nested here), mirroring
/// <see cref="Run.RunSuiteOutcome"/>'s own shape: every branch a caller must handle is visible at the
/// type level, not inferred from a message string.
/// </summary>
public abstract record ExplainRunOutcome
{
    private ExplainRunOutcome()
    {
    }

    /// <summary>The events file was read and understood; see <see cref="Diagnosis"/> for the result.</summary>
    public sealed record Diagnosed(Diagnosis Diagnosis) : ExplainRunOutcome;

    /// <summary><c>eventsPath</c> was omitted and no run has completed this session yet.</summary>
    public sealed record NoRunToExplain(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path is a UNC/network location (forced-authentication risk) — rejected before any filesystem call is made against it.</summary>
    public sealed record InvalidPath(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path does not exist.</summary>
    public sealed record EventsFileNotFound(string Message) : ExplainRunOutcome;

    /// <summary>The resolved path exists but could not be read (permissions, a locked file, or another I/O failure).</summary>
    public sealed record EventsFileUnreadable(string Message) : ExplainRunOutcome;

    /// <summary>The file was read successfully but contained no recognisable vouchfx event — empty, or entirely unparseable/garbage content.</summary>
    public sealed record NoRecognisableEvents(string Message) : ExplainRunOutcome;
}

/// <summary>
/// US-S4-01's structured classification of ONE notable step or ONE environment-error record: a
/// machine-branchable <see cref="Kind"/> from <see cref="VerdictReasonKinds"/>'s closed vocabulary,
/// plus a deterministic plain-text <see cref="Hint"/> for a human (or a host LLM) to read.
/// </summary>
/// <remarks>
/// <b>The bound and the non-empty contract are enforced HERE, not only at the call site.</b> An
/// earlier version capped the hint in <c>VerdictReasonClassifier</c>'s own factory helper, leaving
/// the record's public constructor a way to build a <see cref="VerdictReason"/> carrying an
/// arbitrarily long hint — which US-S4-02 will attach to every notable step at every
/// <c>explain_run</c> tier, so one such construction would evaporate the floor tier's budget
/// guarantee (a security-review finding). <see cref="Hint"/>'s <c>init</c> accessor now normalises
/// on EVERY construction path, including <c>with</c> expressions, so the invariant belongs to the
/// type rather than to a convention its callers are trusted to follow.
/// <para>
/// <b>Consequence: this type is SERIALISE-ONLY on the wire, and US-S4-02 kept it that way.</b> A
/// validating constructor THROWS on a malformed payload, so deserialising untrusted JSON straight
/// into it would turn a bad input into an exception rather than a rejection. Nothing does: this type
/// reaches the wire only inside <c>Diagnosis</c> (via <c>StepDiagnosis.Reason</c>/
/// <c>EnvironmentErrorDiagnosis.Reason</c>), no tool INPUT schema carries a <c>Diagnosis</c>, and no
/// code in <c>src/</c> or <c>tests/</c> deserialises one — checked when US-S4-02 chose to reuse this
/// record on those models rather than project a second, non-validating shape (one type, one truth,
/// no projection to drift). A future story that needs to READ a <c>Diagnosis</c> back from JSON must
/// add a converter or a non-validating projection FIRST.
/// </para>
/// </remarks>
public sealed record VerdictReason(string? Kind, string Hint)
{
    // Initialised from the primary-constructor parameter (parameters are in scope in a record's
    // field initialisers), so the normalisation below runs on the positional construction path too —
    // not only on `with { Hint = … }`, which goes through the init accessor.
    private readonly string _hint = NormaliseHint(Hint);

    /// <summary>
    /// One of <see cref="VerdictReasonKinds"/>' values, or <see langword="null"/> when the rule table
    /// recognised the material well enough to DESCRIBE it but not to CLASSIFY it.
    /// </summary>
    /// <remarks>
    /// <b>Nullable on purpose, and the null case is a real, tested state — not an oversight.</b>
    /// US-S4-01's fail-closed default requires an unrecognised <c>errorKind</c> to leave the kind
    /// unset "rather than guessing", while the same acceptance criterion's Gherkin also requires the
    /// hint to "still describe the raw errorKind and detail verbatim". Those two demands can only
    /// both hold if a reason can carry a hint with no kind, which is why this property is nullable
    /// rather than the whole record being null in that case. A step, by contrast, is left entirely
    /// unclassified (<see cref="VerdictReasonClassifier.ClassifyStep(StepOutcome, SuiteRunSummary)"/>
    /// returns <see langword="null"/>) when no rule matches — there is no equivalent "raw"
    /// description to fall back to for a step, and inventing one would be the fabrication the
    /// fail-closed rule forbids.
    /// </remarks>
    public string? Kind { get; init; } = Kind;

    /// <summary>
    /// A short, bounded (<see cref="VerdictReasonClassifier.MaxHintChars"/>), never-empty plain-text
    /// explanation.
    /// </summary>
    /// <remarks>
    /// Carries only text the ENGINE already emitted (an image reference, a resource name, a
    /// health-gate timeout, an observation sentence) spliced into the rule table's own fixed
    /// wording — this server never resolves a <c>${secret:…}</c> reference and never re-redacts one;
    /// the engine is the sole redaction authority and its already-redacted text is relayed as-is.
    /// </remarks>
    public string Hint
    {
        get => _hint;
        init => _hint = NormaliseHint(value);
    }

    /// <remarks>
    /// Truncation is MARKED, not silent — the same <see cref="VerdictReasonClassifier.TruncationMarker"/>
    /// the rule table's own value capping uses, and inside the same bound (the cut is to
    /// <c>MaxHintChars - 1</c>), so a reader can tell a complete hint from a clipped one without any
    /// budget number stated elsewhere changing.
    /// </remarks>
    private static string NormaliseHint(string hint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hint);

        return hint.Length > VerdictReasonClassifier.MaxHintChars
            ? hint[..(VerdictReasonClassifier.MaxHintChars - 1)] + VerdictReasonClassifier.TruncationMarker
            : hint;
    }
}

/// <summary>
/// The closed <c>reason.kind</c> vocabulary (spec §8.3, adopted by spec §5.9 and this repo's
/// <c>explain_run</c> per plan D4's naming rule) — the exact set of values a host may ever have to
/// branch on.
/// </summary>
/// <remarks>
/// <b><see cref="Compile"/> is a deliberate no-op entry.</b> It exists for §8.3 vocabulary
/// completeness and forward compatibility with a future <c>compile_spec</c> relay (upstream ask U3,
/// structurally out of scope for this repo — see <c>specs/sprints/sprint-00-overview.md</c> §3), and
/// NO rule in <see cref="VerdictReasonClassifier"/> assigns it: no event this server's event-stream
/// reader sees today originates from a Roslyn compile step. Two tests keep that true from different
/// directions — a fixture sweep over the whole corpus, and
/// <c>VerdictReasonClassifierTests.TheCompileKind_IsReferencedNowhereInTheClassifierSourceExceptItsOwnDeclaration</c>,
/// a SOURCE-level guard (the derive-from-source pattern <c>SecretHygieneSourceGuardTests</c> uses)
/// that a new rule could not evade merely by not being covered by a fixture.
/// </remarks>
public static class VerdictReasonKinds
{
    /// <summary>An image could not be pulled — wrong tag, or missing registry credentials.</summary>
    public const string Pull = "pull";

    /// <summary>A resource never passed its health gate / <c>waitFor</c> within the configured window.</summary>
    public const string Unhealthy = "unhealthy";

    /// <summary>Seeding a dependency failed before the suite could exercise anything.</summary>
    public const string Seed = "seed";

    /// <summary>
    /// A RETRY window elapsed without a match. TWO hint variants share this ONE kind — see
    /// <see cref="VerdictReasonClassifier"/>'s rule table — because a host branches on "the wait
    /// expired", while WHY it expired (values seen but unmatched, versus nothing seen at all) is
    /// advice for a human, not a second machine state.
    /// </summary>
    public const string Timeout = "timeout";

    /// <summary>A captured placeholder resolved to nothing, so everything downstream of it was unmet.</summary>
    public const string CaptureUnmet = "capture_unmet";

    /// <summary>A partition/grace-period signal the engine reported in its own words.</summary>
    public const string Partition = "partition";

    /// <summary>An assertion did not hold: an expected and an observed value that differ. <b>The only kind ever assigned to a <c>Fail</c> step.</b></summary>
    public const string Assertion = "assertion";

    /// <summary>Reserved for a future <c>compile_spec</c> relay; never assigned today — see this type's remarks.</summary>
    public const string Compile = "compile";

    /// <summary>
    /// Every value above, for tests and for any future consumer that needs to validate a kind.
    /// </summary>
    /// <remarks>
    /// A <see cref="FrozenSet{T}"/> rather than a <see cref="HashSet{T}"/> behind an interface,
    /// following <c>Validation.DependencyKinds.All</c>: this is a closed vocabulary built once at
    /// type initialisation and read on every classification, which is exactly the shape
    /// <see cref="FrozenSet{T}"/> optimises — and, unlike an <c>IReadOnlySet&lt;string&gt;</c>
    /// surface over a <see cref="HashSet{T}"/>, it cannot be downcast back to a mutable set by a
    /// consumer.
    /// </remarks>
    public static FrozenSet<string> All { get; } = new[]
    {
        Pull, Unhealthy, Seed, Timeout, CaptureUnmet, Partition, Assertion, Compile,
    }.ToFrozenSet(StringComparer.Ordinal);
}
