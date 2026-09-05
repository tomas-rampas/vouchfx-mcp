using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-01's live-mode clause, against the REAL pinned engine: when a matching <c>vouchfx</c> CLI
/// is installed, <c>get_schema</c> must actually run the <see cref="CliPinVerifier"/> fail-closed
/// handshake, actually invoke that binary's <c>vouchfx schema</c> export, and report the result of
/// comparing it against the embedded vendored schema — while still serving the vendored copy.
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
/// This class does, by computing the expected answer from the SAME real binary — invoked directly —
/// and asserting <c>get_schema</c>'s diagnostics agree with it.
/// </para>
/// <para>
/// <b>Why this does NOT simply assert "no diagnostic at the pinned commit", which was this test's
/// first draft.</b> MEASURED on a Windows host: <c>vouchfx schema</c> encodes its stdout with the
/// CONSOLE'S ACTIVE CODE PAGE, not UTF-8. Under code page 852 the schema's <c>§</c> is written as
/// the single byte <c>0xF5</c> and its <c>—</c> is best-fit-mapped to <c>-</c>; this server reads
/// the child's raw bytes as UTF-8 (<c>BoundedStreamReader</c>), so what it actually RECEIVES is a
/// document carrying replacement characters and hyphens. Re-running the identical command under
/// <c>chcp 65001</c> yields output identical to <c>vendored/composed-schema.v1.json</c> apart from
/// CRLF and the missing trailing newline — which <see cref="SchemaJsonCanonicaliser"/> already
/// normalises away — so there is no schema drift at the pin; there is a host-dependent transcoding
/// loss on the wire between the two processes. That is a genuine difference in the document this
/// server received, and <c>get_schema</c> reporting it is correct behaviour, so this test asserts
/// the tool AGREES WITH REALITY rather than asserting the host happens to be configured cleanly.
/// </para>
/// <para>
/// Docker-free and fast: <c>vouchfx schema</c> prints an embedded document — no container, no
/// network, no suite.
/// </para>
/// </remarks>
public class RealGetSchemaAgainstPinnedCliTests
{
    private readonly ITestOutputHelper _testOutput;

    public RealGetSchemaAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
    }

    [Fact]
    public async Task GetSchema_AgainstPinnedInstalledCli_ReportsExactlyTheDivergenceTheRealBinaryExhibits()
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

        // Ground truth, taken from the SAME binary get_schema is about to reach, through the SAME
        // production runner and the SAME output cap the orchestrator uses.
        var directStdout = await realCli.TryRunStdoutAsync(
            ["schema"], VouchfxCliProcessRunner.MaxSchemaOutputBytes, cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(directStdout), "The pinned CLI produced no `vouchfx schema` output.");

        // Mirrors GetSchemaOrchestrator.CrossVerifyAgainstLiveEngineAsync EXACTLY, including its
        // "unparseable live output IS a divergence" arm — which is not hypothetical: on the Windows
        // host this was authored on, code-page transcoding injects a raw 0x07 byte inside a JSON
        // string, so the live document does not even parse (measured; see this class's remarks).
        var expectMismatch = TryCanonicalise(directStdout!) is not { } liveCanonical
            || !string.Equals(
                liveCanonical,
                SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
                StringComparison.Ordinal);

        // The real, process-spawning runner and the real pin — never FakeVouchfxCli, never
        // McpTestHarness.DefaultTestPin.
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: realCli, enginePin: pin);

        var result = await harness.Client.CallToolAsync(
            "get_schema", new Dictionary<string, object?>(), cancellationToken: cts.Token);

        // A divergence is never a tool failure — the caller still receives a usable schema.
        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent from get_schema.");

        var hasDiagnostics = payload.TryGetProperty("diagnostics", out var diagnostics);

        Assert.True(
            hasDiagnostics == expectMismatch,
            expectMismatch
                ? "The installed CLI's `vouchfx schema` output differs from the embedded vendored "
                  + "schema, but get_schema reported no diagnostic — the cross-verification did not "
                  + "run, or did not compare what this test compared."
                : "The installed CLI's `vouchfx schema` output matches the embedded vendored schema, "
                  + "but get_schema reported a diagnostic anyway.");

        if (hasDiagnostics)
        {
            var diagnostic = Assert.Single(diagnostics.EnumerateArray());
            Assert.Equal(VfxCodeCatalogue.LiveSchemaMismatch, diagnostic.GetProperty("code").GetString());
            _testOutput.WriteLine(
                "The installed pinned CLI's `vouchfx schema` output differs from the embedded "
                + "vendored schema on this host, and get_schema correctly reported VFX-D-1106. On "
                + "Windows this is usually the console code page transcoding the CLI's stdout — "
                + "re-run under `chcp 65001` to confirm before suspecting real schema drift.");
        }

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
        // this repo and fail here.
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
    /// <see cref="SchemaJsonCanonicaliser.Canonicalise(string)"/>, or <see langword="null"/> when
    /// the text is not well-formed JSON.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately NARROWER than the orchestrator's own arm, not a twin of it.</b>
    /// <c>GetSchemaOrchestrator.CrossVerifyAgainstLiveEngineAsync</c> catches EVERY
    /// non-<see cref="OperationCanceledException"/> failure of the canonicaliser, because its
    /// contract is that it never throws on untrusted subprocess output. This oracle catches only
    /// <see cref="System.Text.Json.JsonException"/> on purpose: an unparseable live export is the
    /// divergence this test is predicting, whereas anything else escaping the canonicaliser is a
    /// fault in the canonicaliser itself, and the test should FAIL loudly on it rather than quietly
    /// re-derive the production expectation and agree with a bug. The asymmetry means production is
    /// strictly more forgiving than the oracle — which is the safe direction: the only way it can
    /// cost a false failure here is a canonicaliser fault that deserves one.
    /// </remarks>
    private static string? TryCanonicalise(string json)
    {
        try
        {
            return SchemaJsonCanonicaliser.Canonicalise(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
