using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using Vouchfx.Mcp.Prompts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Every <c>**Name**:</c> line in <c>docs/tools-and-resources.md</c>'s Resources section names a
/// resource this server actually advertises, and every advertised name appears there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists</b> (a spec-compliance review's prescription, after all five of US-S5-01's new
/// resource entries shipped with names that differed from the registered ones — "Workspace Suite
/// Index" for <c>vouchfx workspace suite index</c>, "vouchfx Schema" for
/// <c>vouchfx composed JSON Schema</c>, "Example Suite: {title}" for a name with no substitution in
/// it at all, and so on). Every one of those was written by paraphrasing the code instead of
/// transcribing it, every one passed the whole test suite, and every one would have sent a host
/// looking for a resource by a name that does not exist.
/// </para>
/// <para>
/// The check is deliberately CHEAP and NARROW — string equality between a documented name and an
/// advertised one — because the failure it prevents is equally cheap and narrow. It says nothing
/// about whether the prose around each name is accurate; that remains a reviewer's job. What it
/// removes is the whole class of "the documented identifier is not the real identifier", which is
/// the part a reviewer is least likely to catch by reading.
/// </para>
/// <para>
/// Mirrors <see cref="ErrorCatalogueFilesystemParityTests"/>' shape: read the tracked Markdown from
/// the repository, derive the real set from the running server, and assert both directions so
/// neither a stale doc entry nor an undocumented resource can survive.
/// </para>
/// </remarks>
public class ResourceDocumentationParityTests
{
    /// <summary>The heading that opens the section this test reads, and the one that closes it.</summary>
    private const string ResourcesHeading = "## Resources";

    /// <summary>
    /// A documented resource name: a bullet of the form <c>- **Name**: …</c>, or the inline
    /// <c>— **Name**: …</c> form the three run families use (they share one <c>####</c> heading, so
    /// each needs its name on its own URI line).
    /// </summary>
    private static readonly Regex DocumentedName =
        new(@"\*\*Name\*\*:\s*(?<name>[^\r\n]+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public async Task EveryDocumentedResourceName_IsOneTheServerActuallyAdvertises()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var advertised = await AdvertisedNamesAsync(harness, cts.Token);
        var documented = DocumentedNames();

        // Anti-vacuity: a renamed heading or a restructured section would otherwise make this pass
        // over nothing at all.
        Assert.True(
            documented.Count >= 7,
            $"Expected the Resources section to document at least seven names; found {documented.Count}. "
            + "Has the section been restructured, or the '**Name**:' convention dropped?");

        // Both directions. A documented name that is not advertised sends a host looking for
        // something that does not exist; an advertised name that is not documented is a resource
        // nobody can discover from the docs.
        Assert.Equal(
            advertised.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            documented.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task TheDocumentedResourceAndTemplateCOUNTS_MatchWhatTheServerReturns()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);
        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        // The counts the docs state in prose, pinned against the wire. US-S5-01 shipped with three
        // separate documents disagreeing about these numbers — one of them internally contradictory
        // in a single sentence — so they are now measured rather than asserted in English alone.
        //
        // Anti-vacuity floors rather than exact equalities for the wire side: an exact count is a
        // second place to edit when a resource lands, and US-S5-05 measured the cost of that — adding
        // one resource turned this guard red on a number that was never the thing being protected.
        // What IS protected is the docs agreeing with the wire, and that is the assertion below,
        // where the count WORD is derived from the count rather than typed (the treatment
        // TheDocumentedPromptCount_MatchesWhatTheServerReturns already gets, for its own recorded
        // reason).
        Assert.True(resources.Count >= 4, $"Expected at least four concrete resources; found {resources.Count}.");
        Assert.True(templates.Count >= 7, $"Expected at least seven URI templates; found {templates.Count}.");

        var section = ResourcesSection();
        Assert.Contains($"{NumberWord(resources.Count)} concrete resources", section, StringComparison.OrdinalIgnoreCase);

        // "URI templates", not "families" — the two are different numbers here (errors are one family
        // under two templates) and the docs previously used the words interchangeably. This pins the
        // wording that matches what the wire returns.
        Assert.Contains($"{NumberWord(templates.Count)} URI templates", section, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The landing page's "<c>N documentation resources</c>" claim matches how many documentation
    /// resources the server actually advertises (US-S5-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A count in unguarded marketing prose that a story can falsify.</b> <c>site/index.html</c>
    /// states it seven times — the meta, og: and twitter: descriptions, both image alts, the hero and
    /// the eyebrow — and nothing renders those, so they drift silently; the landing-page parity guard
    /// beside this one pins only the TOOL count word. Adding
    /// <c>vouchfx://docs/dsl-guide</c> made "two documentation resources" wrong in exactly that
    /// invisible way.
    /// </para>
    /// <para>
    /// <b>What counts as a documentation resource is derived, not listed:</b> a concrete resource whose
    /// URI sits under the <c>vouchfx-docs:</c> scheme or the <c>vouchfx://docs/</c> prefix — the two
    /// spellings this server serves documentation under (plan D4 keeps the Sprint 1 scheme rather than
    /// renaming it). The workspace spec index is deliberately NOT one: it is an index of the user's own
    /// suites, not documentation about vouchfx.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheLandingPagesDocumentationResourceCount_MatchesWhatTheServerAdvertises()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var documentationResources = (await harness.Client.ListResourcesAsync(cancellationToken: cts.Token))
            .Count(resource =>
                resource.Uri.StartsWith("vouchfx-docs:", StringComparison.Ordinal)
                || resource.Uri.StartsWith("vouchfx://docs/", StringComparison.Ordinal));

        var expected = NumberWord(documentationResources);
        var landingPage = ReadRepoFile("site", "index.html");

        // Anti-vacuity, in the shape the stale-promise guard uses: find every count word the page
        // states for this phrase, then require them all to be the right one. Finding NONE means the
        // copy was rewritten and this check has gone hollow — that is a failure, not a pass.
        var stated = DocumentationResourceCount
            .Matches(landingPage)
            .Select(match => match.Groups["word"].Value)
            .ToArray();

        Assert.True(
            stated.Length > 0,
            "site/index.html states no '<number-word> documentation resources' count at all — this "
            + "check has gone vacuous. Either the copy was rewritten (update the pattern) or the "
            + "claim was dropped from a page that used to carry it.");

        var wrong = stated
            .Where(word => !string.Equals(word, expected, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            wrong.Length == 0,
            $"site/index.html advertises '{string.Join("/", wrong)} documentation resources' but the "
            + $"server advertises {documentationResources} ('{expected}'). Sweep EVERY occurrence — "
            + "the meta, og: and twitter: descriptions are what a link preview shows.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>A "<c>N documentation resources</c>" claim in any number-word spelling.</summary>
    /// <remarks>
    /// <b>An optional adjective is allowed between the count and the noun</b>, because the landing
    /// page carried "two VENDORED documentation resources" in two places and this pattern walked
    /// straight past both — a stale count sitting inside the very phrase the guard was written to
    /// pin. Matching the adjective is what makes the guard cover the sentence a copywriter actually
    /// writes rather than the one the guard's author happened to imagine.
    /// </remarks>
    private static readonly Regex DocumentationResourceCount =
        new(@"\b(?<word>one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:\w+\s+)?documentation\s+resources?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The landing page's "<c>N MCP prompts</c>" claim matches how many prompts the server advertises.
    /// </summary>
    /// <remarks>
    /// The dimension a drift audit named as uncovered: <c>site/index.html</c> is guarded for its tool
    /// count (<c>LandingPageToolParityTests</c>) and, since US-S5-05, its documentation-resource
    /// count — but it stated no prompt count at all until Sprint 5's wrap-up added one, and an
    /// unguarded new number is the next stale number. Deliberately count-ONLY, unlike
    /// <see cref="TheDocumentedPromptCount_MatchesWhatTheServerReturns"/>, which additionally requires
    /// every prompt to be NAMED: that is the right bar for a reference document a reader consults to
    /// find a prompt, and the wrong one for a landing page, which is not where anyone looks up an
    /// identifier.
    /// </remarks>
    [Fact]
    public async Task TheLandingPagesPromptCount_MatchesWhatTheServerAdvertises()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);
        var expected = NumberWord(prompts.Count);
        var landingPage = ReadRepoFile("site", "index.html");

        var stated = PromptCount
            .Matches(landingPage)
            .Select(match => match.Groups["word"].Value)
            .ToArray();

        Assert.True(
            stated.Length > 0,
            "site/index.html states no '<number-word> MCP prompts' count at all — this check has gone "
            + "vacuous. Either the copy was rewritten (update the pattern) or the claim was dropped.");

        var wrong = stated
            .Where(word => !string.Equals(word, expected, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            wrong.Length == 0,
            $"site/index.html advertises '{string.Join("/", wrong)} MCP prompts' but the server "
            + $"advertises {prompts.Count} ('{expected}'). Sweep EVERY occurrence — the meta, og: and "
            + "twitter: descriptions are what a link preview shows.");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>An "<c>N MCP prompts</c>" claim in any number-word spelling.</summary>
    private static readonly Regex PromptCount =
        new(@"\b(?<word>one|two|three|four|five|six|seven|eight|nine|ten)\s+MCP\s+prompts?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every <see cref="Vouchfx.Mcp.Specs.WorkspaceSpecIndexReasons"/> literal is documented, and every
    /// value the docs claim exists is a real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added because this guard missed exactly the drift it was built for, in a dimension it did not
    /// cover.</b> The reason vocabulary is written in the docs as an exhaustive list — "the complete
    /// set of values is" — and it went stale by one member the moment
    /// <c>spec-worker-unavailable</c> was introduced, while the name and count checks stayed green. A
    /// list a reader is told is complete has to be pinned like one.
    /// </para>
    /// <para>
    /// Reflection over the constants rather than a hand-written expected array, deliberately: a
    /// hand-written one is a third copy that can go stale in its own right, and the whole point is
    /// that adding a constant must fail this test until the docs are updated.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryWorkspaceSpecIndexReason_IsDocumented_AndEveryDocumentedOneIsReal()
    {
        var declared = typeof(Vouchfx.Mcp.Specs.WorkspaceSpecIndexReasons)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        // Anti-vacuity: a renamed type or a reflection filter that matched nothing would otherwise
        // make both directions below trivially true.
        Assert.True(declared.Length >= 5, $"Expected at least five reason literals; found {declared.Length}.");

        var section = ResourcesSection();

        // Direction 1 — every real value appears in the docs. This is the one that would have caught
        // the omission.
        foreach (var reason in declared)
        {
            Assert.True(
                section.Contains($"`{reason}`", StringComparison.Ordinal),
                $"WorkspaceSpecIndexReasons value '{reason}' is not documented in "
                + "docs/tools-and-resources.md's Resources section. That list is written as exhaustive, "
                + "so a new reason must be added to it.");
        }

        // Direction 2 — every kebab-case value the docs present as a reason is real. Scoped to the
        // bullet block that introduces the vocabulary, so an unrelated backticked token elsewhere in
        // the section is not mistaken for one.
        foreach (var documented in DocumentedReasons(section))
        {
            Assert.Contains(documented, declared);
        }
    }

    /// <summary>
    /// The backticked kebab-case tokens inside the <c>reason</c> bullet block — the docs' own claim
    /// about what the vocabulary is.
    /// </summary>
    private static IEnumerable<string> DocumentedReasons(string section)
    {
        const string marker = "- `reason` is **omitted entirely**";
        var start = section.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the `reason` vocabulary bullet in the Resources section.");

        // The block runs to the next top-level bullet at the same indentation.
        var end = section.IndexOf("\n- ", start + marker.Length, StringComparison.Ordinal);
        var block = end < 0 ? section[start..] : section[start..end];

        return ReasonToken
            .Matches(block)
            .Select(match => match.Groups["reason"].Value)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>A backticked all-lower-case kebab token — the shape every reason literal has.</summary>
    private static readonly Regex ReasonToken =
        new(@"`(?<reason>[a-z]+(?:-[a-z]+)+)`", RegexOptions.Compiled);

    /// <summary>
    /// Every advertised prompt and every one of its arguments is documented, and every argument the
    /// docs claim exists is really declared (US-S5-02).
    /// </summary>
    /// <remarks>
    /// <b>Established now, with one prompt, because two more stories add three more.</b> US-S5-03 and
    /// US-S5-04 each ship prompts with their own argument sets, and the failure this guards — a
    /// documented argument name that is not the real one — is the same class that the resource
    /// <c>**Name**</c> drift belonged to. Cheaper to extend than to retrofit after the drift.
    /// </remarks>
    [Fact]
    public async Task EveryAdvertisedPrompt_AndEveryArgument_IsDocumented()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);
        var section = PromptsSection();

        // Anti-vacuity: a restructured section or a prompt-free server makes the loops below empty.
        Assert.NotEmpty(prompts);
        Assert.Contains("### `", section, StringComparison.Ordinal);

        foreach (var prompt in prompts)
        {
            Assert.True(
                section.Contains($"### `{prompt.Name}`", StringComparison.Ordinal),
                $"Prompt '{prompt.Name}' has no '### `{prompt.Name}`' heading in the Prompts section.");

            Assert.True(
                section.Contains($"**Name**: {prompt.Name}", StringComparison.Ordinal),
                $"Prompt '{prompt.Name}' is not documented under its advertised name.");

            foreach (var argument in prompt.ProtocolPrompt.Arguments ?? [])
            {
                // Backticked, i.e. in the argument table — not merely mentioned somewhere in prose.
                Assert.True(
                    section.Contains($"`{argument.Name}`", StringComparison.Ordinal),
                    $"Prompt '{prompt.Name}' argument '{argument.Name}' is not documented.");
            }
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task TheDocumentedPromptCount_MatchesWhatTheServerReturns()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: cts.Token);

        Assert.Equal(PromptCatalogue.All.Count, prompts.Count);

        // The prose count, pinned against the wire — the same treatment the resource counts get, and
        // for the same reason: three documents disagreed about those before this guard existed.
        // THE COUNT WORD IS DERIVED FROM THE WIRE, not hardcoded (a code review's finding). Every
        // previous version of this guard spelled the number out, so advancing the count meant editing
        // the guard as well as the docs — and a guard you must edit in lockstep with the thing it
        // guards is one edit away from being edited to agree with a mistake.
        var expectedCountWord = NumberWord(prompts.Count);

        foreach (var (file, text) in DocumentedSurfaces())
        {
            Assert.True(
                text.Contains($"{expectedCountWord} MCP prompt", StringComparison.OrdinalIgnoreCase),
                $"{file} does not state the prompt count as '{expectedCountWord} MCP prompt(s)'. "
                + $"The server advertises {prompts.Count}.");

            // And each names every prompt it claims to describe — the half that caught a document
            // stating a count of two while describing one.
            foreach (var prompt in prompts)
            {
                Assert.True(
                    text.Contains(prompt.Name, StringComparison.Ordinal),
                    $"{file} states a prompt count but never names '{prompt.Name}'.");
            }

            // A "N more prompts follow" promise must not outlive the prompts it promised. Matched by
            // SHAPE rather than by a specific number, so it catches the next stale promise too — the
            // previous version hardcoded "Three", and "Two more prompts follow" then went stale
            // unnoticed until a reviewer read it.
            Assert.False(
                StalePromiseShape.IsMatch(text),
                $"{file} still promises more prompts to follow; all {prompts.Count} have landed. "
                + $"Matched: '{StalePromiseShape.Match(text).Value}'.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>The three documents that state a prompt count.</summary>
    private static IEnumerable<(string File, string Text)> DocumentedSurfaces()
    {
        yield return ("docs/tools-and-resources.md", PromptsSection());
        yield return ("README.md", ReadRepoFile("README.md"));
        yield return ("docs/overview.md", ReadRepoFile("docs", "overview.md"));
    }

    /// <summary>A "<c>N more prompts follow</c>" promise in any spelling.</summary>
    private static readonly Regex StalePromiseShape =
        new(@"\b(one|two|three|four|\d+)\s+more\s+prompts?\s+follow", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The English word for a small count — how the docs spell it in prose.
    /// </summary>
    /// <remarks>
    /// Deliberately small and deliberately THROWS past its range: the alternative (falling back to
    /// digits) would let this guard quietly start matching text no document contains, which is a
    /// vacuous pass. Extending it is a deliberate edit made alongside the docs.
    /// </remarks>
    private static string NumberWord(int count) => count switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        10 => "ten",
        _ => throw new InvalidOperationException(
            $"No number word for {count} — extend NumberWord alongside the docs."),
    };

    private static string ReadRepoFile(params string[] segments)
    {
        var path = Path.Combine([SourceGuardScan.RepoRoot.FullName, .. segments]);
        Assert.True(File.Exists(path), $"Expected a tracked file at '{path}'.");
        return File.ReadAllText(path);
    }

    /// <summary>The text of <c>docs/tools-and-resources.md</c>'s <c>## Prompts</c> section.</summary>
    private static string PromptsSection() => SectionOf("## Prompts");

    private static async Task<List<string>> AdvertisedNamesAsync(
        McpTestHarness harness, CancellationToken cancellationToken)
    {
        var names = new List<string>();

        foreach (var resource in await harness.Client.ListResourcesAsync(cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(resource.Name))
            {
                names.Add(resource.Name);
            }
        }

        foreach (var template in await harness.Client.ListResourceTemplatesAsync(cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(template.ProtocolResourceTemplate.Name))
            {
                names.Add(template.ProtocolResourceTemplate.Name!);
            }
        }

        // DISTINCT, because the errors catalogue is deliberately served under two URI templates with
        // ONE name (plan D4: the Sprint 1 URI and its Sprint 5 alias are the same resource). The docs
        // name it once too, under a single "Error pages (two URI schemes)" heading, so deduplicating
        // is what makes the two sides comparable rather than a fudge.
        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    private static List<string> DocumentedNames() =>
        [.. DocumentedName
            .Matches(ResourcesSection())
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The text of <c>docs/tools-and-resources.md</c> from its <c>## Resources</c> heading to the
    /// next top-level heading (or end of file).
    /// </summary>
    /// <remarks>
    /// Scoped to that section rather than the whole document so a <c>**Name**:</c> appearing in a
    /// TOOL's parameter table — a plausible future addition — is not mistaken for a resource name.
    /// </remarks>
    private static string ResourcesSection() => SectionOf(ResourcesHeading);

    /// <summary>
    /// The text of <c>docs/tools-and-resources.md</c> from <paramref name="heading"/> to the next
    /// top-level heading (or end of file).
    /// </summary>
    /// <remarks>
    /// Scoped to one section rather than the whole document so a <c>**Name**:</c> or a backticked
    /// token appearing in a TOOL's parameter table is not mistaken for a resource or prompt one.
    /// </remarks>
    private static string SectionOf(string heading)
    {
        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, "docs", "tools-and-resources.md");
        Assert.True(File.Exists(path), $"Expected the tracked reference doc at '{path}'.");

        var text = File.ReadAllText(path);
        var start = text.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Expected a '{heading}' heading in docs/tools-and-resources.md.");

        var next = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }
}
