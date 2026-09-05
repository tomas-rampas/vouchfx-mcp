using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        "Runs one or more vouchfx .e2e.yaml suites through the packaged vouchfx CLI and reports the " +
        "verdict (pass / fail / environment error / inconclusive) once the run completes. Give it " +
        "either 'path' (one suite file) or 'paths' (several files and/or workspace-relative globs " +
        "such as 'e2e/checkout/**', which expand to the *.e2e.yaml files they match) — exactly one " +
        "of the two, never both. Every suite runs sequentially under one runId, which the result " +
        "carries as 'runId' — pass it to get_run_events to read that run's raw event stream. Each " +
        "suite's own outcome comes back in 'specs', and the overall verdict is the worst of them " +
        "(Pass < Inconclusive < Fail < EnvironmentError). Optionally restrict the run to steps or " +
        "scenarios matching one or more tags, attach free-form 'labels' recorded with the run (plain " +
        "text, no secrets — stored verbatim in the run registry ONLY, not in the engine's JSON Lines " +
        "event stream, pending upstream ask U4), and/or cap the WHOLE call with a timeout in " +
        "seconds (1-3600, default 300). Requires the vouchfx CLI on PATH at the version this server " +
        "is pinned to, and every suite must pass the same validation validate_suite performs — a " +
        "missing/mismatched CLI or an invalid suite returns a structured result explaining why, " +
        "without attempting to run anything. Only one run may be active per workspace at a time — " +
        "across separate server processes, not just within one — and a concurrent call is rejected " +
        "immediately (VFX-E-1501, retryable, naming the active runId) rather than queued. " +
        "'wait: false' (asynchronous execution) and 'keepEnvironment: true' are accepted but not yet " +
        "available and are refused with VFX-E-1504. Reports progress as the run proceeds, when the " +
        "client requests it.";

    public static McpServerTool Create(RunSuiteOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        Task<CallToolResult> Handle(
            [Description("Absolute or workspace-relative path to a single .e2e.yaml suite file. Supply this or 'paths', never both. Not glob-expanded — use 'paths' for patterns.")]
            string? path = null,
            [Description("One or more absolute/workspace-relative .e2e.yaml paths, each of which may instead be a workspace-relative glob (e.g. 'e2e/checkout/**') expanding to the *.e2e.yaml files it matches. Supply this or 'path', never both. An entry containing '*' or '?' is ALWAYS read as a pattern here, so a file whose name literally contains one (possible on Linux, illegal in a Windows path component) cannot be named through 'paths' — pass it as 'path', which is never glob-expanded.")]
            string[]? paths = null,
            [Description("Only run steps/scenarios matching one or more of these tags. Omit to run the whole suite.")]
            string[]? tags = null,
            [Description("Abort the run if it has not completed within this many seconds (1-3600). Covers the whole call, not each suite. Omit for the default (300s).")]
            int? timeoutSeconds = null,
            [Description("Free-form key/value metadata recorded with the run (e.g. {\"trigger\":\"agent:author\"}), for correlating it later. Plain text only; stored verbatim, so never put a secret here. Recorded in the run registry only — labels do not reach the engine's JSON Lines event stream (awaits upstream ask U4).")]
            Dictionary<string, string>? labels = null,
            [Description("Leave the environment up after the run for debugging. Only false is available today; true is refused with VFX-E-1504 (awaits upstream ask U4).")]
            bool? keepEnvironment = null,
            [Description("Wait for the run to finish before returning. Only true (the default) is available today; false is refused with VFX-E-1504 (awaits upstream ask U4).")]
            bool? wait = null,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(
                orchestrator,
                new RunSuiteRequest
                {
                    Path = path,
                    Paths = paths,
                    Tags = tags,
                    TimeoutSeconds = timeoutSeconds,
                    Labels = labels,
                    KeepEnvironment = keepEnvironment,
                    Wait = wait,
                },
                progress,
                cancellationToken);

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
        RunSuiteRequest request,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        var progressCounter = 0;

        void OnProgress(string message) => progress?.Report(new ProgressNotificationValue
        {
            Progress = Interlocked.Increment(ref progressCounter),
            Message = message,
        });

        var outcome = await orchestrator.RunAsync(request, OnProgress, cancellationToken);

        return outcome switch
        {
            RunSuiteOutcome.Completed completed =>
                StructuredToolResult.Success(completed.Result),
            RunSuiteOutcome.SuiteInvalid suiteInvalid =>
                RenderSuiteInvalid(suiteInvalid.Validation, suiteInvalid.SuitePath),
            RunSuiteOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),
            RunSuiteOutcome.AmbiguousInput ambiguousInput =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.AmbiguousRunInput, ambiguousInput.Message)),

            // The SAME code a single missing `path` already returns (US-S3-02): "you named nothing
            // that exists" is one answer whether the caller named a file or a pattern, and a host
            // that already handles VFX-E-1002 needs no new branch for the glob case.
            RunSuiteOutcome.NoSuitesMatched noSuitesMatched =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.SuiteFileNotFound, noSuitesMatched.Message)),
            RunSuiteOutcome.OptionUnavailable optionUnavailable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunOptionUnavailable, optionUnavailable.Message)),
            RunSuiteOutcome.CliUnavailable cliUnavailable =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.EngineCliUnavailable, cliUnavailable.Message)),
            RunSuiteOutcome.AlreadyRunning alreadyRunning =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunInProgress, alreadyRunning.Message, BuildRunInProgressDetails(alreadyRunning))),
            RunSuiteOutcome.RunNotRecorded runNotRecorded =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.RunNotRecorded, runNotRecorded.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "run_suite produced an unrecognised outcome.")),
        };
    }

    /// <summary>
    /// Builds <c>VFX-E-1501 RunInProgress</c>'s <c>details</c> — spec §4.6 requires the rejection to
    /// include the active <c>runId</c> — or <see langword="null"/> when the registry could not name
    /// the active run (see <see cref="RunSuiteOutcome.AlreadyRunning.ActiveRunId"/> for the one
    /// window in which that happens).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the first call site in the server to populate <see cref="VfxError.Details"/> at
    /// all</b>, and it is the exact shape that field's own documentation names as its worked example.
    /// It honours the normative constraint stated there: the payload is a single server-minted run id
    /// — <c>run-</c> plus 32 hex characters, shape-checked by <c>RunSuiteOrchestrator</c> before it
    /// ever reaches here — so it carries no caller text, no environment, and nothing that could
    /// require redaction, and it is a few dozen bytes rather than a payload dump.
    /// </para>
    /// <para>
    /// Serialised through <see cref="StructuredToolResult.Options"/>, the same options the
    /// <see cref="VfxError"/> wrapping it travels on, so <c>details</c> cannot acquire a different
    /// escaping or naming convention from the object it is nested inside.
    /// </para>
    /// </remarks>
    private static JsonElement? BuildRunInProgressDetails(RunSuiteOutcome.AlreadyRunning alreadyRunning) =>
        alreadyRunning.ActiveRunId is { } runId
            ? JsonSerializer.SerializeToElement(
                new RunInProgressDetails(runId), typeof(RunInProgressDetails), StructuredToolResult.Options)
            : null;

    /// <summary>
    /// <c>VFX-E-1501</c>'s <c>details</c> payload. A named record rather than an anonymous type so
    /// the wire property name is fixed by an attribute that travels with the type, matching every
    /// other contract record in this server (see <c>Contracts/VfxError</c>'s note on why a naming
    /// POLICY on an options instance is not enough).
    /// </summary>
    /// <param name="RunId">The run currently holding the workspace's run lock.</param>
    private sealed record RunInProgressDetails(
        [property: JsonPropertyName("runId")] string RunId);

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
    /// <para>
    /// <b>Both legs name the suite</b> (a gatekeeper review's MAJOR finding). <c>run_suite</c>'s
    /// pre-flight is all-or-nothing across every suite a call covers, and neither
    /// <see cref="Validation.ValidateSuiteResult"/> nor the guard messages inside it carry a path —
    /// they were written for <c>validate_suite</c>, where the caller named the one file the answer is
    /// about. A forty-suite glob therefore used to answer "this suite is invalid" with no way to tell
    /// WHICH. The data leg gains a <c>path</c> field; the error leg gets the same path prefixed onto
    /// its message. One rendering for both, through
    /// <see cref="Validation.PathSafetyGuard.CapAndSanitisePathForDisplay"/> — the bounded rendering every
    /// caller-supplied path echoed into a response goes through.
    /// </para>
    /// </remarks>
    private static CallToolResult RenderSuiteInvalid(Validation.ValidateSuiteResult validation, string suitePath)
    {
        var displayPath = Validation.PathSafetyGuard.CapAndSanitisePathForDisplay(suitePath);

        return ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure, displayPath)
            ? failure!
            : StructuredToolResult.Success(
                new RunSuiteInvalidPayload(VfxCodeCatalogue.SuiteInvalid, displayPath, validation));
    }
}
