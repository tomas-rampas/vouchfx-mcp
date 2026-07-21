using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp;

/// <summary>
/// Registers this server's identity, tool collection, and resource collection with the MCP SDK's
/// DI container.
/// </summary>
/// <remarks>
/// This is the single place that configures <see cref="ModelContextProtocol.Server.McpServerOptions.ServerInfo"/>,
/// <see cref="ModelContextProtocol.Server.McpServerOptions.ToolCollection"/>, and
/// <see cref="ModelContextProtocol.Server.McpServerOptions.ResourceCollection"/>. Both production
/// startup (<see cref="Program"/>, over stdio) and the test suite (over an in-memory paired
/// stream) call this same method, so there is no second copy of the configuration that could
/// drift from what actually ships.
/// </remarks>
public static class VouchfxMcpServerRegistration
{
    /// <summary>
    /// Adds the MCP server with the vouchfx-mcp server identity, tool registry, and resource
    /// registry configured. A transport (stdio, stream, …) still needs to be attached to the
    /// returned builder.
    /// </summary>
    /// <param name="services">The DI container to register against.</param>
    /// <param name="enginePin">
    /// The already-loaded <c>ENGINE_PIN</c> (see <see cref="Program"/>) — the single source of the
    /// version <see cref="CliPinVerifier"/> checks the vouchfx CLI against (REQ-008).
    /// </param>
    /// <param name="vouchfxCli">
    /// The CLI probe <see cref="CliPinVerifier"/> uses. Defaults to the real, process-spawning
    /// <see cref="VouchfxCliProcessRunner"/>; tests supply a fake so they never depend on the real
    /// CLI being installed on the machine running them.
    /// </param>
    /// <param name="suiteRunner">
    /// The process runner <c>run_suite</c>'s <see cref="RunSuiteOrchestrator"/> uses to actually
    /// execute a suite. Defaults to the real, process-spawning <see cref="VouchfxCliSuiteRunner"/>;
    /// tests supply a fake so they never depend on the real CLI or Docker being installed on the
    /// machine running them.
    /// </param>
    /// <param name="lastRunTracker">
    /// REQ-007's session-scoped "what was the last run" record, shared between
    /// <see cref="RunSuiteOrchestrator"/> (the writer) and <see cref="ExplainRunOrchestrator"/> (the
    /// reader). Defaults to a fresh <see cref="LastRunTracker"/> per call — one instance per server
    /// session, matching this server's single-session-per-process design. Tests supply their own to
    /// pre-populate or isolate it.
    /// </param>
    public static IMcpServerBuilder AddVouchfxMcpServer(
        this IServiceCollection services,
        EnginePin enginePin,
        IVouchfxCli? vouchfxCli = null,
        ISuiteRunner? suiteRunner = null,
        ILastRunTracker? lastRunTracker = null)
    {
        ArgumentNullException.ThrowIfNull(enginePin);

        var cliPinVerifier = new CliPinVerifier(vouchfxCli ?? new VouchfxCliProcessRunner(), enginePin);
        var tracker = lastRunTracker ?? new LastRunTracker();
        var runSuiteOrchestrator = new RunSuiteOrchestrator(cliPinVerifier, suiteRunner ?? new VouchfxCliSuiteRunner(), tracker);
        var explainRunOrchestrator = new ExplainRunOrchestrator(tracker);

        return services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = ServerIdentity.Name,
                Version = ServerIdentity.Version,
            };
            options.ToolCollection = [.. ToolRegistry.CreateAll(runSuiteOrchestrator, explainRunOrchestrator)];
            options.ResourceCollection = [.. DocResourceRegistry.CreateAll()];
        });
    }
}
