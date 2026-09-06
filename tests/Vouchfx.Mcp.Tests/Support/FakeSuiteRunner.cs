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
    /// Every <see cref="SuiteRunSpec"/> this fake was handed, in call order — the seam US-S3-02's
    /// multi-suite tests read to assert WHAT the orchestrator spawned (which suites, in which order,
    /// into which events-file paths), which a returned verdict alone cannot show.
    /// </summary>
    /// <remarks>
    /// Deliberately on the fake itself rather than in yet another wrapping runner: every factory
    /// below records into it, so any behaviour can be combined with the assertion. Appended from
    /// <see cref="RunAsync"/>, which the orchestrator only ever calls sequentially under its own
    /// single-flight claim, so no synchronisation is needed and none is implied.
    /// </remarks>
    public List<SuiteRunSpec> ObservedSpecs { get; } = [];

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
    /// <remarks>
    /// Each line is passed through <see cref="TextSanitiser.SanitiseForDisplay"/> before reaching
    /// <c>onOutputLine</c> — <see cref="ISuiteRunner.RunAsync"/>'s own contract documents that
    /// callback as receiving lines already sanitised for display (what the production
    /// <see cref="VouchfxCliSuiteRunner"/> does before every invocation), so this fake honours the
    /// same contract rather than passing raw text through unchanged. That way a test exercises the
    /// REAL contract a caller can rely on, and can never accidentally assert on unescaped output
    /// that the real runner would never actually produce.
    /// </remarks>
    public static FakeSuiteRunner Succeeding(
        IReadOnlyList<string> outputLines, string eventsFileContent, int exitCode, string? stderrExcerpt = null) =>
        new(async (spec, onOutputLine, cancellationToken) =>
        {
            foreach (var line in outputLines)
            {
                onOutputLine(TextSanitiser.SanitiseForDisplay(line));
            }

            await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            return new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, stderrExcerpt);
        });

    /// <summary>
    /// A fake that hands the <see cref="SuiteRunSpec"/> it was given to <paramref name="observe"/>
    /// before behaving like <see cref="Succeeding"/> with no relayed output and exit code 0 — for a
    /// test whose subject is WHAT THE ORCHESTRATOR PASSED DOWN rather than what came back.
    /// </summary>
    /// <remarks>
    /// Exists for US-S3-08's workspace-relative resolution: <c>run_suite</c> rebases a relative
    /// <c>path</c> onto the workspace root and must hand the REBASED path to the runner (which
    /// splices it into the engine CLI's argument list), not the raw one. Asserting on the spec is the
    /// only way to see that — a successful result would look identical either way.
    /// </remarks>
    public static FakeSuiteRunner Observing(Action<SuiteRunSpec> observe, string eventsFileContent) =>
        new(async (spec, _, cancellationToken) =>
        {
            observe(spec);

            await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            return new SuiteProcessResult(0, RunTermination.CompletedNormally, StderrExcerpt: null);
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
    /// A fake that throws <paramref name="failure"/> instead of returning — models a runner (or
    /// anything it calls) failing in a way <see cref="RunSuiteOrchestrator"/> does not anticipate.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NeverExpectedToRun"/>, whose throw is a TEST assertion ("a gate should
    /// have stopped this"). This one is the SUBJECT: US-S3-01's registry must not be left recording a
    /// phantom in-flight run when the runner blows up, and the caller must still receive the original
    /// exception rather than a bookkeeping failure that replaced it.
    /// </remarks>
    public static FakeSuiteRunner Throwing(Exception failure) =>
        new((_, _, _) => throw failure);

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

    /// <summary>
    /// A fake that scripts each suite INDIVIDUALLY (US-S3-02): <paramref name="script"/> is asked,
    /// per suite path, what that suite's events file should contain and what exit code it should
    /// report, and the answer is written to the spec's own events-file path.
    /// </summary>
    /// <remarks>
    /// A function of the PATH rather than a dictionary keyed by one: the paths a multi-suite test
    /// runs are temp-directory-scoped and workspace-rebased, so a test would otherwise have to
    /// reproduce the orchestrator's own resolution to key a dictionary correctly — matching on a file
    /// name suffix inside the script is both simpler and more honest about what the test knows.
    /// Returning <see langword="null"/> from the script means "this suite writes no events file at
    /// all" (the engine crashed before producing one), which is EDGE-001's early-crash shape.
    /// </remarks>
    public static FakeSuiteRunner PerSuite(Func<string, (string? EventsFileContent, int ExitCode)> script) =>
        new(async (spec, _, cancellationToken) =>
        {
            var (eventsFileContent, exitCode) = script(spec.SuitePath);
            if (eventsFileContent is not null)
            {
                await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            }

            return new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, StderrExcerpt: null);
        });

    /// <summary>
    /// A fake that behaves like <see cref="PerSuite"/> until <paramref name="abortAtInvocation"/>
    /// (one-based), at which point it waits for its own token and reports
    /// <see cref="RunTermination.Aborted"/> — models a multi-suite run whose budget runs out partway
    /// through, so the suites after it must be reported as never run.
    /// </summary>
    public static FakeSuiteRunner PerSuiteAbortingAt(
        int abortAtInvocation, Func<string, (string? EventsFileContent, int ExitCode)> script)
    {
        var invocation = 0;

        return new(async (spec, _, cancellationToken) =>
        {
            invocation++;
            if (invocation >= abortAtInvocation)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                await cancelled.Task;

                return new SuiteProcessResult(null, RunTermination.Aborted);
            }

            var (eventsFileContent, exitCode) = script(spec.SuitePath);
            if (eventsFileContent is not null)
            {
                await File.WriteAllTextAsync(spec.EventsFilePath, eventsFileContent, cancellationToken);
            }

            return new SuiteProcessResult(exitCode, RunTermination.CompletedNormally, StderrExcerpt: null);
        });
    }

    public Task<SuiteProcessResult> RunAsync(SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        InvocationCount++;
        ObservedSpecs.Add(spec);
        return _behaviour(spec, onOutputLine, cancellationToken);
    }
}
