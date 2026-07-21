using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 7 (REQ-006, EDGE-001, EDGE-002, EDGE-003) end to end, through the same in-memory MCP
/// harness the other <c>Real*McpTests</c> classes use: <c>run_suite</c> actually executing a suite
/// (via a <see cref="FakeSuiteRunner"/> — never the real CLI or Docker), MCP progress notifications
/// actually flowing over the wire to a client, the EDGE-003 "suite-invalid, not run" result shape,
/// tag pass-through, and single-flight concurrency observed at the protocol layer.
/// </summary>
/// <remarks>
/// The orchestration LOGIC itself (every gate, all four verdicts, EDGE-001/EDGE-002) is already
/// covered exhaustively against <see cref="RunSuiteOrchestrator"/> directly in
/// <c>Run/RunSuiteOrchestratorTests.cs</c>; these tests instead confirm the MCP-FACING contract —
/// the JSON shape a real client sees, and that progress notifications genuinely cross the wire.
/// </remarks>
public class RealRunSuiteMcpTests
{
    [Fact]
    public async Task RunSuite_ValidSuitePassing_ReturnsStructuredResultAndEmitsProgressOverTheWire()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var runner = FakeSuiteRunner.Succeeding(["Starting DCP..."], events, exitCode: 0);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        // ConcurrentBag, not List: the notification handler ProgressCapture registers can be
        // invoked on a different thread than the one polling WaitUntilAsync below (the MCP SDK's
        // message loop dispatches each notification as an independent task — see ProgressCapture's
        // own remarks), and a plain List is not safe for concurrent add-while-enumerate.
        //
        // ProgressCapture.CallAsync, not harness.Client.CallToolAsync(..., IProgress<...>, ...):
        // that SDK convenience overload unregisters its progress handler the instant its own
        // response arrives, racing the message loop's independent dispatch of an
        // already-received-but-not-yet-processed progress notification — a genuine, confirmed SDK
        // race (ModelContextProtocol.Core 1.4.1) that PERMANENTLY drops the notification if the
        // response's dispatch wins, not merely delays it (see ProgressCapture's remarks for the
        // full mechanism). ProgressCapture keeps its own registration alive independently of the
        // call's request/response lifecycle so the WaitUntilAsync below actually has something
        // meaningful to wait for.
        var progressUpdates = new ConcurrentBag<ProgressNotificationValue>();

        var (result, progressRegistration) = await ProgressCapture.CallAsync(
            harness.Client,
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            progressUpdates,
            cts.Token);
        await using var _ = progressRegistration;

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal("Pass", payload.GetProperty("verdict").GetString());
        Assert.Equal(0, payload.GetProperty("exitCode").GetInt32());
        Assert.False(payload.GetProperty("cancelled").GetBoolean());
        Assert.False(payload.GetProperty("timedOut").GetBoolean());
        var steps = payload.GetProperty("steps").EnumerateArray().ToArray();
        var step = Assert.Single(steps);
        Assert.Equal("check-health", step.GetProperty("stepId").GetString());
        Assert.Equal("Pass", step.GetProperty("verdict").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("eventsFilePath").GetString()));

        // Progress delivery is best-effort/unordered over MCP (confirmed during design) — assert
        // only that AT LEAST ONE notification arrived, giving delivery a short bounded grace since
        // notifications are separate wire messages that can trail the tool call's own response.
        await WaitUntilAsync(() => !progressUpdates.IsEmpty, TimeSpan.FromSeconds(5));
        Assert.NotEmpty(progressUpdates);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunSuite_InvalidSuite_ReturnsSuiteInvalidPayloadNotAToolError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("bad-suite.e2e.yaml") },
            cancellationToken: cts.Token);

        // EDGE-003: an invalid SUITE is not a tool-call error — validate_suite treats "valid:false"
        // the same way, and run_suite mirrors that philosophy for its own "did not run" case.
        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal("suite-invalid", payload.GetProperty("kind").GetString());
        Assert.False(payload.GetProperty("validation").GetProperty("valid").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("validation").GetProperty("errors").EnumerateArray());
        Assert.Equal(0, runner.InvocationCount);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunSuite_MissingFile_ReturnsSuiteInvalidWithFileNotFoundKind()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("does-not-exist.e2e.yaml") },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        var errors = payload.GetProperty("validation").GetProperty("errors").EnumerateArray().ToArray();
        Assert.Contains(errors, e => e.GetProperty("kind").GetString() == "file-not-found");
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunSuite_PathBeginningWithDash_ReturnsToolLevelErrorWithoutInvokingRunner()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = "--dangerous-flag" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("begin with", content.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunSuite_TagsArgument_IsPassedThroughToTheRunnerSpec()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SuiteRunSpec? capturedSpec = null;
        var runner = FakeSuiteRunner.Succeeding([], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0);
        var capturingRunner = new CapturingSuiteRunner(runner, spec => capturedSpec = spec);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: capturingRunner);

        string[] requestedTags = ["smoke", "nightly"];
        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?>
            {
                ["path"] = FixturePath("good-suite.e2e.yaml"),
                ["tags"] = requestedTags,
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.NotNull(capturedSpec);
        Assert.Equal(["smoke", "nightly"], capturedSpec!.Tags);
    }

    [Fact]
    public async Task RunSuite_ConcurrentCalls_SecondReturnsAlreadyRunningToolError()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gate = new TaskCompletionSource<SuiteProcessResult>();
        var runner = FakeSuiteRunner.Blocking(gate);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var firstCallTask = harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            cancellationToken: cts.Token);

        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));

        var secondResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            cancellationToken: cts.Token);

        Assert.True(secondResult.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(secondResult.Content));
        Assert.Contains("already in progress", content.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runner.InvocationCount);

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        var firstResult = await firstCallTask;
        Assert.False(firstResult.IsError ?? false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    /// <summary>Wraps another <see cref="ISuiteRunner"/> and records the <see cref="SuiteRunSpec"/> it was called with.</summary>
    private sealed class CapturingSuiteRunner(ISuiteRunner inner, Action<SuiteRunSpec> onSpec) : ISuiteRunner
    {
        public int InvocationCount { get; private set; }

        public Task<SuiteProcessResult> RunAsync(SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
        {
            InvocationCount++;
            onSpec(spec);
            return inner.RunAsync(spec, onOutputLine, cancellationToken);
        }
    }
}
