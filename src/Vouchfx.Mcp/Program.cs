// Entry point for vouchfx-mcp: a local stdio MCP server (todo 2 / REQ-002), with a hidden
// one-shot validation-worker mode used internally by validate_suite's process-isolation boundary
// (see Vouchfx.Mcp.Validation.ValidationWorkerClient).
//
// stdout is the MCP JSON-RPC protocol channel in normal (server) mode, and nothing else may write
// to it — a single stray byte there corrupts every frame a connected agent reads. All logging,
// including the EnginePin startup banner below, therefore goes to stderr instead.
//
// --workspace <path> (US-S3-08) configures the workspace this server operates on, and with it the
// path-containment policy PathSafetyGuard applies. Omitting it is fully supported and leaves every
// path behaving exactly as it did before Sprint 3.
//
// --validate-worker <source> [--level=<level>] [--normalize] is a SEPARATE, one-shot mode with its
// OWN stdout contract: it never speaks MCP at all, never touches the ENGINE_PIN or the host, and
// exits before either would be reached. Its stdout carries exactly one thing — the serialised
// SuiteAnalysis, or, with --normalize, the serialised SuiteNormalization that wraps it (US-S2-04) —
// which is exactly why it is checked first, before anything else in this file runs.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;
using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

if (args.Length > 0 && args[0] == ValidationWorkerProtocol.WorkerModeArgument)
{
    return RunValidateWorker(args);
}

// US-S3-08's workspace, resolved BEFORE anything else in server mode for two reasons. First, a
// malformed `--workspace` is a pure argument fault and deserves to be reported without first
// dragging the operator through pin/schema/catalogue loading. Second, and load-bearing: the
// provenance stamp forced a few lines below reads the workspace root, so the publish has to happen
// before ToolMetaProvider.Current is first materialised — see that type's remarks for why the stamp
// travels by a startup publish rather than through the DI graph everything else uses.
//
// Deliberately AFTER the --validate-worker branch above and never inside it: the worker is a
// disposable child with its own one-shot stdout contract, is handed a path the parent has already
// contained, and is never told which workspace it belongs to.
//
// No flag at all ⇒ workspace stays null ⇒ every path behaves exactly as it did before Sprint 3
// (plan §2.1: containment is new policy, not a bug fix, and it is opt-in).
if (!Workspace.TryParseCommandLine(args, out var workspace, out var workspaceError))
{
    // Same fail-closed startup shape as the three blocks below: one sanitised line on stderr — never
    // stdout, which is the JSON-RPC channel — and a non-zero exit. A `--workspace` that cannot be
    // honoured must not degrade into a server running with containment silently off.
    Console.Error.WriteLine(workspaceError);
    return 1;
}

// Everything about the workspace ITSELF that is knowable now is checked now, once — its root's link
// walk actually resolving, and its run-artefact directory actually landing inside that root. Both
// are permanent properties of the operator's own configuration, and both otherwise surface only as a
// per-call refusal for the server's whole lifetime (or, for the second, as an unhandled exception out
// of DI registration below). Same fail-closed, fail-loud shape as the parse failure above.
if (workspace is not null && PathSafetyGuard.DescribeWorkspaceStartupFailure(workspace) is { } containmentError)
{
    Console.Error.WriteLine(containmentError);
    return 1;
}

ToolMetaProvider.PublishStartupWorkspace(workspace);

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
// case: DiagnosticPageRepository parses EVERY page together as one static initialisation, so a
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

// Registration and host build share ONE fail-closed boundary, and the reason is specific rather
// than defensive housekeeping (a security review's NIT). DescribeWorkspaceStartupFailure above is
// deliberately fail-OPEN for a root walk that THROWS — a permission-denied ancestor or a transient
// I/O fault is left to the per-call path rather than frozen into a startup refusal, and nothing is
// cached in that case. But AddVouchfxMcpServer then constructs FileRunRegistry, whose own
// constructor re-runs the same containment check fail-CLOSED and throws ArgumentException when it
// cannot establish containment — so exactly the case the startup check waved through surfaced here
// as a raw stack trace out of DI registration, which is the one shape every other startup fault in
// this file exists to prevent. Caught narrowly: ArgumentException is what that constructor throws,
// and the message it carries is PathSafetyGuard's own already-sanitised, already-capped rendering.
// The fail-open semantics above are untouched — what changes is only how the consequence is
// reported.
IHost host;
try
{
    builder.Services
        .AddVouchfxMcpServer(pin, workspace: workspace)
        .WithStdioServerTransport();

    // Inside the same boundary because the tool collection is composed by a configuration callback
    // the host resolves, not by the call above — so a future move of that construction behind the
    // callback must not reopen the hole this boundary closes.
    host = builder.Build();
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(
        $"vouchfx-mcp could not configure its run-artefact storage: {TextSanitiser.SanitiseForDisplay(ex.Message)}");
    return 1;
}

var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();

Log.EnginePinLoaded(startupLogger, pin.Version, pin.CommitSha);

// The effective PATH POLICY, stated beside the pin banner (a peer review's MAJOR finding). stderr
// used to be byte-identical with and without --workspace, so an operator could not tell from the
// server's own output whether containment was on — see Log.NoWorkspaceConfigured's remarks. The root
// goes through the same cap-and-sanitise rendering every caller-supplied path does before it reaches
// a message: it is an operator-supplied command-line token, and a console line is exactly what that
// helper's control-character escaping exists for.
if (workspace is null)
{
    Log.NoWorkspaceConfigured(startupLogger);
}
else
{
    Log.WorkspaceConfigured(startupLogger, PathSafetyGuard.CapAndSanitisePathForDisplay(workspace.Root));
}

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
    var normalise = false;
    for (var i = 2; i < workerArgs.Length; i++)
    {
        var argument = workerArgs[i];
        if (argument.StartsWith(ValidationWorkerProtocol.LevelArgumentPrefix, StringComparison.Ordinal) &&
            ValidationLevels.TryParse(argument[ValidationWorkerProtocol.LevelArgumentPrefix.Length..], out var parsed))
        {
            level = parsed;
            continue;
        }

        // US-S2-04. Repeating the flag is accepted rather than rejected: it is idempotent, and the
        // orchestrator builds this argument list itself, so a second occurrence could only come from
        // a hand-run worker where refusing it would help nobody.
        if (string.Equals(argument, ValidationWorkerProtocol.NormaliseArgument, StringComparison.Ordinal))
        {
            normalise = true;
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
    // The static type is the WIDER of the two response shapes so there is one result variable and
    // one serialisation site below; `normalise` decides which of the two is actually written. See
    // ValidationWorkerProtocol.NormaliseArgument for why the shape is mode-selected at all.
    SuiteNormalization result;
    try
    {
        // Read to EOF before anything else: the parent writes the suite text and then closes the
        // handle, and nothing downstream can start until all of it is here. Bounded, not unbounded
        // — see ReadInlineYaml.
        var inlineYaml = isInline ? ReadInlineYaml() : null;

        result = (isInline, normalise) switch
        {
            (true, true) => SuiteValidator.NormaliseYaml(inlineYaml!, level),
            (true, false) => SuiteNormalization.WithoutCanonicalYaml(SuiteValidator.AnalyseYaml(inlineYaml!, level)),
            (false, true) => SuiteValidator.NormaliseFile(workerArgs[1], level),
            (false, false) => SuiteNormalization.WithoutCanonicalYaml(SuiteValidator.AnalyseFile(workerArgs[1], level)),
        };
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
    catch (SemanticRuleContractViolationException ex)
    {
        // The ONE exception whose MESSAGE is printed rather than just its type name. It is
        // content-free by construction — its only constructor composes the message itself from a
        // SanitiseForEcho-bounded rule code plus a field NAME, and none takes free text (see that
        // type's remarks) — so printing it cannot leak suite content, and it is the only way the
        // operator learns WHICH rule broke the no-secret-echo contract and in which field. Without
        // this arm the general one below reduces the whole diagnosis to
        // "crashed: SemanticRuleContractViolationException.", losing exactly the two facts the guard
        // took care to produce.
        //
        // This text REACHES THE HOST, and that is the whole reason the type's guarantee matters.
        // ValidationWorkerClient.ReadExcerptQuietlyAsync takes a 500-character, display-sanitised
        // excerpt of this stream and splices it into the VFX-E-1901 message it returns — so this is
        // not "the parent's log" and not a stderr-only refinement. It is acceptable precisely
        // BECAUSE the message is content-free by construction: no constructor takes free text, and
        // every identifier in it is SanitiseForEcho-bounded (see that type's remarks). The caller's
        // CODE is still VFX-E-1901 either way; what this arm changes is whether the accompanying
        // text names the offending rule or says only "crashed".
        Console.Error.WriteLine($"vouchfx-mcp validation worker crashed: {ex.Message}");
        return 1;
    }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: SuiteValidator
    // is documented to never throw (with the one deliberate exception the arm above handles), so
    // this is a last-resort boundary for a future change to it — or a YamlDotNet/JsonSchema.Net
    // upgrade, or a new semantic rule — breaking that contract, and it now also covers the stdin
    // read above. Worker mode's stdout contract (nothing but the JSON result) must hold even then;
    // a genuine crash is reported on stderr with a non-zero exit instead, which
    // ValidationWorkerClient treats as validation-worker-failed.
    //
    // Only the TYPE NAME is printed here, never ex.Message: an arbitrary exception's message may
    // quote suite content (a YamlException reproduces the offending line), and this stderr stream
    // REACHES THE HOST — ValidationWorkerClient.ReadExcerptQuietlyAsync relays a 500-character
    // sanitised excerpt of it inside the VFX-E-1901 message a caller receives. So printing an
    // arbitrary message here would be a suite-content disclosure through the error channel, not
    // merely a noisy local log. Being content-free by construction is precisely the property the arm
    // above establishes for the one type it names, and it is what earns that type the exemption.
    catch (Exception ex)
#pragma warning restore CA1031
    {
        Console.Error.WriteLine(
            $"vouchfx-mcp validation worker crashed: {TextSanitiser.SanitiseForDisplay(ex.GetType().Name)}.");
        return 1;
    }

    // Without --normalize the stdout contract is EXACTLY what it has always been — one serialised
    // SuiteAnalysis, nothing else — so every existing caller (and RealValidationWorkerProcessTests'
    // direct spawn of this mode) is unaffected by US-S2-04 having widened the mode at all.
    Console.Out.Write(normalise
        ? JsonSerializer.Serialize(result, ValidationWorkerProtocol.JsonOptions)
        : JsonSerializer.Serialize(result.Validation, ValidationWorkerProtocol.JsonOptions));
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
