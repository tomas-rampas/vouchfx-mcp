using System.Text.RegularExpressions;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-checkout CLI installation commands follow ENGINE_PIN. The explicitly separated
/// published-package route follows that release's embedded engine pin instead.
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
/// naming rc.6. The published-package route is checked separately against its release pin.
/// </para>
/// </remarks>
public class InstallGuidePinParityTests
{
    // Verified against the public MCP 1.0.3 package's embedded ENGINE_PIN and:
    // https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.0.3/ENGINE_PIN
    // Historical release facts stay independent of this checkout's advancing ENGINE_PIN.
    private const string PublishedMcpVersion = "1.0.3";
    private const string PublishedCliVersion = "1.0.7";
    private const string PublishedCliCommit = "20d837a2f735c1c785db6593b58ebe10b3dd9f05";

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
        var text = SourceRoute(File.ReadAllLines(Path.Combine(RepoLayout.ResolveRepoRootPath(), guide)));

        Assert.True(SourceClaimsMatch(text, expected),
            "Source-checkout CLI instructions must name only the engine version in ENGINE_PIN.");
    }

    [Fact]
    public void PublishedInstallCommands_NameThePublishedPackageAndItsEmbeddedPin()
    {
        Assert.True(PublishedRouteMatches(ReadInstallGuide()),
            "The published installation route must match its package release and embedded engine pin.");
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("mcp")]
    [InlineData("commit")]
    [InlineData("heading")]
    [InlineData("missing-route")]
    public void PublishedRouteGuard_RejectsMismatchedOrMissingReleaseClaims(string mutation)
    {
        var guide = ReadInstallGuide();
        Assert.True(PublishedRouteMatches(guide), "The original published route must be valid.");
        var changed = mutation switch
        {
            "cli" => guide.Replace("--global vouchfx --version " + PublishedCliVersion,
                "--global vouchfx --version 9.9.9", StringComparison.Ordinal),
            "mcp" => guide.Replace("--global Vouchfx.Mcp --version " + PublishedMcpVersion,
                "--global Vouchfx.Mcp --version 9.9.9", StringComparison.Ordinal),
            "commit" => guide.Replace(PublishedCliCommit, new string('0', 40), StringComparison.Ordinal),
            "heading" => guide.Replace("Published package: MCP", "Unrecognised package: MCP", StringComparison.Ordinal),
            _ => SourceRoute(guide.Split('\n')),
        };
        Assert.False(PublishedRouteMatches(changed), "The published-route guard must reject altered release claims.");
    }

    [Fact]
    public void SourceRouteGuard_RejectsAPublishedVersionInSourceCommands()
    {
        var expected = CliVersionNormaliser.Normalise(EnginePin.Load(RepoLayout.ResolveEnginePinPath()).Version);
        var text = SourceRoute(ReadInstallGuide().Split('\n'));
        Assert.True(SourceClaimsMatch(text, expected), "The original source route must be valid.");
        var changed = text.Replace("--global vouchfx --version " + expected,
            "--global vouchfx --version " + PublishedCliVersion, StringComparison.Ordinal);
        Assert.False(SourceClaimsMatch(changed, expected),
            "The source-route guard must reject a published CLI version substituted into source commands.");
    }

    private static string ReadInstallGuide() =>
        File.ReadAllText(Path.Combine(RepoLayout.ResolveRepoRootPath(), "docs/install.md"));

    private static string SourceRoute(IEnumerable<string> lines) => FilterSections(lines,
        title => title.Contains("Historical", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Published package:", StringComparison.Ordinal), excludeMatches: true);

    private static bool SourceClaimsMatch(string text, string expected)
    {
        var versions = VersionClaims.SelectMany(pattern => pattern.Matches(text))
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        return versions.Length == 1 && string.Equals(versions[0], expected, StringComparison.Ordinal);
    }

    private static bool PublishedRouteMatches(string guide)
    {
        var text = FilterSections(guide.Split('\n'),
            title => title.StartsWith("Published package:", StringComparison.Ordinal), excludeMatches: false);
        var packageVersions = Regex.Matches(text,
            @"\bVouchfx\.Mcp --version (\d[^\s`]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        return packageVersions.Length == 1 && packageVersions[0] == PublishedMcpVersion
            && SourceClaimsMatch(text, PublishedCliVersion)
            && text.Contains($"Published package: MCP {PublishedMcpVersion} with CLI {PublishedCliVersion}", StringComparison.Ordinal)
            && text.Contains($"`{PublishedCliVersion}+{PublishedCliCommit}`", StringComparison.Ordinal)
            && text.Contains($"/blob/v{PublishedMcpVersion}/ENGINE_PIN", StringComparison.Ordinal);
    }

    /// <remarks>
    /// Fenced code is tracked because the guides' shell blocks carry <c># comment</c> lines that
    /// would otherwise read as headings and end an excluded section early.
    /// </remarks>
    private static string FilterSections(IEnumerable<string> lines, Func<string, bool> matches, bool excludeMatches)
    {
        var kept = new List<string>();
        int? selectedBelowLevel = null;
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
                if (selectedBelowLevel is { } skipped && level <= skipped)
                {
                    selectedBelowLevel = null;
                }

                if (selectedBelowLevel is null
                    && matches(heading.Groups[2].Value))
                {
                    selectedBelowLevel = level;
                }
            }

            if ((selectedBelowLevel is not null) != excludeMatches)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }
}
