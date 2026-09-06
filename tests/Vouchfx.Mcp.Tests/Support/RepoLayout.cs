namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Resolves paths into this repo's build layout from a test assembly's own output directory.
/// </summary>
/// <remarks>
/// Shared by every test that spawns the REAL, built <c>Vouchfx.Mcp.dll</c> as a separate process
/// (as opposed to hosting it in-memory via <c>McpTestHarness</c>) — see
/// <c>RealServerProcessTests</c> and <c>RealValidationWorkerProcessTests</c>.
/// </remarks>
internal static class RepoLayout
{
    /// <summary>
    /// The built <c>Vouchfx.Mcp.dll</c> path, derived from this test assembly's own output
    /// directory rather than an absolute machine path.
    /// </summary>
    /// <remarks>
    /// <c>AppContext.BaseDirectory</c> for a test run looks like
    /// ".../tests/Vouchfx.Mcp.Tests/bin/&lt;Configuration&gt;/&lt;TFM&gt;/". The production
    /// project builds to the sibling ".../src/Vouchfx.Mcp/bin/&lt;Configuration&gt;/&lt;TFM&gt;/Vouchfx.Mcp.dll"
    /// under the same repo root, at the same Configuration and TFM — both guaranteed by this
    /// repo's build order (<c>dotnet build</c> precedes <c>dotnet test</c> at the same
    /// <c>-c</c>), so deriving the path this way works identically locally and in CI without
    /// depending on any absolute machine path.
    /// </remarks>
    public static string ResolveServerDllPath()
    {
        var (tfm, configuration, _, testsDir) = ResolveLayout();
        var repoRoot = testsDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

        return Path.Combine(repoRoot.FullName, "src", "Vouchfx.Mcp", "bin", configuration.Name, tfm, "Vouchfx.Mcp.dll");
    }

    /// <summary>
    /// The built <c>Vouchfx.Mcp.Tests.StdinEofChildFixture.dll</c> path — the tiny, purpose-built
    /// child-process fixture <c>VouchfxCliSuiteRunnerTests</c> spawns (via <c>dotnet &lt;path&gt;
    /// &lt;behaviour&gt;</c>) to exercise <see cref="Vouchfx.Mcp.Run.VouchfxCliSuiteRunner"/>'s
    /// graceful-stop-then-force-kill sequence against a REAL OS process, without depending on the
    /// actual <c>vouchfx</c> CLI (or Docker) being installed. Derived the same way as
    /// <see cref="ResolveServerDllPath"/> — see that method's remarks — except the fixture project
    /// sits directly under <c>tests/</c> (sibling to <c>Vouchfx.Mcp.Tests</c>, one level shallower
    /// than <c>src/Vouchfx.Mcp</c>), so its own project directory IS the "testsDir"-relative root.
    /// </summary>
    public static string ResolveStdinEofChildFixtureDllPath()
    {
        var (tfm, configuration, _, testsDir) = ResolveLayout();

        return Path.Combine(
            testsDir.FullName, "StdinEofChildFixture", "bin", configuration.Name, tfm,
            "Vouchfx.Mcp.Tests.StdinEofChildFixture.dll");
    }

    /// <summary>
    /// The built <c>Vouchfx.Mcp.Tests.RunLockHolderFixture.dll</c> path — US-S3-04's child-process
    /// fixture, which acquires the REAL <see cref="Vouchfx.Mcp.Run.WorkspaceRunLock"/> and records a
    /// REAL <c>running</c> registry entry, so <c>RealCrossProcessRunLockTests</c> can prove spec
    /// §4.6's exclusivity against a genuinely separate OS process (and prove the claim evaporates
    /// when that process is killed). Derived exactly like
    /// <see cref="ResolveStdinEofChildFixtureDllPath"/> — see its remarks.
    /// </summary>
    public static string ResolveRunLockHolderFixtureDllPath()
    {
        var (tfm, configuration, _, testsDir) = ResolveLayout();

        return Path.Combine(
            testsDir.FullName, "RunLockHolderFixture", "bin", configuration.Name, tfm,
            "Vouchfx.Mcp.Tests.RunLockHolderFixture.dll");
    }

    /// <summary>
    /// The repo-root <c>ENGINE_PIN</c> file's path, derived the same way as
    /// <see cref="ResolveServerDllPath"/> (walking up from this test assembly's own output
    /// directory) rather than an absolute machine path.
    /// </summary>
    /// <remarks>
    /// Used by tests that verify the ACTUALLY-installed <c>vouchfx</c> CLI against the real pin
    /// this repo ships — as opposed to <see cref="McpTestHarness.DefaultTestPin"/>, an arbitrary
    /// well-formed pin unrelated to this file, which every other test in this repo uses instead so
    /// it never depends on what is (or is not) installed on the machine running it.
    /// </remarks>
    public static string ResolveEnginePinPath()
    {
        var (_, _, _, testsDir) = ResolveLayout();
        var repoRoot = testsDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

        return Path.Combine(repoRoot.FullName, "ENGINE_PIN");
    }

    /// <summary>
    /// This repo's root directory, derived the same way as <see cref="ResolveEnginePinPath"/>
    /// (walking up from this test assembly's own output directory) rather than an absolute machine
    /// path.
    /// </summary>
    /// <remarks>
    /// Used by <c>RealValidateAgainstPinnedCliTests</c>'s corpus regression guard to find the sibling
    /// <c>vouchfx</c> engine-repo checkout (<c>&lt;repoRoot&gt;/../vouchfx</c>) from which it extracts
    /// the engine's rejected corpus at the pinned commit — a maintainer-local resource, exactly like
    /// the pinned CLI that guard also depends on.
    /// </remarks>
    public static string ResolveRepoRootPath()
    {
        var (_, _, _, testsDir) = ResolveLayout();
        return (testsDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory."))
            .FullName;
    }

    /// <summary>
    /// Walks up from this test assembly's own output directory to the pieces every
    /// <c>Resolve*DllPath</c> method needs: the target framework moniker, the build configuration
    /// directory, this test project's own directory, and the shared <c>tests/</c> directory.
    /// </summary>
    private static (string Tfm, DirectoryInfo Configuration, DirectoryInfo TestProjectDir, DirectoryInfo TestsDir) ResolveLayout()
    {
        var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = testOutputDir.Name;
        var configuration = testOutputDir.Parent
            ?? throw new InvalidOperationException("Could not determine the build configuration from the test output path.");
        var testProjectDir = testOutputDir.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
        var testsDir = testProjectDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");

        return (tfm, configuration, testProjectDir, testsDir);
    }
}
