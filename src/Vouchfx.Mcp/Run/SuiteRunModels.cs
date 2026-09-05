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
/// <param name="Verdict">
/// One of <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> — always one of these
/// four, never conflated (§12.1). A cancelled or timed-out run is always reported as
/// <c>Inconclusive</c>, never <c>Fail</c> (EDGE-002).
/// </param>
/// <param name="ExitCode">The vouchfx CLI's own process exit code, when it could be determined.</param>
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
/// <param name="Steps">Every step's outcome. Empty for a cancelled/timed-out run (EDGE-002).</param>
/// <param name="EventsFilePath">
/// The local path to this run's complete JSON Lines event stream — the same file a later
/// <c>explain_run</c> call is expected to read (see <c>CliPinVerifier</c>'s remarks).
/// </param>
/// <param name="EventsTruncated">
/// <see langword="true"/> when the events file exceeded <see cref="EventsFileReader.MaxEventsFileBytes"/>
/// and was only read up to that many bytes before parsing — <see cref="Verdict"/> and
/// <see cref="Steps"/> are derived from whatever complete lines fit within the cap and may therefore
/// be incomplete. <see langword="false"/> (the default) for every ordinary run.
/// </param>
public sealed record RunSuiteResult(
    string Verdict,
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string? RemediationHint,
    IReadOnlyList<StepOutcome> Steps,
    string EventsFilePath,
    bool EventsTruncated = false);

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
/// <param name="Validation">The validation result; always <c>Valid: false</c> with at least one error.</param>
public sealed record RunSuiteInvalidPayload(string Code, ValidateSuiteResult Validation);

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
    public sealed record SuiteInvalid(ValidateSuiteResult Validation) : RunSuiteOutcome;

    /// <summary>
    /// The call itself was malformed — an argument-injection attempt (a path beginning with
    /// <c>-</c>) or an out-of-range <c>timeoutSeconds</c>. Nothing was spawned.
    /// </summary>
    public sealed record InvalidArgument(string Message) : RunSuiteOutcome;

    /// <summary>REQ-008's CLI handshake gate failed (absent or version-mismatched CLI). Nothing was spawned.</summary>
    public sealed record CliUnavailable(string Message) : RunSuiteOutcome;

    /// <summary>Another <c>run_suite</c> call was already in progress on this server instance. Nothing was spawned.</summary>
    public sealed record AlreadyRunning(string Message) : RunSuiteOutcome;

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
