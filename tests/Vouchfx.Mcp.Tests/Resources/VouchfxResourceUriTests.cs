using Vouchfx.Mcp.Resources;

namespace Vouchfx.Mcp.Tests.Resources;

/// <summary>
/// US-S5-01: the <c>vouchfx://</c> URI catalogue's own invariants — the properties that make it safe
/// for every registry and every golden test to read its templates from one place.
/// </summary>
/// <remarks>
/// These are cheap, structural assertions rather than behaviour, and they earn their place because a
/// URI is a PUBLISHED CONTRACT a host caches against: a typo in a constant would otherwise be
/// invisible (the registry advertises it, the golden test asserts the same constant, both agree) and
/// would only surface as a host that cannot resolve a documented URI. Asserting the SHAPE of each
/// constant is what closes that, because the shape is stated here in literal text that a typo cannot
/// travel through.
/// </remarks>
public class VouchfxResourceUriTests
{
    [Fact]
    public void EveryTemplate_UsesTheVouchfxSchemeAndCarriesAnExpansion()
    {
        Assert.NotEmpty(VouchfxResourceUris.AllTemplates);

        foreach (var template in VouchfxResourceUris.AllTemplates)
        {
            Assert.StartsWith($"{VouchfxResourceUris.Scheme}://", template, StringComparison.Ordinal);

            // The defining property of a TEMPLATE, and the one the MCP SDK classifies on: without an
            // expansion this would be a concrete resource and belong in resources/list instead.
            Assert.Contains("{", template, StringComparison.Ordinal);
            Assert.Contains("}", template, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWorkspaceSpecsUri_IsConcrete_SoItBelongsInResourcesListRatherThanTheTemplateList()
    {
        // The resolution of US-S5-01's Gherkin, pinned rather than left in prose: the story lists
        // this URI among the templates, it carries no expansion, and the protocol (and the SDK)
        // separate the two listings on exactly that property. See VouchfxResourceUris' own remarks.
        Assert.DoesNotContain("{", VouchfxResourceUris.WorkspaceSpecsUri, StringComparison.Ordinal);
        Assert.DoesNotContain(VouchfxResourceUris.WorkspaceSpecsUri, VouchfxResourceUris.AllTemplates);
    }

    [Fact]
    public void TheTemplateList_HasNoDuplicates()
    {
        Assert.Equal(
            VouchfxResourceUris.AllTemplates.Count,
            VouchfxResourceUris.AllTemplates.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheErrorPageAlias_IsTheSameStringDiagnosticResourceRegistryAdvertises()
    {
        // Two constants, one value — so a future edit to either side cannot leave the alias
        // advertised under one spelling and asserted under another.
        Assert.Equal(VouchfxResourceUris.ErrorPageTemplate, DiagnosticResourceRegistry.AliasUriTemplate);

        // And the alias is genuinely a SECOND URI, not a rename of the Sprint 1 one (plan D4).
        Assert.NotEqual(DiagnosticResourceRegistry.UriTemplate, DiagnosticResourceRegistry.AliasUriTemplate);
        Assert.StartsWith("vouchfx-docs:///", DiagnosticResourceRegistry.UriTemplate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vouchfx://schema/{version}")]
    [InlineData("vouchfx://docs/errors/{code}")]
    [InlineData("vouchfx://examples/{name}")]
    [InlineData("vouchfx://runs/{runId}/verdict")]
    [InlineData("vouchfx://runs/{runId}/events")]
    [InlineData("vouchfx://runs/{runId}/logs/{container}")]
    public void EachTemplateFromTheStorysOwnGherkin_IsAdvertised(string uriTemplate)
    {
        // The literals are re-typed here ON PURPOSE, from US-S5-01's Gherkin rather than from the
        // constants: a test that reads the same constant the production code reads can only prove
        // they are consistent with each other, never that either matches what the story asked for.
        Assert.Contains(uriTemplate, VouchfxResourceUris.AllTemplates);
    }

    [Fact]
    public void TheAdvertisedTemplateSet_IsExactlyTheStorysSix() =>
        // Fail-closed, mirroring RealToolMetaMcpTests' set-equality pattern: a seventh template added
        // anywhere fails here until it is deliberately added to this list AND swept by the golden
        // tests, so no template can reach a host un-reviewed.
        Assert.Equal(StoryTemplateUris, VouchfxResourceUris.AllTemplates);

    /// <summary>
    /// US-S5-01's Gherkin template set, re-typed from the story rather than read from the production
    /// constants — see <see cref="EachTemplateFromTheStorysOwnGherkin_IsAdvertised"/> for why that
    /// duplication is the point. A <c>static readonly</c> field rather than an inline array literal
    /// per CA1861.
    /// </summary>
    private static readonly string[] StoryTemplateUris =
    [
        "vouchfx://schema/{version}",
        "vouchfx://docs/errors/{code}",
        "vouchfx://examples/{name}",
        "vouchfx://runs/{runId}/verdict",
        "vouchfx://runs/{runId}/events",
        "vouchfx://runs/{runId}/logs/{container}",
    ];
}
