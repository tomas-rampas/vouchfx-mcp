using System.Globalization;
using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A filesystem-parity gate between the tools the LIVE server advertises over <c>tools/list</c> and
/// the published landing page (<c>site/index.html</c>, plus the tool count on <c>site/404.html</c>) —
/// the same "derive from the real thing, never trust a hand-maintained copy" posture
/// <see cref="ErrorCatalogueFilesystemParityTests"/> applies to <c>docs/errors/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs a test at all:</b> <c>scripts/build_site.py</c> copies <c>site/</c> VERBATIM and
/// <c>pages.yml</c> deploys it, so <c>site/index.html</c> IS the homepage at
/// vouchfx-mcp.vouchfx.io — including its meta, Open Graph and Twitter descriptions, which is what a
/// link preview shows. Nothing else in this repo compares it against the server, so a tool added to
/// <c>ToolRegistry</c> shipped a landing page advertising the previous count and missing a card, with
/// no build, test, format or drift gate saying a word. That is exactly what happened when
/// <c>get_schema</c> landed, and it is the class of drift this file exists to make loud.
/// </para>
/// <para>
/// <b>Both directions, and the prose count too.</b> A per-tool anchor check alone would still pass a
/// page carrying a stale card for a deleted tool, and — the failure actually observed — would say
/// nothing about the sentences that spell the count out in words. So this class checks three things:
/// every advertised tool has a card link, the grid holds no card that is not an advertised tool, and
/// every "&lt;number-word&gt; tools" phrase names the real count — across the site pages, the docs
/// that state it, and <c>scripts/build_site.py</c>, whose own hard-coded copy of that copy drifted
/// past a sweep of the rendered pages once already.
/// </para>
/// </remarks>
public class LandingPageToolParityTests
{
    /// <summary>
    /// The exact markup shape every tool card uses to link its reference-page anchor. Matched as a
    /// literal prefix rather than by parsing HTML: this is a drift gate over a hand-written file, and
    /// a tolerant parser would happily accept a card whose markup no longer matches its neighbours.
    /// </summary>
    private const string CardHrefPrefix = "<a class=\"card\" href=\"docs/tools-and-resources.html#";

    /// <summary>
    /// Spelled-out counts, indexed by the number they name — the site writes the tool count in prose
    /// ("Eleven tools. No shelling out…"), not digits, so a numeric comparison cannot see it. The
    /// table stops where the page's copy would need rewriting anyway; overrunning it fails with an
    /// instruction to extend it rather than silently skipping the check.
    /// </summary>
    private static readonly string[] NumberWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty",
    ];

    /// <summary>Every "&lt;number-word&gt; tools" phrase, however cased.</summary>
    /// <remarks>
    /// <c>\s</c> rather than a literal space so Markdown emphasis (<c>**twelve tools**</c>) and a
    /// count split across a wrapped line both still match. <c>times</c> is matched alongside
    /// <c>tools</c> because the tool reference states the count in that form as well ("omitted …
    /// to avoid repeating it twelve times"), and a per-tool count written any other way is drift
    /// this gate should see. Verified across the scanned files: <c>times</c> occurs after a number
    /// word in exactly that one sentence, so the widening adds no false subject.
    /// </remarks>
    private static readonly Regex SpelledOutToolCountPattern = new(
        @"\b(" + string.Join('|', NumberWords) + @")\s+(?:tools|times)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every tracked file that states the tool count in PROSE, and is therefore free to drift.
    /// </summary>
    /// <remarks>
    /// <b><c>scripts/build_site.py</c> is on this list for a measured reason.</b> The site generator
    /// carries its own hard-coded copy of the landing-page cards and the meta/og descriptions, used
    /// for the pages it renders rather than copies verbatim. When <c>normalize_suite</c> landed,
    /// <c>site/index.html</c> and <c>site/404.html</c> were swept to "twelve" and this test passed —
    /// while six separate "eleven tools" strings survived inside the generator, so the published
    /// pages it BUILDS still advertised the old count. Checking the rendered output was never going
    /// to catch that; checking the generator's source does.
    /// <para>
    /// The docs pages are here for the same reason at a smaller scale: <c>docs/overview.md</c> states
    /// the count in a heading, in a prose sentence, and in its status list, and nothing else compares
    /// any of them with the server.
    /// </para>
    /// </remarks>
    private static readonly string[] FilesStatingTheToolCountInProse =
    [
        "site/index.html",
        "site/404.html",
        "scripts/build_site.py",
        "docs/overview.md",
        "docs/tools-and-resources.md",
    ];

    [Fact]
    public async Task EveryAdvertisedTool_HasALandingPageCardLinkingItsDocsAnchor()
    {
        var toolNames = await AdvertisedToolNamesAsync();
        var indexHtml = ReadSiteFile("index.html");

        var missing = toolNames
            .Where(name => !indexHtml.Contains(CardHrefPrefix + name + "\"", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These tools are advertised over tools/list but have no card on site/index.html linking "
            + $"docs/tools-and-resources.html#<name>: {string.Join(", ", missing)}. Add a card "
            + "mirroring its neighbours — site/index.html is the deployed homepage, so a missing card "
            + "is a public claim that the tool does not exist.");
    }

    [Fact]
    public async Task TheLandingPageToolGrid_HoldsNoCardForAToolThatIsNotAdvertised()
    {
        var indexHtml = ReadSiteFile("index.html");

        // Counting the card-shaped links to the tool reference is enough for the reverse direction:
        // combined with the forward test above (every advertised tool HAS one), an equal count means
        // the sets match exactly. The resource links in the same page point at the same document but
        // are not cards, so they cannot inflate this.
        var cardCount = CountOccurrences(indexHtml, CardHrefPrefix);
        var toolCount = (await AdvertisedToolNamesAsync()).Count;

        Assert.True(
            cardCount == toolCount,
            $"site/index.html has {cardCount} tool-reference cards but the server advertises "
            + $"{toolCount} tools. A card survives for a tool that no longer exists, or a card was "
            + "duplicated — either way the deployed homepage misdescribes the server.");
    }

    [Fact]
    public async Task EverySpelledOutToolCount_OnTheLandingPageAndThe404_MatchesTheAdvertisedCount()
    {
        var toolCount = (await AdvertisedToolNamesAsync()).Count;

        Assert.True(
            toolCount < NumberWords.Length,
            $"The server now advertises {toolCount} tools, past the end of this test's number-word "
            + "table. Extend NumberWords rather than dropping the check.");

        var expected = NumberWords[toolCount];

        foreach (var relativePath in FilesStatingTheToolCountInProse)
        {
            var text = ReadRepoFile(relativePath);
            var stated = SpelledOutToolCountPattern.Matches(text)
                .Select(match => match.Groups[1].Value)
                .ToArray();

            // Anti-vacuity: every file listed DOES state the count in prose today (the site's hero,
            // section heading, CTA, footer and every meta/og/twitter description; the docs' own
            // headings and status lines; the generator's hard-coded card and meta copy). Finding none
            // at all means the pattern stopped matching, not that the claim went away.
            Assert.True(
                stated.Length > 0,
                $"{relativePath} states no '<number-word> tools' count at all — this check has gone "
                + "vacuous. Either the copy was rewritten (update the pattern) or the count really "
                + "was dropped from a file that used to carry it.");

            var wrong = stated
                .Where(word => !string.Equals(word, expected, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.True(
                wrong.Length == 0,
                $"{relativePath} advertises '{string.Join("/", wrong)} tools' but the server "
                + $"advertises {toolCount} ('{expected}'). Sweep EVERY occurrence — the meta, "
                + "og: and twitter: descriptions are what a link preview shows, and they drift "
                + "silently because nothing renders them.");
        }

        // The landing page also states the count as digits in its "tools real, not stubs" stat.
        var indexHtml = ReadSiteFile("index.html");
        var digits = toolCount.ToString(CultureInfo.InvariantCulture);
        Assert.True(
            indexHtml.Contains($">{digits}/{digits}<", StringComparison.Ordinal),
            $"site/index.html's 'tools real, not stubs' stat does not read {digits}/{digits}. Update "
            + "it with the rest of the count sweep (or, if the stat was deliberately removed, drop "
            + "this assertion with it).");
    }

    /// <summary>
    /// The tool names the REAL server advertises, taken over the real MCP wire protocol through
    /// <see cref="McpTestHarness"/> — never a list restated here, which would just be a third copy
    /// free to drift alongside the page this class is checking.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AdvertisedToolNamesAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var names = tools.Select(tool => tool.Name).ToArray();

        // Anti-vacuity: a harness that came up with no tools would let every direction of this gate
        // pass by having nothing to compare.
        Assert.NotEmpty(names);

        return names;
    }

    private static string ReadSiteFile(string fileName) =>
        ReadRepoFile("site/" + fileName);

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected a tracked file at '{path}'.");

        return File.ReadAllText(path);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Mirrors <see cref="ErrorCatalogueFilesystemParityTests"/>'s <c>RepoRoot</c> exactly — see that property's remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
            var repoRoot = testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

            return repoRoot;
        }
    }
}
