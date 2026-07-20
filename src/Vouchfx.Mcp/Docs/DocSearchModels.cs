namespace Vouchfx.Mcp.Docs;

/// <summary>One matching section a <see cref="DocSearchService"/> query found.</summary>
/// <param name="Source">The vendored document's title (e.g. <c>vouchfx Language Reference</c>).</param>
/// <param name="HeadingPath">The matching section's <see cref="DocSection.HeadingPath"/>.</param>
/// <param name="Snippet">
/// The section's body text, capped at <see cref="DocSearchService.MaxSnippetLength"/> characters
/// (an ellipsis is appended when truncated).
/// </param>
/// <param name="Url">
/// A deep link to the matching section on the public documentation site: the document's published
/// page URL followed by <c>#</c> and the section's heading-anchor slug.
/// </param>
public sealed record DocSearchMatch(string Source, string HeadingPath, string Snippet, string Url);

/// <summary>The outcome of a <c>search_docs</c> query — REQ-005's search_docs result contract.</summary>
/// <param name="Query">
/// The query as received, sanitised via <see cref="TextSanitiser"/> before being echoed back (the
/// query is agent-supplied, untrusted text).
/// </param>
/// <param name="Matches">
/// The best-scoring sections found, most relevant first, capped at
/// <see cref="DocSearchService.MaxResults"/>; empty (never <see langword="null"/>, never an error)
/// when nothing matched.
/// </param>
public sealed record DocSearchResult(string Query, IReadOnlyList<DocSearchMatch> Matches);
