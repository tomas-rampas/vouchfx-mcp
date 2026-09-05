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

        // US-S3-05: the result names the run it produced (spec §5.7's RunSummary.runId). Asserted on
        // the WIRE, because the whole reason the field was added is that a host had no in-band way to
        // reach its own run's events with get_run_events — a fact no in-process test could observe.
        var runId = payload.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));
        Assert.StartsWith("run-", runId, StringComparison.Ordinal);

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
        Assert.Equal("VFX-D-1100", payload.GetProperty("code").GetString());
        Assert.False(payload.GetProperty("validation").GetProperty("valid").GetBoolean());
        Assert.NotEmpty(payload.GetProperty("validation").GetProperty("errors").EnumerateArray());

        // The additive `path` field (a gatekeeper review's MAJOR finding): the pre-flight is
        // all-or-nothing across every suite a call covers, and the validation payload names no file
        // of its own — so without this a multi-suite caller cannot tell WHICH suite refused the run.
        Assert.EndsWith("bad-suite.e2e.yaml", payload.GetProperty("path").GetString(), StringComparison.Ordinal);

        Assert.Equal(0, runner.InvocationCount);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunSuite_MissingFile_ReturnsFileNotFoundToolErrorWithoutRunningAnything()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("does-not-exist.e2e.yaml") },
            cancellationToken: cts.Token);

        // US-S1-04 split EDGE-003's single "suite-invalid" outcome by cause. A suite that is
        // genuinely INVALID still comes back as data (see the test above, unchanged) — but a
        // MISSING file was never a statement about the suite, and now returns the same
        // VFX-E-1002 tool error validate_suite returns for the identical path. The two tools share
        // one classifier precisely so they cannot answer differently about one file.
        Assert.True(result.IsError);

        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var error = JsonDocument.Parse(content.Text);
        Assert.Equal("VFX-E-1002", error.RootElement.GetProperty("code").GetString());
        Assert.False(error.RootElement.GetProperty("retryable").GetBoolean());

        // The ERROR leg names the suite too, by prefix — the same fact the data leg carries in its
        // `path` field. The guard that wrote this message was only ever asked about one file and
        // therefore names none, which is exactly why run_suite has to supply it.
        Assert.StartsWith("'", error.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(
            "does-not-exist.e2e.yaml", error.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The defining property of this test, unchanged: nothing was spawned.
        Assert.Equal(0, runner.InvocationCount);
        Assert.Empty(consoleOut.Writer.ToString());
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

        // US-S3-04: the wire-level contract of the rejection, on the SAME-process path — the
        // cross-process one is RealCrossProcessRunLockTests'. Asserted here because this is the only
        // place the JSON a client actually parses is observed: `code`, `retryable`, and the
        // `details.runId` spec §4.6 requires (which is also this server's first-ever use of
        // VfxError.Details, so its shape is pinned rather than left to a future reader to discover).
        Assert.NotNull(secondResult.StructuredContent);
        var error = secondResult.StructuredContent.Value;
        Assert.Equal("VFX-E-1501", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(
            "https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1501.html",
            error.GetProperty("docsUrl").GetString());

        var reportedRunId = error.GetProperty("details").GetProperty("runId").GetString();
        Assert.StartsWith("run-", reportedRunId, StringComparison.Ordinal);
        Assert.Contains(reportedRunId!, content.Text, StringComparison.Ordinal);

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        var firstResult = await firstCallTask;
        Assert.False(firstResult.IsError ?? false);
    }

    // ── US-S3-02: run_suite v2 over the wire ─────────────────────────────────────────────────────

    /// <summary>
    /// The story's first Gherkin scenario, end to end: two suites in one call come back as two
    /// <c>specs</c> entries with their own outcomes, and the run's own verdict is the elevated one.
    /// </summary>
    [Fact]
    public async Task RunSuite_MultiplePaths_ReturnsPerSpecOutcomesAndTheElevatedVerdict()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var sandbox = new SuiteSandbox();
        var happyPath = sandbox.WriteSuite("happy-path.e2e.yaml");
        var timeoutCase = sandbox.WriteSuite("timeout-case.e2e.yaml");

        var runner = FakeSuiteRunner.PerSuite(path => path.EndsWith("happy-path.e2e.yaml", StringComparison.Ordinal)
            ? ("""{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0)
            : ("""{"type":"scenario-completed","scenarioId":"s2","verdict":"INCONCLUSIVE"}""", 4));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["paths"] = new[] { happyPath, timeoutCase } },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        Assert.Equal("Inconclusive", payload.GetProperty("verdict").GetString());

        var specs = payload.GetProperty("specs").EnumerateArray().ToArray();
        Assert.Equal(2, specs.Length);
        Assert.Equal(happyPath, specs[0].GetProperty("path").GetString());
        Assert.Equal("Pass", specs[0].GetProperty("outcome").GetString());
        Assert.Equal(timeoutCase, specs[1].GetProperty("path").GetString());
        Assert.Equal("Inconclusive", specs[1].GetProperty("outcome").GetString());

        Assert.Equal(2, runner.InvocationCount);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// Labels round-trip: what the caller sent is what the run registry holds — the half of spec
    /// §5.7's labels behaviour this server can implement (the JSON Lines run envelope is written by
    /// the ENGINE, which has no labels flag to forward them through, so that half is not faked).
    /// </summary>
    [Fact]
    public async Task RunSuite_Labels_AreRecordedIntoTheRunRegistryEntry()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var registry = new InMemoryRunRegistry();
        var runner = FakeSuiteRunner.Succeeding(
            [], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", exitCode: 0);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner, runRegistry: registry);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?>
            {
                ["path"] = FixturePath("good-suite.e2e.yaml"),
                ["labels"] = new Dictionary<string, string> { ["trigger"] = "agent:author", ["iteration"] = "3" },
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);

        var entry = Assert.Single(registry.ListRuns());
        Assert.Equal("agent:author", entry.Labels["trigger"]);
        Assert.Equal("3", entry.Labels["iteration"]);
        Assert.Equal([FixturePath("good-suite.e2e.yaml")], entry.SpecPaths);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The gated-feature stance (a) golden, on the wire: <c>wait: false</c> is a tool ERROR carrying
    /// <c>VFX-E-1504</c> with <c>retryable: false</c>, and names the blocking upstream ask — never a
    /// silently-blocking run, and never an unknown-field rejection.
    /// </summary>
    [Theory]
    [InlineData("wait", false)]
    [InlineData("keepEnvironment", true)]
    public async Task RunSuite_AGatedOption_IsRefusedWithVfxE1504AndNothingIsRun(string argument, bool value)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?>
            {
                ["path"] = FixturePath("good-suite.e2e.yaml"),
                [argument] = value,
            },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var error = result.StructuredContent.Value;

        Assert.Equal("VFX-E-1504", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(
            "https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1504.html",
            error.GetProperty("docsUrl").GetString());
        Assert.Contains("U4", error.GetProperty("message").GetString()!, StringComparison.Ordinal);

        Assert.Equal(0, runner.InvocationCount);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task RunSuite_BothPathAndPaths_IsRefusedWithVfxE1503AndNothingIsRun()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?>
            {
                ["path"] = FixturePath("good-suite.e2e.yaml"),
                ["paths"] = new[] { FixturePath("good-suite.e2e.yaml") },
            },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal("VFX-E-1503", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.False(result.StructuredContent.Value.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, runner.InvocationCount);
        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    /// <summary>
    /// A temp directory holding real suite files, for the multi-suite tests: two DISTINCT valid
    /// suites are needed (the expander de-duplicates, so naming one file twice is one run), and the
    /// shipped fixtures directory only has one.
    /// </summary>
    private sealed class SuiteSandbox : IDisposable
    {
        private readonly string _directory;

        public SuiteSandbox()
        {
            _directory = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-run-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public string WriteSuite(string fileName)
        {
            var fullPath = Path.Combine(_directory, fileName);
            File.WriteAllText(
                fullPath,
                """
                metadata:
                  name: "A suite"
                  owner: "platform-team"

                steps:
                  - id: check-health
                    type: http.rest
                    description: "Confirms the health endpoint responds successfully."
                    target: orders-api
                    method: GET
                    path: /health
                """);

            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

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
