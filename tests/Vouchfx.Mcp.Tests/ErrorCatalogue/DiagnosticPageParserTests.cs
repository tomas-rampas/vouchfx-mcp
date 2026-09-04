using Vouchfx.Mcp.ErrorCatalogue;

namespace Vouchfx.Mcp.Tests.ErrorCatalogue;

/// <summary>
/// Unit coverage for <see cref="DiagnosticPageParser"/>'s fixed heading structure (US-S1-05) —
/// the well-formed happy path, then one test per way a page can be malformed, each asserting the
/// parser throws rather than silently producing a partial page.
/// </summary>
public class DiagnosticPageParserTests
{
    private const string WellFormed = """
        # VFX-E-1002

        ## Title
        SuiteFileNotFound

        ## Explanation
        The named suite file does not exist on disk.

        A second explanation paragraph.

        ## Common causes
        - A typo in the path.
        - The file was moved or deleted.

        ## Fixes
        - Verify the path exists.
        - Use an absolute path.
        """;

    [Fact]
    public void Parse_WellFormedPage_ExtractsEveryField()
    {
        var page = DiagnosticPageParser.Parse(WellFormed);

        Assert.Equal("VFX-E-1002", page.Code);
        Assert.Equal("SuiteFileNotFound", page.Title);
        Assert.Contains("does not exist on disk", page.Explanation, StringComparison.Ordinal);
        Assert.Contains("A second explanation paragraph.", page.Explanation, StringComparison.Ordinal);
        Assert.Equal(["A typo in the path.", "The file was moved or deleted."], page.CommonCauses);
        Assert.Equal(["Verify the path exists.", "Use an absolute path."], page.Fixes);
    }

    [Fact]
    public void Parse_PreservesMultipleExplanationParagraphs()
    {
        var page = DiagnosticPageParser.Parse(WellFormed);

        // Both paragraphs are present, and in order — the parser must not drop or reorder content
        // beyond trimming leading/trailing blank lines.
        var firstIndex = page.Explanation.IndexOf("does not exist on disk", StringComparison.Ordinal);
        var secondIndex = page.Explanation.IndexOf("A second explanation paragraph.", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex);
    }

    [Fact]
    public void Parse_CrlfLineEndings_ParsesIdentically()
    {
        var crlf = WellFormed.Replace("\n", "\r\n", StringComparison.Ordinal);

        var page = DiagnosticPageParser.Parse(crlf);

        Assert.Equal("VFX-E-1002", page.Code);
        Assert.Equal("SuiteFileNotFound", page.Title);
    }

    [Fact]
    public void Parse_EmptyText_Throws()
    {
        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_MissingH1_Throws()
    {
        var missingH1 = WellFormed.Replace("# VFX-E-1002", "not a heading at all", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(missingH1));
    }

    [Fact]
    public void Parse_SecondTopLevelHeading_Throws()
    {
        var doubled = WellFormed + "\n# VFX-E-9999\n";

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(doubled));
    }

    [Fact]
    public void Parse_ContentBeforeFirstH2_Throws()
    {
        var stray = WellFormed.Replace(
            "# VFX-E-1002\n",
            "# VFX-E-1002\n\nstray paragraph with no heading\n",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(stray));
    }

    [Fact]
    public void Parse_RepeatedHeading_Throws()
    {
        var repeated = WellFormed + "\n## Title\nDuplicate\n";

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(repeated));
    }

    [Theory]
    [InlineData("## Title\n")]
    [InlineData("## Explanation\n")]
    [InlineData("## Common causes\n")]
    [InlineData("## Fixes\n")]
    public void Parse_MissingRequiredHeading_Throws(string headingLine)
    {
        var withoutHeading = string.Join(
            '\n',
            WellFormed.Split('\n').Where(line => !line.TrimEnd('\r').Equals(headingLine.Trim(), StringComparison.Ordinal)));

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(withoutHeading));
    }

    [Fact]
    public void Parse_TitleWithMultipleLines_Throws()
    {
        var twoLineTitle = WellFormed.Replace(
            "## Title\nSuiteFileNotFound\n",
            "## Title\nSuiteFileNotFound\nSecondLine\n",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(twoLineTitle));
    }

    [Fact]
    public void Parse_EmptyTitle_Throws()
    {
        var emptyTitle = WellFormed.Replace(
            "## Title\nSuiteFileNotFound\n",
            "## Title\n",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(emptyTitle));
    }

    [Fact]
    public void Parse_EmptyExplanation_Throws()
    {
        var emptyExplanation = WellFormed.Replace(
            "## Explanation\nThe named suite file does not exist on disk.\n\nA second explanation paragraph.\n",
            "## Explanation\n",
            StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(emptyExplanation));
    }

    [Theory]
    [InlineData("Common causes")]
    [InlineData("Fixes")]
    public void Parse_NonBulletLineInBulletSection_Throws(string bulletHeading)
    {
        var withProseLine = MinimalPageWithBulletSectionBody(bulletHeading, "- A real bullet.\nThis line is not a bullet.\n");

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(withProseLine));
    }

    [Theory]
    [InlineData("Common causes")]
    [InlineData("Fixes")]
    public void Parse_EmptyBulletSection_Throws(string bulletHeading)
    {
        var emptySection = MinimalPageWithBulletSectionBody(bulletHeading, string.Empty);

        Assert.Throws<FormatException>(() => DiagnosticPageParser.Parse(emptySection));
    }

    /// <summary>
    /// Builds a minimal well-formed page except that <paramref name="bulletHeading"/>'s own body is
    /// replaced with <paramref name="body"/> verbatim — isolates a single section's malformation
    /// without depending on <see cref="WellFormed"/>'s exact whitespace shape, which raw string
    /// literal Replace-based surgery proved fragile against.
    /// </summary>
    private static string MinimalPageWithBulletSectionBody(string bulletHeading, string body)
    {
        var otherHeading = bulletHeading == "Common causes" ? "Fixes" : "Common causes";

        return $"""
            # VFX-E-1002

            ## Title
            SuiteFileNotFound

            ## Explanation
            The named suite file does not exist on disk.

            ## {bulletHeading}
            {body}
            ## {otherHeading}
            - A real bullet.
            """;
    }
}
