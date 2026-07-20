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
        var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = testOutputDir.Name;
        var configuration = testOutputDir.Parent
            ?? throw new InvalidOperationException("Could not determine the build configuration from the test output path.");
        var testProjectDir = testOutputDir.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
        var testsDir = testProjectDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
        var repoRoot = testsDir.Parent
            ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

        return Path.Combine(repoRoot.FullName, "src", "Vouchfx.Mcp", "bin", configuration.Name, tfm, "Vouchfx.Mcp.dll");
    }
}
