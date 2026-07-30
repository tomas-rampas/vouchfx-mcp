using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Planning;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>plan_coverage</c> tool (Spec D / M3 Planner): a deterministic, read-only coverage-and-gap
/// analysis over a declared <c>.e2e.yaml</c> suite set, an optional event history, and the pinned
/// engine's live step catalogue (REQ-012). Every gap finding carries the hand-off hints
/// <c>scaffold_suite</c> consumes unchanged, so a host can go
/// <c>plan_coverage</c> → <c>scaffold_suite</c> → <c>validate_suite</c> → <c>run_suite</c> without
/// leaving MCP.
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: the pin handshake, argument safety, and the CLI invocation itself all
/// live in <see cref="PlanCoverageOrchestrator"/> — see that type's remarks for the full pipeline and
/// the deliberate <c>--fail-on-gap</c> omission. This type's only job is mapping each
/// <see cref="PlanCoverageOutcome"/> case to the right <see cref="CallToolResult"/> shape.
/// </remarks>
internal static class PlanCoverageTool
{
    public const string Name = "plan_coverage";

    private const string Description =
        "Runs the vouchfx Planner's deterministic, read-only coverage-and-gap analysis over a " +
        "declared .e2e.yaml suite set (a directory searched recursively, or a single suite file), " +
        "an optional JSON Lines event history, and the pinned engine's live step catalogue. " +
        "Reports which declared suites/steps/dependencies were never exercised by the analysed " +
        "history, which registered step type would assert a seam that is exercised but never " +
        "verified, and which steps have become stale/flaky/fragile/inconclusive-prone. A call that " +
        "finds gaps is a SUCCESSFUL result returning findings, not an error -- gaps are the data " +
        "this tool exists to surface. Every coverage/vocabulary gap finding carries a suggested " +
        "dotted step type and step id that feeds scaffold_suite's own steps[].type/steps[].id " +
        "unchanged. Never writes a suite file, never calls a model, never invokes git. Optional " +
        "threshold overrides mirror `vouchfx plan`'s own flags (engine defaults: staleDays 30, " +
        "flakyMinRuns 2, fragileMinEnvErrors 2, inconclusiveMin 2). Typical host workflow: " +
        "plan_coverage -> scaffold_suite -> validate_suite -> run_suite. Requires the vouchfx CLI " +
        "on PATH at ENGINE_PIN with M3 Planner support (`vouchfx plan --json`); a missing/" +
        "mismatched CLI or an invalid suite path returns a structured tool error, never a hang.";

    public static McpServerTool Create(PlanCoverageOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        Task<CallToolResult> Handle(
            [Description(
                "Directory to search recursively for *.e2e.yaml suites, or a single *.e2e.yaml " +
                "file -- the declared universe to analyse. An empty directory (zero discovered " +
                "suites) returns a structured tool error.")]
            string path,
            [Description(
                "Path to a JSON Lines event history file, or a directory of *.jsonl files -- the " +
                "artefact `vouchfx run --events` writes. Omit for no history: every declared " +
                "suite/step is then reported never-run (a valid, successful analysis).")]
            string? eventsPath = null,
            [Description(
                "Override: a step is 'stale' when its last observed step-completed event is more " +
                "than this many days before the newest event in the analysed history. Omit for " +
                "the engine default (30).")]
            int? staleDays = null,
            [Description(
                "Override: a step is 'flaky' when it shows at least one Pass and at least one " +
                "Fail step-completed verdict across at least this many distinct runs. Omit for " +
                "the engine default (2).")]
            int? flakyMinRuns = null,
            [Description(
                "Override: a step is 'fragile' when it shows at least this many EnvironmentError " +
                "step-completed outcomes. Omit for the engine default (2).")]
            int? fragileMinEnvErrors = null,
            [Description(
                "Override: a step is 'inconclusive-prone' when it shows at least this many " +
                "Inconclusive step-completed outcomes. Omit for the engine default (2).")]
            int? inconclusiveMin = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(
                orchestrator, path, eventsPath, staleDays, flakyMinRuns, fragileMinEnvErrors,
                inconclusiveMin, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            // Deterministic, read-only analysis (REQ-013): never writes, modifies, or deletes a
            // suite file, never calls a model, never invokes git. Matches how every other
            // read-only tool in this repo (validate_suite / list_step_types / describe_step_type /
            // search_docs / explain_run / diagnose_run) sets this flag -- none of them additionally
            // sets Destructive; that combination is reserved for the two CLI-invoking, non-read-only
            // tools (run_suite / scaffold_suite), which set ReadOnly = false, Destructive = false.
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        PlanCoverageOrchestrator orchestrator,
        string path,
        string? eventsPath,
        int? staleDays,
        int? flakyMinRuns,
        int? fragileMinEnvErrors,
        int? inconclusiveMin,
        CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.PlanAsync(
                path, eventsPath, staleDays, flakyMinRuns, fragileMinEnvErrors, inconclusiveMin, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            PlanCoverageOutcome.Completed completed =>
                StructuredToolResult.Success(completed.Result),
            PlanCoverageOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(invalidArgument.Message),
            PlanCoverageOutcome.CliUnavailable cliUnavailable =>
                StructuredToolResult.Error(cliUnavailable.Message),
            PlanCoverageOutcome.PlanFailed planFailed =>
                StructuredToolResult.Error(planFailed.Message),
            _ =>
                StructuredToolResult.Error("plan_coverage produced an unrecognised outcome."),
        };
    }
}
