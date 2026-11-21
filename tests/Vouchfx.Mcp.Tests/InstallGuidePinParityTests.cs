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
    /// <summary>
    /// One published MCP release, the CLI its embedded ENGINE_PIN names, and that CLI's commit.
    /// <paramref name="RequiresRollForward"/> is true for the pairs published as .NET 8 builds, whose
    /// install commands carry <c>--allow-roll-forward</c>, and false for the <c>net10.0</c> pair,
    /// whose commands must not.
    /// </summary>
    private sealed record PublishedPair(string Mcp, string Cli, string Commit, bool RequiresRollForward)
    {
        public const string RollForwardFlag = "--allow-roll-forward";

        public string Heading => $"Published package: MCP {Mcp} with CLI {Cli}";
    }

    // Each triple is verified against the public package's embedded ENGINE_PIN:
    // https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.4.0/ENGINE_PIN (CLI 1.4.0 at 18f5a1cd...)
    // https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.1.0/ENGINE_PIN (CLI 1.2.0 at 0af21de5...)
    // https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.0.3/ENGINE_PIN (CLI 1.0.7 at 20d837a2...)
    // Historical release facts stay independent of this checkout's advancing ENGINE_PIN. A published
    // section naming any other pairing fails the guard; a new release adds its triple here.
    // The roll-forward flag is per pair: the .NET 8 pairs (1.1.0 and earlier) require it in every
    // install command, and the net10.0 pair (1.4.0) must not carry it, so the guide cannot regress
    // in either direction. Only the "dotnet tool install" lines are inspected, because the 1.4.0
    // prose names the flag to say it is not needed.
    private static readonly PublishedPair[] PublishedPairs =
    [
        new("1.4.0", "1.4.0", "18f5a1cdfc8c1f10a963bc7c7527a20808bbf289", RequiresRollForward: false),
        new("1.1.0", "1.2.0", "0af21de5bd0a4e91ca002e03e6c256bc4e8ca4e8", RequiresRollForward: true),
        new("1.0.3", "1.0.7", "20d837a2f735c1c785db6593b58ebe10b3dd9f05", RequiresRollForward: true),
    ];

    private const string PublishedHeadingPrefix = "Published package:";

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

    public static TheoryData<string, string> PublishedMutations()
    {
        var data = new TheoryData<string, string>();
        foreach (var mcp in new[] { "1.4.0", "1.1.0", "1.0.3" })
        {
            foreach (var mutation in new[] { "cli", "mcp", "commit", "heading", "output-line", "pin-link", "roll-forward", "drop-section" })
            {
                data.Add(mutation, mcp);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PublishedMutations))]
    public void PublishedRouteGuard_RejectsMismatchedOrMissingReleaseClaims(string mutation, string mcp)
    {
        var guide = ReadInstallGuide();
        Assert.True(PublishedRouteMatches(guide), "The original published route must be valid.");
        var pair = PublishedPairs.Single(candidate => candidate.Mcp == mcp);
        var section = SectionOf(guide, pair.Heading);
        var changedSection = mutation switch
        {
            "cli" => section.Replace("--global vouchfx --version " + pair.Cli,
                "--global vouchfx --version 9.9.9", StringComparison.Ordinal),
            "mcp" => section.Replace("--global Vouchfx.Mcp --version " + pair.Mcp,
                "--global Vouchfx.Mcp --version 9.9.9", StringComparison.Ordinal),
            "commit" => section.Replace(pair.Commit, new string('0', 40), StringComparison.Ordinal),
            "heading" => section.Replace(PublishedHeadingPrefix + " MCP", "Unrecognised package: MCP", StringComparison.Ordinal),
            "output-line" => section.Replace($"`{pair.Cli}+{pair.Commit}`", "`output line removed`", StringComparison.Ordinal),
            "pin-link" => section.Replace($"/blob/v{pair.Mcp}/ENGINE_PIN", "/blob/v9.9.9/ENGINE_PIN", StringComparison.Ordinal),
            "roll-forward" => FlipRollForward(section, pair),
            _ => string.Empty,
        };
        Assert.NotEqual(section, changedSection);
        Assert.False(PublishedRouteMatches(guide.Replace(section, changedSection, StringComparison.Ordinal)),
            "The published-route guard must reject altered release claims.");
    }

    [Fact]
    public void PublishedRouteGuard_RejectsAnUnknownPublishedPairing()
    {
        var guide = ReadInstallGuide();
        var pair = PublishedPairs.Single(candidate => candidate.Mcp == "1.1.0");
        var section = SectionOf(guide, pair.Heading);
        var unknown = section.Replace("1.1.0", "1.1.1", StringComparison.Ordinal)
            .Replace("1.2.0", "1.2.1", StringComparison.Ordinal);
        Assert.False(PublishedRouteMatches(guide + "\n" + unknown),
            "A published section naming an unrecognised MCP and CLI pairing must fail.");
    }

    [Fact]
    public void PublishedRouteGuard_RejectsAGuideWithNoPublishedSection()
    {
        Assert.False(PublishedRouteMatches(string.Join('\n', SourceRoute(ReadInstallGuide().Split('\n')))),
            "A guide with no published route must fail.");
    }

    [Fact]
    public void SourceRouteGuard_RejectsAPublishedVersionInSourceCommands()
    {
        var expected = CliVersionNormaliser.Normalise(EnginePin.Load(RepoLayout.ResolveEnginePinPath()).Version);
        var text = SourceRoute(ReadInstallGuide().Split('\n'));
        Assert.True(SourceClaimsMatch(text, expected), "The original source route must be valid.");
        var changed = text.Replace("--global vouchfx --version " + expected,
            "--global vouchfx --version " + PublishedPairs[^1].Cli, StringComparison.Ordinal);
        Assert.False(SourceClaimsMatch(changed, expected),
            "The source-route guard must reject a published CLI version substituted into source commands.");
    }

    /// <summary>Adds the flag to a pair that must not carry it, or strips it from a pair that must.</summary>
    private static string FlipRollForward(string section, PublishedPair pair)
    {
        var lines = section.Split('\n').Select(line =>
        {
            if (!IsInstallCommand(line))
            {
                return line;
            }

            return pair.RequiresRollForward
                ? line.Replace(" " + PublishedPair.RollForwardFlag, string.Empty, StringComparison.Ordinal)
                : line.TrimEnd('\r') + " " + PublishedPair.RollForwardFlag;
        });
        return string.Join('\n', lines);
    }

    private static bool IsInstallCommand(string line) =>
        line.TrimStart().StartsWith("dotnet tool install ", StringComparison.Ordinal);

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

    /// <summary>
    /// Every published section must be exactly one known pairing and carry that pairing's complete
    /// triple: its own install commands, the expected CLI output line and its release pin link.
    /// </summary>
    private static bool PublishedRouteMatches(string guide)
    {
        var lines = guide.Split('\n');
        var headings = PublishedHeadings(lines);
        if (headings.Count != PublishedPairs.Length
            || !headings.OrderBy(h => h, StringComparer.Ordinal)
                .SequenceEqual(PublishedPairs.Select(p => p.Heading).OrderBy(h => h, StringComparer.Ordinal)))
        {
            return false;
        }

        return PublishedPairs.All(pair => PublishedSectionMatches(SectionOf(guide, pair.Heading), pair));
    }

    private static bool PublishedSectionMatches(string text, PublishedPair pair)
    {
        var packageVersions = Regex.Matches(text,
            @"\bVouchfx\.Mcp --version (\d[^\s`]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var installLines = text.Split('\n').Where(IsInstallCommand).ToArray();
        var flag = pair.RequiresRollForward ? " " + PublishedPair.RollForwardFlag : string.Empty;
        var flagMatches = installLines.All(line =>
            line.Contains(PublishedPair.RollForwardFlag, StringComparison.Ordinal) == pair.RequiresRollForward);
        return packageVersions.Length == 1 && packageVersions[0] == pair.Mcp
            && SourceClaimsMatch(text, pair.Cli)
            && flagMatches
            && text.Contains($"dotnet tool install --global Vouchfx.Mcp --version {pair.Mcp}{flag}", StringComparison.Ordinal)
            && text.Contains($"dotnet tool install --global vouchfx --version {pair.Cli}{flag}", StringComparison.Ordinal)
            && text.Contains($"`{pair.Cli}+{pair.Commit}`", StringComparison.Ordinal)
            && text.Contains($"/blob/v{pair.Mcp}/ENGINE_PIN", StringComparison.Ordinal);
    }

    private static List<string> PublishedHeadings(IEnumerable<string> lines)
    {
        var headings = new List<string>();
        var inFence = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            var heading = inFence ? Match.Empty : Heading.Match(line.TrimEnd('\r'));
            if (heading.Success && heading.Groups[2].Value.StartsWith(PublishedHeadingPrefix, StringComparison.Ordinal))
            {
                headings.Add(heading.Groups[2].Value);
            }
        }

        return headings;
    }

    /// <summary>The heading line and everything nested under it, or empty when the heading is absent.</summary>
    private static string SectionOf(string guide, string heading) => FilterSections(guide.Split('\n'),
        title => string.Equals(title.TrimEnd('\r'), heading, StringComparison.Ordinal), excludeMatches: false);

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
