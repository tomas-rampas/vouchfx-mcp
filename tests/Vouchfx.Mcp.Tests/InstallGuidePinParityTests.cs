using System.Text.RegularExpressions;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The CLI installation commands in the published install and troubleshooting guides name the
/// engine version <c>ENGINE_PIN</c> pins, and no other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The v1.1.0 repin moved every prose mention of the pin but left both
/// guides' copy-paste commands at <c>--version 1.0.7</c>, so a reader following them installed a CLI
/// that <c>CliPinVerifier</c> then refused with a version mismatch. Nothing tied those commands to
/// <c>ENGINE_PIN</c>; <see cref="PackageReleaseNotesPinParityTests"/> covers the package notes only.
/// The next repin that forgets them fails here instead.
/// </para>
/// <para>
/// <b>What counts.</b> Four shapes the guides use to name the CLI they want: a
/// <c>vouchfx --version &lt;v&gt;</c> install or update argument, a <c>-p:Version=</c> or
/// <c>-p:PackageVersion=</c> property on a pack of the CLI project (<c>src/Cli/Vouchfx.Cli</c>; the
/// same properties on this server's own pack name the MCP package, not the engine), the
/// <c>`&lt;v&gt;+&lt;full commit SHA from ENGINE_PIN&gt;`</c>
/// expected-output form, and the bold "targets <b>vouchfx &lt;v&gt;</b>" sentence. A bare
/// <c>vouchfx --version</c> (the check command, with no argument) is not a version claim and is not
/// matched. A section whose heading says <c>Historical</c> is excluded with everything nested under
/// it: install.md keeps an rc.6 fallback for older checkouts on purpose, and that section must keep
/// naming rc.6.
/// </para>
/// </remarks>
public class InstallGuidePinParityTests
{
    private static readonly Regex[] VersionClaims =
    [
        new(@"\bvouchfx --version (\d[^\s`]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"src/Cli/Vouchfx\.Cli\b[^\n]*?-p:Version=(\d[^\s`]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"src/Cli/Vouchfx\.Cli\b[^\n]*?-p:PackageVersion=(\d[^\s`]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"`(\d[^\s`+]*)\+<full commit SHA from ENGINE_PIN>`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
        new(@"targets \*\*vouchfx (\d[^\s*]*)\*\*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)),
    ];

    private static readonly Regex Heading = new(
        @"^(#{1,6})\s+(.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Theory]
    [InlineData("docs/install.md")]
    [InlineData("docs/troubleshooting.md")]
    public void CliInstallCommands_NameTheEnginePinVersion_AndNoOther(string guide)
    {
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var expected = CliVersionNormaliser.Normalise(pin.Version);
        var text = WithoutHistoricalSections(File.ReadAllLines(Path.Combine(RepoLayout.ResolveRepoRootPath(), guide)));

        var versions = VersionClaims
            .SelectMany(pattern => pattern.Matches(text))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            versions.Length > 0,
            $"{guide} names no vouchfx CLI version outside its historical sections. It is expected to "
            + "give copy-paste install commands for the pinned CLI; if they were removed on purpose, "
            + "remove this case with them.");
        Assert.True(
            versions.Length == 1 && string.Equals(versions[0], expected, StringComparison.Ordinal),
            $"{guide} tells readers to install vouchfx {string.Join(", ", versions)}, but ENGINE_PIN pins "
            + $"{pin.Version}. A CLI installed from those commands fails the pin handshake, so update them "
            + "in the same change as the pin.");
    }

    /// <remarks>
    /// Fenced code is tracked because the guides' shell blocks carry <c># comment</c> lines that
    /// would otherwise read as headings and end an excluded section early.
    /// </remarks>
    private static string WithoutHistoricalSections(IEnumerable<string> lines)
    {
        var kept = new List<string>();
        int? skippingBelowLevel = null;
        var inFence = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            var heading = inFence ? Match.Empty : Heading.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                if (skippingBelowLevel is { } skipped && level <= skipped)
                {
                    skippingBelowLevel = null;
                }

                if (skippingBelowLevel is null
                    && heading.Groups[2].Value.Contains("Historical", StringComparison.OrdinalIgnoreCase))
                {
                    skippingBelowLevel = level;
                }
            }

            if (skippingBelowLevel is null)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }
}
