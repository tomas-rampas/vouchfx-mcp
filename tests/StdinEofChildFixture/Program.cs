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
//   ignore               Never reads stdin at all, and blocks forever until externally killed.
//                        Models an engine that does not understand --shutdown-on-stdin-eof (an
//                        older CLI) or whose teardown hangs past its own internal backstop, so the
//                        ONLY way it ever stops is the force-kill fallback.

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

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: <graceful <delayMs>|ignore>");
            return 1;
        }

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
