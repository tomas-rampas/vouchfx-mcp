using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Scaffold;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>scaffold_suite</c> tool: generates a machine-drafted, catalogue-grounded, schema-valid
/// <c>.e2e.yaml</c> skeleton from structured step types, ids, and an environment outline (REQ-007).
/// Free text is not a parameter — the host LLM chooses types via <c>list_step_types</c> first.
/// </summary>
internal static class ScaffoldSuiteTool
{
    public const string Name = "scaffold_suite";

    private const string Description =
        "Generates a machine-drafted, schema-valid vouchfx .e2e.yaml suite skeleton from " +
        "structured arguments only (never free text): an ordered list of steps (each with id " +
        "and dotted family.provider type, optional label), plus optional services and " +
        "dependencies for the environment outline. Invokes the pinned vouchfx CLI " +
        "`scaffold --intent` so CLI and MCP cannot drift. Returns the YAML content with a " +
        "provenance comment (human review required before trust). Unknown step types and other " +
        "scaffold validation failures return a structured tool error naming the problem. " +
        "Typical host workflow: free-text goal (LLM) → list_step_types → scaffold_suite → " +
        "fill semantics → validate_suite → run_suite. Requires the vouchfx CLI on PATH at " +
        "ENGINE_PIN with Spec B scaffold support.";

    public static McpServerTool Create(ScaffoldSuiteOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        Task<CallToolResult> Handle(
            [Description(
                "Ordered steps to emit. Each item needs id (unique within the suite) and type " +
                "(dotted family.provider from list_step_types, e.g. http.rest). Optional label " +
                "is a short human comment only.")]
            ScaffoldStepRequest[] steps,
            [Description(
                "Optional environment.services outline: each item has name and optional image " +
                "(defaults in the engine when omitted).")]
            ScaffoldServiceRequest[]? services = null,
            [Description(
                "Optional environment.dependencies outline: each item has name and type " +
                "(e.g. postgres, kafka) supported by the engine topology mapper.")]
            ScaffoldDependencyRequest[]? dependencies = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(orchestrator, steps, services, dependencies, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            // Emits a draft document; does not mutate the caller's filesystem or destroy data.
            ReadOnly = false,
            Destructive = false,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        ScaffoldSuiteOrchestrator orchestrator,
        ScaffoldStepRequest[]? steps,
        ScaffoldServiceRequest[]? services,
        ScaffoldDependencyRequest[]? dependencies,
        CancellationToken cancellationToken)
    {
        // MCP clients may omit required arrays; treat null the same as empty for the local gate.
        IReadOnlyList<ScaffoldStepRequest> stepList = steps ?? [];

        var outcome = await orchestrator.ScaffoldAsync(stepList, services, dependencies, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            ScaffoldSuiteOutcome.Completed completed =>
                StructuredToolResult.Success(completed.Result),
            ScaffoldSuiteOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(invalidArgument.Message),
            ScaffoldSuiteOutcome.CliUnavailable cliUnavailable =>
                StructuredToolResult.Error(cliUnavailable.Message),
            ScaffoldSuiteOutcome.ScaffoldFailed scaffoldFailed =>
                StructuredToolResult.Error(scaffoldFailed.Message),
            _ =>
                StructuredToolResult.Error("scaffold_suite produced an unrecognised outcome."),
        };
    }
}
