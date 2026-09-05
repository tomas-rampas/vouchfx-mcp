using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for US-S3-04's "read-only tools stay lock-free" rule (spec §4.6:
/// "read-only tools are safe to call concurrently"): <see cref="Vouchfx.Mcp.Run.IRunLock"/> has
/// exactly TWO call sites in <c>src/</c> — <c>run_suite</c>'s claim and <c>cancel_run</c>'s probe —
/// and no read-only tool is among them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs a test.</b> The lock claim is about STARTING a run, never about reading one —
/// <see cref="Vouchfx.Mcp.Run.IRunLock"/>'s own remarks say so, and every read-side tool
/// (<c>explain_run</c>, <c>diagnose_run</c>, <c>get_run_events</c>, since US-S3-03
/// <c>get_run_status</c>/<c>list_runs</c>, and since US-S3-06 <c>get_step_timeline</c>) depends on it:
/// a read that took the lock would be refused
/// with <c>VFX-E-1501</c> while a run was in flight — which is exactly when a host most wants to
/// read — and, worse, could make a concurrent <c>run_suite</c> fail for the moment it held the
/// claim. Nothing enforced that but the prose, and prose does not fail a build. US-S3-05 was the
/// first story to add a new read-side tool over the run registry since the lock landed, which made
/// it the right moment; US-S3-03 added two more plus the one non-read-only exception below, and
/// US-S3-06 a sixth.
/// </para>
/// <para>
/// <b>Why <c>cancel_run</c> is a second permitted site, and why that does not weaken the rule.</b>
/// The property being protected is "no READ-ONLY tool takes the lock", not "exactly one file does".
/// <c>cancel_run</c> is not read-only — it exists to change a run's lifecycle, and its tool
/// declaration says <c>ReadOnly = false</c> — and it needs the one liveness signal a workspace has to
/// tell "another server process is running this" (<c>VFX-E-1507</c>) from "this <c>running</c> entry
/// is residue a killed server left behind" (<c>VFX-E-1508</c>). Reading that signal means acquiring
/// the claim, because the handle IS the lock (see <c>WorkspaceRunLock</c>); there is no
/// non-exclusive probe to write instead. The exact-equality set below is what keeps the exception
/// from spreading: adding a third site — in particular one belonging to a read-only tool — fails
/// this test by name.
/// </para>
/// <para>
/// <b>Mirrors <see cref="ReadOnlySourceGuardTests"/>'s shape exactly</b> — a whitespace-tolerant
/// regex over source with comments and string literals stripped, and a fail-closed EXACT-equality
/// check against a named set, so a new call site fails by name and a stale entry cannot rot. It is
/// a boundary on WHERE such code may live, not an audit of what it does: a second
/// <c>TryAcquire</c> added inside <c>RunSuiteOrchestrator</c> would still pass the set check, which
/// is why the per-file count is asserted as well.
/// </para>
/// </remarks>
public class RunLockSourceGuardTests
{
    /// <summary>
    /// Every file in <c>src/</c> allowed to CALL <c>TryAcquire</c>, and the number of times each
    /// may. <c>RunSuiteOrchestrator</c> claims the workspace for the duration of one run; nothing
    /// else in this server starts a run. <c>CancelRunOrchestrator</c> takes and immediately releases
    /// the same claim as a liveness probe — see this class's remarks for why that one exception is
    /// permitted and this one is not read-only.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> GuardedCallSites =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs"] = 1,
            ["src/Vouchfx.Mcp/Run/CancelRunOrchestrator.cs"] = 1,
        };

    /// <summary>
    /// Every tool orchestrator that must NEVER appear among the call sites above — the read-only
    /// tools spec §4.6 promises are safe to call concurrently with a run.
    /// </summary>
    /// <remarks>
    /// Redundant with the exact-equality check by construction, and deliberately kept: it states the
    /// PROPERTY in the assertion message rather than leaving a future reader to infer it from a set
    /// of allowed paths, and it fails with the offending tool's own name rather than with a diff.
    /// </remarks>
    private static readonly string[] ReadOnlyOrchestratorsThatMustNeverTakeTheLock =
    [
        "src/Vouchfx.Mcp/Run/GetRunEventsOrchestrator.cs",
        "src/Vouchfx.Mcp/Run/GetRunStatusOrchestrator.cs",
        "src/Vouchfx.Mcp/Run/GetStepTimelineOrchestrator.cs",
        "src/Vouchfx.Mcp/Run/ListRunsOrchestrator.cs",
        "src/Vouchfx.Mcp/Diagnosis/ExplainRunOrchestrator.cs",
        "src/Vouchfx.Mcp/Diagnosis/DiagnoseRunOrchestrator.cs",
    ];

    /// <summary>
    /// An INVOCATION of <c>TryAcquire</c>: a receiver, a dot, the name, an open paren. Declarations
    /// (<c>RunLockResult TryAcquire()</c> in <c>IRunLock</c> and <c>WorkspaceRunLock</c>) carry no
    /// leading dot and so are deliberately not matched — this guard is about who CALLS the lock, not
    /// about who defines it.
    /// </summary>
    private static readonly Regex TryAcquireInvocation = new(@"\.\s*TryAcquire\s*\(", RegexOptions.Compiled);

    [Fact]
    public void TheRunLock_HasExactlyTheTwoNamedCallSitesInSrc_SoReadOnlyToolsCannotTakeIt()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => TryAcquireInvocation.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            GuardedCallSites.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void NoReadOnlyToolsOrchestrator_TakesTheRunLock()
    {
        foreach (var relativePath in ReadOnlyOrchestratorsThatMustNeverTakeTheLock)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Anti-vacuity: a renamed or deleted file would otherwise make this check pass over nothing.
            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.DoesNotMatch(TryAcquireInvocation, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void EachPermittedCallSite_TakesTheLockExactlyTheExpectedNumberOfTimes()
    {
        foreach (var (relativePath, expectedCount) in GuardedCallSites)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Anti-vacuity: a renamed or deleted file would otherwise make this check pass over nothing.
            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            var matches = TryAcquireInvocation.Matches(SourceGuardScan.ExecutableSourceOf(fullPath)).Count;

            Assert.True(
                matches == expectedCount,
                $"'{relativePath}' claims the run lock {matches} time(s); exactly {expectedCount} is "
                + "expected. A second claim on one path is either a double-acquire (which the "
                + "same-process flag, or the lock itself, would refuse) or a second code path that "
                + "needs its own review.");
        }
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
