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
    /// <param name="runRegistry">
    /// US-S3-01's run registry, shared between <see cref="RunSuiteOrchestrator"/> (the writer) and
    /// <see cref="ExplainRunOrchestrator"/> (the reader). <b>Defaults are selected by
    /// workspace-configured-ness</b>, and this is the single seam where that choice is made — see
    /// the body for why the two implementations are not interchangeable defaults. Tests supply their
    /// own to pre-populate or isolate it.
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
        IRunRegistry? runRegistry = null,
        Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(enginePin);

        var cli = vouchfxCli ?? new VouchfxCliProcessRunner();
        var cliPinVerifier = new CliPinVerifier(cli, enginePin);

        // US-S3-01: the run registry's implementation is chosen HERE and nowhere else, purely by
        // whether the host opted into a workspace.
        //
        //   workspace configured ⇒ FileRunRegistry under workspace.OutputDir. That directory is the
        //     one place US-S3-08 established this server may write, so persistence — and therefore
        //     restart survival for explain_run/get_run_status — is available exactly when the host
        //     has said where to put it. OutputDir is CONSUMED, never recomputed (the sprint's exit
        //     checklist forbids a story deriving its own base directory).
        //
        //   no workspace ⇒ InMemoryRunRegistry. US-S3-08's compatibility rule is that a host which
        //     never opted in sees behaviour byte for byte unchanged; a registry that invented a base
        //     directory of its own would create files on a host that never asked for any, which is
        //     precisely the failure that rule exists to prevent.
        //
        // ONE instance either way, shared by the writer and the reader — the same sharing the
        // retired ILastRunTracker had, and what makes run_suite → explain_run work without a second
        // source of truth.
        // The workspace is handed to FileRunRegistry as well as its output directory: that type
        // containment-checks the one against the other at construction, so a symlinked `.vouchfx`
        // pointing out of the root is refused structurally rather than trusted. Program.cs runs the
        // same check earlier to turn it into a readable startup line — see
        // PathSafetyGuard.DescribeWorkspaceStartupFailure for why both exist.
        var registry = runRegistry ?? (workspace is null
            ? new InMemoryRunRegistry()
            : new FileRunRegistry(workspace.OutputDir, workspace));

        // US-S3-04: the cross-process run lock is chosen by the SAME single criterion, in the same
        // place, and consumes the same already-resolved OutputDir — spec §4.6 puts the lock at
        // <outputDir>/.lock, so where there is no output directory there is no lock to take. `null`
        // is therefore a first-class mode, not a missing dependency: RunSuiteOrchestrator's
        // in-process single-flight flag then remains the only guard, which is byte for byte how this
        // server behaved before Sprint 3 for a host that never passed --workspace.
        //
        // Constructed here rather than inside the orchestrator for the reason FileRunRegistry is:
        // the containment check that decides whether this server may write under that directory at
        // all belongs to a startup-time construction, where it fails loudly and once (see
        // WorkspaceRunLock's remarks and Program.cs's narrow RunArtefactStorageException boundary),
        // not to a per-call path.
        var runLock = workspace is null ? null : new WorkspaceRunLock(workspace.OutputDir, workspace);

        // US-S3-03: the cancel_run bridge. ONE instance, shared by the writer (run_suite publishes its
        // in-flight run's stop signal into it) and the reader (cancel_run fires it) — the same
        // single-instance discipline the registry above follows, and for a sharper reason: a second
        // instance here would not merely drift, it would make cancel_run report VFX-E-1507 against a
        // run this very process is holding. Process-local by design and never persisted; see
        // IRunCancellationRegistry for why a cross-process cancel is refused rather than simulated.
        var cancellations = new InProcessRunCancellations();

        var runSuiteOrchestrator = new RunSuiteOrchestrator(
            cliPinVerifier, suiteRunner ?? new VouchfxCliSuiteRunner(), registry, workspace, runLock,
            cancellations);
        var explainRunOrchestrator = new ExplainRunOrchestrator(registry, workspace);
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

        // US-S3-05: reads the SAME registry instance run_suite writes and explain_run reads — one
        // registry per server, exactly as US-S3-01 established. It is handed no run lock, and that
        // is the point: get_run_events is read-only, and spec §4.6's "read-only tools are safe to
        // call concurrently" holds structurally because there is nothing here to take a lock with.
        var getRunEventsOrchestrator = new GetRunEventsOrchestrator(registry, workspace);

        // US-S3-03: all three read the SAME registry instance run_suite writes. get_run_status and
        // list_runs are handed no run lock — deliberately, exactly as get_run_events is not: they are
        // read-only, and spec §4.6's "read-only tools are safe to call concurrently" holds
        // structurally because there is nothing in them to take a lock with. cancel_run IS handed it,
        // because distinguishing "another process is running this" from "this entry is residue" needs
        // the one liveness signal the workspace has, and cancel_run is not a read-only tool.
        var getRunStatusOrchestrator = new GetRunStatusOrchestrator(registry);
        var cancelRunOrchestrator = new CancelRunOrchestrator(registry, cancellations, runLock);
        var listRunsOrchestrator = new ListRunsOrchestrator(registry);

        // US-S3-06: the same registry instance again, and the same workspace — get_step_timeline
        // resolves a runId to an events path exactly as get_run_events does, then reads it through the
        // same bounded reader and the same SuiteEventParser explain_run uses. It is handed no run lock,
        // for the reason the read-only tools above are not: there is nothing in it to take one with.
        var getStepTimelineOrchestrator = new GetStepTimelineOrchestrator(registry, workspace);

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
                    getRunEventsOrchestrator,
                    getRunStatusOrchestrator,
                    cancelRunOrchestrator,
                    listRunsOrchestrator,
                    getStepTimelineOrchestrator,
                    workspace)
            ];
            options.ResourceCollection = [.. DocResourceRegistry.CreateAll(), DiagnosticResourceRegistry.Create()];
        });
    }
}
