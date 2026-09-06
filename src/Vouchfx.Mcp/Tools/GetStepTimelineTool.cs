using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>get_step_timeline</c> tool (Sprint 3 / US-S3-06; spec §5.10): one step's COMPLETE RETRY
/// attempt timeline, extracted from the same parsed event stream <c>explain_run</c> reads and immune
/// to the response-size tiers that shrink <c>explain_run</c>'s own copy of it.
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper: every gate (argument bounds, the registry lookup, the <c>specPath</c>
/// adjudication, path safety, the bounded read, the shared parse, and the response budget) lives in
/// <see cref="GetStepTimelineOrchestrator"/>. This type maps each
/// <see cref="GetStepTimelineOutcome"/> case to the right <see cref="CallToolResult"/> shape and owns
/// the tool's name, description and schema.
/// </para>
/// <para>
/// <b>The description states the attempt vocabulary explicitly</b>, for the reason
/// <see cref="GetRunEventsTool"/>'s does the same for wire tokens: a model reading it will otherwise
/// assume <c>outcome</c> is the four-way verdict taxonomy under a different spelling, and
/// sprint-00-overview.md §5 treats that conflation as a defect. It also states which fields this build
/// cannot source, so a host is not left to infer meaning from a null.
/// </para>
/// </remarks>
internal static class GetStepTimelineTool
{
    public const string Name = "get_step_timeline";

    private const string Description =
        "Returns ONE step's complete attempt timeline from a finished run — every RETRY poll the " +
        "engine recorded for it, with what each attempt observed. Call it when explain_run showed a " +
        "step that retried and you need the whole history rather than the first few attempts: " +
        "explain_run's response-size tiers shrink its 'attempts' arrays (to ten, then five, then " +
        "none) under pressure, and this tool never shrinks the LIST — it drops per-attempt evidence " +
        "TEXT instead, and says so with 'observedCapped'. It reads the same event stream explain_run " +
        "parses, so the two can never disagree; it never re-runs anything, never spawns the engine " +
        "CLI, and never takes the run lock. " +
        "Each attempt's 'outcome' is this tool's OWN three-value vocabulary — 'matched' (this poll " +
        "found what the step was waiting for), 'unmatched' (it did not, which is the ordinary state " +
        "of every poll before the last one and NOT a failure), or 'error' (no determination was " +
        "reached; the attempt's 'error' field says why). It is NOT the Pass/Fail/EnvironmentError/" +
        "Inconclusive verdict taxonomy and NOT the engine's PASS/FAIL/ENV_ERROR/INCONCLUSIVE wire " +
        "tokens; the step's own verdict appears in 'conclusion' instead. " +
        "Each attempt's 'at' is the engine's own event timestamp, relayed verbatim — but do NOT " +
        "difference two of them to time anything: the engine stamps it when it writes its report, so " +
        "every event in one run tends to share the same value. Use each attempt's 'tMs' — the engine's " +
        "own per-attempt duration in milliseconds — to order and time the timeline. " +
        "Two fields are always null: each attempt's 'delayMs' (the backoff before it), which the event " +
        "stream does not carry at all, and the step's 'timeoutMs', which this build does not source. " +
        "They are explicit nulls rather than values synthesised from other numbers; for a step's " +
        "declared timeout, read the suite with validate_suite, or read the raw 'step-started' event " +
        "through get_run_events. " +
        "'verifyMode' describes what THIS RUN evidenced, not what the suite declared: 'RETRY' when " +
        "more than one attempt was recorded (only engine-owned polling produces that), 'ONCE' when " +
        "exactly one was, and null when the run recorded no attempt event for the step — which is the " +
        "ORDINARY shape of a step that did not retry, because the engine emits no attempt events for " +
        "one. Read 'RETRY' as \"this step retried\" and treat 'ONCE' and null alike as \"it did not\"; " +
        "a null verifyMode with an empty 'attempts' list is a normal successful result, not an error. " +
        "'ONCE' is spec §5.10's token and is deliberately not the suite language's own value (that is " +
        "IMMEDIATE) — do not copy it into a suite. " +
        "'specPath' must name one of the suites the run covered (get_run_status lists them) and is " +
        "refused otherwise. For a run that covered SEVERAL suites it is informational rather than a " +
        "filter: the engine's events carry no per-suite attribution, so the timeline is the run-wide " +
        "one for that step id. 'specPathAttributed' is false in exactly that case.";

    public static McpServerTool Create(GetStepTimelineOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // All three parameters are required — spec §5.10's input shape marks none of them optional —
        // so none carries a default. Contrast get_run_events, whose optional filters each need an
        // explicit '= null' to be advertised as optional in the SDK's generated schema.
        Task<CallToolResult> Handle(
            [Description("The run to read — the 'runId' run_suite returned on its result (e.g. 'run-1f2e…'), or one list_runs reported. Resolved through the run registry, which spans server restarts when the server was launched with --workspace and is session-scoped otherwise.")]
            string runId,
            [Description("The suite the step belongs to. Must be one of the paths this run covered — call get_run_status with the same runId to see them. A relative path is resolved against the workspace root first. For a run that covered several suites this is validated but cannot filter: the engine's events carry no per-suite attribution, and 'specPathAttributed' comes back false to say so.")]
            string specPath,
            [Description("The step whose attempt timeline you want, matched exactly. Take it from explain_run's notableSteps[].stepId or from get_run_events with types ['step-completed']. A step id the run never recorded is refused (VFX-E-1510) rather than answered with an empty timeline.")]
            string stepId,
            CancellationToken cancellationToken = default) =>
            HandleAsync(orchestrator, new GetStepTimelineRequest(runId, specPath, stepId), cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        GetStepTimelineOrchestrator orchestrator,
        GetStepTimelineRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.GetAsync(request, cancellationToken);

        return outcome switch
        {
            GetStepTimelineOutcome.Found found =>
                StructuredToolResult.Success(found.Result),

            // The same code every other tool uses for "a value this server rejects on its own terms".
            GetStepTimelineOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),

            // Shared with get_run_status/cancel_run/get_run_events — one code, one wording, for one
            // catalogued condition (see RunIdArgument.DescribeMissingRun).
            GetStepTimelineOutcome.RunNotFound runNotFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunNotFound, runNotFound.Message)),

            GetStepTimelineOutcome.SpecPathNotInRun specPathNotInRun =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.SpecPathNotInRun, specPathNotInRun.Message)),
            GetStepTimelineOutcome.StepNotInRun stepNotInRun =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.StepNotInRun, stepNotInRun.Message)),

            // The three events-file outcomes map exactly as get_run_events maps them — the same three
            // conditions on the same file through the same reader.
            GetStepTimelineOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message)),
            GetStepTimelineOutcome.EventsFileNotFound notFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileNotFound, notFound.Message)),
            GetStepTimelineOutcome.EventsFileUnreadable unreadable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EventsFileUnreadable, unreadable.Message)),

            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "get_step_timeline produced an unrecognised outcome.")),
        };
    }
}
