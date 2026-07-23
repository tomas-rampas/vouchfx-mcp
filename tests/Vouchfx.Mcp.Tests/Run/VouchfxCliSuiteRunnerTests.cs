using System.Collections.Concurrent;
using System.Diagnostics;
using Vouchfx.Mcp.Run;

// A plain `using Vouchfx.Mcp.Tests.StdinEofChildFixture;` is NOT enough to bring the fixture's
// `Program` type into unqualified scope here: Vouchfx.Mcp itself (referenced transitively) defines
// its OWN top-level-statement-synthesised `Program` class in the GLOBAL namespace, and C#'s name
// resolution finds a same-named type via an ENCLOSING namespace (the global namespace is an
// enclosing namespace of every namespace, including this file's `Vouchfx.Mcp.Tests.Run`) before it
// ever considers a `using namespace;` import — so an unqualified `Program` would silently bind to
// THAT type instead, failing to compile against `GracefulExitMarker`. A `using` ALIAS sidesteps this
// entirely: the alias identifier itself is unique, so there is nothing for it to be shadowed by.
using StdinEofChildFixtureProgram = Vouchfx.Mcp.Tests.StdinEofChildFixture.Program;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// Covers <see cref="VouchfxCliSuiteRunner"/>'s own graceful-stop-then-force-kill sequence (todo 17,
/// graceful teardown) directly, against a REAL spawned child process — the tiny
/// <c>Vouchfx.Mcp.Tests.StdinEofChildFixture</c> console app built alongside this test project —
/// rather than the real <c>vouchfx</c> CLI, which cannot be assumed installed here (mirrors every
/// other <c>Real*ProcessTests</c> class's own "spawn a real, built binary" pattern in this test
/// suite, one layer down at the process-lifecycle seam <see cref="VouchfxCliSuiteRunner.RunAgainstProcessAsync"/>
/// exposes internally to tests).
/// </summary>
/// <remarks>
/// <see cref="VouchfxCliSuiteRunner.RunAsync"/> itself always resolves and spawns "vouchfx"
/// specifically (<c>VouchfxCliPathResolver</c>), so it cannot be redirected at a fake executable —
/// that is exactly why <c>RunAgainstProcessAsync</c> exists as a separate, internally-visible seam:
/// it is the SAME code <see cref="VouchfxCliSuiteRunner.RunAsync"/> delegates to once a process has
/// been started, parameterised by an injectable grace period so these tests can prove its real
/// behaviour quickly rather than needing production's real ~35-second bound.
/// </remarks>
public class VouchfxCliSuiteRunnerTests
{
    private static readonly string FixtureDllPath = RepoLayout.ResolveStdinEofChildFixtureDllPath();

    // ── BuildArguments: --shutdown-on-stdin-eof, and every pre-existing flag, never regressed ─────

    [Fact]
    public void BuildArguments_IncludesShutdownOnStdinEofAlongsideEveryOtherAlwaysPassedFlag()
    {
        var spec = new SuiteRunSpec("suite.e2e.yaml", ["smoke", "nightly"], "events.jsonl");

        var arguments = VouchfxCliSuiteRunner.BuildArguments(spec);

        Assert.Equal(
            [
                "run",
                "suite.e2e.yaml",
                "--events",
                "events.jsonl",
                "--fail-on-env-error",
                "--fail-on-inconclusive",
                "--no-decorations",
                "--no-telemetry",
                "--shutdown-on-stdin-eof",
                "--tag",
                "smoke",
                "--tag",
                "nightly",
            ],
            arguments);
    }

    [Fact]
    public void BuildArguments_NoTags_StillIncludesShutdownOnStdinEofAndOmitsAnyTagFlags()
    {
        var spec = new SuiteRunSpec("suite.e2e.yaml", [], "events.jsonl");

        var arguments = VouchfxCliSuiteRunner.BuildArguments(spec);

        Assert.Contains("--shutdown-on-stdin-eof", arguments);
        Assert.DoesNotContain("--tag", arguments);
    }

    // ── GracefulShutdownGrace: pin the documented, recommended value ────────────────────────────────

    [Fact]
    public void GracefulShutdownGrace_Is35Seconds()
    {
        // 30s (the engine's own documented internal teardown budget) + a 5s margin — see the
        // constant's own remarks for the full rationale.
        //
        // INVARIANT (captured here for maintainability): The literal 35s value is only a PROXY.
        // The REAL invariant is GracefulShutdownGrace > engine's teardown budget (~30s), ensuring
        // the engine gets a genuine chance to run its teardown to completion (or hit its own
        // internal ShutdownBackstop) before the MCP force-kills instead. This unit test CANNOT
        // verify the relationship directly because the engine's teardown budget is not exposed to
        // the MCP at runtime.
        //
        // THEREFORE: The graceful-teardown LIVE DRILL (see docs/validation/graceful-teardown-drill.md)
        // run on every ENGINE_PIN bump (see ENGINE_PIN's "Steps:" checklist) is the ENFORCING GATE
        // for this relationship. If the engine ever advances its own teardown budget past 35s, the
        // drill will catch the divergence empirically (engine fails to self-exit within grace, MCP
        // force-kills, topology is orphaned). If the engine ever EXPOSES its teardown budget at
        // runtime, this test should be upgraded to assert the relationship directly and the drill
        // can be demoted from mandatory to advisory.
        Assert.Equal(TimeSpan.FromSeconds(35), VouchfxCliSuiteRunner.GracefulShutdownGrace);
    }

    // ── RunAgainstProcessAsync: the graceful path ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAgainstProcessAsync_GracefulChild_ClosesStdinAndExitsWithinGrace_NoForceKillNeeded()
    {
        // The child sleeps 300ms after observing stdin EOF before writing its marker and exiting —
        // comfortably inside the 5-second grace this test injects, so the graceful path alone must
        // account for the child stopping (a force-kill would never let the child reach its own
        // Console.Out.WriteLine at all).
        using var process = StartFixture("graceful", "300");
        var lines = new ConcurrentQueue<string>();

        // Cancels almost immediately — well before the child's own 300ms delay elapses — so
        // RunAgainstProcessAsync's WaitForExitAsync(cancellationToken) reliably throws
        // OperationCanceledException and takes the graceful-stop-then-force-kill branch, rather than
        // racing the child's own unforced exit.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var result = await VouchfxCliSuiteRunner.RunAgainstProcessAsync(
            process, line => lines.Enqueue(line), TimeSpan.FromSeconds(5), cts.Token);
        stopwatch.Stop();

        Assert.Equal(RunTermination.Aborted, result.Termination);

        // The load-bearing proof that the GRACEFUL path — not the force-kill fallback — is what
        // actually stopped the child: force-killing would end the child before it could ever reach
        // its own Console.Out.WriteLine/Flush, so the marker's presence proves the child was left to
        // exit cooperatively. Waited for with a short, generous, bounded poll: the relay task that
        // would capture this line is deliberately NOT awaited by RunAgainstProcessAsync on the abort
        // path (see its own remarks) — it is abandoned in the background instead, and normally
        // finishes within milliseconds since the pipe closes the instant the child itself exits.
        await WaitUntilAsync(
            () => lines.Contains(StdinEofChildFixtureProgram.GracefulExitMarker),
            TimeSpan.FromSeconds(5));

        // Well under the 5-second grace: the child stopped on its own almost immediately (50ms
        // cancellation delay + 300ms simulated teardown + scheduling overhead), proving the method
        // did not wait out the full grace before observing the exit. 5s (not a tighter bound) for
        // symmetry with the generously-sized force-kill-path bounds below (grace+10s / grace-100ms):
        // this window also has to absorb the fixture's own `dotnet <dll>` runtime cold-start (the
        // child only reaches Console.In.ReadToEnd() once the runtime has warmed up), which a tighter
        // bound would risk flaking on a cold or loaded CI agent.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected the graceful child to be observed as exited well within the grace period; took {stopwatch.Elapsed}.");
    }

    // ── RunAgainstProcessAsync: the force-kill fallback ─────────────────────────────────────────────

    [Fact]
    public async Task RunAgainstProcessAsync_IgnoringChild_ForceKillsAtGraceDeadlineAndReturnsPromptly()
    {
        using var process = StartFixture("ignore");
        var lines = new ConcurrentQueue<string>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var grace = TimeSpan.FromSeconds(1);

        var stopwatch = Stopwatch.StartNew();
        var result = await VouchfxCliSuiteRunner.RunAgainstProcessAsync(
            process, line => lines.Enqueue(line), grace, cts.Token);
        stopwatch.Stop();

        Assert.Equal(RunTermination.Aborted, result.Termination);

        // The child never reads stdin, so closing it is a no-op it never observes — the ONLY way it
        // ever ends is the force-kill fallback. It follows the marker it would only ever write AFTER
        // observing stdin EOF can never have been relayed.
        Assert.DoesNotContain(StdinEofChildFixtureProgram.GracefulExitMarker, lines);

        // Bounded: the method waited (at least roughly) the full grace period before giving up and
        // force-killing — proving the grace was genuinely honoured, not skipped — but still returned
        // promptly overall (grace + kill-confirmation overhead, nowhere near indefinite). A generous
        // upper bound absorbs CI/OS scheduling jitter without weakening the "it does not hang" proof.
        Assert.True(
            stopwatch.Elapsed >= grace - TimeSpan.FromMilliseconds(100),
            $"Expected the method to wait out (approximately) the full {grace} grace period before force-killing; took {stopwatch.Elapsed}.");
        Assert.True(
            stopwatch.Elapsed < grace + TimeSpan.FromSeconds(10),
            $"Expected RunAgainstProcessAsync to return promptly (bounded) even against a child that never cooperates; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAgainstProcessAsync_IgnoringChild_ProcessIsActuallyDeadAfterForceKill()
    {
        var process = StartFixture("ignore");
        var processId = process.Id;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await VouchfxCliSuiteRunner.RunAgainstProcessAsync(
            process, _ => { }, TimeSpan.FromMilliseconds(300), cts.Token);

        // RunAgainstProcessAsync disposes the Process object it was given (`using (process)`), so
        // the real proof the OS process itself is gone (not merely that this method returned) is
        // asking the OS directly by PID, independent of the disposed wrapper.
        var stillRunning = IsProcessStillRunning(processId);
        Assert.False(stillRunning, $"Expected process {processId} to have been force-killed, but it is still running.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static Process StartFixture(params string[] fixtureArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(FixtureDllPath);
        foreach (var arg in fixtureArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the stdin-EOF child fixture process.");
    }

    private static bool IsProcessStillRunning(int processId)
    {
        try
        {
            using var byPid = Process.GetProcessById(processId);
            return !byPid.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when no process with that id exists (already reaped by the OS) —
            // exactly the "definitely gone" case this helper reports.
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }
}
