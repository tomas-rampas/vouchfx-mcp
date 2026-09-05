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
        Assert.Equal(16, toolsAfterError.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
