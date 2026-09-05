using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// An <see cref="IRunRegistry"/> whose <see cref="StartRun"/> fails the way a real
/// <see cref="FileRunRegistry"/> fails on a read-only workspace root or an exhausted volume — the
/// seam <c>RunSuiteOrchestrator</c>'s <see cref="RunSuiteOutcome.RunNotRecorded"/> arm exists for.
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
/// </remarks>
internal sealed class UnwritableRunRegistry(Exception failure) : IRunRegistry
{
    /// <summary>A registry that fails its first write with a full-disk-shaped <see cref="IOException"/>.</summary>
    public static UnwritableRunRegistry WithDiskFull() =>
        new(new IOException("There is not enough space on the disk."));

    /// <summary>A registry that fails its first write the way a read-only root does.</summary>
    public static UnwritableRunRegistry WithAccessDenied() =>
        new(new UnauthorizedAccessException("Access to the path is denied."));

    public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
        throw failure;

    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
        throw new InvalidOperationException("No run was ever recorded, so nothing can transition.");

    public RunRegistryEntry? TryGetRun(string runId) => null;

    public IReadOnlyList<RunRegistryEntry> ListRuns() => [];

    public bool IsRecordedEventsFilePath(string eventsPath) => false;
}
