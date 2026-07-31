namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Why a <see cref="CliInvocationResult"/> did not reach a launched-and-exited state. Only
/// meaningful when <see cref="CliInvocationResult.Launched"/> is <see langword="false"/> — a
/// <see cref="CliInvocationResult.Completed"/> result never carries one (<see cref="CliInvocationResult.FailureReason"/>
/// is <see langword="null"/> there).
/// </summary>
/// <remarks>
/// Callers that only need "was this usable" can keep checking <see cref="CliInvocationResult.Launched"/>
/// alone. Callers that must give the caller a DIFFERENT actionable message depending on WHY it
/// failed — e.g. "install the CLI" is the wrong instruction for "the analysis exceeded its time
/// budget" — switch on this instead. See <c>PlanCoverageOrchestrator.PlanAsync</c> and
/// <c>ScaffoldSuiteOrchestrator.ScaffoldAsync</c> for the two production consumers.
/// </remarks>
public enum CliLaunchFailureReason
{
    /// <summary>The binary could not be resolved on PATH, or the OS refused to start it once resolved.</summary>
    NotFound,

    /// <summary>The process was started but did not exit within the invocation's wall-clock timeout and was killed.</summary>
    TimedOut,

    /// <summary>The process's stdout or stderr exceeded the invocation's byte cap before it exited, and was killed.</summary>
    OutputCapExceeded,
}

/// <summary>
/// Outcome of a single vouchfx CLI invocation that may exit non-zero (for example
/// <c>scaffold</c> rejecting an unknown step type). Unlike
/// <see cref="IVouchfxCli.TryRunStdoutAsync"/>, this preserves exit code and stderr so tools can
/// surface the engine's own diagnostic.
/// </summary>
public sealed class CliInvocationResult
{
    private CliInvocationResult(bool launched, int exitCode, string? stdout, string? stderr, CliLaunchFailureReason? failureReason)
    {
        Launched = launched;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        FailureReason = failureReason;
    }

    /// <summary>
    /// The CLI binary could not be resolved on PATH, or <c>Process.Start</c> itself failed. Exit
    /// code and stream text are not meaningful. See <see cref="TimedOut"/> and
    /// <see cref="OutputCapExceeded"/> for the other two ways an invocation can fail to reach a
    /// launched-and-exited state — a caller that needs to tell "the CLI is missing" apart from "the
    /// CLI ran too long" or "the CLI produced too much output" must switch on
    /// <see cref="FailureReason"/> rather than treating every non-launched result identically.
    /// </summary>
    public static CliInvocationResult NotLaunched { get; } =
        new(false, exitCode: -1, stdout: null, stderr: null, CliLaunchFailureReason.NotFound);

    /// <summary>
    /// The process was started but did not exit within the invocation's wall-clock timeout and was
    /// killed. Exit code and stream text are not meaningful.
    /// </summary>
    public static CliInvocationResult TimedOut { get; } =
        new(false, exitCode: -1, stdout: null, stderr: null, CliLaunchFailureReason.TimedOut);

    /// <summary>
    /// The process's stdout or stderr exceeded the invocation's byte cap before it exited, and was
    /// killed. Exit code and stream text are not meaningful.
    /// </summary>
    public static CliInvocationResult OutputCapExceeded { get; } =
        new(false, exitCode: -1, stdout: null, stderr: null, CliLaunchFailureReason.OutputCapExceeded);

    /// <summary>Whether the process was started and reached an observed exit.</summary>
    public bool Launched { get; }

    /// <summary>Process exit code when <see cref="Launched"/> is <see langword="true"/>; otherwise <c>-1</c>.</summary>
    public int ExitCode { get; }

    /// <summary>Raw stdout text (may be empty). Null when the invocation did not reach a launched-and-exited state.</summary>
    public string? Stdout { get; }

    /// <summary>Raw stderr text (may be empty). Null when the invocation did not reach a launched-and-exited state.</summary>
    public string? Stderr { get; }

    /// <summary>
    /// Why this invocation did not reach a launched-and-exited state; <see langword="null"/> when
    /// <see cref="Launched"/> is <see langword="true"/>.
    /// </summary>
    public CliLaunchFailureReason? FailureReason { get; }

    /// <summary>Builds a completed-invocation result (process launched and exited).</summary>
    public static CliInvocationResult Completed(int exitCode, string? stdout, string? stderr) =>
        new(true, exitCode, stdout, stderr, failureReason: null);
}
