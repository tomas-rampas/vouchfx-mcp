using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The single point where every MCP tool this server advertises is assembled.
/// </summary>
/// <remarks>
/// Each tool's name, description, and input schema are owned by that tool's own <c>Create()</c>
/// factory (see e.g. <see cref="ValidateSuiteTool"/>); this registry only aggregates them. All six
/// tools are real as of REQ-007 — <c>explain_run</c> was the last stub.
/// </remarks>
public static class ToolRegistry
{
    /// <summary>Creates every tool this server advertises, in the order <c>tools/list</c> reports them.</summary>
    /// <param name="runSuiteOrchestrator">
    /// REQ-006/REQ-008's full run_suite gate + execution pipeline, passed only to
    /// <see cref="RunSuiteTool"/> — the one tool that is CLI/process-dependent. Every other tool is
    /// CLI-independent and never sees it.
    /// </param>
    /// <param name="explainRunOrchestrator">
    /// REQ-007's pure read + parse + diagnose pipeline, passed only to <see cref="ExplainRunTool"/>.
    /// </param>
    public static IReadOnlyList<McpServerTool> CreateAll(
        RunSuiteOrchestrator runSuiteOrchestrator, ExplainRunOrchestrator explainRunOrchestrator) =>
    [
        ValidateSuiteTool.Create(),
        ListStepTypesTool.Create(),
        DescribeStepTypeTool.Create(),
        SearchDocsTool.Create(),
        RunSuiteTool.Create(runSuiteOrchestrator),
        ExplainRunTool.Create(explainRunOrchestrator),
    ];
}
