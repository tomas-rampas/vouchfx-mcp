using System.Diagnostics;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for issue #79's "the transitive dependency closure is pinned"
/// invariant: every tracked <c>.csproj</c> in this repository carries a sibling, COMMITTED
/// <c>packages.lock.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs a test rather than trusting CI's <c>--locked-mode</c> restore alone.</b>
/// <c>--locked-mode</c> (see <c>build.yml</c>/<c>release.yml</c>'s locked-restore steps) fails with
/// <c>NU1004</c> when a COMMITTED <c>packages.lock.json</c> drifts from what restore would resolve —
/// but it does NOT fail when a project has no <c>packages.lock.json</c> at all. MEASURED on the SDK
/// this repo pins (8.0.424, matching <c>global.json</c>'s 8.0.400 floor): a locked-mode restore
/// against a project with no lock file exits 0 and SILENTLY CREATES one on disk instead of refusing.
/// A newly added project — a fifth fixture, say — that forgot to run a plain <c>dotnet restore</c>
/// and commit the generated lock file would therefore restore "successfully" in CI while carrying an
/// unpinned transitive closure, defeating the whole point of issue #79 without CI ever going red.
/// This test closes that hole structurally, both locally and in CI, by asserting the coverage
/// directly rather than trusting an absence of NU1004 to imply it.
/// </para>
/// <para>
/// <b>Enumeration is via the real <c>git</c> CLI</b> (<c>git ls-files '*.csproj'</c> and
/// <c>git ls-files '*packages.lock.json'</c>), run against the repo root
/// (<see cref="SourceGuardScan.RepoRoot"/>), rather than <c>Directory.EnumerateFiles</c>: the
/// property this guard protects is "every TRACKED project has a TRACKED lock file" — a lock file
/// present on disk but never <c>git add</c>-ed would satisfy a filesystem walk while still shipping
/// nothing to CI, which restores from a clean checkout. A pathspec with no <c>/</c> matches at any
/// depth (the same glob semantics as <c>.gitignore</c>), so both queries need no directory
/// enumeration of their own.
/// </para>
/// <para>
/// <b>Fail-closed shape.</b> Both git queries and every process spawn are asserted to succeed before
/// their output is trusted (a <c>git</c> not on PATH, or a repo-root mis-resolution, fails loudly here
/// rather than degrading to a vacuous pass over an empty set). The remedy named in the failure message
/// is the one CI's own locked-restore step comments already point at: run
/// <c>dotnet restore Vouchfx.Mcp.sln</c> locally (no <c>--locked-mode</c>) and commit the generated
/// <c>packages.lock.json</c>.
/// </para>
/// </remarks>
public class LockFileCoverageSourceGuardTests
{
    [Fact]
    public void EveryTrackedCsproj_HasATrackedSiblingPackagesLockJson()
    {
        var repoRoot = SourceGuardScan.RepoRoot.FullName;

        var trackedCsprojFiles = RunGitLsFiles(repoRoot, "*.csproj");
        var trackedLockFiles = RunGitLsFiles(repoRoot, "*packages.lock.json")
            .ToHashSet(StringComparer.Ordinal);

        // Anti-vacuity: an empty result here means the git invocation silently found nothing
        // (wrong cwd, git not on PATH producing empty stdout, …) rather than that this repo
        // genuinely has zero .csproj files — which would make every assertion below pass vacuously.
        Assert.True(
            trackedCsprojFiles.Length > 0,
            "`git ls-files '*.csproj'` returned no files — expected at least the four projects this "
            + "repo ships (src/Vouchfx.Mcp, tests/Vouchfx.Mcp.Tests, tests/StdinEofChildFixture, "
            + "tests/RunLockHolderFixture). A working-directory or PATH problem would produce this "
            + "same empty result and must not be read as 'no projects to check'.");

        var missing = trackedCsprojFiles
            .Select(csproj => (Csproj: csproj, Lock: ExpectedLockFilePath(csproj)))
            .Where(pair => !trackedLockFiles.Contains(pair.Lock))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The following tracked .csproj file(s) have no committed sibling packages.lock.json:\n"
            + string.Join('\n', missing.Select(pair => $"  {pair.Csproj} -> expected {pair.Lock}"))
            + "\nRemedy: run `dotnet restore Vouchfx.Mcp.sln` locally (RestorePackagesWithLockFile=true "
            + "in Directory.Build.props generates the file) and commit the result — see issue #79 and "
            + "the locked-restore step comments in .github/workflows/build.yml.");
    }

    /// <summary>
    /// A project's lock file always lives beside its <c>.csproj</c>, named exactly
    /// <c>packages.lock.json</c> (the SDK's default; this repo names no <c>&lt;RestorePackagesPath&gt;</c>
    /// or <c>&lt;NuGetLockFilePath&gt;</c> override).
    /// </summary>
    private static string ExpectedLockFilePath(string csprojRepoRelativePath)
    {
        var directory = csprojRepoRelativePath[..(csprojRepoRelativePath.LastIndexOf('/') + 1)];
        return $"{directory}packages.lock.json";
    }

    /// <summary>Runs <c>git ls-files &lt;pattern&gt;</c> against <paramref name="repoRoot"/> and returns each matched repo-relative path (forward-slashed, exactly as git emits it).</summary>
    private static string[] RunGitLsFiles(string repoRoot, string pattern)
    {
        var startInfo = new ProcessStartInfo("git", $"ls-files -- \"{pattern}\"")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start 'git ls-files -- \"{pattern}\"'.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"'git ls-files -- \"{pattern}\"' exited {process.ExitCode} (cwd '{repoRoot}'). stderr:\n{stderr}");

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
