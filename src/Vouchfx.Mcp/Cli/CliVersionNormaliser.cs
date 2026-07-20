namespace Vouchfx.Mcp.Cli;

/// <summary>
/// Normalises a version string — either <c>ENGINE_PIN</c>'s own (<c>v1.0.0-alpha.9</c>-shaped,
/// leading <c>v</c>) or the vouchfx CLI's own <c>--version</c> output — to the bare, comparable
/// core form <see cref="CliPinVerifier"/> compares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verified against the real, currently-installed CLI, not assumed</b> (REQ-008 STEP 0): running
/// <c>vouchfx --version</c> against the actual published <c>1.0.0-alpha.9</c> global tool reports
/// </para>
/// <code>
/// 1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1
/// </code>
/// <para>
/// — no leading <c>v</c> (the engine's <c>.github/workflows/release.yml</c> strips it from the git
/// tag before packing: <c>version="${tag#v}"</c>, then <c>-p:Version="$VERSION"</c>), but WITH a
/// <c>+&lt;commit-sha&gt;</c> build-metadata suffix that exactly matches <c>ENGINE_PIN</c>'s own
/// commit-SHA field — the .NET SDK embeds it at CI build time
/// (<c>Vouchfx.Cli.csproj</c> sets <c>ContinuousIntegrationBuild=true</c> under CI) even though the
/// CLI project itself references no SourceLink package; a throwaway local build without a git
/// checkout behind it (as used to sanity-check <c>System.CommandLine</c>'s own <c>--version</c>
/// wiring while investigating this) does NOT reproduce that suffix, which is why this was verified
/// against the real installed tool rather than trusted from a synthetic build. System.CommandLine's
/// built-in <c>--version</c> option prints the assembly's own <c>AssemblyInformationalVersion</c>
/// verbatim, exactly as observed above.
/// </para>
/// <para>
/// Normalisation, applied to BOTH sides before comparison: trim surrounding whitespace; strip one
/// leading <c>v</c>/<c>V</c>; drop everything from the first <c>+</c> onward — SemVer build
/// metadata, which the SemVer spec itself says MUST be ignored when determining version
/// equality/precedence. The result is compared case-insensitively by <see cref="CliPinVerifier"/>.
/// </para>
/// </remarks>
public static class CliVersionNormaliser
{
    /// <summary>Normalises <paramref name="value"/> to its bare core version form.</summary>
    public static string Normalise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();

        if (trimmed.Length > 0 && trimmed[0] is 'v' or 'V')
        {
            trimmed = trimmed[1..];
        }

        var buildMetadataIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex >= 0)
        {
            trimmed = trimmed[..buildMetadataIndex];
        }

        return trimmed;
    }

    /// <summary>
    /// Whether <paramref name="normalisedValue"/> (already passed through <see cref="Normalise"/>)
    /// looks enough like a version to report as "found" rather than "unparseable": non-empty and
    /// starting with an ASCII digit. Deliberately loose — this server's only job is comparing it
    /// against the pin, not fully validating SemVer grammar; a stricter check would only risk
    /// rejecting a real, future version-string shape this server has never seen.
    /// </summary>
    public static bool LooksLikeAVersion(string normalisedValue) =>
        normalisedValue.Length > 0 && char.IsAsciiDigit(normalisedValue[0]);
}
