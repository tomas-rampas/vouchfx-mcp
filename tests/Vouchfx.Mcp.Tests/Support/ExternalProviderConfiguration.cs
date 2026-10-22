namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Whether a <c>vouchfx</c> CLI started from this test process would be affected by trusted external
/// providers (engine v1.2.0 and later), which the pinned-CLI oracles comparing the engine's bundled
/// step types with the vendored artefacts cannot account for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> From v1.2.0 every provider-scanning CLI command searches, in order, its
/// <c>--providers</c> arguments, the directories in <c>VOUCHFX_PROVIDERS</c>, and
/// <c>~/.vouchfx/providers</c> when that directory exists. The production code passes no provider
/// options, so a CLI these tests start inherits whatever the developer's environment and home
/// directory hold. Measured at the v1.2.0 pin with the published <c>rpc.json-rpc</c> provider in the
/// home directory: the list oracle failed with a bare string difference and the schema oracle blamed
/// the engine build or the code-page modelling, although the engine was exactly the pinned build.
/// This precondition names the real cause instead.
/// </para>
/// <para>
/// <b>Deliberately approximate, in the direction that matters.</b> It mirrors only the engine's
/// search ORDER, never its admission rules: an existing but empty directory loads nothing and is
/// ignored, while a <c>VOUCHFX_PROVIDERS</c> entry that does not exist counts, because the engine
/// refuses to start with it. Any directory that cannot be enumerated also counts. A false positive
/// costs a re-run without providers; a false negative would bring back the misattributed failure.
/// </para>
/// </remarks>
internal static class ExternalProviderConfiguration
{
    /// <summary>The fixed failure message for an oracle that cannot run against loaded providers.</summary>
    /// <remarks>Fixed text on purpose: it names the variable and the conventional directory, never a
    /// resolved host path.</remarks>
    public const string HermeticRequirement =
        "The vouchfx CLI this oracle starts would load trusted external providers: VOUCHFX_PROVIDERS is "
        + "set, or ~/.vouchfx/providers is not empty. The oracle compares the pinned engine's bundled "
        + "step types with this server's vendored artefacts, so it cannot pass while providers are "
        + "loaded. Unset VOUCHFX_PROVIDERS and move ~/.vouchfx/providers aside, then run it again.";

    /// <summary>
    /// <see langword="true"/> when a CLI started now would load, or refuse to start over, an external
    /// provider location.
    /// </summary>
    public static bool AffectsTheCli()
    {
        var configured = (Environment.GetEnvironmentVariable("VOUCHFX_PROVIDERS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (configured.Any(directory => !Directory.Exists(directory) || IsNotEmpty(directory)))
        {
            return true;
        }

        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vouchfx", "providers");

        return Directory.Exists(home) && IsNotEmpty(home);
    }

    private static bool IsNotEmpty(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
