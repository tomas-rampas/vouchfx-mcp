using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

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
        Assert.Equal(3, resources.Count);
        Assert.Equal(7, templates.Count);

        var section = ResourcesSection();
        Assert.Contains("three concrete resources", section, StringComparison.OrdinalIgnoreCase);

        // "seven URI templates", not "seven families" — the two are different numbers here (errors
        // are one family under two templates) and the docs previously used the words
        // interchangeably. This pins the wording that matches what the wire returns.
        Assert.Contains("seven URI templates", section, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(consoleOut.Writer.ToString());
    }

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
    private static string ResourcesSection()
    {
        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, "docs", "tools-and-resources.md");
        Assert.True(File.Exists(path), $"Expected the tracked reference doc at '{path}'.");

        var text = File.ReadAllText(path);
        var start = text.IndexOf(ResourcesHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Expected a '{ResourcesHeading}' heading in docs/tools-and-resources.md.");

        var next = text.IndexOf("\n## ", start + ResourcesHeading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }
}
