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
            .Select(candidate => ToMatch(candidate.Section, terms))
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

    private static DocSearchMatch ToMatch(DocSection section, IReadOnlyList<string> terms)
    {
        var doc = VendoredDocuments.Find(section.DocId);
        return new DocSearchMatch(
            doc.Title,
            section.HeadingPath,
            CapSnippet(section.Body, terms),
            $"{doc.SiteBaseUrl}#{section.Slug}");
    }

    /// <summary>
    /// Characters of lead-in context kept before a match that falls outside the leading window,
    /// so the re-anchored snippet starts on the sentence or table row around the hit rather than
    /// mid-word on the hit itself.
    /// </summary>
    private const int SnippetLeadIn = 200;

    /// <summary>
    /// Caps <paramref name="body"/> to <see cref="MaxSnippetLength"/> characters, keeping the
    /// matched text inside the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain leading truncation is used whenever it already shows every term the body contains —
    /// the common case, and it keeps those snippets byte-identical to what they have always been.
    /// It is abandoned only when some searched term sits BEYOND the cap, where a leading window is
    /// actively misleading: it returns a match whose snippet does not contain the thing the caller
    /// searched for.
    /// </para>
    /// <para>
    /// That case became reachable at the engine v1.0.0-rc.4 repin. The language reference's
    /// "Common step fields" section gained ~430 characters of new <c>capture</c> documentation
    /// ahead of its <c>verifyMode</c> row, pushing the only full statement of the
    /// IMMEDIATE/RETRY contract past the 1 000-character cap — so a <c>search_docs</c> call for
    /// "verifyMode" surfaced that section with a snippet that never mentioned verifyMode. Sections
    /// grow with every engine release; anchoring on the hit removes the whole class of problem
    /// rather than buying headroom that the next release spends again.
    /// </para>
    /// <para>
    /// <c>internal</c> rather than <c>private</c> so the window FLOOR can be tested directly. The
    /// floor only binds when the walk-back travels further than the whole budget, which needs an
    /// unbroken non-whitespace run of ~900 characters — the vendored documents' longest is 145, so
    /// no test driven through <see cref="Search"/> can reach it, and a test claiming to guard it
    /// would be asserting the anchoring contract under a floor-shaped name.
    /// </para>
    /// </remarks>
    internal static string CapSnippet(string body, IReadOnlyList<string> terms)
    {
        if (body.Length <= MaxSnippetLength)
        {
            return body;
        }

        if (FindFirstTermBeyondLeadingWindow(body, terms) is not { } match)
        {
            return body[..MaxSnippetLength].TrimEnd() + "…";
        }

        var (anchor, matchedLength) = match;

        // The leading ellipsis is charged against the window's own budget so the whole snippet still
        // honours the documented "never longer than MaxSnippetLength + 1" bound (the +1 being the
        // single trailing ellipsis) that the leading-truncation path has always met. Computed before
        // the floor because the floor depends on it.
        // One source of truth for the ellipsis's width, used by both the budget below and the
        // prefix actually emitted, so the two cannot drift if the marker ever stops being one char.
        const string ellipsis = "…";
        var budget = MaxSnippetLength - ellipsis.Length;

        // Snap the window start back to a whitespace boundary so it does not open mid-word, floored
        // so the walk-back cannot push the matched term back out of the window: inside a long
        // unbroken token the snap would otherwise run to the token's start and silently restore the
        // very symptom this method exists to fix.
        //
        // The floor accounts for the ellipsis AND the term's own length. An earlier version used
        // `anchor - (MaxSnippetLength - 1)`, which was off by exactly one character plus the term:
        // at the floor the window ended immediately BEFORE the anchor, so the snippet excluded the
        // very term it was anchored on — and a window showing only the term's first character would
        // have satisfied the bound while failing the contract just as badly. Unreachable in today's
        // vendored documents (longest non-whitespace run measured: 145 characters against a
        // ~999 budget), but the justification for anchoring at all is that these documents grow.
        var floor = Math.Max(0, anchor + matchedLength - budget);
        var start = Math.Max(floor, anchor - SnippetLeadIn);
        while (start > floor && !char.IsWhiteSpace(body[start - 1]))
        {
            start--;
        }

        var prefix = start > 0 ? ellipsis : string.Empty;
        var length = Math.Min(MaxSnippetLength - prefix.Length, body.Length - start);
        var window = body.Substring(start, length).Trim();

        var suffix = start + length < body.Length ? ellipsis : string.Empty;

        return prefix + window + suffix;
    }

    /// <summary>
    /// The earliest position of any term that is NOT already wholly visible in the leading
    /// <see cref="MaxSnippetLength"/> characters. Returns <see langword="null"/> — meaning "keep
    /// the leading window unchanged" — when every term present in the body is already visible
    /// there, or when no term occurs in the body at all (a heading-only match: nothing to anchor
    /// on).
    /// </summary>
    /// <remarks>
    /// A term already inside the window is skipped, not treated as a reason to abandon anchoring
    /// for the whole query. Bailing on the first covered term would have confined anchoring to
    /// effectively single-term queries: in the very section this behaviour exists for, "Common step
    /// fields", <c>verifyMode</c> first occurs at 1230 while <c>how</c> occurs at 668 — so
    /// <c>search_docs("how does verifyMode RETRY work")</c>, the example query used in this file's
    /// own <see cref="MaxQueryLength"/> documentation, would still have returned a snippet with no
    /// "verifyMode" in it.
    /// </remarks>
    private static (int Index, int Length)? FindFirstTermBeyondLeadingWindow(
        string body, IReadOnlyList<string> terms)
    {
        (int Index, int Length)? earliest = null;

        foreach (var term in terms)
        {
            var index = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                // Not in this body at all — it may have matched the heading. Nothing to anchor on,
                // and no evidence either way about the window.
                continue;
            }

            if (index + term.Length <= MaxSnippetLength)
            {
                // Already wholly visible in the leading window: this term needs no anchoring, but
                // a different term still might.
                continue;
            }

            // The term's LENGTH travels with its index: the window floor has to guarantee the whole
            // term fits, not merely that the window opens at it.
            if (earliest is null || index < earliest.Value.Index)
            {
                earliest = (index, term.Length);
            }
        }

        return earliest;
    }
}
