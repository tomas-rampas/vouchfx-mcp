// Entry point for vouchfx-mcp: a local stdio MCP server (todo 2 / REQ-002), with a hidden
// one-shot validation-worker mode used internally by validate_suite's process-isolation boundary
// (see Vouchfx.Mcp.Validation.ValidationWorkerClient).
//
// stdout is the MCP JSON-RPC protocol channel in normal (server) mode, and nothing else may write
// to it — a single stray byte there corrupts every frame a connected agent reads. All logging,
// including the EnginePin startup banner below, therefore goes to stderr instead.
//
// --validate-worker <source> [--level=<level>] is a SEPARATE, one-shot mode with its OWN stdout
// contract: it never speaks MCP at all, never touches the ENGINE_PIN or the host, and exits before
// either would be reached. Its stdout carries exactly one thing — the serialised SuiteAnalysis —
// which is exactly why it is checked first, before anything else in this file runs.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;
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

// US-S1-05's diagnostic catalogue (docs/errors/*.md, one page per VfxCodeCatalogue.All entry) is
// forced here for the SAME reason as the ToolMetaProvider block immediately above: it is another
// static, embedded-resource-backed initialiser (DiagnosticPageRepository.AllByCode), so a missing
// or malformed page is exactly the same class of packaging fault as a missing schema version
// marker — and, left lazy, it would surface as an unreadable TypeInitializationException on
// whichever call (explain_diagnostic OR a vouchfx-docs:///errors/{code} resource read) happened to
// touch it first, rather than as a friendly, fatal startup message. Worse than the ToolMetaProvider
// case: DiagnosticPageRepository parses ALL 24 pages together as one static initialisation, so a
// SINGLE bad page would poison EVERY code's lookup for the rest of the process's lifetime — and the
// resources/read path's failure mode would be an unhandled TypeInitializationException slipping
// past DiagnosticResourceRegistry's InvalidOperationException-only catch (see that type's
// GetPageText), since a wrapped static-initialiser fault is not the exception type that catch
// clause expects. Forcing initialisation here, before either access path can ever be reached, is
// what rules both of those out.
try
{
    _ = DiagnosticPageRepository.AllByCode;
}
#pragma warning disable CA1031 // Do not catch general exception types — same narrow, fail-safe
// startup boundary rationale as the two blocks above: whatever the embedded-resource read or page
// parse throws, it ends as a sanitised one-liner on stderr and a non-zero exit, never a stack trace.
catch (Exception ex)
#pragma warning restore CA1031
{
    Console.Error.WriteLine(PinFailureReporting.DescribeDiagnosticCatalogueFailure(ex));
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

// --validate-worker <source> [--level=<level>]: runs SuiteValidator's full, already-hardened
// pipeline (size/anchor/alias caps, Scanner-based nesting bound, schema validation, and — since
// US-S2-02 — the summary and the semantic pass; see SuiteValidator and YamlSafetyGuard) against a
// single suite and reports the result as JSON on stdout, then exits. A one-shot, no-DI, no-logging
// process by design: ValidationWorkerClient (the validate_suite orchestrator) spawns exactly this,
// bounded by a wall-clock timeout, and kills the whole process tree if it hangs — see that type's
// remarks for why an in-process guard alone is not enough.
//
// <source> is either a suite file path or ValidationWorkerProtocol.InlineYamlArgument, in which case
// the suite text arrives on THIS process's stdin (US-S2-02 — see that constant's remarks for why
// stdin rather than a scratch file). Both sources run the identical pipeline; the only difference is
// where the bytes come from, which is the whole point of routing inline YAML through here at all.
static int RunValidateWorker(string[] workerArgs)
{
    if (workerArgs.Length < 2 || string.IsNullOrWhiteSpace(workerArgs[1]))
    {
        Console.Error.WriteLine(
            $"{ValidationWorkerProtocol.WorkerModeArgument} requires a suite file path or "
            + $"{ValidationWorkerProtocol.InlineYamlArgument} argument.");
        return 1;
    }

    // Positional source, then zero or one --level=<token> flag. Anything else is rejected rather
    // than ignored: a level this build does not recognise means the orchestrator and the worker
    // disagree about the contract, and silently falling back to the default would answer a question
    // nobody asked.
    var level = ValidationLevels.Default;
    for (var i = 2; i < workerArgs.Length; i++)
    {
        var argument = workerArgs[i];
        if (argument.StartsWith(ValidationWorkerProtocol.LevelArgumentPrefix, StringComparison.Ordinal) &&
            ValidationLevels.TryParse(argument[ValidationWorkerProtocol.LevelArgumentPrefix.Length..], out var parsed))
        {
            level = parsed;
            continue;
        }

        // SanitiseForEcho, not SanitiseForDisplay: this echoes a caller-shaped token back, which is
        // exactly what VfxCode.SanitiseForEcho's 64-character cap exists for — the same choice
        // ValidateSuiteInput makes for an unrecognised `level`. Not reachable from an MCP caller
        // (ValidationWorkerClient builds these arguments itself), but a worker invoked by hand must
        // not be the one place that echoes an unbounded argument.
        Console.Error.WriteLine(
            $"{ValidationWorkerProtocol.WorkerModeArgument} received an unrecognised argument "
            + $"'{VfxCode.SanitiseForEcho(argument)}'.");
        return 1;
    }

    var isInline = string.Equals(workerArgs[1], ValidationWorkerProtocol.InlineYamlArgument, StringComparison.Ordinal);

    // The stdin read sits INSIDE the same crash boundary as the analysis, not before it. Reading a
    // pipe is the one step here with a genuinely open-ended failure surface — a broken handle, a
    // decoder fault, an OutOfMemoryException on a hostile parent's stream — and only IOException was
    // ever caught, so any sibling failure escaped RunValidateWorker unhandled and reached the
    // runtime's default handler: a stack trace on stderr and a .NET-chosen exit code, which is
    // precisely the outcome the general catch below exists to prevent for the analysis half.
    SuiteAnalysis result;
    try
    {
        // Read to EOF before anything else: the parent writes the suite text and then closes the
        // handle, and nothing downstream can start until all of it is here. Bounded, not unbounded
        // — see ReadInlineYaml.
        var inlineYaml = isInline ? ReadInlineYaml() : null;

        result = isInline
            ? SuiteValidator.AnalyseYaml(inlineYaml!, level)
            : SuiteValidator.AnalyseFile(workerArgs[1], level);
    }
    catch (IOException ex) when (isInline)
    {
        // Kept as its own arm, ahead of the general one, because it is the ONE failure here with a
        // specific, true story to tell — the pipe broke — and the operator-facing message should say
        // so rather than "crashed". Filtered on isInline so it can only claim a stdin failure when
        // there was a stdin read; SuiteValidator handles a file IOException itself and never
        // propagates one.
        Console.Error.WriteLine(
            $"vouchfx-mcp validation worker could not read the inline suite from stdin "
            + $"({TextSanitiser.SanitiseForDisplay(ex.GetType().Name)}).");
        return 1;
    }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: SuiteValidator
    // is documented to never throw, so this is a last-resort boundary in case a future change to
    // it (or a YamlDotNet/JsonSchema.Net upgrade, or a new semantic rule) breaks that contract, and
    // it now also covers the stdin read above. Worker mode's stdout contract (nothing but the JSON
    // result) must hold even then; a genuine crash is reported on stderr with a non-zero exit
    // instead, which ValidationWorkerClient treats as validation-worker-failed.
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

// Reads the inline suite text from stdin, bounded at one character past the size cap
// YamlSafetyGuard already enforces. The bound is not redundant with that guard: the guard runs on
// text this process has ALREADY buffered, so without a bound here a hostile or buggy parent could
// make this worker allocate without limit before the guard ever saw a byte. One character past the
// cap is enough for the guard to reach its own "too large" verdict on the truncated text and report
// it the same way it would for a file — the caller gets VFX-D-1103, not a mystery.
//
// Decoded as UTF-8 explicitly, over the RAW standard input stream, rather than through Console.In:
// Console.In decodes using Console.InputEncoding, which on Windows is the OEM code page, and a suite
// carrying any non-ASCII character would then be validated as text the caller never sent. The
// parent pins the matching UTF-8 encoding on its side (see ValidationWorkerClient's
// StandardInputEncoding, which records the full rationale). BOM detection is left on so a leading
// byte-order mark — which is not part of the suite — is stripped rather than parsed as content.
static string ReadInlineYaml()
{
    var limit = YamlSafetyGuard.MaxSuiteSizeBytes + 1;
    var builder = new System.Text.StringBuilder();
    var buffer = new char[8192];

    using var reader = new StreamReader(
        Console.OpenStandardInput(),
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        detectEncodingFromByteOrderMarks: true);

    int read;
    while (builder.Length < limit && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        builder.Append(buffer, 0, read);
    }

    return builder.ToString();
}
