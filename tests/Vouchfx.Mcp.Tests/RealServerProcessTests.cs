using System.Diagnostics;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spawns the real, built vouchfx-mcp server process — not the in-memory harness
/// <c>McpServerSkeletonTests</c> uses — and drives it over real stdio.
/// </summary>
/// <remarks>
/// The in-memory harness clears all logging providers rather than replicating
/// <c>Program.cs</c>'s console-to-stderr redirection, so it cannot verify that redirection is
/// actually wired correctly in the shipped binary: a regression there (e.g. someone removing
/// <c>LogToStandardErrorThreshold</c>) would corrupt the real stdio JSON-RPC stream with log
/// output but would not be caught by any in-memory test. This test closes the process's own
/// stdin and asserts a clean exit with byte-empty stdout, exercising the real
/// <c>StdioServerTransport</c> and the real logging configuration end to end. No Docker, no
/// network — just one short-lived local process — so it stays fast and hermetic.
/// </remarks>
public class RealServerProcessTests
{
    [Fact]
    public async Task RealProcess_StdinClosedImmediately_ExitsCleanlyWithEmptyStdout()
    {
        var serverDllPath = ResolveServerDllPath();
        Assert.True(
            File.Exists(serverDllPath),
            $"Expected the built server at '{serverDllPath}'. This test assumes the solution " +
            "was already built at the same configuration as this test run — true both in CI " +
            "(build always precedes test at the same -c) and in the documented local workflow.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDllPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp server process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Simulates the parent process disconnecting immediately — the same condition the
        // in-memory harness exercises via WithStreamServerTransport's paired pipes, but here
        // over the real StdioServerTransport and the real console logging configuration.
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var stdout = await stdoutTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stdout);

        // stderr carries the startup log lines (ENGINE_PIN banner, host lifecycle) — it is
        // deliberately not asserted here. Only stdout is the channel nothing but the MCP
        // protocol may ever touch.
        await stderrTask;
    }

    private static string ResolveServerDllPath()
    {
        // AppContext.BaseDirectory for this test run looks like
        // ".../tests/Vouchfx.Mcp.Tests/bin/<Configuration>/<TFM>/". The production project
        // builds to the sibling ".../src/Vouchfx.Mcp/bin/<Configuration>/<TFM>/Vouchfx.Mcp.dll"
        // under the same repo root, at the same Configuration and TFM — both guaranteed by this
        // repo's build order (dotnet build precedes dotnet test at the same -c), so deriving
        // the path this way works identically locally and in CI without depending on any
        // absolute machine path.
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
