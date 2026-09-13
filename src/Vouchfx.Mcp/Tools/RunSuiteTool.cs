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

    /// <summary>
    /// How many findings ONE entry of the pre-flight-failure payload's <c>invalidSuites</c> array
    /// carries before the rest are counted into its <c>omittedErrorCount</c> (vouchfx-mcp#78).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ten, matching <c>FailProposalBuilder.MaxProposals</c> and
    /// <c>SpecEditProposalBuilder.MaxProposals</c></b> — this server's existing "enough to act on in
    /// one repair round" number, reused rather than a fourth figure invented beside them. Schema
    /// findings cascade: a suite with sixty of them almost always has one mistake near the top of the
    /// file that produced fifty-nine consequences, so the first ten describe the defect and the rest
    /// describe the first ten.
    /// </para>
    /// <para>
    /// Applied ONLY to the additive <c>invalidSuites</c> array. The first suite's own
    /// <c>validation.errors</c> is passed through uncapped, exactly as it has always been — see
    /// <see cref="FitWithinBudget"/> for that boundary and why it is drawn there.
    /// </para>
    /// </remarks>
    internal const int MaxErrorsPerInvalidSuite = 10;

    /// <summary>
    /// The response-size class the pre-flight-failure payload is fitted to — the same 64&#160;KB
    /// figure <c>GetRunEventsOrchestrator.MaxResponseBytes</c>,
    /// <c>GetStepTimelineOrchestrator.MaxResponseBytes</c>,
    /// <c>GetRunArtifactsOrchestrator.MaxResponseBytes</c> and
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c> name.
    /// </summary>
    /// <remarks>
    /// <b>Scoped to the EDGE-003 failure payload, deliberately.</b> <c>run_suite</c>'s SUCCESS result
    /// has never had a response budget — its size is bounded by the engine's own step and spec counts
    /// rather than by anything this server chooses — and vouchfx-mcp#78 does not give it one. Adding a
    /// budget to a shape no caller asked about would be an unrelated contract change smuggled in
    /// beside an additive field.
    /// </remarks>
    internal const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// The bare payload's own budget — half of <see cref="MaxResponseBytes"/>, because
    /// <see cref="StructuredToolResult.Success"/> carries every payload TWICE (once as
    /// <c>structuredContent</c>, once as an escaped text content block, measured at 2.213x rather than
    /// 2x — <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c> is the single authority on that
    /// number). Halving is the same large-and-necessary but not-sufficient correction every other
    /// bounded tool here applies.
    /// </summary>
    internal const int EffectivePreflightBudgetBytes = MaxResponseBytes / 2;

    /// <summary>
    /// How many candidate <c>invalidSuites</c> lengths <see cref="FitWithinBudget"/> measures before
    /// falling back to the entry-free shape. Mirrors <c>GetRunArtifactsOrchestrator.MaxFitProbes</c>,
    /// and for its reason: a fixed, small number of serialisations rather than a search that could run
    /// long on a pathological payload.
    /// </summary>
    private const int MaxFitProbes = 8;

    /// <summary>
    /// The size probe, mirroring every sibling tool's so a measured figure here is comparable — and,
    /// more to the point, so it carries the same <c>JavaScriptEncoder.Default</c> the wire does.
    /// </summary>
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

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
                RenderSuiteInvalid(suiteInvalid),
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
    /// <para>
    /// <b>Which leg is chosen is decided by the FIRST failure, and that is exactly the pre-#78
    /// behaviour rather than a new rule</b> (vouchfx-mcp#78). Spec §4.4 makes an error result's whole
    /// body a single <see cref="VfxError"/>, so "the validity of suite N was never determined" cannot
    /// be folded in beside data about the others; and before #78 the pre-flight stopped at the first
    /// invalid suite, so the first failure IS the suite that has always decided this. Keying the split
    /// off <c>Failures[0]</c> therefore leaves <c>isError</c> identical for every input it was
    /// identical for before — a widening of the data leg must not silently re-classify calls. The
    /// orchestrator's loop stops as soon as that first failure is a call failure, for the same reason:
    /// nothing after it can change this decision.
    /// </para>
    /// <para>
    /// <b>The SAME classification is then applied per entry</b>, not just to the first — see
    /// <see cref="BuildInvalidSuiteReports"/>. A later failure of the "never determined" class is
    /// counted into <see cref="RunSuiteInvalidPayload.UndeterminedSuiteCount"/> rather than described,
    /// which is what keeps the promise this method's first paragraph makes ("a suite whose validity was
    /// never determined is a tool error, never this payload") true of every suite in a multi-suite call
    /// rather than only of the one that chose the leg.
    /// </para>
    /// </remarks>
    private static CallToolResult RenderSuiteInvalid(RunSuiteOutcome.SuiteInvalid outcome)
    {
        var first = outcome.Failures[0];
        var displayPath = Validation.PathSafetyGuard.CapAndSanitisePathForDisplay(first.SuitePath);

        return ValidationOutcomeRenderer.TryRenderCallFailure(first.Validation, out var failure, displayPath)
            ? failure!
            : StructuredToolResult.Success(BuildInvalidSuitesPayload(outcome));
    }

    /// <summary>
    /// Builds the bounded <c>VFX-D-1100</c> payload for <paramref name="outcome"/> (vouchfx-mcp#78).
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> purely so the size guarantee can be asserted against a synthetic
    /// worst case — twenty-five suites of maximally long findings is not something a real pre-flight
    /// can be driven to on demand, and the bound must hold anyway. The DECISION of whether this
    /// payload is returned at all still belongs to <see cref="RenderSuiteInvalid"/>.
    /// </remarks>
    internal static RunSuiteInvalidPayload BuildInvalidSuitesPayload(RunSuiteOutcome.SuiteInvalid outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var first = outcome.Failures[0];
        var (reports, undetermined) = BuildInvalidSuiteReports(outcome.Failures);

        var candidate = new RunSuiteInvalidPayload(
            VfxCodeCatalogue.SuiteInvalid,
            Validation.PathSafetyGuard.CapAndSanitisePathForDisplay(first.SuitePath),
            first.Validation,
            reports,
            outcome.OmittedInvalidSuiteCount,

            // Both sides of the collection cap, in the one wire counter: the undetermined failures
            // among the COLLECTED ones, which this boundary classifies, plus those the orchestrator
            // had to classify itself because their results did not survive the cap.
            undetermined + outcome.UndeterminedPastCapCount);

        // MEASURED, not assumed to fit — see FitWithinBudget.
        return FitWithinBudget(candidate);
    }

    /// <summary>
    /// Splits the collected pre-flight failures into the DETERMINED-invalid ones — rendered as bounded
    /// wire entries — and a count of those whose validity was never determined (vouchfx-mcp#78).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split is <see cref="ValidationOutcomeRenderer.IsCallFailure"/>'s, not a second rule
    /// written here</b> — the same classification that decides which LEG this whole result takes,
    /// applied per entry. Two copies of it are two copies that can disagree, and the disagreement
    /// available here is the expensive one: an entry claiming to diagnose a suite that was never read.
    /// </para>
    /// <para>
    /// A call failure is counted rather than described because there is nothing honest to put in an
    /// entry: spec §4.4 reserves the single <see cref="VfxError"/> body for that class of answer, and
    /// the code it carries may be one this server does not catalogue at all (it crosses a process
    /// boundary from the worker, which is why <see cref="ValidationOutcomeRenderer"/> fails an unknown
    /// code CLOSED). <see cref="RunSuiteInvalidPayload.UndeterminedSuiteCount"/> records the fact
    /// without asserting a diagnosis. By construction these are never the FIRST failure — the
    /// orchestrator stops walking as soon as the first one is — so the list this method returns is
    /// never empty; the size fit downstream can still shed it to empty — see
    /// <see cref="FitWithinBudget"/>.
    /// </para>
    /// </remarks>
    private static (InvalidSuiteReport[] Reports, int UndeterminedCount) BuildInvalidSuiteReports(
        IReadOnlyList<PreflightSuiteFailure> failures)
    {
        var reports = new List<InvalidSuiteReport>(failures.Count);
        var undetermined = 0;

        foreach (var failure in failures)
        {
            if (ValidationOutcomeRenderer.IsCallFailure(failure.Validation))
            {
                undetermined++;
                continue;
            }

            var errors = failure.Validation.Errors;
            var kept = Math.Min(errors.Count, MaxErrorsPerInvalidSuite);

            reports.Add(new InvalidSuiteReport(
                Validation.PathSafetyGuard.CapAndSanitisePathForDisplay(failure.SuitePath),
                kept == errors.Count ? errors : errors.Take(kept).ToArray(),
                errors.Count - kept));
        }

        return (reports.ToArray(), undetermined);
    }

    /// <summary>
    /// Sheds trailing <c>invalidSuites</c> entries until the payload serialises inside
    /// <see cref="EffectivePreflightBudgetBytes"/>, restating
    /// <see cref="RunSuiteInvalidPayload.OmittedInvalidSuiteCount"/> against the total each time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same measured-fit shape <c>GetRunArtifactsOrchestrator.FitWithinBudget</c> and
    /// <c>GetStepTimelineOrchestrator</c> use, and for the same reason: a static worst-case
    /// calculation over caller-authored text (a path, a schema message, a JSON pointer built from the
    /// document's own keys) is arithmetic about a bound nobody can actually hold, whereas serialising
    /// the candidate answers the question directly.
    /// </para>
    /// <para>
    /// <b>What this does and does not bound.</b> It bounds the payload this method BUILDS. The
    /// pre-existing <c>validation</c> field is passed through uncapped, exactly as it was before
    /// vouchfx-mcp#78 — a single suite's findings have never been capped anywhere in this server, and
    /// capping them here would silently reshape <c>run_suite</c>'s existing wire as a side effect of an
    /// additive story. So a payload CAN still exceed the budget, on the strength of that one
    /// pre-existing field alone, exactly as it could before the story.
    /// </para>
    /// <para>
    /// <b>The property the story actually needs is narrower, and it does hold.</b> When the fit sheds
    /// everything, what remains of the ADDITIVE portion is a fixed, content-independent
    /// <c>"invalidSuites":[]</c> plus the two integer counters — a constant on the order of eighty
    /// bytes, not zero (the exact figure is measured in <c>RunSuiteInvalidPayloadBoundsTests</c>, which
    /// is where a number belongs rather than in prose that cannot be checked). So the fields this story
    /// adds contribute a bounded constant in the worst case and can never be what carries a response
    /// over the class; an over-budget response is one the pre-#78 shape would have been over-budget for
    /// too.
    /// </para>
    /// <para>
    /// <b>The probe measures the bare payload, without the <c>meta</c> stamp</b>
    /// <see cref="StructuredToolResult.Success"/> appends — the established convention every sibling
    /// budget here follows, and the reason <c>ToolMeta</c>'s own measurement is recorded separately
    /// as a documented baseline rather than folded into each tool's constant.
    /// </para>
    /// </remarks>
    private static RunSuiteInvalidPayload FitWithinBudget(RunSuiteInvalidPayload candidate)
    {
        var bytes = SerialisedByteCount(candidate);
        if (bytes <= EffectivePreflightBudgetBytes || candidate.InvalidSuites.Count == 0)
        {
            return candidate;
        }

        // Every invalid suite the pre-flight found, including those MaxReportedInvalidSuites already
        // dropped — so the reported omission count stays absolute rather than restarting at this point.
        var invalidTotal = candidate.InvalidSuites.Count + candidate.OmittedInvalidSuiteCount;
        var keep = candidate.InvalidSuites.Count;

        for (var probe = 0; probe < MaxFitProbes && keep > 0; probe++)
        {
            var rescaled = (int)((long)keep * EffectivePreflightBudgetBytes / bytes);
            keep = Math.Clamp(rescaled, keep / 2, keep - 1);

            var shed = WithInvalidSuiteCount(candidate, invalidTotal, keep);
            bytes = SerialisedByteCount(shed);
            if (bytes <= EffectivePreflightBudgetBytes)
            {
                return shed;
            }
        }

        return WithInvalidSuiteCount(candidate, invalidTotal, keep: 0);
    }

    /// <summary>
    /// The same payload with its <c>invalidSuites</c> list shortened to <paramref name="keep"/>
    /// entries and the omission count restated against <paramref name="invalidTotal"/>.
    /// </summary>
    private static RunSuiteInvalidPayload WithInvalidSuiteCount(
        RunSuiteInvalidPayload candidate, int invalidTotal, int keep) =>
        candidate with
        {
            InvalidSuites = candidate.InvalidSuites.Take(keep).ToArray(),
            OmittedInvalidSuiteCount = invalidTotal - keep,
        };

    private static int SerialisedByteCount(RunSuiteInvalidPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, SizeProbeOptions).Length;
}
