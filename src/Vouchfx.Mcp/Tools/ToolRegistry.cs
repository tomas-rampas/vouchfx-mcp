using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Scaffold;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The single point where every MCP tool this server advertises is assembled.
/// </summary>
/// <remarks>
/// Each tool's name, description, and input schema are owned by that tool's own <c>Create()</c>
/// factory (see e.g. <see cref="ValidateSuiteTool"/>); this registry only aggregates them. All
/// eight tools are real — including <c>scaffold_suite</c> (Spec B Generator) and
/// <c>diagnose_run</c> (Spec C M2 Healer).
/// </remarks>
public static class ToolRegistry
{
    /// <summary>Creates every tool this server advertises, in the order <c>tools/list</c> reports them.</summary>
    /// <param name="runSuiteOrchestrator">
    /// REQ-006/REQ-008's full run_suite gate + execution pipeline, passed only to
    /// <see cref="RunSuiteTool"/> — the one tool that is CLI/process-dependent for suite execution.
    /// </param>
    /// <param name="explainRunOrchestrator">
    /// REQ-007's pure read + parse + diagnose pipeline, passed only to <see cref="ExplainRunTool"/>.
    /// </param>
    /// <param name="diagnoseRunOrchestrator">
    /// Spec C / M2 Healer pipeline (explain + Fail proposals), passed only to
    /// <see cref="DiagnoseRunTool"/>.
    /// </param>
    /// <param name="liveStepCatalogue">
    /// REQ-010's live engine catalogue (from <c>vouchfx list --json</c>), passed to
    /// <see cref="ListStepTypesTool"/> and <see cref="DescribeStepTypeTool"/>.
    /// </param>
    /// <param name="scaffoldSuiteOrchestrator">
    /// Spec B / REQ-007's scaffold pipeline (pinned CLI <c>scaffold --intent</c>), passed only to
    /// <see cref="ScaffoldSuiteTool"/>.
    /// </param>
    public static IReadOnlyList<McpServerTool> CreateAll(
        RunSuiteOrchestrator runSuiteOrchestrator,
        ExplainRunOrchestrator explainRunOrchestrator,
        DiagnoseRunOrchestrator diagnoseRunOrchestrator,
        LiveStepCatalogue liveStepCatalogue,
        ScaffoldSuiteOrchestrator scaffoldSuiteOrchestrator) =>
    [
        ValidateSuiteTool.Create(),
        ListStepTypesTool.Create(liveStepCatalogue),
        DescribeStepTypeTool.Create(liveStepCatalogue),
        SearchDocsTool.Create(),
        ScaffoldSuiteTool.Create(scaffoldSuiteOrchestrator),
        RunSuiteTool.Create(runSuiteOrchestrator),
        ExplainRunTool.Create(explainRunOrchestrator),
        DiagnoseRunTool.Create(diagnoseRunOrchestrator),
    ];
}
