using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests.Docs;

/// <summary>
/// Covers <see cref="MarkdownSlugifier"/> against the three headings verified directly against
/// the live <c>vouchfx.io</c> site (see that type's remarks) — the anchors these tests assert on
/// are not guesses at the algorithm, they are what the published site actually renders.
/// </summary>
public class MarkdownSlugifierTests
{
    [Theory]
    [InlineData("Common step fields", "common-step-fields")]
    [InlineData("`http.rest`", "httprest")]
    [InlineData("Engine-owned polling with verifyMode: RETRY", "engine-owned-polling-with-verifymode-retry")]
    public void Slugify_HeadingsVerifiedAgainstTheLiveSite_ProducesTheirActualPublishedAnchor(string heading, string expectedSlug)
    {
        Assert.Equal(expectedSlug, MarkdownSlugifier.Slugify(heading));
    }

    [Fact]
    public void Slugify_MultipleConsecutiveSpaces_CollapsesToASingleHyphen()
    {
        Assert.Equal("a-b", MarkdownSlugifier.Slugify("a    b"));
    }

    [Fact]
    public void Slugify_LeadingAndTrailingWhitespace_IsTrimmedNotHyphenated()
    {
        Assert.Equal("word", MarkdownSlugifier.Slugify("  word  "));
    }

    [Fact]
    public void Slugify_AlreadyLowercaseWithHyphens_IsUnchanged()
    {
        Assert.Equal("db-assert-postgres", MarkdownSlugifier.Slugify("db-assert-postgres"));
    }

    [Fact]
    public void Slugify_PunctuationOnly_ProducesAnEmptyString()
    {
        Assert.Equal(string.Empty, MarkdownSlugifier.Slugify("!!!"));
    }

    [Fact]
    public void Slugify_NonAsciiCharacters_AreDecomposedThenDropped()
    {
        // Mirrors the reference algorithm's default unicode=False behaviour: NFKD-normalise (which
        // decomposes 'é' into a plain ASCII 'e' plus a combining acute accent), then
        // encode('ascii', 'ignore') — the combining accent (non-ASCII) is dropped, leaving the
        // base ASCII letter behind rather than losing it entirely or throwing.
        Assert.Equal("cafe", MarkdownSlugifier.Slugify("café"));
    }
}
