using System.Collections.Concurrent;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — the cancel_run bridge (Sprint 3 / US-S3-03; spec §5.8, plan §2.7 invariant 7).
//
// US-S3-03's AC-002 is explicit: "cancellation goes through the SAME graceful-stop mechanism
// run_suite already uses to stop the CLI child process (closing stdin per --shutdown-on-stdin-eof,
// ~35 s grace, then force-kill) — this story does not introduce a second, divergent cancellation
// path". This file is what makes that structurally true rather than a claim.
//
// The mechanism already exists and is reached by exactly one thing: the CancellationToken
// RunSuiteOrchestrator hands to ISuiteRunner.RunAsync. VouchfxCliSuiteRunner reacts to that token by
// closing the child's stdin, waiting GracefulShutdownGrace, and then killing the process tree. So
// cancel_run does not need a stop mechanism of its own — it needs a HANDLE on the token that already
// drives one. That is the whole of this type: a per-process map from a runId to the
// CancellationTokenSource whose token the in-flight run is running under, live for exactly as long as
// that run is.
//
// Firing it is therefore EQUIVALENT to the caller's own token firing, which is why nothing downstream
// had to be taught about cancel_run: EDGE-002's existing path already reports a cancelled run as
// Inconclusive (never Fail), already distinguishes a cancellation from a timeout, and already
// releases the workspace claim in RunAsync's finally. The only thing this file adds to that path is
// the ability to say WHO asked — see IRunCancellationScope.CancellationRequested, which is what turns
// the run's terminal registry status from `completed` into `cancelled`.
//
// ---------------------------------------------------------------------------------------------
// The honest limit: this is PER PROCESS, and cannot be anything else today
// ---------------------------------------------------------------------------------------------
//
// A run held by a DIFFERENT server process against the same workspace cannot be signalled from here.
// There is no IPC channel to it: US-S3-04's cross-process claim is a FileShare.None handle that
// deliberately carries no payload (see WorkspaceRunLock — the exclusivity that makes it a lock is the
// same thing that stops a second process reading anything through it), and inventing a
// request-a-cancellation sideband file would be a second, divergent cancellation path — exactly what
// AC-002 forbids — with its own staleness, ordering and trust problems.
//
// So cancel_run applies sprint-00-overview.md §3's stance (a): the capability that exists works, and
// the one that does not is REFUSED with a structured, catalogued error naming the limitation
// (VFX-E-1507 RunNotCancellable) rather than silently reporting a cancellation that never happened.
// A host is never told a run was cancelled when nothing was signalled.

/// <summary>
/// The seam <c>cancel_run</c> signals an in-flight <c>run_suite</c> through — a process-local map
/// from a run id to the cancellation source the run is already executing under.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface for one implementation, and it earns that.</b> <see cref="InProcessRunCancellations"/>
/// is the only production implementation and no second one is planned; the seam exists so
/// <c>CancelRunOrchestrator</c>'s tests can drive "a run is in flight here" and "it is not" without
/// standing up a real <c>run_suite</c> call, and so this file — rather than a field on
/// <c>RunSuiteOrchestrator</c> — is the single place the cancel bridge's contract is written down.
/// </para>
/// <para>
/// <b>Nothing here is persisted, deliberately.</b> A cancellation request is meaningful only while a
/// process is holding the run; recording one on disk would create a request that outlives the run it
/// targets and a second source of truth about a run's state beside <see cref="IRunRegistry"/>.
/// </para>
/// </remarks>
public interface IRunCancellationRegistry
{
    /// <summary>
    /// Publishes <paramref name="stopSignal"/> as the way to stop <paramref name="runId"/>, for the
    /// lifetime of the returned scope.
    /// </summary>
    /// <param name="runId">The run id <see cref="IRunRegistry.StartRun"/> just minted.</param>
    /// <param name="stopSignal">
    /// The source whose token the run is executing under. Cancelling it must reach
    /// <see cref="ISuiteRunner.RunAsync"/>'s own token — that is what makes the graceful stop the
    /// SAME one <c>run_suite</c> already uses rather than a second path.
    /// </param>
    /// <returns>
    /// A scope that MUST be disposed when the run ends, however it ends. Disposal unpublishes the run
    /// and guarantees <paramref name="stopSignal"/> is never touched afterwards, which is what makes
    /// it safe for the caller to dispose the source itself immediately after.
    /// </returns>
    IRunCancellationScope Register(string runId, CancellationTokenSource stopSignal);

    /// <summary>
    /// Asks the run identified by <paramref name="runId"/> to stop, if this process is holding it.
    /// </summary>
    /// <param name="reason">
    /// The caller's free-form <c>reason</c>, recorded on the scope for the holder to read.
    /// <b>Never echoed back to any caller and never persisted</b> — see
    /// <see cref="IRunCancellationScope.CancellationReason"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a live registration was found and signalled (idempotent — a second
    /// request for the same run also returns <see langword="true"/>), <see langword="false"/> when
    /// this process is not holding that run.
    /// </returns>
    bool TryRequestCancellation(string runId, string? reason);

    /// <summary>
    /// Whether ANY run is currently in flight in this process — irrespective of which one.
    /// </summary>
    /// <remarks>
    /// <b>Exists for exactly one caller, and it is a truthfulness fix rather than a feature</b> (a
    /// gatekeeper MAJOR and a security MINOR, independently). <c>CancelRunOrchestrator</c>'s run-lock
    /// probe answers per WORKSPACE — "is the claim held?" — and its <c>VFX-E-1507</c> message used to
    /// translate a held claim into a per-RUN assertion ("a DIFFERENT server process is running it"),
    /// which is categorically false whenever the claim is held by a run in THIS process while the
    /// entry being asked about is a phantom. Under single-flight this process holds the workspace claim
    /// for very nearly as long as it has a live registration, so this predicate is what lets that
    /// message distinguish "the probe was masked by my own run" from "the probe genuinely found someone
    /// else's claim".
    /// <para>
    /// <b>"Very nearly", not "exactly" — there are TWO publish gaps, and an earlier version of this
    /// remark named neither.</b> <c>RunSuiteOrchestrator.RunAsync</c> takes the claim at its gate and
    /// releases it in a <c>finally</c>; <see cref="Register"/> is called strictly inside that span, so
    /// the claim is held and this predicate is <see langword="false"/> in both windows below:
    /// <list type="bullet">
    /// <item><description>
    /// <b>The HEAD window</b>, between acquiring the claim and <see cref="Register"/> — which happens
    /// only after <c>IRunRegistry.StartRun</c> has minted the run id the registration is keyed by. That
    /// span CONTAINS the CLI version handshake, so it is not instantaneous: on a cold first call, or
    /// against a wedged CLI, it lasts up to the 15-second version-probe timeout.
    /// </description></item>
    /// <item><description>
    /// <b>The TAIL window</b>, between the scope's disposal when the run body unwinds and the claim's
    /// release one frame later in <c>RunAsync</c>'s <c>finally</c>. Brief, and already named at
    /// <c>CancelRunOrchestrator.DescribeUncancellableRun</c>.
    /// </description></item>
    /// </list>
    /// Both are why the caller's no-run-here branch HEDGES rather than asserting another process holds
    /// the claim: in the head window the holder is this very process, starting a run it has not
    /// registered yet.
    /// </para>
    /// <para>
    /// Deliberately NOT a count and NOT an enumeration: the caller needs one bit, and exposing the
    /// live run ids would put a second, racier answer to "what is running" beside
    /// <see cref="IRunRegistry"/>'s.
    /// </para>
    /// </remarks>
    bool AnyRunIsHeldHere();
}

/// <summary>
/// One run's live registration in <see cref="IRunCancellationRegistry"/>. Disposing it unpublishes
/// the run; reading <see cref="CancellationRequested"/> tells the holder whether the stop it observed
/// came from <c>cancel_run</c> or from its own budget/caller token.
/// </summary>
public interface IRunCancellationScope : IDisposable
{
    /// <summary>
    /// <see langword="true"/> once <see cref="IRunCancellationRegistry.TryRequestCancellation"/> has
    /// signalled this run.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole reason the scope is returned rather than kept private.</b> The run's
    /// terminal registry status depends on it: a run stopped by <c>cancel_run</c> is recorded as
    /// <see cref="RunRegistryStatus.Cancelled"/>, and one stopped by its own timeout budget or by the
    /// MCP caller's token stays <see cref="RunRegistryStatus.Completed"/> — the distinction
    /// <c>RunRegistryEntry</c> has declared since US-S3-01 and that this story makes reachable.
    /// Remains readable after <see cref="IDisposable.Dispose"/>, because the holder reads it while
    /// writing that terminal status.
    /// </remarks>
    bool CancellationRequested { get; }

    /// <summary>
    /// The <c>reason</c> the first cancellation request carried, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>WRITE-ONLY in this build, and that is stated rather than implied</b> (a security review's
    /// INFO finding: the previous wording, "kept for the holder's own progress narration", described
    /// a use no code makes). Nothing reads this property outside
    /// <c>CancelRunOrchestratorTests</c> — the run's holder does not narrate it, because
    /// <c>run_suite</c>'s progress channel would then carry caller free text that this server did not
    /// author. It is retained because it is the only place a cancellation's WHY exists at all, and a
    /// future story that wants to narrate or log it needs the value captured at the moment it was
    /// supplied rather than reconstructed afterwards.
    /// <para>
    /// What it must never become: it is caller-supplied free text, so it is never written into the
    /// run registry (plan §2.7 invariant 4 — the registry stores run metadata, and a reason string is
    /// neither bounded by that model nor part of it) and never echoed into another tool's result. The
    /// FIRST request wins: a second <c>cancel_run</c> call for the same run does not rewrite the
    /// reason the run is already stopping for.
    /// </para>
    /// </remarks>
    string? CancellationReason { get; }
}

/// <summary>
/// The one <see cref="IRunCancellationRegistry"/> implementation: a concurrent map, live only in this
/// process's memory, holding at most one entry per in-flight run.
/// </summary>
/// <remarks>
/// <para>
/// <b>At most one entry in practice, and unbounded by construction is fine.</b> Single-flight
/// (<c>RunSuiteOrchestrator</c>'s interlocked claim, plus US-S3-04's cross-process lock) means one
/// run per server at a time, so this map holds zero or one entry — but nothing here DEPENDS on that,
/// because every entry is removed by its own scope's disposal on every path a run can end on
/// (completion, cancellation, timeout, and the exception arm), so the map cannot grow even if
/// single-flight were relaxed.
/// </para>
/// <para>
/// <b>Thread safety: a <see cref="ConcurrentDictionary{TKey, TValue}"/> plus a per-entry gate.</b>
/// <c>cancel_run</c> runs on a different thread from the <c>run_suite</c> call it is cancelling —
/// that is the entire point — so the dictionary handles publish/unpublish and the entry's own lock
/// handles the one genuinely racy pair: signalling the source, and the holder closing the entry
/// before it disposes that source. Closing takes the same lock, so once
/// <see cref="Scope.Dispose"/> has returned, no <see cref="TryRequestCancellation"/> can still be
/// inside <see cref="CancellationTokenSource.Cancel()"/> — which is what makes disposing the source
/// immediately afterwards safe, rather than merely likely to be.
/// </para>
/// <para>
/// <b><see cref="CancellationTokenSource.Cancel()"/> is called while that lock is held</b>, which
/// invokes the token's registered callbacks synchronously on this thread. That is deliberate and
/// bounded: the only callbacks on this particular source's token are the framework's own linked-token
/// propagation (<c>RunSuiteOrchestrator</c> links it into the call budget) and whatever
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>/process-wait registrations the run has
/// outstanding. Nothing in this server registers a callback that re-enters this type, so the lock
/// cannot be taken recursively; the alternative — cancelling outside the lock — would reopen exactly
/// the use-after-dispose window the lock exists to close.
/// <para>
/// <b>The residual is named rather than left implicit</b> (a security review's INFO finding). Running
/// arbitrary callbacks under a lock has two failure modes this design tolerates only because nothing
/// exercises them: a callback that blocks would hold the gate — and therefore stall the holder's own
/// <see cref="Scope.Dispose"/> — for as long as it blocked; and a callback that THROWS makes
/// <see cref="CancellationTokenSource.Cancel()"/> raise an
/// <see cref="AggregateException"/>, which would escape <c>cancel_run</c> uncoded, as a framework
/// exception with no <c>VFX-</c> code, from inside a <c>lock</c>. Both are unreachable today for one
/// structural reason: <b>nothing in <c>src/</c> registers a callback on a cancellation token at
/// all</b> — <c>RunCancellationSourceGuardTests</c> is what keeps that true, so the day a
/// registration appears, that test fails and this paragraph is what the author is sent to read.
/// (The framework's own linked-source propagation is not a user callback and cannot throw
/// user code.)
/// </para>
/// </para>
/// </remarks>
public sealed class InProcessRunCancellations : IRunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, Scope> _live = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IRunCancellationScope Register(string runId, CancellationTokenSource stopSignal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(stopSignal);

        var scope = new Scope(this, runId, stopSignal);

        // AddOrUpdate rather than TryAdd: a duplicate run id cannot happen (the registry mints a GUID
        // per run) and an in-flight duplicate cannot happen either (single-flight), so the only way to
        // reach a collision is a bug — and the safe behaviour for one is that the NEWEST run is the
        // one cancel_run can reach, never a stale registration shadowing it forever.
        _live[runId] = scope;
        return scope;
    }

    /// <inheritdoc />
    public bool TryRequestCancellation(string runId, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return _live.TryGetValue(runId, out var scope) && scope.TryRequest(reason);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ConcurrentDictionary{TKey, TValue}.IsEmpty"/> rather than
    /// <c>Count == 0</c>: the former is a lock-free read of the bucket state, while <c>Count</c>
    /// takes every lock in the table. The answer is a racy snapshot either way — a run can start or
    /// end in the instant after it is read — and the caller's message is worded to be true of a
    /// snapshot rather than of an invariant.
    /// </remarks>
    public bool AnyRunIsHeldHere() => !_live.IsEmpty;

    private void Unpublish(string runId, Scope scope) =>
        // The value is compared as well as the key, so a scope disposed LATE can never unpublish a
        // different, live registration that replaced it under the same id.
        ((System.Collections.Generic.ICollection<KeyValuePair<string, Scope>>)_live)
            .Remove(new KeyValuePair<string, Scope>(runId, scope));

    private sealed class Scope : IRunCancellationScope
    {
        private readonly InProcessRunCancellations _owner;
        private readonly string _runId;
        private readonly CancellationTokenSource _stopSignal;
        private readonly object _gate = new();
        private bool _closed;

        public Scope(InProcessRunCancellations owner, string runId, CancellationTokenSource stopSignal)
        {
            _owner = owner;
            _runId = runId;
            _stopSignal = stopSignal;
        }

        public bool CancellationRequested { get; private set; }

        public string? CancellationReason { get; private set; }

        public bool TryRequest(string? reason)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    // The run finished between this caller reading the map and reaching here. Reported
                    // as "not held", so cancel_run re-reads the registry and answers about what the run
                    // actually did rather than claiming a cancellation that changed nothing.
                    return false;
                }

                CancellationRequested = true;
                CancellationReason ??= reason;

                // Inside the lock — see this type's remarks for why, and for why that is safe here.
                _stopSignal.Cancel();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
            }

            _owner.Unpublish(_runId, this);
        }
    }
}
