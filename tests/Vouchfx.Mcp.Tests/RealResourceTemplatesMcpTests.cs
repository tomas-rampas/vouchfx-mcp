using ModelContextProtocol.Client;
using Vouchfx.Mcp.Docs;
using Vouchfx.Mcp.Resources;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S5-01 AC-001: <c>resources/templates/list</c> advertises every URI template this story adds,
/// with a human-readable name and description each — and <c>resources/list</c> keeps advertising the
/// two vendored documents it always has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven over the real MCP wire</b> through <see cref="McpTestHarness"/>, not against the
/// registries directly, because the thing under test is what a HOST sees: the SDK is what routes an
/// <see cref="ModelContextProtocol.Server.McpServerResource"/> into one listing or the other,
/// according to whether its <c>UriTemplate</c> carries a <c>{…}</c> expansion, and a unit test over
/// the registries could not observe that routing at all.
/// </para>
/// <para>
/// <b>The template list is asserted as an EXACT SET</b>, not as "contains at least". A seventh
/// template added anywhere — by this repo or by a future SDK behaviour change — fails here until it
/// is deliberately added, which is the same fail-closed shape <c>RealSecretHygieneMcpTests</c> and
/// <c>RealToolMetaMcpTests</c> already use for the tool set.
/// </para>
/// <para>
/// <b>A set, and NOT a sequence, because the SDK does not preserve registration order here</b>
/// (measured, and re-measured across an SDK major: registering the templates in
/// <c>ResourceRegistry</c>'s declared order produces <c>resources/templates/list</c> in an unrelated
/// order, consistent with the SDK holding its resource collection in a hash-keyed structure. The
/// order differs BETWEEN SDK versions too — <c>runs/{runId}/events</c> came first on 1.4.1,
/// <c>runs/{runId}/logs/{container}</c> on 2.2.0 — which is exactly the breakage a sequence
/// assertion would have taken on the bump, and did not). So the ordering of that listing is the SDK's to decide and nothing in
/// this repository may claim to control it; both sides of the comparison are sorted ordinally before
/// comparing, which is what makes this assertion about MEMBERSHIP rather than about an implementation
/// detail that would break on an SDK bump for no product reason.
/// </para>
/// </remarks>
public class RealResourceTemplatesMcpTests
{
    /// <summary>
    /// The complete advertised template set — Sprint 1's diagnostic template plus Sprint 5's six.
    /// Compared as a sorted SET; see this type's remarks.
    /// </summary>
    private static readonly string[] ExpectedTemplateUris =
    [
        DiagnosticResourceRegistry.UriTemplate,
        .. VouchfxResourceUris.AllTemplates,
    ];

    [Fact]
    public async Task ResourceTemplatesList_AdvertisesExactlyTheExpectedSet()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        Assert.Equal(
            ExpectedTemplateUris.OrderBy(uri => uri, StringComparer.Ordinal).ToArray(),
            templates
                .Select(template => template.ProtocolResourceTemplate.UriTemplate)
                .OrderBy(uri => uri, StringComparer.Ordinal)
                .ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task EveryAdvertisedTemplate_CarriesAHumanReadableNameAndDescription()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        // Anti-vacuity: the loop below proves nothing over an empty listing.
        Assert.Equal(ExpectedTemplateUris.Length, templates.Count);

        foreach (var template in templates)
        {
            var protocolTemplate = template.ProtocolResourceTemplate;

            Assert.False(
                string.IsNullOrWhiteSpace(protocolTemplate.Name),
                $"{protocolTemplate.UriTemplate} has no name.");

            // The description is what a host shows a user deciding whether to read the resource, and
            // AC-001 requires one per template. The length floor is what stops a placeholder passing.
            Assert.True(
                protocolTemplate.Description?.Length > 40,
                $"{protocolTemplate.UriTemplate} has no substantive description.");

            Assert.False(
                string.IsNullOrWhiteSpace(protocolTemplate.MimeType),
                $"{protocolTemplate.UriTemplate} declares no MIME type.");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ResourcesList_AdvertisesExactlyTheVendoredDocuments_TheSpecIndex_AndTheDslGuide()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);

        // Sorted for the reason the template assertion is (see this type's remarks): the SDK owns
        // this listing's order and nothing here may claim to. What IS this repository's to assert is
        // membership — the two Sprint 1 documents are still advertised, unchanged, and Sprint 5 adds
        // exactly two concrete resources beside them (US-S5-01's spec index, US-S5-05's DSL guide).
        Assert.Equal(
            new[]
            {
                VendoredDocuments.LanguageReference.ResourceUri,
                VendoredDocuments.Recipes.ResourceUri,
                VouchfxResourceUris.WorkspaceSpecsUri,
                VouchfxResourceUris.DslGuideUri,
            }.OrderBy(uri => uri, StringComparer.Ordinal).ToArray(),
            resources.Select(resource => resource.Uri).OrderBy(uri => uri, StringComparer.Ordinal).ToArray());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NoTemplateAppearsInBothListings()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var resources = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);
        var templates = await harness.Client.ListResourceTemplatesAsync(cancellationToken: cts.Token);

        // The protocol's own split, asserted rather than assumed: a concrete resource is readable
        // directly and a template must be expanded first, so a URI in both listings would tell a host
        // two contradictory things about how to use it.
        Assert.Empty(
            resources
                .Select(resource => resource.Uri)
                .Intersect(
                    templates.Select(template => template.ProtocolResourceTemplate.UriTemplate),
                    StringComparer.Ordinal));

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
