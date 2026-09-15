using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-01's live-mode clause, against the REAL pinned engine: when a matching <c>vouchfx</c> CLI
/// is installed, <c>get_schema</c> must actually run the <see cref="CliPinVerifier"/> fail-closed
/// handshake, actually invoke that binary's <c>vouchfx schema</c> export, and — since issue #89 —
/// report NO divergence on any host whose console output code page maps the schema's three non-ASCII
/// characters outside <c>{0x09, 0x0A, 0x0D, 0x22, 0x5C}</c>, while still serving the vendored copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs only when the actually-installed CLI matches ENGINE_PIN; skips cleanly otherwise.</b>
/// This reuses the EXACT gate <see cref="RealPlanCoverageAgainstPinnedCliTests"/> and
/// <see cref="RealValidateAgainstPinnedCliTests"/> already established — the production
/// <see cref="CliPinVerifier"/> run against the real <see cref="VouchfxCliProcessRunner"/> and the
/// real <c>ENGINE_PIN</c> file, returning early (a silent pass, not a failure) on any non-Ok
/// result. Do not invent a second skip mechanism: this repo has no dynamic-skip test package, and a
/// divergent scheme would have to be kept in step with the CLI gate every tool already uses.
/// </para>
/// <para>
/// <b>What this proves that the fake-CLI tests cannot.</b> <see cref="RealGetSchemaMcpTests"/>
/// drives the agreement and mismatch paths through <see cref="FakeVouchfxCli"/>, which proves the
/// COMPARISON logic in isolation. It cannot prove the tool reaches a real, pinned binary at all.
/// This class does, by counting the <c>vouchfx schema</c> invocations the tool call actually makes
/// against that binary and asserting the resulting cross-verification is clean.
/// </para>
/// <para>
/// <b>Why the stance is now "no diagnostic at the pinned commit", not "agrees with whatever this
/// host receives" (issue #89).</b> It used to be the latter, deliberately, because the answer WAS
/// host-dependent: <c>vouchfx schema</c> encodes its stdout with the console's active output code
/// page, and on a page that cannot represent every schema character the engine best-fit-maps those
/// characters BEFORE any byte reaches this server, so a residual difference survived and
/// <c>get_schema</c> reported VFX-D-1106 — on EVERY call. MEASURED 2026-09-15 under cp852: <c>—</c>
/// U+2014 arrives as <c>-</c>, <c>§</c> U+00A7 is recovered intact, and <c>…</c> U+2026 arrives as a
/// raw <c>0x07</c> that breaks JSON parsing outright. The orchestrator now compares the live export
/// against the vendored document PROJECTED through that same encoding, so an unrepresentable
/// character mangled identically on both sides is no longer drift. That lets this test assert a
/// stronger, checkable property than "agrees with whatever arrived".
/// </para>
/// <para>
/// <b>But NOT on literally any host, and the summary above is scoped accordingly.</b> Two documented
/// residuals keep a diagnostic possible at the pinned commit, both of them false positives rather
/// than missed drift, and both belonging to the repair rather than to the engine:
/// <list type="number">
/// <item><description>A best-fit mapping that lands on <c>0x09</c>, <c>0x0A</c> or <c>0x0D</c> INSIDE
/// a string is unrepairable — those three are exempt from the control-character pre-pass because
/// escaping them would break valid JSON between tokens — so the export stays unparseable and
/// VFX-D-1106 fires. See <c>SchemaJsonCanonicaliser.EscapeRawControlCharacters</c>'s "SECOND
/// residual" paragraph, which records the measured OEM mappings that reach those
/// bytes.</description></item>
/// <item><description>A page whose best-fit produced a <c>"</c> (<c>0x22</c>) or a <c>\</c>
/// (<c>0x5C</c>) would make the PROJECTION itself unparseable, so
/// <c>GetSchemaOrchestrator</c> has nothing to compare against and falls through to the
/// diagnostic.</description></item>
/// </list>
/// Neither is reachable at the current pin — the composed schema's only non-ASCII characters are
/// <c>—</c>, <c>§</c> and <c>…</c>, and no measured page maps any of them into that set — which is
/// why this test asserts a clean result rather than tolerating one.
/// </para>
/// <para>
/// <b>And it is not vacuous.</b> The second assertion block takes the text the counting CLI actually
/// returned, makes a SINGLE-CHARACTER substitution in a pure-ASCII marker, and requires VFX-D-1106 to
/// fire. That discriminates a correct projection from one that over-masks: a one-character ASCII edit
/// is exactly what a comparison that had degenerated into "always equal" would swallow. Measured on
/// this host at rc.5: under <c>chcp 65001</c> the live export equals
/// <c>vendored/composed-schema.v1.json</c> byte-for-byte apart from CRLF and a trailing newline
/// (<c>diff</c> exit 0), i.e. there is no schema drift at this pin for either comparison to be
/// confused by.
/// </para>
/// <para>
/// Docker-free and fast: <c>vouchfx schema</c> prints an embedded document — no container, no
/// network, no suite.
/// </para>
/// </remarks>
public class RealGetSchemaAgainstPinnedCliTests
{
    /// <summary>
    /// A pure-ASCII string occurring exactly once in the composed schema, and
    /// <see cref="DriftedAsciiMarker"/> the same string with ONE character substituted. Pure ASCII so
    /// every code page — OEM or otherwise — carries it byte-for-byte unaltered, which is what makes
    /// the edit survive the projection and therefore makes the negative control below discriminating.
    /// </summary>
    private const string AsciiMarker = "E2E Integration Test";

    /// <inheritdoc cref="AsciiMarker"/>
    private const string DriftedAsciiMarker = "E2F Integration Test";

    private readonly ITestOutputHelper _testOutput;

    public RealGetSchemaAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [Fact]
    public async Task GetSchema_AgainstPinnedInstalledCli_ReportsNoDivergenceAtThePinnedCommit()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();

        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN "
                + $"({pin.Version}); this test only exercises the real CLI when one is present. "
                + $"Gate outcome: {pinCheck.GetType().Name}.");
            return;
        }

        // Counting decorator over the REAL runner: the only way to see from outside that the tool
        // actually reached the installed binary. A clean result that never invoked `vouchfx schema`
        // would be indistinguishable from a clean result that did, and this test's whole claim is
        // about a comparison having happened.
        var countingCli = new SchemaExportCountingCli(realCli);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: countingCli, enginePin: pin);

        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent from get_schema.");

        Assert.True(
            countingCli.SchemaExports >= 1,
            "get_schema returned without ever invoking `vouchfx schema` on the pinned binary, so its "
            + "cross-verification did not run and 'no diagnostic' proves nothing.");

        var hasDiagnostics = payload.TryGetProperty("diagnostics", out var diagnostics);
        Assert.False(
            hasDiagnostics,
            "get_schema reported a divergence against the PINNED engine. At ENGINE_PIN the installed "
            + "engine's `vouchfx schema` export and the embedded vendored schema are the same document, "
            + "and the cross-verification now models the console output code page as well (issue #89), "
            + "so this means either a genuine schema/pin mismatch or a regression in that modelling. "
            + $"Diagnostics: {(hasDiagnostics ? diagnostics.GetRawText() : "<none>")}");

        _testOutput.WriteLine(
            $"MEASURED live against pinned CLI ({pin.Version}): `vouchfx schema` invoked "
            + $"{countingCli.SchemaExports} time(s) through the tool path; cross-verification clean "
            + $"(no VFX-D-1106) with engine output encoding "
            + $"{EngineOutputEncoding.Current.WebName} (code page {EngineOutputEncoding.Current.CodePage}).");

        // NOT VACUOUS, and deliberately not a three-key stub. A stub document proves only that the
        // diagnostic CAN fire — it would still fire against a comparison that had degenerated into
        // "anything unlike a schema diverges". This replays the EXACT text the counting CLI returned,
        // through the SAME production encoding, with ONE character substituted in a pure-ASCII marker
        // that every code page carries unaltered: the smallest edit the projection must still see. A
        // comparison that over-masked — the real risk of the #89 fix, given that the projection
        // provably collapses whole classes of characters (see GetSchemaOrchestrator's accepted
        // residual) — swallows this and fails here.
        var liveExport = countingCli.LastSchemaExport;
        Assert.False(
            string.IsNullOrWhiteSpace(liveExport),
            "The counting CLI recorded no `vouchfx schema` output to build the negative control from.");
        Assert.Contains(AsciiMarker, liveExport!, StringComparison.Ordinal);

        var driftedExport = liveExport!.Replace(AsciiMarker, DriftedAsciiMarker, StringComparison.Ordinal);
        Assert.NotEqual(liveExport, driftedExport);

        using var corruptedLive = new LiveSchemaDocument(
            FakeVouchfxCli.WithExports(
                CliVersionNormaliser.Normalise(pin.Version),
                listJson: "{}",
                schemaJson: driftedExport),
            new CliPinVerifier(FakeVouchfxCli.ReportingVersion(CliVersionNormaliser.Normalise(pin.Version)), pin));
        var corruptedOutcome = await new GetSchemaOrchestrator(corruptedLive)
            .GetSchemaAsync("full", format: null, cts.Token);
        var corruptedCompleted = Assert.IsType<GetSchemaOutcome.Completed>(corruptedOutcome);
        var corruptedDiagnostic = Assert.Single(corruptedCompleted.Result.Diagnostics!);
        Assert.Equal(VfxCodeCatalogue.LiveSchemaMismatch, corruptedDiagnostic.Code);

        // stdout is the JSON-RPC channel and nothing else may ever write to it — asserted here
        // because spawning a REAL child process is the path most likely to leak a stray line onto it.
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetSchema_AgainstPinnedInstalledCli_StillServesTheVendoredDocumentNotTheLiveExport()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var realCli = new VouchfxCliProcessRunner();

        var pinCheck = await new CliPinVerifier(realCli, pin).VerifyAsync(cts.Token);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). "
                + $"Gate outcome: {pinCheck.GetType().Name}.");
            return;
        }

        // "Never silently prefers one source over the other", asserted where it actually matters:
        // with a REAL engine present and answering, the document served is still the embedded,
        // drift-gated vendored copy — the same one validate_suite evaluates against. A future change
        // that quietly started returning the live export instead would pass every fake-CLI test in
        // this repo and fail here. It would also now fail LOUDLY on a non-UTF-8 console, because the
        // live export is the one that carries the console's best-fit damage.
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: realCli, enginePin: pin);
        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent from get_schema.");

        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.Canonicalise(payload.GetProperty("jsonSchema").GetRawText()));

        // stdout is the JSON-RPC channel and nothing else may ever write to it — asserted here for
        // the same reason as in this class's sibling test: spawning a REAL child process is exactly
        // the path most likely to leak a stray line onto it.
        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Counts <c>vouchfx schema</c> exports made through the decorated CLI, so a test can prove the
    /// cross-verification actually reached the pinned binary rather than inferring it from a clean
    /// result.
    /// </summary>
    private sealed class SchemaExportCountingCli : IVouchfxCli
    {
        private readonly IVouchfxCli _inner;
        private int _schemaExports;
        private string? _lastSchemaExport;

        public SchemaExportCountingCli(IVouchfxCli inner)
        {
            _inner = inner;
        }

        public int SchemaExports => Volatile.Read(ref _schemaExports);

        /// <summary>
        /// The text the last <c>vouchfx schema</c> export actually returned — i.e. the bytes the real
        /// binary produced, already decoded with this host's engine output encoding by
        /// <see cref="VouchfxCliProcessRunner"/>. Recorded so the negative control can be built from
        /// what the tool path REALLY saw rather than from a hand-written stand-in.
        /// </summary>
        public string? LastSchemaExport => Volatile.Read(ref _lastSchemaExport);

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
            _inner.TryGetVersionOutputAsync(cancellationToken);

        public async Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            CancellationToken cancellationToken = default)
        {
            var isSchemaExport = arguments is { Count: 1 }
                && string.Equals(arguments[0], "schema", StringComparison.Ordinal);

            if (isSchemaExport)
            {
                Interlocked.Increment(ref _schemaExports);
            }

            var stdout = await _inner.TryRunStdoutAsync(arguments, maxStreamBytes, cancellationToken);

            if (isSchemaExport && stdout is not null)
            {
                Volatile.Write(ref _lastSchemaExport, stdout);
            }

            return stdout;
        }

        public Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            _inner.RunAsync(arguments, maxStreamBytes, timeout, cancellationToken);
    }
}
