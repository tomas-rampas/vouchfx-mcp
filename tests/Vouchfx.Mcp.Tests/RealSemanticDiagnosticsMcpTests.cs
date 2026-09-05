using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-03's wire-level goldens: the semantic channel as an MCP host actually sees it, over the
/// real JSON-RPC transport and through the real spawned validation worker.
/// </summary>
/// <remarks>
/// <b>Why these exist beside the per-rule unit tests.</b> A rule test proves a rule; it proves
/// nothing about whether the finding survives JSON serialisation, the worker's stdout pipe, the
/// parent's deserialisation through <c>Diagnostic</c>'s validating constructor, and the tool's
/// result envelope. The story's own test convention asks for exactly one such golden — the
/// <c>semanticDiagnostics</c>/<c>errors</c> channel separation — and the rest of this class covers
/// the Gherkin scenarios whose subject is the WIRE rather than a rule.
/// </remarks>
public class RealSemanticDiagnosticsMcpTests
{
    [Fact]
    public async Task SchemaAndSemanticFindings_ArriveInSeparateArrays()
    {
        // Gherkin scenario 6, end to end: "the schema error appears only in diagnostics / the
        // semantic warning appears only in semanticDiagnostics" (`errors` being the shipped spelling
        // of the spec's `diagnostics` — US-S2-02's field-name note).
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new()
        {
            // One schema error (http.rest requires `path`) and one semantic warning (an unused
            // capture) in the same document.
            ["yaml"] = """
                metadata:
                  owner: platform-team
                  tags: [smoke]
                environment:
                  services:
                    orders-api:
                      image: orders:1.0
                steps:
                  - id: create-order
                    type: http.rest
                    target: orders-api
                    method: POST
                    capture:
                      orderId: "$.id"
                """,
            ["level"] = "full",
        }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);

        var schemaCodes = payload.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString()).ToArray();
        var semanticCodes = payload.GetProperty("semanticDiagnostics").EnumerateArray()
            .Select(d => d.GetProperty("code").GetString()).ToArray();

        Assert.Contains("VFX-D-1101", schemaCodes);
        Assert.Contains("VFX-D-1204", semanticCodes);
        Assert.DoesNotContain("VFX-D-1204", schemaCodes);
        Assert.DoesNotContain("VFX-D-1101", semanticCodes);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task AnAsyncStepWithoutRetry_ArrivesAsAWarningCarryingAnApplicableFix()
    {
        // Gherkin scenario 3 on the wire: severity and fix.replacement are both host-visible fields,
        // so a golden that stopped at the rule would not prove a host can apply the fix.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new()
        {
            ["yaml"] = """
                steps:
                  - id: await-callback
                    type: webhook-listen.http
                    listener: callbacks
                    match:
                      path: /hooks/orders
                """,
            ["level"] = "semantic",
        }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);

        var finding = Assert.Single(
            payload.GetProperty("semanticDiagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "VFX-D-1209");

        Assert.Equal("warning", finding.GetProperty("severity").GetString());
        Assert.Equal("verifyMode: RETRY", finding.GetProperty("fix").GetProperty("replacement").GetString());
        Assert.Equal(
            "https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1209.html",
            finding.GetProperty("docsUrl").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ASecretLiteral_MakesTheCallSucceedWithValidFalse()
    {
        // Gherkin scenario 4 on the wire, including the half that is easy to get wrong: a
        // secret-literal finding is DATA on a SUCCESSFUL call (isError stays false, spec §4.4) even
        // though it flips `valid`.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new()
        {
            ["yaml"] = """
                steps:
                  - id: call
                    type: http.rest
                    target: orders-api
                    method: GET
                    path: /health
                    headers:
                      authorization: "AKIAIOSFODNN7EXAMPLE"
                """,
            ["level"] = "full",
        }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);

        Assert.False(payload.GetProperty("valid").GetBoolean());
        Assert.Empty(payload.GetProperty("errors").EnumerateArray());

        var finding = Assert.Single(
            payload.GetProperty("semanticDiagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "VFX-D-1207");
        Assert.Equal("error", finding.GetProperty("severity").GetString());

        // The literal never comes back — not in the message, not anywhere in the result.
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", payload.GetRawText(), StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ASecretNamedCaptureIsReportedWithoutCrashingTheWorker()
    {
        // The end-to-end proof that the seam's no-secret-echo obligation is actually met by the
        // shipped rules rather than merely enforced against a fake one: a capture literally NAMED
        // `${secret:…}` is unused, so VFX-D-1204 fires, and the call must come back clean — a rule
        // that interpolated the fact-set name would instead crash the worker into VFX-E-1901.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallToolAsync(harness, "validate_suite", new()
        {
            ["yaml"] = """
                steps:
                  - id: create-order
                    type: http.rest
                    target: orders-api
                    method: POST
                    path: /orders
                    capture:
                      "${secret:vault/prod-db-password}": "$.id"
                """,
            ["level"] = "semantic",
        }, cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = GetStructuredContent(result);

        Assert.Contains(
            payload.GetProperty("semanticDiagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "VFX-D-1204");

        var raw = payload.GetRawText();
        Assert.DoesNotContain("vault", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prod-db-password", raw, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NoLevelEverEmitsTheU1GatedTopologyCode()
    {
        // Gherkin scenario 5 on the wire.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        foreach (var level in new[] { "schema", "semantic", "full" })
        {
            var result = await CallToolAsync(harness, "validate_suite", new()
            {
                ["yaml"] = """
                    environment:
                      dependencies:
                        broker:
                          type: kafka
                    steps:
                      - id: consume
                        type: mq-expect.kafka
                        target: broker
                        topic: a-topic-no-producer-publishes
                        match:
                          key: "1"
                    """,
                ["level"] = level,
            }, cts.Token);

            Assert.False(result.IsError ?? false);
            Assert.DoesNotContain(
                GetStructuredContent(result).GetProperty("semanticDiagnostics").EnumerateArray(),
                d => d.GetProperty("code").GetString() == "VFX-D-1210");
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness,
        string toolName,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");
}
