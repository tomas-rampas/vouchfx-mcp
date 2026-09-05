using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Planning;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Scaffold;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The single point where every MCP tool this server advertises is assembled.
/// </summary>
/// <remarks>
/// Each tool's name, description, and input schema are owned by that tool's own <c>Create()</c>
/// factory (see e.g. <see cref="ValidateSuiteTool"/>); this registry only aggregates them. All
/// sixteen tools are real — including <c>plan_coverage</c> (Spec D / M3 Planner),
/// <c>scaffold_suite</c> (Spec B Generator), <c>diagnose_run</c> (Spec C M2 Healer),
/// <c>explain_diagnostic</c> (US-S1-05's code catalogue lookup), <c>get_schema</c> (US-S2-01's
/// composed-schema reader), <c>normalize_suite</c> (US-S2-04's read-only canonical formatter),
/// <c>get_run_events</c> (US-S3-05's paged raw event reader), and US-S3-03's run-lifecycle trio
/// <c>get_run_status</c>, <c>cancel_run</c> and <c>list_runs</c>.
/// <c>plan_coverage</c> is registered immediately before <c>scaffold_suite</c>, reflecting the
/// host workflow it composes with: plan → scaffold → validate → run. <c>explain_diagnostic</c>,
/// then <c>get_schema</c>, then <c>normalize_suite</c>, then <c>get_run_events</c>, then the three
/// run-lifecycle tools are appended last — this registry is append-only: earlier tools keep their
/// <c>tools/list</c> position when a new one lands. <c>get_schema</c> and <c>normalize_suite</c> are
/// therefore NOT filed next to their CLI-free siblings at the head of the list, and neither
/// <c>get_run_events</c> nor <c>get_run_status</c>/<c>cancel_run</c>/<c>list_runs</c> is filed next
/// to <c>run_suite</c> or <c>explain_run</c> despite belonging to the same run lifecycle —
/// deliberately: honouring append-only ordering matters more than thematic grouping, since a host
/// that cached positions must not see them shift.
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
    /// <param name="planCoverageOrchestrator">
    /// Spec D / M3 Planner's coverage-and-gap pipeline (pinned CLI <c>plan --json</c>), passed only
    /// to <see cref="PlanCoverageTool"/>.
    /// </param>
    /// <param name="getSchemaOrchestrator">
    /// US-S2-01's composed-schema reader (embedded vendored schema, optionally cross-verified
    /// against <c>vouchfx schema</c>), passed only to <see cref="GetSchemaTool"/>.
    /// </param>
    /// <param name="getRunEventsOrchestrator">
    /// US-S3-05's paged raw-event reader (run registry → events file → filtered, sanitised page),
    /// passed only to <see cref="GetRunEventsTool"/>. Never takes the run lock — it is a read-only
    /// tool (spec §4.6).
    /// </param>
    /// <param name="getRunStatusOrchestrator">
    /// US-S3-03's registry lookup, passed only to <see cref="GetRunStatusTool"/>. Read-only and
    /// lock-free.
    /// </param>
    /// <param name="cancelRunOrchestrator">
    /// US-S3-03's cancel bridge, passed only to <see cref="CancelRunTool"/>. The one tool here that is
    /// NOT read-only, and the one that may probe the run lock — see
    /// <see cref="Run.CancelRunOrchestrator"/> for why both are true of it and of nothing else.
    /// </param>
    /// <param name="listRunsOrchestrator">
    /// US-S3-03's paginated registry listing, passed only to <see cref="ListRunsTool"/>. Read-only,
    /// lock-free, and sharing <see cref="Run.OpaqueCursor"/> with <see cref="GetRunEventsTool"/>.
    /// </param>
    /// <param name="workspace">
    /// US-S3-08's startup workspace, or <see langword="null"/> when the host supplied no
    /// <c>--workspace</c> flag. Passed only to the two tools that reach the validation worker with a
    /// caller-supplied path directly (<see cref="ValidateSuiteTool"/>,
    /// <see cref="NormalizeSuiteTool"/>); every other path-taking tool receives it through the
    /// orchestrator it was already given, which is where its own path gate already lives.
    /// </param>
    public static IReadOnlyList<McpServerTool> CreateAll(
        RunSuiteOrchestrator runSuiteOrchestrator,
        ExplainRunOrchestrator explainRunOrchestrator,
        DiagnoseRunOrchestrator diagnoseRunOrchestrator,
        LiveStepCatalogue liveStepCatalogue,
        ScaffoldSuiteOrchestrator scaffoldSuiteOrchestrator,
        PlanCoverageOrchestrator planCoverageOrchestrator,
        GetSchemaOrchestrator getSchemaOrchestrator,
        GetRunEventsOrchestrator getRunEventsOrchestrator,
        GetRunStatusOrchestrator getRunStatusOrchestrator,
        CancelRunOrchestrator cancelRunOrchestrator,
        ListRunsOrchestrator listRunsOrchestrator,
        Workspace? workspace = null) =>
    [
        ValidateSuiteTool.Create(workspace),
        ListStepTypesTool.Create(liveStepCatalogue),
        DescribeStepTypeTool.Create(liveStepCatalogue),
        SearchDocsTool.Create(),
        PlanCoverageTool.Create(planCoverageOrchestrator),
        ScaffoldSuiteTool.Create(scaffoldSuiteOrchestrator),
        RunSuiteTool.Create(runSuiteOrchestrator),
        ExplainRunTool.Create(explainRunOrchestrator),
        DiagnoseRunTool.Create(diagnoseRunOrchestrator),
        ExplainDiagnosticTool.Create(),
        GetSchemaTool.Create(getSchemaOrchestrator),
        NormalizeSuiteTool.Create(workspace),
        GetRunEventsTool.Create(getRunEventsOrchestrator),
        GetRunStatusTool.Create(getRunStatusOrchestrator),
        CancelRunTool.Create(cancelRunOrchestrator),
        ListRunsTool.Create(listRunsOrchestrator),
    ];
}
