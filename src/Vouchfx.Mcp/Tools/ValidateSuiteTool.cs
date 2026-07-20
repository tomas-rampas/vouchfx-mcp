using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>validate_suite</c> tool: validates an <c>.e2e.yaml</c> suite against the vouchfx JSON
/// Schema without running it (REQ-003).
/// </summary>
/// <remarks>
/// A thin MCP-facing wrapper: the actual validation pipeline runs isolated in a child process, via
/// <see cref="ValidationWorkerClient"/> — see that type's remarks for why untrusted YAML content
/// must never be parsed directly inside this long-lived server process.
/// </remarks>
internal static class ValidateSuiteTool
{
    public const string Name = "validate_suite";

    private const string Description =
        "Validates a vouchfx .e2e.yaml integration test suite against the engine's JSON Schema " +
        "and reports every structural error found (with a JSON pointer and, where derivable, a " +
        "YAML line number), without running the suite. Give it the path to the suite file to " +
        "check. Always returns a structured result — valid:true, or valid:false with an errors " +
        "list — even when the file is missing or the YAML itself is malformed. It never throws " +
        "for validation failures: every bad input, timeout, or worker failure is returned as a " +
        "structured result.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static async Task<CallToolResult> Handle(
        [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to validate.")]
        string path,
        CancellationToken cancellationToken) =>
        StructuredToolResult.Success(await ValidationWorkerClient.ValidateAsync(path, cancellationToken: cancellationToken));
}
