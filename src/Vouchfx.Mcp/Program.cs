// Entry point for vouchfx-mcp: a local stdio MCP server (todo 2 / REQ-002).
//
// stdout is the MCP JSON-RPC protocol channel and nothing else may write to it — a single
// stray byte there corrupts every frame a connected agent reads. All logging, including the
// EnginePin startup banner below, therefore goes to stderr instead.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp;

var pinPath = Path.Combine(AppContext.BaseDirectory, "ENGINE_PIN");

EnginePin pin;
try
{
    pin = EnginePin.Load(pinPath);
}
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: this is a
// narrow, fail-safe startup boundary around a single call. Anything EnginePin.Load can throw
// (FileNotFoundException/FormatException for the documented cases; IOException,
// UnauthorizedAccessException, PathTooLongException, ArgumentException, or a platform-specific
// SecurityException for races and environment quirks the documented cases don't enumerate) must
// end the same way: a friendly, sanitised one-liner on stderr and a non-zero exit — never a raw
// stack trace on any pin-load path.
catch (Exception ex)
#pragma warning restore CA1031
{
    // A missing or corrupt ENGINE_PIN is a startup-fatal error: this server has no meaningful
    // engine version to report or gate the CLI handshake against, so it must not proceed to
    // serve MCP requests at all.
    Console.Error.WriteLine(PinFailureReporting.DescribeLoadFailure(ex));
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// The default console logging provider writes to stdout; redirect everything to stderr so
// logging can never corrupt the MCP JSON-RPC stream carried over stdio.
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddVouchfxMcpServer()
    .WithStdioServerTransport();

var host = builder.Build();

Log.EnginePinLoaded(host.Services.GetRequiredService<ILogger<Program>>(), pin.Version, pin.CommitSha);

// Runs until stdin closes: WithStdioServerTransport registers a hosted service that awaits the
// MCP session and, once the session ends (client disconnect / stdin EOF), stops the host — so
// this returns once the parent process disconnects, without any extra shutdown wiring here.
await host.RunAsync();

return 0;
