using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>describe_step_type</c> tool: describes one step type's full field-level contract
/// (REQ-004).
/// </summary>
internal static class DescribeStepTypeTool
{
    public const string Name = "describe_step_type";

    private const string Description =
        "Describes one vouchfx step type's full contract: its required and optional fields, " +
        "each with its JSON Schema type and description. Give it the dotted " +
        "'<family>.<provider>' type name (e.g. 'mq-publish.kafka') exactly as list_step_types " +
        "reports it. An unknown type returns a tool error listing every valid type rather than " +
        "crashing.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("The dotted '<family>.<provider>' step type to describe, e.g. 'db-assert.postgres'.")]
        string type)
    {
        var info = StepTypeCatalogue.Find(type);
        if (info is null)
        {
            var knownTypes = string.Join(", ", StepTypeCatalogue.All.Select(t => t.Type));
            // The type argument is caller-supplied (M1): sanitised before it is spliced into the
            // error message.
            return StructuredToolResult.Error(
                $"Unknown step type '{TextSanitiser.SanitiseForDisplay(type)}'. Known types: {knownTypes}.");
        }

        return StructuredToolResult.Success(info);
    }
}
