// Placeholder entry point for the vouchfx-mcp scaffold (todo 1 / REQ-001).
//
// This is NOT the MCP server. It only proves the project builds, wires up
// EnginePin correctly, and can report the vouchfx engine version this tool
// is pinned against. The stdio MCP server itself — tool registration,
// JSON-RPC framing, and request handlers wrapping the packaged `vouchfx`
// CLI — arrives in a later todo.

using Vouchfx.Mcp;

const string ToolName = "Vouchfx.Mcp";

var pinPath = Path.Combine(AppContext.BaseDirectory, "ENGINE_PIN");

try
{
    var pin = EnginePin.Load(pinPath);
    Console.Error.WriteLine($"{ToolName}: pinned to vouchfx engine {pin.Version} ({pin.CommitSha})");
}
catch (Exception ex) when (ex is FileNotFoundException or FormatException or IOException or UnauthorizedAccessException)
{
    // IOException/UnauthorizedAccessException cover a delete/permission race between the
    // File.Exists check in EnginePin.Load and the subsequent File.ReadLines — without this,
    // that race surfaced as a raw, unfriendly stack trace instead of a clear message.
    Console.Error.WriteLine($"{ToolName}: could not read ENGINE_PIN at '{pinPath}': {ex.Message}");
}

return 0;
