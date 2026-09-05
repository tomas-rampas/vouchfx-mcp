using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// An <see cref="IRunRegistry"/> whose writes fail the way a real <see cref="FileRunRegistry"/> fails
/// on a read-only workspace root or an exhausted volume — the seam
/// <c>RunSuiteOrchestrator</c>'s three guarded registry writes exist for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Injected rather than provoked, deliberately.</b> The alternative — pointing a real
/// <see cref="FileRunRegistry"/> at a genuinely unwritable directory — needs a different mechanism on
/// every platform this tool ships to (an ACL on Windows, a mode bit on Unix, neither reliable inside a
/// container running as root), and would make a taxonomy test depend on the CI runner's identity. The
/// behaviour under test is the orchestrator's, not the filesystem's: what matters is that an
/// <see cref="IOException"/> escaping <see cref="IRunRegistry.StartRun"/> becomes a catalogued
/// <c>VFX-E-1502</c> rather than an uncoded framework exception.
/// </para>
/// <para>
/// The exception TYPE is the caller's choice so a test can prove the whole caught family
/// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) lands on the same arm —
/// and, by exclusion, that an unrelated exception type still escapes as the bug it would be.
/// </para>
/// <para>
/// <b>WHICH write fails is the caller's choice too</b>, and that is what
/// <see cref="FailingOnTransitionOnly"/> adds. The two failures are not symmetric: a failed
/// <see cref="IRunRegistry.StartRun"/> means NOTHING RAN, so the caller is told (VFX-E-1502); a failed
/// completing transition happens after the engine has already produced a verdict, so the caller must
/// still be given that verdict and only the RECORD is lost. Proving the second needs a registry that
/// records the start normally and fails only afterwards — otherwise the run never gets far enough to
/// have a verdict to preserve.
/// </para>
/// </remarks>
internal sealed class UnwritableRunRegistry(Exception failure, bool failStartRun = true) : IRunRegistry
{
    /// <summary>
    /// The entries whose start DID succeed, so a test can assert what the record was left saying when
    /// the completing write failed. Only ever populated in the <see cref="FailingOnTransitionOnly"/>
    /// shape; empty otherwise, because nothing was ever recorded.
    /// </summary>
    private readonly InMemoryRunRegistry _started = new();

    /// <summary>A registry that fails its first write with a full-disk-shaped <see cref="IOException"/>.</summary>
    public static UnwritableRunRegistry WithDiskFull() =>
        new(new IOException("There is not enough space on the disk."));

    /// <summary>A registry that fails its first write the way a read-only root does.</summary>
    public static UnwritableRunRegistry WithAccessDenied() =>
        new(new UnauthorizedAccessException("Access to the path is denied."));

    /// <summary>
    /// A registry whose <see cref="StartRun"/> SUCCEEDS (delegated to a real
    /// <see cref="InMemoryRunRegistry"/>, so the run gets a genuine id and events path) and whose
    /// <see cref="RecordStatusTransition"/> then fails — the volume filling up, or the root being
    /// remounted read-only, between the start of a run and its completion.
    /// </summary>
    public static UnwritableRunRegistry FailingOnTransitionOnly() =>
        new(new IOException("There is not enough space on the disk."), failStartRun: false);

    /// <summary>How many times a transition was attempted — non-zero proves the guard was actually exercised.</summary>
    public int TransitionAttemptCount { get; private set; }

    public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
        failStartRun ? throw failure : _started.StartRun(specPaths, labels);

    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null)
    {
        TransitionAttemptCount++;

        throw failStartRun
            ? new InvalidOperationException("No run was ever recorded, so nothing can transition.")
            : failure;
    }

    public RunRegistryEntry? TryGetRun(string runId) => failStartRun ? null : _started.TryGetRun(runId);

    public IReadOnlyList<RunRegistryEntry> ListRuns() => failStartRun ? [] : _started.ListRuns();
}
