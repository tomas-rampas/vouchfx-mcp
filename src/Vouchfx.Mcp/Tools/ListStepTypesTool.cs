using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>list_step_types</c> tool: lists every step type the pinned vouchfx engine supports,
/// grouped by family, from the live engine catalogue export (REQ-010).
/// </summary>
internal static class ListStepTypesTool
{
    public const string Name = "list_step_types";

    private const string Description =
        "Lists every step type the pinned vouchfx engine supports, in dotted " +
        "'<family>.<provider>' form (e.g. 'http.rest', 'db-assert.postgres', 'mq-publish.kafka') " +
        "grouped by family, with each type's captureSupported flag and familyIntent one-liner " +
        "from the engine's live `vouchfx list --json` export. Takes no arguments. Requires the " +
        "pinned vouchfx CLI on PATH (Spec A rich catalogue). Call describe_step_type for the " +
        "full required/optional field contract of any one type this returns.";

    public static McpServerTool Create(LiveStepCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        async Task<CallToolResult> Handle(CancellationToken cancellationToken) =>
            await HandleAsync(catalogue, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        LiveStepCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        var load = await catalogue.GetOrLoadAsync(cancellationToken);
        if (load is StepCatalogueLoadResult.Failed failed)
        {
            return StructuredToolResult.Error(failed.Message);
        }

        var ok = (StepCatalogueLoadResult.Ok)load;
        var families = ok.StepTypes
            .GroupBy(t => t.Family, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StepFamilyGroup(
                g.Key,
                g.First().FamilyIntent,
                g.OrderBy(t => t.Type, StringComparer.Ordinal)
                    .Select(t => new StepTypeSummary(
                        t.Type,
                        t.Provider,
                        t.Description,
                        t.CaptureSupported,
                        t.FamilyIntent))
                    .ToArray()))
            .ToArray();

        return StructuredToolResult.Success(new ListStepTypesResult(families));
    }
}

/// <summary>The <c>list_step_types</c> result contract: every step type, grouped by family.</summary>
internal sealed record ListStepTypesResult(IReadOnlyList<StepFamilyGroup> Families);

/// <summary>One step family and every type registered under it.</summary>
/// <param name="Family">Family id (e.g. <c>http</c>).</param>
/// <param name="FamilyIntent">Short intent one-liner shared by types in this family.</param>
/// <param name="Types">Types registered under this family.</param>
internal sealed record StepFamilyGroup(
    string Family,
    string FamilyIntent,
    IReadOnlyList<StepTypeSummary> Types);

/// <summary>A one-line summary of a single step type, as returned by <c>list_step_types</c>.</summary>
internal sealed record StepTypeSummary(
    string Type,
    string Provider,
    string? Description,
    bool CaptureSupported,
    string FamilyIntent);
