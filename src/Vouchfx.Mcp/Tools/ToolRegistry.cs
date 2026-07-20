using ModelContextProtocol.Server;
using Vouchfx.Mcp.Cli;

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
    /// <param name="cliPinVerifier">
    /// REQ-008's CLI presence + version handshake, passed only to <see cref="RunSuiteTool"/> — the
    /// one tool that is CLI-dependent. Every other tool is CLI-independent and never sees it.
    /// </param>
    public static IReadOnlyList<McpServerTool> CreateAll(CliPinVerifier cliPinVerifier) =>
    [
        ValidateSuiteTool.Create(),
        ListStepTypesTool.Create(),
        DescribeStepTypeTool.Create(),
        SearchDocsTool.Create(),
        RunSuiteTool.Create(cliPinVerifier),
        ExplainRunTool.Create(),
    ];
}
