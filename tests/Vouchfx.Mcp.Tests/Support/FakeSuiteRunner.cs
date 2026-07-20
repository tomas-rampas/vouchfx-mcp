using Vouchfx.Mcp;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A scriptable <see cref="ISuiteRunner"/> for tests: no real process is ever spawned, so no test
/// depends on the real <c>vouchfx</c> CLI or Docker being installed on the machine running it
/// (REQ-006's whole point in making <see cref="ISuiteRunner"/> an injectable seam) — mirrors
/// <see cref="FakeVouchfxCli"/>'s exact role one layer up.
/// </summary>
internal sealed class FakeSuiteRunner : ISuiteRunner
{
    private readonly Func<SuiteRunSpec, Action<string>, CancellationToken, Task<SuiteProcessResult>> _behaviour;

    private FakeSuiteRunner(Func<SuiteRunSpec, Action<string>, CancellationToken, Task<SuiteProcessResult>> behaviour) =>
        _behaviour = behaviour;

    /// <summary>How many times <see cref="RunAsync"/> was called — proves a gate rejected a call BEFORE ever reaching the runner.</summary>
    public int InvocationCount { get; private set; }

    /// <summary>
    /// A fake that gate-only tests can pass to <see cref="McpTestHarness.StartAsync"/> when they
    /// never expect a run to actually be attempted — throws if it ever is, so a gate regression
    /// that let a call fall through is caught immediately rather than silently returning a
    /// plausible-looking result.
    /// </summary>
    public static FakeSuiteRunner NeverExpectedToRun() =>
        new((_, _, _) => throw new InvalidOperationException(
            "FakeSuiteRunner.NeverExpectedToRun was invoked — a gate that should have rejected this " +
            "call before reaching the runner did not."));

    /// <summary>
    /// A fake that relays <paramref name="outputLines"/> (in order), writes
    /// <paramref name="eventsFileContent"/> to <see cref="SuiteRunSpec.EventsFilePath"/>, and
    /// completes normally with <paramref name="exitCode"/>.
    /// </summary>
    public static FakeSuiteRunner Succeeding(
        IReadOnlyList<string> outputLines, string eventsFileContent, int exitCode, string? stderrExcerpt = null) =>
        new(async (spec, onOutputLine, cancellationToken) =>
        {
            foreach (var line in outputLines)
            {
                onOutputLine(line);
            }

            await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            return new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, stderrExcerpt);
        });

    /// <summary>
    /// A fake that completes normally with <paramref name="exitCode"/> and NO events file at all
    /// (simulates the CLI failing before it could ever write one — EDGE-001's early-crash case),
    /// optionally carrying a captured <paramref name="stderrExcerpt"/> for the fallback classifier.
    /// </summary>
    public static FakeSuiteRunner FailingBeforeAnyEvents(int exitCode, string? stderrExcerpt = null) =>
        new((_, _, _) => Task.FromResult(new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, stderrExcerpt)));

    /// <summary>
    /// A fake that never completes until <paramref name="gate"/> is completed — lets a test observe
    /// the state WHILE a run is in progress (e.g. proving a second concurrent call is rejected)
    /// before releasing the first.
    /// </summary>
    public static FakeSuiteRunner Blocking(TaskCompletionSource<SuiteProcessResult> gate) =>
        new((_, _, _) => gate.Task);

    /// <summary>
    /// A fake that waits for <paramref name="cancellationToken"/> to fire, invokes
    /// <paramref name="onStopRequested"/>, takes <paramref name="simulatedStopDelay"/> to actually
    /// stop (standing in for the production runner's force-kill-then-confirm sequence — see
    /// <see cref="VouchfxCliSuiteRunner"/>'s remarks: there is no separate "graceful" phase in the
    /// real implementation, only an immediate whole-tree kill and a bounded exit confirmation), then
    /// reports <see cref="RunTermination.Aborted"/>. Deliberately SHORT and test-scale so EDGE-002's
    /// overall contract — the orchestrator does not race ahead of the runner's own stop, and the
    /// eventual result is cancelled/timed-out, never Fail — can be proven quickly.
    /// </summary>
    public static FakeSuiteRunner ObservingCancellation(TimeSpan simulatedStopDelay, Action onStopRequested) =>
        new(async (_, _, cancellationToken) =>
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await cancelled.Task;

            onStopRequested();
            await Task.Delay(simulatedStopDelay, CancellationToken.None);

            return new SuiteProcessResult(null, RunTermination.Aborted);
        });

    /// <summary>
    /// A fake that simulates the exact BLOCKER a review found in <see cref="VouchfxCliSuiteRunner"/>:
    /// a relay task that NEVER reaches EOF (e.g. a surviving grandchild process holding a redirected
    /// pipe's write handle open forever). Starts such a task but — mirroring the FIX, not the bug —
    /// never awaits it: it is immediately abandoned via <see cref="BoundedStreamReader.ObserveQuietly"/>,
    /// the exact same call the real runner now makes, so this fake's own <see cref="RunAsync"/>
    /// returns promptly regardless. If either this fake or the orchestrator wrapping it incorrectly
    /// awaited the never-completing task instead, the test using this would hang and time out rather
    /// than fail cleanly.
    /// </summary>
    public static FakeSuiteRunner WithNeverCompletingRelay(string eventsFileContent, int exitCode) =>
        new(async (spec, _, cancellationToken) =>
        {
            var neverCompletingRelay = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            BoundedStreamReader.ObserveQuietly(neverCompletingRelay);

            await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            return new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, StderrExcerpt: null);
        });

    public Task<SuiteProcessResult> RunAsync(SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        InvocationCount++;
        return _behaviour(spec, onOutputLine, cancellationToken);
    }
}
