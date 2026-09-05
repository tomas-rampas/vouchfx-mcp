using System.Globalization;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-03's <c>list_runs</c> pipeline: read the run registry, apply the caller's
/// <c>label</c>/<c>since</c> filters, and return ONE page of run summaries newest-first plus an
/// opaque continuation cursor. Purely read + filter + page — it never re-runs anything, never opens
/// an events file, and never takes the run lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cursor is <see cref="OpaqueCursor"/>, reused verbatim under its own scope</b> — the
/// sprint's exit checklist requires "one cursor implementation, verified by a shared unit-test
/// fixture, not two", and <c>OpaqueCursorContract</c> is driven from this tool's tests as well as
/// <c>get_run_events</c>'. Nothing about cursors is re-implemented here; this type supplies a scope
/// constant, a filter binding, and a position, which is the whole of what that type asks of a caller.
/// </para>
/// <para>
/// <b>The position is a TIMESTAMP, not an index, and that choice is load-bearing.</b> The registry is
/// re-scanned on every page (<see cref="IRunRegistry.ListRuns"/> — a directory walk plus a parse per
/// entry for the file-backed implementation) and runs can be ADDED between two pages of one walk.
/// Every addition lands at the HEAD, because the list is newest-first and
/// <see cref="RunRegistryEntry.StartedAtUtc"/> is strictly increasing (see
/// <see cref="IRunRegistry.ListRuns"/>' own remarks) — so an INDEX position would shift by exactly
/// the number of runs started mid-walk and the next page would re-serve that many rows the caller
/// already has. A timestamp position is immune: "resume at the first run started strictly before
/// <c>T</c>" names the same boundary however many newer runs appear in front of it. The cost is
/// symmetric and smaller: runs started mid-walk are never seen by that walk at all, which is the
/// correct behaviour for a newest-first listing whose caller asked for a snapshot.
/// <para>
/// The residual is a TIE — two runs sharing an exact <c>startedAt</c> tick, with the page boundary
/// falling between them: the second is silently dropped from the walk, since the next page resumes
/// STRICTLY before the boundary tick. It is documented rather than engineered around because of what
/// makes a tie unreachable in the configurations this server actually runs in, and the argument is
/// stated carefully because an earlier version of this comment gave the WRONG one (a gatekeeper
/// review's MINOR finding — it claimed the workspace lock hands out distinct timestamps, which is not
/// something a mutex does):
/// <list type="bullet">
/// <item><description>
/// <b>Within one registry instance, ties are impossible by construction.</b>
/// <see cref="RunRegistryTimestamps.NextStartedAt"/> forces each stamp STRICTLY above the last,
/// adding a tick when the clock has not visibly moved; <see cref="FileRunRegistry"/> seeds that floor
/// from the newest entry already on disk, so a restart does not reset it.
/// </description></item>
/// <item><description>
/// <b>Across two processes sharing a workspace, the lock's contribution is SERIALISATION, not
/// distinctness.</b> US-S3-04's claim is held for the whole of a run, and
/// <see cref="IRunRegistry.StartRun"/> happens under it — so two <c>StartRun</c> calls against one
/// output directory are separated by at least one complete run (engine spawn, execution, teardown),
/// which is many orders of magnitude beyond the ~15 ms system-timer resolution that produces equal
/// stamps in the first place. That is why a tie does not arise, and it says nothing about the lock
/// generating distinct values.
/// </description></item>
/// <item><description>
/// <b>Without a workspace there is no lock — and no sharing either</b>, because the registry is then
/// <see cref="InMemoryRunRegistry"/> and process-scoped, which is the first bullet again.
/// </description></item>
/// </list>
/// <see cref="IRunRegistry.ListRuns"/>' own remarks already acknowledge the honest residual: two
/// server processes running CONCURRENTLY degrade to whatever the shared system clock says. That is
/// the state in which a tie is theoretically reachable, and the drop-one-row consequence is pinned by
/// <c>ListRunsOrchestratorTests.BuildPage_TwoRunsSharingAStartedAtTick_...</c> rather than left to be
/// discovered. (The position stays a <see cref="long"/> regardless: widening
/// <see cref="OpaqueCursor"/> to carry a composite key would change the SHARED cursor's format —
/// <c>get_run_events</c>' included — to close a window that requires two concurrent servers on one
/// workspace and a page boundary landing inside a 15 ms window.)
/// </para>
/// </para>
/// <para>
/// <b>Read-only and LOCK-FREE</b> (US-S3-04's AC-004, spec §4.6). Nothing here touches
/// <see cref="IRunLock"/>, so a host may list runs while one is in flight — <c>RunLockSourceGuardTests</c>
/// holds that structurally. A <c>running</c> entry is reported exactly as the registry records it,
/// including a phantom one left by a hard-killed server; see
/// <see cref="GetRunStatusOrchestrator"/>'s remarks for the full stance and for why
/// <c>cancel_run</c>, not this tool, is where a host establishes whether such an entry is real.
/// </para>
/// <para>
/// <b>What a page COSTS, MEASURED rather than estimated</b> (probe run 2026-09-05, Windows 11,
/// .NET 8, warm NTFS cache, one <see cref="FileRunRegistry"/> per read):
/// <list type="bullet">
/// <item><description>
/// <b>1,000 runs</b> — one <see cref="IRunRegistry.ListRuns"/> scan 138&#160;ms; one page 132&#160;ms;
/// a full walk at <c>limit: 200</c> <b>745&#160;ms</b> over 5 pages. Unremarkable, and the case a
/// developer workspace is actually in.
/// </description></item>
/// <item><description>
/// <b>10,000 runs</b> (the cap, exactly) — one scan 1,384&#160;ms; one page 1,404&#160;ms; a full
/// walk <b>70.1&#160;s</b> over 50 pages. That is the shape to notice: every page re-scans the WHOLE
/// registry, so a walk costs pages × scan and grows quadratically in the run count. It is the same
/// re-read-per-page trade <c>GetRunEventsOrchestrator</c> records for its own pager, reached from the
/// other direction — there a page re-reads one file, here it re-walks one directory.
/// </description></item>
/// <item><description>
/// <b>12,000 runs</b> (past the cap) — one scan still returns exactly <b>10,000</b>, and the full
/// walk still returns <b>10,000 rows in 50 pages</b>. <b>2,000 runs are simply invisible, with
/// nothing in the response saying so.</b>
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>What the registry's own scan cap means for a large workspace, stated because a host cannot see
/// it.</b> <see cref="FileRunRegistry.MaxRunsScanned"/> bounds one <see cref="IRunRegistry.ListRuns"/>
/// call at 10,000 run directories, applied over the FILESYSTEM's enumeration order and therefore
/// before the newest-first sort. A workspace holding more than that many runs is consequently paged
/// over an arbitrary 10,000-run slice, and — since enumeration order is not guaranteed stable between
/// calls — successive pages need not come from the same slice. Reaching that many runs already means
/// the workspace needs a retention sweep, which no story in this sprint delivers (there is no reaper);
/// the figures above are what such a sweep should be sized against.
/// This tool does not paper over it with a heuristic <c>truncated</c> flag: unlike
/// <c>get_run_events</c>, which is told by its reader that the cap was hit,
/// <see cref="IRunRegistry.ListRuns"/> returns a plain list and "exactly 10,000 entries came back" is
/// indistinguishable from a workspace that genuinely holds 10,000 runs. Guessing would be worse than
/// the documented bound. The honest fix is a reaper plus a reader that reports its own cap, and both
/// belong to whichever story takes retention on.
/// </para>
/// </remarks>
public sealed class ListRunsOrchestrator
{
    /// <summary>Spec §4.5's default page size.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Spec §4.5's maximum page size.</summary>
    public const int MaxLimit = 2_000;

    /// <summary>The tool's own name, from the factory that owns it (see <see cref="GetRunEventsOrchestrator"/>).</summary>
    private static readonly string ToolName = Tools.ListRunsTool.Name;

    private readonly IRunRegistry _runRegistry;

    /// <param name="runRegistry">US-S3-01's run registry — the only source of this tool's answer. Read, never written.</param>
    public ListRunsOrchestrator(IRunRegistry runRegistry)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        _runRegistry = runRegistry;
    }

    /// <summary>Filters, positions and pages the registry's runs.</summary>
    public ListRunsOutcome List(ListRunsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateArguments(request, out var filters, out var limit) is { } argumentError)
        {
            return argumentError;
        }

        // The cursor is verified BEFORE the registry is scanned, mirroring get_run_events: a cursor
        // this server did not issue is a fact about the ARGUMENTS, knowable without any I/O.
        var startBeforeTicks = long.MaxValue;
        if (request.Cursor is not null)
        {
            if (!OpaqueCursor.TryDecode(
                    request.Cursor, CursorScopes.ListRuns, filters.CursorBinding, out var decoded, out var rejection))
            {
                return new ListRunsOutcome.InvalidCursor(OpaqueCursor.DescribeRejection(rejection, ToolName));
            }

            startBeforeTicks = decoded;
        }

        return new ListRunsOutcome.Paged(BuildPage(_runRegistry.ListRuns(), filters, limit, startBeforeTicks));
    }

    /// <summary>The caller's filters, normalised once — and the cursor binding derived from them.</summary>
    /// <remarks>
    /// <see langword="internal"/>, with <see cref="ValidateArguments"/> and <see cref="BuildPage"/>,
    /// purely so a test can drive the page builder with the SAME filters the production path produces
    /// rather than an imitation of them — the arrangement <see cref="GetRunEventsOrchestrator"/> uses
    /// for identical reasons.
    /// </remarks>
    /// <param name="LabelKey">The label key to require, or <see langword="null"/> for no label filter.</param>
    /// <param name="LabelValue">
    /// The exact value that key must carry, or <see langword="null"/> when the caller named a bare key
    /// and any value matches.
    /// </param>
    /// <param name="SinceUtc">The inclusive lower bound on <c>startedAt</c>, or <see langword="null"/>.</param>
    internal sealed record Filters(
        string? LabelKey, string? LabelValue, DateTimeOffset? SinceUtc, string CursorBinding)
    {
        /// <summary>Whether <paramref name="entry"/> survives both filters.</summary>
        public bool Matches(RunRegistryEntry entry)
        {
            if (SinceUtc is { } since && entry.StartedAtUtc < since)
            {
                return false;
            }

            if (LabelKey is not { } key)
            {
                return true;
            }

            // Ordinal on both halves: labels are host-supplied correlation keys matched by a machine,
            // stored verbatim and never normalised (RunRegistryEntry.Labels), so a culture-aware or
            // case-insensitive comparison here would match labels the host considers distinct.
            if (!entry.Labels.TryGetValue(key, out var value))
            {
                return false;
            }

            return LabelValue is null || string.Equals(value, LabelValue, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Applies every argument bound. Returns the refusal, or <see langword="null"/> with
    /// <paramref name="filters"/> and <paramref name="limit"/> set.
    /// </summary>
    /// <remarks>
    /// <b>The <c>label</c> filter's FORM is adjudicated here, because spec §5.8 does not state one</b>
    /// — it types the argument as a bare <c>string</c> while §5.7 makes labels a
    /// <c>Record&lt;string,string&gt;</c>, so a single string has to mean something and the spec does
    /// not say what. Two forms are accepted, both exact and both ordinal:
    /// <list type="bullet">
    /// <item><description><c>key=value</c> — runs carrying that key with exactly that value.</description></item>
    /// <item><description><c>key</c> — runs carrying that key with ANY value.</description></item>
    /// </list>
    /// The bare-key form exists because §5.7's own example labels
    /// (<c>{ "trigger": "agent:author", "iteration": "3" }</c>) make "every run this agent triggered,
    /// whatever the iteration" the obvious query, and it would otherwise be unexpressible. Nothing
    /// wildcards or substring-matches: a filter that silently widened would return runs a host did not
    /// ask about, and a label is matched by a machine rather than searched by a human. A filter whose
    /// key half is empty (a leading <c>=</c>) is refused rather than matched against a key
    /// <see cref="RunLabelRules"/> would never have accepted at <c>run_suite</c> time.
    /// </remarks>
    internal static ListRunsOutcome.InvalidArgument? ValidateArguments(
        ListRunsRequest request, out Filters filters, out int limit)
    {
        filters = null!;
        limit = DefaultLimit;

        if (request.Limit is { } requestedLimit && (requestedLimit < 1 || requestedLimit > MaxLimit))
        {
            return new ListRunsOutcome.InvalidArgument(
                $"{ToolName}'s 'limit' must be between 1 and {MaxLimit} (spec §4.5); the default is "
                + $"{DefaultLimit}. Got: {requestedLimit}. It is refused rather than clamped so a short "
                + "page is never mistaken for the end of the list.");
        }

        limit = request.Limit ?? DefaultLimit;

        string? labelKey = null;
        string? labelValue = null;
        if (request.Label is { } rawLabel)
        {
            if (rawLabel.Length > RunLifecycleLimits.MaxLabelFilterChars)
            {
                return new ListRunsOutcome.InvalidArgument(
                    $"{ToolName}'s 'label' must be at most {RunLifecycleLimits.MaxLabelFilterChars} "
                    + "characters — longer than any label run_suite would have accepted, so it could "
                    + "not match anything.");
            }

            var separator = rawLabel.IndexOf(RunLifecycleLimits.LabelFilterSeparator, StringComparison.Ordinal);
            labelKey = separator < 0 ? rawLabel : rawLabel[..separator];
            labelValue = separator < 0 ? null : rawLabel[(separator + 1)..];

            if (string.IsNullOrWhiteSpace(labelKey))
            {
                return new ListRunsOutcome.InvalidArgument(
                    $"{ToolName}'s 'label' must name a label key: either 'key=value' to match a key and "
                    + "an exact value, or just 'key' to match any run carrying that key. Omit 'label' "
                    + "to list every run.");
            }
        }

        DateTimeOffset? sinceUtc = null;
        if (request.Since is { } rawSince)
        {
            // Round-trip/ISO-8601 only, invariant culture, and NOT AssumeLocal: a bare
            // "2026-09-05T10:00:00" means different instants on the host and on the agent's machine,
            // and silently resolving it against the SERVER's zone would filter by a boundary the
            // caller never named. AssumeUniversal makes the documented rule ("UTC unless the value
            // carries its own offset") the one that actually runs.
            if (!DateTimeOffset.TryParse(
                    rawSince,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedSince))
            {
                return new ListRunsOutcome.InvalidArgument(
                    $"{ToolName}'s 'since' must be an ISO-8601 timestamp (for example "
                    + "'2026-09-05T10:00:00Z'). A value with no offset is read as UTC. Got an "
                    + "unparseable value; omit 'since' to list every run.");
            }

            sinceUtc = parsedSince;
        }

        filters = new Filters(labelKey, labelValue, sinceUtc, ComposeBinding(labelKey, labelValue, sinceUtc));
        return null;
    }

    /// <summary>
    /// Builds the cursor's filter binding from the arguments that decide WHICH runs the page walk
    /// enumerates.
    /// </summary>
    /// <remarks>
    /// <b><c>since</c> is bound as its PARSED instant, not as the caller's text</b>, so
    /// <c>2026-01-01T00:00:00Z</c> and <c>2026-01-01T01:00:00+01:00</c> — which select an identical
    /// set — share a cursor instead of refusing each other's. <b>The label's key and value are bound
    /// SEPARATELY</b>, so <c>a=b</c> and a hypothetical key literally named <c>a=b</c> cannot collide.
    /// <b><c>limit</c> is deliberately absent</b>: changing the page size mid-walk is legitimate (see
    /// <see cref="OpaqueCursor.ComposeBinding"/>).
    /// <para>
    /// <see langword="internal"/> for the same reason <see cref="ValidateArguments"/> and
    /// <see cref="BuildPage"/> are: the cursor-contract cases in <c>ListRunsOrchestratorTests</c>
    /// compose their bindings through THIS method rather than through a hand-copied replica of it. The
    /// replica was real, and the risk it carried was real too — a binding assembled in the test from
    /// the same three parts in the same order would keep passing after this method changed the order
    /// or added a part (a gatekeeper review's MINOR finding).
    /// </para>
    /// </remarks>
    internal static string ComposeBinding(string? labelKey, string? labelValue, DateTimeOffset? sinceUtc) =>
        OpaqueCursor.ComposeBinding(
            labelKey,
            labelValue,
            sinceUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Takes the newest-first registry snapshot, skips past <paramref name="startBeforeTicks"/>, and
    /// keeps matching runs until <paramref name="limit"/> — then looks ONE further match ahead to
    /// decide whether a cursor is owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Strictly BEFORE, not at-or-before.</b> The position is the <c>startedAt</c> of the last run
    /// on the previous page, so the boundary run itself must not be served twice.
    /// </para>
    /// <para>
    /// <b>The look-ahead is what makes <c>nextCursor</c> mean something</b> — identical to
    /// <see cref="GetRunEventsOrchestrator.BuildPage"/>'s: present ⇒ at least one further matching run
    /// exists, so a host following the cursor never gets an empty page as its stopping condition.
    /// </para>
    /// <para>
    /// <b>No byte budget, unlike <c>get_run_events</c>.</b> A <see cref="RunListItem"/> is five scalar
    /// fields — an id, two short tokens and two timestamps — with no caller-supplied text in it at
    /// all, so the full page of 2000 is around 300&#160;KB of fixed-shape JSON rather than something
    /// whose size depends on what a suite happened to emit. That is precisely why spec §5.8's
    /// <c>Pick</c> excludes <c>specPaths</c> and <c>labels</c>, and why honouring the <c>Pick</c>
    /// removes the need for the measured budget the raw-event pager carries.
    /// </para>
    /// </remarks>
    internal static ListRunsResult BuildPage(
        IReadOnlyList<RunRegistryEntry> newestFirst, Filters filters, int limit, long startBeforeTicks)
    {
        var page = new List<RunListItem>(Math.Min(limit, 64));
        long? nextPosition = null;

        foreach (var entry in newestFirst)
        {
            if (entry.StartedAtUtc.UtcTicks >= startBeforeTicks || !filters.Matches(entry))
            {
                continue;
            }

            if (page.Count >= limit)
            {
                // This match is the proof a further page exists. The cursor carries the LAST RETURNED
                // run's position rather than this one's, so the next page resumes strictly before the
                // boundary the caller has already seen — which stays correct even if this particular
                // run is gone by then.
                nextPosition = page[^1].StartedAtUtc.UtcTicks;
                break;
            }

            page.Add(RunListItem.From(entry));
        }

        var nextCursor = nextPosition is { } position
            ? OpaqueCursor.Encode(CursorScopes.ListRuns, filters.CursorBinding, position)
            : null;

        return new ListRunsResult(page, nextCursor);
    }
}
