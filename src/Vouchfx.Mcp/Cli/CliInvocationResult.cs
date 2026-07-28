namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Outcome of a single vouchfx CLI invocation that may exit non-zero (for example
/// <c>scaffold</c> rejecting an unknown step type). Unlike
/// <see cref="IVouchfxCli.TryRunStdoutAsync"/>, this preserves exit code and stderr so tools can
/// surface the engine's own diagnostic.
/// </summary>
public sealed class CliInvocationResult
{
    private CliInvocationResult(bool launched, int exitCode, string? stdout, string? stderr)
    {
        Launched = launched;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
    }

    /// <summary>
    /// The CLI binary could not be resolved or launched (not on PATH, Start failed, timeout,
    /// or output-cap breach). Exit code and stream text are not meaningful.
    /// </summary>
    public static CliInvocationResult NotLaunched { get; } = new(false, exitCode: -1, stdout: null, stderr: null);

    /// <summary>Whether the process was started and reached a observed exit.</summary>
    public bool Launched { get; }

    /// <summary>Process exit code when <see cref="Launched"/> is <see langword="true"/>; otherwise <c>-1</c>.</summary>
    public int ExitCode { get; }

    /// <summary>Raw stdout text (may be empty). Null when not launched or when the stream was truncated by the cap.</summary>
    public string? Stdout { get; }

    /// <summary>Raw stderr text (may be empty). Null when not launched or when the stream was truncated by the cap.</summary>
    public string? Stderr { get; }

    /// <summary>Builds a completed-invocation result (process launched and exited).</summary>
    public static CliInvocationResult Completed(int exitCode, string? stdout, string? stderr) =>
        new(true, exitCode, stdout, stderr);
}
