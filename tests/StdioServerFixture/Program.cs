using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Transport;

// Test-only executable: real SDK stdio and production tool/host wiring, but never a real engine.
// Its only child is the production YAML validation worker, which parses the test-owned suite.
if (args.Length != 1)
{
    return 2;
}

var root = Path.GetFullPath(args[0]);
var workspace = new Workspace(root, Path.Combine(root, "e2e"), Path.Combine(root, ".vouchfx", "runs"), null);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = [],
});
builder.Logging.ClearProviders();
var pin = new EnginePin("v1.0.1", "bb2db1303fb6d287a2f001a8ef4e618db4620ae4");
StdioServerShutdown.WithDisconnectAwareStdioTransport(builder.Services.AddVouchfxMcpServer(
    pin, new InertCli(), new HeldCleanupRunner(workspace.Root), workspace: workspace));
await builder.Build().RunAsync();
return 0;

internal sealed class InertCli : IVouchfxCli
{
    public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("1.0.1+bb2db1303fb6d287a2f001a8ef4e618db4620ae4");

    public Task<string?> TryRunStdoutAsync(
        IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This fixture cannot invoke an engine command.");

    public Task<CliInvocationResult> RunAsync(
        IReadOnlyList<string> arguments, long maxStreamBytes, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This fixture cannot invoke an engine command.");
}

internal sealed class HeldCleanupRunner(string root) : ISuiteRunner
{
    public async Task<SuiteProcessResult> RunAsync(
        SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "runner-started"), "started", CancellationToken.None);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The inert runner must be stopped by transport EOF.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "cleanup-started"), "cancelled", CancellationToken.None);
            using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(root, "release-cleanup")))
            {
                await Task.Delay(20, cleanupBudget.Token);
            }

            await File.WriteAllTextAsync(Path.Combine(root, "cleanup-completed"), "completed", CancellationToken.None);
            return new SuiteProcessResult(4, RunTermination.Aborted);
        }
    }
}
