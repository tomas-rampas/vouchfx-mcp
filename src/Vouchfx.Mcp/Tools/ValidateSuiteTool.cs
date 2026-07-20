using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>validate_suite</c> tool: will validate an <c>.e2e.yaml</c> suite against the vouchfx
/// JSON Schema without running it.
/// </summary>
internal static class ValidateSuiteTool
{
    public const string Name = "validate_suite";

    private const string Description =
        "Validates a vouchfx .e2e.yaml integration test suite against the engine's JSON Schema " +
        "and reports every structural error found (with file and line context), without running " +
        "the suite. Give it the path to the suite file to check.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to validate.")]
        string path) =>
        StubToolResult.NotImplemented(Name, "Validating .e2e.yaml suites against the vouchfx JSON Schema");
}
