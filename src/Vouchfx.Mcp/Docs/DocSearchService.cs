namespace Vouchfx.Mcp.Docs;

/// <summary>
/// The <c>search_docs</c> tool's engine: scores every section of every vendored document
/// (<see cref="VendoredDocRepository.AllSections"/>) against a free-text query and returns the
/// best matches with a deep link to the corresponding published page — REQ-005.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoring, deliberately kept simple and deterministic:</b> the query is split into
/// whitespace-separated terms; each term contributes <see cref="HeadingMatchWeight"/> to a
/// section's score if it appears ANYWHERE in that section's own heading text (case-insensitive
/// substring match — <c>"verifyMode"</c> matches inside <c>"verifyMode: RETRY"</c>), and
/// <see cref="BodyMatchWeight"/> if it appears anywhere in the section's body. This is PRESENCE,
/// not raw occurrence COUNT: a section that mentions a term once scores the same as one that
/// mentions it fifty times. That choice is deliberate — <see cref="VendoredDocuments.Recipes"/>'s
/// worked examples are long and can casually re-mention a term (e.g. <c>verifyMode: RETRY</c>
/// appears in nearly every recipe's YAML), which would otherwise let a section's sheer LENGTH
/// dominate the ranking over a short, precisely relevant one purely by having more chances to
/// repeat the same word.
/// </para>
/// <para>
/// <b>Ties are broken by original document/heading order</b> (language reference before recipes,
/// then top-to-bottom within each), not left to an unstable sort — so a query result is exactly
/// reproducible across runs, and a short, canonical section (e.g.
/// <see cref="VendoredDocuments.LanguageReference"/>'s "Common step fields", which defines
/// <c>verifyMode</c> itself) reliably outranks a longer worked example that only mentions the same
/// term in passing.
/// </para>
/// <para>
/// <b>The query is agent-supplied and untrusted, and bounded accordingly</b> — mirroring
/// <c>validate_suite</c>'s <c>YamlSafetyGuard.MaxSuiteSizeBytes</c> pre-parse-reject precedent:
/// <see cref="ScoreSection"/> costs roughly <c>O(termCount × totalDocumentContent)</c> per call
/// (a <see cref="string.Contains(string, StringComparison)"/> scan per term, per section), and
/// with NEITHER the query length nor its term count bounded, an agent-controlled multi-megabyte
/// query — or a within-length-limit query shaped as thousands of single-character
/// whitespace-separated "terms" — could turn a single <c>search_docs</c> call into disproportionate
/// CPU work on THIS server's own single stdio session, with no way for a caller to abort a
/// synchronous call short of disconnecting entirely. <see cref="MaxQueryLength"/> rejects an
/// oversized query outright, before any term-splitting or section-scoring work runs at all;
/// <see cref="MaxQueryTerms"/> separately bounds the term count for a query that stays under that
/// length but is still adversarially shaped. Both are checked BEFORE <see cref="VendoredDocRepository.AllSections"/>
/// is touched. <see cref="Search"/> also accepts a <see cref="CancellationToken"/>, checked once up
/// front and once per section scored, so the MCP SDK's own request cancellation can abort a call
/// promptly — a defence-in-depth measure: the length/term caps above are what make even an
/// UN-cancelled pathological query cheap regardless.
/// </para>
/// </remarks>
public static class DocSearchService
{
    /// <summary>The maximum number of matches <see cref="Search"/> returns, best first.</summary>
    public const int MaxResults = 5;

    /// <summary>
    /// The maximum length, in characters, of a single <see cref="DocSearchMatch.Snippet"/>. Several
    /// of the vendored recipes' worked examples (a full <c>.e2e.yaml</c> file plus explanation) run
    /// well past this; capping keeps one large match from crowding out the others in an agent's
    /// context, while 1,000 characters is still generous enough to show a complete short recipe or
    /// a meaningful excerpt of a long one. A truncated snippet ends with an ellipsis.
    /// </summary>
    public const int MaxSnippetLength = 1_000;

    /// <summary>
    /// Maximum accepted <see cref="Search"/> query length, in characters. A query longer than this
    /// is rejected outright — before any term-splitting or section-scoring work runs — and reported
    /// as a clean, empty-match result (never a hang, never a thrown exception). A genuine free-text
    /// search question (e.g. <c>"how does verifyMode RETRY work"</c>) is a short sentence; 1,000
    /// characters is generous headroom over any plausible legitimate one while bounding the cost an
    /// agent-controlled query can impose. See this type's remarks for the full threat model.
    /// </summary>
    public const int MaxQueryLength = 1_000;

    /// <summary>
    /// Maximum number of whitespace-separated terms actually scored against each section. A query
    /// that stays under <see cref="MaxQueryLength"/> could still be shaped as hundreds of short
    /// "terms" (e.g. a long run of separated punctuation or single characters); this caps
    /// <see cref="ScoreSection"/>'s per-section cost to a small, constant multiple regardless of how
    /// a within-length-limit query is shaped. Terms beyond this count are simply dropped, not
    /// rejected — 20 terms is far more than any real search question needs, so dropping the excess
    /// degrades gracefully rather than failing the whole query.
    /// </summary>
    public const int MaxQueryTerms = 20;

    private const int HeadingMatchWeight = 5;
    private const int BodyMatchWeight = 1;

    /// <summary>
    /// Searches every vendored document's sections for <paramref name="query"/> and returns the
    /// best matches.
    /// </summary>
    /// <param name="query">The free-text query. Longer than <see cref="MaxQueryLength"/> resolves
    /// to an empty-match result rather than being scored.</param>
    /// <param name="cancellationToken">
    /// Checked once up front and once per section scored — allows an external caller (the MCP SDK,
    /// on request cancellation) to abort a call promptly. Not the primary defence against a
    /// pathological query; see this type's remarks.
    /// </param>
    /// <remarks>
    /// Never throws for a search outcome — an empty, unmatched, over-length, or hostile query all
    /// resolve to a structured result, an empty <see cref="DocSearchResult.Matches"/> list, never a
    /// crash. It CAN throw <see cref="OperationCanceledException"/>, but only when
    /// <paramref name="cancellationToken"/> itself was cancelled by the caller.
    /// </remarks>
    public static DocSearchResult Search(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawQuery = query ?? string.Empty;

        if (rawQuery.Length > MaxQueryLength)
        {
            // Reject outright, before any term-splitting or section-scoring work runs at all —
            // and never echo the whole oversized string back either.
            var truncatedEcho = TextSanitiser.SanitiseForDisplay(rawQuery[..MaxQueryLength]) + "…";
            return new DocSearchResult(truncatedEcho, []);
        }

        var sanitisedQuery = TextSanitiser.SanitiseForDisplay(rawQuery);

        var terms = SplitTerms(rawQuery);
        if (terms.Count == 0)
        {
            return new DocSearchResult(sanitisedQuery, []);
        }

        if (terms.Count > MaxQueryTerms)
        {
            terms = terms.Take(MaxQueryTerms).ToList();
        }

        var scored = new List<(DocSection Section, int Index, int Score)>();
        var index = 0;
        foreach (var section in VendoredDocRepository.AllSections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var score = ScoreSection(section, terms);
            if (score > 0)
            {
                scored.Add((section, index, score));
            }

            index++;
        }

        var matches = scored
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .Take(MaxResults)
            .Select(candidate => ToMatch(candidate.Section))
            .ToList();

        return new DocSearchResult(sanitisedQuery, matches);
    }

    private static int ScoreSection(DocSection section, IReadOnlyList<string> terms)
    {
        var score = 0;
        foreach (var term in terms)
        {
            if (section.Heading.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += HeadingMatchWeight;
            }

            if (section.Body.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += BodyMatchWeight;
            }
        }

        return score;
    }

    private static List<string> SplitTerms(string query) =>
        [.. query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    private static DocSearchMatch ToMatch(DocSection section)
    {
        var doc = VendoredDocuments.Find(section.DocId);
        return new DocSearchMatch(doc.Title, section.HeadingPath, CapSnippet(section.Body), $"{doc.SiteBaseUrl}#{section.Slug}");
    }

    private static string CapSnippet(string body) =>
        body.Length <= MaxSnippetLength ? body : body[..MaxSnippetLength].TrimEnd() + "…";
}
