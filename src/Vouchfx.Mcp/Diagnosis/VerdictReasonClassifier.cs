using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Diagnosis;

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

    private static string NormaliseHint(string hint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hint);

        return hint.Length > VerdictReasonClassifier.MaxHintChars
            ? hint[..VerdictReasonClassifier.MaxHintChars]
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

/// <summary>
/// US-S4-01's <c>reason.kind</c> rule table: a PURE function over material
/// <see cref="SuiteEventParser"/> has already extracted, assigning a structured
/// <see cref="VerdictReason"/> to each notable step and each environment-error record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure by construction — no I/O, no CLI spawn, no new event type parsed.</b> Every input is a
/// record the parser already produced (<see cref="StepOutcome"/>, <see cref="StepAttempt"/>,
/// <see cref="EnvironmentErrorSummary"/>); the only work done here is reading text those records
/// already carry. That is US-S4-01's last acceptance criterion, and it is also what lets both
/// <c>explain_run</c> and <c>diagnose_run</c> use ONE table (spec §8.3: "one rule table, two
/// consumers, never two implementations") without either of them re-reading the events file.
/// </para>
/// <para>
/// <b>Why the STEP surface takes <see cref="StepOutcome"/> and not
/// <see cref="StepDiagnosis"/>.</b> <c>StepDiagnosis</c> is the TIER-TRIMMED projection: at
/// <c>ExplainRunOrchestrator</c>'s floor tier its observation is dropped and its attempt list is
/// empty. Classifying from it would make a step's <c>reason.kind</c> depend on how big the rest of
/// the response happened to be — the same run classified differently on two calls. The rules key on
/// the untrimmed parser output instead, which is exactly what US-S4-02 needs to keep the
/// classification present at every tier including the floor. The ATTEMPT LIST is load-bearing for
/// the same reason: it is what tells a step that polled apart from one that did not (see the
/// capture-unmet rule).
/// </para>
/// <para>
/// <b>The rule table, in evaluation order.</b> Order matters only where two rules could both match;
/// where it does, the MORE SPECIFIC evidence wins and a test pins the precedence.
/// </para>
/// <list type="table">
/// <listheader><term>Surface</term><description>Rules, in order</description></listheader>
/// <item>
///   <term>Step, verdict <c>Fail</c></term>
///   <description>
///   <see cref="VerdictReasonKinds.Assertion"/> when the observation carries BOTH an expected and an
///   observed value; otherwise unclassified. This branch is CLOSED: no other kind is reachable for a
///   <c>Fail</c> step, which is the structural half of US-S4-03's "an <c>assertion</c>-classified
///   Fail step never produces a spec-edit proposal" — the partition is enforced by construction
///   here, not by convention there.
///   </description>
/// </item>
/// <item>
///   <term>Step, verdict <c>EnvironmentError</c>/<c>Inconclusive</c></term>
///   <description>
///   <see cref="VerdictReasonKinds.CaptureUnmet"/> (an expected name paired with a literal
///   <c>null</c> observed value, on a step that did NOT poll) →
///   <see cref="VerdictReasonKinds.Partition"/> (a partition/grace-period signal in a STRING value
///   of the observation) → <see cref="VerdictReasonKinds.Timeout"/> (any <c>Inconclusive</c> step
///   the first two did not explain, in two hint variants keyed on whether ANY retry attempt carried
///   an observation) → otherwise unclassified.
///   </description>
/// </item>
/// <item>
///   <term>Step, verdict <c>Pass</c> (or an unrecognised verdict string)</term>
///   <description>Never classified. A passing step is not notable, and an unrecognised verdict is precisely the "we don't know" state the taxonomy's unknown-token rule protects.</description>
/// </item>
/// <item>
///   <term>Environment-error record</term>
///   <description>
///   <see cref="VerdictReasonKinds.Pull"/>/<see cref="VerdictReasonKinds.Unhealthy"/>/
///   <see cref="VerdictReasonKinds.Seed"/> from <see cref="EnvironmentErrorSummary.ErrorKind"/>
///   against the recognised sets below; then, ONLY for a kind none of those recognised, the AC's
///   enumerated <c>"manifest unknown"</c> message signature → <see cref="VerdictReasonKinds.Pull"/>;
///   anything else keeps <see cref="VerdictReason.Kind"/> <see langword="null"/> while still
///   describing the raw kind and detail.
///   </description>
/// </item>
/// </list>
/// <para>
/// <b>Partition is a STEP rule only, and this DOES decline part of a clause — stated rather than
/// hidden.</b> US-S4-01 writes the rule as "a step whose observation/DETAIL text names a partition
/// or grace-period signal": <c>observation</c> is a step's field, <c>detail</c> is an
/// environment-error record's, so read literally the clause reaches both surfaces. Only the
/// observation half is implemented, because the IMMEDIATELY PRECEDING criterion governs the other
/// surface explicitly and in the opposite direction — environment-error records "classify by
/// <c>ErrorKind</c>", and "an <c>ErrorKind</c> this table does not recognise leaves
/// <c>reason.kind</c> unset (<c>null</c>) rather than guessing — a deliberate fail-closed default,
/// never a fabricated classification". Promoting such a record to <c>partition</c> on a text scan of
/// its <c>detail</c> is precisely the guess that criterion forbids, so where the two clauses
/// collide the explicit fail-closed one wins. A partition that manifests as a step outcome — which
/// is how the engine reports one — is classified exactly as the story describes. If a future sprint
/// wants the detail half, it needs the environment-error criterion widened first, not this rule
/// loosened.
/// </para>
/// <para>
/// <b>Hint wording is the spec's, with placeholders substituted plain.</b> Spec §8.3's hint
/// templates are quoted in markdown, where every placeholder is written <c>`&lt;image&gt;`</c>,
/// <c>`&lt;ms&gt;`</c>, <c>`&lt;e&gt;`</c> and so on — backticked because an unbackticked
/// <c>&lt;image&gt;</c> would be eaten by any markdown renderer, not because the backticks are part
/// of the emitted text. They are therefore dropped and the value substituted bare, and the whole
/// table uses that one convention (including the hints this repo authors, which is why they do not
/// pick up <c>FailProposalBuilder</c>'s <c>'quoted'</c> style). Every hint is snapshot-tested
/// character for character in <c>VerdictReasonClassifierTests</c>, so any future rewording is a
/// deliberate, visible edit.
/// </para>
/// <para>
/// <b>Secret hygiene.</b> Hints splice engine-emitted text into fixed wording and never resolve,
/// re-derive, or re-redact anything: whatever the engine already redacted stays redacted, and this
/// server never reads a secret store. EVERY engine-supplied fragment reaching a hint passes through
/// <see cref="TextSanitiser.SanitiseForDisplay"/> at THIS boundary — not because the parser failed
/// to sanitise it, but so the property is structural here rather than an inherited assumption; for
/// values decoded out of JSON it is genuinely load-bearing (see <see cref="RenderScalar"/>), and for
/// the rest it is idempotent and allocation-free on already-printable text.
/// </para>
/// </remarks>
public static class VerdictReasonClassifier
{
    /// <summary>
    /// Maximum characters in any hint this table emits.
    /// </summary>
    /// <remarks>
    /// Deliberately small. US-S4-02 carries the classification at EVERY <c>explain_run</c> tier
    /// INCLUDING the floor tier — whose whole purpose is to shed observation text — so a hint has to
    /// be cheap enough that adding one per notable step and per environment-error record cannot move
    /// the response into a lower tier. At this bound the floor tier's worst case is 3 steps + 3
    /// environment errors × 300 characters ≈ 1.8&#160;KB against
    /// <c>ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes</c> (32&#160;KB). The cap matters:
    /// <see cref="EnvironmentErrorSummary.ErrorKind"/>/<see cref="EnvironmentErrorSummary.ResourceName"/>/
    /// <see cref="EnvironmentErrorSummary.Detail"/> are each capped at 2,000 characters at parse
    /// time, so an uncapped hint concatenating them would be ~4&#160;KB on its own. Enforced by
    /// <see cref="VerdictReason.Hint"/> itself, so no construction path can exceed it.
    /// </remarks>
    internal const int MaxHintChars = 300;

    /// <summary>
    /// Maximum characters of any single VALUE (a step id, a resource name, an image reference, an
    /// expected/actual scalar) spliced into a hint.
    /// </summary>
    /// <remarks>
    /// Every value-shaped fragment is capped here, BEFORE
    /// <see cref="VerdictReason.Hint"/>'s whole-hint bound, so one long value can never crowd the
    /// rest of a sentence out of the hint entirely — a 2,000-character step id would otherwise
    /// consume the whole 300-character budget and leave the reader nothing but a truncated
    /// identifier. The one deliberate exception is an environment error's <c>detail</c>, which IS
    /// the tail of its sentence in <see cref="BuildSeedHint"/>/<see cref="BuildRawDescription"/> and
    /// is bounded by the hint cap instead.
    /// </remarks>
    internal const int MaxValueChars = 120;

    /// <summary>
    /// The most recorded attempts a step may have and still be eligible for
    /// <see cref="VerdictReasonKinds.CaptureUnmet"/> — see that rule for why polling disqualifies it.
    /// </summary>
    private const int MaxAttemptsForCaptureUnmet = 1;

    /// <summary>Maximum nesting depth the observation searches descend.</summary>
    /// <remarks>
    /// An observation is arbitrary engine-supplied JSON up to
    /// <c>SuiteEventParser.MaxObservationCharsAtParse</c> (10,000 characters); a depth bound plus
    /// <see cref="MaxObservationNodes"/> keeps these walks O(1)-bounded rather than proportional to
    /// whatever shape a future engine (or a hostile suite's echoed response body) produces. Real
    /// engine observations are one or two levels deep — <c>{"exists":{"expected":true,"actual":false}}</c>
    /// is the measured shape — so this is a backstop, not a working limit.
    /// </remarks>
    private const int MaxObservationDepth = 12;

    /// <summary>Maximum JSON nodes an observation search visits — see <see cref="MaxObservationDepth"/>.</summary>
    private const int MaxObservationNodes = 512;

    /// <summary>Longest digit run this table will relay as a millisecond figure — beyond it the "timeout" is not a plausible one.</summary>
    private const int MaxTimeoutDigits = 15;

    /// <summary>
    /// The one MESSAGE signature that promotes an OTHERWISE-UNRECOGNISED error kind to
    /// <see cref="VerdictReasonKinds.Pull"/> — the registry's own wording for a tag that does not
    /// exist, named explicitly by US-S4-01 ("an image-pull-shaped kind (e.g. <c>ImagePull</c>, or a
    /// message containing 'manifest unknown')").
    /// </summary>
    /// <remarks>
    /// <b>Evaluated AFTER the kind table, and the ordering is the whole point.</b> A recognised kind
    /// is the engine's own statement of what went wrong and always wins: a <c>HealthGate</c> failure
    /// whose detail happens to quote a registry error ("… waiting for a container whose last event
    /// was manifest unknown …") is an unhealthy resource, not a pull failure. An earlier version
    /// tested this signature in the FIRST branch, so any recognised kind could be overridden by a
    /// substring of caller-influenced text — a security-review finding, since the detail text
    /// ultimately traces back to the suite. This does not weaken the fail-closed default either: it
    /// is an ENUMERATED signature the story lists, not an inference from an unknown kind's shape, and
    /// an unrecognised kind whose detail lacks this exact phrase still classifies as nothing at all.
    /// </remarks>
    private const string ManifestUnknownSignature = "manifest unknown";

    /// <summary>Punctuation trimmed from a candidate image token's edges before it is tested — prose around a reference, not part of it.</summary>
    private static readonly char[] TokenPunctuation = ['.', ',', ';', '(', ')', '[', ']', '"', '\'', '!', '?'];

    /// <summary>
    /// <see cref="EnvironmentErrorSummary.ErrorKind"/> values that mean "the image could not be
    /// pulled". Compared case-insensitively: the engine writes PascalCase
    /// <c>OrchestrationErrorKind</c> names, and tolerating a casing change costs nothing and
    /// fabricates nothing.
    /// </summary>
    private static readonly FrozenSet<string> PullErrorKinds =
        new[] { "ImagePull" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Error kinds that mean "the resource never became healthy" — the three shapes US-S4-01 names.</summary>
    private static readonly FrozenSet<string> UnhealthyErrorKinds =
        new[] { "HealthGate", "Unhealthy", "WaitFor" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Error kinds that mean "seeding failed".</summary>
    private static readonly FrozenSet<string> SeedErrorKinds =
        new[] { "Seed" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The partition/grace-period signals this repo recognises in a step's own observation text —
    /// this codebase's OWN vocabulary for the third Inconclusive cause (<c>RunVerdict.Inconclusive</c>:
    /// "timeout, partition grace period exceeded, upstream capture unmet";
    /// <c>ExplainRunOrchestrator.CategoryMeaning</c>: "a partition that outlasted its grace period").
    /// </summary>
    private static readonly string[] PartitionSignals = ["partition", "grace period", "grace-period"];

    /// <summary>Observation keys naming the value a step EXPECTED.</summary>
    private static readonly FrozenSet<string> ExpectedKeys =
        new[] { "expected" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Observation keys naming the value a step actually OBSERVED — one measured, one fixture-only,
    /// one defensive.
    /// </summary>
    /// <remarks>
    /// <c>actual</c> is MEASURED against the pinned engine (<c>{"exists":{"expected":true,"actual":false}}</c>
    /// — <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>). <c>got</c> appears only in this repo's
    /// own synthetic fixtures (<c>Run/GetStepTimelineOrchestratorTests.cs:218</c>,
    /// <c>RealGetStepTimelineMcpTests.cs:100</c>), never in engine output anyone has measured.
    /// <c>observed</c> appears in neither and is purely defensive, read on the same
    /// additive-frozen-contract reasoning <c>SuiteEventParser</c> uses to probe fields the engine does
    /// not emit today: reading a key that is absent costs nothing and asserts nothing. An earlier
    /// version of this comment claimed all three "appear in this repo's measured and fixture event
    /// streams", which was false for <c>observed</c> and overstated for <c>got</c>.
    /// </remarks>
    private static readonly FrozenSet<string> ObservedKeys =
        new[] { "actual", "got", "observed" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies one notable step, resolving its RETRY attempts from
    /// <see cref="SuiteRunSummary.AttemptsByStepId"/>.
    /// </summary>
    /// <returns>The step's reason, or <see langword="null"/> when no rule matched — never a guess.</returns>
    public static VerdictReason? ClassifyStep(StepOutcome step, SuiteRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(summary);

        var attempts = summary.AttemptsByStepId.TryGetValue(step.StepId, out var recorded)
            ? recorded
            : [];

        return ClassifyStep(step, attempts);
    }

    /// <summary>
    /// Classifies one notable step against an explicit attempt list — the overload
    /// <c>ExplainRunOrchestrator.BuildStepDiagnosis</c> uses, since it has already resolved the same
    /// list for the attempt timeline and must not look it up twice.
    /// </summary>
    /// <param name="step">The step's UNTRIMMED parser output — see this type's remarks on why not <see cref="StepDiagnosis"/>.</param>
    /// <param name="attempts">Every recorded attempt for this step, untrimmed; empty for an IMMEDIATE step.</param>
    public static VerdictReason? ClassifyStep(StepOutcome step, IReadOnlyList<StepAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(attempts);

        // A CLOSED branch, and that is the point: assertion is the only kind a Fail step can ever
        // receive (US-S4-01), so the rules below are not merely "unlikely" to fire on a Fail step —
        // they are unreachable from it.
        if (string.Equals(step.Verdict, nameof(RunVerdict.Fail), StringComparison.Ordinal))
        {
            return ClassifyAssertion(step);
        }

        // Everything else in the table is EnvironmentError/Inconclusive material. Pass is never
        // notable; an unrecognised verdict string is the taxonomy's "we don't know" state and must
        // not be classified as anything.
        var isEnvironmentError = string.Equals(step.Verdict, nameof(RunVerdict.EnvironmentError), StringComparison.Ordinal);
        var isInconclusive = string.Equals(step.Verdict, nameof(RunVerdict.Inconclusive), StringComparison.Ordinal);
        if (!isEnvironmentError && !isInconclusive)
        {
            return null;
        }

        if (ClassifyCaptureUnmet(step, attempts) is { } captureUnmet)
        {
            return captureUnmet;
        }

        if (FindPartitionText(step.Observation) is { } partitionText)
        {
            // The engine's OWN wording, relayed. Spec §8.3 says "engine text" for this kind, and this
            // server relays rather than rephrases it — the same discipline RawEventRelay applies to
            // the raw stream.
            return new VerdictReason(VerdictReasonKinds.Partition, partitionText);
        }

        return isInconclusive ? ClassifyTimeout(attempts) : null;
    }

    /// <summary>Classifies one <c>environment-error</c> record.</summary>
    /// <returns>
    /// Never <see langword="null"/>: an unrecognised <see cref="EnvironmentErrorSummary.ErrorKind"/>
    /// yields a reason whose <see cref="VerdictReason.Kind"/> is <see langword="null"/> but whose
    /// hint still describes the raw kind and detail — see <see cref="VerdictReason.Kind"/>'s own
    /// remarks for why both halves of that are required.
    /// </returns>
    public static VerdictReason ClassifyEnvironmentError(EnvironmentErrorSummary error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // The kind table FIRST, in full: the engine's own statement of what went wrong outranks any
        // signature scraped out of its message text — see ManifestUnknownSignature's remarks.
        if (PullErrorKinds.Contains(error.ErrorKind))
        {
            return new VerdictReason(VerdictReasonKinds.Pull, BuildPullHint(error));
        }

        if (UnhealthyErrorKinds.Contains(error.ErrorKind))
        {
            return new VerdictReason(VerdictReasonKinds.Unhealthy, BuildUnhealthyHint(error));
        }

        if (SeedErrorKinds.Contains(error.ErrorKind))
        {
            return new VerdictReason(VerdictReasonKinds.Seed, BuildSeedHint(error));
        }

        // Only now, with the kind unrecognised, does the AC's enumerated message signature apply.
        if (CarriesManifestUnknownSignature(error.Detail))
        {
            return new VerdictReason(VerdictReasonKinds.Pull, BuildPullHint(error));
        }

        // Fail-closed: no kind. The hint still says what the engine said, verbatim, so a host loses
        // nothing but a machine-branchable label it would have been wrong to invent.
        return new VerdictReason(Kind: null, BuildRawDescription(error));
    }

    // ── Step rules ───────────────────────────────────────────────────────────────────────────────

    private static VerdictReason? ClassifyAssertion(StepOutcome step)
    {
        var evidence = ReadEvidence(step.Observation);

        // BOTH values, per US-S4-01. A Fail step with only one side (or none) is left unclassified
        // rather than reported as an assertion mismatch nobody can see the two halves of — the same
        // "no usable expected/observed evidence, skip rather than invent" rule FailProposalBuilder
        // already applies to its own proposals.
        return evidence is { Expected: { } expected, Observed: { } observed }
            ? new VerdictReason(VerdictReasonKinds.Assertion, $"Expected {expected}, actual {observed}.")
            : null;
    }

    /// <summary>
    /// <see cref="VerdictReasonKinds.CaptureUnmet"/> for a step that declared what it expected, whose
    /// observed value the engine recorded as literal <c>null</c>, AND WHICH DID NOT POLL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The attempt gate is a correction, not a refinement.</b> The
    /// <c>{"expected":"orderId","got":null}</c> shape this rule keys on is taken from this repo's own
    /// fixtures — but a code review established what those fixtures actually depict: in
    /// <c>Run/GetStepTimelineOrchestratorTests.cs:218</c> it is attempt 3 of a poll that PASSES on
    /// attempt 4, i.e. an ORDINARY MID-RETRY MISS, not a capture that resolved to nothing. Keying on
    /// the shape alone therefore claimed a capture defect on the commonest Inconclusive shape there
    /// is, and — because it outranks the timeout rule — took US-S4-03's <c>timeouts</c>/<c>match</c>
    /// proposals off the table for it, replacing an actionable diagnosis with a wrong one.
    /// </para>
    /// <para>
    /// A step with <see cref="MaxAttemptsForCaptureUnmet"/> or fewer recorded attempts never entered
    /// a poll cycle (an IMMEDIATE step records none; one attempt is a single try), so "the value was
    /// never there" is the only reading left. A step that demonstrably RETRIED falls through to the
    /// timeout rule, whose two variants already describe exactly that situation and whose non-empty
    /// variant already names "the match key or capture path" as the likely cause — so nothing is
    /// lost, and the classification stops overstating what the evidence supports.
    /// </para>
    /// </remarks>
    private static VerdictReason? ClassifyCaptureUnmet(StepOutcome step, IReadOnlyList<StepAttempt> attempts)
    {
        if (attempts.Count > MaxAttemptsForCaptureUnmet)
        {
            return null;
        }

        var evidence = ReadEvidence(step.Observation);
        if (!evidence.IsCaptureUnmet)
        {
            return null;
        }

        var stepId = Sanitise(Cap(step.StepId, MaxValueChars));
        return new VerdictReason(
            VerdictReasonKinds.CaptureUnmet,
            $"Step {stepId} never captured {evidence.Expected}: the capture path resolved to nothing.");
    }

    private static VerdictReason ClassifyTimeout(IReadOnlyList<StepAttempt> attempts)
    {
        // "Carried an observation" is deliberately a test on the observation's PRESENCE, not on its
        // content: an engine-emitted empty JSON string ("" — two characters of raw text) counts as a
        // value observed, because the engine looked, found something, and recorded it as empty. That
        // is the non-empty variant's own case ("values were seen, none matched"), and second-guessing
        // it here would mean this table deciding which engine-recorded observations are real.
        var observedCount = attempts.Count(a => !string.IsNullOrWhiteSpace(a.Observation));

        return observedCount > 0
            ? new VerdictReason(
                VerdictReasonKinds.Timeout,
                $"Observed {observedCount.ToString(CultureInfo.InvariantCulture)} value(s) but none matched; " +
                "the match key or capture path is probably wrong.")
            : new VerdictReason(
                VerdictReasonKinds.Timeout,
                "No values observed at all; the producer path, target name, or serialization is the " +
                "likely cause.");
    }

    // ── Environment-error hints ─────────────────────────────────────────────────────────────────

    private static string BuildPullHint(EnvironmentErrorSummary error)
    {
        // The detail is the richer source (it usually carries the full registry reference); the
        // resource name is the fallback identity, and the "(unknown)" SENTINEL is never relayed as
        // one — SuiteEventParser documents it as a sentinel precisely so consumers do not render it
        // as an engine-reported identity.
        var image = ExtractImageReference(error.Detail)
            ?? (string.Equals(error.ResourceName, SuiteEventParser.UnnamedResourceSentinel, StringComparison.Ordinal)
                ? null
                : DescribeResource(error.ResourceName));

        return image is null
            ? "Image tag likely wrong or registry auth missing."
            : $"Image tag likely wrong or registry auth missing: {image}";
    }

    private static string BuildUnhealthyHint(EnvironmentErrorSummary error)
    {
        var name = DescribeResource(error.ResourceName);
        var timeoutMs = ExtractTimeoutMilliseconds(error.Detail);

        return timeoutMs is null
            ? $"Resource {name} never became healthy; check its logs."
            : $"Resource {name} never became healthy within {timeoutMs}ms; check its logs.";
    }

    private static string BuildSeedHint(EnvironmentErrorSummary error)
    {
        var target = DescribeResource(error.ResourceName);

        // The detail is the SENTENCE here, not a value slotted into one, so it is bounded by the
        // hint cap rather than MaxValueChars — see that constant's remarks.
        return string.IsNullOrWhiteSpace(error.Detail)
            ? $"Seeding failed on {target}."
            : EndSentence($"Seeding failed on {target}: {Sanitise(error.Detail.Trim())}");
    }

    private static string BuildRawDescription(EnvironmentErrorSummary error)
    {
        var name = DescribeResource(error.ResourceName);
        var kind = Sanitise(Cap(error.ErrorKind, MaxValueChars));

        return string.IsNullOrWhiteSpace(error.Detail)
            ? $"Resource {name} reported {kind}."
            : EndSentence($"Resource {name} reported {kind}: {Sanitise(error.Detail.Trim())}");
    }

    /// <summary>
    /// The resource name as a hint renders it — the sentinel <see cref="SuiteEventParser.UnnamedResourceSentinel"/>
    /// passes through unchanged (it already reads as "(unknown)", which is honest), and everything
    /// else is capped and sanitised like any other value spliced into a hint.
    /// </summary>
    private static string DescribeResource(string resourceName) => Sanitise(Cap(resourceName, MaxValueChars));

    // ── Text signatures ─────────────────────────────────────────────────────────────────────────

    private static bool CarriesManifestUnknownSignature(string? detail) =>
        detail is not null && detail.Contains(ManifestUnknownSignature, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first whitespace-delimited token in <paramref name="detail"/> that looks like a container
    /// image reference, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// A hand-written linear scan rather than a regular expression, following
    /// <c>Validation.PlaceholderScanner</c>'s precedent: the input is engine text derived from
    /// untrusted suite content, and a backtracking pattern over it is the shape a catastrophic
    /// backtracking (ReDoS) input targets. This scan is O(n) with no backtracking, so it needs no
    /// timeout to be safe.
    /// </remarks>
    private static string? ExtractImageReference(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var start = 0;
        for (var i = 0; i <= detail.Length; i++)
        {
            if (i < detail.Length && !char.IsWhiteSpace(detail[i]))
            {
                continue;
            }

            var token = detail[start..i].Trim(TokenPunctuation);
            start = i + 1;

            if (LooksLikeImageReference(token))
            {
                return Sanitise(Cap(token, MaxValueChars));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="token"/> has the SHAPE of a container image reference:
    /// <c>registry/repo:tag</c>, <c>repo:tag</c>, or <c>registry/repo</c>.
    /// </summary>
    /// <remarks>
    /// Shape only — this never asserts the image EXISTS or that the engine meant it as one. A URL is
    /// excluded (<c>://</c>) so a connection string in the detail cannot be presented as an image,
    /// and a token with no letter at all is excluded so a timing figure or an exit code cannot be.
    /// </remarks>
    private static bool LooksLikeImageReference(string token)
    {
        if (token.Length is < 3 or > 200)
        {
            return false;
        }

        if (token.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!token.Contains('/') && !token.Contains(':'))
        {
            return false;
        }

        if (token[0] is ':' or '/' or '-' || token[^1] is ':' or '/')
        {
            return false;
        }

        var sawLetter = false;
        foreach (var c in token)
        {
            if (char.IsAsciiLetter(c))
            {
                sawLetter = true;
                continue;
            }

            if (!char.IsAsciiDigit(c) && c is not ('.' or '_' or '-' or ':' or '/' or '@'))
            {
                return false;
            }
        }

        return sawLetter;
    }

    /// <summary>
    /// The digits of the first <c>&lt;number&gt;ms</c> figure in <paramref name="detail"/> — the
    /// configured health-gate window, when the engine named one — or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// An environment-error record carries only <c>errorKind</c>/<c>resourceName</c>/<c>detail</c>, so
    /// the detail text is the ONLY place a configured timeout can come from. When it names none, the
    /// hint omits the clause rather than substituting a default — a fabricated number in a diagnostic
    /// is worse than a missing one. The result is a run of ASCII digits by construction, so it needs
    /// no sanitisation before it is spliced into a hint.
    /// </remarks>
    private static string? ExtractTimeoutMilliseconds(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        for (var i = 0; i < detail.Length; i++)
        {
            if (!char.IsAsciiDigit(detail[i]))
            {
                continue;
            }

            var digitsStart = i;
            while (i < detail.Length && char.IsAsciiDigit(detail[i]))
            {
                i++;
            }

            var digitsEnd = i;

            // At most one separating space: "30000ms" and "30000 ms" are both spellings a message
            // might use; "30000 milliseconds elapsed ms" is not one this rule pretends to read.
            var cursor = i < detail.Length && detail[i] == ' ' ? i + 1 : i;
            if (cursor + 2 > detail.Length ||
                !(detail[cursor] is 'm' or 'M') ||
                !(detail[cursor + 1] is 's' or 'S'))
            {
                continue;
            }

            // "300msec"/"300ms1" are not a millisecond figure this rule claims to have read.
            var after = cursor + 2;
            if (after < detail.Length && char.IsAsciiLetterOrDigit(detail[after]))
            {
                continue;
            }

            var digits = detail[digitsStart..digitsEnd];
            return digits.Length > MaxTimeoutDigits ? null : digits;
        }

        return null;
    }

    /// <summary>
    /// The engine's own partition/grace-period SENTENCE from an observation, or
    /// <see langword="null"/> when the observation names no such signal in a place this rule accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a STRING VALUE counts — a signal in a KEY or a NUMBER does not.</b> An earlier version
    /// tested the whole raw observation blob, so an ordinary Kafka-shaped poll observation
    /// (<c>{"matched":false,"partition":3,"offset":112}</c>) classified as <c>partition</c> purely
    /// because a JSON KEY was spelled that way — a code-review MAJOR, and an expensive one: under
    /// US-S4-03 <c>partition</c> yields guidance text only, so the misclassification silently
    /// suppressed the <c>timeouts</c>/<c>match</c> spec-edit proposals that shape should have
    /// produced. A key names a FIELD; only a value is the engine SAYING something. A step whose
    /// observation carries the word only in a key now falls through to the timeout rule, which is
    /// what it was all along.
    /// </para>
    /// <para>
    /// The one case that still relays the whole blob is an observation that is not parseable JSON —
    /// which in practice means <c>SuiteEventParser</c> capped it mid-document. The sentence cannot be
    /// isolated there, and dropping the classification entirely would lose a real signal, so the raw
    /// text is relayed instead: still the engine's own text, never a sentence this server invented.
    /// </para>
    /// </remarks>
    private static string? FindPartitionText(string? observation)
    {
        // A cheap whole-text pre-filter: no signal anywhere means no parse is needed at all. It never
        // decides the classification on its own — the parse below is what does that.
        if (string.IsNullOrWhiteSpace(observation) || !CarriesPartitionSignal(observation))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(observation);
            var budget = MaxObservationNodes;
            return FindSignalString(document.RootElement, 0, ref budget);
        }
        catch (JsonException)
        {
            return Sanitise(Cap(observation.Trim(), MaxHintChars));
        }
    }

    private static bool CarriesPartitionSignal(string text)
    {
        foreach (var signal in PartitionSignals)
        {
            if (text.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first STRING VALUE under <paramref name="element"/> carrying a partition signal, bounded
    /// by <see cref="MaxHintChars"/> — the hint's own bound, not <see cref="MaxValueChars"/>, because
    /// this is the whole hint rather than a value spliced into one (a spec-review finding: the
    /// precise relay was capped at 120 characters while the coarse raw-text fallback got 300).
    /// </summary>
    private static string? FindSignalString(JsonElement element, int depth, ref int budget)
    {
        if (depth > MaxObservationDepth || budget <= 0)
        {
            return null;
        }

        budget--;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                // Tested on the DECODED text and sanitised for output. The signal words are ASCII, so
                // sanitisation cannot change whether one matched.
                //
                // TRIMMED BEFORE CAPPING, matching the raw-text fallback in FindPartitionText — and
                // not merely for tidiness. A value of 300+ leading spaces followed by "partition"
                // satisfies CarriesPartitionSignal, and capping it FIRST yields 300 spaces (0x20 is
                // printable ASCII, so Sanitise leaves it), which VerdictReason.NormaliseHint then
                // rejects as an empty hint — throwing ArgumentException out of ClassifyStep and
                // turning one malformed observation into a failed tool call on an already-failing
                // run. Trimming first means the cap always keeps the sentence, never the padding.
                var text = element.GetString();
                return text is not null && CarriesPartitionSignal(text)
                    ? Sanitise(Cap(text.Trim(), MaxHintChars))
                    : null;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FindSignalString(property.Value, depth + 1, ref budget) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindSignalString(item, depth + 1, ref budget) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    // ── Observation evidence ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The expected/observed pair an observation carries, if any — the single read that feeds BOTH
    /// the assertion rule (both values present) and the capture-unmet rule (an expected name whose
    /// observed value is literal <c>null</c>).
    /// </summary>
    /// <param name="Expected">The expected value, rendered and sanitised; <see langword="null"/> when the observation named none.</param>
    /// <param name="Observed">The observed value, rendered and sanitised; <see langword="null"/> when the observation named none OR named it as JSON <c>null</c>.</param>
    /// <param name="ObservedIsNull">Whether an observed key was present and its value was literal JSON <c>null</c> — the discriminator <see cref="Observed"/> alone cannot carry.</param>
    private readonly record struct ObservationEvidence(string? Expected, string? Observed, bool ObservedIsNull)
    {
        /// <summary>
        /// The observation SHAPE the capture-unmet rule keys on: a declared expectation whose
        /// observed value the engine recorded as literal <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <b>A necessary condition, not a sufficient one.</b> The shape is taken from this repo's
        /// own fixtures (<c>{"expected":"orderId","got":null}</c> —
        /// <c>Run/GetStepTimelineOrchestratorTests.cs:218</c>,
        /// <c>RealGetStepTimelineMcpTests.cs:100</c>) rather than invented here, but in BOTH of those
        /// it depicts a mid-RETRY poll miss (the first is attempt 3 of a poll that passes on attempt
        /// 4) — NOT a capture that resolved to nothing. It only means capture-unmet on a step that
        /// never polled, which is why <see cref="ClassifyCaptureUnmet"/> gates on the attempt list as
        /// well; see that method for the full account. It stays distinct from an assertion mismatch
        /// by construction either way: an assertion has TWO values that differ, this has one value
        /// and an absence.
        /// </remarks>
        public bool IsCaptureUnmet => Expected is not null && ObservedIsNull;
    }

    private static ObservationEvidence ReadEvidence(string? observation)
    {
        if (string.IsNullOrWhiteSpace(observation))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(observation);
            var budget = MaxObservationNodes;
            return FindEvidence(document.RootElement, 0, ref budget);
        }
        catch (JsonException)
        {
            // An observation capped mid-document at parse time is no longer valid JSON. Fail closed:
            // no evidence, hence no classification, rather than a guess scraped out of a fragment.
            return default;
        }
    }

    /// <summary>
    /// Finds the first object carrying an expected value together with an observed one (or an
    /// explicit <c>null</c>), descending into nested objects and arrays.
    /// </summary>
    /// <remarks>
    /// Nesting is not hypothetical: the pinned engine's own measured shape is
    /// <c>{"exists":{"expected":true,"actual":false}}</c>
    /// (<c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>), so a flat, top-level-only read would
    /// leave the assertion rule dead on the shape the engine actually writes.
    /// </remarks>
    private static ObservationEvidence FindEvidence(JsonElement element, int depth, ref int budget)
    {
        if (depth > MaxObservationDepth || budget <= 0)
        {
            return default;
        }

        budget--;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                string? expected = null;
                string? observed = null;
                var observedIsNull = false;
                var sawObserved = false;

                foreach (var property in element.EnumerateObject())
                {
                    if (expected is null && ExpectedKeys.Contains(property.Name))
                    {
                        expected = RenderScalar(property.Value);
                    }
                    else if (!sawObserved && ObservedKeys.Contains(property.Name))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            observedIsNull = true;
                            sawObserved = true;
                        }
                        else if (RenderScalar(property.Value) is { } rendered)
                        {
                            observed = rendered;
                            sawObserved = true;
                        }
                    }
                }

                if (expected is not null && (observed is not null || observedIsNull))
                {
                    return new ObservationEvidence(expected, observed, observedIsNull);
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindEvidence(property.Value, depth + 1, ref budget);
                    if (nested.Expected is not null)
                    {
                        return nested;
                    }
                }

                return default;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindEvidence(item, depth + 1, ref budget);
                    if (nested.Expected is not null)
                    {
                        return nested;
                    }
                }

                return default;

            default:
                return default;
        }
    }

    /// <summary>
    /// Renders a JSON scalar as hint text, or <see langword="null"/> when the element is not a scalar
    /// (an object/array/null carries no single value a hint can name).
    /// </summary>
    /// <remarks>
    /// <b>Re-sanitised, and that is a real boundary rather than belt-and-braces.</b>
    /// <see cref="SuiteEventParser"/> sanitises the observation's RAW JSON TEXT, where a control
    /// character appears as the six printable characters of a <c>\uXXXX</c> escape and therefore
    /// passes through untouched. <see cref="JsonElement.GetString"/> DECODES that escape back into a
    /// real control character — so a value extracted here would re-introduce exactly what
    /// <see cref="TextSanitiser"/> exists to keep out of a terminal. Running the decoded value through
    /// it again closes that.
    /// </remarks>
    private static string? RenderScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() is { } text ? Sanitise(Cap(text, MaxValueChars)) : null,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
            Sanitise(Cap(element.GetRawText(), MaxValueChars)),
        _ => null,
    };

    // ── Shared helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every engine-supplied fragment reaching a hint passes through here.
    /// </summary>
    /// <remarks>
    /// For text decoded out of JSON this is load-bearing (see <see cref="RenderScalar"/>); for text
    /// the parser already sanitised it is idempotent and, on already-printable ASCII,
    /// allocation-free — <see cref="TextSanitiser.SanitiseForDisplay"/> returns its argument
    /// unchanged unless it actually has something to escape. Applying it uniformly converts "the
    /// parser already did it" from a prose claim into a structural property of this file (a
    /// security-review finding).
    /// </remarks>
    private static string Sanitise(string text) => TextSanitiser.SanitiseForDisplay(text);

    /// <summary>
    /// Appends a full stop only when the text does not already end in terminal punctuation — engine
    /// detail text sometimes ends in one and sometimes does not, and neither a doubled full stop nor
    /// a sentence that just stops is acceptable in a snapshot-tested hint.
    /// </summary>
    private static string EndSentence(string text) =>
        text.Length > 0 && text[^1] is '.' or '!' or '?' or ':' ? text : text + ".";

    private static string Cap(string text, int maxChars) => text.Length > maxChars ? text[..maxChars] : text;
}
