using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-05's wire-facing goldens for <c>get_run_events</c>, driven over the real MCP protocol
/// through <see cref="McpTestHarness"/>: the shape a real client sees, the WIRE vocabulary the
/// events carry, the sanitisation the relay applies, and the <c>run_suite</c> → <c>get_run_events</c>
/// round trip through one shared run registry.
/// </summary>
/// <remarks>
/// <para>
/// The filter-then-paginate arithmetic and every argument bound are covered exhaustively against
/// <see cref="GetRunEventsOrchestrator"/> directly in <c>Run/GetRunEventsOrchestratorTests.cs</c>;
/// these tests confirm what a HOST observes, which is a different question.
/// </para>
/// <para>
/// Control characters are composed from numeric constants rather than written as literals — see
/// <c>Run/RawEventRelayTests</c>'s remarks for why a raw ESC byte does not belong in a source file.
/// </para>
/// </remarks>
public class RealGetRunEventsMcpTests
{
    private const char Escape = (char)0x1B;
    private const char Bell = (char)0x07;

    /// <summary>The two event types <see cref="GetRunEvents_AfterARunSuiteCallThisSession_ReturnsThatRunsRawEvents"/>'s fixture emits, in file order.</summary>
    private static readonly string[] ExpectedRunSuiteEventTypes = ["step-completed", "scenario-completed"];

    [Fact]
    public async Task GetRunEvents_AfterARunSuiteCallThisSession_ReturnsThatRunsRawEvents()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string events = """
            {"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 1);

        // The registry is injected so this test can CROSS-CHECK the id, not to obtain it: since
        // US-S3-05, run_suite's success result carries `runId` itself, which is the whole in-band
        // hand-off this test exists to prove. (Before that it did not, and a host had no way at all
        // to reach its own run's events — the gap a gatekeeper review's M2 named.) Keeping the
        // registry injected still buys something: it proves the id the CALLER was handed is the same
        // entry get_run_events resolves, rather than two ids that merely happen to work.
        var registry = new InMemoryRunRegistry();
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: runner, runRegistry: registry);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            cancellationToken: cts.Token);
        Assert.False(runResult.IsError ?? false);

        var runPayload = runResult.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        var runId = runPayload.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var recorded = Assert.Single(registry.ListRuns());
        Assert.Equal(recorded.RunId, runId);

        // The whole round trip a host actually performs: run_suite → its own runId → get_run_events.
        var payload = await CallAsync(harness, Arguments(runId), cts.Token);

        // The two tools share ONE run registry within a session — the same sharing run_suite and
        // explain_run already rely on.
        var types = payload.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(ExpectedRunSuiteEventTypes, types);
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("eventSchemaVersion").GetString()));

        // `truncated` is part of the wire shape and is present on every page, not only a short one —
        // a host that only saw it when it was true would have no way to distinguish "complete" from
        // "an older server that never reported this".
        Assert.False(payload.GetProperty("truncated").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_UsesWireVocabulary_NeverTheMcpResponseStrings()
    {
        // The story's FIRST Gherkin scenario. sprint-00-overview.md §5: a raw-event-stream-reading
        // tool uses PASS/FAIL/ENV_ERROR/INCONCLUSIVE, never Pass/Fail/EnvironmentError/Inconclusive.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun("""
            {"type":"step-completed","stepId":"provision-broker","verdict":"ENV_ERROR","durationMs":12}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

            var payload = await CallAsync(harness, Arguments(RunIdOf(registry)), cts.Token);

            Assert.Equal("ENV_ERROR", payload.GetProperty("events")[0].GetProperty("verdict").GetString());

            // …and the response string appears NOWHERE in the whole result. A negative assertion,
            // because the failure mode this guards against is a well-meaning translation added later
            // "for consistency".
            var raw = payload.GetRawText();
            Assert.DoesNotContain("EnvironmentError", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Inconclusive", raw, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_SanitisesRelayedText_TheSameWayExplainRunDoes()
    {
        // The story's THIRD Gherkin scenario. Asserted against the SAME TextSanitiser explain_run's
        // relay uses, not against a literal — a hand-written expectation could agree with itself
        // while both relays drifted from each other.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var hostile = "captured-" + Escape + "]0;pwned" + Bell + "-value";
        var observation = JsonSerializer.Serialize(new Dictionary<string, string> { ["captured"] = hostile });
        var (registry, path) = GivenRun(
            """{"type":"step-attempt","stepId":"verify-order","attempt":1,"observation":""" + observation + "}");
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

            var payload = await CallAsync(harness, Arguments(RunIdOf(registry)), cts.Token);
            var captured = payload.GetProperty("events")[0]
                .GetProperty("observation").GetProperty("captured").GetString();

            Assert.Equal(TextSanitiser.SanitiseForDisplay(hostile), captured);
            Assert.DoesNotContain(Escape, payload.GetRawText());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_PagesOverTheWireWithAnOpaqueCursor()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var lines = Enumerable.Range(1, 25).Select(i => $$"""{"type":"step-attempt","stepId":"s","attempt":{{i}}}""");
        var (registry, path) = GivenRun(string.Join('\n', lines));
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var runId = RunIdOf(registry);

            var firstArguments = Arguments(runId);
            firstArguments["limit"] = 10;
            var first = await CallAsync(harness, firstArguments, cts.Token);

            Assert.Equal(10, first.GetProperty("events").GetArrayLength());
            var cursor = first.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrEmpty(cursor));

            // Opaque on the wire too: a host must see nothing it could come to parse.
            Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
            Assert.DoesNotContain(CursorScopes.RunEvents, cursor, StringComparison.Ordinal);

            var secondArguments = Arguments(runId);
            secondArguments["limit"] = 10;
            secondArguments["cursor"] = cursor;
            var second = await CallAsync(harness, secondArguments, cts.Token);

            Assert.Equal(
                Enumerable.Range(11, 10),
                second.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("attempt").GetInt32()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_OmitsNextCursorEntirelyOnTheLastPage()
    {
        // spec §5.11 types it `nextCursor?`, so the last page must not carry `"nextCursor": null` —
        // a host checking for the property's PRESENCE would page forever.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun("""{"type":"step-completed","stepId":"a","verdict":"PASS"}""");
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

            var payload = await CallAsync(harness, Arguments(RunIdOf(registry)), cts.Token);

            Assert.False(payload.TryGetProperty("nextCursor", out _));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData("run-does-not-exist", null, "VFX-E-1505")]
    [InlineData(null, "not-a-real-cursor", "VFX-E-1506")]
    public async Task GetRunEvents_StructuredErrors_CarryTheCatalogueCodeAndNeverCrashTheServer(
        string? unknownRunId, string? badCursor, string expectedCode)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun("""{"type":"a"}""");
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

            var arguments = Arguments(unknownRunId ?? RunIdOf(registry));
            if (badCursor is not null)
            {
                arguments["cursor"] = badCursor;
            }

            var result = await harness.Client.CallToolAsync("get_run_events", arguments, cancellationToken: cts.Token);

            Assert.True(result.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains(expectedCode, content.Text, StringComparison.Ordinal);

            // The server keeps serving afterwards — a bad argument is a tool error, never a crash.
            var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            Assert.Contains(tools, tool => tool.Name == "get_run_events");
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_AnOutOfRangeLimit_IsRefusedWithVfxE1006()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun("""{"type":"a"}""");
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

            var arguments = Arguments(RunIdOf(registry));
            arguments["limit"] = GetRunEventsOrchestrator.MaxLimit + 1;
            var result = await harness.Client.CallToolAsync("get_run_events", arguments, cancellationToken: cts.Token);

            Assert.True(result.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains("VFX-E-1006", content.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunEvents_IsAdvertisedAsAReadOnlyTool()
    {
        // US-S3-04's AC: read-only tools stay lock-free, and this annotation is what tells a host it
        // is safe to call concurrently (spec §4.6). RunLockSourceGuardTests holds the other half —
        // that no read-only tool can take the lock at all.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == "get_run_events");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    private static Dictionary<string, object?> Arguments(string? runId) =>
        new(StringComparer.Ordinal) { ["runId"] = runId };

    private static string RunIdOf(StubRunRegistry registry) => registry.ListRuns()[0].RunId;

    private static async Task<JsonElement> CallAsync(
        McpTestHarness harness, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync("get_run_events", arguments, cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false, DescribeFailure(result));
        return result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
    }

    private static string DescribeFailure(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no content)";

    /// <summary>A registry holding one completed run whose events file the caller must delete.</summary>
    private static (StubRunRegistry Registry, string Path) GivenRun(string eventsFileContent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"real-get-run-events-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, eventsFileContent);
        return (StubRunRegistry.WithCompletedRun(path), path);
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
