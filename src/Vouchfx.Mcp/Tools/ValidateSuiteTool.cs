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
        "check. A suite that is merely INVALID is a successful call: valid:true, or valid:false " +
        "with an errors list carrying VFX-D-#### diagnostic codes — malformed YAML and schema " +
        "violations both come back that way, never as a tool error. A call that could not be " +
        "performed at all (the file is missing or unreadable, the path is a network location, or " +
        "the isolated validation worker timed out or failed) returns a tool error carrying a " +
        "single VFX-E-#### error object instead, because the suite's validity was never " +
        "determined. It never throws for either case.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static async Task<CallToolResult> Handle(
        [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to validate.")]
        string path,
        CancellationToken cancellationToken)
    {
        var validation = await ValidationWorkerClient.ValidateAsync(path, cancellationToken: cancellationToken);

        // US-S1-04: a result whose problems are all diagnostics is still a SUCCESS carrying data —
        // the behaviour this tool has always had, and the precedent the whole VFX-code split was
        // built around. Only a code that says validity was never determined becomes isError.
        // See ValidationOutcomeRenderer for why the rule lives there rather than inline here.
        return ValidationOutcomeRenderer.TryRenderCallFailure(validation, out var failure)
            ? failure!
            : StructuredToolResult.Success(validation);
    }
}
