using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>get_run_status</c> tool (Sprint 3 / US-S3-03; spec §5.8): one run's current lifecycle
/// state, straight from the persisted run registry.
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: the argument bounds and the registry lookup live in
/// <see cref="GetRunStatusOrchestrator"/>. This type maps each <see cref="GetRunStatusOutcome"/> case
/// to the right <see cref="CallToolResult"/> shape and owns the tool's name, description and schema —
/// this codebase's standing rule that those belong to the tool's own factory.
/// </remarks>
internal static class GetRunStatusTool
{
    public const string Name = "get_run_status";

    private const string Description =
        "Returns one run's current lifecycle state from the run registry: its status " +
        "(running / completed / cancelled), its verdict once it has one, when it started and " +
        "finished, which suites it covered, where its event stream lives, and the labels run_suite " +
        "recorded for it. Call it to poll a run you started, to re-find a run after your own process " +
        "restarted, or to turn a runId into the eventsFilePath the diagnosis tools read. The answer " +
        "is the registry's own record — the same source explain_run and get_run_events resolve a " +
        "runId through — so it can never disagree with them. The registry spans server restarts when " +
        "the server was launched with --workspace, and is session-scoped otherwise. A 'running' " +
        "status is the registry's LAST RECORDED state, not a liveness check: a server killed mid-run " +
        "never records its completion and leaves an entry reading 'running' permanently, and there " +
        "is no reaper. Use cancel_run to find out which it is — it answers 'already_finished', " +
        "'cancelled', VFX-E-1507 (another server process is running it) or VFX-E-1508 (nothing is " +
        "running it; the entry is residue). Per-step detail is not here: call explain_run for a " +
        "diagnosis or get_run_events for the raw stream. Never spawns the engine CLI, never takes " +
        "the run lock, and is safe to call while a run is in flight.";

    public static McpServerTool Create(GetRunStatusOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        CallToolResult Handle(
            [Description("The run to report on — the 'runId' run_suite returned on its result (e.g. 'run-1f2e…'), or one list_runs reported. Resolved through the run registry, which spans server restarts when the server was launched with --workspace and is session-scoped otherwise.")]
            string runId) =>
            Render(orchestrator.Get(new GetRunStatusRequest(runId)));

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static CallToolResult Render(GetRunStatusOutcome outcome) => outcome switch
    {
        GetRunStatusOutcome.Found found =>
            StructuredToolResult.Success(found.Result),

        GetRunStatusOutcome.InvalidArgument invalidArgument =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),

        // The same code get_run_events answers for the identical condition — see VFX-E-1505's
        // catalogue entry, which anticipates this tool sharing it by name.
        GetRunStatusOutcome.RunNotFound runNotFound =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.RunNotFound, runNotFound.Message)),

        _ =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.UnrecognisedOutcome, "get_run_status produced an unrecognised outcome.")),
    };
}
