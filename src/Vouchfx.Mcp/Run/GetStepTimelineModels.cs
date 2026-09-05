using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — get_step_timeline models (Sprint 3 / US-S3-06; spec §5.10).
//
// Spec §5.10 fixes the shapes:
//
//   interface GetStepTimelineInput  { runId: string; specPath: string; stepId: string; }
//   interface Attempt               { n: number; at: string; delayMs: number;
//                                     outcome: "matched" | "unmatched" | "error";
//                                     observed?: string; error?: string; }
//   interface GetStepTimelineOutput { meta: ToolMeta; stepId: string; verifyMode: "ONCE" | "RETRY";
//                                     timeoutMs: number; attempts: Attempt[]; conclusion: string; }
//
// `meta` is NOT a field here: StructuredToolResult.Success stamps it through the one choke point
// every tool uses, and a payload carrying its own top-level `meta` is REJECTED there.
//
// ---------------------------------------------------------------------------------------------
// Vocabulary: `outcome` is this tool's OWN three-value enum and nothing else's
// ---------------------------------------------------------------------------------------------
//
// sprint-00-overview.md §5 treats vocabulary conflation as a defect in the story, and this tool sits
// exactly where two other vocabularies meet, so the three are named here once:
//
//   * The FOUR-WAY VERDICT TAXONOMY — Pass / Fail / EnvironmentError / Inconclusive (RunVerdict) —
//     describes how a STEP, a SCENARIO or a RUN ended. It never appears on an attempt.
//   * The WIRE TOKENS — PASS / FAIL / ENV_ERROR / INCONCLUSIVE — are what the engine writes into the
//     event stream, and what get_run_events relays untouched. They never appear on this tool's
//     output at all.
//   * THIS TOOL'S ATTEMPT OUTCOME — matched / unmatched / error (StepAttemptOutcome below) —
//     describes what ONE POLL of a RETRY step established: it found what it was looking for, it did
//     not, or it could not tell. Three values, lower-case, and deliberately not a subset or a
//     re-spelling of either list above.
//
// GetStepTimelineOrchestrator.MapAttemptOutcome is the single mapping site from the first vocabulary
// to the third, and it records why each wire token folds where it does.
//
// ---------------------------------------------------------------------------------------------
// How this tool reports its upstream gap: PER FIELD, not with a payload-level `partial` marker
// ---------------------------------------------------------------------------------------------
//
// Spec §5.10 types fields this build cannot source (see GetStepTimelineOrchestrator's remarks for which,
// and for the measurements that decided each). A reviewer's open question was whether the payload should
// additionally carry a top-level marker — `partial: true`, or similar — announcing "this result is not
// everything the spec's shape promises". It deliberately does not, and the decision is recorded here so
// the next story does not re-open it as an oversight:
//
//   * The nulls are ALREADY self-describing. Each unsourceable field is written as an EXPLICIT null with
//     its reason documented on the field itself, rather than omitted — so a host reading `timeoutMs: null`
//     learns "the source carries no value this build reads", which is precisely what a marker would say,
//     at the exact field the gap concerns. A payload-level boolean is strictly coarser: it says something
//     is missing without saying what, and a host would still have to inspect every field to find out.
//   * A marker would be permanently true and therefore carry no information. Every successful result from
//     this tool has the same gap, so `partial` would never vary — an invariant dressed up as data, which
//     a host would learn to ignore within one call.
//   * `truncated`/`omittedAttemptCount` already occupy the "this response is not everything" slot for the
//     thing that DOES vary — what the response BUDGET dropped — and they are named identically to
//     get_run_events' so a host learns one rule. Adding a second, differently-meaning completeness flag
//     beside them invites exactly the conflation those names were chosen to avoid.
//
// Upstream ask U7 is the tracker for the gap itself. No `partial` field is added.

/// <summary><c>get_step_timeline</c>'s arguments, as the caller sent them — unvalidated.</summary>
/// <param name="RunId">The run whose event stream holds the timeline, as recorded in the run registry.</param>
/// <param name="SpecPath">
/// The suite the step belongs to. Validated against the run's own <c>specPaths</c> — see
/// <see cref="GetStepTimelineOrchestrator"/> for the full adjudication of what this argument can and
/// cannot mean, and for the <see cref="GetStepTimelineResult.SpecPathAttributed"/> flag that says
/// which of the two applied.
/// </param>
/// <param name="StepId">The step whose attempt timeline is wanted. Matched exactly (ordinally).</param>
public sealed record GetStepTimelineRequest(string? RunId, string? SpecPath, string? StepId);

/// <summary>
/// Spec §5.10's <c>Attempt.outcome</c> union, as string constants — this tool's OWN three-value
/// vocabulary. See this file's header for the two vocabularies it must never be confused with.
/// </summary>
public static class StepAttemptOutcome
{
    /// <summary>The attempt's assertion held: this poll found what the step was waiting for.</summary>
    public const string Matched = "matched";

    /// <summary>
    /// The attempt ran and its assertion did not hold — the ordinary state of every RETRY poll before
    /// the last one. <b>Not a failure of the run</b>: under <c>verifyMode: RETRY</c> the engine expects
    /// unmatched attempts and keeps polling, so an unmatched attempt beside a passing step is normal.
    /// </summary>
    public const string Unmatched = "unmatched";

    /// <summary>
    /// The attempt yielded no match/no-match determination at all — the engine reported an
    /// infrastructure failure or an indeterminate result for it, or reported nothing this server can
    /// classify. See <c>GetStepTimelineOrchestrator.MapAttemptOutcome</c> for exactly which inputs land
    /// here and why, and <see cref="StepTimelineAttempt.Error"/> for the per-attempt explanation that
    /// always accompanies this value.
    /// </summary>
    public const string Error = "error";
}

/// <summary>Spec §5.10's <c>verifyMode</c> union, as string constants.</summary>
/// <remarks>
/// <b>These are spec §5.10's tokens, and they are NOT the suite DSL's.</b> The vouchfx language's own
/// <c>verifyMode</c> takes <c>IMMEDIATE</c> (the default) or <c>RETRY</c> — see
/// <c>vendored/language-reference.md</c> — so <c>ONCE</c> is a word that appears in the spec's tool
/// contract and nowhere in any suite this server would validate. It is used here because US-S3-06's
/// acceptance criteria and Gherkin name it literally, and because this field is a statement about what
/// the RUN evidenced rather than about what the suite declared (see
/// <see cref="GetStepTimelineResult.VerifyMode"/>). The mismatch is flagged for spec adjudication; the
/// tool's own description warns a host not to copy the value into a suite.
/// </remarks>
public static class StepVerifyMode
{
    /// <summary>Exactly one attempt was recorded for this step in this run.</summary>
    public const string Once = "ONCE";

    /// <summary>More than one attempt was recorded — which only engine-owned polling produces.</summary>
    public const string Retry = "RETRY";
}

/// <summary>Spec §5.10's <c>Attempt</c>: one poll of one step, as the run's event stream recorded it.</summary>
/// <param name="N">
/// The engine's own one-based attempt counter for this step (<c>step-attempt.attempt</c>), relayed —
/// NOT this list's index. A duplicate or out-of-order emission therefore shows up as a repeated or
/// non-monotonic <c>n</c> rather than being silently renumbered into a tidy sequence that the events
/// file does not support.
/// </param>
/// <param name="At">
/// Spec §5.10's absolute timestamp for this attempt, relayed from the event's own <c>ts</c> property —
/// <b>populated against the pinned engine</b>, and <see langword="null"/> only for a stream that
/// carries neither <c>ts</c> nor <c>at</c>.
/// <para>
/// <b>MEASURED, and an earlier version of this documentation asserted the opposite.</b> It said this
/// field was "<see langword="null"/> at the currently pinned engine, always", inferred from the story's
/// synthetic fixtures — none of which emitted a timestamp — rather than from a real run. A live probe
/// against the pinned engine (<c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>, which records the
/// verbatim lines) shows every event it writes carries <c>ts</c>, <c>step-attempt</c> included:
/// <c>"ts":"2026-09-05T22:21:12.3829238+00:00"</c>, 33 characters. The relay was already correct and is
/// unchanged; only this description was wrong.
/// </para>
/// <para>
/// <b>What it is NOT: a per-attempt instant.</b> The engine stamps <c>ts</c> when it renders its
/// buffered report rather than when the attempt happened, so every event in one file shares a handful of
/// identical values (measured: 15 events, 3 distinct <c>ts</c>, across a run whose polling window alone
/// was ten seconds). It is relayed verbatim because it is what the source says and this server does not
/// second-guess engine-written values — but a host must NOT difference two <c>at</c> values to time
/// anything. <see cref="TMs"/> is what orders and times the timeline.
/// </para>
/// <para>
/// <b>Never synthesised.</b> Had the field been absent, the obvious reach — the run registry's
/// <c>startedAt</c> plus <c>tMs</c> — would have produced a plausible-looking ISO-8601 instant resting
/// on an unverified assumption about what <c>tMs</c> measures, and a fabricated timestamp is worse than
/// no timestamp precisely because it does not look fabricated. That rule still governs: what is written
/// here is the engine's own string, capped and sanitised, or an explicit null.
/// </para>
/// </param>
/// <param name="DelayMs">
/// Spec §5.10's backoff delay before this attempt — <see langword="null"/>, always, and
/// <b>STRUCTURALLY so</b>: the event stream carries no inter-attempt delay at all, on any event type,
/// so there is nothing to relay and no shape of engine output that would populate this field without a
/// contract change. Contrast <see cref="At"/>, whose absence was an empirical claim and turned out to
/// be false.
/// <para>
/// <b>The derivation that was available is now measurably wrong, not merely unverified.</b> Deriving a
/// delay from consecutive <see cref="TMs"/> values requires knowing whether <c>tMs</c> is cumulative
/// (elapsed since the step began) or per-attempt (this attempt's own duration); the engine's field
/// documentation available to this repository says only "elapsed wall-clock time", which does not
/// settle it. A live probe does: the eight attempts of a real ten-second RETRY window report
/// 6, 5, 6, 19, 18, 6, 6, 6 ms — non-monotonic, and summing to a fraction of the window — so <c>tMs</c>
/// is PER-ATTEMPT duration and consecutive differences are not backoffs by any reading.
/// <see cref="TMs"/> is reported instead, verbatim, so a host can see the timeline's shape without this
/// server asserting an interpretation of it.
/// </para>
/// </param>
/// <param name="TMs">
/// The engine's own <c>tMs</c> for this attempt, relayed verbatim. <b>Additive</b> — spec §5.10 does
/// not list it — and named exactly as the engine names it, so nobody mistakes it for one of the spec's
/// own fields. It is what makes a timeline with no <see cref="At"/> and no <see cref="DelayMs"/> still
/// ordered in time.
/// </param>
/// <param name="Outcome">
/// One of <see cref="StepAttemptOutcome"/>'s three literals — never the four-way verdict taxonomy and
/// never a wire token. See this file's header.
/// </param>
/// <param name="Observed">
/// This attempt's own <c>observation</c> evidence — a diff, a matched count, a response excerpt — as
/// sanitised JSON text, or <see langword="null"/> when the event carried none or when the response
/// budget dropped the text (<see cref="GetStepTimelineResult.ObservedCapped"/> says which).
/// <b>Engine-redacted already</b>: this server never re-redacts and never resolves a
/// <c>${secret:…}</c>, it only bounds and control-character-escapes what the engine wrote.
/// </param>
/// <param name="Error">
/// Why this attempt reached no determination, populated for exactly the attempts whose
/// <see cref="Outcome"/> is <see cref="StepAttemptOutcome.Error"/> and omitted for every other.
/// Carries the event's own <c>error</c> property when the stream has one, and otherwise this server's
/// own one-line statement of what it read — including the verbatim wire token, when there was one it
/// could not classify. Omitted from the JSON when <see langword="null"/>, matching spec §5.10's
/// optional marker on this field.
/// </param>
public sealed record StepTimelineAttempt(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("at")] string? At,
    [property: JsonPropertyName("delayMs")] long? DelayMs,
    [property: JsonPropertyName("tMs")] long TMs,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Observed,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error);

/// <summary>Spec §5.10's <c>GetStepTimelineOutput</c>, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="SpecPath">
/// The <c>specPath</c> the caller named, echoed back sanitised and capped. <b>Additive</b> (spec §5.10
/// does not list it) and kept because <see cref="SpecPathAttributed"/> is unreadable without it: a flag
/// saying "this timeline could not be attributed to the suite you named" needs to name the suite.
/// </param>
/// <param name="StepId">The step this timeline is about, exactly as it appears in the run's event stream.</param>
/// <param name="VerifyMode">
/// How this step was verified <b>in this run, as the event stream evidences it</b> — one of
/// <see cref="StepVerifyMode"/>'s two literals, or <see langword="null"/> when the stream recorded no
/// attempt at all for the step and there is therefore nothing to evidence.
/// <para>
/// <b>This is not a reading of the suite file, and the distinction matters.</b> This server will not
/// open the suite to find a declared value: the file on disk today is not necessarily the file that ran
/// (nothing pins a content hash into the registry), so a value read from it would be an assertion about
/// the run sourced from something that is not the run. What the stream establishes instead is the
/// number of attempts, and more than one attempt is only producible by engine-owned polling — so
/// <c>RETRY</c> is a fact about the run. <c>ONCE</c> is the honest name for the complementary case
/// ("this step was verified once in this run") and is deliberately NOT a claim that the suite declared
/// <c>verifyMode: IMMEDIATE</c>: a RETRY step that matched on its first poll is indistinguishable here,
/// and asserting otherwise would be a guess dressed as a reading. See <see cref="StepVerifyMode"/> for
/// why the token is <c>ONCE</c> rather than the DSL's own <c>IMMEDIATE</c>, and note the tool
/// description tells hosts not to copy it into a suite.
/// </para>
/// <para>
/// <b>The stream does carry the DECLARED mode, on an event type this build does not parse</b> —
/// measured, and an earlier version of this paragraph flatly denied it ("the event stream carries no
/// <c>verifyMode</c> field"). The engine's <c>step-started</c> event carries it verbatim
/// (<c>"verifyMode":"RETRY"</c>, beside the <c>timeoutMs</c> <see cref="TimeoutMs"/> discusses). That
/// does not change what THIS field means or should mean — the declared mode and the run-evidenced one
/// are different facts, and a tool answering "what did this step actually do" wants the second — but it
/// does mean the first is available to a future story, and that such a story should add a separate,
/// separately-named field rather than re-source this one.
/// </para>
/// <para>
/// <b><see langword="null"/> is the ORDINARY shape for an IMMEDIATE step, not an edge case</b>
/// (measured, and it settles a reviewer's open question). The pinned engine emits no
/// <c>step-attempt</c> event at all for a single-attempt IMMEDIATE step — a real run of one produced a
/// <c>step-started</c> and a <c>step-completed</c> line and nothing between them — so such a step
/// reaches a host here with an empty <see cref="Attempts"/> list and a null <c>verifyMode</c>.
/// <c>ONCE</c> is consequently reported for a narrower population than its name suggests: a step that
/// really did record exactly one attempt event, which in practice means a RETRY step that matched on
/// its first poll. Both shapes are correct and neither is an error; a host asking "was this step
/// retried?" should read <c>RETRY</c> as yes and treat <c>ONCE</c> and <c>null</c> alike as no.
/// </para>
/// </param>
/// <param name="TimeoutMs">
/// Spec §5.10's per-step timeout — <see langword="null"/>, always, from <b>this build</b>.
/// <para>
/// <b>The event stream DOES carry it, on an event type this server does not parse</b> — measured, and
/// an earlier version of this documentation said the v1 contract simply did not have it. The engine's
/// <c>step-started</c> event carries <c>timeoutMs</c> (and the suite's declared <c>verifyMode</c>
/// beside it): <c>{"type":"step-started",…,"verifyMode":"RETRY","timeoutMs":10000}</c>, from a real run
/// against the pinned engine. <see cref="SuiteEventParser"/> handles four event types —
/// <c>step-attempt</c>, <c>step-completed</c>, <c>scenario-completed</c>, <c>environment-error</c> —
/// and <c>step-started</c> is not among them, so nothing in this server reads that line today. The null
/// is therefore an honest statement about what this build sources, not about what the contract offers,
/// and closing it is an available follow-up (it widens the SHARED parser, which three other tools also
/// consume) rather than an upstream ask.
/// </para>
/// <para>
/// What remains refused either way is the DERIVATION: the nearest derivable quantity — the largest
/// <c>tMs</c> observed — is how long the step actually took, a different fact that would be actively
/// misleading under this field's name. A host that needs the declared timeout today can read the suite
/// with <c>validate_suite</c>/<c>get_schema</c>, which is where suite content legitimately comes from,
/// or read the raw <c>step-started</c> line through <c>get_run_events</c>, which relays every event type
/// untouched.
/// </para>
/// </param>
/// <param name="Attempts">
/// Every attempt the run's event stream recorded for this step, in file order — <b>the whole point of
/// the tool</b>. This list is NOT shortened by a per-tier attempt cap the way
/// <c>explain_run.notableSteps[].attempts</c> is; see <see cref="GetStepTimelineOrchestrator"/> for the
/// budget order that makes that true, and <see cref="Truncated"/> for the one bound that can still
/// shorten it.
/// </param>
/// <param name="Conclusion">
/// A short sentence naming how the step ended and what the timeline shows — derived from the step's own
/// <c>step-completed</c> verdict when the stream recorded one, and from the attempts alone when it did
/// not. <b>This is the one field on this payload that legitimately names the four-way verdict
/// taxonomy</b>, because a step's conclusion IS a verdict; the per-attempt <c>outcome</c> field is the
/// one that must never carry those words.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when what came back is not everything the run's stream held about this step —
/// <b>the same meaning <c>get_run_events.truncated</c> carries</b>, deliberately named the same so a
/// host learns one rule. Two reasons set it: the events file exceeded
/// <see cref="EventsFileReader.MaxEventsFileBytes"/> and was read only up to that cap (so later attempts
/// were never seen), or the response budget dropped attempts from the end of the list
/// (<see cref="OmittedAttemptCount"/> says how many, and that path is unreachable for any realistic
/// RETRY timeline — see <see cref="GetStepTimelineOrchestrator"/>'s measured figures).
/// </param>
/// <param name="OmittedAttemptCount">
/// How many attempts the response budget dropped from the END of <see cref="Attempts"/>; <c>0</c> for
/// every ordinary timeline. Non-zero implies <see cref="Truncated"/>, but not the reverse — a
/// byte-capped events file truncates without omitting anything this server ever saw.
/// </param>
/// <param name="ObservedCapped">
/// <see langword="true"/> when at least one attempt's <see cref="StepTimelineAttempt.Observed"/> text
/// was shortened or dropped to fit the response budget. The attempt itself is still present with its
/// <c>n</c>, <c>tMs</c> and <c>outcome</c> intact — this server gives up EVIDENCE TEXT before it gives
/// up an entry in the timeline, which is the inversion of <c>explain_run</c>'s tier order that the
/// whole tool exists for.
/// </param>
/// <param name="SpecPathAttributed">
/// Whether the timeline can be attributed to the <see cref="SpecPath"/> the caller named.
/// <para>
/// <see langword="true"/> when the run covered exactly one suite: every event in the stream came from
/// it, so the attribution is a certainty rather than an assumption. <see langword="false"/> when the
/// run covered several — a multi-suite <c>run_suite</c> call concatenates each suite's stream into one
/// file and <b>no line carries a suite attribution</b> (US-S3-02's documented trade), so a step id
/// appearing in two of those suites yields one merged timeline this server cannot split. The
/// <c>specPath</c> is still validated against the run's recorded set in both cases; when this flag is
/// false it is informational rather than a filter, and the <c>conclusion</c> says so in words.
/// </para>
/// </param>
public sealed record GetStepTimelineResult(
    [property: JsonPropertyName("specPath")] string SpecPath,
    [property: JsonPropertyName("stepId")] string StepId,
    [property: JsonPropertyName("verifyMode")] string? VerifyMode,
    [property: JsonPropertyName("timeoutMs")] long? TimeoutMs,
    [property: JsonPropertyName("attempts")] IReadOnlyList<StepTimelineAttempt> Attempts,
    [property: JsonPropertyName("conclusion")] string Conclusion,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("omittedAttemptCount")] int OmittedAttemptCount,
    [property: JsonPropertyName("observedCapped")] bool ObservedCapped,
    [property: JsonPropertyName("specPathAttributed")] bool SpecPathAttributed);

/// <summary>
/// What one <c>get_step_timeline</c> call produced — a closed union (the private constructor confines
/// derivation to the cases nested here), mirroring <see cref="GetRunEventsOutcome"/> so the tool's
/// switch maps each case to exactly one <c>VFX-E-</c> code and the compiler enumerates the cases when
/// one is added.
/// </summary>
public abstract record GetStepTimelineOutcome
{
    private GetStepTimelineOutcome()
    {
    }

    /// <summary>The timeline was built.</summary>
    public sealed record Found(GetStepTimelineResult Result) : GetStepTimelineOutcome;

    /// <summary>An argument was missing, blank, or over a bound — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : GetStepTimelineOutcome;

    /// <summary>No run with that id is in the registry — <c>VFX-E-1505</c>.</summary>
    public sealed record RunNotFound(string Message) : GetStepTimelineOutcome;

    /// <summary>The run exists but never covered the named suite — <c>VFX-E-1509</c>.</summary>
    public sealed record SpecPathNotInRun(string Message) : GetStepTimelineOutcome;

    /// <summary>The run's event stream records no step with that id — <c>VFX-E-1510</c>.</summary>
    public sealed record StepNotInRun(string Message) : GetStepTimelineOutcome;

    /// <summary>The run's events path is a UNC location or escapes the workspace — <c>VFX-E-1001</c>.</summary>
    public sealed record InvalidPath(string Message) : GetStepTimelineOutcome;

    /// <summary>The run exists but its events file is gone — <c>VFX-E-1004</c>.</summary>
    public sealed record EventsFileNotFound(string Message) : GetStepTimelineOutcome;

    /// <summary>The events file exists but could not be read — <c>VFX-E-1005</c>.</summary>
    public sealed record EventsFileUnreadable(string Message) : GetStepTimelineOutcome;
}
