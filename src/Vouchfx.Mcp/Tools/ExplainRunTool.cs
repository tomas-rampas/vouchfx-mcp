using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Diagnosis;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>explain_run</c> tool: diagnoses a completed suite run from its JSON Lines event stream —
/// REQ-007. Pure read + parse + diagnose: never spawns the CLI, the validation worker, or a
/// container.
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: every gate (path resolution/safety, the events-file read, the
/// diagnosis itself) lives in <see cref="ExplainRunOrchestrator"/> — see that type's remarks for the
/// full ordering and rationale. This type's only job is mapping each <see cref="ExplainRunOutcome"/>
/// case to the right <see cref="CallToolResult"/> shape.
/// </remarks>
internal static class ExplainRunTool
{
    public const string Name = "explain_run";

    private const string Description =
        "Explains a completed vouchfx suite run in plain language from its JSON Lines event " +
        "stream: the verdict and what that CATEGORY means (pass / a genuine defect / an " +
        "infrastructure problem that implies no test defect / inconclusive), the failing or " +
        "inconclusive step(s) with their RETRY attempt timeline, and the observation/diff " +
        "evidence the events carry. Give it the path to the run's events file; if omitted, the " +
        "most recent run_suite call this session is used. Never re-runs anything — this only " +
        "reads and diagnoses an existing events file. The diagnosis is trimmed to fit a 32KB " +
        "budget, so a very large or noisy run returns bounded evidence and the full detail remains " +
        "in the events file itself, whose path is always included.";

    public static McpServerTool Create(ExplainRunOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // The '= null' default is load-bearing, not stylistic: it is what makes the SDK's generated
        // JSON schema mark eventsPath as OPTIONAL (see ExplainRun_Schema_HasOptionalStringEventsPath)
        // and lets a caller omit it entirely without the SDK failing parameter binding before Handle
        // even runs — mirrors RunSuiteTool's identical rationale for its own optional parameters.
        Task<CallToolResult> Handle(
            [Description("Path to the run's JSON Lines event stream file. Omit to use the most recent run this session.")]
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
        ExplainRunOrchestrator orchestrator, string? eventsPath, CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.ExplainAsync(eventsPath, cancellationToken);

        return outcome switch
        {
            ExplainRunOutcome.Diagnosed diagnosed =>
                StructuredToolResult.Success(diagnosed.Diagnosis),
            ExplainRunOutcome.NoRunToExplain noRun =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.NoRunToExplain, noRun.Message)),
            // The same code PathSafetyGuard's own rejection carries: this outcome is that guard's
            // UNC/network verdict on the events path, so one condition keeps one code.
            ExplainRunOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message)),
            ExplainRunOutcome.EventsFileNotFound notFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileNotFound, notFound.Message)),
            ExplainRunOutcome.EventsFileUnreadable unreadable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileUnreadable, unreadable.Message)),
            ExplainRunOutcome.NoRecognisableEvents noEvents =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.NoRecognisableEvents, noEvents.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "explain_run produced an unrecognised outcome.")),
        };
    }
}
