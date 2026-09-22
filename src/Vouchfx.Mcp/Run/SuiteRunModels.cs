using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

// ---------------------------------------------------------------------------
// SuiteEventParser's output
// ---------------------------------------------------------------------------

/// <summary>One step's final outcome, as reported by a <c>step-completed</c> event (§14.4).</summary>
/// <param name="StepId">The step's own identifier, sanitised for display.</param>
/// <param name="Verdict">One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> (<see cref="RunVerdict"/>'s own names).</param>
/// <param name="DurationMs">Total wall-clock duration of all attempts combined, in milliseconds.</param>
/// <param name="AttemptCount">
/// The highest attempt number observed across this step's <c>step-attempt</c> events (RETRY's
/// polling timeline, folded down to a count) — <c>1</c> for an IMMEDIATE step or one whose attempt
/// events were not captured for any reason.
/// </param>
/// <param name="Observation">
/// The step's own <c>observation</c> evidence (a diff, matched count, or similar — sanitised, capped
/// raw JSON text), used by REQ-007's <c>explain_run</c> diagnosis. <see langword="null"/> when the
/// step-completed event carried none. <c>run_suite</c> itself never reads this field.
/// </param>
public sealed record StepOutcome(string StepId, string Verdict, long DurationMs, int AttemptCount, string? Observation = null);

/// <summary>
/// One individual attempt of a step, as reported by a <c>step-attempt</c> event (§14.4) — RETRY's
/// polling timeline, used by REQ-007's <c>explain_run</c> diagnosis. <c>run_suite</c> itself never
/// reads this; only <see cref="StepOutcome.AttemptCount"/> (a fold of these, computed separately).
/// </summary>
/// <param name="Attempt">The one-based attempt counter.</param>
/// <param name="TMs">Elapsed wall-clock time for this attempt, in milliseconds.</param>
/// <param name="Outcome">
/// This attempt's own resolved outcome, rendered as one of <see cref="RunVerdict"/>'s PascalCase
/// names; <see langword="null"/> for a mid-RETRY poll with no outcome yet <b>and</b> for an outcome
/// token this server does not recognise. <see cref="RawOutcome"/> is what tells those two apart.
/// </param>
/// <param name="Observation">This attempt's own observation evidence (sanitised, capped raw JSON text); <see langword="null"/> when none was carried.</param>
/// <param name="RawOutcome">
/// The VERBATIM <c>outcome</c> wire token the event carried (sanitised and capped like every other
/// label), or <see langword="null"/> when the event carried no <c>outcome</c> property at all.
/// <para>
/// <b>Added by US-S3-06, and it closes a real ambiguity rather than duplicating
/// <see cref="Outcome"/>.</b> <see cref="Outcome"/> is <c>ParseWireToken(...)?.ToString()</c>, which
/// collapses two genuinely different facts to <see langword="null"/>: "the engine reported no outcome
/// for this attempt" and "the engine reported a token this build does not know" (the v1 event
/// contract is additive-frozen, so the second is a supported forward-compatibility state, not
/// corruption). <c>get_step_timeline</c> has to distinguish them — one is an attempt still in flight
/// when the stream ends, the other is an attempt this server cannot classify — so the raw token is
/// kept beside the parsed one. <c>explain_run</c>/<c>diagnose_run</c>/<c>run_suite</c> read only
/// <see cref="Outcome"/> and are unaffected.
/// </para>
/// </param>
/// <param name="Error">
/// The event's own <c>error</c> property, sanitised and capped, or <see langword="null"/> when it
/// carried none.
/// <para>
/// <b>Read opportunistically, and MEASURED ABSENT.</b> Spec §5.10's <c>Attempt.error?</c> field wants
/// a per-attempt error string; a real RETRY run against the pinned engine
/// (<c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>) shows <c>step-attempt</c> carrying exactly
/// <c>v</c>, <c>schemaVersion</c>, <c>type</c>, <c>ts</c>, <c>runId</c>, <c>stepId</c>,
/// <c>attempt</c>, <c>tMs</c>, <c>outcome</c> and <c>observation</c> — no <c>error</c> among them — so
/// in practice this is <see langword="null"/> today. It is probed anyway, for exactly the reason
/// <c>GetRunEventsOrchestrator.ResolveEventSchemaVersion</c> probes <c>eventSchemaVersion</c> first
/// despite the engine not emitting it: the contract is additive-frozen, so a future engine that starts
/// carrying the field needs no change here, and reading a property that is absent costs nothing and
/// asserts nothing.
/// </para>
/// </param>
/// <param name="At">
/// The event's own absolute timestamp (<c>ts</c>, else <c>at</c>), sanitised and capped, or
/// <see langword="null"/> when it carried neither.
/// <para>
/// <b>MEASURED PRESENT at the pinned engine — the same probe that found <see cref="Error"/> absent
/// found this on every line.</b> Every event the engine writes carries <c>ts</c>, a 33-character
/// ISO-8601 instant with offset, so this is a real value in production; an earlier version of this
/// documentation asserted the opposite from synthetic fixtures alone. It is not a per-attempt instant
/// (the engine stamps it as it renders its buffered report, so a file's events share a handful of
/// identical values) — <see cref="TMs"/> is what orders a timeline. See
/// <c>GetStepTimelineOrchestrator</c> and <c>StepTimelineAttempt.At</c> for the full account, including
/// why an absent timestamp would still never be synthesised from the run's <c>startedAt</c> plus
/// <see cref="TMs"/>.
/// </para>
/// </param>
public sealed record StepAttempt(
    int Attempt,
    long TMs,
    string? Outcome,
    string? Observation,
    string? RawOutcome = null,
    string? Error = null,
    string? At = null);

/// <summary>One <c>environment-error</c> event (§12.1, §14.4) — always distinct from a <c>Fail</c>.</summary>
/// <param name="ErrorKind">The <c>OrchestrationErrorKind</c> name the engine reported (e.g. <c>"ImagePull"</c>, <c>"Provision"</c>), sanitised for display.</param>
/// <param name="ResourceName">The Aspire resource name the failure concerns, sanitised for display.</param>
/// <param name="Detail">A trimmed summary of the underlying failure, sanitised for display; <see langword="null"/> when the engine reported none.</param>
public sealed record EnvironmentErrorSummary(string ErrorKind, string ResourceName, string? Detail);

/// <summary>The whole events file, reduced to what <see cref="RunSuiteOrchestrator"/> and <c>ExplainRunOrchestrator</c> need.</summary>
/// <param name="AggregateVerdict">
/// The suite's overall verdict, computed by elevating every <c>scenario-completed</c> event's own
/// verdict (§12.1 precedence — see <see cref="RunVerdictExtensions.Elevate"/>). <see langword="null"/>
/// when no <c>scenario-completed</c> event was found at all (e.g. the run failed before any scenario
/// could start) — <see cref="RunSuiteOrchestrator"/> falls back to the CLI's own exit code in that
/// case; <c>ExplainRunOrchestrator</c> (which has no exit code to fall back to) instead tries
/// elevating from <see cref="Steps"/>' own verdicts.
/// </param>
/// <param name="Steps">Every step's outcome, in the order its <c>step-completed</c> event appeared.</param>
/// <param name="EnvironmentErrors">Every <c>environment-error</c> event found, used to build a remediation hint.</param>
/// <param name="AttemptsByStepId">
/// Every <c>step-attempt</c> event, grouped by step id (the SAME sanitised id
/// <see cref="StepOutcome.StepId"/> uses) and kept in file order — REQ-007's RETRY timeline evidence.
/// A step with no recorded attempts (an IMMEDIATE step, or one whose attempt events were not
/// captured) simply has no entry here.
/// </param>
public sealed record SuiteRunSummary(
    RunVerdict? AggregateVerdict,
    IReadOnlyList<StepOutcome> Steps,
    IReadOnlyList<EnvironmentErrorSummary> EnvironmentErrors,
    IReadOnlyDictionary<string, IReadOnlyList<StepAttempt>> AttemptsByStepId);

// ---------------------------------------------------------------------------
// RunSuiteOrchestrator's own result payloads
// ---------------------------------------------------------------------------

/// <summary>
/// REQ-006's <c>run_suite</c> result: the suite actually ran (to completion, or to a bounded
/// cancellation/timeout — see <see cref="Cancelled"/>/<see cref="TimedOut"/>).
/// </summary>
/// <param name="RunId">
/// The id this run was registered under — spec §5.7's <c>RunSummary.runId</c>, and the value
/// <c>get_run_events</c> (and, from US-S3-03, <c>get_run_status</c>/<c>list_runs</c>) takes as its
/// <c>runId</c> argument.
/// <para>
/// <b>Added in US-S3-05, and the omission it fixes was real:</b> until this field existed, this
/// server minted an id, wrote it into the registry, named it in <c>VFX-E-1501</c>'s <c>details</c>
/// when it REFUSED a call — and never told the caller of a SUCCESSFUL run what it was. A host had no
/// in-band way to reach its own run's events at all. Additive, and taken while this package is still
/// unpublished, so it costs no consumer a migration.
/// </para>
/// <para>
/// <b><see langword="null"/> in exactly the case <see cref="EventsFilePath"/> is empty</b>: the
/// call's <c>timeoutSeconds</c> budget expired during path expansion or the pre-flight, before any
/// run was registered. No id was minted, so there is none to report — and inventing one would name a
/// run that the registry will never have heard of, which is strictly worse than a null a host can
/// test for. Written as an explicit <c>null</c> rather than omitted, matching this record's other
/// optional fields (<see cref="ExitCode"/>, <see cref="RemediationHint"/>) — one record should not
/// signal absence two different ways.
/// </para>
/// </param>
/// <param name="Verdict">
/// One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — always one of these
/// four, never conflated (§12.1). A cancelled or timed-out run is always reported as
/// <c>Inconclusive</c>, never <c>Fail</c> (EDGE-002).
/// </param>
/// <param name="ExitCode">
/// The vouchfx CLI's own process exit code, when it could be determined — and only when the run
/// covered EXACTLY ONE suite. A multi-suite run (US-S3-02's <c>paths</c>) spawns the CLI once per
/// suite, so there is no single exit code that describes it: reporting the last one, or the one
/// belonging to whichever suite happened to set the elevated verdict, would be an arbitrary choice
/// dressed up as a fact. <see langword="null"/> is the honest answer there, and
/// <see cref="Specs"/> is where per-suite outcomes live.
/// </param>
/// <param name="Cancelled">
/// <see langword="true"/> when the run ended because the CALLER's own cancellation fired (the MCP
/// request itself was cancelled) — distinct from <see cref="TimedOut"/>.
/// </param>
/// <param name="TimedOut">
/// <see langword="true"/> when the run ended because it did not complete within the effective
/// <c>timeoutSeconds</c> budget — distinct from <see cref="Cancelled"/>.
/// </param>
/// <param name="RemediationHint">
/// A short, actionable hint when this server has one, <see langword="null"/> otherwise. Three
/// sources, and a host should treat it as prose to show rather than a field to branch on:
/// <list type="bullet">
/// <item><description>
/// <c>EnvironmentError</c> (EDGE-001) — naming the failing resource from the events stream's own
/// <c>environment-error</c> events, or the Docker daemon when that is the most likely cause.
/// </description></item>
/// <item><description>
/// A TIMEOUT — stating that the run did not complete within the budget. (A run cancelled by its
/// caller gets none: the caller already knows why it stopped.) Usually seen on <c>Inconclusive</c>,
/// but see the note below on the multi-suite case.
/// </description></item>
/// <item><description>
/// An ENGINE ENVIRONMENT-CONFIGURATION DIAGNOSTIC with no scenario result (vouchfx-mcp#96) — the
/// engine's own diagnostic sentence, relayed verbatim behind a prefix stating exactly the two things
/// this server observed. In the measured rc.5 case (a dependency <c>env</c> entry naming an
/// engine-set variable) this is the ONLY explanation available anywhere: the engine writes no events
/// file, so <c>explain_run</c>/<c>diagnose_run</c>/<c>get_step_timeline</c> have nothing to read. See
/// <c>RunSuiteOrchestrator.BuildEngineRefusalHint</c>.
/// </description></item>
/// </list>
/// <b>Never populated for <c>Pass</c>. A <c>Fail</c> is never EXPLAINED by a hint</b> — a genuine test
/// failure is explained by its steps and by <c>diagnose_run</c> — <b>but a <c>Fail</c> can still
/// ARRIVE with one</b>, and the case is real rather than theoretical: in a multi-suite run where an
/// earlier suite failed and a later one exhausted the timeout budget,
/// <c>RunSuiteOrchestrator.BuildAbortedResult</c> elevates <c>Elevate(Fail, Inconclusive)</c> to
/// <c>Fail</c> (§12.1 ranks Fail above Inconclusive) and sets the timeout hint unconditionally.
/// <para>
/// <b>MULTI-SUITE SCOPING, which applies to BOTH of the last two sources.</b> A hint is produced by
/// ONE suite and the FIRST one produced is kept as the run's
/// (<c>RunSuiteOrchestrator.ExecuteRegisteredRunAsync</c>'s <c>remediationHint ??=</c>), so in a run
/// covering several suites the hint need not describe the run as a whole:
/// <list type="bullet">
/// <item><description>
/// the TIMEOUT hint says why the run STOPPED, not why the (possibly different, possibly failing)
/// suite that set the elevated verdict reached it;
/// </description></item>
/// <item><description>
/// the ENVIRONMENT-CONFIGURATION hint says "<i>a suite</i> produced no scenario result" — deliberately
/// indefinite — because a later suite may have run normally and filled <see cref="Steps"/>. The
/// refused one is identifiable in <see cref="Specs"/>: its <see cref="SpecRunOutcome.Outcome"/> is
/// <c>Inconclusive</c> with no steps.
/// </description></item>
/// </list>
/// Both are the same rule stated twice: this field is prose to show, scoped to one suite, never a
/// field to branch on and never a statement about the run's aggregate.
/// </para>
/// </param>
/// <param name="Steps">
/// Every step's outcome, across every suite this run covered, in the order the events file reports
/// them. Empty for a cancelled/timed-out run that reached no suite at all (EDGE-002). For a
/// single-suite run this is exactly what it always was; for a multi-suite one it is the
/// concatenation of <see cref="Specs"/>' own step lists, kept at the top level so a caller that
/// only ever read <c>steps</c> keeps working unchanged.
/// </param>
/// <param name="EventsFilePath">
/// The local path to this run's complete JSON Lines event stream — the same file a later
/// <c>explain_run</c> call is expected to read (see <c>CliPinVerifier</c>'s remarks). ONE file per
/// RUN, not per suite: a multi-suite run's per-suite streams are concatenated into it (see
/// <see cref="RunSuiteOrchestrator"/>'s remarks on the events layout and its trade-offs).
/// <para>
/// <b>EMPTY in exactly one case: the call's <c>timeoutSeconds</c> budget expired during path
/// expansion or the pre-flight, before any run was registered.</b> No run id was minted and no events
/// file was ever created, so there is no path to report and inventing one would hand a host a file
/// name that will never exist. See <see cref="RunSuiteOrchestrator"/>'s remarks on the whole-call
/// budget for why that case is reported as a timed-out RESULT rather than an error.
/// </para>
/// </param>
/// <param name="EventsTruncated">
/// <see langword="true"/> when the events file exceeded <see cref="EventsFileReader.MaxEventsFileBytes"/>
/// and was only read up to that many bytes before parsing — <see cref="Verdict"/> and
/// <see cref="Steps"/> are derived from whatever complete lines fit within the cap and may therefore
/// be incomplete. <see langword="false"/> (the default) for every ordinary run.
/// </param>
/// <param name="Specs">
/// One entry per suite this run covered, in run order — spec §5.7's <c>RunSummary.specs[]</c> shape
/// (US-S3-02). Present for every run, including a single-suite one, where it carries exactly one
/// entry whose <see cref="SpecRunOutcome.Outcome"/> equals <see cref="Verdict"/> and whose steps are
/// <see cref="Steps"/>: a caller should not have to branch on how many suites it asked for to read
/// the same information.
/// <para>
/// <b>Empty in exactly one case</b>, the same one <see cref="EventsFilePath"/> names: the call's
/// budget expired during path EXPANSION, so the suite set was never resolved and there is no path
/// this server could honestly attribute an outcome to. Once expansion has completed, a budget expiry
/// during the pre-flight still reports every resolved suite here with a <see langword="null"/>
/// outcome — "not run", the same shape a suite after an aborted one already gets.
/// </para>
/// </param>
public sealed record RunSuiteResult(
    string? RunId,
    string Verdict,
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string? RemediationHint,
    IReadOnlyList<StepOutcome> Steps,
    string EventsFilePath,
    IReadOnlyList<SpecRunOutcome> Specs,
    bool EventsTruncated = false);

/// <summary>
/// One suite's own outcome within a run — spec §5.7's <c>RunSummary.specs[]</c> element
/// (US-S3-02), minus the fields this server has no source for yet.
/// </summary>
/// <param name="Path">
/// The suite file this entry is about, as this server RESOLVED it: absolute, workspace-rebased, and
/// — when it arrived through a glob — the concrete file the pattern selected, never the pattern.
/// A caller correlating an outcome back to what it asked for needs the file, not the request.
/// </param>
/// <param name="Outcome">
/// One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c>, or
/// <see langword="null"/> for a suite that never ran — which happens only when an earlier suite's
/// cancellation or timeout ended the whole run before this one started. Spec §5.7 makes this field
/// optional for exactly that reason; a suite that did not run has no verdict, and inventing
/// <c>Inconclusive</c> for it would assert that the engine tried and could not decide.
/// </param>
/// <param name="Steps">This suite's own step outcomes, in events-file order. Empty for a suite that did not run.</param>
public sealed record SpecRunOutcome(string Path, string? Outcome, IReadOnlyList<StepOutcome> Steps)
{
    /// <summary>
    /// The suite file this entry is about — <b>sanitised for display</b>, and sanitised HERE so no
    /// construction site can forget to.
    /// </summary>
    /// <remarks>
    /// <b>A resolved suite path is third-party-authored text</b> (a security review's MINOR finding).
    /// Since US-S3-02 these paths can arrive through a GLOB, so the file NAME half is whatever
    /// happened to be on disk rather than anything the caller typed — and on Linux and macOS a file
    /// name may contain any byte except <c>/</c> and NUL, including ESC. Relayed raw into a tool
    /// result, a name carrying an ANSI sequence reaches whatever terminal or log renders the host's
    /// output. Every other caller- or engine-sourced string in a <c>run_suite</c> result already goes
    /// through <see cref="TextSanitiser.SanitiseForDisplay"/> (step ids, environment-error detail,
    /// relayed output lines); this field was the one that did not.
    /// <para>
    /// <b>Declared as a property over the positional parameter</b> — legal C# for a record, and the
    /// only shape that makes the transformation unconditional: the four construction sites in
    /// <see cref="RunSuiteOrchestrator"/> each pass a raw resolved path and none of them has to
    /// remember this rule. The RAW path is still what the engine is spawned against, because that
    /// comes from the orchestrator's own <c>suitePaths</c> list and never from this record — a
    /// sanitised path would not open.
    /// </para>
    /// <para>
    /// Not capped, deliberately: <c>SuitePathExpander</c> already bounds both the number of resolved
    /// paths and their total character count at the point of expansion, so the length half of the
    /// problem is settled before a path reaches here. Contrast
    /// <c>RunSuiteOutcome.SuiteInvalid.SuitePath</c>, which is deliberately raw and is rendered
    /// through <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c> at the tool boundary instead —
    /// there the path travels alone in an error message, here it travels in a bounded array.
    /// </para>
    /// </remarks>
    public string Path { get; init; } = TextSanitiser.SanitiseForDisplay(Path);
}

/// <summary>
/// EDGE-003's "suite-invalid, not run" result: <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>'s
/// pre-flight check rejected the suite before the CLI was ever spawned — the same
/// <see cref="ValidateSuiteResult"/> shape <c>validate_suite</c> itself returns, so an agent that
/// already knows that shape recognises this one immediately.
/// </summary>
/// <param name="Code">
/// Always <see cref="VfxCodeCatalogue.SuiteInvalid"/> — distinguishes this from
/// <see cref="RunSuiteResult"/> at a glance, and, being a <c>VFX-D-</c> code, states positively that
/// this payload is a DIAGNOSTIC returned as data on a successful call. US-S1-04 replaced the former
/// literal <c>"suite-invalid"</c> kind here; the field's cardinality is unchanged and this payload
/// still comes back through <c>StructuredToolResult.Success</c> with <c>isError</c> false, exactly as
/// it always has (that is the one existing "diagnostics are data" precedent the whole VFX-code split
/// was designed around, and it is guard-tested in <c>RealVfxCodeContractMcpTests</c>).
/// </param>
/// <param name="Path">
/// WHICH suite failed, as this server resolved it — capped and sanitised for display.
/// <b>Additive field</b> (a gatekeeper review's MAJOR finding): the pre-flight is all-or-nothing
/// across every suite a call names, so a forty-suite glob whose first bad file refuses the whole run
/// used to hand the caller a validation payload with no way at all to tell which file it was about.
/// The <see cref="ValidateSuiteResult"/> below does not carry the path itself — <c>validate_suite</c>
/// never needed it, because there the caller named the one file — so it is carried here instead of
/// being read out of the errors' messages.
/// <para>
/// Since vouchfx-mcp#78 this is specifically the FIRST invalid suite, which is exactly the suite this
/// field has always named (the pre-flight used to stop there). <see cref="InvalidSuites"/> is the
/// complete, bounded list; this field and <see cref="Validation"/> are kept so a host written against
/// the pre-#78 shape reads the same value it always did.
/// </para>
/// </param>
/// <param name="Validation">
/// The FIRST invalid suite's validation result; always <c>Valid: false</c> with at least one error.
/// Carried verbatim and UNCAPPED, exactly as before vouchfx-mcp#78 — <see cref="InvalidSuites"/>'s
/// own copy of the same suite's errors is the bounded one.
/// </param>
/// <param name="InvalidSuites">
/// Every suite the pre-flight determined to be INVALID, in the order the suites were validated —
/// vouchfx-mcp#78. When it carries any entry at all, the first one's
/// <see cref="InvalidSuiteReport.Path"/> equals <paramref name="Path"/>.
/// <para>
/// <b>It CAN be empty</b>, in exactly one situation: the response-size fit below shed every entry,
/// because the suite this payload is about carries more findings in the uncapped
/// <paramref name="Validation"/> field than the whole budget allows. <paramref name="OmittedInvalidSuiteCount"/>
/// then accounts for all of them, so the count is still exact — and the first suite's own findings are
/// not lost, since <paramref name="Validation"/> is what they were too large to fit BESIDE. An earlier
/// version of this documentation claimed the list was never empty; it is not, and the shed-all case is
/// measured in <c>RunSuiteInvalidPayloadBoundsTests</c>.
/// </para>
/// <para>
/// <b>The point of the field:</b> the pre-flight walks every suite on the success path anyway, so
/// collecting every failure costs no extra work — and a caller repairing a forty-suite glob that used
/// to need up to forty round trips now needs one. The all-or-nothing rule is untouched: a single
/// invalid suite still refuses the whole call and runs nothing.
/// </para>
/// <para>
/// <b>DIAGNOSTIC-ONLY, and that is a contract rather than an accident.</b> Every entry here is a suite
/// whose invalidity was DETERMINED — the pipeline read it, judged it, and can say what is wrong with
/// it. A suite whose validity could NOT be determined (missing, unreadable, out-of-workspace, or a
/// validation worker that timed out or failed) is a different fact and gets no entry at all: it is
/// counted in <paramref name="UndeterminedSuiteCount"/> instead. Three reasons, and the first is
/// normative — spec §4.4 gives an error result a single <see cref="VfxError"/> body, so a
/// "never determined" finding cannot be carried as an element of a data array without inventing a
/// second error channel; the code such a finding carries can be one this server does not catalogue
/// (it crosses a process boundary from the worker), and <c>ValidationOutcomeRenderer</c> fails those
/// CLOSED rather than relaying them as data; and the two facts are answers to different questions, so
/// a host must not have to guess which kind of entry it is holding.
/// </para>
/// <para>
/// <b>Bounded, and every bound is visible.</b> <see cref="RunSuiteOrchestrator.MaxReportedInvalidSuites"/>
/// bounds how many entries are COLLECTED, <c>RunSuiteTool.MaxErrorsPerInvalidSuite</c> bounds each
/// entry's own error list, and a measured byte fit at the tool boundary sheds trailing entries when
/// the serialised payload would exceed the response-size class every other bounded tool in this server
/// respects. <paramref name="OmittedInvalidSuiteCount"/> reports the total of the first and third;
/// <see cref="InvalidSuiteReport.OmittedErrorCount"/> reports the second, per entry.
/// </para>
/// </param>
/// <param name="OmittedInvalidSuiteCount">
/// How many DETERMINED-invalid suites are not described in <paramref name="InvalidSuites"/> —
/// <c>0</c> when it describes them all. Counts both the suites dropped at the collection cap and any
/// shed by the response-size fit, because a caller does not care which of those two bounds bit, only
/// how much of the answer it is not seeing.
/// </param>
/// <param name="UndeterminedSuiteCount">
/// How many further suites failed the pre-flight without its being able to say WHY in this payload's
/// terms — the "validity was never determined" class <paramref name="InvalidSuites"/> excludes.
/// <c>0</c> for the ordinary case. Covers both sides of the collection cap: the ones the tool boundary
/// classified out of the collected failures, and the ones the orchestrator classified as it dropped
/// them (see <see cref="RunSuiteOutcome.SuiteInvalid.UndeterminedPastCapCount"/>).
/// <para>
/// <b>Deliberately a SECOND counter rather than more of <paramref name="OmittedInvalidSuiteCount"/>.</b>
/// "I know this suite is invalid but ran out of room to tell you" and "I could not tell whether this
/// suite is valid" are different facts with different remedies — the first is repaired by reading the
/// entries and calling again, the second by fixing a file or a machine. One number covering both would
/// misreport each of them as the other.
/// </para>
/// <para>
/// <b>It is self-correcting across calls, which is why counting is enough.</b> These suites are never
/// the FIRST failure — when one is, the whole call takes the error leg instead and returns the full
/// <c>VFX-E-…</c> answer about it. So a caller that repairs the reported diagnostics and calls again
/// finds the undetermined one eventually promoted to first (immediately, unless
/// <paramref name="OmittedInvalidSuiteCount"/> is also non-zero — then there are still-undescribed
/// invalid suites ahead of it, and it takes as many repair rounds as it takes to clear them), and gets
/// the complete explanation then.
/// </para>
/// </param>
public sealed record RunSuiteInvalidPayload(
    string Code,
    string Path,
    ValidateSuiteResult Validation,
    IReadOnlyList<InvalidSuiteReport> InvalidSuites,
    int OmittedInvalidSuiteCount,
    int UndeterminedSuiteCount);

/// <summary>
/// One invalid suite inside <see cref="RunSuiteInvalidPayload.InvalidSuites"/> (vouchfx-mcp#78) — the
/// suite's path and the pre-flight's findings about it, both bounded for the wire.
/// </summary>
/// <param name="Path">
/// The suite this entry is about, capped and sanitised for display through
/// <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c> — the same rendering
/// <see cref="RunSuiteInvalidPayload.Path"/> goes through, applied at the same boundary.
/// </param>
/// <param name="Errors">
/// This suite's findings, capped at <c>RunSuiteTool.MaxErrorsPerInvalidSuite</c> entries in the order
/// the pre-flight produced them. Deliberately the SAME <see cref="SuiteValidationError"/> shape
/// <c>validate_suite</c> publishes, so an agent that can already read one finding can read these.
/// </param>
/// <param name="OmittedErrorCount">
/// How many of this suite's findings are not in <paramref name="Errors"/>; <c>0</c> when they all are.
/// A consumer must never have to infer incompleteness from a list length — the same cap-plus-count
/// shape <c>diagnose_run</c>'s <c>omittedProposalCount</c> and <c>get_run_artifacts</c>'
/// <c>omittedResourceCount</c> use.
/// </param>
public sealed record InvalidSuiteReport(
    string Path,
    IReadOnlyList<SuiteValidationError> Errors,
    int OmittedErrorCount);

/// <summary>
/// One suite that failed <c>run_suite</c>'s EDGE-003 pre-flight, paired with the pre-flight's own
/// answer about it (vouchfx-mcp#78).
/// </summary>
/// <param name="SuitePath">
/// The resolved suite path, RAW and uncapped — the tool boundary is what renders it (through
/// <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c>), so this type carries the fact rather than a
/// rendering of it. Same rule, and same reason, as <see cref="SpecRunOutcome.Path"/>'s contrast note.
/// </param>
/// <param name="Validation">This suite's own pre-flight result; always <c>Valid: false</c>.</param>
public sealed record PreflightSuiteFailure(string SuitePath, ValidateSuiteResult Validation);

/// <summary>
/// The outcome of <see cref="RunSuiteOrchestrator.RunAsync(RunSuiteRequest, Action{string}, CancellationToken)"/> — a closed discriminated union (a
/// private constructor confines derivation to the cases nested here), mirroring
/// <see cref="Cli.CliPinResult"/>'s own shape for the same reason: every branch a caller must handle
/// is visible at the type level, not inferred from a message string.
/// </summary>
public abstract record RunSuiteOutcome
{
    private RunSuiteOutcome()
    {
    }

    /// <summary>The run was attempted and produced a result — see <see cref="RunSuiteResult"/> for how a cancelled/timed-out run is represented within this case.</summary>
    public sealed record Completed(RunSuiteResult Result) : RunSuiteOutcome;

    /// <summary>EDGE-003: one or more suites failed pre-flight validation. The CLI was never spawned.</summary>
    /// <param name="Failures">
    /// EVERY suite the pre-flight rejected, in validation order — vouchfx-mcp#78. Never empty; the
    /// constructor refuses an empty list rather than letting a "nothing was invalid" SuiteInvalid
    /// exist at all, because every consumer of this case reads <see cref="Failures"/><c>[0]</c>.
    /// <para>
    /// <b>Bounded at <see cref="RunSuiteOrchestrator.MaxReportedInvalidSuites"/> entries</b>, with the
    /// remainder counted in <paramref name="OmittedInvalidSuiteCount"/> — a run may cover up to
    /// <see cref="SuitePathExpander.MaxExpandedPaths"/> suites, and holding a full
    /// <see cref="ValidateSuiteResult"/> for every one of them is a memory bound this list is the
    /// right place to apply. The WIRE bounds (per-suite error cap, response-size fit) belong to the
    /// tool boundary and are applied there.
    /// </para>
    /// </param>
    /// <param name="OmittedInvalidSuiteCount">
    /// How many further suites were determined INVALID beyond the ones in <paramref name="Failures"/>;
    /// <c>0</c> when it holds them all. The pre-flight still VALIDATES every suite past the cap — only
    /// the storing stops — so this count is exact rather than a floor.
    /// </param>
    /// <param name="UndeterminedPastCapCount">
    /// How many suites past the collection cap failed with their validity NEVER DETERMINED, rather
    /// than being determined invalid.
    /// <para>
    /// <b>Split from <paramref name="OmittedInvalidSuiteCount"/> at the point of counting, and it has
    /// to be.</b> Past the cap there is no <see cref="ValidateSuiteResult"/> left to classify later —
    /// dropping it is the whole point of the cap — so if the two classes were not told apart HERE,
    /// they never could be, and <c>omittedInvalidSuiteCount</c> would silently absorb suites this
    /// server never determined anything about. That would break the one contract
    /// <see cref="RunSuiteInvalidPayload.InvalidSuites"/> makes about its own omission count. The tool
    /// boundary adds this to the undetermined failures it finds among <paramref name="Failures"/>
    /// itself, so one wire counter covers both sides of the cap.
    /// </para>
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The all-or-nothing rule is untouched by vouchfx-mcp#78</b>: one invalid suite still refuses
    /// the whole call and runs nothing. What changed is only how much of the answer comes back — the
    /// pre-flight loop already visited every suite on the success path, so collecting every failure
    /// costs no extra work and saves a caller repairing a forty-suite glob up to forty round trips.
    /// </para>
    /// <para>
    /// <b>This case's POSITIONAL shape changed</b> — it was <c>(ValidateSuiteResult, string)</c> and is
    /// now <c>(IReadOnlyList&lt;PreflightSuiteFailure&gt;, int, int)</c> — <b>and that is a deliberate
    /// pre-1.0 decision, not an oversight.</b> This package ships as a dotnet TOOL and is not consumed
    /// as a library, so the only affected callers are in this repository and the compiler enumerated
    /// them; the WIRE change the story ships is separately additive (see
    /// <see cref="RunSuiteInvalidPayload"/>), which is the compatibility that actually matters to a
    /// host. The alternative — keeping the old positional
    /// pair beside a list — would have left two representations of the same fact and a rule about
    /// which one to trust. <see cref="Validation"/>/<see cref="SuitePath"/> survive as PROJECTIONS of
    /// the list for exactly that reason: one source, read two convenient ways.
    /// </para>
    /// </remarks>
    public sealed record SuiteInvalid(
        IReadOnlyList<PreflightSuiteFailure> Failures,
        int OmittedInvalidSuiteCount = 0,
        int UndeterminedPastCapCount = 0) : RunSuiteOutcome
    {
        private readonly IReadOnlyList<PreflightSuiteFailure> _failures = RequireAtLeastOne(Failures);

        /// <inheritdoc cref="SuiteInvalid.Failures"/>
        /// <remarks>
        /// <b>The check appears TWICE — on the backing field's initialiser and in the <c>init</c>
        /// accessor — and that is not redundancy: the two cover different construction paths, and
        /// neither covers the other's.</b> MEASURED, by deleting the initialiser: the compiler answers
        /// <c>CS8907 "Parameter 'Failures' is unread"</c> plus <c>CS8618</c>. When a positional record
        /// declares the matching property MANUALLY, the generated primary constructor does not assign
        /// it; the parameter reaches the object only through an explicit use, which the field
        /// initialiser is. The accessor then covers what the initialiser cannot — every
        /// <c>with</c>-expression, which re-runs no initialiser at all. Drop either one and the
        /// invariant develops a hole (or, for the initialiser, simply stops compiling). A documented
        /// invariant a copy-and-mutate could sidestep is a comment, not an invariant.
        /// </remarks>
        public IReadOnlyList<PreflightSuiteFailure> Failures
        {
            get => _failures;
            init => _failures = RequireAtLeastOne(value);
        }

        /// <summary>
        /// The FIRST invalid suite's pre-flight result — which suite that is has not changed across
        /// vouchfx-mcp#78 (the loop used to stop there), so every existing caller of this property
        /// reads exactly what it always did.
        /// </summary>
        public ValidateSuiteResult Validation => Failures[0].Validation;

        /// <summary>The FIRST invalid suite's resolved path, raw and uncapped. See <see cref="Validation"/>.</summary>
        public string SuitePath => Failures[0].SuitePath;

        private static IReadOnlyList<PreflightSuiteFailure> RequireAtLeastOne(
            IReadOnlyList<PreflightSuiteFailure> failures)
        {
            ArgumentNullException.ThrowIfNull(failures);

            return failures.Count > 0
                ? failures
                : throw new ArgumentException(
                    "A SuiteInvalid outcome must name at least one invalid suite.", nameof(failures));
        }
    }

    /// <summary>
    /// The call itself was malformed — an argument-injection attempt (a path or tag beginning with
    /// <c>-</c>), an out-of-range <c>timeoutSeconds</c>, a label or path list past its bound, or an
    /// absolute glob. Nothing was spawned.
    /// </summary>
    public sealed record InvalidArgument(string Message) : RunSuiteOutcome;

    /// <summary>
    /// US-S3-02: the call supplied both <c>path</c> and <c>paths</c>, or neither, so it does not
    /// identify a suite set (<c>VFX-E-1503</c>). Nothing was spawned.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InvalidArgument"/> for the reason <c>VFX-E-1503</c>'s own catalogue
    /// entry gives, and mirroring <c>validate_suite</c>'s <c>VFX-E-1152</c> exactly: both arguments
    /// are individually well formed, and the remedy ("drop one of the two") is knowable from the
    /// code alone without reading the message.
    /// </remarks>
    public sealed record AmbiguousInput(string Message) : RunSuiteOutcome;

    /// <summary>
    /// US-S3-02: a well-formed <c>paths</c> pattern selected no suite at all (<c>VFX-E-1002</c>) —
    /// never an empty, "successful" run. Nothing was spawned.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="SuiteInvalid"/>: nothing was determined about any suite, because
    /// there was no suite to determine anything about. It maps to the same
    /// <c>VFX-E-1002 SuiteFileNotFound</c> a single missing <c>path</c> already returns, so "you
    /// named nothing that exists" is one answer whether the caller named a file or a pattern.
    /// </remarks>
    public sealed record NoSuitesMatched(string Message) : RunSuiteOutcome;

    /// <summary>
    /// US-S3-02: the call asked for a behaviour this build cannot honour — <c>wait: false</c> or
    /// <c>keepEnvironment: true</c> (<c>VFX-E-1504</c>, upstream ask U4). Nothing was spawned.
    /// </summary>
    /// <remarks>
    /// <c>sprint-00-overview.md</c> §3's gated-feature stance (a), and the reason this is a distinct
    /// case rather than an <see cref="InvalidArgument"/>: the argument is not invalid. It is
    /// accepted, well formed, and within its documented domain — the BEHAVIOUR it selects is what is
    /// missing. Reporting it as a bad argument would tell a host to fix its call when the fix is an
    /// engine release.
    /// </remarks>
    public sealed record OptionUnavailable(string Message) : RunSuiteOutcome;

    /// <summary>REQ-008's CLI handshake gate failed (absent or version-mismatched CLI). Nothing was spawned.</summary>
    public sealed record CliUnavailable(string Message) : RunSuiteOutcome;

    /// <summary>
    /// Another <c>run_suite</c> call was already in progress — on this server instance, or (since
    /// US-S3-04) in ANOTHER server process against the same workspace. Nothing was spawned.
    /// </summary>
    /// <param name="Message">A human-readable explanation naming the workspace-wide scope of the claim.</param>
    /// <param name="ActiveRunId">
    /// The run id of the run currently holding the claim, as read back from the run registry —
    /// spec §4.6 requires <c>VFX-E-1501</c>'s <c>details</c> to carry it, and <c>RunSuiteTool</c> is
    /// what puts it there.
    /// <para>
    /// <see langword="null"/> is a real, expected state rather than a failure: with no
    /// <c>--workspace</c> the registry is in memory and holds a running entry only for this process's
    /// own run (which it does, so the id IS reported); cross-process, the id can be absent or stale —
    /// <see cref="RunSuiteOrchestrator"/>'s <c>BuildAlreadyRunningOutcome</c> remarks enumerate the
    /// windows (head, tail, registry-scan cap, and a holder still inside its CLI handshake) and are
    /// the single authority on them; this comment deliberately does not summarise that list, so the
    /// two cannot drift.
    /// </para>
    /// </param>
    public sealed record AlreadyRunning(string Message, string? ActiveRunId) : RunSuiteOutcome;

    /// <summary>
    /// The <see cref="IRunRegistry"/> could not record the run at all — its storage refused the write
    /// (a read-only or unwritable output directory, an exhausted volume). Nothing was spawned.
    /// </summary>
    /// <remarks>
    /// A distinct case rather than a reuse of <see cref="CliUnavailable"/> or an
    /// <see cref="RunSuiteResult"/> with an <c>Inconclusive</c> verdict, because it is neither: the
    /// engine was never consulted, and no run was attempted, so reporting a VERDICT would assert
    /// something about a suite that never executed. It is a failure of this server's own storage
    /// before the first gate that could produce a verdict — the only shape that says so honestly is a
    /// tool error (<c>VFX-E-1502</c>), which is what <c>RunSuiteTool</c> renders it as.
    /// </remarks>
    public sealed record RunNotRecorded(string Message) : RunSuiteOutcome;
}
