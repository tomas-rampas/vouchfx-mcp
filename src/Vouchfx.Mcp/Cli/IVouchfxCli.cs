namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Abstracts "run the vouchfx CLI" so pin verification and live catalogue/schema export can be unit
/// tested without the real CLI installed. The production implementation
/// (<see cref="VouchfxCliProcessRunner"/>) spawns the real <c>vouchfx</c> process on PATH; tests
/// inject a fake that returns a canned result instantly.
/// </summary>
public interface IVouchfxCli
{
    /// <summary>
    /// Attempts to run <c>vouchfx --version</c> and return its raw stdout text.
    /// </summary>
    /// <returns>
    /// The raw, untrimmed stdout text on a clean run, or <see langword="null"/> if the CLI could
    /// not be launched at all (not on PATH), did not exit cleanly, or did not respond within a
    /// bounded timeout. Every one of those means "the CLI cannot be verified as installed" —
    /// <see cref="CliPinVerifier"/> treats them identically, as <c>NotFound</c>.
    /// </returns>
    Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to run the vouchfx CLI with the given argument list and return its raw stdout on a
    /// successful (exit 0) run.
    /// </summary>
    /// <param name="arguments">
    /// Arguments passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> (never a
    /// shell command line) — e.g. <c>["list", "--json"]</c> or <c>["schema"]</c>.
    /// </param>
    /// <param name="maxStreamBytes">
    /// Maximum bytes read from <em>each</em> of stdout and stderr before the process is treated as
    /// misbehaving (both streams are bounded independently at this cap). Use a larger value for
    /// <c>list --json</c> / <c>schema</c> than for <c>--version</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for process exit.</param>
    /// <returns>
    /// The raw stdout text on exit 0, or <see langword="null"/> if the CLI could not be launched,
    /// timed out, exceeded the output cap, or exited non-zero.
    /// </returns>
    Task<string?> TryRunStdoutAsync(
        IReadOnlyList<string> arguments,
        long maxStreamBytes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the vouchfx CLI with the given argument list and returns the full invocation outcome
    /// (launch success, exit code, stdout, stderr). Used by tools such as <c>scaffold_suite</c>
    /// that must surface non-zero CLI diagnostics (unknown step type, empty steps, …) rather than
    /// collapsing every failure into <see langword="null"/>.
    /// </summary>
    /// <param name="arguments">
    /// Arguments passed via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> (never a
    /// shell command line) — e.g. <c>["scaffold", "--intent", path]</c>.
    /// </param>
    /// <param name="maxStreamBytes">
    /// Maximum bytes read from <em>each</em> of stdout and stderr before the process is treated as
    /// misbehaving (both streams are bounded independently at this cap).
    /// </param>
    /// <param name="timeout">
    /// Wall-clock budget for this invocation before it is killed and reported as
    /// <see cref="CliInvocationResult.TimedOut"/>. <see langword="null"/> uses the implementation's
    /// own default — a short, Docker-free-invocation budget. Callers whose invocation legitimately
    /// does more work than that default assumes (e.g. <c>plan</c>, which walks a suite tree plus an
    /// optional event-history directory) should pass an explicit, longer budget rather than sharing
    /// the default.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for process exit. Kept as the LAST parameter (CA1068).</param>
    /// <returns>
    /// <see cref="CliInvocationResult.NotLaunched"/> when the binary could not be resolved or Start
    /// failed, <see cref="CliInvocationResult.TimedOut"/> when it ran past <paramref name="timeout"/>,
    /// <see cref="CliInvocationResult.OutputCapExceeded"/> when a stream exceeded
    /// <paramref name="maxStreamBytes"/> before exit, or otherwise a completed result with exit code
    /// and captured streams. See <see cref="CliInvocationResult.FailureReason"/> for distinguishing
    /// the three non-launched cases.
    /// </returns>
    Task<CliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments,
        long maxStreamBytes,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
