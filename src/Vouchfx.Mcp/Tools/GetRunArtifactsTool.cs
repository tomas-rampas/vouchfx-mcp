using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>get_run_artifacts</c> tool (Sprint 3 / US-S3-07; spec §5.12): one consistent place to ask
/// what a finished run left behind — reporting what this build can derive from the run registry and the
/// run's own event stream, and saying explicitly, per field, what is still gated on upstream ask
/// <b>U4</b>.
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper: every gate (argument bounds, the <c>kind</c> vocabulary, the registry
/// lookup, path safety, the bounded read, the parse and the projection) lives in
/// <see cref="GetRunArtifactsOrchestrator"/>. This type maps each <see cref="GetRunArtifactsOutcome"/>
/// case to the right <see cref="CallToolResult"/> shape and owns the tool's name, description and
/// schema.
/// </para>
/// <para>
/// <b>The description states the partial behaviour, which is US-S3-07's AC-004 half that lives in
/// code.</b> The criterion is that the gap "is documented on the tool itself (description text) and in
/// docs/tools-and-resources.md, not left to be discovered by a caller receiving an empty array" — so
/// the text below says up front that <c>logs</c> is always empty, that the engine's own report paths
/// are not available, what the environment section can and cannot contain, and that <c>container</c>
/// and <c>tailLines</c> are accepted for forward compatibility without selecting or bounding anything
/// yet. A model reading it should never have to infer any of that from a response.
/// </para>
/// </remarks>
internal static class GetRunArtifactsTool
{
    public const string Name = "get_run_artifacts";

    private const string Description =
        "Returns what a finished run left behind: its event-stream artefact, and whatever environment " +
        "resources the run's own events named. Reads only the run registry and that run's JSON Lines " +
        "event stream — it never re-runs anything, never spawns the engine CLI, and never takes the " +
        "run lock, so it is safe to call while another run is in flight. " +
        "THIS TOOL IS PARTIAL TODAY and says so on every result with 'partial': true, plus a 'gaps' " +
        "array naming each missing field, why it is missing, and the upstream ask that would close it. " +
        "Concretely, and so you do not have to discover it from an empty array: " +
        "(1) 'logs' is ALWAYS an empty array — this server has no container log access at all, and " +
        "returns an empty list rather than an error or an invented line. Full log access awaits " +
        "upstream ask U4. " +
        "(2) 'reports' carries only 'events' — the path of the run's own JSON Lines stream (the file " +
        "explain_run, get_run_events and get_step_timeline read), with 'available' saying whether it " +
        "still exists. The engine's own HTML and JUnit report paths are OMITTED, not null: the engine " +
        "owns where it writes them and this server is never told. Also awaits U4. " +
        "(3) 'environment' reports resources under 'resources', NOT under 'services'/'dependencies', " +
        "which are always empty. The only environment identifier in the v1 event stream is the " +
        "resource an 'environment-error' event names, and that event does not say whether the name is " +
        "a service or a dependency — so each entry says role 'unclassified' rather than guessing, and " +
        "'health' is always null, meaning NOT OBSERVED and never 'unhealthy'. A run in which nothing " +
        "went wrong therefore reports NO environment resources: that is a correct, successful answer, " +
        "not a failure of this tool. " +
        "'kind' selects one section ('logs', 'reports', 'environment') or all of them ('all', the " +
        "default when omitted); an unselected section is omitted from the result entirely, and the " +
        "echoed 'kind' tells you which you asked for. " +
        "'container' and 'tailLines' are accepted and VALIDATED but currently select and bound nothing " +
        "— they exist so this tool's contract does not change again when U4 lands. 'tailLines' " +
        "defaults to 200 and must be between 1 and 5000; a value outside that range is refused with " +
        "VFX-E-1006 rather than clamped, so you never believe you received more lines than you did. " +
        "A run whose events file has since been deleted is reported, not refused: " +
        "'reports.events.available' comes back false with a matching 'gaps' entry, and the other " +
        "sections still answer.";

    public static McpServerTool Create(GetRunArtifactsOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Every optional parameter carries an explicit '= null' default for the reason
        // GetRunEventsTool records: it is what makes the SDK's generated JSON schema mark the parameter
        // OPTIONAL and lets a caller omit it without the SDK failing parameter binding before Handle
        // runs. `runId` deliberately has no default — it is the one required argument.
        Task<CallToolResult> Handle(
            [Description("The run to inspect — the 'runId' run_suite returned on its result (e.g. 'run-1f2e…'), or one list_runs reported. Resolved through the run registry, which spans server restarts when the server was launched with --workspace and is session-scoped otherwise.")]
            string runId,
            [Description("Which section to return: 'reports', 'logs', 'environment', or 'all'. Omit for 'all'. Matched case-insensitively; any other value is refused with VFX-E-1006. A section you did not select is omitted from the result rather than returned empty, so you can tell 'not asked for' from 'nothing there'.")]
            string? kind = null,
            [Description("Which container's logs to tail. ACCEPTED AND VALIDATED BUT SELECTS NOTHING in this build — there is no container log access at all (upstream ask U4). It is echoed back on the result so you can confirm the server read it. At most 256 characters.")]
            string? container = null,
            [Description("How many log lines to tail (1-5000, default 200). ACCEPTED AND VALIDATED BUT BOUNDS NOTHING in this build — 'logs' is always empty (upstream ask U4). A value outside the range is refused rather than clamped, so the bound you code against today is the one that will apply when log access lands.")]
            int? tailLines = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(
                orchestrator,
                new GetRunArtifactsRequest(runId, kind, container, tailLines),
                cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        GetRunArtifactsOrchestrator orchestrator,
        GetRunArtifactsRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.GetAsync(request, cancellationToken);

        return outcome switch
        {
            GetRunArtifactsOutcome.Found found =>
                StructuredToolResult.Success(found.Result),

            // The same code every other tool uses for "a value this server rejects on its own terms" —
            // a missing runId, an unknown 'kind', an out-of-range 'tailLines'. See VFX-E-1006's
            // catalogue entry for why one code covers all of them.
            GetRunArtifactsOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),

            // Shared with get_run_status/cancel_run/get_run_events/get_step_timeline — one code, one
            // wording, for one catalogued condition (see RunIdArgument.DescribeMissingRun).
            GetRunArtifactsOutcome.RunNotFound runNotFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunNotFound, runNotFound.Message)),

            // The same code PathSafetyGuard's own rejection carries, exactly as explain_run maps it.
            // Note what is deliberately ABSENT beside it: there is no VFX-E-1004/VFX-E-1005 mapping
            // here, because a swept or unreadable events file comes back as a successful, partial
            // result — see GetRunArtifactsOutcome's remarks.
            GetRunArtifactsOutcome.InvalidPath invalidPath =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message)),

            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "get_run_artifacts produced an unrecognised outcome.")),
        };
    }
}
