using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>get_schema</c> tool (Sprint 2 / US-S2-01): serves the vouchfx composed JSON Schema — the
/// whole document or one addressable section of it — as a schema document or a markdown digest.
/// </summary>
/// <remarks>
/// <para>
/// A thin MCP-facing wrapper, like <see cref="PlanCoverageTool"/>: section addressing, format
/// selection, the summary rendering, and the live cross-verification all live in
/// <see cref="GetSchemaOrchestrator"/> — see that type's remarks for why the vendored copy is what
/// gets served even when a pinned CLI is present.
/// </para>
/// <para>
/// <b>Belongs to the CLI-FREE/CLI-optional class</b> alongside <see cref="ValidateSuiteTool"/>,
/// <see cref="SearchDocsTool"/>, and <see cref="ExplainDiagnosticTool"/> — it never fails for want
/// of an engine. It is the only tool in that class that USES the CLI when one happens to be
/// present, and only to check its own embedded document, never to obtain content.
/// </para>
/// </remarks>
internal static class GetSchemaTool
{
    public const string Name = "get_schema";

    private const string Description =
        "Returns the vouchfx .e2e.yaml language's composed JSON Schema — the whole document " +
        "('full', the default), or one section of it: 'metadata', 'environment', 'variables', " +
        "'steps' (the common step object), or 'step:<family>.<provider>' for one step type's own " +
        "definition (e.g. 'step:http.rest'). " +
        "Use it to author or repair a suite against the exact contract validate_suite enforces, or " +
        "as the machine-readable companion to describe_step_type's human-facing catalogue entry. " +
        "format 'json-schema' (the default) returns the schema subtree itself; format 'summary' " +
        "returns a short markdown digest built only from the schema's own field descriptions, " +
        "capped at 8 KB — useful when the full document is larger than needed. Works fully offline " +
        "from the vendored schema this server embeds at its pinned engine commit; when a matching " +
        "vouchfx CLI IS installed, the embedded copy is cross-checked against that engine's own " +
        "`vouchfx schema` export and any divergence is reported as a diagnostic on the (still " +
        "successful) result. An unknown section or step type returns a structured tool error, never " +
        "an empty success.";

    public static McpServerTool Create(GetSchemaOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        Task<CallToolResult> Handle(
            [Description(
                "Which part of the schema to return: 'full' (default), 'metadata', 'environment', " +
                "'variables', 'steps', or 'step:<family>.<provider>' for a single step type (call " +
                "list_step_types for the current set of dotted type names). Case-sensitive.")]
            string? section = null,
            [Description(
                "'json-schema' (default) for the schema subtree itself, or 'summary' for a markdown " +
                "digest of the section's own field descriptions, capped at 8 KB. Case-sensitive.")]
            string? format = null,
            CancellationToken cancellationToken = default) =>
            HandleAsync(orchestrator, section, format, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            // Read-only in the strongest sense available to this server: no suite file is touched,
            // no process is spawned for CONTENT (the optional `vouchfx schema` probe is a read of
            // the engine's own embedded document), and nothing outside this assembly's manifest
            // resources is read. Matches every other read-only tool's flag choice — see
            // PlanCoverageTool's own note on why Destructive is deliberately not set alongside it.
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        GetSchemaOrchestrator orchestrator,
        string? section,
        string? format,
        CancellationToken cancellationToken)
    {
        var outcome = await orchestrator.GetSchemaAsync(section, format, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            GetSchemaOutcome.Completed completed =>
                StructuredToolResult.Success(completed.Result),
            // A live/vendored MISMATCH is deliberately absent from this switch: it rides the
            // Completed arm above as a Diagnostic inside the result (spec §4.4 — diagnostics are
            // data on a successful call), never as an isError. The caller still received a usable
            // schema; only the environment is in question.
            GetSchemaOutcome.SectionNotFound sectionNotFound =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.SchemaSectionNotFound, sectionNotFound.Message)),
            GetSchemaOutcome.InvalidArgument invalidArgument =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.InvalidToolArgument, invalidArgument.Message)),
            _ =>
                StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                    VfxCodeCatalogue.UnrecognisedOutcome, "get_schema produced an unrecognised outcome.")),
        };
    }
}
