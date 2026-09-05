using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spec C / M2 Healer end-to-end through the in-memory MCP harness: <c>diagnose_run</c> returns
/// Fail proposals, EnvironmentError guidance, and keeps <c>explain_run</c> working.
/// </summary>
public class RealDiagnoseRunMcpTests
{
    [Fact]
    public async Task DiagnoseRun_FailFixture_ReturnsProposalWithPatch()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-run-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80,"observation":{"expected":"SHIPPED","actual":"PENDING"}}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""",
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("Fail", payload.GetProperty("diagnosis").GetProperty("verdict").GetString());
            var proposals = payload.GetProperty("proposals").EnumerateArray().ToArray();
            Assert.NotEmpty(proposals);
            Assert.Equal("assert-order-status", proposals[0].GetProperty("stepId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(proposals[0].GetProperty("patch").GetString()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_EnvironmentErrorFixture_EmptyProposalsAndGuidancePresent()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-env-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"pull access denied"}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""",
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("EnvironmentError", payload.GetProperty("diagnosis").GetProperty("verdict").GetString());
            Assert.Empty(payload.GetProperty("proposals").EnumerateArray().ToArray());
            Assert.NotEmpty(payload.GetProperty("environmentGuidance").EnumerateArray().ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_StillWorks_AlongsideDiagnoseRun()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-explain-still-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""",
            cts.Token);
        try
        {
            var explain = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);
            Assert.False(explain.IsError ?? false);
            Assert.Equal("Pass", explain.StructuredContent!.Value.GetProperty("verdict").GetString());

            var diagnose = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);
            Assert.False(diagnose.IsError ?? false);
            Assert.Equal("Pass", diagnose.StructuredContent!.Value.GetProperty("diagnosis").GetProperty("verdict").GetString());
            Assert.Empty(diagnose.StructuredContent!.Value.GetProperty("proposals").EnumerateArray().ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_BadPath_ReturnsToolErrorAndServerStillListsTwelveTools()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "diagnose_run",
            new Dictionary<string, object?> { ["eventsPath"] = @"\\attacker-host\share\events.jsonl" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var toolsAfterError = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(13, toolsAfterError.Count);
        Assert.Contains(toolsAfterError, t => t.Name == "diagnose_run");
        Assert.Contains(toolsAfterError, t => t.Name == "explain_run");

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
