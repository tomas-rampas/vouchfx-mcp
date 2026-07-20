namespace Vouchfx.Mcp.Docs;

/// <summary>One Markdown heading's own content, as parsed by <see cref="MarkdownDocumentParser"/>.</summary>
/// <param name="DocId">The <see cref="VendoredDocument.Id"/> this section belongs to.</param>
/// <param name="HeadingPath">
/// This heading's own text, preceded by every enclosing ancestor heading's text, joined with
/// <c>" &gt; "</c> (e.g. <c>vouchfx Recipes: Common Patterns and Examples &gt; Engine-owned polling
/// with verifyMode: RETRY</c>) — gives a match some surrounding context beyond the bare heading.
/// </param>
/// <param name="Heading">This heading's own text alone (the last segment of <paramref name="HeadingPath"/>).</param>
/// <param name="Level">The heading's Markdown level (1 for <c>#</c> through 6 for <c>######</c>).</param>
/// <param name="Slug">
/// This heading's anchor slug, as <c>vouchfx.io</c> actually renders it — see
/// <see cref="MarkdownSlugifier"/>.
/// </param>
/// <param name="Body">
/// Every line between this heading and the next heading of any level (leading/trailing blank lines
/// trimmed) — does NOT include the content of a nested sub-heading (that becomes its own, separate
/// <see cref="DocSection"/>).
/// </param>
public sealed record DocSection(string DocId, string HeadingPath, string Heading, int Level, string Slug, string Body);
