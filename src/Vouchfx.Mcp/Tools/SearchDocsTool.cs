using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>search_docs</c> tool: will search the vouchfx documentation for a free-text query.
/// </summary>
internal static class SearchDocsTool
{
    public const string Name = "search_docs";

    private const string Description =
        "Searches the vouchfx documentation (the YAML DSL specification, the engineering " +
        "blueprint, and the generated language reference) for a free-text query and returns the " +
        "most relevant passages with source references. Use it to answer 'how do I...' or 'what " +
        "does X mean' questions about the engine.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("Free-text search query, e.g. 'how does verifyMode RETRY work'.")]
        string query) =>
        StubToolResult.NotImplemented(Name, "Searching the vouchfx documentation");
}
