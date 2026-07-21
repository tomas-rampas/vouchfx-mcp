namespace Vouchfx.Mcp.Run;

/// <summary>
/// Abstracts "run a suite through the vouchfx CLI and report how the child process ended" so
/// <see cref="RunSuiteOrchestrator"/> can be unit tested without the real CLI or Docker installed.
/// </summary>
/// <remarks>
/// The production implementation (<see cref="VouchfxCliSuiteRunner"/>) spawns the real
/// <c>vouchfx run</c> child process; tests inject a fake that scripts a sequence of output lines,
/// an exit code, and/or a cancellation response — never a real process. This mirrors
/// <see cref="Vouchfx.Mcp.Cli.IVouchfxCli"/>'s exact role for the version-handshake boundary, one
/// layer up: <see cref="RunAsync"/> does not itself parse the produced events file — the caller
/// reads and parses <see cref="SuiteRunSpec.EventsFilePath"/> once this returns
/// <see cref="RunTermination.CompletedNormally"/>, via <see cref="SuiteEventParser"/> — so this
/// interface's only job is process lifecycle, not the vouchfx event-stream contract.
/// </remarks>
public interface ISuiteRunner
{
    /// <summary>
    /// Runs the suite described by <paramref name="spec"/>, invoking <paramref name="onOutputLine"/>
    /// for each line of the child's console output as it becomes available (best-effort, coarse
    /// progress — see <see cref="VouchfxCliSuiteRunner"/>'s remarks for why this is the only
    /// genuinely live signal available), until the process exits on its own or
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="spec">The suite path, tag filters, and events-file destination for this run.</param>
    /// <param name="onOutputLine">
    /// Invoked once per non-empty output line, already sanitised for display. Never invoked
    /// concurrently with itself (lines are relayed as they are read, not in parallel).
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelling this — whether from the caller's own MCP-level cancellation or from a
    /// timeout budget the caller layered on top — must not simply abandon the child process.
    /// Implementations own the EDGE-002 teardown sequence themselves — DELIVERED, not aspirational
    /// (todo 17): the production implementation (<see cref="VouchfxCliSuiteRunner"/>) requests a
    /// GRACEFUL stop first, closing the child's stdin — the signal an engine started with
    /// <c>--shutdown-on-stdin-eof</c> uses to cancel its own token and run its DCP/Testcontainers
    /// teardown to completion — waits a bounded grace period for that to finish, and only falls
    /// back to a hard kill of the whole process tree once that grace elapses with the child still
    /// alive. Either way the outcome is reported as <see cref="RunTermination.Aborted"/> rather than
    /// thrown, so the caller never has to distinguish "the process misbehaved" from "cancellation
    /// was honoured" through exception handling.
    /// </param>
    Task<SuiteProcessResult> RunAsync(
        SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken);
}

/// <summary>The suite to run, as <see cref="ISuiteRunner"/> needs it.</summary>
/// <param name="SuitePath">
/// Path to the <c>.e2e.yaml</c> suite file — already past <see cref="RunSuiteOrchestrator"/>'s
/// argument-safety and pre-validation gates by the time a runner ever sees it.
/// </param>
/// <param name="Tags">Zero or more tag filters, passed through to the CLI's own <c>--tag</c> selection.</param>
/// <param name="EventsFilePath">
/// Where the run's JSON Lines event stream must end up once <see cref="ISuiteRunner.RunAsync"/>
/// returns <see cref="RunTermination.CompletedNormally"/>. The runner is responsible for making
/// this path exist and be populated on a normal completion (for the production implementation,
/// this is simply the CLI's own <c>--events</c> flag); the caller does the reading and parsing.
/// </param>
public sealed record SuiteRunSpec(string SuitePath, IReadOnlyList<string> Tags, string EventsFilePath);

/// <summary>How the child process ended.</summary>
public enum RunTermination
{
    /// <summary>The child process exited on its own before <c>cancellationToken</c> fired.</summary>
    CompletedNormally,

    /// <summary>
    /// <c>cancellationToken</c> fired before the child exited on its own, and the runner carried
    /// out its own stop-then-kill sequence in response. The CALLER (<see cref="RunSuiteOrchestrator"/>)
    /// is responsible for distinguishing "the caller's own token fired" (report as cancelled) from
    /// "an internal timeout budget fired" (report as timed out) — this interface intentionally
    /// exposes only ONE outcome for both, since from the runner's own perspective they are handled
    /// identically.
    /// </summary>
    Aborted,
}

/// <summary>
/// The outcome of <see cref="ISuiteRunner.RunAsync"/> — process lifecycle only, never a parsed
/// verdict (see this file's remarks for why that parsing happens one layer up).
/// </summary>
/// <param name="ExitCode">
/// The child's exit code, or <see langword="null"/> if it could not be determined (e.g. the process
/// was killed and its exit code could not be confirmed within the kill-confirmation bound).
/// </param>
/// <param name="Termination">How the child process ended.</param>
/// <param name="StderrExcerpt">
/// A bounded, sanitised excerpt of the child's stderr output, used by
/// <see cref="RunSuiteOrchestrator"/>'s exit-code fallback classifier (EDGE-001) to look for a
/// Docker-daemon-unavailable signature when no events were produced at all. <see langword="null"/>
/// when no stderr was captured (e.g. a fake runner that does not model it).
/// </param>
public sealed record SuiteProcessResult(int? ExitCode, RunTermination Termination, string? StderrExcerpt = null);
