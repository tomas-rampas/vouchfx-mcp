using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>run_suite</c> tool: runs an <c>.e2e.yaml</c> suite through the packaged vouchfx CLI and
/// reports its taxonomy-faithful verdict (REQ-006, EDGE-001, EDGE-002, EDGE-003).
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: every gate (argument safety, EDGE-003 pre-validation, REQ-008's CLI
/// handshake, single-flight concurrency) and the run itself live in
/// <see cref="RunSuiteOrchestrator"/> — see that type's remarks for the full ordering and rationale.
/// This type's only jobs are (1) translating <see cref="RunSuiteOrchestrator"/>'s neutral
/// <c>Action&lt;string&gt;</c> progress callback into the MCP SDK's
/// <see cref="IProgress{ProgressNotificationValue}"/> shape, and (2) mapping each
/// <see cref="RunSuiteOutcome"/> case to the right <see cref="CallToolResult"/> shape.
/// </remarks>
internal static class RunSuiteTool
{
    public const string Name = "run_suite";

    private const string Description =
        "Runs a vouchfx .e2e.yaml suite through the packaged vouchfx CLI and reports its verdict " +
        "(pass / fail / environment error / inconclusive) once the run completes. Give it the " +
        "suite path; optionally restrict the run to steps or scenarios matching one or more " +
        "tags, and/or cap the whole run with a timeout in seconds (1-3600, default 300). Requires " +
        "the vouchfx CLI on PATH at the version this server is pinned to, and the suite must pass " +
        "the same validation validate_suite performs — a missing/mismatched CLI or an invalid " +
        "suite returns a structured result explaining why, without attempting to run anything. " +
        "Only one run may be active on this server at a time; a concurrent call is rejected " +
        "immediately. Reports progress as the run proceeds, when the client requests it.";

    public static McpServerTool Create(RunSuiteOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        Task<CallToolResult> Handle(
            [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to run.")]
            string path,
            [Description("Only run steps/scenarios matching one or more of these tags. Omit to run the whole suite.")]
            string[]? tags = null,
            [Description("Abort the run if it has not completed within this many seconds (1-3600). Omit for the default (300s).")]
            int? timeoutSeconds = null,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(orchestrator, path, tags, timeoutSeconds, progress, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = false,
            Destructive = false,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        RunSuiteOrchestrator orchestrator,
        string path,
        string[]? tags,
        int? timeoutSeconds,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var progressCounter = 0;

        void OnProgress(string message) => progress?.Report(new ProgressNotificationValue
        {
            Progress = Interlocked.Increment(ref progressCounter),
            Message = message,
        });

        var outcome = await orchestrator.RunAsync(path, tags, timeoutSeconds, OnProgress, cancellationToken);

        return outcome switch
        {
            RunSuiteOutcome.Completed completed =>
                StructuredToolResult.Success(completed.Result),
            RunSuiteOutcome.SuiteInvalid suiteInvalid =>
                StructuredToolResult.Success(new RunSuiteInvalidPayload("suite-invalid", suiteInvalid.Validation)),
            RunSuiteOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(invalidArgument.Message),
            RunSuiteOutcome.CliUnavailable cliUnavailable =>
                StructuredToolResult.Error(cliUnavailable.Message),
            RunSuiteOutcome.AlreadyRunning alreadyRunning =>
                StructuredToolResult.Error(alreadyRunning.Message),
            _ =>
                StructuredToolResult.Error("run_suite produced an unrecognised outcome."),
        };
    }
}
