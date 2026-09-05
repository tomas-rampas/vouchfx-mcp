// Vouchfx.Mcp.Tests.RunLockHolderFixture — a tiny child-process fixture used ONLY by
// RealCrossProcessRunLockTests (US-S3-04) to be the OTHER PROCESS in a genuinely cross-process
// exclusivity test.
//
// Why a fixture rather than a second real vouchfx-mcp server: a server only holds the run lock while
// it is RUNNING A SUITE, and running a suite means the pinned `vouchfx` engine CLI and Docker —
// neither of which any test in this repo may depend on (CLAUDE.md). This process instead enters the
// exact state a mid-run server is in, using the SAME production types the server uses:
//
//   1. Acquire Vouchfx.Mcp.Run.WorkspaceRunLock on <outputDir>/.lock — the identical open flags,
//      because it is the identical type, not a copy of its flags.
//   2. Record a `running` entry through Vouchfx.Mcp.Run.FileRunRegistry — the same write, in the
//      same order the orchestrator performs it (claim first, record second), so the runId a rejected
//      server reports is the one a real holder would have produced.
//   3. Print "HELD <runId>" to stdout, flush, and block on stdin until EOF.
//   4. On EOF, print "RELEASED" and then let the `using` drop the claim — the marker that
//      distinguishes this exit from any other, and which the clean-release test asserts.
//
// The controlling test then either closes stdin (a clean release) or KILLS this process (the
// stale-lock case the acceptance criterion names). Killing it is the whole point: it is the only way
// to prove the claim is released by the OPERATING SYSTEM rather than by any code path here — and it
// is exactly why step 4's marker is asserted rather than merely printed: without it, a holder that
// died for an unrelated reason would satisfy the clean-release test too.
//
// Deliberately a SEPARATE, minimal console app, exactly like StdinEofChildFixture beside it — never
// a hidden mode bolted onto the shipped Vouchfx.Mcp tool, which already carries one hidden mode
// (--validate-worker) that exists because production needs it, not because a test does.

using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.RunLockHolderFixture;

public static class Program
{
    /// <summary>
    /// Written to stdout (and flushed) once the lock is held and the run is recorded, followed by a
    /// space and the run id. The controlling test waits for this line rather than for a delay, so
    /// the handshake is deterministic instead of a race against process startup.
    /// </summary>
    public const string HeldMarker = "RUN-LOCK-HOLDER-HELD";

    /// <summary>Written to stdout immediately before a cleanly-released holder exits 0.</summary>
    public const string ReleasedMarker = "RUN-LOCK-HOLDER-RELEASED";

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: <outputDirectory> <specPath>");
            return 1;
        }

        var outputDirectory = args[0];
        var specPath = args[1];

        // workspace: null — this fixture is pointed straight at an output directory the test created,
        // with no workspace root to contain it against. That is the same argument every unit test
        // passes; the containment check itself is covered by RunRegistryContainmentTests.
        var runLock = new WorkspaceRunLock(outputDirectory, workspace: null);

        if (runLock.TryAcquire() is not RunLockResult.Acquired acquired)
        {
            Console.Error.WriteLine("Could not acquire the run lock; another holder already has it.");
            return 2;
        }

        using (acquired.Release)
        {
            // Claim first, record second — the order RunSuiteOrchestrator uses, so a test that
            // rejects a second server observes exactly the state a real in-flight run produces.
            var entry = new FileRunRegistry(outputDirectory, workspace: null).StartRun([specPath]);

            Console.Out.WriteLine($"{HeldMarker} {entry.RunId}");
            Console.Out.Flush();

            // Blocks until the controlling test closes this process's stdin — or never returns,
            // because the test killed the process instead. Both are the point.
            while (Console.In.ReadLine() is not null)
            {
                // Any line is ignored; only EOF ends the hold.
            }

            Console.Out.WriteLine(ReleasedMarker);
            Console.Out.Flush();
        }

        return 0;
    }
}
