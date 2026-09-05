using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-03's <c>get_run_status</c> pipeline: bound the <c>runId</c>, look it up in the run registry,
/// and return the entry. That is the whole tool, and its brevity is the acceptance criterion — AC-001
/// requires the answer to be "sourced from the persisted registry, not a second, divergent status
/// model", and a pipeline with nothing in it cannot diverge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and LOCK-FREE by design</b> (US-S3-04's AC-004, spec §4.6's "read-only tools are
/// safe to call concurrently"). Nothing here touches <see cref="IRunLock"/>, so this tool answers
/// while a run is in flight — which is precisely when a host asks it. <c>RunLockSourceGuardTests</c>
/// holds that structurally: it enumerates every <c>TryAcquire</c> call site in <c>src/</c> and this
/// file is deliberately not among them.
/// </para>
/// <para>
/// <b>The <c>running</c> status is the registry's LAST RECORDED state, not a liveness probe — and
/// this tool deliberately does not turn it into one.</b> A server killed with
/// <c>SIGKILL</c>/<c>TerminateProcess</c> mid-run never writes the completing transition, so its
/// entry says <c>running</c> forever; <see cref="FileRunRegistry"/> documents that at length and
/// there is no reaper. The one signal that could settle it — is the workspace's <c>.lock</c> held? —
/// cannot be read without ACQUIRING the lock (see <see cref="WorkspaceRunLock"/>: a
/// <see cref="FileShare.None"/> handle is the lock, so probing it means taking it, however briefly),
/// and a read-only tool that momentarily takes the run lock could make a concurrent <c>run_suite</c>
/// fail with <c>VFX-E-1501</c> — a read-only tool causing a write-side rejection, which is exactly
/// what §4.6 and the source guard exist to prevent.
/// <para>
/// So the stance is stated rather than smuggled: <c>get_run_status</c> and <c>list_runs</c> report
/// what the registry says, and <c>cancel_run</c> — which is NOT read-only and may legitimately touch
/// the lock — is where a host finds out whether a <c>running</c> entry is real. It answers
/// <c>already_finished</c>, <c>cancelled</c>, <c>VFX-E-1507</c> (a live run held by another process)
/// or <c>VFX-E-1508</c> (residue: nothing is running it) for exactly that question. The tool
/// description and <c>docs/troubleshooting.md</c> both say so.
/// </para>
/// </para>
/// <para>
/// <b>No path is resolved and no file is opened.</b> Unlike <c>explain_run</c>/<c>get_run_events</c>,
/// this tool never reads the events file, so it needs no <c>PathSafetyGuard</c> pass and no workspace
/// — the entry's own <c>eventsFilePath</c> is relayed as recorded, for the caller to hand to a tool
/// that does. That is why this type takes no <see cref="Workspace"/>: it would have nothing to check.
/// </para>
/// </remarks>
public sealed class GetRunStatusOrchestrator
{
    /// <summary>
    /// The tool's own name, taken from the ONE place that owns it — see
    /// <see cref="GetRunEventsOrchestrator"/>'s identical arrangement and its reasoning.
    /// </summary>
    private static readonly string ToolName = Tools.GetRunStatusTool.Name;

    private readonly IRunRegistry _runRegistry;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the ONLY source of this tool's answer. Read, never written.
    /// </param>
    public GetRunStatusOrchestrator(IRunRegistry runRegistry)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        _runRegistry = runRegistry;
    }

    /// <summary>Resolves one run id through the registry.</summary>
    public GetRunStatusOutcome Get(GetRunStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RunIdArgument.Validate(request.RunId, ToolName) is { } argumentError)
        {
            return new GetRunStatusOutcome.InvalidArgument(argumentError);
        }

        var entry = _runRegistry.TryGetRun(request.RunId!);
        return entry is null
            ? new GetRunStatusOutcome.RunNotFound(RunIdArgument.DescribeMissingRun(request.RunId!))
            : new GetRunStatusOutcome.Found(new GetRunStatusResult(SanitiseSpecPathsForEgress(entry)));
    }

    /// <summary>
    /// The entry as it goes on the wire: identical to the registry's own, except that every
    /// <see cref="RunRegistryEntry.SpecPaths"/> element is rendered through
    /// <see cref="TextSanitiser.SanitiseForDisplay"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ONE transformation between the registry and the response, and it is a security
    /// boundary</b> (a security review's MINOR finding). Since US-S3-02 a run's spec paths can arrive
    /// through a GLOB, so their file-name half is third-party-authored: on Linux and macOS a file name
    /// may contain any byte but <c>/</c> and NUL, ESC included. The registry stores those paths
    /// VERBATIM and must keep doing so — the entry is the record of what actually ran, and an escaped
    /// path would not open — so the escaping belongs here, at egress, exactly as
    /// <see cref="SpecRunOutcome.Path"/> does it for <c>run_suite</c>'s own result. The two sites
    /// carry one fact between them: no resolved suite path reaches a host unescaped.
    /// </para>
    /// <para>
    /// <b>AC-001's "not a second, divergent status model" survives this, and deliberately so.</b>
    /// There is still no projection TYPE: this returns the registry's own
    /// <see cref="RunRegistryEntry"/> — the SAME INSTANCE whenever nothing needed escaping, which is
    /// every ordinary path — and otherwise a <c>with</c>-copy differing in exactly one field. A
    /// <c>with</c>-expression cannot drift the way a hand-written projection record can, because it
    /// re-declares nothing: a field added to the entry tomorrow appears in the response with no edit
    /// here. <c>GetRunStatusOrchestratorTests</c> pins both branches, including the reference
    /// identity of the untouched one.
    /// </para>
    /// <para>
    /// <b>Why only <c>specPaths</c>.</b> <c>runId</c> and <c>eventsFilePath</c> are minted by this
    /// server (<c>run-</c> plus hex, and a path composed from it), <c>status</c>/<c>outcome</c> are
    /// closed vocabularies rejected on the way in, the timestamps are numbers, and <c>labels</c> are
    /// refused at both boundaries if any key or value contains a control character
    /// (<see cref="RunLabelRules"/>). <c>specPaths</c> is the only field carrying text this server
    /// neither minted nor character-checked.
    /// </para>
    /// </remarks>
    private static RunRegistryEntry SanitiseSpecPathsForEgress(RunRegistryEntry entry)
    {
        var sanitised = new string[entry.SpecPaths.Count];
        var changed = false;

        for (var i = 0; i < entry.SpecPaths.Count; i++)
        {
            var original = entry.SpecPaths[i];
            sanitised[i] = TextSanitiser.SanitiseForDisplay(original);

            // Reference comparison, not equality: SanitiseForDisplay returns the ARGUMENT itself when
            // it changed nothing (it only allocates once it meets a character it must escape), so this
            // is the cheapest possible "did anything need escaping" test and needs no second scan.
            changed |= !ReferenceEquals(sanitised[i], original);
        }

        return changed ? entry with { SpecPaths = sanitised } : entry;
    }
}

/// <summary>
/// The <c>runId</c> argument's bound and its "no such run" message, written once for the three
/// run-lifecycle tools that take one.
/// </summary>
/// <remarks>
/// <b>Shared so the three cannot disagree about the same fact.</b> <c>get_run_status</c>,
/// <c>cancel_run</c> and <c>get_run_events</c> all answer <c>VFX-E-1505</c> for an unknown run id,
/// and that code's catalogue entry explicitly anticipates the sharing ("US-S3-03's
/// get_run_status/cancel_run take a runId too and share this code — the condition and the remedy are
/// identical"). Three separately-worded messages for one catalogued condition would make the
/// catalogue page's summary true of none of them.
/// </remarks>
internal static class RunIdArgument
{
    /// <summary>Returns the refusal message, or <see langword="null"/> when the argument is acceptable.</summary>
    public static string? Validate(string? runId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return $"{toolName} requires 'runId' — the id run_suite returned (as 'runId') for the run "
                   + "you mean. Call list_runs to see the runs this server knows about.";
        }

        return runId.Length > RunLifecycleLimits.MaxRunIdChars
            ? $"{toolName}'s 'runId' must be at most {RunLifecycleLimits.MaxRunIdChars} characters."
            : null;
    }

    /// <summary>
    /// The <c>VFX-E-1505</c> message every run-lifecycle tool uses — deliberately the same wording
    /// <c>get_run_events</c> already ships, so a host reading two tools' refusals reads one fact.
    /// </summary>
    /// <remarks>
    /// The id is echoed through <see cref="VfxCode.SanitiseForEcho"/>: it is caller-supplied text on
    /// its way into a message, and this tool never verified it is one of the ids this server minted
    /// (it is not in the registry — that is the whole point of this message).
    /// </remarks>
    public static string DescribeMissingRun(string runId) =>
        $"No run with id '{VfxCode.SanitiseForEcho(runId)}' is in the run registry. The registry spans "
        + "server restarts when the server was launched with --workspace, and is session-scoped "
        + "otherwise; call run_suite first, or call list_runs to see the runs this server knows about.";
}
