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

    /// <summary>
    /// The two CONCRETE <c>vouchfx://</c> URIs, re-typed for the same reason the six templates are.
    /// </summary>
    /// <remarks>
    /// <b>Added because these two had no re-typed anchor at all</b> (a peer review's finding,
    /// measured across the test tree): every assertion about
    /// <see cref="VouchfxResourceUris.WorkspaceSpecsUri"/> and
    /// <see cref="VouchfxResourceUris.DslGuideUri"/> — the registries, the goldens, the parity guards,
    /// the fail-closed resource-set equalities — reads the production constant. That is exactly the
    /// self-consistency trap this file's template block exists to escape: a typo in the constant
    /// (<c>vouchfx://docs/dsl-guilde</c>) would propagate to every one of those assertions, keep the
    /// whole suite green, and ship a URI no host could have been told to use. A published URI is a
    /// permanent promise; it is worth typing twice.
    /// </remarks>
    [Theory]
    [InlineData("vouchfx://workspace/specs")]
    [InlineData("vouchfx://docs/dsl-guide")]
    public void EachConcreteUri_MatchesTheLiteralItsStoryPublished(string uri) =>
        // Against the PRODUCTION constants, mirroring EachTemplateFromTheStorysOwnGherkin_IsAdvertised.
        //
        // An earlier version asserted `Assert.Contains(uri, StoryConcreteUris)` — the InlineData
        // literal against the re-typed array beside it, i.e. two copies of the same hand-typed text
        // and no production constant anywhere in the assertion. A typo in the constant still passed.
        // Copilot flagged it on PR #90 and was right: a re-typed anchor that never touches the thing
        // it anchors is a tautology with a reassuring name, which is worse than no test.
        Assert.Contains(
            uri,
            new[] { VouchfxResourceUris.WorkspaceSpecsUri, VouchfxResourceUris.DslGuideUri });

    [Fact]
    public void TheConcreteVouchfxUris_AreExactlyTheseTwo()
    {
        // Both directions, so the anchor cannot silently stop covering a constant: each re-typed
        // literal equals its constant, and no THIRD concrete vouchfx:// URI has appeared without a
        // deliberate edit here. (The two vendored documents are deliberately out of scope — they are
        // vouchfx-docs:/// and predate this scheme; plan D4 keeps them where they are.)
        Assert.Equal(
            StoryConcreteUris,
            new[] { VouchfxResourceUris.WorkspaceSpecsUri, VouchfxResourceUris.DslGuideUri });
    }

    /// <summary>
    /// The concrete URIs as their stories published them — US-S5-01's workspace suite index and
    /// US-S5-05's DSL guide — re-typed rather than read from the constants.
    /// </summary>
    private static readonly string[] StoryConcreteUris =
    [
        "vouchfx://workspace/specs",
        "vouchfx://docs/dsl-guide",
    ];

    // ── RunEventsUri: the expansion authority (issue #87) ───────────────────────────────────────

    /// <summary>
    /// The expansion produces the published URI — asserted against a RE-TYPED literal, for the same
    /// reason the template block above re-types its six.
    /// </summary>
    /// <remarks>
    /// A test that expanded the template itself and compared the two would prove only that
    /// <c>String.Replace</c> works. What has to hold is that the string a host receives in a tool
    /// payload is the URI this repository published, character for character — including the scheme,
    /// both separators and the trailing segment — so the expected value is typed out in full here.
    /// </remarks>
    [Fact]
    public void RunEventsUri_IsTheAdvertisedTemplateWithTheRunIdSubstituted()
    {
        const string runId = "run-0123456789abcdef0123456789abcdef";

        Assert.Equal(
            "vouchfx://runs/run-0123456789abcdef0123456789abcdef/events",
            VouchfxResourceUris.RunEventsUri(runId));
    }

    /// <summary>
    /// The expansion leaves no placeholder behind — the property that keeps the defensive branch in
    /// <see cref="VouchfxResourceUris.RunEventsUri"/> unreachable.
    /// </summary>
    /// <remarks>
    /// Renaming the template's <c>{runId}</c> expansion would otherwise publish a URI with literal
    /// braces in it: <c>String.Replace</c> on a needle that is not there returns the haystack, with
    /// no compiler error anywhere. This fails first, and names the reason.
    /// </remarks>
    [Fact]
    public void TheRunEventsExpansion_LeavesNoPlaceholderBehind()
    {
        var expanded = VouchfxResourceUris.RunEventsUri("run-" + new string('a', 32));

        Assert.DoesNotContain("{", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("}", expanded, StringComparison.Ordinal);
    }

    /// <summary>
    /// The expansion is the SAME string the run-events resource is registered under, so a payload's
    /// <c>resourceUri</c> and <c>resources/templates/list</c> cannot disagree.
    /// </summary>
    [Fact]
    public void RunEventsUri_ExpandsTheTemplateThatIsActuallyAdvertised()
    {
        const string runId = "run-0123456789abcdef0123456789abcdef";

        Assert.Contains(VouchfxResourceUris.RunEventsTemplate, VouchfxResourceUris.AllTemplates);
        Assert.Equal(
            VouchfxResourceUris.RunEventsTemplate.Replace("{runId}", runId, StringComparison.Ordinal),
            VouchfxResourceUris.RunEventsUri(runId));
    }

    /// <summary>
    /// An id this server could not have minted is REFUSED rather than expanded into a URI that
    /// resolves to nothing.
    /// </summary>
    /// <remarks>
    /// The cases are the ones a published URI would be damaged by: a path separator or a <c>..</c>
    /// would escape the run namespace, and a friendly-looking short id is simply not something the
    /// registry can be holding. See <c>RunRegistryCore.IsWellFormedRunId</c>, which is the ONE shape
    /// rule and is reused here rather than re-implemented — a second, looser rule at the URI seam
    /// would be exactly the drift this type exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("run-seam")]
    [InlineData("run-../../etc/passwd")]
    [InlineData(@"run-..\..\windows")]
    [InlineData("RUN-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("run-0123456789abcdef0123456789abcdeff")]
    public void RunEventsUri_RefusesAnIdThisServerCouldNotHaveMinted(string runId) =>
        Assert.Throws<ArgumentException>(() => VouchfxResourceUris.RunEventsUri(runId));
}
