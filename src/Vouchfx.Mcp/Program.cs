// Entry point for vouchfx-mcp: a local stdio MCP server (todo 2 / REQ-002), with a hidden
// one-shot validation-worker mode used internally by validate_suite's process-isolation boundary
// (see Vouchfx.Mcp.Validation.ValidationWorkerClient).
//
// stdout is the MCP JSON-RPC protocol channel in normal (server) mode, and nothing else may write
// to it — a single stray byte there corrupts every frame a connected agent reads. All logging,
// including the EnginePin startup banner below, therefore goes to stderr instead.
//
// --validate-worker <path> is a SEPARATE, one-shot mode with its OWN stdout contract: it never
// speaks MCP at all, never touches the ENGINE_PIN or the host, and exits before either would be
// reached. Its stdout carries exactly one thing — the serialised ValidateSuiteResult — which is
// exactly why it is checked first, before anything else in this file runs.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

if (args.Length > 0 && args[0] == ValidationWorkerProtocol.WorkerModeArgument)
{
    return RunValidateWorker(args);
}

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

// The provenance stamp every successful tool result carries (US-S1-02) is derived once, here,
// rather than lazily on first use. Its schemaVersion is read out of the embedded composed schema,
// so a schema whose version marker has moved or gone is exactly the same class of fault as a
// missing or corrupt ENGINE_PIN above: a packaging error that leaves this server unable to state
// what it is validating against. Forcing it at startup makes it fail the same fatal, one-line way
// — rather than as a TypeInitializationException surfacing on whichever tool call happened to be
// first, which is both later and far harder to read.
try
{
    _ = ToolMetaProvider.Current;
}
#pragma warning disable CA1031 // Do not catch general exception types — same narrow, fail-safe
// startup boundary rationale as the ENGINE_PIN block above: whatever the embedded-resource read
// throws, it ends as a sanitised one-liner on stderr and a non-zero exit, never a stack trace.
catch (Exception ex)
#pragma warning restore CA1031
{
    Console.Error.WriteLine(PinFailureReporting.DescribeToolMetaFailure(ex));
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
    .AddVouchfxMcpServer(pin)
    .WithStdioServerTransport();

var host = builder.Build();

Log.EnginePinLoaded(host.Services.GetRequiredService<ILogger<Program>>(), pin.Version, pin.CommitSha);

// Runs until stdin closes: WithStdioServerTransport registers a hosted service that awaits the
// MCP session and, once the session ends (client disconnect / stdin EOF), stops the host — so
// this returns once the parent process disconnects, without any extra shutdown wiring here.
await host.RunAsync();

return 0;

// --validate-worker <path>: runs SuiteValidator's full, already-hardened pipeline (size/anchor/
// alias caps, Scanner-based nesting bound, schema validation — see SuiteValidator and
// YamlSafetyGuard) against a single suite file and reports the result as JSON on stdout, then
// exits. A one-shot, no-DI, no-logging process by design: ValidationWorkerClient (the
// validate_suite orchestrator) spawns exactly this, bounded by a wall-clock timeout, and kills the
// whole process tree if it hangs — see that type's remarks for why an in-process guard alone is
// not enough.
static int RunValidateWorker(string[] workerArgs)
{
    if (workerArgs.Length < 2 || string.IsNullOrWhiteSpace(workerArgs[1]))
    {
        Console.Error.WriteLine(
            $"{ValidationWorkerProtocol.WorkerModeArgument} requires a suite file path argument.");
        return 1;
    }

    ValidateSuiteResult result;
    try
    {
        result = SuiteValidator.ValidateFile(workerArgs[1]);
    }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: SuiteValidator
    // is documented to never throw, so this is a last-resort boundary in case a future change to
    // it (or a YamlDotNet/JsonSchema.Net upgrade) breaks that contract. Worker mode's stdout
    // contract (nothing but the JSON result) must hold even then; a genuine crash is reported on
    // stderr with a non-zero exit instead, which ValidationWorkerClient treats as
    // validation-worker-failed.
    catch (Exception ex)
#pragma warning restore CA1031
    {
        Console.Error.WriteLine(
            $"vouchfx-mcp validation worker crashed: {TextSanitiser.SanitiseForDisplay(ex.GetType().Name)}.");
        return 1;
    }

    Console.Out.Write(JsonSerializer.Serialize(result, ValidationWorkerProtocol.JsonOptions));
    Console.Out.Flush();

    return 0;
}
