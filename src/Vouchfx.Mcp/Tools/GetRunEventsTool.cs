using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>get_run_events</c> tool (Sprint 3 / US-S3-05; spec §5.11): paged, filtered access to the
/// raw JSON Lines events a run produced, for hosts that want to build their own timeline rather than
/// consume <c>explain_run</c>'s summarised one.
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper: every gate (argument bounds, cursor verification, registry lookup,
/// path safety, the bounded read, filter-then-paginate, and the sanitised relay) lives in
/// <see cref="GetRunEventsOrchestrator"/>. This type's only job is mapping each
/// <see cref="GetRunEventsOutcome"/> case to the right <see cref="CallToolResult"/> shape.
/// </para>
/// <para>
/// <b>Wire vocabulary, and the tool description says so.</b> Events come back carrying the engine's
/// own tokens — <c>PASS</c>, <c>FAIL</c>, <c>ENV_ERROR</c>, <c>INCONCLUSIVE</c> — not this server's
/// <c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c> response strings. That is
/// stated in the description because a model reading it will otherwise assume the two vocabularies
/// are the same one, and sprint-00-overview.md §5 treats conflating them as a defect.
/// </para>
/// </remarks>
internal static class GetRunEventsTool
{
    public const string Name = "get_run_events";

    private const string Description =
        "Returns one page of a completed run's RAW JSON Lines events, exactly as the vouchfx engine " +
        "wrote them — no summarising, no re-running, no interpretation. Call it when you want to " +
        "build your own timeline or dashboard over a run instead of using explain_run's summarised " +
        "diagnosis, or to inspect an event type this server does not model. Give it the 'runId' " +
        "run_suite returned; optionally narrow to one or more event 'types' (e.g. 'step-attempt') " +
        "and/or a single 'stepId', both applied BEFORE paging so 'limit' bounds the matching events " +
        "returned rather than the lines scanned. 'limit' defaults to 200 and may not exceed 2000, " +
        "and a page is additionally bounded by a response-size budget, so you may receive fewer " +
        "events than you asked for. When more matching events remain, the result carries " +
        "'nextCursor': pass it back unchanged as 'cursor' to continue, keeping 'runId', 'types' and " +
        "'stepId' identical (a cursor is opaque and is refused if the filters change; 'limit' may " +
        "change freely). 'truncated' is true when this page is not everything the stream held — this " +
        "server could not read the whole file (its 50 MB / 2,000,000-line bounds), or a single line " +
        "was passed over because it was unreadable (too long to parse under an active filter, or not " +
        "valid JSON with further lines behind it). Check it alongside 'nextCursor' before concluding " +
        "you have the full timeline. " +
        "Events use the engine's WIRE vocabulary — PASS / FAIL / ENV_ERROR / INCONCLUSIVE — not the " +
        "Pass/Fail/EnvironmentError/Inconclusive strings other tools' results carry. Unknown event " +
        "types and unknown fields pass through untouched, but text is NOT byte-identical to the " +
        "file: every string and property name comes back with each non-ASCII character rendered as " +
        "a literal \\uXXXX escape (the same sanitising explain_run applies), strings over 2000 " +
        "characters are cut and the event is flagged '_vfxStringsCapped', and an event too large or " +
        "too deep to reproduce is replaced by a small '_vfxTruncated' marker. Never spawns the " +
        "engine CLI and never blocks a run.";

    public static McpServerTool Create(GetRunEventsOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Every optional parameter carries an explicit '= null' default for the reason
        // ExplainRunTool/RunSuiteTool record: it is what makes the SDK's generated JSON schema mark
        // the parameter OPTIONAL and lets a caller omit it without the SDK failing parameter binding
        // before Handle runs. `runId` deliberately has no default — it is the one required argument.
        Task<CallToolResult> Handle(
            [Description("The run to read — the 'runId' run_suite returned on its result (e.g. 'run-1f2e…'). Resolved through the run registry, which spans server restarts when the server was launched with --workspace and is session-scoped otherwise. Note this is THIS SERVER's id; the engine's own bare-hex runId inside the relayed events is a different value.")]
            string runId,
            [Description("Only return events whose 'type' is one of these (e.g. ['step-attempt','step-completed']). Matched exactly, against the token the engine wrote. Omit for every type.")]
            string[]? types = null,
            [Description("Only return events belonging to this step id. Matched exactly. Omit for every step.")]
            string? stepId = null,
            [Description("Maximum events to return (1-2000, default 200). Applied AFTER 'types'/'stepId', so it bounds matching events. A page may still be shorter if the response-size budget is reached first — check 'nextCursor' rather than the count to decide whether the walk is over.")]
            int? limit = null,
            [Description("A 'nextCursor' from a previous get_run_events call, passed back unchanged, to fetch the following page. Opaque — do not construct or parse it. It is bound to the 'runId'/'types'/'stepId' it was issued under and is refused if any of those change; 'limit' may change freely.")]
            string? cursor = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(
                orchestrator,
                new GetRunEventsRequest(runId, types, stepId, limit, cursor),
                cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        GetRunEventsOrchestrator orchestrator,
        GetRunEventsRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.GetAsync(request, cancellationToken);

        return outcome switch
        {
            GetRunEventsOutcome.Paged paged =>
                StructuredToolResult.Success(paged.Result),

            // The same code every other tool uses for "a value this server rejects on its own terms"
            // — a missing runId, an out-of-range limit, an over-long filter. See VFX-E-1006's
            // catalogue entry for why one code covers all of them.
            GetRunEventsOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),
            GetRunEventsOutcome.InvalidCursor invalidCursor =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidCursor, invalidCursor.Message)),
            GetRunEventsOutcome.RunNotFound runNotFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunNotFound, runNotFound.Message)),

            // The same code PathSafetyGuard's own rejection carries, exactly as explain_run maps it.
            GetRunEventsOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message)),
            GetRunEventsOutcome.EventsFileNotFound notFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileNotFound, notFound.Message)),
            GetRunEventsOutcome.EventsFileUnreadable unreadable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileUnreadable, unreadable.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "get_run_events produced an unrecognised outcome.")),
        };
    }
}
