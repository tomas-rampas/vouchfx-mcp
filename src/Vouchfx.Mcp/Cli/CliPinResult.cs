namespace Vouchfx.Mcp.Cli;

/// <summary>
/// The outcome of <see cref="CliPinVerifier.VerifyAsync"/> — REQ-008's CLI presence + version
/// handshake, as a closed discriminated union (a private constructor confines derivation to the
/// four cases nested here).
/// </summary>
public abstract record CliPinResult
{
    private CliPinResult()
    {
    }

    /// <summary>The installed CLI's version matches <c>ENGINE_PIN</c>.</summary>
    /// <param name="DetectedVersion">The CLI's own reported version, normalised and sanitised.</param>
    public sealed record Ok(string DetectedVersion) : CliPinResult;

    /// <summary>
    /// The CLI could not be launched at all (not on PATH), did not exit cleanly, or did not
    /// respond within the bounded timeout — every one of those is reported identically, since none
    /// of them leaves this server able to use the CLI.
    /// </summary>
    /// <param name="Message">An actionable message naming the exact install command.</param>
    public sealed record NotFound(string Message) : CliPinResult;

    /// <summary>The CLI launched and reported a version, but it does not match <c>ENGINE_PIN</c>.</summary>
    /// <param name="DetectedVersion">The CLI's own reported version, normalised and sanitised.</param>
    /// <param name="ExpectedVersion">The pinned version, as recorded in <c>ENGINE_PIN</c>.</param>
    /// <param name="Message">An actionable message naming both versions and the exact update command.</param>
    public sealed record VersionMismatch(string DetectedVersion, string ExpectedVersion, string Message) : CliPinResult;

    /// <summary>
    /// The CLI launched and exited cleanly, but its output could not be recognised as a version at
    /// all (see <see cref="CliVersionNormaliser.LooksLikeAVersion"/>).
    /// </summary>
    /// <param name="RawOutput">The CLI's raw, sanitised output.</param>
    /// <param name="Message">An actionable message including the sanitised raw output.</param>
    public sealed record Unparseable(string RawOutput, string Message) : CliPinResult;
}
