using System.Diagnostics;
using System.Text;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// Covers <see cref="RunSuiteOrchestrator"/> — REQ-006's full gate ordering, EDGE-001's
/// environment-error classification, and EDGE-002's cancellation/timeout handling — entirely
/// against a <see cref="FakeSuiteRunner"/>, so nothing here depends on the real <c>vouchfx</c> CLI
/// or Docker being installed.
/// </summary>
/// <remarks>
/// EDGE-003's own pre-validation gate (<see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>)
/// is NOT faked here: it is the SAME isolated, non-Docker, non-CLI worker process
/// <c>ValidationWorkerClientTests</c> already spawns directly and every <c>Real*McpTests</c> class
/// spawns transitively through <c>validate_suite</c> — an already-accepted process boundary in this
/// codebase's own test suite, distinct from "the real vouchfx CLI or Docker" the spec's "no real
/// CLI/Docker in tests" constraint refers to.
/// </remarks>
public class RunSuiteOrchestratorTests
{
    // The VALUE here is arbitrary and deliberately NOT kept in step with the repo's real
    // ENGINE_PIN — these tests never touch the real ENGINE_PIN file at
    // all. All that matters is that CreateOrchestrator's FakeVouchfxCli reports THIS SAME version
    // (see its default "1.0.0-alpha.9" argument below), so CliPinVerifier's exact-match handshake
    // returns Ok and the tests genuinely reach the runner, rather than short-circuiting into
    // CliUnavailable before ever getting there.
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    // ── Argument safety ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-rf")]
    [InlineData("--danger")]
    [InlineData("-C:/anything")]
    public async Task RunAsync_PathBeginningWithDash_ReturnsInvalidArgumentWithoutInvokingRunner(string path)
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(path, tags: null, timeoutSeconds: null, onProgress: null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("begin with", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    [InlineData(int.MinValue)]
    public async Task RunAsync_TimeoutSecondsOutOfRange_ReturnsInvalidArgumentWithoutInvokingRunner(int timeoutSeconds)
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), tags: null, timeoutSeconds, onProgress: null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("timeoutSeconds", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    // ── Argument safety: tags (MAJOR review fix — flag injection / null-element / size limits) ───

    [Theory]
    [InlineData("-smoke")]
    [InlineData("--no-telemetry")]
    public async Task RunAsync_TagBeginningWithDash_ReturnsInvalidArgumentWithoutInvokingRunner(string tag)
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), [tag], null, null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("begin with", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_NullTagElement_ReturnsInvalidArgumentWithoutThrowingOrInvokingRunner()
    {
        // A malformed MCP payload can legally place a JSON null inside a string array at runtime,
        // regardless of this server's own compile-time nullable-reference-type annotations — the
        // null-forgiving '!' below models exactly that: a genuine runtime null, not a type-checked one.
        IReadOnlyList<string> tagsWithNull = ["smoke", null!];
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), tagsWithNull, null, null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("null", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_EmptyOrWhitespaceTag_ReturnsInvalidArgumentWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), ["   "], null, null, CancellationToken.None);

        Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_TooManyTags_ReturnsInvalidArgumentWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);
        var tooManyTags = Enumerable.Range(0, RunSuiteOrchestrator.MaxTagCount + 1).Select(i => $"tag{i}").ToArray();

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), tooManyTags, null, null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("Too many tags", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_TagTooLong_ReturnsInvalidArgumentWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);
        var tooLongTag = new string('a', RunSuiteOrchestrator.MaxTagLength + 1);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), [tooLongTag], null, null, CancellationToken.None);

        var invalid = Assert.IsType<RunSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("exceeds", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_ValidTags_AreAcceptedAndRunnerIsInvoked()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 0);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), ["smoke", "nightly"], null, null, CancellationToken.None);

        Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal(1, runner.InvocationCount);
    }

    // ── EDGE-003: pre-validation, four distinct structured errors, runner never invoked ─────────

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsSuiteInvalidWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("does-not-exist.e2e.yaml"), null, null, null, CancellationToken.None);

        var suiteInvalid = Assert.IsType<RunSuiteOutcome.SuiteInvalid>(outcome);
        Assert.False(suiteInvalid.Validation.Valid);
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Kind == "file-not-found");
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_UncPath_ReturnsSuiteInvalidWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            @"\\attacker-host\share\suite.e2e.yaml", null, null, null, CancellationToken.None);

        var suiteInvalid = Assert.IsType<RunSuiteOutcome.SuiteInvalid>(outcome);
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Kind == "invalid-path");
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_UnparseableYaml_ReturnsSuiteInvalidWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("malformed.e2e.yaml"), null, null, null, CancellationToken.None);

        var suiteInvalid = Assert.IsType<RunSuiteOutcome.SuiteInvalid>(outcome);
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Kind == "yaml-parse");
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunAsync_SchemaInvalid_ReturnsSuiteInvalidWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("bad-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var suiteInvalid = Assert.IsType<RunSuiteOutcome.SuiteInvalid>(outcome);
        Assert.False(suiteInvalid.Validation.Valid);
        Assert.NotEmpty(suiteInvalid.Validation.Errors);
        Assert.Equal(0, runner.InvocationCount);
    }

    // ── REQ-008: CLI gate, runner never invoked when it fails ──────────────────────────────────

    [Fact]
    public async Task RunAsync_CliNotFound_ReturnsCliUnavailableWithoutInvokingRunner()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner, FakeVouchfxCli.NotFound());

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var cliUnavailable = Assert.IsType<RunSuiteOutcome.CliUnavailable>(outcome);
        Assert.Contains("PATH", cliUnavailable.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    // ── The four verdicts stay distinct ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PassVerdict_ReturnsPassWithPopulatedStepsAndProgressNotifications()
    {
        const string events = """
            {"type":"step-started","stepId":"check-health"}
            {"type":"step-attempt","stepId":"check-health","attempt":1}
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":142}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var runner = FakeSuiteRunner.Succeeding(["Starting DCP..."], events, exitCode: 0);
        var orchestrator = CreateOrchestrator(runner);
        var narrations = new List<string>();

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), null, null, narrations.Add, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Pass", completed.Result.Verdict);
        Assert.Equal(0, completed.Result.ExitCode);
        Assert.Null(completed.Result.RemediationHint);
        var step = Assert.Single(completed.Result.Steps);
        Assert.Equal("check-health", step.StepId);
        Assert.Equal("Pass", step.Verdict);
        Assert.Equal(142, step.DurationMs);
        Assert.Equal(1, step.AttemptCount);

        // At least one progress notification was emitted — never asserting exact count/order (MCP
        // progress delivery is best-effort), but this server's OWN callback invocation is
        // synchronous and ordered, so both the relayed child line and the post-hoc narration burst
        // are expected to be present.
        Assert.NotEmpty(narrations);
        Assert.Contains(narrations, n => n.Contains("Starting DCP", StringComparison.Ordinal));
        Assert.Contains(narrations, n => n.Contains("check-health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_FailVerdict_ReturnsFail()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 1);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Fail", completed.Result.Verdict);
        Assert.Null(completed.Result.RemediationHint);
    }

    [Fact]
    public async Task RunAsync_InconclusiveVerdict_ReturnsInconclusive()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}""";
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 4);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.False(completed.Result.Cancelled);
        Assert.False(completed.Result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_EnvironmentErrorEvent_ReturnsEnvironmentErrorWithRemediationHintNeverFail()
    {
        const string events = """
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-db","detail":"pull access denied","verdict":"ENV_ERROR"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;
        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 3);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("EnvironmentError", completed.Result.Verdict);
        Assert.NotEqual("Fail", completed.Result.Verdict);
        Assert.NotNull(completed.Result.RemediationHint);
        Assert.Contains("orders-db", completed.Result.RemediationHint, StringComparison.Ordinal);
    }

    // ── EDGE-001: early crash with no events at all, classified by exit code, never Fail ────────

    [Fact]
    public async Task RunAsync_FailsBeforeAnyEvents_DockerExitCode_ClassifiesAsEnvironmentErrorWithDockerHint()
    {
        var runner = FakeSuiteRunner.FailingBeforeAnyEvents(
            exitCode: 3, stderrExcerpt: "Cannot connect to the Docker daemon at unix:///var/run/docker.sock");
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("EnvironmentError", completed.Result.Verdict);
        Assert.NotNull(completed.Result.RemediationHint);
        Assert.Contains("Docker", completed.Result.RemediationHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_FailsBeforeAnyEvents_UnexpectedExitCode_ClassifiesAsEnvironmentErrorNeverFail()
    {
        // A code the CLI's own documented taxonomy never produces for a real Fail (1) — an
        // unhandled crash, or a usage error before scenario execution could even begin.
        var runner = FakeSuiteRunner.FailingBeforeAnyEvents(exitCode: 134);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("EnvironmentError", completed.Result.Verdict);
    }

    [Fact]
    public async Task RunAsync_FailsBeforeAnyEvents_ExitCode0_ClassifiesAsPass()
    {
        var runner = FakeSuiteRunner.FailingBeforeAnyEvents(exitCode: 0);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Pass", completed.Result.Verdict);
        Assert.Empty(completed.Result.Steps);
    }

    // ── EDGE-002: cancellation and timeout ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CallerCancelled_StopsTheRunnerAndReturnsInconclusiveCancelled()
    {
        var stopRequested = false;
        var runner = FakeSuiteRunner.ObservingCancellation(TimeSpan.FromMilliseconds(200), () => stopRequested = true);
        var orchestrator = CreateOrchestrator(runner);

        using var cts = new CancellationTokenSource();
        var runTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cts.Token);

        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));
        cts.Cancel();

        var outcome = await runTask;

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.True(completed.Result.Cancelled);
        Assert.False(completed.Result.TimedOut);
        Assert.Empty(completed.Result.Steps);
        Assert.True(stopRequested, "Expected the runner to observe the cancellation request.");
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_StopsTheRunnerAndReturnsInconclusiveTimedOut()
    {
        var runner = FakeSuiteRunner.ObservingCancellation(TimeSpan.FromMilliseconds(100), () => { });
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), null, RunSuiteOrchestrator.MinTimeoutSeconds, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.False(completed.Result.Cancelled);
        Assert.True(completed.Result.TimedOut);
        Assert.NotNull(completed.Result.RemediationHint);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringEventsFileRead_ReturnsInconclusiveNotACrash()
    {
        // A review fix: BuildCompletedOutcomeAsync now threads the caller's token through to
        // EventsFileReader.TryReadBoundedAsync, and EventsFileReader itself was earlier fixed to
        // let a genuine OperationCanceledException propagate rather than silently degrading it to
        // "could not be read". This proves the two fixes compose correctly: a cancellation that
        // lands AFTER the suite run itself already completed normally (during the subsequent
        // events-file read) still resolves to the SAME structured Inconclusive outcome EDGE-002
        // already uses for a cancellation DURING the run — never an unhandled exception.
        using var cts = new CancellationTokenSource();
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";
        var innerRunner = FakeSuiteRunner.Succeeding([], events, exitCode: 0);
        var runner = new CancellingAfterCompletionSuiteRunner(innerRunner, cts);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cts.Token);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.True(completed.Result.Cancelled);
        Assert.False(completed.Result.TimedOut);
        Assert.Equal(1, innerRunner.InvocationCount);
    }

    // ── BLOCKER regression: a relay that never reaches EOF must never wedge the single-flight gate ──

    [Fact]
    public async Task RunAsync_RunnerWithNeverCompletingRelay_StillReturnsPromptlyAndReleasesTheGate()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";
        var runner = FakeSuiteRunner.WithNeverCompletingRelay(events, exitCode: 0);
        var orchestrator = CreateOrchestrator(runner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cts.Token);

        Assert.IsType<RunSuiteOutcome.Completed>(outcome);

        // The load-bearing assertion: the single-flight gate was actually released (the `finally`
        // in RunAsync ran). Before the fix, a hung relay would have hung ExecuteRunAsync itself,
        // so this second call — reusing the SAME orchestrator instance — would never even have
        // been reached; if the BLOCKER regressed, it would report AlreadyRunning forever instead.
        var secondOutcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cts.Token);
        Assert.IsType<RunSuiteOutcome.Completed>(secondOutcome);
    }

    // ── todo 17: the single-flight gate releases after the REAL graceful-stop-then-force-kill
    //    sequence, driven end to end through a REAL spawned child process (not a scripted fake) ────

    [Fact]
    public async Task RunAsync_RealGracefulFixtureCancelled_ReleasesGateAndSubsequentRunSucceeds()
    {
        // The fixture stops COOPERATIVELY (well inside the 5-second grace) once its stdin is
        // closed — proving the orchestrator's gate releases after the GRACEFUL path, driven
        // through VouchfxCliSuiteRunner.RunAgainstProcessAsync exactly as production does, rather
        // than a fake that merely simulates the timing.
        var runner = new RealFixtureProcessSuiteRunner("graceful", TimeSpan.FromSeconds(5), "200");
        var orchestrator = CreateOrchestrator(runner);

        // Cancellation fires only once the RUNNER has actually started (mirrors
        // RunAsync_CallerCancelled_StopsTheRunnerAndReturnsInconclusiveCancelled's own pattern) —
        // NOT a short fixed delay, which would race the validation-worker/CLI-pin gates that run
        // BEFORE the runner is ever reached and could fire mid-validation instead.
        using var firstCts = new CancellationTokenSource();
        var firstTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, firstCts.Token);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));
        firstCts.Cancel();

        var outcome = await firstTask;
        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.True(completed.Result.Cancelled);

        // The load-bearing assertion: the single-flight gate was actually released. A SECOND call
        // on the SAME orchestrator instance must be allowed to actually attempt a run (never
        // AlreadyRunning) — if the real graceful-stop sequence ever wedged the `finally` in
        // RunSuiteOrchestrator.RunAsync, this call would report AlreadyRunning instead.
        using var secondCts = new CancellationTokenSource();
        var secondTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, secondCts.Token);
        await WaitUntilAsync(() => runner.InvocationCount == 2, TimeSpan.FromSeconds(15));
        secondCts.Cancel();

        var secondOutcome = await secondTask;
        Assert.False(
            secondOutcome is RunSuiteOutcome.AlreadyRunning,
            "Expected the single-flight gate to have been released after the real graceful-stop path.");
        Assert.IsType<RunSuiteOutcome.Completed>(secondOutcome);
    }

    [Fact]
    public async Task RunAsync_RealIgnoringFixtureCancelled_ForceKillFallbackStillReleasesGate()
    {
        // The fixture NEVER reads stdin and never exits cooperatively — the only way it stops is
        // VouchfxCliSuiteRunner's force-kill fallback, exercised here with a short injected grace
        // so the test stays fast. Proves the gate releases after the FORCE-KILL path too.
        var runner = new RealFixtureProcessSuiteRunner("ignore", TimeSpan.FromMilliseconds(500));
        var orchestrator = CreateOrchestrator(runner);

        using var firstCts = new CancellationTokenSource();
        var firstTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, firstCts.Token);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));
        firstCts.Cancel();

        var outcome = await firstTask;
        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.Equal("Inconclusive", completed.Result.Verdict);
        Assert.True(completed.Result.Cancelled);

        // Gate released after the FORCE-KILL fallback path specifically: a second call on the SAME
        // orchestrator instance is allowed to actually attempt a run (never AlreadyRunning) —
        // itself force-killed again, since this runner always spawns an "ignore" fixture.
        using var secondCts = new CancellationTokenSource();
        var secondTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, secondCts.Token);
        await WaitUntilAsync(() => runner.InvocationCount == 2, TimeSpan.FromSeconds(15));
        secondCts.Cancel();

        var secondOutcome = await secondTask;
        Assert.False(
            secondOutcome is RunSuiteOutcome.AlreadyRunning,
            "Expected the single-flight gate to have been released after the real force-kill fallback path.");
        Assert.IsType<RunSuiteOutcome.Completed>(secondOutcome);
    }

    // ── MAJOR review fix: the events file read is bounded, never unbounded ───────────────────────

    [Fact]
    public async Task RunAsync_EventsFileExceedsCap_IsTruncatedAndStillReturnsAResult()
    {
        // Filler content strictly larger than MaxEventsFileBytes, followed by a genuine
        // scenario-completed line that lands AFTER the cap and must therefore never actually be
        // parsed — proving truncation genuinely discards what is past the cap, not merely labels
        // the result while still reading everything.
        const int fillerLineLength = 1024;
        var fillerLine = new string('a', fillerLineLength) + "\n";
        var linesNeeded = (int)(EventsFileReader.MaxEventsFileBytes / fillerLine.Length) + 10;

        var eventsBuilder = new StringBuilder(linesNeeded * fillerLine.Length + 256);
        for (var i = 0; i < linesNeeded; i++)
        {
            eventsBuilder.Append(fillerLine);
        }

        eventsBuilder.Append("""{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""");
        var events = eventsBuilder.ToString();

        var runner = FakeSuiteRunner.Succeeding([], events, exitCode: 0);
        var orchestrator = CreateOrchestrator(runner);

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var completed = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
        Assert.True(completed.Result.EventsTruncated);

        // With the trailing scenario-completed line never actually reached, no scenario-completed
        // event was parsed at all, so the result falls back to the exit-code classifier (Pass, for
        // exitCode 0) rather than reflecting the (unreachable) trailing PASS event.
        Assert.Equal("Pass", completed.Result.Verdict);
        Assert.Empty(completed.Result.Steps);
    }

    // ── Single-flight concurrency ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SecondCallWhileFirstInProgress_ReturnsAlreadyRunningAndRunnerInvokedOnlyOnce()
    {
        var gate = new TaskCompletionSource<SuiteProcessResult>();
        var runner = FakeSuiteRunner.Blocking(gate);
        var orchestrator = CreateOrchestrator(runner);

        var firstTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));

        var secondOutcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        Assert.IsType<RunSuiteOutcome.AlreadyRunning>(secondOutcome);
        Assert.Equal(1, runner.InvocationCount);

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        var firstOutcome = await firstTask;
        Assert.IsType<RunSuiteOutcome.Completed>(firstOutcome);

        // The gate is released once the first call finishes — a THIRD call must now succeed again,
        // proving the concurrency flag was actually cleared rather than left permanently stuck.
        var thirdRunner = FakeSuiteRunner.Succeeding([], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0);
        var thirdOrchestrator = CreateOrchestrator(thirdRunner);
        var thirdOutcome = await thirdOrchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        Assert.IsType<RunSuiteOutcome.Completed>(thirdOutcome);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static RunSuiteOrchestrator CreateOrchestrator(
        ISuiteRunner runner, IVouchfxCli? cli = null, ILastRunTracker? lastRunTracker = null) =>
        new(new CliPinVerifier(cli ?? FakeVouchfxCli.ReportingVersion("1.0.0-alpha.9"), Pin), runner, lastRunTracker ?? new LastRunTracker());

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

    /// <summary>
    /// Wraps another <see cref="ISuiteRunner"/> and cancels <paramref name="toCancel"/> right AFTER
    /// the inner call completes — models a cancellation landing between "the suite run finished"
    /// and "the events file has been read", exactly the window
    /// <see cref="RunAsync_CancelledDuringEventsFileRead_ReturnsInconclusiveNotACrash"/> exercises.
    /// </summary>
    private sealed class CancellingAfterCompletionSuiteRunner(ISuiteRunner inner, CancellationTokenSource toCancel) : ISuiteRunner
    {
        public async Task<SuiteProcessResult> RunAsync(SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(spec, onOutputLine, cancellationToken);
            toCancel.Cancel();
            return result;
        }
    }

    /// <summary>
    /// An <see cref="ISuiteRunner"/> that spawns a REAL <c>Vouchfx.Mcp.Tests.StdinEofChildFixture</c>
    /// child process (todo 17's test fixture — see <c>VouchfxCliSuiteRunnerTests</c>) and drives it
    /// through the EXACT same <see cref="VouchfxCliSuiteRunner.RunAgainstProcessAsync"/> production
    /// code <see cref="VouchfxCliSuiteRunner.RunAsync"/> itself delegates to, rather than a scripted
    /// fake that only simulates the timing. Lets
    /// <see cref="RunAsync_RealGracefulFixtureCancelled_ReleasesGateAndSubsequentRunSucceeds"/> and
    /// <see cref="RunAsync_RealIgnoringFixtureCancelled_ForceKillFallbackStillReleasesGate"/> prove
    /// <see cref="RunSuiteOrchestrator"/>'s single-flight gate genuinely releases after BOTH the real
    /// graceful-stop path and the real force-kill fallback, end to end through the actual gate — not
    /// merely through <see cref="FakeSuiteRunner"/>'s scripted timing (which the existing BLOCKER
    /// regression test already covers generically, for any well-behaved runner).
    /// </summary>
    private sealed class RealFixtureProcessSuiteRunner(string behaviour, TimeSpan gracePeriod, string? fixtureArg = null) : ISuiteRunner
    {
        private int _invocationCount;

        /// <summary>
        /// How many times <see cref="RunAsync"/> has actually started (i.e. the fixture process was
        /// spawned) — lets a test wait for the runner to genuinely be reached before cancelling,
        /// exactly like <see cref="FakeSuiteRunner.InvocationCount"/>, rather than racing a fixed
        /// delay against the validation-worker/CLI-pin gates that run BEFORE the runner ever is.
        /// </summary>
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<SuiteProcessResult> RunAsync(SuiteRunSpec spec, Action<string> onOutputLine, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(RepoLayout.ResolveStdinEofChildFixtureDllPath());
            startInfo.ArgumentList.Add(behaviour);
            if (fixtureArg is not null)
            {
                startInfo.ArgumentList.Add(fixtureArg);
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the stdin-EOF child fixture process.");
            Interlocked.Increment(ref _invocationCount);

            return VouchfxCliSuiteRunner.RunAgainstProcessAsync(process, onOutputLine, gracePeriod, cancellationToken);
        }
    }
}
