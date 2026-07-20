using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Builds the tool-level error result every stub handler in this build returns.
/// </summary>
/// <remarks>
/// This todo delivers only the server skeleton — the handshake and the tool registry — so
/// every tool's handler is an honest stub: it tells the calling agent the capability is not
/// implemented yet rather than pretending to succeed or throwing an unhandled exception that
/// could look like a server crash. A later todo replaces a stub's <c>Handle</c> method body with
/// the real implementation; the tool's registration in <see cref="ToolRegistry"/> (name,
/// description, input schema) does not change when that happens.
/// </remarks>
internal static class StubToolResult
{
    /// <param name="toolName">The tool's registered name, e.g. <c>validate_suite</c>.</param>
    /// <param name="capability">
    /// A capitalised noun phrase naming what the tool will do once implemented, e.g.
    /// "Validating .e2e.yaml suites against the vouchfx JSON Schema" — no trailing period;
    /// it is spliced into a sentence.
    /// </param>
    public static CallToolResult NotImplemented(string toolName, string capability) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = $"'{toolName}' is not implemented yet in this build of vouchfx-mcp. " +
                    $"{capability} will be wired up to the packaged vouchfx CLI in a later release.",
            },
        ],
    };
}
