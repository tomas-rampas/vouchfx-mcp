using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>describe_step_type</c> tool: will describe one step type's full YAML field contract.
/// </summary>
internal static class DescribeStepTypeTool
{
    public const string Name = "describe_step_type";

    private const string Description =
        "Describes one vouchfx step type's full contract: its required and optional YAML " +
        "fields, capture/placeholder behaviour, and a worked .e2e.yaml example. Give it the " +
        "dotted '<family>.<provider>' type name (e.g. 'mq-publish.kafka') exactly as " +
        "list_step_types reports it.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("The dotted '<family>.<provider>' step type to describe, e.g. 'db-assert.postgres'.")]
        string type) =>
        StubToolResult.NotImplemented(Name, "Describing a vouchfx step type's field-level contract");
}
