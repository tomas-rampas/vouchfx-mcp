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
    public RunRegistryEntry AddCompletedRun(string eventsFilePath, string outcome = nameof(RunVerdict.Pass)) =>
        Add(RunRegistryStatus.Completed, eventsFilePath, outcome);

    /// <summary>
    /// Appends a run still in flight — no outcome, no finish time. Used by
    /// <c>ExplainRunOrchestratorTests.ExplainAsync_DefaultsPastARunStillInFlightToTheLastFinishedRun</c>
    /// to prove such a run is NOT <c>explain_run</c>'s default.
    /// </summary>
    public RunRegistryEntry AddRunningRun(string eventsFilePath) =>
        Add(RunRegistryStatus.Running, eventsFilePath, outcome: null);

    private RunRegistryEntry Add(string status, string eventsFilePath, string? outcome)
    {
        var startedAtUtc = _nextStartedAtUtc;
        _nextStartedAtUtc = _nextStartedAtUtc.AddMinutes(1);

        var entry = new RunRegistryEntry(
            RunId: "run-" + Guid.NewGuid().ToString("N"),
            Status: status,
            Outcome: outcome,
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: RunRegistryStatus.IsTerminal(status) ? startedAtUtc.AddSeconds(1) : null,
            SpecPaths: ["stub.e2e.yaml"],
            EventsFilePath: eventsFilePath,
            Labels: new Dictionary<string, string>(StringComparer.Ordinal));

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
