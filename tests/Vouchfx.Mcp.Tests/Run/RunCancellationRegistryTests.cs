using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="InProcessRunCancellations"/> at its own seam — the three behaviours its remarks argue
/// for and which <c>CancelRunOrchestratorTests</c> reaches only incidentally, if at all: the
/// closed-after-dispose gate, the key-AND-value compare in <c>Unpublish</c>, and
/// <see cref="InProcessRunCancellations.Register"/>'s replace-existing semantics.
/// </summary>
/// <remarks>
/// <b>Why these are worth their own class</b> (a gatekeeper review's MINOR finding). Each is a
/// deliberate, non-obvious choice whose whole justification lives in a comment: the gate is what makes
/// disposing the caller's <see cref="CancellationTokenSource"/> immediately after the scope SAFE
/// rather than merely usually-safe; the value compare is what stops a late-disposed scope unpublishing
/// a live registration that replaced it; and <c>AddOrUpdate</c>-over-<c>TryAdd</c> is what makes the
/// NEWEST run the reachable one if a duplicate id ever occurs. All three would survive a "simplifying"
/// edit with every other test in the repo still green — which is exactly the class of change a unit
/// test at this level exists to catch.
/// </remarks>
public class RunCancellationRegistryTests
{
    private const string RunId = "run-0123456789abcdef0123456789abcdef";

    // ── The closed-after-dispose gate ────────────────────────────────────────────────────────────

    /// <summary>
    /// Once the scope is disposed, a cancellation request finds nothing to signal and the source is
    /// never touched — which is the property that makes disposing that source next line safe.
    /// </summary>
    [Fact]
    public void TryRequestCancellation_AfterTheScopeIsDisposed_SignalsNothingAndReportsNotHeld()
    {
        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();

        var scope = cancellations.Register(RunId, stopSignal);
        scope.Dispose();

        Assert.False(cancellations.TryRequestCancellation(RunId, reason: null));
        Assert.False(stopSignal.IsCancellationRequested);
        Assert.False(scope.CancellationRequested);
    }

    /// <summary>
    /// What the <c>_closed</c> gate buys OVER the map removal, asserted the only way it can be:
    /// disposing the scope and then the source it published — the exact sequence
    /// <c>RunSuiteOrchestrator</c> performs — never lets a concurrent request reach a disposed
    /// <see cref="CancellationTokenSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stated plainly: the gate's extra guarantee is not deterministically observable, and this
    /// test does not pretend otherwise.</b> The test above proves a disposed scope answers "not held",
    /// but that answer would also arrive from the map removal alone, so it does not distinguish the
    /// two. The gate's actual job is narrower and is about ORDERING: because
    /// <c>Scope.Dispose</c> takes the same lock <c>TryRequest</c> holds while calling
    /// <see cref="CancellationTokenSource.Cancel()"/>, once <c>Dispose</c> has RETURNED no request can
    /// still be inside <c>Cancel()</c> — which is what makes the caller's next line,
    /// <c>cancelSignal.Dispose()</c>, safe rather than merely usually safe. Without it, a request that
    /// read the map an instant before disposal fires a source that is being disposed underneath it,
    /// and <see cref="ObjectDisposedException"/> escapes <c>cancel_run</c> uncoded.
    /// </para>
    /// <para>
    /// So this is a bounded STRESS, not a proof: it can only ever fail if the gate is removed, and
    /// even then only sometimes. It is kept because "sometimes" over a few thousand iterations is
    /// vastly more likely to be caught here than in production, and because the alternative — no
    /// coverage at all for the one line the design's safety argument rests on — is worse. It cannot
    /// produce a false failure: the assertion is the absence of an exception from correct code.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DisposingTheScopeAndThenItsSource_NeverLetsAConcurrentRequestTouchADisposedSource()
    {
        const int iterations = 2_000;

        for (var i = 0; i < iterations; i++)
        {
            var cancellations = new InProcessRunCancellations();
            var stopSignal = new CancellationTokenSource();
            var scope = cancellations.Register(RunId, stopSignal);

            var requester = Task.Run(() => cancellations.TryRequestCancellation(RunId, reason: null));

            // The production sequence, verbatim: unpublish, then dispose the source. Any
            // ObjectDisposedException would surface out of the awaited task below.
            scope.Dispose();
            stopSignal.Dispose();

            await requester;
        }
    }

    /// <summary>
    /// <see cref="IRunCancellationScope.CancellationRequested"/> stays readable after disposal — the
    /// holder reads it while writing the run's terminal status, which happens after the scope has
    /// been unpublished on some paths.
    /// </summary>
    [Fact]
    public void CancellationRequested_SurvivesDisposal_BecauseTheHolderReadsItWhileRecordingTheOutcome()
    {
        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();

        var scope = cancellations.Register(RunId, stopSignal);
        Assert.True(cancellations.TryRequestCancellation(RunId, "why"));

        scope.Dispose();

        Assert.True(scope.CancellationRequested);
        Assert.Equal("why", scope.CancellationReason);
    }

    // ── Unpublish compares the VALUE as well as the key ──────────────────────────────────────────

    /// <summary>
    /// A scope disposed LATE must not unpublish the live registration that replaced it under the same
    /// run id.
    /// </summary>
    /// <remarks>
    /// The whole reason <c>Unpublish</c> removes a <see cref="KeyValuePair{TKey, TValue}"/> rather
    /// than calling <c>TryRemove(key, out _)</c>. With a key-only removal this test's final assertion
    /// fails: the replacement would be silently unpublished by the older scope's disposal, and the run
    /// it belongs to would become uncancellable for the rest of its life while
    /// <c>get_run_status</c> still reported it as <c>running</c>.
    /// </remarks>
    [Fact]
    public void DisposingAScopeThatWasAlreadyReplaced_LeavesTheReplacementReachable()
    {
        var cancellations = new InProcessRunCancellations();
        using var firstSignal = new CancellationTokenSource();
        using var secondSignal = new CancellationTokenSource();

        var first = cancellations.Register(RunId, firstSignal);
        using var second = cancellations.Register(RunId, secondSignal);

        // The stale scope disposes AFTER the replacement was published — the ordering the value
        // compare exists for.
        first.Dispose();

        Assert.True(cancellations.TryRequestCancellation(RunId, reason: null));
        Assert.True(secondSignal.IsCancellationRequested);
        Assert.False(firstSignal.IsCancellationRequested);
    }

    // ── Register replaces rather than refusing ───────────────────────────────────────────────────

    /// <summary>
    /// Registering a second scope under an id that already has one makes the NEWEST reachable, never
    /// leaves the stale one shadowing it.
    /// </summary>
    /// <remarks>
    /// A duplicate id cannot occur in production — the registry mints a GUID per run and single-flight
    /// permits one in flight at a time — so this pins the behaviour chosen FOR THE BUG CASE:
    /// <c>AddOrUpdate</c> rather than <c>TryAdd</c>, because a stale registration winning would leave
    /// a genuinely running run permanently uncancellable, which is strictly worse than the alternative.
    /// </remarks>
    [Fact]
    public void Register_ForARunIdThatAlreadyHasAScope_MakesTheNewestOneTheReachableRegistration()
    {
        var cancellations = new InProcessRunCancellations();
        using var staleSignal = new CancellationTokenSource();
        using var freshSignal = new CancellationTokenSource();

        using var stale = cancellations.Register(RunId, staleSignal);
        using var fresh = cancellations.Register(RunId, freshSignal);

        Assert.True(cancellations.TryRequestCancellation(RunId, reason: null));

        Assert.True(fresh.CancellationRequested);
        Assert.True(freshSignal.IsCancellationRequested);
        Assert.False(stale.CancellationRequested);
        Assert.False(staleSignal.IsCancellationRequested);
    }

    // ── AnyRunIsHeldHere: the bit VFX-E-1507's message depends on ─────────────────────────────────

    /// <summary>
    /// The predicate <c>cancel_run</c> uses to tell "my own run holds the workspace lock" from "the
    /// lock genuinely belongs to somebody else" — false with nothing in flight, true while a
    /// registration is live, false again once it is disposed.
    /// </summary>
    [Fact]
    public void AnyRunIsHeldHere_TracksTheLifetimeOfARegistration()
    {
        var cancellations = new InProcessRunCancellations();
        Assert.False(cancellations.AnyRunIsHeldHere());

        using var stopSignal = new CancellationTokenSource();
        var scope = cancellations.Register(RunId, stopSignal);
        Assert.True(cancellations.AnyRunIsHeldHere());

        scope.Dispose();
        Assert.False(cancellations.AnyRunIsHeldHere());
    }

    /// <summary>
    /// A run that has been SIGNALLED is still held — the request does not unpublish it. The run keeps
    /// running until its own graceful stop completes, and it stays cancellable (idempotently) until
    /// then.
    /// </summary>
    [Fact]
    public void AnyRunIsHeldHere_StaysTrueAfterACancellationIsRequested_UntilTheScopeIsDisposed()
    {
        var cancellations = new InProcessRunCancellations();
        using var stopSignal = new CancellationTokenSource();
        using var scope = cancellations.Register(RunId, stopSignal);

        Assert.True(cancellations.TryRequestCancellation(RunId, reason: null));
        Assert.True(cancellations.AnyRunIsHeldHere());
    }
}
