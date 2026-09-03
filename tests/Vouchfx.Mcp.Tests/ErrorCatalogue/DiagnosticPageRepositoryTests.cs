using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;

namespace Vouchfx.Mcp.Tests.ErrorCatalogue;

/// <summary>
/// Catalogue-page completeness guards for <see cref="DiagnosticPageRepository"/> (US-S1-05) — the
/// "every code has a page" half of the bidirectional gate US-S1-06 completes with "every page has an
/// emitter". <see cref="VfxCodeCatalogueTests"/> in <c>Contracts/</c> already proves every emitted
/// code is catalogued; this class proves every catalogued code resolves to a real, well-formed page.
/// </summary>
/// <remarks>
/// Merely referencing <see cref="DiagnosticPageRepository.AllByCode"/> anywhere in this class already
/// exercises its static initialiser, which throws if any catalogued code's embedded page is missing,
/// misnamed, or malformed (see that type's own remarks) — so <see cref="EveryCatalogueCode_HasAWellFormedEmbeddedPage"/>
/// is really asserting "the repository loaded at all", made explicit rather than left as an implicit
/// side effect of test collection.
/// </remarks>
public class DiagnosticPageRepositoryTests
{
    [Fact]
    public void EveryCatalogueCode_HasAWellFormedEmbeddedPage()
    {
        var catalogueCodes = VfxCodeCatalogue.All.Select(entry => entry.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(catalogueCodes.Count, DiagnosticPageRepository.AllByCode.Count);
        Assert.Equal(catalogueCodes, DiagnosticPageRepository.AllByCode.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPage_OwnH1MatchesTheCodeItIsFiledUnder()
    {
        foreach (var (code, page) in DiagnosticPageRepository.AllByCode)
        {
            Assert.Equal(code, page.Code);
        }
    }

    [Fact]
    public void EveryPage_HasNonEmptyTitleExplanationCausesAndFixes()
    {
        foreach (var page in DiagnosticPageRepository.AllByCode.Values)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title), $"{page.Code} has a blank Title.");
            Assert.False(string.IsNullOrWhiteSpace(page.Explanation), $"{page.Code} has a blank Explanation.");
            Assert.NotEmpty(page.CommonCauses);
            Assert.NotEmpty(page.Fixes);

            Assert.All(page.CommonCauses, cause => Assert.False(string.IsNullOrWhiteSpace(cause)));
            Assert.All(page.Fixes, fix => Assert.False(string.IsNullOrWhiteSpace(fix)));
        }
    }

    [Fact]
    public void GetRawText_UnknownCode_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DiagnosticPageRepository.GetRawText("VFX-E-1850"));
    }

    [Fact]
    public void TryGetPage_UnknownCode_ReturnsFalse()
    {
        Assert.False(DiagnosticPageRepository.TryGetPage("VFX-E-1850", out _));
        Assert.False(DiagnosticPageRepository.TryGetPage(null, out _));
    }

    [Fact]
    public void TryGetPage_CatalogueCode_ReturnsTrueAndMatchesGetRawText()
    {
        var found = DiagnosticPageRepository.TryGetPage(VfxCodeCatalogue.RunInProgress, out var page);

        Assert.True(found);
        Assert.Equal(VfxCodeCatalogue.RunInProgress, page.Code);

        // Same underlying bytes the raw-text accessor (and therefore the vouchfx-docs:///errors/{code}
        // resource) serves — the "one file, two access paths" invariant, checked at the repository
        // layer directly rather than only over MCP.
        var rawText = DiagnosticPageRepository.GetRawText(VfxCodeCatalogue.RunInProgress);
        Assert.Contains(page.Title, rawText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedResourceNameFor_FollowsTheVendoredNamingConvention()
    {
        Assert.Equal("Vouchfx.Mcp.Errors.VFX-E-1501.md", DiagnosticPageRepository.EmbeddedResourceNameFor("VFX-E-1501"));
    }
}
