using System.Diagnostics;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level regression guard for issue #79's "the transitive dependency closure is pinned"
/// invariant: every tracked <c>.csproj</c> in this repository carries a sibling, COMMITTED
/// <c>packages.lock.json</c> — and, in the other direction, no committed lock file is an orphan.
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
/// <b>Bidirectional, matching this repo's own convention</b> (e.g. <c>VfxCodeCatalogue</c>'s
/// completeness gate, <c>PromptDocumentParser</c>'s declared-argument/placeholder set equality): a
/// forward-only check ("every csproj has a lock file") would miss the opposite drift — an orphaned
/// <c>packages.lock.json</c> left behind after its project was deleted or moved, which is dead
/// weight that silently stops tracking anything real. <see cref="NoTrackedPackagesLockJsonIsOrphaned"/>
/// asserts the reverse direction.
/// </para>
/// <para>
/// <b>Enumeration is via the real <c>git</c> CLI</b> (<c>git ls-files '*.csproj'</c> and
/// <c>git ls-files '*packages.lock.json'</c>), run against the repo root
/// (<see cref="SourceGuardScan.RepoRoot"/>), rather than <c>Directory.EnumerateFiles</c>: the
/// property this guard protects is "every TRACKED project has a TRACKED lock file" — a lock file
/// present on disk but never <c>git add</c>-ed would satisfy a filesystem walk while still shipping
/// nothing to CI, which restores from a clean checkout. A pathspec with no <c>/</c> matches at any
/// depth (the same glob semantics as <c>.gitignore</c>), so both queries need no directory
/// enumeration of their own. The git invocation itself mirrors
/// <see cref="RealValidateAgainstPinnedCliTests"/>'s house pattern (<c>StartGit</c>/
/// <c>RunGitTextAsync</c>): <c>ArgumentList</c> rather than a hand-quoted argument string (so a
/// pattern or path containing a space or quote cannot be mis-parsed by shell-style splitting),
/// concurrent stdout/stderr reads paired with <c>WaitForExitAsync</c> (so a chatty stderr cannot
/// deadlock a synchronous <c>ReadToEnd</c> against a full stdout pipe), and a bounded wait via a
/// caller-owned <see cref="CancellationTokenSource"/> timeout rather than an unbounded one.
/// </para>
/// <para>
/// <b>Fail-closed shape, with two anchors against a silently-wrong repo root.</b> Every process
/// spawn is asserted to succeed before its output is trusted (a <c>git</c> not on PATH fails loudly
/// here rather than degrading to a vacuous pass over an empty set), and
/// <see cref="EveryTrackedCsproj_HasATrackedSiblingPackagesLockJson"/> additionally anchors that
/// <see cref="SourceGuardScan.RepoRoot"/> really resolved to the repo root
/// (<c>Vouchfx.Mcp.sln</c> exists there) and that the one project this whole repo ships,
/// <c>src/Vouchfx.Mcp/Vouchfx.Mcp.csproj</c>, is present in what git reports — a git invocation
/// silently narrowed to the wrong cwd or a mis-typed pathspec could still return SOME files and
/// pass the plain non-empty check without either anchor. The remedy named in the failure message
/// is the one CI's own locked-restore step comments already point at: run
/// <c>dotnet restore Vouchfx.Mcp.sln</c> locally (no <c>--locked-mode</c>) and commit the generated
/// <c>packages.lock.json</c>.
/// </para>
/// </remarks>
public class LockFileCoverageSourceGuardTests
{
    /// <summary>Wide enough for a local <c>git ls-files</c> call (near-instant) with headroom for a
    /// loaded CI runner, tight enough that a genuinely hung invocation still fails this test rather
    /// than the whole run's timeout.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task EveryTrackedCsproj_HasATrackedSiblingPackagesLockJson()
    {
        var repoRoot = SourceGuardScan.RepoRoot.FullName;

        // Anchor 1: prove RepoRoot really resolved to the repo root before trusting anything the
        // git queries below report relative to it — a mis-walked path could exist and even hold a
        // .git directory (a submodule, a nested checkout) while being the wrong root entirely.
        Assert.True(
            File.Exists(Path.Combine(repoRoot, "Vouchfx.Mcp.sln")),
            $"'{repoRoot}' does not contain Vouchfx.Mcp.sln — SourceGuardScan.RepoRoot mis-resolved; "
            + "every relative path this guard checks would be wrong.");

        using var cts = new CancellationTokenSource(GitTimeout);

        var trackedCsprojFiles = await RunGitLsFilesAsync(repoRoot, "*.csproj", cts.Token);
        var trackedLockFiles = await RunGitLsFilesAsync(repoRoot, "*packages.lock.json", cts.Token);

        // Anti-vacuity: an empty result here means the git invocation silently found nothing
        // (wrong cwd, git not on PATH producing empty stdout, …) rather than that this repo
        // genuinely has zero .csproj files — which would make every assertion below pass vacuously.
        Assert.True(
            trackedCsprojFiles.Length > 0,
            "`git ls-files '*.csproj'` returned no files — expected at least the four projects this "
            + "repo ships (src/Vouchfx.Mcp, tests/Vouchfx.Mcp.Tests, tests/StdinEofChildFixture, "
            + "tests/RunLockHolderFixture). A working-directory or PATH problem would produce this "
            + "same empty result and must not be read as 'no projects to check'.");

        // Anchor 2: the one product project this repo packages and ships must never silently drop
        // out of the tracked set — a stronger, name-specific companion to the non-empty check above,
        // which a narrowed-but-nonempty match (e.g. only test fixtures) would still satisfy.
        Assert.Contains("src/Vouchfx.Mcp/Vouchfx.Mcp.csproj", trackedCsprojFiles);

        var trackedLockFileSet = trackedLockFiles.ToHashSet(StringComparer.Ordinal);
        var missing = trackedCsprojFiles
            .Select(csproj => (Csproj: csproj, Lock: SiblingLockFilePath(csproj)))
            .Where(pair => !trackedLockFileSet.Contains(pair.Lock))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "The following tracked .csproj file(s) have no committed sibling packages.lock.json:\n"
            + string.Join('\n', missing.Select(pair => $"  {pair.Csproj} -> expected {pair.Lock}"))
            + "\nRemedy: run `dotnet restore Vouchfx.Mcp.sln` locally (RestorePackagesWithLockFile=true "
            + "in Directory.Build.props generates the file) and commit the result — see issue #79 and "
            + "the locked-restore step comments in .github/workflows/build.yml.");
    }

    [Fact]
    public async Task NoTrackedPackagesLockJsonIsOrphaned()
    {
        var repoRoot = SourceGuardScan.RepoRoot.FullName;
        using var cts = new CancellationTokenSource(GitTimeout);

        var trackedCsprojDirectories = (await RunGitLsFilesAsync(repoRoot, "*.csproj", cts.Token))
            .Select(DirectoryOf)
            .ToHashSet(StringComparer.Ordinal);
        var trackedLockFiles = await RunGitLsFilesAsync(repoRoot, "*packages.lock.json", cts.Token);

        var orphaned = trackedLockFiles
            .Where(lockFile => !trackedCsprojDirectories.Contains(DirectoryOf(lockFile)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            orphaned.Length == 0,
            "The following tracked packages.lock.json file(s) have no sibling tracked .csproj "
            + "(orphaned — most likely a project was deleted or moved without removing or relocating "
            + "its lock file):\n"
            + string.Join('\n', orphaned.Select(path => $"  {path}"))
            + "\nRemedy: `git rm` the orphaned lock file, or restore the missing .csproj if its "
            + "removal was a mistake.");
    }

    /// <summary>
    /// A project's lock file always lives beside its <c>.csproj</c>, named exactly
    /// <c>packages.lock.json</c> (the SDK's default; this repo names no <c>&lt;RestorePackagesPath&gt;</c>
    /// or <c>&lt;NuGetLockFilePath&gt;</c> override).
    /// </summary>
    private static string SiblingLockFilePath(string csprojRepoRelativePath) =>
        $"{DirectoryOf(csprojRepoRelativePath)}packages.lock.json";

    /// <summary><paramref name="repoRelativePath"/>'s containing directory, repo-relative with a
    /// trailing slash (empty string for a repo-root file, which none of the paths this guard
    /// handles ever are).</summary>
    private static string DirectoryOf(string repoRelativePath) =>
        repoRelativePath[..(repoRelativePath.LastIndexOf('/') + 1)];

    /// <summary>Runs <c>git ls-files &lt;pattern&gt;</c> against <paramref name="repoRoot"/> and
    /// returns each matched repo-relative path (forward-slashed, exactly as git emits it).</summary>
    private static async Task<string[]> RunGitLsFilesAsync(
        string repoRoot, string pattern, CancellationToken cancellationToken)
    {
        using var process = StartGit(repoRoot, ["ls-files", "--", pattern]);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);

        Assert.True(
            process.ExitCode == 0,
            $"'git -C \"{repoRoot}\" ls-files -- \"{pattern}\"' exited {process.ExitCode}. "
            + $"stderr:\n{await stderrTask}");

        var stdout = await stdoutTask;
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    /// <summary>Starts <c>git -C &lt;repoRoot&gt; &lt;args&gt;</c> via <c>ArgumentList</c> (never a
    /// hand-quoted <c>Arguments</c> string, which a pattern or path containing a space or quote could
    /// mis-parse), mirroring <c>RealValidateAgainstPinnedCliTests.StartGit</c> exactly.</summary>
    private static Process StartGit(string repoRoot, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repoRoot);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start 'git' (args: {string.Join(' ', args)}).");
    }
}
