using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S1-02 end to end, through the same in-memory MCP harness every other <c>Real*McpTests</c>
/// class uses: every one of the nine tools' SUCCESS results carries
/// <c>meta: { schemaVersion, serverVersion, workspaceRoot }</c>, so a host can identify the DSL
/// schema version and server version that produced a result without a separate handshake call.
/// </summary>
/// <remarks>
/// <para>
/// <b>One test, not nine</b>: the acceptance criterion is a property of the WHOLE tool surface, and
/// splitting it per tool would let a tenth tool be added with no stamp and no failing test. This
/// test therefore drives every tool that can succeed, collects the names it actually proved, and
/// asserts that set is EXACTLY what <c>tools/list</c> advertises — the same fail-closed shape
/// <see cref="SecretHygieneSourceGuardTests"/> uses for spawn sites. A new tool fails this test
/// until it is driven here too.
/// </para>
/// <para>
/// Three harnesses rather than one because three tools need a non-default dependency to reach a
/// success result at all (<c>run_suite</c> a suite runner, <c>plan_coverage</c> a <c>plan --json</c>
/// handler) — the same overrides those tools' own <c>Real*McpTests</c> classes pass. Nothing here
/// touches the real <c>vouchfx</c> CLI or Docker.
/// </para>
/// <para>
/// The stamp's mechanism (the single <see cref="StructuredToolResult"/> choke point, its resolver
/// chain, and the byte-for-byte guarantee that attaching it reshaped nothing else) is covered in
/// <c>Tools/StructuredToolResultTests</c>; this class only proves the MCP-facing outcome.
/// </para>
/// </remarks>
public class RealToolMetaMcpTests
{
    private const string PassingEvents = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    private const string PlanReportJson = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.0.0-test",
          "thresholds": { "staleDays": 30, "flakyMinRuns": 2, "fragileMinEnvErrors": 2, "inconclusiveMin": 2 },
          "inventory": {
            "suites": [ { "path": "checkout.e2e.yaml", "scenarioId": "checkout-flow", "name": "checkout-flow", "stepCount": 2 } ],
            "services": [ "api" ],
            "dependencies": [],
            "stepTypes": [ "http.rest" ],
            "runCount": 1,
            "firstEventTs": "2026-01-01T00:00:00+00:00",
            "lastEventTs": "2026-01-01T00:05:00+00:00",
            "skippedEventLines": 0,
            "unmatchedObservations": 0,
            "unanalysableSuites": [],
            "unmappableDependencies": []
          },
          "findings": []
        }
        """;

    [Fact]
    public async Task EveryToolsSuccessResult_CarriesTheMetaProvenanceStamp()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var stamped = new List<string>();
        var eventsPath = WriteTempEvents(PassingEvents);

        try
        {
            // ── The six tools the default harness can drive to success, plus explain_run ────────
            await using (var harness = await McpTestHarness.StartAsync(cts.Token))
            {
                // serverInfo.version as a REAL client received it in the initialize handshake — the
                // acceptance criterion is that meta.serverVersion matches it, so it is read from the
                // handshake rather than from ServerIdentity a second time.
                var handshakeVersion = harness.Client.ServerInfo.Version;

                await AssertStampedAsync(harness, "validate_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") }, handshakeVersion, stamped, cts.Token);
                await AssertStampedAsync(harness, "list_step_types", null, handshakeVersion, stamped, cts.Token);
                await AssertStampedAsync(harness, "describe_step_type", new() { ["type"] = "http.rest" }, handshakeVersion, stamped, cts.Token);
                await AssertStampedAsync(harness, "search_docs", new() { ["query"] = "verifyMode" }, handshakeVersion, stamped, cts.Token);
                await AssertStampedAsync(
                    harness,
                    "scaffold_suite",
                    new()
                    {
                        ["steps"] = new object[]
                        {
                            new Dictionary<string, object?> { ["id"] = "get-api", ["type"] = "http.rest" },
                        },
                    },
                    handshakeVersion,
                    stamped,
                    cts.Token);
                await AssertStampedAsync(harness, "explain_run", new() { ["eventsPath"] = eventsPath }, handshakeVersion, stamped, cts.Token);
                await AssertStampedAsync(harness, "diagnose_run", new() { ["eventsPath"] = eventsPath }, handshakeVersion, stamped, cts.Token);
            }

            // ── run_suite: needs a suite runner to reach a verdict ─────────────────────────────
            await using (var harness = await McpTestHarness.StartAsync(
                cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEvents, exitCode: 0)))
            {
                await AssertStampedAsync(
                    harness, "run_suite", new() { ["path"] = FixturePath("good-suite.e2e.yaml") },
                    harness.Client.ServerInfo.Version, stamped, cts.Token);
            }

            // ── plan_coverage: needs a `plan --json` handler ───────────────────────────────────
            var planCli = FakeVouchfxCli.WithPlanHandler(
                CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version),
                _ => CliInvocationResult.Completed(0, PlanReportJson, string.Empty));
            await using (var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: planCli))
            {
                await AssertStampedAsync(
                    harness, "plan_coverage", new() { ["path"] = "suites/" },
                    harness.Client.ServerInfo.Version, stamped, cts.Token);

                // Fail-closed: the set proved above must be EXACTLY the advertised tool surface, so
                // a tenth tool cannot be added without either carrying the stamp or failing here.
                var advertised = (await harness.Client.ListToolsAsync(cancellationToken: cts.Token))
                    .Select(t => t.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                Assert.Equal(advertised, stamped.OrderBy(name => name, StringComparer.Ordinal).ToArray());
                Assert.Equal(9, stamped.Count);
            }
        }
        finally
        {
            File.Delete(eventsPath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task MetaStamp_AppearsInBothTheTextContentBlockAndStructuredContent()
    {
        // StructuredToolResult deliberately carries the same JSON twice — a text Content block for
        // clients that only read Content, and StructuredContent for those that parse it. A stamp
        // present in only one of them would be invisible to half of all hosts, so both are asserted
        // over the REAL wire (not just against the builder in StructuredToolResultTests).
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "search_docs",
            new Dictionary<string, object?> { ["query"] = "verifyMode" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);

        var structured = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.");
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Equal(text, structured.GetRawText());

        using var reparsedText = JsonDocument.Parse(text);
        Assert.Equal(
            ToolMetaProvider.Current.SchemaVersion,
            reparsedText.RootElement.GetProperty("meta").GetProperty("schemaVersion").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static async Task AssertStampedAsync(
        McpTestHarness harness,
        string toolName,
        Dictionary<string, object?>? arguments,
        string handshakeServerVersion,
        List<string> stamped,
        CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false, $"'{toolName}' did not return a success result.");

        var structured = result.StructuredContent
            ?? throw new InvalidOperationException($"'{toolName}' returned no StructuredContent.");

        Assert.True(
            structured.TryGetProperty("meta", out var meta),
            $"'{toolName}' returned a success result with no 'meta' stamp.");

        Assert.Equal(ToolMetaProvider.Current.SchemaVersion, meta.GetProperty("schemaVersion").GetString());
        Assert.Equal(handshakeServerVersion, meta.GetProperty("serverVersion").GetString());
        Assert.Equal(ToolMetaProvider.Current.WorkspaceRoot, meta.GetProperty("workspaceRoot").GetString());
        Assert.Equal(3, meta.EnumerateObject().Count());

        stamped.Add(toolName);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string WriteTempEvents(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"real-tool-meta-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
