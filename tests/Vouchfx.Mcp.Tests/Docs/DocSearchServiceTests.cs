using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests.Docs;

/// <summary>
/// Covers <see cref="DocSearchService"/> — REQ-005's <c>search_docs</c> logic — against the REAL
/// vendored documents (not synthetic fixtures): the acceptance criterion is specifically that a
/// real query against the real, embedded engine docs surfaces real, correct content and a correct
/// deep link, so these tests deliberately do not mock the document source.
/// </summary>
public class DocSearchServiceTests
{
    // ── REQ-005 acceptance: search_docs("verifyMode") ──────────────────────────────────────────

    [Fact]
    public void Search_VerifyMode_TopMatchIsTheRetryPollingRecipeWithACorrectDeepLink()
    {
        var result = DocSearchService.Search("verifyMode");

        Assert.NotEmpty(result.Matches);
        var top = result.Matches[0];

        Assert.Equal("vouchfx Recipes: Common Patterns and Examples", top.Source);
        Assert.Contains("Engine-owned polling with verifyMode: RETRY", top.HeadingPath, StringComparison.Ordinal);
        Assert.Contains("RETRY", top.Snippet, StringComparison.Ordinal);
        Assert.Equal("https://vouchfx.io/recipes/#engine-owned-polling-with-verifymode-retry", top.Url);
    }

    [Fact]
    public void Search_VerifyMode_SurfacesTheCommonStepFieldsMatchMentioningBothImmediateAndRetry()
    {
        // The one place in either vendored document that states the full, exact contract: "Either
        // IMMEDIATE (default) or RETRY." This is the acceptance criterion's "the RETRY/IMMEDIATE
        // documentation text" — a different section from the top (RETRY-only) match above.
        var result = DocSearchService.Search("verifyMode");

        var immediateAndRetryMatch = Assert.Single(result.Matches, m =>
            m.Snippet.Contains("IMMEDIATE", StringComparison.Ordinal) &&
            m.Snippet.Contains("RETRY", StringComparison.Ordinal));

        Assert.Equal("vouchfx Language Reference", immediateAndRetryMatch.Source);
        Assert.Contains("Common step fields", immediateAndRetryMatch.HeadingPath, StringComparison.Ordinal);
        Assert.StartsWith("https://", immediateAndRetryMatch.Url, StringComparison.Ordinal);
        Assert.Contains("vouchfx.io", immediateAndRetryMatch.Url, StringComparison.Ordinal);
        Assert.Equal("https://vouchfx.io/language-reference/#common-step-fields", immediateAndRetryMatch.Url);
    }

    [Fact]
    public void Search_VerifyMode_ReturnsAtMostMaxResultsMatches()
    {
        var result = DocSearchService.Search("verifyMode");

        Assert.True(result.Matches.Count <= DocSearchService.MaxResults);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var lower = DocSearchService.Search("verifymode");
        var mixedCase = DocSearchService.Search("VeRiFyMoDe");

        Assert.NotEmpty(lower.Matches);
        Assert.Equal(lower.Matches.Select(m => m.Url), mixedCase.Matches.Select(m => m.Url));
    }

    // ── No matches: a structured empty result, never a crash ──────────────────────────────────

    [Fact]
    public void Search_QueryWithNoMatches_ReturnsAnEmptyMatchListWithoutCrashing()
    {
        var result = DocSearchService.Search("zzz-nonexistent-term-that-cannot-appear-xyz-999");

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsAnEmptyMatchListWithoutCrashing()
    {
        var result = DocSearchService.Search(string.Empty);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Search_WhitespaceOnlyQuery_ReturnsAnEmptyMatchListWithoutCrashing()
    {
        var result = DocSearchService.Search("   \t  ");

        Assert.Empty(result.Matches);
    }

    // ── Untrusted-input hygiene: the query is agent-supplied ───────────────────────────────────

    [Fact]
    public void Search_HostileControlCharacterQuery_SanitisesTheEchoedQueryWithoutCrashing()
    {
        var disallowedByte = ((char)27).ToString(); // ESC
        var query = $"verifyMode{disallowedByte}";

        var result = DocSearchService.Search(query);

        Assert.DoesNotContain(disallowedByte, result.Query, StringComparison.Ordinal);
        foreach (var c in result.Query)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    [Fact]
    public void Search_NonPrintableUnicodeCharacterInQuery_SanitisesTheEchoedQuery()
    {
        var disallowedCharacter = ((char)0x202E).ToString(); // RIGHT-TO-LEFT OVERRIDE
        var query = $"recipes{disallowedCharacter}";

        var result = DocSearchService.Search(query);

        Assert.DoesNotContain(disallowedCharacter, result.Query, StringComparison.Ordinal);
    }

    // ── The snippet cap ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_EveryReturnedSnippet_NeverExceedsMaxSnippetLengthPlusAnEllipsis()
    {
        // "Kafka" hits several long, worked recipe examples (full .e2e.yaml files plus
        // explanation) that are known to run well past the cap.
        var result = DocSearchService.Search("Kafka");

        Assert.NotEmpty(result.Matches);
        Assert.All(result.Matches, m => Assert.True(
            m.Snippet.Length <= DocSearchService.MaxSnippetLength + 1,
            $"Snippet length {m.Snippet.Length} exceeds the {DocSearchService.MaxSnippetLength}-character cap."));
    }

    [Fact]
    public void Search_ASectionLongerThanTheCap_IsActuallyTruncatedWithAnEllipsis()
    {
        // Confirms the cap is exercised, not just never violated: find a real section whose raw
        // body is longer than the cap, search for a term unique to it, and check the returned
        // snippet was shortened and marked as truncated. The exact length is NOT asserted as
        // MaxSnippetLength + 1: CapSnippet does body[..MaxSnippetLength].TrimEnd() + "…", so a cut
        // that lands on whitespace trims a little further and can be shorter — what must hold is
        // the upper bound (never longer than the cap plus the ellipsis), that truncation actually
        // happened (shorter than the untruncated body), and the ellipsis marker itself.
        var longSection = VendoredDocRepository.AllSections.First(s => s.Body.Length > DocSearchService.MaxSnippetLength);

        var result = DocSearchService.Search(longSection.Heading);

        var match = Assert.Single(result.Matches, m => m.HeadingPath == longSection.HeadingPath);
        Assert.EndsWith("…", match.Snippet, StringComparison.Ordinal);
        Assert.True(
            match.Snippet.Length <= DocSearchService.MaxSnippetLength + 1,
            $"Expected the snippet to be capped at {DocSearchService.MaxSnippetLength + 1} characters " +
            $"(including the ellipsis), was {match.Snippet.Length}.");
        Assert.True(
            match.Snippet.Length < longSection.Body.Length,
            "Expected the snippet to be shorter than the section's untruncated body.");
    }

    [Fact]
    public void Search_TermOccurringOnlyBeyondTheCap_ReturnsASnippetAnchoredOnTheMatch()
    {
        // Regression, engine v1.0.0-rc.4 repin: "Common step fields" grew ~430 characters of new
        // `capture` documentation ahead of its `verifyMode` row, pushing the term past the
        // 1 000-character cap. A leading-window snippet therefore answered a "verifyMode" search
        // with text that never mentions verifyMode. The window must re-anchor on the hit.
        var section = VendoredDocRepository.AllSections
            .Single(s => s.HeadingPath.Contains("Common step fields", StringComparison.Ordinal));

        var firstOccurrence = section.Body.IndexOf("verifyMode", StringComparison.Ordinal);
        Assert.True(
            firstOccurrence > DocSearchService.MaxSnippetLength,
            $"Fixture premise broken: 'verifyMode' occurs at {firstOccurrence}, inside the " +
            $"{DocSearchService.MaxSnippetLength}-character leading window, so this test would " +
            "pass without exercising the anchoring path at all.");

        var match = Assert.Single(
            DocSearchService.Search("verifyMode").Matches,
            m => m.HeadingPath == section.HeadingPath);

        Assert.Contains("verifyMode", match.Snippet, StringComparison.Ordinal);
        Assert.StartsWith("…", match.Snippet, StringComparison.Ordinal);
        Assert.True(
            match.Snippet.Length <= DocSearchService.MaxSnippetLength + 1,
            $"Anchored snippet length {match.Snippet.Length} exceeds the " +
            $"{DocSearchService.MaxSnippetLength + 1}-character bound the leading-window path " +
            "has always honoured.");
    }

    [Fact]
    public void Search_MultiTermQueryWithOneTermBeyondTheCap_StillAnchorsOnTheInvisibleTerm()
    {
        // Regression guard for the short-circuit this originally shipped with: bailing out as soon
        // as ANY term was visible confined anchoring to single-term queries. This is the example
        // query from DocSearchService's own MaxQueryLength documentation and from
        // docs/tools-and-resources.md — 'how' sits inside the cap, 'verifyMode' does not.
        var section = VendoredDocRepository.AllSections
            .Single(s => s.HeadingPath.Contains("Common step fields", StringComparison.Ordinal));

        var how = section.Body.IndexOf("how", StringComparison.OrdinalIgnoreCase);
        var verifyMode = section.Body.IndexOf("verifyMode", StringComparison.Ordinal);
        Assert.True(
            how >= 0 && how + 3 <= DocSearchService.MaxSnippetLength,
            $"Fixture premise broken: 'how' at {how} is not inside the leading window, so this " +
            "test would not exercise the mixed-visibility path.");
        Assert.True(
            verifyMode > DocSearchService.MaxSnippetLength,
            $"Fixture premise broken: 'verifyMode' at {verifyMode} is already visible.");

        var match = Assert.Single(
            DocSearchService.Search("how does verifyMode RETRY work").Matches,
            m => m.HeadingPath == section.HeadingPath);

        Assert.Contains("verifyMode", match.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_EveryTermVisibleInsideTheCap_KeepsTheUnchangedLeadingWindow()
    {
        // The other half of the contract: anchoring must not fire when the leading window already
        // shows every term the body contains — that snippet stays byte-identical to what the
        // pre-anchoring implementation produced, body[..cap].TrimEnd() + "…".
        //
        // The section is named rather than picked by First(...): a predicate-chosen section is only
        // asserted through Search(), which truncates at MaxResults, so it passed by ranking luck and
        // would fail confusingly the first time the vendored docs reflow.
        const string query = "Common step fields";
        var section = VendoredDocRepository.AllSections
            .Single(s => s.HeadingPath.Contains(query, StringComparison.Ordinal));

        Assert.True(
            section.Body.Length > DocSearchService.MaxSnippetLength,
            "Fixture premise broken: this section no longer exceeds the cap, so nothing is truncated.");

        // Querying the section's own heading maximises its score, so it cannot fall out of the
        // MaxResults window. The premise this test needs is that no term forces anchoring: a term
        // absent from the body cannot (it is skipped), and a present one must be wholly visible.
        foreach (var term in query.Split(' '))
        {
            var first = section.Body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                first < 0 || first + term.Length <= DocSearchService.MaxSnippetLength,
                $"Fixture premise broken: '{term}' occurs at {first}, beyond the leading window, so " +
                "this test would exercise the anchoring path instead of the one it names.");
        }

        var match = Assert.Single(
            DocSearchService.Search(query).Matches,
            m => m.HeadingPath == section.HeadingPath);

        Assert.Equal(section.Body[..DocSearchService.MaxSnippetLength].TrimEnd() + "…", match.Snippet);
    }

    [Fact]
    public void Search_AnchoredSnippet_ContainsTheMatchedTerm()
    {
        // The end-to-end anchoring contract over the REAL vendored documents. Deliberately not
        // described as a floor guard: the floor cannot bind at these documents' token lengths (see
        // CapSnippet_WindowFloor_KeepsTheWholeMatchedTermInsideTheWindow, which drives the floor
        // directly). This is what caught `is not var (anchor, matchedLength)` — a pattern that
        // compiles, is always false, and disables anchoring wholesale.
        foreach (var term in new[] { "verifyMode", "IMMEDIATE" })
        {
            var result = DocSearchService.Search(term);

            Assert.All(result.Matches, m => Assert.True(
                !m.Snippet.StartsWith('…') || m.Snippet.Contains(term, StringComparison.OrdinalIgnoreCase),
                $"Anchored snippet for '{term}' does not contain the term: {m.Snippet[..Math.Min(120, m.Snippet.Length)]}"));
        }
    }

    [Fact]
    public void CapSnippet_WindowFloor_KeepsTheWholeMatchedTermInsideTheWindow()
    {
        // The floor guard proper, driven against CapSnippet directly because no document in the
        // vendored corpus can reach it: the floor binds only when the whitespace walk-back travels
        // further than the entire budget, which needs an unbroken non-whitespace run approaching
        // 1 000 characters (longest measured in the real documents: 145).
        //
        // The synthetic body puts a 950-character unbroken token immediately before the term, so
        // the walk-back would run to that token's start and — with the pre-fix floor of
        // `anchor - (MaxSnippetLength - 1)` — end the window one character BEFORE the anchor,
        // excluding the very term it was anchored on. A floor that forgot the term's own length
        // would instead show a fragment of it. Both are caught here.
        const string term = "needle";
        var body =
            new string('x', DocSearchService.MaxSnippetLength + 50) + " " +
            new string('y', 950) + term +
            new string('z', 500);

        var snippet = DocSearchService.CapSnippet(body, [term]);

        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains(term, snippet, StringComparison.Ordinal);
        Assert.True(
            snippet.Length <= DocSearchService.MaxSnippetLength + 1,
            $"Anchored snippet length {snippet.Length} exceeds the documented bound.");
    }

    // ── B1 regression (gatekeeper BLOCKER) + m2(c): every returned link must resolve to a REAL ──
    // parsed heading — no anchor manufactured from a phantom, fence-swallowed "section".

    [Fact]
    public void Search_PaymentGatewayQuery_NoLongerReturnsAPhantomAnchorFromTheFencedYamlComment()
    {
        // The exact scenario the gatekeeper's B1 finding was about: recipes.md's WireMock example
        // is a ```yaml fence containing a "# The stubbed payment gateway..." COMMENT line. Before
        // MarkdownDocumentParser became fence-aware, that comment was misparsed as its own
        // heading/section, and this query would have surfaced a deep link to an anchor that does
        // not exist on the published site.
        var result = DocSearchService.Search("payment gateway");

        Assert.NotEmpty(result.Matches);
        Assert.Contains(result.Matches, m =>
            m.HeadingPath.Contains("stubbed payment gateway", StringComparison.OrdinalIgnoreCase));

        AssertEveryMatchUrlIsARealParsedHeadingSlug(result);
    }

    [Theory]
    [InlineData("payment gateway")]
    [InlineData("verifyMode")]
    [InlineData("Kafka")]
    [InlineData("Redis")]
    [InlineData("Vault")]
    [InlineData("Elasticsearch")]
    public void Search_EveryMatchUrl_HasAnAnchorThatIsARealParsedHeadingSlug(string query)
    {
        var result = DocSearchService.Search(query);

        Assert.NotEmpty(result.Matches);
        AssertEveryMatchUrlIsARealParsedHeadingSlug(result);
    }

    private static void AssertEveryMatchUrlIsARealParsedHeadingSlug(DocSearchResult result)
    {
        var realSlugsBySiteBaseUrl = VendoredDocuments.All.ToDictionary(
            doc => doc.SiteBaseUrl,
            doc => new HashSet<string>(
                VendoredDocRepository.AllSections.Where(s => s.DocId == doc.Id).Select(s => s.Slug),
                StringComparer.Ordinal));

        foreach (var match in result.Matches)
        {
            var siteBaseUrl = Assert.Single(realSlugsBySiteBaseUrl.Keys, baseUrl => match.Url.StartsWith(baseUrl, StringComparison.Ordinal));
            var anchor = match.Url[(siteBaseUrl.Length + 1)..]; // +1 skips the '#'
            Assert.Contains(anchor, realSlugsBySiteBaseUrl[siteBaseUrl]);
        }
    }

    // ── Security MAJOR: an agent-controlled query must never be able to hang the server ─────────

    [Fact]
    public void Search_QueryLongerThanMaxQueryLength_ReturnsEmptyMatchesWithoutScoringAnySection()
    {
        var oversizedQuery = new string('a', DocSearchService.MaxQueryLength + 1);

        var result = DocSearchService.Search(oversizedQuery);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Search_QueryLongerThanMaxQueryLength_TruncatesTheEchoedQueryRatherThanReflectingItInFull()
    {
        var oversizedQuery = new string('a', DocSearchService.MaxQueryLength + 500);

        var result = DocSearchService.Search(oversizedQuery);

        Assert.True(
            result.Query.Length <= DocSearchService.MaxQueryLength + 1, // +1 for the ellipsis
            $"Expected the echoed query to be capped near MaxQueryLength, was {result.Query.Length} characters.");
        Assert.EndsWith("…", result.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_QueryAtExactlyMaxQueryLength_IsStillProcessedNormallyNotRejected()
    {
        // The boundary itself must not be rejected — only STRICTLY over the limit is.
        var exactLengthQuery = "verifyMode" + new string(' ', DocSearchService.MaxQueryLength - "verifyMode".Length);
        Assert.Equal(DocSearchService.MaxQueryLength, exactLengthQuery.Length);

        var result = DocSearchService.Search(exactLengthQuery);

        Assert.NotEmpty(result.Matches);
    }

    [Fact]
    public void Search_HundredsOfSingleCharacterTermsUnderTheLengthCap_CompletesWithoutHanging()
    {
        // Under MaxQueryLength but adversarially shaped as hundreds of whitespace-separated
        // single-character "terms" — the pathological shape MaxQueryTerms exists to bound
        // independently of the length cap. xUnit's own default test timeout is the ultimate
        // backstop; a correctly bounded implementation returns essentially instantly here.
        var manyTermsQuery = string.Join(' ', Enumerable.Repeat("x", 500));
        Assert.True(manyTermsQuery.Length <= DocSearchService.MaxQueryLength, "Test precondition violated.");

        var result = DocSearchService.Search(manyTermsQuery);

        Assert.NotNull(result);
    }

    [Fact]
    public void Search_CancelledToken_ThrowsOperationCanceledExceptionRatherThanCompleting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => DocSearchService.Search("verifyMode", cts.Token));
    }
}
