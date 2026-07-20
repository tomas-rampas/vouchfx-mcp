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
public sealed record StepOutcome(string StepId, string Verdict, long DurationMs, int AttemptCount);

/// <summary>One <c>environment-error</c> event (§12.1, §14.4) — always distinct from a <c>Fail</c>.</summary>
/// <param name="ErrorKind">The <c>OrchestrationErrorKind</c> name the engine reported (e.g. <c>"ImagePull"</c>, <c>"Provision"</c>), sanitised for display.</param>
/// <param name="ResourceName">The Aspire resource name the failure concerns, sanitised for display.</param>
/// <param name="Detail">A trimmed summary of the underlying failure, sanitised for display; <see langword="null"/> when the engine reported none.</param>
public sealed record EnvironmentErrorSummary(string ErrorKind, string ResourceName, string? Detail);

/// <summary>The whole events file, reduced to what <see cref="RunSuiteOrchestrator"/> needs to build a result.</summary>
/// <param name="AggregateVerdict">
/// The suite's overall verdict, computed by elevating every <c>scenario-completed</c> event's own
/// verdict (§12.1 precedence — see <see cref="RunVerdictExtensions.Elevate"/>). <see langword="null"/>
/// when no <c>scenario-completed</c> event was found at all (e.g. the run failed before any scenario
/// could start) — the orchestrator falls back to the CLI's own exit code in that case.
/// </param>
/// <param name="Steps">Every step's outcome, in the order its <c>step-completed</c> event appeared.</param>
/// <param name="EnvironmentErrors">Every <c>environment-error</c> event found, used to build a remediation hint.</param>
public sealed record SuiteRunSummary(
    RunVerdict? AggregateVerdict,
    IReadOnlyList<StepOutcome> Steps,
    IReadOnlyList<EnvironmentErrorSummary> EnvironmentErrors);

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
/// <see langword="true"/> when the events file exceeded <see cref="RunSuiteOrchestrator.MaxEventsFileBytes"/>
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
/// <param name="Kind">Always the literal <c>"suite-invalid"</c> — distinguishes this from <see cref="RunSuiteResult"/> at a glance.</param>
/// <param name="Validation">The validation result; always <c>Valid: false</c> with at least one error.</param>
public sealed record RunSuiteInvalidPayload(string Kind, ValidateSuiteResult Validation);

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
}
