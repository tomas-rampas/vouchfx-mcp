namespace Vouchfx.Mcp.Docs;

/// <summary>
/// Everything <see cref="Vouchfx.Mcp.Resources.DocResourceRegistry"/> and
/// <see cref="DocSearchService"/> need to know about one of this server's two vendored engine
/// documents.
/// </summary>
/// <param name="Id">
/// A short, stable identifier for the document (e.g. <c>language-reference</c>) — also the final
/// path segment of <paramref name="ResourceUri"/>.
/// </param>
/// <param name="Title">A human-readable title, used as the MCP resource's <c>Name</c>.</param>
/// <param name="Description">A one/two-sentence summary, used as the MCP resource's <c>Description</c>.</param>
/// <param name="EmbeddedResourceName">
/// The exact manifest resource name embedded into this assembly (see
/// <c>Vouchfx.Mcp.csproj</c>'s <c>EmbeddedResource</c> items) — the ONLY place this document's
/// content is ever read from; never the disk.
/// </param>
/// <param name="SiteBaseUrl">
/// The published page on <c>vouchfx.io</c> this vendored copy corresponds to, always ending in a
/// trailing slash so a deep link is built by simple concatenation: <c>SiteBaseUrl + "#" + slug</c>.
/// </param>
/// <param name="ResourceUri">The stable MCP resource URI this document is served under.</param>
public sealed record VendoredDocument(
    string Id, string Title, string Description, string EmbeddedResourceName, string SiteBaseUrl, string ResourceUri);

/// <summary>
/// The fixed catalogue of vendored engine documents this server exposes as MCP resources and
/// searches via <c>search_docs</c> — REQ-005. Both documents are embedded, byte-exact copies of the
/// pinned engine commit's <c>docs/language-reference.md</c> and <c>docs/recipes.md</c> (see
/// <c>ENGINE_PIN</c> and <c>vendored/README.md</c>).
/// </summary>
public static class VendoredDocuments
{
    /// <summary>
    /// The generated language reference: every common step field and every registered step type's
    /// required/optional fields.
    /// </summary>
    public static readonly VendoredDocument LanguageReference = new(
        Id: "language-reference",
        Title: "vouchfx Language Reference",
        Description:
            "The generated .e2e.yaml language reference: every common step field (id, type, " +
            "verifyMode, timeout, …) and every registered step type's required/optional fields, " +
            "in Markdown. Byte-identical to the pinned engine's docs/language-reference.md.",
        EmbeddedResourceName: "Vouchfx.Mcp.Vendored.language-reference.md",
        SiteBaseUrl: "https://vouchfx.io/language-reference/",
        ResourceUri: "vouchfx-docs:///language-reference");

    /// <summary>Task-oriented recipes for common integration-testing scenarios.</summary>
    public static readonly VendoredDocument Recipes = new(
        Id: "recipes",
        Title: "vouchfx Recipes: Common Patterns and Examples",
        Description:
            "Task-oriented recipes for common testing scenarios — seeding fixtures, test doubles, " +
            "secrets, engine-owned verifyMode: RETRY polling, message-queue verification, CI " +
            "integration, and more — each a runnable .e2e.yaml example with explanation. " +
            "Byte-identical to the pinned engine's docs/recipes.md.",
        EmbeddedResourceName: "Vouchfx.Mcp.Vendored.recipes.md",
        SiteBaseUrl: "https://vouchfx.io/recipes/",
        ResourceUri: "vouchfx-docs:///recipes");

    /// <summary>Every vendored document this server knows about, in the order it advertises them.</summary>
    public static IReadOnlyList<VendoredDocument> All { get; } = [LanguageReference, Recipes];

    /// <summary>Finds a vendored document by its <see cref="VendoredDocument.Id"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> does not name a known document.</exception>
    public static VendoredDocument Find(string id) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown vendored document id '{id}'.");
}
