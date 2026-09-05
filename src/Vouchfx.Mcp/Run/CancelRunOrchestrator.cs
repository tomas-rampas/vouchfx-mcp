using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-03's <c>cancel_run</c> pipeline: resolve a <c>runId</c> through the run registry and, when
/// this process is the one running it, ask it to stop through the SAME graceful-stop mechanism
/// <c>run_suite</c> already uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no cancellation mechanism in this file, and that is AC-002 satisfied structurally.</b>
/// The story requires cancellation to go "through the same graceful-stop mechanism run_suite already
/// uses to stop the CLI child process (closing stdin per <c>--shutdown-on-stdin-eof</c>, ~35 s grace,
/// then force-kill)" and to introduce no second path. So this type owns no process handle, no timer
/// and no kill. It fires the <see cref="CancellationTokenSource"/> the in-flight run is ALREADY
/// executing under (published under its run id by <c>RunSuiteOrchestrator</c> — see
/// <see cref="IRunCancellationRegistry"/>), which is indistinguishable, everywhere downstream, from
/// the MCP caller's own token firing. <see cref="VouchfxCliSuiteRunner"/> then performs exactly the
/// stop sequence the AC names, and EDGE-002's existing path reports the run as <c>Inconclusive</c>
/// (never <c>Fail</c>) and releases the workspace claim, unchanged.
/// </para>
/// <para>
/// <b>The four answers, and why two of them are errors.</b>
/// <list type="bullet">
/// <item><description>
/// <c>cancelled</c> — the run was in flight HERE and has been signalled. Returned as soon as the
/// signal is delivered; see <see cref="CancelRunStatus.Cancelled"/> for why this call does not wait
/// for the stop to complete.
/// </description></item>
/// <item><description>
/// <c>already_finished</c> — the run had already reached a terminal status. <c>isError: false</c>,
/// per the story's AC and its own Gherkin scenario: a polling host loses this race routinely, and the
/// honest answer is what happened.
/// </description></item>
/// <item><description>
/// <c>VFX-E-1507 RunNotCancellable</c> — the run is recorded as in flight, but NOT by this process,
/// and the workspace's run lock does not prove it is residue either. See the next paragraph, and
/// <see cref="DescribeUncancellableRun"/> for why the message distinguishes three shapes of that.
/// </description></item>
/// <item><description>
/// <c>VFX-E-1508 StaleRunEntry</c> — the run is recorded as in flight and the workspace's run lock is
/// FREE, which proves no process is running it. The entry is residue: a server killed mid-run, or a
/// run whose completing registry write failed.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Cross-process cancellation is NOT implementable today, and is refused rather than faked.</b>
/// US-S3-04's claim is a <see cref="FileShare.None"/> handle that deliberately carries no payload
/// (see <see cref="WorkspaceRunLock"/>), so there is no channel from this process to the one holding
/// the run — and a request-a-cancellation sideband file would be precisely the "second, divergent
/// cancellation path" AC-002 forbids, with its own staleness and trust problems on top. This is
/// <c>sprint-00-overview.md</c> §3's stance (a): the capability that exists works, and the one that
/// does not returns a structured, catalogued error naming the limitation. A host is never told a run
/// was cancelled when nothing was signalled.
/// </para>
/// <para>
/// <b>Why this tool may touch the run lock when <c>get_run_status</c>/<c>list_runs</c> may not.</b>
/// Distinguishing "another process is running this" from "this entry is residue" needs the one
/// liveness signal that exists, and reading it means ACQUIRING the lock: the handle IS the lock, so
/// there is no non-exclusive way to ask. <c>cancel_run</c> is not a read-only tool — it exists to
/// CHANGE a run's lifecycle — so spec §4.6's "read-only tools are safe to call concurrently" rule
/// does not bind it, and <c>RunLockSourceGuardTests</c> names it as the second, and only other,
/// permitted call site. The read-only tools remain structurally excluded, which is the property that
/// rule actually protects.
/// <para>
/// <b>The probe's race is real and is stated rather than glossed:</b> the probe holds the lock for
/// the microseconds between <see cref="IRunLock.TryAcquire"/> returning and the claim being disposed
/// on the next line, and a <c>run_suite</c> call that tries to claim the workspace inside that window
/// is rejected with <c>VFX-E-1501 RunInProgress</c> when nothing was actually running. Three things
/// bound it: the probe is reached only when a <c>running</c> entry exists AND is not held here (so
/// never during an ordinary run, when the lock is held and the probe fails instantly without taking
/// anything); it is taken at most once per call; and <c>VFX-E-1501</c> is <c>retryable: true</c>, so
/// the host's documented next action already resolves it. The alternative — never probing — would
/// leave a permanently-<c>running</c> phantom entry indistinguishable from a live run for the whole
/// server, which is a worse and permanent failure than a transient retryable one.
/// </para>
/// </para>
/// </remarks>
public sealed class CancelRunOrchestrator
{
    /// <summary>The tool's own name, from the factory that owns it (see <see cref="GetRunEventsOrchestrator"/>).</summary>
    private static readonly string ToolName = Tools.CancelRunTool.Name;

    private readonly IRunRegistry _runRegistry;
    private readonly IRunCancellationRegistry _cancellations;
    private readonly IRunLock? _runLock;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the authority on whether the run exists and whether it is over. This
    /// tool never writes it: the run's own <c>run_suite</c> call records the terminal
    /// <see cref="RunRegistryStatus.Cancelled"/> transition when it unwinds, which keeps the registry's
    /// three documented write points intact.
    /// </param>
    /// <param name="cancellations">
    /// The bridge <c>RunSuiteOrchestrator</c> publishes into. <b>Must be the SAME instance</b> — a
    /// second one would leave every cancellation reporting <c>VFX-E-1507</c> against a run this very
    /// process is holding.
    /// </param>
    /// <param name="runLock">
    /// US-S3-04's cross-process claim, or <see langword="null"/> when no workspace is configured. With
    /// none, there is no lock to probe and no other process to share a registry with (the registry is
    /// <see cref="InMemoryRunRegistry"/> and session-scoped), so the stale/held distinction cannot
    /// arise and is not attempted.
    /// </param>
    public CancelRunOrchestrator(
        IRunRegistry runRegistry,
        IRunCancellationRegistry cancellations,
        IRunLock? runLock = null)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        ArgumentNullException.ThrowIfNull(cancellations);

        _runRegistry = runRegistry;
        _cancellations = cancellations;
        _runLock = runLock;
    }

    /// <summary>Asks one run to stop, and reports what could be done about it.</summary>
    public CancelRunOutcome Cancel(CancelRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RunIdArgument.Validate(request.RunId, ToolName) is { } argumentError)
        {
            return new CancelRunOutcome.InvalidArgument(argumentError);
        }

        if (request.Reason is { Length: > RunLifecycleLimits.MaxReasonChars })
        {
            return new CancelRunOutcome.InvalidArgument(
                $"{ToolName}'s 'reason' must be at most {RunLifecycleLimits.MaxReasonChars} characters. "
                + "It is context for the operator, not a payload — keep it short.");
        }

        var runId = request.RunId!;

        var entry = _runRegistry.TryGetRun(runId);
        if (entry is null)
        {
            return new CancelRunOutcome.RunNotFound(RunIdArgument.DescribeMissingRun(runId));
        }

        if (RunRegistryStatus.IsTerminal(entry.Status))
        {
            return AlreadyFinished(runId);
        }

        // THE cancellation. Everything above is resolution and everything below is the honest report
        // of a run this process cannot reach.
        if (_cancellations.TryRequestCancellation(runId, request.Reason))
        {
            return new CancelRunOutcome.Answered(new CancelRunResult(runId, CancelRunStatus.Cancelled));
        }

        // Re-read before concluding anything. The run can legitimately have finished between the
        // status check above and the signal attempt — that window is exactly what
        // InProcessRunCancellations' per-entry gate reports as "not held" — and answering
        // `already_finished` about a run that IS finished beats reporting a lifecycle error about it.
        if (_runRegistry.TryGetRun(runId) is { } recheck && RunRegistryStatus.IsTerminal(recheck.Status))
        {
            return AlreadyFinished(runId);
        }

        return DescribeUncancellableRun(runId);
    }

    private static CancelRunOutcome.Answered AlreadyFinished(string runId) =>
        new(new CancelRunResult(runId, CancelRunStatus.AlreadyFinished));

    /// <summary>
    /// Decides between <c>VFX-E-1507</c> (a live run this process cannot reach) and
    /// <c>VFX-E-1508</c> (an entry no process is running), using the one liveness signal that exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See this type's remarks for why the probe is permitted here, and for the race it accepts.
    /// Every ambiguous answer resolves to <c>VFX-E-1507</c>: claiming an entry is stale when this
    /// server could not establish that would invite an operator to discard the record of a run that is
    /// genuinely in flight elsewhere.
    /// </para>
    /// <para>
    /// <b>The probe answers per WORKSPACE; the message must not overstate it as per RUN</b> (a
    /// gatekeeper MAJOR and a security MINOR, independently). "The lock is held" establishes that
    /// SOMETHING holds the workspace's claim — not that the thing holding it is the run the caller
    /// named. The earlier wording ("a DIFFERENT server process is running it") was categorically false
    /// in two reachable states: a phantom <c>running</c> entry coexisting with a live run in THIS
    /// process, and the publish window at the tail of every local run (the cancellation scope is
    /// disposed when the run body unwinds, while the lock is released one frame later in
    /// <c>RunSuiteOrchestrator.RunAsync</c>'s <c>finally</c>). So the held-lock case is split by the
    /// one further fact this process can establish for certain —
    /// <see cref="IRunCancellationRegistry.AnyRunIsHeldHere"/>, which under single-flight is exactly
    /// "this process is the claim's holder":
    /// <list type="bullet">
    /// <item><description>
    /// <b>A run IS in flight here</b> (and it is not this one, or the signal above would have
    /// succeeded) ⇒ the claim is OURS, so the probe learned nothing about the named entry. Say that,
    /// and point at the run this server can actually cancel.
    /// </description></item>
    /// <item><description>
    /// <b>No run is in flight here</b> ⇒ the claim belongs to another process, OR the named run has
    /// just finished here and its completing record was lost. Hedged exactly as the no-workspace
    /// branch above already hedges, rather than asserting the first of two possibilities.
    /// </description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private CancelRunOutcome DescribeUncancellableRun(string runId)
    {
        var echoed = VfxCode.SanitiseForEcho(runId);

        if (_runLock is null)
        {
            // No workspace ⇒ InMemoryRunRegistry ⇒ every run in it belongs to THIS process, and this
            // one is not in the cancellation map. It has therefore left the orchestrator's run body
            // and is writing its own completion right now (or its completing write failed and was
            // reported on stderr — see RunSuiteOrchestrator.ReportCompletionNotRecorded). Either way
            // there is nothing left to signal.
            return new CancelRunOutcome.NotCancellable(
                $"The run '{echoed}' is recorded as running but is not in flight in this server "
                + "process, so there is nothing to signal — it is finishing now, or its completing "
                + "record was lost. Call get_run_status again; without --workspace the run registry is "
                + "session-scoped, so no other server process can be holding it.");
        }

        // ONE probe, released immediately. Taking the claim is the only way to ask whether it is free
        // — see WorkspaceRunLock: the handle IS the lock.
        var probe = _runLock.TryAcquire();
        if (probe is RunLockResult.Acquired acquired)
        {
            acquired.Release.Dispose();

            return new CancelRunOutcome.StaleEntry(
                $"The run '{echoed}' is recorded as running, but this workspace's run lock is free — so "
                + "no server process is running it. The entry is residue: a server killed mid-run that "
                + "never wrote its completion, or a run whose completing registry write failed (that "
                + "one is announced on this server's stderr). There is no reaper, so it will read "
                + "'running' until its run directory is removed. Nothing was cancelled because nothing "
                + "is running.");
        }

        if (probe is not RunLockResult.HeldByAnotherRun)
        {
            return new CancelRunOutcome.NotCancellable(
                $"The run '{echoed}' is recorded as running and this server could not determine "
                + "whether another process is holding it: the workspace's run lock could not be "
                + "opened at all (a permissions problem, or something planted at the lock path). "
                + "Nothing was cancelled. Check the output directory, then retry.");
        }

        // The claim is held. WHO holds it is the whole question — see this method's remarks.
        return new CancelRunOutcome.NotCancellable(
            _cancellations.AnyRunIsHeldHere()
                ? $"The run '{echoed}' is not in flight in this server process, and this server "
                  + "cannot tell whether it is live elsewhere: a DIFFERENT run in this same server is "
                  + "holding the workspace's run lock, so probing it says nothing about this entry. "
                  + "Call list_runs and cancel THAT runId if it is the run you meant; otherwise wait "
                  + "for it to finish and call cancel_run again, when the probe can answer for this "
                  + "entry."
                : $"The run '{echoed}' is not in flight in this server process, and the workspace's "
                  + "run lock is held by something else. Either a DIFFERENT server process against "
                  + "this workspace is running it — cancel it from the server that started it, or "
                  + "wait, and its status becomes terminal either way — or it has just finished here "
                  + "and its completing record was lost. Call get_run_status again; if nothing is "
                  + "running it, the claim frees itself when its holder exits.");
    }
}
