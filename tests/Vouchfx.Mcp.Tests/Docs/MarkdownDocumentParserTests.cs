using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests.Docs;

/// <summary>Covers <see cref="MarkdownDocumentParser"/> against small, synthetic Markdown documents.</summary>
public class MarkdownDocumentParserTests
{
    [Fact]
    public void Parse_SingleHeadingWithBody_ReturnsOneSection()
    {
        const string markdown = "# Title\n\nSome body text.\nA second line.\n";

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var section = Assert.Single(sections);
        Assert.Equal("Title", section.Heading);
        Assert.Equal("Title", section.HeadingPath);
        Assert.Equal(1, section.Level);
        Assert.Equal("title", section.Slug);
        Assert.Equal("Some body text.\nA second line.", section.Body);
    }

    [Fact]
    public void Parse_NestedHeadings_BuildsAHeadingPathFromEveryAncestor()
    {
        const string markdown = """
            # Doc title

            Intro.

            ## Section one

            Section one body.

            ### Subsection

            Subsection body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.Equal(3, sections.Count);

        Assert.Equal("Doc title", sections[0].HeadingPath);
        Assert.Equal("Doc title > Section one", sections[1].HeadingPath);
        Assert.Equal("Doc title > Section one > Subsection", sections[2].HeadingPath);

        Assert.Equal("Subsection body.", sections[2].Body);
    }

    [Fact]
    public void Parse_SiblingHeadingsAtSameLevel_DoNotNestUnderEachOther()
    {
        const string markdown = """
            # Title

            ## First

            First body.

            ## Second

            Second body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Title > First", sections[1].HeadingPath);
        Assert.Equal("Title > Second", sections[2].HeadingPath);
        Assert.Equal("First body.", sections[1].Body);
        Assert.Equal("Second body.", sections[2].Body);
    }

    [Fact]
    public void Parse_ASubHeadingsBodyIsNotIncludedInItsParentsSection()
    {
        const string markdown = """
            ## Parent

            Parent intro.

            ### Child

            Child body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var parent = Assert.Single(sections, s => s.Heading == "Parent");
        Assert.Equal("Parent intro.", parent.Body);
        Assert.DoesNotContain("Child body", parent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TextBeforeTheFirstHeading_IsDiscarded()
    {
        const string markdown = "Some preamble with no heading.\n\n# Title\n\nBody.\n";

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var section = Assert.Single(sections);
        Assert.Equal("Body.", section.Body);
    }

    [Fact]
    public void Parse_HashWithoutASpace_IsNotTreatedAsAHeading()
    {
        const string markdown = "# Title\n\n#not-a-heading\nBody line.\n";

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var section = Assert.Single(sections);
        Assert.Contains("#not-a-heading", section.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EverySectionCarriesTheSuppliedDocId()
    {
        const string markdown = "# A\n\nbody\n\n## B\n\nbody\n";

        var sections = MarkdownDocumentParser.Parse("my-doc", markdown);

        Assert.All(sections, s => Assert.Equal("my-doc", s.DocId));
    }

    [Fact]
    public void Parse_RealLanguageReferenceDocument_ProducesTheExpectedCommonStepFieldsSection()
    {
        var text = VendoredDocRepository.GetRawText(VendoredDocuments.LanguageReference.Id);

        var sections = MarkdownDocumentParser.Parse(VendoredDocuments.LanguageReference.Id, text);

        var commonFields = Assert.Single(sections, s => s.Heading == "Common step fields");
        Assert.Equal("common-step-fields", commonFields.Slug);
        Assert.Contains("verifyMode", commonFields.Body, StringComparison.Ordinal);
        Assert.Contains("IMMEDIATE", commonFields.Body, StringComparison.Ordinal);
        Assert.Contains("RETRY", commonFields.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RealRecipesDocument_ProducesTheExpectedVerifyModeRetrySection()
    {
        var text = VendoredDocRepository.GetRawText(VendoredDocuments.Recipes.Id);

        var sections = MarkdownDocumentParser.Parse(VendoredDocuments.Recipes.Id, text);

        var retrySection = Assert.Single(sections, s => s.Heading == "Engine-owned polling with verifyMode: RETRY");
        Assert.Equal("engine-owned-polling-with-verifymode-retry", retrySection.Slug);
        Assert.Contains("RETRY", retrySection.Body, StringComparison.Ordinal);
    }

    // ── B1: a '#'-prefixed line inside a fenced code block is NEVER a heading (gatekeeper BLOCKER) ─

    [Fact]
    public void Parse_HashCommentInsideAYamlFence_IsNotTreatedAsAHeading()
    {
        const string markdown = """
            # Real heading

            ```yaml
            steps:
              # A YAML comment that looks exactly like an ATX heading line
              - id: s1
            ```

            More real body text.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var section = Assert.Single(sections);
        Assert.Equal("Real heading", section.Heading);
        Assert.Contains("# A YAML comment", section.Body, StringComparison.Ordinal);
        Assert.Contains("More real body text.", section.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MultipleFencesEachWithHashComments_NeitherProducesAPhantomSection()
    {
        const string markdown = """
            # Title

            ## First

            ```bash
            # comment one
            echo hi
            ```

            First body.

            ## Second

            ```yaml
            # comment two
            key: value
            ```

            Second body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.Equal(3, sections.Count);
        Assert.DoesNotContain(sections, s => s.Heading.Contains("comment", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TildeFence_IsAlsoRecognisedAsAFence()
    {
        const string markdown = """
            # Title

            ~~~
            # not a heading
            ~~~

            Body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        var section = Assert.Single(sections);
        Assert.Contains("# not a heading", section.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LongerClosingFence_StillClosesTheFence()
    {
        // CommonMark permits a closing fence at least as long as the opening one.
        const string markdown = """
            # Title

            ```
            # not a heading
            ````

            ## Real next heading

            Body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.Contains(sections, s => s.Heading == "Real next heading");
    }

    [Fact]
    public void Parse_ShorterFenceLikeLineInsideAFence_DoesNotCloseIt()
    {
        // A 4-backtick fence can only be closed by 4+ backticks — a bare 3-backtick line inside it
        // is just more fenced content, not a close.
        const string markdown = """
            # Title

            ````
            ```
            # still not a heading, still inside the outer fence
            ```
            ````

            ## Real next heading

            Body.
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.DoesNotContain(sections, s => s.Heading.Contains("still not a heading", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Heading == "Real next heading");
    }

    [Fact]
    public void Parse_ClosingFenceWithTrailingInfoString_DoesNotCloseTheFence()
    {
        // Per CommonMark, a closing fence must carry no info string — a line that merely starts
        // with the fence characters but has trailing content is not a valid close.
        const string markdown = """
            # Title

            ```yaml
            key: value
            ``` this is not a valid close, it has trailing text
            # still inside the fence
            ```

            ## Real next heading
            """;

        var sections = MarkdownDocumentParser.Parse("doc", markdown);

        Assert.DoesNotContain(sections, s => s.Heading.Contains("still inside", StringComparison.Ordinal));
        Assert.Contains(sections, s => s.Heading == "Real next heading");
    }

    // ── m2(b): the parsed section count over the REAL vendored docs is pinned, so a future ─────
    // over-parsing regression (or an engine doc sync that changes the heading count) fails loudly
    // rather than silently.

    [Fact]
    public void Parse_RealLanguageReferenceDocument_ProducesExactlyTheKnownRealHeadingCount()
    {
        var text = VendoredDocRepository.GetRawText(VendoredDocuments.LanguageReference.Id);

        var sections = MarkdownDocumentParser.Parse(VendoredDocuments.LanguageReference.Id, text);

        // 1 title + "Common step fields" + "Step types" + 25 step-type sections + "Regenerating
        // this file" = 29. Verified by direct inspection of every heading in the vendored file
        // (none inside a fence) at the pinned ENGINE_PIN commit; four bash-fenced "#"-prefixed
        // lines under "Regenerating this file" are correctly excluded by the fence-aware parser.
        Assert.Equal(29, sections.Count);
    }

    [Fact]
    public void Parse_RealRecipesDocument_ProducesExactlyTheKnownRealHeadingCount()
    {
        var text = VendoredDocRepository.GetRawText(VendoredDocuments.Recipes.Id);

        var sections = MarkdownDocumentParser.Parse(VendoredDocuments.Recipes.Id, text);

        // 1 title + 16 "##" sections (the 15 numbered recipes plus a trailing, unnumbered "See
        // also") + 13 "### Example: ..." worked-example subsections (the two CI-integration
        // recipes have no separate Example subsection) = 30. Verified by direct inspection at the
        // pinned ENGINE_PIN commit; roughly two dozen YAML-fenced "#"-prefixed comment lines are
        // correctly excluded by the fence-aware parser.
        Assert.Equal(30, sections.Count);
    }

    [Fact]
    public void Parse_RealVendoredDocuments_ProduceNoDuplicateSlugsYet()
    {
        // MINOR m1: neither vendored document currently has a real duplicate heading, so
        // MarkdownDocumentParser's MkDocs-matching duplicate-slug disambiguation (see its
        // MakeSlugUnique remarks) is exercised here only as "does nothing when nothing is
        // duplicated" — this guards the OTHER half of that fix: if a future engine doc sync ever
        // introduces two same-text headings on the same page, THIS assertion continues to hold
        // (the disambiguator kicks in and slugs stay unique) rather than silently producing two
        // sections with the same slug and an ambiguous deep link.
        foreach (var doc in VendoredDocuments.All)
        {
            var text = VendoredDocRepository.GetRawText(doc.Id);
            var sections = MarkdownDocumentParser.Parse(doc.Id, text);
            var slugs = sections.Select(s => s.Slug).ToList();

            Assert.Equal(slugs.Distinct(StringComparer.Ordinal).Count(), slugs.Count);
        }
    }
}
