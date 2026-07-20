using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The single point where every MCP tool this server advertises is assembled.
/// </summary>
/// <remarks>
/// Each tool's name, description, and input schema are owned by that tool's own <c>Create()</c>
/// factory (see e.g. <see cref="ValidateSuiteTool"/>); this registry only aggregates them. A
/// later todo swaps a stub <c>Handle</c> method body for a real implementation one tool at a
/// time — that is a change to that tool's file alone, never to this list.
/// </remarks>
public static class ToolRegistry
{
    /// <summary>Creates every tool this server advertises, in the order <c>tools/list</c> reports them.</summary>
    public static IReadOnlyList<McpServerTool> CreateAll() =>
    [
        ValidateSuiteTool.Create(),
        ListStepTypesTool.Create(),
        DescribeStepTypeTool.Create(),
        SearchDocsTool.Create(),
        RunSuiteTool.Create(),
        ExplainRunTool.Create(),
    ];
}
