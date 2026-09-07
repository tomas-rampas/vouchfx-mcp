// Vouchfx.Mcp.Tests.StdinEofChildFixture — a tiny, purpose-built child-process fixture used ONLY
// by VouchfxCliSuiteRunnerTests (todo 17, graceful teardown) to exercise the real graceful-stop-
// then-force-kill sequence against a REAL, spawnable, cross-platform OS process, without depending
// on the actual `vouchfx` CLI (or Docker) being installed on the machine running the test suite —
// VouchfxCliSuiteRunner always resolves and spawns "vouchfx" specifically (VouchfxCliPathResolver),
// so it cannot itself be redirected at a fake executable; this fixture instead stands in for it one
// layer down, at the "already-started child Process" seam VouchfxCliSuiteRunner.RunAgainstProcessAsync
// exposes internally to tests.
//
// Deliberately a SEPARATE, minimal console app — never a hidden mode bolted onto the shipped
// Vouchfx.Mcp tool itself: this exists purely for test scaffolding. tests/Directory.Build.props
// keeps it IsPackable=false, exactly like Vouchfx.Mcp.Tests, so it can never end up in the packaged
// vouchfx-mcp dotnet tool nupkg.
//
// Two behaviours, selected by args[0]:
//
//   graceful <delayMs>   Blocks reading stdin to its end — trapping the EOF that
//                        VouchfxCliSuiteRunner's graceful-stop step produces by closing the
//                        child's redirected stdin — then sleeps <delayMs> (simulating the engine's
//                        own DCP/Testcontainers teardown taking some time) before writing
//                        GracefulExitMarker to stdout and exiting 0. Models an engine started with
//                        --shutdown-on-stdin-eof completing its teardown inside the grace period.
//
//   ignore               Never reads stdin at all, and blocks until externally killed. Models an
//                        engine that does not understand --shutdown-on-stdin-eof (an older CLI) or
//                        whose teardown hangs past its own internal backstop, so the ONLY way it
//                        ever stops is the force-kill fallback.
//
// BOTH modes additionally arm a hard self-terminate deadline (SelfTerminateDeadline). That is not
// part of the behaviour being modelled — it is a backstop for the case where the PARENT dies without
// killing this process, which orphans an "ignore" child forever and leaves it holding a lock on the
// build output. Measured: one was found alive fifteen minutes after its run. See that field.

using System.Threading;

namespace Vouchfx.Mcp.Tests.StdinEofChildFixture;

public static class Program
{
    /// <summary>
    /// Written to stdout (and flushed) immediately before a "graceful" child exits on its own.
    /// Tests assert this marker WAS relayed to prove the child exited gracefully (never force-killed
    /// before it had a chance to run), or was NEVER relayed to prove the opposite for an "ignore"
    /// child that could only ever have been force-killed.
    /// </summary>
    public const string GracefulExitMarker = "STDIN-EOF-CHILD-GRACEFUL-EXIT";

    /// <summary>
    /// A hard, unconditional self-terminate deadline. <b>The parent is expected to kill this process
    /// long before it — this is the backstop for when the parent CANNOT.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists (measured).</b> A reviewer hit a build lock from a leaked
    /// <c>Vouchfx.Mcp.Tests.StdinEofChildFixture.dll ignore</c> process still alive fifteen minutes
    /// after it started. The <c>ignore</c> mode blocks on <c>Thread.Sleep(Timeout.Infinite)</c> by
    /// design, so its ONLY exit is an external kill — which means that if the test host dies before
    /// issuing that kill (a crash, a CI cancellation, a developer stopping the run), the child is
    /// orphaned forever and keeps a file lock on the build output.
    /// </para>
    /// <para>
    /// <b>No amount of test-side cleanup can close this</b>: cleanup code cannot run in a process that
    /// has already died. The only party that can guarantee this process ends is this process, so the
    /// deadline lives here.
    /// </para>
    /// <para>
    /// <b>Two minutes cannot weaken any assertion.</b> Every test that spawns this fixture bounds
    /// itself in milliseconds — the longest grace period any of them uses is one second — so the
    /// parent's force-kill always wins by two orders of magnitude. If this deadline is ever the thing
    /// that ends the process, a test has already failed for its own reasons.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SelfTerminateDeadline = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Exit code used when <see cref="SelfTerminateDeadline"/> fires — distinct from every other exit
    /// path so a confused test never reads a watchdog exit as a cooperative one.
    /// </summary>
    public const int SelfTerminatedExitCode = 87;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: <graceful <delayMs>|ignore>");
            return 1;
        }

        StartSelfTerminateWatchdog();

        switch (args[0])
        {
            case "graceful":
                return RunGraceful(args);

            case "ignore":
                // Deliberately never reads stdin at all, and blocks forever: the only way this
                // process ever ends is an external kill (VouchfxCliSuiteRunner's force-kill
                // fallback) — there is no cooperative exit path here at all, by design.
                Thread.Sleep(Timeout.Infinite);
                return 0; // Unreachable — Thread.Sleep(Infinite) never returns.

            default:
                Console.Error.WriteLine($"Unknown behaviour '{args[0]}'.");
                return 1;
        }
    }

    /// <summary>
    /// Arms the deadline on a background thread — see <see cref="SelfTerminateDeadline"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Environment.Exit(int)"/>, not a cooperative signal</b>, because the mode this
    /// exists for has no cooperative path at all: <c>ignore</c> is blocked inside
    /// <c>Thread.Sleep(Timeout.Infinite)</c>, which nothing short of process termination interrupts.
    /// </para>
    /// <para>
    /// <b>A background thread rather than a <see cref="System.Threading.Timer"/></b>: a timer is
    /// eligible for collection once nothing references it, and the only reference here would be a
    /// local in a method that has already returned. An <c>IsBackground</c> thread also cannot itself
    /// keep the process alive, so on every ordinary path this costs one sleeping thread and changes
    /// nothing about when the process exits.
    /// </para>
    /// <para>
    /// It writes to stderr, never stdout: stdout is the channel the tests relay and assert on, and a
    /// watchdog line there could be mistaken for fixture output.
    /// </para>
    /// </remarks>
    private static void StartSelfTerminateWatchdog()
    {
        var watchdog = new Thread(static () =>
        {
            Thread.Sleep(SelfTerminateDeadline);

            Console.Error.WriteLine(
                $"stdin-eof child fixture self-terminating after {SelfTerminateDeadline.TotalMinutes:N0} "
                + "minute(s): its parent never killed it, which means the parent died first. This is a "
                + "backstop against orphaned fixture processes holding a lock on the build output.");
            Console.Error.Flush();

            Environment.Exit(SelfTerminatedExitCode);
        })
        {
            IsBackground = true,
            Name = "stdin-eof-child-fixture-watchdog",
        };

        watchdog.Start();
    }

    private static int RunGraceful(string[] args)
    {
        var delayMs = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 0;

        // Blocks until the parent closes the write end of this process's redirected stdin — the
        // exact EOF signal VouchfxCliSuiteRunner's graceful-stop step produces by calling
        // Process.StandardInput.Close() on its side of the pipe.
        Console.In.ReadToEnd();

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        Console.Out.WriteLine(GracefulExitMarker);
        Console.Out.Flush();
        return 0;
    }
}
