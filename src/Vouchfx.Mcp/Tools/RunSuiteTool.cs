using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>run_suite</c> tool: will run an <c>.e2e.yaml</c> suite through the packaged vouchfx
/// CLI and report its verdict.
/// </summary>
internal static class RunSuiteTool
{
    public const string Name = "run_suite";

    private const string Description =
        "Runs a vouchfx .e2e.yaml suite through the packaged vouchfx CLI and reports its verdict " +
        "(pass / fail / environment error / inconclusive) once the run completes. Give it the " +
        "suite path; optionally restrict the run to steps or scenarios matching one or more " +
        "tags, and/or cap the whole run with a timeout in seconds.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = false,
        Destructive = false,
    });

    private static CallToolResult Handle(
        [Description("Absolute or workspace-relative path to the .e2e.yaml suite file to run.")]
        string path,
        [Description("Only run steps/scenarios matching one or more of these tags. Omit to run the whole suite.")]
        string[]? tags = null,
        [Description("Abort the run if it has not completed within this many seconds. Omit for the engine's default timeout.")]
        int? timeoutSeconds = null) =>
        StubToolResult.NotImplemented(Name, "Running vouchfx suites through the packaged CLI");
}
