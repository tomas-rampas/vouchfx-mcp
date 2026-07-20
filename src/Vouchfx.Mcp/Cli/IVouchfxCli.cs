namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Abstracts "ask the vouchfx CLI what version it is" so <see cref="CliPinVerifier"/> can be unit
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
}
