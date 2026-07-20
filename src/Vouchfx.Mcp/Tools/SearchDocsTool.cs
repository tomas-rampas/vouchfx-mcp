using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>search_docs</c> tool: searches the two vendored engine documents (the generated language
/// reference and the recipes library) for a free-text query — REQ-005.
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: the actual search runs against <see cref="DocSearchService"/> — see
/// that type's remarks for the scoring approach and why it is deliberately presence-based rather
/// than raw occurrence-count based.
/// </remarks>
internal static class SearchDocsTool
{
    public const string Name = "search_docs";

    private const string Description =
        "Searches the vouchfx documentation (the generated language reference and the recipes " +
        "library) for a free-text query and returns the most relevant sections, each with a " +
        "snippet and a deep link to the corresponding page on vouchfx.io. Use it to answer 'how " +
        "do I...' or 'what does X mean' questions about the engine, e.g. 'how does verifyMode " +
        "RETRY work' or 'seed a database with SQL fixtures'. Always returns a structured result " +
        "— an empty match list when nothing is found — even for a query with no matches; it " +
        "never throws.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("Free-text search query, e.g. 'how does verifyMode RETRY work'.")]
        string query,
        CancellationToken cancellationToken) =>
        StructuredToolResult.Success(DocSearchService.Search(query, cancellationToken));
}
