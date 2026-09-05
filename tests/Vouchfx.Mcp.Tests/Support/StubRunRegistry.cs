using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// An <see cref="IRunRegistry"/> whose entries a test writes directly, so a reader
/// (<c>ExplainRunOrchestrator</c>, <c>DiagnoseRunOrchestrator</c>) can be exercised against a run
/// whose events file the TEST chose — without first driving a whole <c>run_suite</c> call through
/// the CLI gate and a fake runner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the production registries cannot be used for this.</b> Both
/// <see cref="InMemoryRunRegistry"/> and <see cref="FileRunRegistry"/> MINT the events-file path
/// themselves (see <see cref="IRunRegistry.StartRun"/> — where a run's artefacts live is the storage
/// backend's decision, deliberately not the caller's), so neither can be pointed at a fixture file a
/// test wrote to an arbitrary path. That is the right production behaviour and the reason this stub
/// exists rather than a seam being widened to accommodate tests.
/// </para>
/// <para>
/// It implements the ordering and the finished/unfinished distinction by delegating to the SAME
/// <see cref="RunRegistryExtensions"/> the production types use, so a test built on it cannot pass
/// against ordering semantics the real registries do not have.
/// </para>
/// </remarks>
internal sealed class StubRunRegistry : IRunRegistry
{
    private readonly List<RunRegistryEntry> _entries = [];
    private DateTimeOffset _nextStartedAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A registry holding exactly one completed run whose events file is <paramref name="eventsFilePath"/>.</summary>
    public static StubRunRegistry WithCompletedRun(string eventsFilePath, string outcome = nameof(RunVerdict.Pass))
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(eventsFilePath, outcome);
        return registry;
    }

    /// <summary>
    /// Appends a completed run. Each call takes a later <see cref="RunRegistryEntry.StartedAtUtc"/>
    /// than the last, so "most recent" is the run added most recently — deterministic, with no
    /// dependence on the system clock's resolution.
    /// </summary>
    /// <param name="specPaths">
    /// The suite paths to record. Defaults to one innocuous relative name; supplied explicitly by the
    /// egress-sanitising cases, for which the path's CONTENT is the subject.
    /// </param>
    public RunRegistryEntry AddCompletedRun(
        string eventsFilePath,
        string outcome = nameof(RunVerdict.Pass),
        IReadOnlyDictionary<string, string>? labels = null,
        IReadOnlyList<string>? specPaths = null) =>
        Add(RunRegistryStatus.Completed, eventsFilePath, outcome, labels, specPaths);

    /// <summary>
    /// Appends a run still in flight — no outcome, no finish time. Used by
    /// <c>ExplainRunOrchestratorTests.ExplainAsync_DefaultsPastARunStillInFlightToTheLastFinishedRun</c>
    /// to prove such a run is NOT <c>explain_run</c>'s default, and by US-S3-03's
    /// <c>cancel_run</c>/<c>list_runs</c> tests, for which a <c>running</c> entry is the subject
    /// rather than a distractor.
    /// </summary>
    public RunRegistryEntry AddRunningRun(
        string eventsFilePath, IReadOnlyDictionary<string, string>? labels = null) =>
        Add(RunRegistryStatus.Running, eventsFilePath, outcome: null, labels);

    /// <summary>
    /// Appends a run recorded as <see cref="RunRegistryStatus.Cancelled"/> — the terminal status
    /// US-S3-03's <c>cancel_run</c> makes reachable.
    /// </summary>
    /// <param name="outcome">
    /// The verdict the run genuinely reached. Defaults to <c>Inconclusive</c> — the ordinary case, a
    /// run cancelled before any suite failed — but is deliberately a PARAMETER, because the status and
    /// the outcome are independent: a multi-suite run cancelled after an earlier suite failed is
    /// recorded <c>cancelled</c>/<c>Fail</c>, and
    /// <c>RunSuiteOrchestratorTests.Cancel_ARunAlreadyRecordedAsCancelledWithAFailOutcome_...</c> is
    /// what exercises that pairing through this fixture.
    /// </param>
    public RunRegistryEntry AddCancelledRun(
        string eventsFilePath, string outcome = nameof(RunVerdict.Inconclusive)) =>
        Add(RunRegistryStatus.Cancelled, eventsFilePath, outcome, labels: null);

    private RunRegistryEntry Add(
        string status,
        string eventsFilePath,
        string? outcome,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<string>? specPaths = null)
    {
        var startedAtUtc = _nextStartedAtUtc;
        _nextStartedAtUtc = _nextStartedAtUtc.AddMinutes(1);

        var entry = new RunRegistryEntry(
            RunId: "run-" + Guid.NewGuid().ToString("N"),
            Status: status,
            Outcome: outcome,
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: RunRegistryStatus.IsTerminal(status) ? startedAtUtc.AddSeconds(1) : null,
            SpecPaths: specPaths ?? ["stub.e2e.yaml"],
            EventsFilePath: eventsFilePath,
            Labels: labels ?? new Dictionary<string, string>(StringComparer.Ordinal));

        _entries.Add(entry);
        return entry;
    }

    public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
        throw new NotSupportedException("StubRunRegistry is a READER fixture; use AddCompletedRun/AddRunningRun to seed it.");

    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
        throw new NotSupportedException("StubRunRegistry is a READER fixture; use AddCompletedRun/AddRunningRun to seed it.");

    public RunRegistryEntry? TryGetRun(string runId) =>
        _entries.FirstOrDefault(entry => string.Equals(entry.RunId, runId, StringComparison.Ordinal));

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="RunRegistryCore.OrderMostRecentFirst"/> — the same comparator both
    /// production registries use — so this fixture cannot order runs by a rule they do not have.
    /// (It previously ordered by <see cref="RunRegistryEntry.StartedAtUtc"/> alone, which happened to
    /// agree here only because <see cref="Add"/> hands out strictly increasing timestamps and the
    /// run-id tie-break therefore never fires.)
    /// </remarks>
    public IReadOnlyList<RunRegistryEntry> ListRuns() => RunRegistryCore.OrderMostRecentFirst(_entries);
}
