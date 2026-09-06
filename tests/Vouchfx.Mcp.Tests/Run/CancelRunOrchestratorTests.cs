using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// US-S3-03's <c>cancel_run</c> at the unit seam: which of the four answers each situation earns,
/// and — the design's load-bearing claim — that the "cancelled" answer fires the run's OWN
/// cancellation source rather than anything this type invented.
/// </summary>
/// <remarks>
/// The end-to-end proof that firing that source produces a cancelled run with an <c>Inconclusive</c>
/// outcome (and never <c>Fail</c>) lives in <c>RealRunLifecycleMcpTests</c>, over the real wire
/// against a real <c>RunSuiteOrchestrator</c>; these cases pin the resolution rules in front of it.
/// </remarks>
public class CancelRunOrchestratorTests
{
    private const string EventsFilePath = "/tmp/vouchfx/run-events.jsonl";

    [Fact]
    public void Cancel_RunInFlightInThisProcess_FiresTheRunsOwnCancellationSource()
    {
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);

        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(entry.RunId, stopSignal);

        var outcome = new CancelRunOrchestrator(registry, cancellations)
            .Cancel(new CancelRunRequest(entry.RunId));

        var answered = Assert.IsType<CancelRunOutcome.Answered>(outcome);
        Assert.Equal(CancelRunStatus.Cancelled, answered.Result.Status);
        Assert.Equal(entry.RunId, answered.Result.RunId);

        // AC-002 at its narrowest: what cancel_run does is cancel the token the run is ALREADY
        // executing under. Everything else — stdin close, grace period, force-kill, the Inconclusive
        // verdict, the released workspace claim — is run_suite's existing behaviour reacting to it,
        // which is exactly why there is no second cancellation path to review.
        Assert.True(stopSignal.IsCancellationRequested);
        Assert.True(scope.CancellationRequested);
    }

    [Fact]
    public void Cancel_WithAReason_RecordsItOnTheScopeAndNeverInTheResult()
    {
        const string reason = "superseded by a newer commit";

        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);

        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(entry.RunId, stopSignal);

        var answered = Assert.IsType<CancelRunOutcome.Answered>(
            new CancelRunOrchestrator(registry, cancellations)
                .Cancel(new CancelRunRequest(entry.RunId, reason)));

        Assert.Equal(reason, scope.CancellationReason);

        // Spec §5.8's CancelRunOutput is { meta, runId, status } — there is no field for a reason, and
        // adding one would put caller free text back on the wire for no purpose. The result carries
        // exactly the two values the spec names.
        Assert.Equal(entry.RunId, answered.Result.RunId);
        Assert.DoesNotContain(reason, answered.Result.RunId, StringComparison.Ordinal);
        Assert.DoesNotContain(reason, answered.Result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_CalledTwiceForOneRun_IsIdempotentAndKeepsTheFirstReason()
    {
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);

        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(entry.RunId, stopSignal);
        var orchestrator = new CancelRunOrchestrator(registry, cancellations);

        var first = Assert.IsType<CancelRunOutcome.Answered>(
            orchestrator.Cancel(new CancelRunRequest(entry.RunId, "first")));
        var second = Assert.IsType<CancelRunOutcome.Answered>(
            orchestrator.Cancel(new CancelRunRequest(entry.RunId, "second")));

        Assert.Equal(CancelRunStatus.Cancelled, first.Result.Status);

        // A host that polls and re-cancels must not be told the run cannot be cancelled just because
        // it already asked. The run is still stopping; "cancelled" remains the true answer.
        Assert.Equal(CancelRunStatus.Cancelled, second.Result.Status);

        // The run is already stopping for the first reason; rewriting it would change the record of
        // WHY under the operator who is watching it happen.
        Assert.Equal("first", scope.CancellationReason);
    }

    [Theory]
    [InlineData(RunRegistryStatus.Completed, nameof(RunVerdict.Pass))]
    [InlineData(RunRegistryStatus.Cancelled, nameof(RunVerdict.Inconclusive))]
    public void Cancel_RunAlreadyTerminal_IsAlreadyFinished_AndNotAnError(string status, string outcome)
    {
        // Gherkin (US-S3-03): "Given a run has already completed … Then the tool result's isError
        // field is false, And status is 'already_finished'." Asserted at this seam as "an Answered
        // outcome" — the union case the tool maps to StructuredToolResult.Success; the isError half
        // itself is pinned over the wire in RealRunLifecycleMcpTests.
        var registry = new StubRunRegistry();
        var entry = status == RunRegistryStatus.Cancelled
            ? registry.AddCancelledRun(EventsFilePath)
            : registry.AddCompletedRun(EventsFilePath, outcome);

        var answered = Assert.IsType<CancelRunOutcome.Answered>(
            new CancelRunOrchestrator(registry, new InProcessRunCancellations())
                .Cancel(new CancelRunRequest(entry.RunId)));

        Assert.Equal(CancelRunStatus.AlreadyFinished, answered.Result.Status);
        Assert.Equal(entry.RunId, answered.Result.RunId);
    }

    [Fact]
    public void Cancel_RunAlreadyTerminal_NeverSignalsAnything()
    {
        // Defensive, and worth its own case: a stale registration under a finished run's id must not
        // be fired. It would cancel whatever that source now belongs to.
        var registry = new StubRunRegistry();
        var entry = registry.AddCompletedRun(EventsFilePath);

        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(entry.RunId, stopSignal);

        Assert.IsType<CancelRunOutcome.Answered>(
            new CancelRunOrchestrator(registry, cancellations).Cancel(new CancelRunRequest(entry.RunId)));

        Assert.False(stopSignal.IsCancellationRequested);
        Assert.False(scope.CancellationRequested);
    }

    [Fact]
    public void Cancel_UnknownRunId_IsRunNotFound()
    {
        var outcome = new CancelRunOrchestrator(new StubRunRegistry(), new InProcessRunCancellations())
            .Cancel(new CancelRunRequest("run-00000000000000000000000000000000"));

        Assert.IsType<CancelRunOutcome.RunNotFound>(outcome);
    }

    [Fact]
    public void Cancel_RunningRunNotHeldHere_WithNoWorkspace_IsNotCancellable()
    {
        // No workspace ⇒ no run lock ⇒ no other process can share this registry, so the only honest
        // reading of "running but not in my cancellation map" is "it is finishing right now". Never
        // reported as stale: without a lock there is nothing that could prove staleness.
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations(), runLock: null)
            .Cancel(new CancelRunRequest(entry.RunId));

        var notCancellable = Assert.IsType<CancelRunOutcome.NotCancellable>(outcome);
        Assert.Contains("session-scoped", notCancellable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_RunningRunHeldByAnotherProcess_IsNotCancellable_AndHedgesRatherThanAsserting()
    {
        // THE cross-process stance, at its seam. The lock is held and NO run is in flight here, so
        // this server has no channel to whatever holds it — and says exactly that rather than
        // returning `cancelled` for a signal it never sent.
        //
        // It also does not overstate what the probe established (a gatekeeper MAJOR / security MINOR):
        // "the workspace lock is held" is not "another PROCESS is running THIS RUN". The other
        // possibility — the run finished here and its completing record was lost — is named rather
        // than asserted away, exactly as the no-workspace branch above already does.
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);
        var runLock = new StubRunLock(new RunLockResult.HeldByAnotherRun());

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations(), runLock)
            .Cancel(new CancelRunRequest(entry.RunId));

        var notCancellable = Assert.IsType<CancelRunOutcome.NotCancellable>(outcome);
        Assert.Contains("DIFFERENT server process", notCancellable.Message, StringComparison.Ordinal);
        Assert.Contains("completing record was lost", notCancellable.Message, StringComparison.Ordinal);

        // The refuted claim, pinned negatively so a regression to the categorical wording fails here:
        // this branch must never state as fact that another process is running this run.
        Assert.DoesNotContain(
            "is in flight, but a DIFFERENT server process", notCancellable.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The branch the categorical message was flat wrong about: a phantom <c>running</c> entry
    /// alongside a live run in THIS process. The lock is held — by us — so the probe learned nothing
    /// about the entry the caller asked about, and the message says so.
    /// </summary>
    /// <remarks>
    /// Reachable, not hypothetical: a server killed mid-run leaves a <c>running</c> entry, the OS
    /// frees its lock, and the next server starts an unrelated run against the same workspace. Asking
    /// to cancel the phantom then hits exactly this state, and the old wording told the operator that
    /// a different server process was running it — which was false in every particular.
    /// </remarks>
    [Fact]
    public void Cancel_RunningEntryWhileThisServerIsBusyWithADifferentRun_SaysTheProbeWasMaskedByOurOwnRun()
    {
        var registry = new StubRunRegistry();
        var phantom = registry.AddRunningRun(EventsFilePath);
        var ourOwnRun = registry.AddRunningRun(EventsFilePath);

        // OUR run is in flight and holds the workspace claim; the phantom is not in the map.
        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(ourOwnRun.RunId, stopSignal);

        var outcome = new CancelRunOrchestrator(
                registry, cancellations, new StubRunLock(new RunLockResult.HeldByAnotherRun()))
            .Cancel(new CancelRunRequest(phantom.RunId));

        var notCancellable = Assert.IsType<CancelRunOutcome.NotCancellable>(outcome);
        Assert.Contains("in this same server", notCancellable.Message, StringComparison.Ordinal);
        Assert.Contains("list_runs", notCancellable.Message, StringComparison.Ordinal);

        // And it must NOT claim another process has it — that is the falsehood this branch exists for.
        Assert.DoesNotContain("DIFFERENT server process", notCancellable.Message, StringComparison.Ordinal);

        // Nothing was signalled: our own run must not be stopped because somebody asked about a
        // different id.
        Assert.False(stopSignal.IsCancellationRequested);
        Assert.False(scope.CancellationRequested);
    }

    [Fact]
    public void Cancel_RunningRunWithAFreeLock_IsAStaleEntry_AndTheProbeIsReleasedImmediately()
    {
        // THE phantom-entry stance. A free lock beside a `running` entry proves no process is running
        // it — the OS releases the claim when the holder dies, so there is nothing else it could mean.
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);
        var release = new TrackingDisposable();
        var runLock = new StubRunLock(new RunLockResult.Acquired(release));

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations(), runLock)
            .Cancel(new CancelRunRequest(entry.RunId));

        var stale = Assert.IsType<CancelRunOutcome.StaleEntry>(outcome);
        Assert.Contains("no server process is running it", stale.Message, StringComparison.Ordinal);

        // The probe must not outlive the answer. Holding the claim past this call would wedge the
        // workspace for every subsequent run — a read-shaped tool turning into an indefinite writer,
        // which is the exact failure the lock-free rule elsewhere exists to prevent.
        Assert.True(release.WasDisposed);
    }

    [Fact]
    public void Cancel_RunningRunWhoseLockCannotBeEvaluated_IsNotCancellable_NotStale()
    {
        // Fail toward "cannot cancel". Claiming an entry is stale on the strength of a probe that
        // FAILED would invite an operator to delete the record of a run that is genuinely in flight
        // elsewhere; the reverse mistake costs a retry.
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);
        var runLock = new StubRunLock(new RunLockResult.Unavailable(new IOException("denied")));

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations(), runLock)
            .Cancel(new CancelRunRequest(entry.RunId));

        var notCancellable = Assert.IsType<CancelRunOutcome.NotCancellable>(outcome);
        Assert.Contains("could not determine", notCancellable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_RunThatFinishesBetweenTheStatusCheckAndTheSignal_IsAlreadyFinished()
    {
        // The recheck-before-concluding path: the run reached its completing write between this call
        // reading the entry and trying to signal it, so the signal found nothing and the SECOND
        // registry read is what settles the answer. Answering `already_finished` about a run that IS
        // finished beats reporting a lifecycle error about it — and beats a cross-process claim that
        // would be simply untrue.
        //
        // NOTE on how "not held" arises here, corrected from an earlier comment that named the wrong
        // mechanism: this fixture registers NOTHING in the cancellation map, so
        // TryRequestCancellation returns false on the MISSING-KEY path, not through
        // InProcessRunCancellations' per-entry `_closed` gate. Both produce "not held" and this
        // orchestrator cannot tell them apart by design — but the gate is exercised by
        // RunCancellationRegistryTests, not by this case.
        var registry = new FinishesOnSecondLookupRegistry(EventsFilePath);
        var runLock = new StubRunLock(new RunLockResult.HeldByAnotherRun());

        var answered = Assert.IsType<CancelRunOutcome.Answered>(
            new CancelRunOrchestrator(registry, new InProcessRunCancellations(), runLock)
                .Cancel(new CancelRunRequest(registry.RunId)));

        Assert.Equal(CancelRunStatus.AlreadyFinished, answered.Result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancel_MissingOrBlankRunId_IsAnInvalidArgument(string? runId)
    {
        var outcome = new CancelRunOrchestrator(new StubRunRegistry(), new InProcessRunCancellations())
            .Cancel(new CancelRunRequest(runId));

        var invalid = Assert.IsType<CancelRunOutcome.InvalidArgument>(outcome);
        Assert.Contains("runId", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_OverLongRunId_IsRefusedBeforeTheRegistryIsTouched()
    {
        // Mirrors GetRunStatusOrchestratorTests' identical case (a review's NIT: cancel_run shares the
        // bound through RunIdArgument but nothing pinned it here). A registry that throws on any
        // lookup is what proves the bound runs FIRST rather than merely that it runs — and this tool
        // has more to lose from a late check than get_run_status does, since it also probes the lock.
        var registry = new ThrowingOnLookupRegistry();

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations())
            .Cancel(new CancelRunRequest(new string('r', RunLifecycleLimits.MaxRunIdChars + 1)));

        Assert.IsType<CancelRunOutcome.InvalidArgument>(outcome);
        Assert.False(registry.WasQueried);
    }

    [Fact]
    public void Cancel_OverLongReason_IsRefusedRatherThanRetainedInMemory()
    {
        var registry = new StubRunRegistry();
        var entry = registry.AddRunningRun(EventsFilePath);

        var outcome = new CancelRunOrchestrator(registry, new InProcessRunCancellations())
            .Cancel(new CancelRunRequest(
                entry.RunId, new string('x', RunLifecycleLimits.MaxReasonChars + 1)));

        var invalid = Assert.IsType<CancelRunOutcome.InvalidArgument>(outcome);
        Assert.Contains("reason", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>An <see cref="IRunLock"/> that always answers the same way.</summary>
    private sealed class StubRunLock(RunLockResult result) : IRunLock
    {
        public RunLockResult TryAcquire() => result;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose() => WasDisposed = true;
    }

    /// <summary>
    /// A registry whose one run reads <c>running</c> on the first lookup and <c>completed</c> on
    /// every one after — the shape of a run reaching its completing write between
    /// <c>cancel_run</c>'s status check and its signal attempt.
    /// </summary>
    private sealed class FinishesOnSecondLookupRegistry : IRunRegistry
    {
        private readonly RunRegistryEntry _running;
        private readonly RunRegistryEntry _completed;
        private int _lookups;

        public FinishesOnSecondLookupRegistry(string eventsFilePath)
        {
            RunId = "run-" + Guid.NewGuid().ToString("N");
            var startedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            _running = new RunRegistryEntry(
                RunId, RunRegistryStatus.Running, Outcome: null, startedAt, FinishedAtUtc: null,
                SpecPaths: ["stub.e2e.yaml"], eventsFilePath,
                Labels: new Dictionary<string, string>(StringComparer.Ordinal));
            _completed = _running with
            {
                Status = RunRegistryStatus.Completed,
                Outcome = nameof(RunVerdict.Pass),
                FinishedAtUtc = startedAt.AddSeconds(1),
            };
        }

        public string RunId { get; }

        public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
            throw new NotSupportedException();

        public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
            throw new NotSupportedException();

        public RunRegistryEntry? TryGetRun(string runId) =>
            string.Equals(runId, RunId, StringComparison.Ordinal)
                ? (Interlocked.Increment(ref _lookups) == 1 ? _running : _completed)
                : null;

        public RunListing ListRuns() => RunListing.Complete([_completed]);
    }
}
