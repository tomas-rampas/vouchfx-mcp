using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — get_run_status / cancel_run / list_runs models (Sprint 3 / US-S3-03; spec §5.8,
// §4.5).
//
// Spec §5.8 fixes all three shapes:
//
//   interface GetRunStatusInput  { runId: string; }
//   interface GetRunStatusOutput { meta: ToolMeta; run: RunSummary; }
//
//   interface CancelRunInput  { runId: string; reason?: string; }
//   interface CancelRunOutput { meta: ToolMeta; runId: string; status: "cancelled" | "already_finished"; }
//
//   interface ListRunsInput  { limit?: number; cursor?: string; label?: string; since?: string; }
//   interface ListRunsOutput { meta: ToolMeta; runs: Pick<RunSummary,
//                                "runId"|"status"|"outcome"|"startedAt"|"finishedAt">[];
//                              nextCursor?: string; }
//
// `meta` is NOT a field on any payload here: StructuredToolResult.Success stamps it through the one
// choke point every tool uses, and a payload carrying its own top-level `meta` is REJECTED there.
//
// ---------------------------------------------------------------------------------------------
// What `run` is, and why it is the registry entry itself
// ---------------------------------------------------------------------------------------------
//
// US-S3-03's AC-001 requires get_run_status to be "sourced from the persisted registry — NOT A
// SECOND, DIVERGENT STATUS MODEL". The strongest possible reading of that is the one taken here:
// there is no projection type at all. `run` IS RunRegistryEntry, serialised through the
// JsonPropertyName attributes it has carried since US-S3-01 — which are already spec §5.8's own
// names (`runId`, `status`, `outcome`, `startedAt`, `finishedAt`). A projection record would be a
// second declaration of the same fields, free to drift by exactly the amount a review would have to
// notice; a direct serialisation cannot drift because there is nothing to drift from.
//
// Where that lands relative to spec §5.7's full `RunSummary`, stated rather than glossed:
//
//   * PRESENT and identical: runId, status, outcome, startedAt, finishedAt.
//   * PRESENT and additive: specPaths, eventsFilePath, labels. §5.7's RunSummary has no `labels`
//     field, but list_runs' own `label` FILTER (§5.8) is unusable if a host cannot see what a run's
//     labels are, and eventsFilePath is what makes the get_run_status → get_run_events hand-off
//     in-band. Both are already in the registry; withholding them would be a deliberate omission.
//   * ABSENT: §5.7's `specs[]` (per-suite steps), `environment`, and `artifacts`. None of them is in
//     the registry, and none COULD be without copying event-stream content into a persistent
//     metadata store — which IRunRegistry's own remarks forbid outright (plan §2.7 invariant 4).
//     They live in the events file, and the tools that read it are explain_run (summarised) and
//     get_run_events (raw). `specPaths` carries the paths, so a host can still tell what ran.
//
// That divergence is deliberate and is recorded here rather than hidden behind a type that looks
// like §5.7's RunSummary and is not.
//
// ONE transformation exists between the stored entry and the serialised one, and it is not a
// projection: GetRunStatusOrchestrator.SanitiseSpecPathsForEgress escapes non-printable characters in
// `specPaths` (glob-resolved file names are third-party-authored text — a security review's MINOR
// finding). It is a `with`-copy of the entry, applied only when a path actually needed escaping, so
// the "nothing to drift from" property above is unchanged: no field is re-declared anywhere, and a
// field added to RunRegistryEntry tomorrow appears in the response with no edit to this file.
//
// ---------------------------------------------------------------------------------------------
// list_runs' item shape is a PROJECTION, and that one is spec-mandated
// ---------------------------------------------------------------------------------------------
//
// §5.8 types the list items as `Pick<RunSummary, "runId"|"status"|"outcome"|"startedAt"|"finishedAt">`
// — five fields, exactly. So list_runs does NOT serialise the entry: a page of 200 entries carrying
// spec paths and labels would be both off-contract and an unbounded response (spec paths and labels
// are caller-supplied and only per-entry bounded). RunListItem below is that Pick, and the one place
// the two shapes are allowed to differ.

/// <summary>Bounds shared by the three run-lifecycle tools' arguments.</summary>
/// <remarks>
/// Gathered here rather than restated per tool so <c>get_run_status</c>, <c>cancel_run</c> and
/// <c>list_runs</c> cannot bound the same argument differently — the same reasoning
/// <see cref="RunLabelRules"/> records for the label rules two layers enforce.
/// </remarks>
public static class RunLifecycleLimits
{
    /// <summary>
    /// Longest <c>runId</c> any of the three tools will even look at, matching
    /// <see cref="GetRunEventsOrchestrator.MaxFilterValueChars"/>.
    /// </summary>
    /// <remarks>
    /// A real run id is 36 characters (<c>run-</c> plus 32 hex). This bound is not a format check —
    /// it is the "do not carry a multi-megabyte argument into a message" guard every caller-supplied
    /// string in this server has, and it is deliberately the same figure <c>get_run_events</c> uses
    /// for the identical argument.
    /// </remarks>
    public const int MaxRunIdChars = 2_000;

    /// <summary>Longest <c>reason</c> <c>cancel_run</c> accepts.</summary>
    /// <remarks>
    /// Bounded even though the reason is never persisted, never echoed to any caller, and never put
    /// on the wire (see <see cref="IRunCancellationScope.CancellationReason"/>): it is held in memory
    /// for the run's lifetime, and every caller-supplied string this server retains is bounded at its
    /// boundary rather than trusted to be reasonable.
    /// </remarks>
    public const int MaxReasonChars = 2_000;

    /// <summary>Longest <c>label</c> filter <c>list_runs</c> accepts — a key, optionally <c>=</c> and a value.</summary>
    /// <remarks>
    /// Sized from what it is compared against: <see cref="RunLabelRules.MaxKeyLength"/> plus
    /// <see cref="RunLabelRules.MaxValueLength"/> plus the separator. A longer filter cannot match any
    /// label this server would have accepted at <c>run_suite</c> time, so accepting one would only
    /// mean scanning the whole registry to answer "no".
    /// </remarks>
    public const int MaxLabelFilterChars = RunLabelRules.MaxKeyLength + RunLabelRules.MaxValueLength + 1;

    /// <summary>The character separating a label filter's key from its value.</summary>
    public const char LabelFilterSeparator = '=';
}

/// <summary><c>get_run_status</c>'s arguments, as the caller sent them — unvalidated.</summary>
/// <param name="RunId">The run to report on, as recorded in the run registry.</param>
public sealed record GetRunStatusRequest(string? RunId);

/// <summary>Spec §5.8's <c>GetRunStatusOutput</c>, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="Run">
/// The registry's own entry for the run — see this file's header for why this is the entry itself
/// rather than a projection of it, for exactly how it relates to spec §5.7's <c>RunSummary</c>, and
/// for the single field (<c>specPaths</c>) that is escaped on the way out.
/// </param>
public sealed record GetRunStatusResult(
    [property: JsonPropertyName("run")] RunRegistryEntry Run);

/// <summary>What one <c>get_run_status</c> call produced — a closed union, mirroring <see cref="GetRunEventsOutcome"/>.</summary>
public abstract record GetRunStatusOutcome
{
    private GetRunStatusOutcome()
    {
    }

    /// <summary>The run's current state, straight from the registry.</summary>
    public sealed record Found(GetRunStatusResult Result) : GetRunStatusOutcome;

    /// <summary><c>runId</c> was missing, blank, or over <see cref="RunLifecycleLimits.MaxRunIdChars"/> — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : GetRunStatusOutcome;

    /// <summary>No run with that id is in the registry — <c>VFX-E-1505</c>.</summary>
    public sealed record RunNotFound(string Message) : GetRunStatusOutcome;
}

/// <summary><c>cancel_run</c>'s arguments, as the caller sent them — unvalidated.</summary>
/// <param name="RunId">The run to stop.</param>
/// <param name="Reason">
/// Free-form context for the cancellation. Never persisted, never returned, never relayed — see
/// <see cref="IRunCancellationScope.CancellationReason"/>.
/// </param>
public sealed record CancelRunRequest(string? RunId, string? Reason = null);

/// <summary>Spec §5.8's <c>CancelRunOutput</c>, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="RunId">The run this answer is about, echoed back as spec §5.8 requires.</param>
/// <param name="Status">One of <see cref="CancelRunStatus"/>' two literals — never free text.</param>
public sealed record CancelRunResult(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Spec §5.8's <c>CancelRunOutput.status</c> union, as string constants.</summary>
public static class CancelRunStatus
{
    /// <summary>
    /// The run was in flight in this server process and has been asked to stop, through the same
    /// graceful-stop mechanism <c>run_suite</c> already uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Asked to stop", not "has stopped" — and <c>cancel_run</c> does not wait.</b> The engine is
    /// given <c>VouchfxCliSuiteRunner</c>'s full grace period to shut down cleanly before it is
    /// killed, so blocking this call until the run is genuinely terminal would hold an MCP request
    /// open for tens of seconds — the same reason <see cref="IRunLock"/> answers immediately rather
    /// than queueing. The run's own <c>run_suite</c> call returns its (Inconclusive) result as it
    /// always has. Spec §5.8's union has no third literal to express "stopping", so the tool
    /// description and the docs state this in words instead of inventing one.
    /// </para>
    /// <para>
    /// <b>A host observing the terminal state polls until the status is TERMINAL — <c>completed</c>
    /// OR <c>cancelled</c> — never until it reads <c>cancelled</c> specifically.</b> That narrower
    /// wording was a non-terminating instruction (a gatekeeper review's MAJOR finding), because two
    /// reachable windows end a cancelled-from-here run at <c>completed</c> or leave it at
    /// <c>running</c> forever:
    /// <list type="bullet">
    /// <item><description>
    /// <b>The read/write race.</b> <c>RunSuiteOrchestrator.TerminalStatusFor</c> reads
    /// <see cref="IRunCancellationScope.CancellationRequested"/> and then writes the transition,
    /// while the scope stays published until the run body unwinds. A cancellation delivered AFTER
    /// that read and BEFORE the scope's disposal is signalled honestly — this tool answers
    /// <c>cancelled</c>, and the run genuinely stops — but the completing write has already been
    /// composed with <see cref="RunRegistryStatus.Completed"/>. The window is microseconds wide and
    /// the fix is this guidance, not a re-ordering: making the write depend on a later read would
    /// mean holding the cancellation scope open across the registry write, which is precisely the
    /// use-after-dispose window <c>InProcessRunCancellations</c>' per-entry gate exists to close.
    /// </description></item>
    /// <item><description>
    /// <b>A failed completing write.</b> <c>RunSuiteOrchestrator.ReportCompletionNotRecorded</c>
    /// announces on stderr that the verdict was returned but the registry write failed; the entry
    /// then stays <see cref="RunRegistryStatus.Running"/> with no reaper to clear it. A poll waiting
    /// for any particular terminal token never finishes. <c>cancel_run</c> called again on such an
    /// entry is what settles it (<c>VFX-E-1507</c>/<c>VFX-E-1508</c>).
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The run had already reached a terminal status, so there was nothing to cancel. <b>Not an
    /// error</b> — US-S3-03's AC and its own Gherkin both require <c>isError: false</c> here: asking
    /// to cancel a run that has finished is a race a polling host will lose routinely, and the honest
    /// answer is what happened, not a failure.
    /// </summary>
    public const string AlreadyFinished = "already_finished";
}

/// <summary>What one <c>cancel_run</c> call produced — a closed union.</summary>
public abstract record CancelRunOutcome
{
    private CancelRunOutcome()
    {
    }

    /// <summary>An answer was reached: the run was signalled, or was already over.</summary>
    public sealed record Answered(CancelRunResult Result) : CancelRunOutcome;

    /// <summary><c>runId</c>/<c>reason</c> was missing, blank, or over its bound — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : CancelRunOutcome;

    /// <summary>No run with that id is in the registry — <c>VFX-E-1505</c>.</summary>
    public sealed record RunNotFound(string Message) : CancelRunOutcome;

    /// <summary>
    /// The run is recorded as in flight but THIS server process is not the one running it, so there
    /// is no channel to signal it through — <c>VFX-E-1507</c>.
    /// </summary>
    /// <remarks>
    /// The <paramref name="Message"/> distinguishes the three shapes this covers rather than
    /// asserting the first of them; see <c>CancelRunOrchestrator.DescribeUncancellableRun</c> for why
    /// a held workspace lock does not, on its own, mean another PROCESS is running this RUN.
    /// </remarks>
    public sealed record NotCancellable(string Message) : CancelRunOutcome;

    /// <summary>
    /// The run is recorded as in flight but the workspace's run lock is free, so no process is
    /// running it: the entry is residue — from a server killed mid-run, or from a run whose
    /// completing registry write failed — <c>VFX-E-1508</c>.
    /// </summary>
    public sealed record StaleEntry(string Message) : CancelRunOutcome;
}

/// <summary><c>list_runs</c>' arguments, as the caller sent them — unvalidated.</summary>
/// <param name="Limit">
/// Maximum runs to return. <see langword="null"/> means spec §4.5's default of 200; above its maximum
/// of 2000 is refused, never silently clamped.
/// </param>
/// <param name="Cursor">A <c>nextCursor</c> from a previous call, or <see langword="null"/> for the first page.</param>
/// <param name="Label">
/// A label filter: either <c>key=value</c> (both matched exactly, ordinally) or a bare <c>key</c>
/// (any value). See <see cref="ListRunsOrchestrator"/> for why the form is adjudicated here rather
/// than taken from spec §5.8, which types this only as <c>string</c>.
/// </param>
/// <param name="Since">
/// An ISO-8601 timestamp; only runs whose <c>startedAt</c> is at or after it are returned.
/// </param>
public sealed record ListRunsRequest(
    int? Limit = null,
    string? Cursor = null,
    string? Label = null,
    string? Since = null);

/// <summary>
/// Spec §5.8's <c>Pick&lt;RunSummary, "runId"|"status"|"outcome"|"startedAt"|"finishedAt"&gt;</c> —
/// the list item, and the ONE place this story projects rather than serialising the registry entry.
/// </summary>
/// <remarks>
/// See this file's header: the Pick is spec-mandated, and it also keeps a 200-entry page bounded,
/// since <c>specPaths</c> and <c>labels</c> are caller-supplied and only per-entry bounded. The
/// property names are the entry's own, so an item and a <c>get_run_status</c> result never disagree
/// about what a field is called.
/// </remarks>
public sealed record RunListItem(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("finishedAt")] DateTimeOffset? FinishedAtUtc)
{
    /// <summary>Projects one registry entry onto the five fields spec §5.8 lists.</summary>
    public static RunListItem From(RunRegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new RunListItem(
            entry.RunId, entry.Status, entry.Outcome, entry.StartedAtUtc, entry.FinishedAtUtc);
    }
}

/// <summary>Spec §5.8's <c>ListRunsOutput</c>, minus the <c>meta</c> the result envelope stamps on.</summary>
/// <param name="Runs">One page of runs, <b>newest first</b> — <see cref="IRunRegistry.ListRuns"/>' own order.</param>
/// <param name="NextCursor">
/// The token to pass as <c>cursor</c> for the next page, or <see langword="null"/> — omitted from the
/// JSON entirely — when this page is the last one. Present ONLY when a further matching run genuinely
/// exists: <see cref="ListRunsOrchestrator"/> looks one match ahead before minting it, so a host that
/// follows <c>nextCursor</c> never receives an empty page as its stopping condition.
/// </param>
public sealed record ListRunsResult(
    [property: JsonPropertyName("runs")] IReadOnlyList<RunListItem> Runs,
    [property: JsonPropertyName("nextCursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor);

/// <summary>What one <c>list_runs</c> call produced — a closed union.</summary>
public abstract record ListRunsOutcome
{
    private ListRunsOutcome()
    {
    }

    /// <summary>The page was produced.</summary>
    public sealed record Paged(ListRunsResult Result) : ListRunsOutcome;

    /// <summary>An argument was out of range or unparseable — <c>VFX-E-1006</c>.</summary>
    public sealed record InvalidArgument(string Message) : ListRunsOutcome;

    /// <summary>The <c>cursor</c> could not be verified — <c>VFX-E-1506</c>.</summary>
    public sealed record InvalidCursor(string Message) : ListRunsOutcome;
}
