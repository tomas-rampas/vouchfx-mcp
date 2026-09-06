using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
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
        "finished run in the run registry is used — that registry spans server restarts when the " +
        "server was launched with --workspace, and is session-scoped otherwise. Never re-runs " +
        "anything, never writes the suite " +
        "file, never auto-applies proposals — a human (or host LLM under human review) applies " +
        "changes. Free text is not a parameter. The diagnosis is trimmed to fit a 32KB budget, " +
        "same as explain_run; full detail remains in the events file path returned.";

    public static McpServerTool Create(DiagnoseRunOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Optional eventsPath (= null) so the JSON schema marks it optional — same rationale as
        // explain_run / run_suite optional parameters.
        Task<CallToolResult> Handle(
            [Description(
                "Path to the run's JSON Lines event stream file. Omit to use the most recent " +
                "finished run in the run registry (which spans server restarts when the server was " +
                "launched with --workspace; session-scoped otherwise). Suite path is not required " +
                "for v1; proposals are evidence-based from observations when suite YAML is not " +
                "supplied.")]
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
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.NoRunToExplain, noRun.Message)),
            // Deliberately the SAME five codes explain_run uses: the two tools read the same events
            // file through the same guards and fail for the same five reasons, so a host that has
            // learned explain_run's codes already knows diagnose_run's.
            DiagnoseRunOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message)),
            DiagnoseRunOutcome.EventsFileNotFound notFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileNotFound, notFound.Message)),
            DiagnoseRunOutcome.EventsFileUnreadable unreadable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileUnreadable, unreadable.Message)),
            DiagnoseRunOutcome.NoRecognisableEvents noEvents =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.NoRecognisableEvents, noEvents.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "diagnose_run produced an unrecognised outcome.")),
        };
    }
}
