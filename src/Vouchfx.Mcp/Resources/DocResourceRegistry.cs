using ModelContextProtocol.Server;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// The single point where every MCP resource this server advertises is assembled — the resource
/// analogue of <see cref="Vouchfx.Mcp.Tools.ToolRegistry"/>.
/// </summary>
/// <remarks>
/// One static, non-templated resource per <see cref="VendoredDocuments.All"/> entry — REQ-005.
/// Each resource's content is the vendored document's full, verbatim Markdown text (read once, from
/// the embedded assembly resource, via <see cref="VendoredDocRepository.GetRawText"/> — never the
/// disk); its URI, name, description, and MIME type all come from that document's own
/// <see cref="VendoredDocument"/> entry, so adding a third vendored document later is a one-line
/// change there, not here.
/// </remarks>
public static class DocResourceRegistry
{
    /// <summary>Creates every resource this server advertises, in the order <c>resources/list</c> reports them.</summary>
    public static IReadOnlyList<McpServerResource> CreateAll() =>
        [.. VendoredDocuments.All.Select(CreateResource)];

    private static McpServerResource CreateResource(VendoredDocument doc) =>
        McpServerResource.Create(
            () => VendoredDocRepository.GetRawText(doc.Id),
            new McpServerResourceCreateOptions
            {
                UriTemplate = doc.ResourceUri,
                Name = doc.Title,
                Description = doc.Description,
                MimeType = "text/markdown",
            });
}
