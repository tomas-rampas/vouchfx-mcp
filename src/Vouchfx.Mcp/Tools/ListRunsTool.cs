using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>list_runs</c> tool (Sprint 3 / US-S3-03; spec §5.8, §4.5): one page of the run registry's
/// runs, newest first, with the shared opaque cursor.
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: the filters, the positioning and the cursor live in
/// <see cref="ListRunsOrchestrator"/>, which reuses <see cref="OpaqueCursor"/> verbatim under its own
/// scope rather than encoding a second cursor format.
/// </remarks>
internal static class ListRunsTool
{
    public const string Name = "list_runs";

    private const string Description =
        "Lists the runs in the run registry, newest first, one page at a time. Call it to find a " +
        "runId you no longer hold, to see what has run recently, or to correlate runs by the labels " +
        "run_suite recorded. Each entry carries just five fields — runId, status, outcome, startedAt " +
        "and finishedAt; call get_run_status for one run's full record (spec paths, events file, " +
        "labels), explain_run for a diagnosis, or get_run_events for the raw stream. Filter with " +
        "'label' (either 'key=value' for an exact match on both, or a bare 'key' to match any value) " +
        "and/or 'since' (an ISO-8601 timestamp; only runs started at or after it, read as UTC when " +
        "the value carries no offset). 'limit' defaults to 200 and may not exceed 2000. When more " +
        "runs remain, the result carries 'nextCursor': pass it back unchanged as 'cursor' to " +
        "continue, keeping 'label' and 'since' identical (a cursor is opaque and is refused if those " +
        "change; 'limit' may change freely). Paging is a snapshot of the registry as it was when the " +
        "walk started, so runs started mid-walk are not inserted into it. A 'running' status is the " +
        "registry's last recorded state, not a liveness check — a server killed mid-run leaves an " +
        "entry reading 'running' permanently, and cancel_run is what tells you whether such an entry " +
        "is real. The registry spans server restarts when the server was launched with --workspace, " +
        "and is session-scoped otherwise; a workspace holding more than 10,000 runs is listed from an " +
        "arbitrary 10,000-run slice of it. Never spawns the engine CLI, never takes the run lock, and " +
        "is safe to call while a run is in flight.";

    public static McpServerTool Create(ListRunsOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Every parameter carries an explicit '= null' default — this tool has no required argument at
        // all, and without the defaults the SDK's generated schema would mark them required and fail
        // binding on the bare "list everything" call this tool exists for.
        CallToolResult Handle(
            [Description("Maximum runs to return (1-2000, default 200). An out-of-range value is refused, never silently clamped, so a short page is never mistaken for the end of the list.")]
            int? limit = null,
            [Description("A 'nextCursor' from a previous list_runs call, passed back unchanged, to fetch the following page. Opaque — do not construct or parse it. It is bound to the 'label'/'since' it was issued under and is refused if either changes; 'limit' may change freely.")]
            string? cursor = null,
            [Description("Only return runs carrying this label: 'key=value' matches that key with exactly that value, and a bare 'key' matches any run carrying that key whatever its value. Both halves are matched exactly — there is no wildcard or substring matching. Omit for every run.")]
            string? label = null,
            [Description("Only return runs whose startedAt is at or after this ISO-8601 timestamp (e.g. '2026-09-05T10:00:00Z'). A value carrying no offset is read as UTC. Omit for every run.")]
            string? since = null) =>
            Render(orchestrator.List(new ListRunsRequest(limit, cursor, label, since)));

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static CallToolResult Render(ListRunsOutcome outcome) => outcome switch
    {
        ListRunsOutcome.Paged paged =>
            StructuredToolResult.Success(paged.Result),

        ListRunsOutcome.InvalidArgument invalidArgument =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),

        // The same code, and the same four rejection reasons, get_run_events answers with — there is
        // one cursor implementation, so there is one way a cursor can be refused (VFX-E-1506's entry).
        ListRunsOutcome.InvalidCursor invalidCursor =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.InvalidCursor, invalidCursor.Message)),

        _ =>
            StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.UnrecognisedOutcome, "list_runs produced an unrecognised outcome.")),
    };
}
