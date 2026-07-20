using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>list_step_types</c> tool: lists every step type the pinned vouchfx engine supports,
/// grouped by family (REQ-004).
/// </summary>
internal static class ListStepTypesTool
{
    public const string Name = "list_step_types";

    private const string Description =
        "Lists every step type the pinned vouchfx engine supports, in dotted " +
        "'<family>.<provider>' form (e.g. 'http.rest', 'db-assert.postgres', 'mq-publish.kafka') " +
        "grouped by family, with a one-line description per type where the schema carries one. " +
        "Takes no arguments. Call describe_step_type for the full field-level contract of any " +
        "one type this returns.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle()
    {
        var families = StepTypeCatalogue.All
            .GroupBy(t => t.Family, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StepFamilyGroup(
                g.Key,
                g.OrderBy(t => t.Type, StringComparer.Ordinal)
                    .Select(t => new StepTypeSummary(t.Type, t.Provider, t.Description))
                    .ToArray()))
            .ToArray();

        return StructuredToolResult.Success(new ListStepTypesResult(families));
    }
}

/// <summary>The <c>list_step_types</c> result contract: every step type, grouped by family.</summary>
internal sealed record ListStepTypesResult(IReadOnlyList<StepFamilyGroup> Families);

/// <summary>One step family and every type registered under it.</summary>
internal sealed record StepFamilyGroup(string Family, IReadOnlyList<StepTypeSummary> Types);

/// <summary>A one-line summary of a single step type, as returned by <c>list_step_types</c>.</summary>
internal sealed record StepTypeSummary(string Type, string Provider, string? Description);
