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
/// <param name="Outcome">This attempt's own resolved outcome, when the engine reported one; <see langword="null"/> for a mid-RETRY poll with no outcome yet.</param>
/// <param name="Observation">This attempt's own observation evidence (sanitised, capped raw JSON text); <see langword="null"/> when none was carried.</param>
public sealed record StepAttempt(int Attempt, long TMs, string? Outcome, string? Observation);

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
/// A short, actionable hint, populated whenever <see cref="Verdict"/> is <c>EnvironmentError</c>
/// (EDGE-001) — e.g. naming the Docker daemon when that is the most likely cause. <see langword="null"/>
/// for every other verdict.
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
/// </param>
/// <param name="Validation">The validation result; always <c>Valid: false</c> with at least one error.</param>
public sealed record RunSuiteInvalidPayload(string Code, string Path, ValidateSuiteResult Validation);

/// <summary>
/// The outcome of <see cref="RunSuiteOrchestrator.RunAsync"/> — a closed discriminated union (a
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

    /// <summary>EDGE-003: the suite failed pre-flight validation. The CLI was never spawned.</summary>
    /// <param name="Validation">The pre-flight's own result for <paramref name="SuitePath"/>.</param>
    /// <param name="SuitePath">
    /// WHICH suite failed — the resolved path, raw and uncapped (the tool boundary is what renders
    /// it, through <c>PathSafetyGuard.CapAndSanitisePathForDisplay</c>, so this type carries the fact
    /// rather than a rendering of it). Carried because the pre-flight is all-or-nothing across a
    /// multi-suite call and the validation result alone names no file: without this a glob's caller
    /// cannot tell which of forty suites refused the run (a gatekeeper review's MAJOR finding).
    /// </param>
    public sealed record SuiteInvalid(ValidateSuiteResult Validation, string SuitePath) : RunSuiteOutcome;

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
