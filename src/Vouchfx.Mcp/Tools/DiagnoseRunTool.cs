using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>diagnose_run</c> tool (Spec C / M2 Healer): taxonomy-faithful diagnosis plus Fail-only
/// review patch proposals from a JSON Lines events file. Never auto-applies, never hosts an LLM,
/// never re-runs the suite.
/// </summary>
internal static class DiagnoseRunTool
{
    public const string Name = "diagnose_run";

    private const string Description =
        "Diagnoses a completed vouchfx suite run from its JSON Lines event stream (same " +
        "taxonomy-faithful explanation as explain_run) and, for genuine step-level Fail outcomes " +
        "with observation evidence only, returns review-only patch proposals (stepId, rationale, " +
        "unified-diff style patch). EnvironmentError yields infrastructure guidance and never " +
        "YAML rewrite patches; Inconclusive never includes suite-rewrite patches; Pass returns " +
        "empty proposals. Give the path to the run's events file; if omitted, the most recent " +
        "run_suite call this session is used. Never re-runs anything, never writes the suite " +
        "file, never auto-applies proposals — a human (or host LLM under human review) applies " +
        "changes. Free text is not a parameter. Response is capped like explain_run; full detail " +
        "remains in the events file path returned.";

    public static McpServerTool Create(DiagnoseRunOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Optional eventsPath (= null) so the JSON schema marks it optional — same rationale as
        // explain_run / run_suite optional parameters.
        Task<CallToolResult> Handle(
            [Description(
                "Path to the run's JSON Lines event stream file. Omit to use the most recent " +
                "run this session. Suite path is not required for v1; proposals are evidence-based " +
                "from observations when suite YAML is not supplied.")]
            string? eventsPath = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(orchestrator, eventsPath, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        DiagnoseRunOrchestrator orchestrator, string? eventsPath, CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.DiagnoseAsync(eventsPath, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            DiagnoseRunOutcome.Diagnosed diagnosed =>
                StructuredToolResult.Success(diagnosed.Result),
            DiagnoseRunOutcome.NoRunToExplain noRun =>
                StructuredToolResult.Error(noRun.Message),
            DiagnoseRunOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(invalidPath.Message),
            DiagnoseRunOutcome.EventsFileNotFound notFound =>
                StructuredToolResult.Error(notFound.Message),
            DiagnoseRunOutcome.EventsFileUnreadable unreadable =>
                StructuredToolResult.Error(unreadable.Message),
            DiagnoseRunOutcome.NoRecognisableEvents noEvents =>
                StructuredToolResult.Error(noEvents.Message),
            _ =>
                StructuredToolResult.Error("diagnose_run produced an unrecognised outcome."),
        };
    }
}
