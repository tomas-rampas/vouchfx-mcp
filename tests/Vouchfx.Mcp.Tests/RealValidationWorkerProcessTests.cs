using System.Diagnostics;
using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spawns the real, built vouchfx-mcp binary directly in <c>--validate-worker &lt;path&gt;</c>
/// mode — the hidden, one-shot mode <see cref="ValidationWorkerClient"/> (the <c>validate_suite</c>
/// orchestrator) spawns as a child process. See <see cref="RealServerProcessTests"/> for the
/// equivalent coverage of the ordinary MCP server mode.
/// </summary>
/// <remarks>
/// Confirms worker mode's own contract directly against the real binary, independent of
/// <see cref="ValidationWorkerClient"/>: given <c>--validate-worker &lt;path&gt;</c>, the process
/// runs <see cref="SuiteValidator"/>'s pipeline against that one file, writes exactly the
/// serialised result to stdout, and exits — without ever starting the MCP host (no handshake is
/// attempted, and the process does not sit waiting on stdin the way the ordinary server mode
/// does).
/// </remarks>
public class RealValidationWorkerProcessTests
{
    [Fact]
    public async Task ValidateWorker_GoodFixture_WritesOnlyTheJsonResultAndExitsZero()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();
        Assert.True(
            File.Exists(serverDllPath),
            $"Expected the built server at '{serverDllPath}'. This test assumes the solution " +
            "was already built at the same configuration as this test run.");

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "good-suite.e2e.yaml");

        var (exitCode, stdout, stderr) = await RunWorkerAsync(serverDllPath, fixturePath);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<ValidateSuiteResult>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result!.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateWorker_BadFixture_WritesInvalidResultAndExitsZero()
    {
        // A non-zero exit is reserved for a genuine worker crash (see ValidationWorkerClient's
        // validation-worker-failed handling) — an ordinary "the suite is invalid" outcome is a
        // successful run of the worker itself, so this must still exit 0.
        var serverDllPath = RepoLayout.ResolveServerDllPath();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bad-suite.e2e.yaml");

        var (exitCode, stdout, stderr) = await RunWorkerAsync(serverDllPath, fixturePath);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);

        var result = JsonSerializer.Deserialize<ValidateSuiteResult>(stdout, ValidationWorkerProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.False(result!.Valid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task ValidateWorker_MissingPathArgument_ExitsNonZeroWithoutAttemptingAnMcpHandshake()
    {
        var serverDllPath = RepoLayout.ResolveServerDllPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDllPath);
        startInfo.ArgumentList.Add(ValidationWorkerProtocol.WorkerModeArgument);
        // Deliberately no path argument.

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp validation worker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // Worker mode never reads stdin — closing it here proves that: if this process were
        // instead waiting on an MCP handshake, closing stdin would end its session (as
        // RealServerProcessTests proves for the ordinary server), which is a materially
        // different code path from the immediate, synchronous exit asserted below.
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cts.Token);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Empty(await stdoutTask);
        Assert.NotEmpty(await stderrTask);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWorkerAsync(string serverDllPath, string suitePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(serverDllPath);
        startInfo.ArgumentList.Add(ValidationWorkerProtocol.WorkerModeArgument);
        startInfo.ArgumentList.Add(suitePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the vouchfx-mcp validation worker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
