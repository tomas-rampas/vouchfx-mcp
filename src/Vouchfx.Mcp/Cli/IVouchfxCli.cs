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
    /// <param name="maxStdoutBytes">
    /// Maximum bytes read from stdout before the process is treated as misbehaving. Use a larger
    /// cap for <c>list --json</c> / <c>schema</c> than for <c>--version</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for process exit.</param>
    /// <returns>
    /// The raw stdout text on exit 0, or <see langword="null"/> if the CLI could not be launched,
    /// timed out, exceeded the output cap, or exited non-zero.
    /// </returns>
    Task<string?> TryRunStdoutAsync(
        IReadOnlyList<string> arguments,
        long maxStdoutBytes,
        CancellationToken cancellationToken = default);
}
