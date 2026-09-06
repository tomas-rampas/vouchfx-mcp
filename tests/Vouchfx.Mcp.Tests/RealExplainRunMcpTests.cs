using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 8 (REQ-007, EDGE-004) end to end, through the same in-memory MCP harness the other
/// <c>Real*McpTests</c> classes use: <c>explain_run</c> actually diagnosing an events file, the
/// session last-run default (via a prior <c>run_suite</c> call sharing the SAME harness's tracker),
/// and the structured error shapes for bad paths.
/// </summary>
/// <remarks>
/// The diagnosis LOGIC itself (all four verdicts, RETRY timelines, the 64KB response cap) is already
/// covered exhaustively against <see cref="Vouchfx.Mcp.Diagnosis.ExplainRunOrchestrator"/> directly in
/// <c>Diagnosis/ExplainRunOrchestratorTests.cs</c>; these tests instead confirm the MCP-FACING
/// contract — the JSON shape a real client sees, and that <c>run_suite</c> and <c>explain_run</c>
/// genuinely share one tracker within a single server session.
/// </remarks>
public class RealExplainRunMcpTests
{
    [Fact]
    public async Task ExplainRun_AfterAPriorRunSuiteCallThisSession_DefaultsToItsEventsFileWithNoArgument()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string events = """
            {"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 1);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            cancellationToken: cts.Token);
        Assert.False(runResult.IsError ?? false);

        // No eventsPath at all: explain_run must default to the SAME harness's tracker, which
        // run_suite just updated.
        var explainResult = await harness.Client.CallToolAsync("explain_run", cancellationToken: cts.Token);

        Assert.False(explainResult.IsError ?? false);
        var payload = explainResult.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal("Fail", payload.GetProperty("verdict").GetString());
        var notableSteps = payload.GetProperty("notableSteps").EnumerateArray().ToArray();
        var step = Assert.Single(notableSteps);
        Assert.Equal("assert-order-status", step.GetProperty("stepId").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// US-S4-02's golden wire test: the structured content a real client sees carries the top-level
    /// <c>classificationHints</c> array, a per-step <c>reason</c> object, and a per-environment-error
    /// <c>reason</c> — in the SDK's camelCase wire spelling.
    /// </summary>
    /// <remarks>
    /// The classification LOGIC is covered exhaustively against the classifier and the orchestrator
    /// directly (<c>Diagnosis/VerdictReasonClassifierTests</c>,
    /// <c>Diagnosis/ExplainRunOrchestratorTests</c>); what only a wire test can prove is the JSON
    /// SHAPE — that the fields are present under the names a host branches on, and that a null kind
    /// serialises as a real null rather than vanishing.
    /// </remarks>
    [Fact]
    public async Task ExplainRun_StructuredContent_CarriesClassificationHintsAndPerItemReason()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-explain-run-reason-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """,
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

            var hints = payload.GetProperty("classificationHints").EnumerateArray()
                .Select(hint => hint.GetString())
                .ToArray();
            Assert.Equal(2, hints.Length);
            Assert.Contains("Expected 120.00, actual 95.00.", hints);
            Assert.Contains("Resource events never became healthy within 30000ms; check its logs.", hints);

            var step = Assert.Single(payload.GetProperty("notableSteps").EnumerateArray().ToArray());
            var stepReason = step.GetProperty("reason");
            Assert.Equal("assertion", stepReason.GetProperty("kind").GetString());
            Assert.Equal("Expected 120.00, actual 95.00.", stepReason.GetProperty("hint").GetString());

            var error = Assert.Single(payload.GetProperty("environmentErrors").EnumerateArray().ToArray());
            var errorReason = error.GetProperty("reason");
            Assert.Equal("unhealthy", errorReason.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(errorReason.GetProperty("hint").GetString()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// An unclassifiable step's <c>reason</c> reaches the wire as an explicit <c>null</c>, and an
    /// unrecognised error kind's reason carries a null <c>kind</c> beside a real hint — the two
    /// distinct "we do not know" shapes US-S4-01's fail-closed default produces.
    /// </summary>
    [Fact]
    public async Task ExplainRun_StructuredContent_RendersAnUnclassifiedReasonAndANullKindAsExplicitNulls()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-explain-run-nullreason-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120}
            {"type":"environment-error","errorKind":"SomeFutureEngineKind","resourceName":"events","detail":"never heard of it"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """,
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

            var step = Assert.Single(payload.GetProperty("notableSteps").EnumerateArray().ToArray());
            Assert.Equal(JsonValueKind.Null, step.GetProperty("reason").ValueKind);

            var error = Assert.Single(payload.GetProperty("environmentErrors").EnumerateArray().ToArray());
            var errorReason = error.GetProperty("reason");
            Assert.Equal(JsonValueKind.Null, errorReason.GetProperty("kind").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(errorReason.GetProperty("hint").GetString()));

            // Only the environment error is described, so only its hint is listed.
            Assert.Single(payload.GetProperty("classificationHints").EnumerateArray().ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_ExplicitEventsPath_ReturnsDiagnosisAsStructuredSuccess()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-explain-run-test-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""" + "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""",
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("Pass", payload.GetProperty("verdict").GetString());
            Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("categoryMeaning").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("summary").GetString()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_NoEventsPathAndNoPriorRunThisSession_ReturnsToolLevelError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync("explain_run", cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("run_suite", content.Text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("does-not-matter-missing.jsonl")]
    [InlineData(@"\\attacker-host\share\events.jsonl")]
    public async Task ExplainRun_BadEventsPath_ReturnsToolLevelErrorWithoutCrashingServer(string eventsPath)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "explain_run",
            new Dictionary<string, object?> { ["eventsPath"] = eventsPath },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);

        // The error must be a normal tool result, not a crash: the server has to still be
        // responding to further requests afterwards.
        var toolsAfterError = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(18, toolsAfterError.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
