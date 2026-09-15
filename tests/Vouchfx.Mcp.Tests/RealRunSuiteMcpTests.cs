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
        // race (diagnosed on ModelContextProtocol.Core 1.4.1; not re-verified on 2.2.0) that
        // PERMANENTLY drops the notification if the
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

        // vouchfx-mcp#78's additive fields are present even for the single-suite case — a caller
        // should not have to branch on how many suites it named to read the same information.
        var invalidSuites = payload.GetProperty("invalidSuites").EnumerateArray().ToArray();
        var only = Assert.Single(invalidSuites);
        Assert.EndsWith("bad-suite.e2e.yaml", only.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(only.GetProperty("errors").EnumerateArray());
        Assert.Equal(0, only.GetProperty("omittedErrorCount").GetInt32());
        Assert.Equal(0, payload.GetProperty("omittedInvalidSuiteCount").GetInt32());
        Assert.Equal(0, payload.GetProperty("undeterminedSuiteCount").GetInt32());

        Assert.Equal(0, runner.InvocationCount);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// vouchfx-mcp#78 on the wire: a call naming three broken suites answers about all three at once,
    /// so repairing a glob costs one round trip rather than one per broken file.
    /// </summary>
    [Fact]
    public async Task RunSuite_SeveralInvalidSuites_ReportsEveryOneInOneResponse()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var sandbox = new SuiteSandbox();
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var unparseable = sandbox.WriteFile("a-unparseable.e2e.yaml", "metadata: [unclosed\n");
        var valid = sandbox.WriteSuite("b-valid.e2e.yaml");
        var noSteps = sandbox.WriteFile(
            "c-no-steps.e2e.yaml",
            """
            metadata:
              name: "Missing its steps"
              owner: "platform-team"
            """);
        var notAList = sandbox.WriteFile("d-not-a-list.e2e.yaml", "steps: not-a-list\n");

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["paths"] = new[] { unparseable, valid, noSteps, notAList } },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal("VFX-D-1100", payload.GetProperty("code").GetString());

        var invalidSuites = payload.GetProperty("invalidSuites").EnumerateArray().ToArray();
        Assert.Equal(3, invalidSuites.Length);
        Assert.Equal(0, payload.GetProperty("omittedInvalidSuiteCount").GetInt32());
        Assert.Equal(0, payload.GetProperty("undeterminedSuiteCount").GetInt32());

        Assert.Equal(
            [Path.GetFileName(unparseable), Path.GetFileName(noSteps), Path.GetFileName(notAList)],
            invalidSuites.Select(entry => Path.GetFileName(entry.GetProperty("path").GetString()!)));

        // Each entry carries its OWN findings, in validate_suite's own finding shape.
        Assert.All(invalidSuites, entry =>
        {
            var errors = entry.GetProperty("errors").EnumerateArray().ToArray();
            Assert.NotEmpty(errors);
            Assert.All(errors, error => Assert.False(string.IsNullOrEmpty(error.GetProperty("code").GetString())));
        });

        // The VALID suite is absent from the list AND was not run — all-or-nothing, unchanged.
        Assert.DoesNotContain(
            Path.GetFileName(valid),
            invalidSuites.Select(entry => Path.GetFileName(entry.GetProperty("path").GetString()!)));
        Assert.Equal(0, runner.InvocationCount);

        // The retained pre-#78 fields describe the first entry, which is the suite they have always
        // described (the loop used to stop there).
        Assert.Equal(
            invalidSuites[0].GetProperty("path").GetString(),
            payload.GetProperty("path").GetString());
        Assert.False(payload.GetProperty("validation").GetProperty("valid").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// A suite with more findings than one entry may carry has its list capped and the omission
    /// COUNTED — the repo's visible-bounds rule, which forbids inferring incompleteness from a list
    /// length.
    /// </summary>
    /// <remarks>
    /// The uncapped copy of the same suite's findings is still in <c>validation.errors</c> (the
    /// pre-#78 field, deliberately untouched), which is what lets this test assert the arithmetic
    /// exactly rather than just "10 or fewer": the two must agree on how many were left out.
    /// </remarks>
    [Fact]
    public async Task RunSuite_InvalidSuiteWithManyFindings_CapsTheEntrysErrorsAndCountsTheRest()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var sandbox = new SuiteSandbox();
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        // Thirty steps, each missing the required `type` — one finding per step, comfortably past
        // the ten-per-entry cap however the validator's noise suppression folds them.
        var steps = string.Concat(Enumerable.Range(0, 30).Select(index =>
            $"  - id: step-{index:D2}\n    description: \"A step with no type at all.\"\n"));

        var path = sandbox.WriteFile(
            "many-findings.e2e.yaml",
            "metadata:\n  name: \"Many findings\"\n  owner: \"platform-team\"\n\nsteps:\n" + steps);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = path },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        var totalErrors = payload.GetProperty("validation").GetProperty("errors").GetArrayLength();
        Assert.True(
            totalErrors > Vouchfx.Mcp.Tools.RunSuiteTool.MaxErrorsPerInvalidSuite,
            $"The fixture produced only {totalErrors} findings, so this test would not exercise the cap.");

        var entry = Assert.Single(payload.GetProperty("invalidSuites").EnumerateArray().ToArray());
        Assert.Equal(
            Vouchfx.Mcp.Tools.RunSuiteTool.MaxErrorsPerInvalidSuite,
            entry.GetProperty("errors").GetArrayLength());
        Assert.Equal(
            totalErrors - Vouchfx.Mcp.Tools.RunSuiteTool.MaxErrorsPerInvalidSuite,
            entry.GetProperty("omittedErrorCount").GetInt32());

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

    /// <summary>
    /// vouchfx-mcp#96 ON THE WIRE. The orchestrator tests already prove the classification; what only
    /// a wire test can show is that the engine's sentence survives serialisation and arrives in
    /// <c>structuredContent.remediationHint</c>, which is the field a host actually reads — the whole
    /// point of the issue being that the refusal was invisible to every surface a host has.
    /// </summary>
    [Fact]
    public async Task RunSuite_EngineConfigDiagnosticWithNoScenarioResult_TheHintReachesStructuredContent()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The measured rc.5 shape: exit 4, the refusal on stdout, no events file written at all.
        var runner = FakeSuiteRunner.FailingBeforeAnyEvents(
            exitCode: 4,
            stderrExcerpt: null,
            stdoutDiagnosticExcerpt: Run.EngineDiagnosticExcerptTests.MeasuredRc5RefusalLine);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

        var result = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
            cancellationToken: cts.Token);

        // NOT a tool error: the run happened, reached a verdict, and is reported as a result.
        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        // The taxonomy invariant, asserted where a host sees it (an authoring fault the engine reports
        // as Inconclusive — never Fail, never Pass).
        Assert.Equal("Inconclusive", payload.GetProperty("verdict").GetString());
        Assert.Equal(4, payload.GetProperty("exitCode").GetInt32());
        Assert.False(payload.GetProperty("cancelled").GetBoolean());
        Assert.False(payload.GetProperty("timedOut").GetBoolean());
        Assert.Empty(payload.GetProperty("steps").EnumerateArray());

        var hint = payload.GetProperty("remediationHint").GetString();
        Assert.NotNull(hint);
        Assert.StartsWith(
            "The engine reported an environment configuration error and a suite produced no scenario result: ",
            hint,
            StringComparison.Ordinal);
        Assert.Contains(
            "which the engine sets itself for this dependency type", hint, StringComparison.Ordinal);
        Assert.Contains(
            "declare the backend as a service with 'image:'", hint, StringComparison.Ordinal);

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

        /// <summary>Writes arbitrary text as a suite file and returns its absolute path.</summary>
        public string WriteFile(string fileName, string content)
        {
            var fullPath = Path.Combine(_directory, fileName);
            File.WriteAllText(fullPath, content);
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
