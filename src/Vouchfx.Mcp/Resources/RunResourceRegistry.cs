using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Resources;

// Vouchfx.Mcp.Resources — the vouchfx://runs/{runId}/… family (Sprint 5 / US-S5-01 AC-006;
// plan §2.4's resourceUri hand-off).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE ONE RULE THIS FILE EXISTS TO OBEY: NO SECOND IMPLEMENTATION
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The AC is explicit that these resources serve "the SAME data" explain_run / get_run_events /
// get_run_artifacts return, and this sprint's own Out-of-scope forbids "a competing second
// implementation". So every handler below is three lines of plumbing over the orchestrator the
// corresponding TOOL already calls, with the SAME instance injected at the SAME registration site:
//
//   vouchfx://runs/{runId}/verdict          → ExplainRunOrchestrator.ExplainAsync
//   vouchfx://runs/{runId}/events           → GetRunEventsOrchestrator.GetAsync
//   vouchfx://runs/{runId}/logs/{container} → GetRunArtifactsOrchestrator.GetAsync (kind: logs)
//
// Nothing here reads an events file, parses an event, applies a bound, or decides a verdict. If a
// future change makes one of these bodies differ from its tool's, that change is the bug.
//
// The `verdict` resource takes one extra step the others do not, and it is a registry LOOKUP rather
// than logic: ExplainRunOrchestrator's public entry point takes an events PATH (that is its tool's
// own argument), so a runId is resolved through the run registry to the eventsFilePath the registry
// itself minted, and THAT is handed over. It is the identical two-step explain_run performs when it
// defaults to the most recent finished run — the same registry, the same field, and the same
// containment check downstream — with the run named instead of inferred.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// LOGS ARE EMPTY, AND SAY SO — the same honesty get_run_artifacts already ships
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// There is NO container log access in this build: no engine flag exposes it and this server never
// talks to a container runtime (upstream ask U4). get_run_artifacts reports that as an empty `logs`
// array plus a `gaps` entry naming the field, the reason and the ask, with `partial: true` computed
// from that list — sprint-00-overview.md §3 stance (b).
//
// This resource inherits ALL of that by construction, because it serves that orchestrator's own
// result object. What it deliberately does NOT do is invent a friendlier shape for the resource
// form — no `lines: []` at the top level, no 204, no empty body. A host reading
// vouchfx://runs/r/logs/api gets the same `partial`/`gaps` pair the tool gives it and can act on the
// same field. The `{container}` segment is accepted, validated and ECHOED, and it selects nothing;
// that is the forward-compatibility posture GetRunArtifactsResult.Container documents, held here so
// this URI's contract does not change when U4 lands.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// NEITHER RESOURCE TAKES THE RUN LOCK
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Spec §4.6's "read-only tools are safe to call concurrently" applies verbatim to read-only
// RESOURCES, and holds here structurally for the reason it holds for the four events-file readers:
// there is nothing in these handlers to take a lock with. RunLockSourceGuardTests pins
// IRunLock.TryAcquire to its two permitted call sites; this file is not one of them and must not
// become one.

/// <summary>
/// The <c>vouchfx://runs/{runId}/…</c> resource family — a run's verdict, its raw events, and its
/// container-log inventory, each serving exactly what the corresponding tool returns.
/// </summary>
public static class RunResourceRegistry
{
    /// <summary>
    /// Creates all three run resources, in the order <c>resources/templates/list</c> reports them.
    /// </summary>
    /// <param name="runRegistry">
    /// The ONE registry instance <c>run_suite</c> writes and every reader reads — passed in rather
    /// than resolved, exactly as every orchestrator receives it, so a resource can never observe a
    /// different set of runs from the tools.
    /// </param>
    /// <param name="explainRun">The instance <c>explain_run</c> itself uses.</param>
    /// <param name="getRunEvents">The instance <c>get_run_events</c> itself uses.</param>
    /// <param name="getRunArtifacts">The instance <c>get_run_artifacts</c> itself uses.</param>
    public static IReadOnlyList<McpServerResource> CreateAll(
        IRunRegistry runRegistry,
        ExplainRunOrchestrator explainRun,
        GetRunEventsOrchestrator getRunEvents,
        GetRunArtifactsOrchestrator getRunArtifacts)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        ArgumentNullException.ThrowIfNull(explainRun);
        ArgumentNullException.ThrowIfNull(getRunEvents);
        ArgumentNullException.ThrowIfNull(getRunArtifacts);

        return
        [
            CreateVerdict(runRegistry, explainRun),
            CreateEvents(getRunEvents),
            CreateLogs(getRunArtifacts),
        ];
    }

    private static McpServerResource CreateVerdict(
        IRunRegistry runRegistry, ExplainRunOrchestrator explainRun) =>
        McpServerResource.Create(
            (string runId, CancellationToken cancellationToken) =>
                ReadVerdictAsync(runRegistry, explainRun, runId, cancellationToken),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.RunVerdictTemplate,
                Name = "vouchfx run verdict",
                Description =
                    "The same diagnosis explain_run returns for this run, as a cacheable document: the "
                    + "verdict and what that CATEGORY means (pass / a genuine defect / an "
                    + "infrastructure problem that implies no test defect / inconclusive), the notable "
                    + "steps with their retry attempt timelines, and each one's structured reason "
                    + "{ kind, hint }. Response-string vocabulary (Pass / Fail / EnvironmentError / "
                    + "Inconclusive), never the engine's wire tokens. Reads and diagnoses an existing "
                    + "events file; never re-runs anything.",
                MimeType = ResourceJson.MimeType,
            });

    private static McpServerResource CreateEvents(GetRunEventsOrchestrator getRunEvents) =>
        McpServerResource.Create(
            (string runId, CancellationToken cancellationToken) =>
                ReadEventsAsync(getRunEvents, runId, cancellationToken),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.RunEventsTemplate,
                Name = "vouchfx run events (first page)",
                Description =
                    "The FIRST page of this run's raw JSON Lines event stream — the same payload "
                    + "get_run_events returns with no filters and its default limit, including its "
                    + "nextCursor and truncated flags. Events are relayed as the engine wrote them, so "
                    + "verdict fields here read PASS/FAIL/ENV_ERROR/INCONCLUSIVE (wire tokens), not the "
                    + "response strings explain_run uses. To page beyond the first page, or to filter "
                    + "by event type or step, call the get_run_events tool — a URI carries no cursor.",
                MimeType = ResourceJson.MimeType,
            });

    private static McpServerResource CreateLogs(GetRunArtifactsOrchestrator getRunArtifacts) =>
        McpServerResource.Create(
            (string runId, string container, CancellationToken cancellationToken) =>
                ReadLogsAsync(getRunArtifacts, runId, container, cancellationToken),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.RunLogsTemplate,
                Name = "vouchfx run container logs",
                Description =
                    "This run's container-log inventory for the named container — the same 'logs' "
                    + "section get_run_artifacts returns. IMPORTANT: this build has no container log "
                    + "access at all (no engine flag exposes it and this server never talks to a "
                    + "container runtime), so the logs array is ALWAYS empty, partial is true, and the "
                    + "gaps array names the field, the reason and the upstream ask (U4) that would "
                    + "close it. It is never fabricated into. The container name is validated and "
                    + "echoed back, and currently selects nothing — the argument exists so this URI's "
                    + "contract does not change when log access lands.",
                MimeType = ResourceJson.MimeType,
            });

    private static async Task<string> ReadVerdictAsync(
        IRunRegistry runRegistry,
        ExplainRunOrchestrator explainRun,
        string runId,
        CancellationToken cancellationToken)
    {
        var requestedRunId = ResourceArgumentGuard.Require(runId, "runId");

        // The SAME registry lookup and the SAME VFX-E-1505 wording every run-lifecycle tool uses, so
        // a host reading a tool's refusal and this resource's refusal reads one fact.
        var entry = runRegistry.TryGetRun(requestedRunId)
            ?? throw new McpException(RunIdArgument.DescribeMissingRun(requestedRunId));

        var outcome = await explainRun.ExplainAsync(entry.EventsFilePath, cancellationToken);

        return outcome switch
        {
            ExplainRunOutcome.Diagnosed diagnosed => ResourceJson.Serialise(diagnosed.Diagnosis),

            // Every remaining case carries the orchestrator's own already-composed, already-sanitised
            // message. Forwarded verbatim rather than reworded: the tool and the resource describe
            // the same condition, and two wordings for one condition is the drift this whole file is
            // built to avoid. NoRunToExplain is unreachable from here — that case means "no eventsPath
            // was supplied and nothing has finished", and this handler always supplies one — but it is
            // matched explicitly so the compiler enumerates the union rather than letting it fall into
            // the catch-all.
            ExplainRunOutcome.NoRunToExplain noRun => throw new McpException(noRun.Message),
            ExplainRunOutcome.InvalidPath invalidPath => throw new McpException(invalidPath.Message),
            ExplainRunOutcome.EventsFileNotFound notFound => throw new McpException(notFound.Message),
            ExplainRunOutcome.EventsFileUnreadable unreadable => throw new McpException(unreadable.Message),
            ExplainRunOutcome.NoRecognisableEvents noEvents => throw new McpException(noEvents.Message),
            _ => throw new McpException("Reading this run's verdict produced an unrecognised outcome."),
        };
    }

    private static async Task<string> ReadEventsAsync(
        GetRunEventsOrchestrator getRunEvents, string runId, CancellationToken cancellationToken)
    {
        var requestedRunId = ResourceArgumentGuard.Require(runId, "runId");

        // Filters and cursor deliberately omitted: a URI template has no slot for either, and
        // inventing query-string parameters would make this a second, differently-shaped paging API
        // over the tool that already has one. The description says so, and nextCursor/truncated
        // travel in the body, so a host is never left believing a first page is the whole stream.
        var outcome = await getRunEvents.GetAsync(new GetRunEventsRequest(requestedRunId), cancellationToken);

        return outcome switch
        {
            GetRunEventsOutcome.Paged paged => ResourceJson.Serialise(paged.Result),
            GetRunEventsOutcome.InvalidArgument invalid => throw new McpException(invalid.Message),
            GetRunEventsOutcome.InvalidCursor invalidCursor => throw new McpException(invalidCursor.Message),
            GetRunEventsOutcome.RunNotFound notFound => throw new McpException(notFound.Message),
            GetRunEventsOutcome.InvalidPath invalidPath => throw new McpException(invalidPath.Message),
            GetRunEventsOutcome.EventsFileNotFound missing => throw new McpException(missing.Message),
            GetRunEventsOutcome.EventsFileUnreadable unreadable => throw new McpException(unreadable.Message),
            _ => throw new McpException("Reading this run's events produced an unrecognised outcome."),
        };
    }

    private static async Task<string> ReadLogsAsync(
        GetRunArtifactsOrchestrator getRunArtifacts,
        string runId,
        string container,
        CancellationToken cancellationToken)
    {
        var requestedRunId = ResourceArgumentGuard.Require(runId, "runId");
        var requestedContainer = ResourceArgumentGuard.Require(container, "container");

        var outcome = await getRunArtifacts.GetAsync(
            new GetRunArtifactsRequest(requestedRunId, RunArtifactKind.Logs, requestedContainer),
            cancellationToken);

        return outcome switch
        {
            // Note there is no error case for a swept or unreadable events file, and that is
            // get_run_artifacts' own documented design rather than an omission here: for an artefacts
            // INVENTORY the events file is one input of three, so its absence is reported inside a
            // successful result (partial: true plus a gap) rather than as a failure.
            GetRunArtifactsOutcome.Found found => ResourceJson.Serialise(found.Result),
            GetRunArtifactsOutcome.InvalidArgument invalid => throw new McpException(invalid.Message),
            GetRunArtifactsOutcome.RunNotFound notFound => throw new McpException(notFound.Message),
            GetRunArtifactsOutcome.InvalidPath invalidPath => throw new McpException(invalidPath.Message),
            _ => throw new McpException("Reading this run's log inventory produced an unrecognised outcome."),
        };
    }
}
