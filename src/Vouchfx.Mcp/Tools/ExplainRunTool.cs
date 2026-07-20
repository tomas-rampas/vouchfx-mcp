using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>explain_run</c> tool: will explain a completed suite run from its JSON Lines event
/// stream.
/// </summary>
internal static class ExplainRunTool
{
    public const string Name = "explain_run";

    private const string Description =
        "Explains a completed vouchfx suite run in plain language from its JSON Lines event " +
        "stream: what passed, what failed and why, and where retries or timeouts occurred. Give " +
        "it the path to the run's events file; if omitted, the most recent run's event stream is " +
        "used.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("Path to the run's JSON Lines event stream file. Omit to use the most recent run.")]
        string? eventsPath = null) =>
        StubToolResult.NotImplemented(Name, "Explaining a vouchfx run from its event stream");
}
