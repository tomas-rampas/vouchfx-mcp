using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Planning;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Scaffold;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

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
    /// The CLI probe <see cref="CliPinVerifier"/> and live catalogue/schema loaders use. Defaults
    /// to the real, process-spawning <see cref="VouchfxCliProcessRunner"/>; tests supply a fake so
    /// they never depend on the real CLI being installed on the machine running them.
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
    /// <param name="workspace">
    /// US-S3-08's workspace, resolved once at server start from <c>--workspace &lt;path&gt;</c> (see
    /// <see cref="Program"/>), or <see langword="null"/> when the host supplied no such flag.
    /// <b>Null is the full-fidelity legacy mode</b>, not a degraded one: every path parameter then
    /// behaves byte for byte as it did before Sprint 3 (plan §2.1 — containment is new policy, gated
    /// on opting in). Threaded from here into every component that gates a caller-supplied path, so
    /// there is exactly one workspace per server and no component resolves its own.
    /// </param>
    public static IMcpServerBuilder AddVouchfxMcpServer(
        this IServiceCollection services,
        EnginePin enginePin,
        IVouchfxCli? vouchfxCli = null,
        ISuiteRunner? suiteRunner = null,
        ILastRunTracker? lastRunTracker = null,
        Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(enginePin);

        var cli = vouchfxCli ?? new VouchfxCliProcessRunner();
        var cliPinVerifier = new CliPinVerifier(cli, enginePin);
        var tracker = lastRunTracker ?? new LastRunTracker();
        var runSuiteOrchestrator = new RunSuiteOrchestrator(cliPinVerifier, suiteRunner ?? new VouchfxCliSuiteRunner(), tracker, workspace);
        var explainRunOrchestrator = new ExplainRunOrchestrator(tracker, workspace);
        var diagnoseRunOrchestrator = new DiagnoseRunOrchestrator(explainRunOrchestrator);
        var liveStepCatalogue = new LiveStepCatalogue(cli, cliPinVerifier, enginePin);
        var scaffoldSuiteOrchestrator = new ScaffoldSuiteOrchestrator(cliPinVerifier, cli, enginePin);
        var planCoverageOrchestrator = new PlanCoverageOrchestrator(cliPinVerifier, cli, enginePin);

        // US-S2-01: LiveSchemaDocument has existed and been fully tested since REQ-010 but was
        // never CONSTRUCTED here, so `vouchfx schema` was dead code in a shipping server. get_schema
        // is its first caller. Process-scoped and never disposed, exactly like LiveStepCatalogue
        // above (see that type's remarks): one instance per MCP server process, its SemaphoreSlim
        // held for the process lifetime and reclaimed by the OS at exit. Disposing it here is not an
        // option — the tool collection outlives this method by the whole session — and giving the
        // orchestrator ownership would have it dispose something the server, not the tool, owns.
        var liveSchemaDocument = new LiveSchemaDocument(cli, cliPinVerifier);
        var getSchemaOrchestrator = new GetSchemaOrchestrator(liveSchemaDocument);

        return services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = ServerIdentity.Name,
                Version = ServerIdentity.Version,
            };
            options.ToolCollection =
            [
                .. ToolRegistry.CreateAll(
                    runSuiteOrchestrator,
                    explainRunOrchestrator,
                    diagnoseRunOrchestrator,
                    liveStepCatalogue,
                    scaffoldSuiteOrchestrator,
                    planCoverageOrchestrator,
                    getSchemaOrchestrator,
                    workspace)
            ];
            options.ResourceCollection = [.. DocResourceRegistry.CreateAll(), DiagnosticResourceRegistry.Create()];
        });
    }
}
