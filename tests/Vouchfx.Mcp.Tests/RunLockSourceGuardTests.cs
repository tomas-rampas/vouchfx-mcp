using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for US-S3-04's "read-only tools stay lock-free" rule (spec §4.6:
/// "read-only tools are safe to call concurrently"): <see cref="Vouchfx.Mcp.Run.IRunLock"/> has
/// exactly ONE call site in <c>src/</c>, and it is <c>run_suite</c>'s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs a test, and why now.</b> The lock claim is about STARTING a run, never about
/// reading one — <see cref="Vouchfx.Mcp.Run.IRunLock"/>'s own remarks say so, and every read-side
/// tool (<c>explain_run</c>, <c>diagnose_run</c>, and since US-S3-05 <c>get_run_events</c>) depends
/// on it: a read that took the lock would be refused with <c>VFX-E-1501</c> while a run was in
/// flight, which is exactly when a host most wants to read. Nothing enforced that but the prose,
/// and prose does not fail a build. US-S3-05 is the first story to add a new read-side tool over the
/// run registry since the lock landed, which makes it the right moment.
/// </para>
/// <para>
/// <b>Mirrors <see cref="ReadOnlySourceGuardTests"/>'s shape exactly</b> — a whitespace-tolerant
/// regex over source with comments and string literals stripped, and a fail-closed EXACT-equality
/// check against a named set, so a new call site fails by name and a stale entry cannot rot. It is
/// a boundary on WHERE such code may live, not an audit of what it does: a second
/// <c>TryAcquire</c> added inside <c>RunSuiteOrchestrator</c> would still pass, which is why the
/// count is asserted as well as the file set.
/// </para>
/// </remarks>
public class RunLockSourceGuardTests
{
    /// <summary>
    /// The only file in <c>src/</c> allowed to CALL <c>TryAcquire</c>, and the only number of times
    /// it may. <c>RunSuiteOrchestrator</c> claims the workspace for the duration of one run; nothing
    /// else in this server starts a run, so nothing else has a claim to make.
    /// </summary>
    private const string GuardedCallSiteRelativePath = "src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs";

    /// <summary>Exactly one claim per run — the acquire in <c>RunSuiteOrchestrator</c>'s single-flight gate.</summary>
    private const int ExpectedCallSiteCount = 1;

    /// <summary>
    /// An INVOCATION of <c>TryAcquire</c>: a receiver, a dot, the name, an open paren. Declarations
    /// (<c>RunLockResult TryAcquire()</c> in <c>IRunLock</c> and <c>WorkspaceRunLock</c>) carry no
    /// leading dot and so are deliberately not matched — this guard is about who CALLS the lock, not
    /// about who defines it.
    /// </summary>
    private static readonly Regex TryAcquireInvocation = new(@"\.\s*TryAcquire\s*\(", RegexOptions.Compiled);

    [Fact]
    public void TheRunLock_HasExactlyOneCallSiteInSrc_SoReadOnlyToolsCannotTakeIt()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => TryAcquireInvocation.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([GuardedCallSiteRelativePath], actualSites);
    }

    [Fact]
    public void TheOneCallSite_ClaimsTheLockExactlyOnce()
    {
        var fullPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            GuardedCallSiteRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // Anti-vacuity: a renamed or deleted file would otherwise make this check pass over nothing.
        Assert.True(File.Exists(fullPath), $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

        var matches = TryAcquireInvocation.Matches(SourceGuardScan.ExecutableSourceOf(fullPath)).Count;

        Assert.True(
            matches == ExpectedCallSiteCount,
            $"'{GuardedCallSiteRelativePath}' claims the run lock {matches} time(s); exactly "
            + $"{ExpectedCallSiteCount} is expected. A second claim on one run's path is either a "
            + "double-acquire (which the same-process flag would refuse) or a second code path that "
            + "needs its own review.");
    }

    [Fact]
    public void TheInvocationPattern_MatchesCallsAndNotDeclarations()
    {
        // Sanity check for the regex above, because the whole guard is only as good as it is.
        Assert.Matches(TryAcquireInvocation, "var acquisition = _runLock.TryAcquire();");
        Assert.Matches(TryAcquireInvocation, "_runLock\n    .TryAcquire();");
        Assert.DoesNotMatch(TryAcquireInvocation, "RunLockResult TryAcquire();");
        Assert.DoesNotMatch(TryAcquireInvocation, "public RunLockResult TryAcquire()");
    }
}
