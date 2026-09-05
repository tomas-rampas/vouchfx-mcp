namespace Vouchfx.Mcp.Run;

/// <summary>
/// The single source of truth for "what runs exist and what state are they in" — US-S3-01's
/// replacement for the session-scoped <c>ILastRunTracker</c> it retires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three write points, exactly as the story requires:</b> <see cref="StartRun"/> at run start,
/// <see cref="RecordStatusTransition"/> on every status change, and — because completion IS a status
/// change — the same method again at completion, with the outcome. There is deliberately no fourth
/// "update" entry point: one writer method means one place where a status is validated, one place
/// where <see cref="RunRegistryEntry.FinishedAtUtc"/> is stamped, and one place a persistent
/// implementation has to make crash-safe.
/// </para>
/// <para>
/// <b>The registry mints the events-file path, not its caller.</b> <see cref="StartRun"/> returns an
/// entry whose <see cref="RunRegistryEntry.EventsFilePath"/> is already decided, because WHERE a
/// run's artefacts live is a property of the storage backend: the file-backed registry puts them
/// beside the run's own metadata under the workspace's <c>outputDir</c> (which is what makes restart
/// survival real rather than temp-directory-accidental), and the in-memory one leaves them in the OS
/// temp directory exactly where they have always been. <c>RunSuiteOrchestrator</c> asking for a run
/// id and then computing its own path would put that decision in two places and let them drift.
/// </para>
/// <para>
/// <b>Metadata only — plan §2.7 invariant 4, re-asserted for a PERSISTENT surface.</b> The entry
/// model (<see cref="RunRegistryEntry"/>) is the complete list of what may be stored: run id,
/// status, outcome, timestamps, spec paths, events-file path, labels. No resolved
/// <c>${secret:…}</c> value, no environment variable, no log line, no events-file or suite CONTENT
/// is ever copied here. That mattered less when the record lived only in process memory for the
/// duration of one session; it matters a great deal now that it is a file on the host's disk that
/// outlives the process, so <c>RealRunRegistryMcpTests</c> asserts it against the registry's actual
/// on-disk bytes rather than against its object model.
/// </para>
/// <para>
/// <b>Thread safety is part of the contract, not an implementation detail.</b> <c>run_suite</c>
/// writes while <c>explain_run</c>/<c>diagnose_run</c> read, potentially on different threads of the
/// same session; every implementation must make both safe, and must never let a reader observe a
/// half-written entry. See each implementation's own remarks for how it achieves that.
/// </para>
/// </remarks>
public interface IRunRegistry
{
    /// <summary>
    /// Mints a run id and an events-file path, records the run as
    /// <see cref="RunRegistryStatus.Running"/>, and returns the entry — the FIRST of the three write
    /// points.
    /// </summary>
    /// <param name="specPaths">
    /// The suite path(s) this run covers. Copied defensively; must contain at least one entry.
    /// </param>
    /// <param name="labels">
    /// Host-supplied labels (spec §5.7), already bounded and validated by the caller —
    /// <see cref="RunRegistryEntry.Labels"/> states what may and may not be in them.
    /// <see langword="null"/> is recorded as an empty map, which is what a <c>run_suite</c> call that
    /// sent no <c>labels</c> produces.
    /// </param>
    /// <returns>The recorded entry, already persisted.</returns>
    RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null);

    /// <summary>
    /// Records a status change against an existing run — the SECOND and THIRD write points (an
    /// intermediate transition, and the terminal one that completes the run).
    /// </summary>
    /// <param name="runId">The run to transition, as returned by <see cref="StartRun"/>.</param>
    /// <param name="status">One of <see cref="RunRegistryStatus"/>'s tokens.</param>
    /// <param name="outcome">
    /// One of <see cref="RunVerdict"/>'s four PascalCase names when the status is terminal;
    /// <see langword="null"/> otherwise. An engine wire token (<c>PASS</c>) or any other string is
    /// rejected — see <see cref="RunRegistryEntry.Outcome"/> for why that boundary is enforced here
    /// rather than trusted.
    /// </param>
    /// <returns>
    /// The updated entry, or <see langword="null"/> when <paramref name="runId"/> is not in the
    /// registry at all. A missing run is NOT an exception: a transition can legitimately arrive for
    /// a run whose entry a host deleted from disk between the start and the completion, and failing
    /// the whole tool call over lost housekeeping would be worse than losing the record.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="status"/> is not a known status; <paramref name="outcome"/> is neither
    /// <see langword="null"/> nor one of <see cref="RunVerdict"/>'s names;
    /// <paramref name="status"/> is terminal and neither <paramref name="outcome"/> nor the entry's
    /// existing one supplies a verdict (a run cannot finish saying nothing); the run has already
    /// reached a terminal status and <paramref name="status"/> is not itself terminal (a finished run
    /// stays finished); or the run has already finished and <paramref name="outcome"/> names a
    /// DIFFERENT verdict from the recorded one (a recorded verdict is not rewritten). See
    /// <see cref="RunRegistryCore.ApplyStatusTransition"/> for each rule's reasoning.
    /// </exception>
    RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null);

    /// <summary>The entry for <paramref name="runId"/>, or <see langword="null"/> if there is none.</summary>
    RunRegistryEntry? TryGetRun(string runId);

    /// <summary>
    /// Every run this registry knows about, <b>most recent first</b> — ordered by
    /// <see cref="RunRegistryEntry.StartedAtUtc"/> descending, ties broken by
    /// <see cref="RunRegistryEntry.RunId"/> ordinal descending.
    /// </summary>
    /// <remarks>
    /// The tie-break is a determinism backstop, not the ordering that normally applies: every
    /// implementation makes <see cref="RunRegistryEntry.StartedAtUtc"/> STRICTLY increasing within
    /// its own instance (see <see cref="RunRegistryTimestamps.NextStartedAt"/>), because the system
    /// clock's real resolution on Windows is around 15 ms and two runs started inside one tick of it
    /// would otherwise have no defined order at all — which would make "the most recent run"
    /// non-deterministic exactly where <c>explain_run</c> depends on it.
    /// </remarks>
    IReadOnlyList<RunRegistryEntry> ListRuns();
}

/// <summary>
/// The queries every <see cref="IRunRegistry"/> consumer needs, derived once from
/// <see cref="IRunRegistry.ListRuns"/> rather than reimplemented by each implementation — so the
/// in-memory and file-backed registries cannot answer "which run is the most recent" differently.
/// </summary>
public static class RunRegistryExtensions
{
    /// <summary>The most recently started run whatever its status, or <see langword="null"/> if the registry is empty.</summary>
    public static RunRegistryEntry? MostRecentRun(this IRunRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // ONE ListRuns() call, held in a local: against the file-backed registry each call is a
        // directory scan plus a parse of every entry, so calling it twice (once to test Count, once
        // to index) would double that cost for no benefit.
        var runs = registry.ListRuns();
        return runs.Count > 0 ? runs[0] : null;
    }

    /// <summary>
    /// The most recently started run that has reached a terminal status —
    /// <c>explain_run</c>/<c>diagnose_run</c>'s default when the caller omits <c>eventsPath</c>.
    /// </summary>
    /// <remarks>
    /// <b>Finished, not merely most recent, and that is a compatibility requirement.</b> The
    /// retired <c>ILastRunTracker</c> recorded a run only at COMPLETION, so a call made while a run
    /// was still in flight defaulted to the previous COMPLETED run. The registry writes an entry at
    /// run start as well, so defaulting to <see cref="MostRecentRun"/> would silently change that
    /// behaviour into "diagnose the run that is happening right now" — against an events file the
    /// engine is still appending to, or has not created yet. US-S3-01's acceptance criterion is that
    /// the replacement is strictly more capable and never a regression, so the filter here is what
    /// preserves the old semantics exactly while the persistence underneath widens what "most
    /// recent" can reach (a run from a previous server process).
    /// </remarks>
    public static RunRegistryEntry? MostRecentFinishedRun(this IRunRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.ListRuns().FirstOrDefault(entry => RunRegistryStatus.IsTerminal(entry.Status));
    }
}

/// <summary>
/// The one place a run's <see cref="RunRegistryEntry.StartedAtUtc"/> is stamped, shared by every
/// <see cref="IRunRegistry"/> implementation so they cannot order runs differently.
/// </summary>
internal static class RunRegistryTimestamps
{
    /// <summary>
    /// The current UTC instant, forced to be STRICTLY greater than <paramref name="previous"/> (by
    /// one tick when the clock has not visibly advanced), and stores it back.
    /// </summary>
    /// <remarks>
    /// <b>Why a monotonic floor rather than a raw clock read.</b> <see cref="DateTimeOffset.UtcNow"/>
    /// has 100 ns precision but nothing like 100 ns RESOLUTION — on Windows it is driven by a system
    /// timer that updates roughly every 15 ms. Two <c>run_suite</c> calls a few milliseconds apart
    /// therefore routinely get the IDENTICAL timestamp, which would leave "which of these is the
    /// most recent run" decided by <see cref="IRunRegistry.ListRuns"/>'s arbitrary run-id tie-break
    /// — i.e. by a GUID, i.e. at random. <c>explain_run</c>'s whole default-to-last-run behaviour
    /// rests on that ordering, so the floor is a correctness fix, not a cosmetic one.
    /// <para>
    /// The floor is per registry INSTANCE. A file-backed registry in a fresh process seeds it from
    /// the newest entry already on disk (see <c>FileRunRegistry</c>), so ordering survives a restart
    /// too; across two processes running CONCURRENTLY it degrades to whatever the shared system
    /// clock says, which is the best any file-based scheme can do without the cross-process lock
    /// US-S3-04 introduces.
    /// </para>
    /// <para>
    /// Callers must invoke this while holding their own write lock — it reads and writes
    /// <paramref name="previous"/> non-atomically by design, since every caller already needs that
    /// lock for the write the timestamp belongs to.
    /// </para>
    /// </remarks>
    public static DateTimeOffset NextStartedAt(ref DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        if (previous is { } floor && now <= floor)
        {
            now = floor.AddTicks(1);
        }

        previous = now;
        return now;
    }
}
