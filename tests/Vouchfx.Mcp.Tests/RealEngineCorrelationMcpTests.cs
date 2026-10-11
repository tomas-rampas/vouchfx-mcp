using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>Real MCP stream transport over a controlled events file, not a live engine invocation.</summary>
public class RealEngineCorrelationMcpTests
{
    private const string FirstId = "1234567890abcdef1234567890abcdef";
    private const string SecondId = "abcdef1234567890abcdef1234567890";
    private const string Events =
        """
        {"type":"reproducibility-envelope","runId":"1234567890abcdef1234567890abcdef","secretReferences":["private-name-canary"]}
        {"type":"step-attempt","runId":"1234567890abcdef1234567890abcdef","stepId":"check","attempt":1,"tMs":1,"outcome":"FAIL"}
        {"type":"step-completed","runId":"1234567890abcdef1234567890abcdef","stepId":"check","verdict":"FAIL","durationMs":1}
        {"type":"environment-error","runId":"abcdef1234567890abcdef1234567890","errorKind":"Provision","resourceName":"db"}
        {"type":"scenario-completed","runId":"abcdef1234567890abcdef1234567890","scenarioId":"other","verdict":"ENV_ERROR"}
        """;

    [Theory]
    [InlineData("explain_run")]
    [InlineData("diagnose_run")]
    [InlineData("get_step_timeline")]
    [InlineData("get_run_artifacts")]
    public async Task DerivedTools_RetainDistinctEngineIdsWithoutReplacingRegistryIdentity(string tool)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, Events, cts.Token);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path);
            var entry = registry.ListRuns()[0];
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var arguments = Arguments(tool, path, entry);
            var result = await harness.Client.CallToolAsync(tool, arguments, cancellationToken: cts.Token);
            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected structured content.");
            Assert.DoesNotContain("private-name-canary", payload.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("secretReferences", payload.GetRawText(), StringComparison.Ordinal);
            if (tool == "diagnose_run")
            {
                payload = payload.GetProperty("diagnosis");
            }

            Assert.True(payload.TryGetProperty("engineCorrelation", out var correlation));
            Assert.Equal(new[] { FirstId, SecondId }, correlation.GetProperty("runIds").EnumerateArray().Select(v => v.GetString()).ToArray());
            Assert.False(correlation.GetProperty("partial").GetBoolean());
            if (tool == "get_run_artifacts")
            {
                Assert.Equal(entry.RunId, payload.GetProperty("runId").GetString());
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RawEvents_AlreadyRelayEngineIds_WhileStatusKeepsItsServerId()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-raw-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, Events, cts.Token);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path);
            var entry = registry.ListRuns()[0];
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var arguments = new Dictionary<string, object?> { ["runId"] = entry.RunId };
            var result = await harness.Client.CallToolAsync("get_run_events", arguments, cancellationToken: cts.Token);
            Assert.False(result.IsError ?? false);
            var raw = result.StructuredContent!.Value.GetProperty("events");
            Assert.Equal(5, raw.GetArrayLength());
            Assert.Equal(FirstId, raw[0].GetProperty("runId").GetString());
            Assert.Equal(SecondId, raw[4].GetProperty("runId").GetString());
            var status = await harness.Client.CallToolAsync("get_run_status", arguments, cancellationToken: cts.Token);
            Assert.False(status.IsError ?? false);
            var recordedRun = status.StructuredContent!.Value.GetProperty("run");
            Assert.Equal(entry.RunId, recordedRun.GetProperty("runId").GetString());
            Assert.False(recordedRun.TryGetProperty("engineCorrelation", out _));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static Dictionary<string, object?> Arguments(string tool, string path, RunRegistryEntry entry) => tool switch
    {
        "explain_run" or "diagnose_run" => new() { ["eventsPath"] = path },
        "get_step_timeline" => new() { ["runId"] = entry.RunId, ["specPath"] = entry.SpecPaths[0], ["stepId"] = "check" },
        _ => new() { ["runId"] = entry.RunId, ["kind"] = "environment" },
    };

    [Fact]
    public async Task MalformedUtf8Id_DoesNotDiscardTheUsableStepOverTheWire()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-utf8-{Guid.NewGuid():N}.jsonl");
        var prefix = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"step-completed\",\"stepId\":\"check\",\"verdict\":\"FAIL\",\"runId\":\"bad");
        var suffix = System.Text.Encoding.UTF8.GetBytes("\"}");
        await File.WriteAllBytesAsync(path, [.. prefix, 0xff, .. suffix], cts.Token);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token);
            var result = await harness.Client.CallToolAsync("explain_run", new Dictionary<string, object?> { ["eventsPath"] = path }, cancellationToken: cts.Token);
            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent!.Value;
            Assert.Equal("Fail", payload.GetProperty("verdict").GetString());
            Assert.Equal("check", payload.GetProperty("notableSteps")[0].GetProperty("stepId").GetString());
            var correlation = payload.GetProperty("engineCorrelation");
            Assert.Equal(0, correlation.GetProperty("runIds").GetArrayLength());
            Assert.True(correlation.GetProperty("partial").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task AFileReadThatHitsTheExistingByteCap_MarksItsObservedIdsPartial()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-file-cap-{Guid.NewGuid():N}.jsonl");
        await using (var file = File.Create(path))
        {
            var prefix = System.Text.Encoding.UTF8.GetBytes("""{"type":"scenario-completed","verdict":"PASS","runId":"known-before-cap"}""" + "\n");
            await file.WriteAsync(prefix, cts.Token);
            file.SetLength(EventsFileReader.MaxEventsFileBytes + 1);
        }

        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token);
            var result = await harness.Client.CallToolAsync("explain_run", new Dictionary<string, object?> { ["eventsPath"] = path }, cancellationToken: cts.Token);
            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent!.Value;
            Assert.Equal("Pass", payload.GetProperty("verdict").GetString());
            Assert.True(payload.GetProperty("eventsTruncated").GetBoolean());
            var correlation = payload.GetProperty("engineCorrelation");
            Assert.Equal("known-before-cap", correlation.GetProperty("runIds")[0].GetString());
            Assert.True(correlation.GetProperty("partial").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task MultiSuiteRun_ConcatenatesEngineIdsWithoutCreatingASingleEngineIdentity()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), $"mcp-correlation-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "first.e2e.yaml");
        var second = Path.Combine(directory, "second.e2e.yaml");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "good-suite.e2e.yaml");
        File.Copy(fixture, first);
        File.Copy(fixture, second);
        var runner = FakeSuiteRunner.PerSuite(path =>
        {
            var id = Path.GetFileName(path) == "first.e2e.yaml" ? FirstId : SecondId;
            return (JsonSerializer.Serialize(new { type = "scenario-completed", runId = id, verdict = "PASS" }), 0);
        });
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);
            var run = await harness.Client.CallToolAsync("run_suite", new Dictionary<string, object?> { ["paths"] = new[] { first, second } }, cancellationToken: cts.Token);
            Assert.False(run.IsError ?? false);
            Assert.Equal(2, runner.InvocationCount);
            var serverId = run.StructuredContent!.Value.GetProperty("runId").GetString()!;
            Assert.StartsWith("run-", serverId, StringComparison.Ordinal);
            var explained = await harness.Client.CallToolAsync("explain_run", cancellationToken: cts.Token);
            Assert.False(explained.IsError ?? false);
            var correlation = explained.StructuredContent!.Value.GetProperty("engineCorrelation");
            Assert.Equal(new[] { FirstId, SecondId }, correlation.GetProperty("runIds").EnumerateArray().Select(v => v.GetString()).ToArray());
            Assert.False(correlation.GetProperty("partial").GetBoolean());
            Assert.DoesNotContain(serverId, correlation.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task BoundedCorrelation_SurvivesAllDerivedResponseShrinkLadders()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-budget-{Guid.NewGuid():N}.jsonl");
        var ids = Enumerable.Range(0, EngineCorrelation.MaxRunIds).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('+', EngineCorrelation.MaxRunIdChars - 4)).ToArray();
        var lines = ids.Select(id => JsonSerializer.Serialize(new { type = "future-event", runId = id })).ToList();
        for (var i = 0; i < 500; i++)
        {
            lines.Add(JsonSerializer.Serialize(new { type = "step-attempt", stepId = "check", attempt = i + 1, outcome = "FAIL", observation = new string('+', 2_000) }));
            lines.Add(JsonSerializer.Serialize(new { type = "step-completed", stepId = "check-" + i, verdict = "FAIL", observation = new string('+', 2_000) }));
            lines.Add(JsonSerializer.Serialize(new { type = "environment-error", resourceName = i + new string('+', 200), errorKind = new string('+', 100), detail = new string('+', 400) }));
        }

        await File.WriteAllTextAsync(path, string.Join('\n', lines), cts.Token);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path);
            var entry = registry.ListRuns()[0];
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            foreach (var tool in new[] { "explain_run", "diagnose_run", "get_step_timeline", "get_run_artifacts" })
            {
                var result = await harness.Client.CallToolAsync(tool, Arguments(tool, path, entry), cancellationToken: cts.Token);
                Assert.False(result.IsError ?? false);
                var payload = result.StructuredContent!.Value;
                using var bytes = new MemoryStream();
                using (var writer = new Utf8JsonWriter(bytes))
                {
                    writer.WriteStartObject();
                    foreach (var property in payload.EnumerateObject().Where(p => p.Name != "meta"))
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                Assert.True(bytes.Length <= 32_768, $"{tool}: {bytes.Length} payload bytes exceed the existing 32768-byte budget.");
                var diagnosis = tool == "diagnose_run" ? payload.GetProperty("diagnosis") : payload;
                var correlation = diagnosis.GetProperty("engineCorrelation");
                Assert.Equal(ids, correlation.GetProperty("runIds").EnumerateArray().Select(v => v.GetString()).ToArray());
                Assert.False(correlation.GetProperty("partial").GetBoolean());
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("explain_run")]
    [InlineData("diagnose_run")]
    [InlineData("get_step_timeline")]
    [InlineData("get_run_artifacts")]
    [InlineData("reports")]
    [InlineData("logs")]
    public async Task AnUnobservedEngineId_IsOmittedRatherThanInventedFromTheServerId(string surface)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = Path.Combine(Path.GetTempPath(), $"mcp-correlation-absent-{Guid.NewGuid():N}.jsonl");
        var sectionOnly = surface is "reports" or "logs";
        var content = sectionOnly ? Events : """{"type":"step-completed","stepId":"check","verdict":"PASS"}""";
        await File.WriteAllTextAsync(path, content, cts.Token);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path);
            var entry = registry.ListRuns()[0];
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var tool = sectionOnly ? "get_run_artifacts" : surface;
            var arguments = Arguments(tool, path, entry);
            if (sectionOnly)
            {
                arguments["kind"] = surface;
            }

            var result = await harness.Client.CallToolAsync(tool, arguments, cancellationToken: cts.Token);
            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent!.Value;
            if (tool == "diagnose_run")
            {
                payload = payload.GetProperty("diagnosis");
            }

            Assert.False(payload.TryGetProperty("engineCorrelation", out _));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
