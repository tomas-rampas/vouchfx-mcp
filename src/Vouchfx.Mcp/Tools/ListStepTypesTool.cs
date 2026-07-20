using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>list_step_types</c> tool: will list every step type the pinned vouchfx engine
/// supports.
/// </summary>
internal static class ListStepTypesTool
{
    public const string Name = "list_step_types";

    private const string Description =
        "Lists every step type the pinned vouchfx engine supports, in dotted " +
        "'<family>.<provider>' form (e.g. 'http.rest', 'db-assert.postgres', 'mq-publish.kafka') " +
        "across all eleven step families. Takes no arguments. Call describe_step_type for the " +
        "full field-level contract of any one type this returns.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle() =>
        StubToolResult.NotImplemented(Name, "Listing the vouchfx step-type catalogue");
}
