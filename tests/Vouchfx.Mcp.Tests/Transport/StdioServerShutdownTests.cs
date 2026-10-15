using System.Diagnostics;
using System.Text.Json;

namespace Vouchfx.Mcp.Tests.Transport;

/// <summary>Exercises actual SDK stdio EOF with a runner that cannot launch an engine.</summary>
public class StdioServerShutdownTests
{
    [Fact]
    public async Task StdinEof_DuringActiveRun_CancelsAndWaitsForCleanupBeforeRecordingTerminalOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-stdio-eof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "e2e"));
        var suite = Path.Combine(root, "e2e", "good-suite.e2e.yaml");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "good-suite.e2e.yaml"), suite);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(RepoLayout.ResolveStdioServerFixtureDllPath());
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "stdio-eof-regression", version = "1.0.0" },
                },
            });
            var initialize = await process.StandardOutput.ReadLineAsync(deadline.Token);
            using (var response = JsonDocument.Parse(Assert.IsType<string>(initialize)))
            {
                Assert.Equal("2.0", response.RootElement.GetProperty("jsonrpc").GetString());
                Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            }

            // Start draining before EOF; a blocked output pipe must not manufacture a shutdown failure.
            var stdout = process.StandardOutput.ReadToEndAsync();
            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new { name = "run_suite", arguments = new { path = suite, timeoutSeconds = 300 } },
            });
            await WaitForFileAsync(Path.Combine(root, "runner-started"), process, deadline.Token);
            var registryPath = Assert.Single(Directory.GetFiles(Path.Combine(root, ".vouchfx", "runs"), "run.json", SearchOption.AllDirectories));
            AssertRunning(registryPath);

            // The only cancellation stimulus is closing the actual server stdin pipe once.
            process.StandardInput.Close();
            await WaitForFileAsync(Path.Combine(root, "cleanup-started"), process, deadline.Token);
            Assert.False(process.HasExited);
            Assert.False(File.Exists(Path.Combine(root, "cleanup-completed")));
            AssertRunning(registryPath);

            // The runner remains held while cancelled. Host exit and terminal persistence must wait.
            await Task.Delay(200, deadline.Token);
            Assert.False(process.HasExited);
            AssertRunning(registryPath);
            await File.WriteAllTextAsync(Path.Combine(root, "release-cleanup"), "release", deadline.Token);
            await process.WaitForExitAsync(deadline.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "cleanup-completed")));
            using var entry = JsonDocument.Parse(await File.ReadAllTextAsync(registryPath, deadline.Token));
            var recorded = entry.RootElement.GetProperty("run");
            Assert.Equal("completed", recorded.GetProperty("status").GetString());
            Assert.Equal("Inconclusive", recorded.GetProperty("outcome").GetString());
            Assert.NotEqual(JsonValueKind.Null, recorded.GetProperty("finishedAt").ValueKind);

            // No response is required after disconnection. Every remaining stdout line must still
            // be an actual JSON-RPC frame, never a fixture marker or a fabricated terminal response.
            foreach (var line in (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var message = JsonDocument.Parse(line);
                Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
            }

            await stderr;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            // This unique test-owned directory contains only inert fixture files and YAML evidence.
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task WaitForFileAsync(string path, Process process, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            Assert.False(process.HasExited, "The fixture exited before reaching the expected lifecycle stage.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static void AssertRunning(string path)
    {
        using var entry = JsonDocument.Parse(File.ReadAllText(path));
        var recorded = entry.RootElement.GetProperty("run");
        Assert.Equal("running", recorded.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("finishedAt").ValueKind);
    }
}
