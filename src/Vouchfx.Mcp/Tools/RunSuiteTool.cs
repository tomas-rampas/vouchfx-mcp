using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
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
                RenderSuiteInvalid(suiteInvalid.Validation),
            RunSuiteOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),
            RunSuiteOutcome.CliUnavailable cliUnavailable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EngineCliUnavailable, cliUnavailable.Message)),
            RunSuiteOutcome.AlreadyRunning alreadyRunning =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunInProgress, alreadyRunning.Message)),
            RunSuiteOutcome.RunNotRecorded runNotRecorded =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunNotRecorded, runNotRecorded.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "run_suite produced an unrecognised outcome.")),
        };
    }

    /// <summary>
    /// Renders EDGE-003's "suite failed pre-flight validation" outcome.
    /// </summary>
    /// <remarks>
    /// <b>The invariant this method exists to protect:</b> a suite that is genuinely INVALID comes
    /// back through <see cref="StructuredToolResult.Success"/> with <c>isError</c> false, carrying
    /// <see cref="VfxCodeCatalogue.SuiteInvalid"/> — an MCP client keying off <c>isError</c> has
    /// never seen an invalid suite as a tool failure and must not start now.
    /// <para>
    /// The pre-flight check can nonetheless fail for a reason that is NOT a statement about the
    /// suite — the file is missing, unreadable, on a network path, or the validation worker timed
    /// out. In those cases the orchestrator still reports <c>SuiteInvalid</c> (it correctly says
    /// only "pre-flight did not pass"), but there is no validation verdict to hand back as data, so
    /// the shared <see cref="ValidationOutcomeRenderer"/> — the SAME classification
    /// <c>validate_suite</c> applies, so the two tools cannot disagree about one file — turns it
    /// into a tool error instead.
    /// </para>
    /// </remarks>
    private static CallToolResult RenderSuiteInvalid(Validation.ValidateSuiteResult validation) =>
        ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure)
            ? failure!
            : StructuredToolResult.Success(
                new RunSuiteInvalidPayload(VfxCodeCatalogue.SuiteInvalid, validation));
}
