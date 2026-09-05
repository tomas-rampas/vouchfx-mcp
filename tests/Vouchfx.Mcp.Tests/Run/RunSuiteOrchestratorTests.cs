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
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Code == "VFX-E-1002");
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
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Code == "VFX-E-1001");
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
        Assert.Contains(suiteInvalid.Validation.Errors, e => e.Code == "VFX-D-1102");
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

    // ── US-S3-04: cross-process single-flight (spec §4.6, VFX-E-1501) ────────────────────────────

    /// <summary>
    /// AC-003's <c>details.runId</c> at the orchestrator seam: the rejection names the run that is
    /// actually in flight, read back from the shared registry — the linkage that makes
    /// <c>VFX-E-1501</c> actionable rather than merely correct.
    /// </summary>
    [Fact]
    public async Task RunAsync_SecondCallWhileFirstInProgress_NamesTheActiveRunIdFromTheRegistry()
    {
        var gate = new TaskCompletionSource<SuiteProcessResult>();
        var runner = FakeSuiteRunner.Blocking(gate);
        var registry = new InMemoryRunRegistry();
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        var firstTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));

        var secondOutcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var rejected = Assert.IsType<RunSuiteOutcome.AlreadyRunning>(secondOutcome);

        // Not merely "some id": THE id of the entry sitting at `running` because of the very claim
        // this call failed to take.
        var active = Assert.Single(registry.ListRuns(), entry => entry.Status == RunRegistryStatus.Running);
        Assert.Equal(active.RunId, rejected.ActiveRunId);
        Assert.Contains(active.RunId, rejected.Message, StringComparison.Ordinal);

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        Assert.IsType<RunSuiteOutcome.Completed>(await firstTask);
    }

    /// <summary>
    /// AC-002 at its narrowest: the claim is decided by the injected <see cref="IRunLock"/>, not by
    /// this process's own flag — a first call, on an untouched orchestrator, is rejected purely
    /// because another process holds the workspace's lock.
    /// </summary>
    /// <remarks>
    /// <b>The mismatched CLI is the assertion, not scenery.</b> US-S3-04 moved the concurrency gate
    /// AHEAD of REQ-008's handshake so a rejected call never pays for a process spawn it cannot use
    /// (see <see cref="RunSuiteOrchestrator"/>'s ordering remarks). With the old ordering this call
    /// would return <see cref="RunSuiteOutcome.CliUnavailable"/>; the ordering is therefore pinned by
    /// a test that fails loudly if it is ever moved back, rather than left as a comment.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenTheRunLockIsHeldElsewhere_IsRejectedBeforeTheCliHandshake()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(
            runner,
            cli: FakeVouchfxCli.ReportingVersion("9.9.9-not-the-pin"),
            runLock: new StubRunLock(new RunLockResult.HeldByAnotherRun()));

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        Assert.IsType<RunSuiteOutcome.AlreadyRunning>(outcome);
        Assert.Equal(0, runner.InvocationCount);
    }

    /// <summary>
    /// A lock the output directory itself refused is NOT a concurrency answer. Reporting it as
    /// <c>VFX-E-1501</c> (retryable, "wait for the other run") would have a host poll forever against
    /// a directory that will never accept a run; <c>VFX-E-1502</c> is what already means "the run
    /// could not be recorded before it started", which is exactly the condition.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTheRunLockIsUnavailable_ReportsRunNotRecordedRatherThanRunInProgress()
    {
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(
            runner,
            runLock: new StubRunLock(new RunLockResult.Unavailable(new UnauthorizedAccessException("denied"))));

        var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        var notRecorded = Assert.IsType<RunSuiteOutcome.RunNotRecorded>(outcome);

        // The failure's TYPE, never its Message — BCL filesystem exceptions routinely embed a path.
        Assert.Contains(nameof(UnauthorizedAccessException), notRecorded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", notRecorded.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    /// <summary>
    /// AC-001's release leg against the REAL <see cref="WorkspaceRunLock"/>: after an ordinary
    /// completion the file lock is genuinely gone, so an independent instance — standing in for the
    /// next server process — can take it.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithTheRealFileLock_ReleasesItOnCompletion()
    {
        await WithTemporaryOutputDirectoryAsync(async outputDirectory =>
        {
            var runLock = new WorkspaceRunLock(outputDirectory, workspace: null);
            var runner = FakeSuiteRunner.Succeeding([], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0);
            var orchestrator = CreateOrchestrator(runner, runLock: runLock);

            var outcome = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
            Assert.IsType<RunSuiteOutcome.Completed>(outcome);

            AssertLockIsFree(outputDirectory);
        });
    }

    /// <summary>
    /// The same release leg on the CRASH path. This is the case a <c>finally</c> exists for: the
    /// runner throws, the original exception still reaches the caller (asserted by the existing
    /// registry tests), and the workspace must not be left locked by a run that no longer exists.
    /// </summary>
    [Fact]
    public async Task RunAsync_RunnerThrows_StillReleasesTheFileLock()
    {
        await WithTemporaryOutputDirectoryAsync(async outputDirectory =>
        {
            var runLock = new WorkspaceRunLock(outputDirectory, workspace: null);
            var orchestrator = CreateOrchestrator(
                FakeSuiteRunner.Throwing(new InvalidOperationException("runner exploded")), runLock: runLock);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None));

            AssertLockIsFree(outputDirectory);
        });
    }

    /// <summary>
    /// EDGE-002's cancellation path releases the claim too — a cancelled run reaches
    /// <c>Inconclusive</c> through a completely different arm from an ordinary completion, and both
    /// arms funnel through the same <c>finally</c> only if nothing between them returns early.
    /// </summary>
    [Fact]
    public async Task RunAsync_Cancelled_StillReleasesTheFileLock()
    {
        await WithTemporaryOutputDirectoryAsync(async outputDirectory =>
        {
            var runLock = new WorkspaceRunLock(outputDirectory, workspace: null);
            using var cancellation = new CancellationTokenSource();
            var runner = FakeSuiteRunner.ObservingCancellation(TimeSpan.FromMilliseconds(20), () => { });
            var orchestrator = CreateOrchestrator(runner, runLock: runLock);

            var runTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cancellation.Token);
            await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));
            await cancellation.CancelAsync();

            var completed = Assert.IsType<RunSuiteOutcome.Completed>(await runTask);
            Assert.True(completed.Result.Cancelled);

            AssertLockIsFree(outputDirectory);
        });
    }

    /// <summary>
    /// Asserts the workspace's run lock is free by TAKING it — the only test that does not depend on
    /// the platform's residue behaviour (on Unix a hard-killed holder leaves the file behind, so
    /// <c>File.Exists</c> would be the wrong question everywhere).
    /// </summary>
    private static void AssertLockIsFree(string outputDirectory)
    {
        var probe = new WorkspaceRunLock(outputDirectory, workspace: null);
        var acquired = Assert.IsType<RunLockResult.Acquired>(probe.TryAcquire());
        acquired.Release.Dispose();
    }

    private static async Task WithTemporaryOutputDirectoryAsync(Func<string, Task> body)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-orch-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            await body(Path.Combine(sandbox, ".vouchfx", "runs"));
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Temp-directory hygiene only.
            }
        }
    }

    // ── The rejection's `details.runId`, and the release of the in-process flag after one ────────

    /// <summary>
    /// A same-process rejection names the active run from the id THIS process minted, without
    /// scanning the registry at all — asserted by counting <see cref="IRunRegistry.ListRuns"/> calls,
    /// because "it is faster" is not observable and "it did not consult the registry" is.
    /// </summary>
    /// <remarks>
    /// The scan is not merely redundant here, it is strictly worse: it is exposed to all three of the
    /// staleness windows <see cref="RunSuiteOrchestrator"/> documents (a head window before the
    /// holder's own write lands, a tail window after it completes, and a permanent one when the
    /// workspace holds more runs than <see cref="FileRunRegistry.MaxRunsScanned"/>), whereas the
    /// minted id is exact by construction.
    /// </remarks>
    [Fact]
    public async Task RunAsync_SecondCallWhileFirstInProgress_NamesTheMintedRunIdWithoutScanningTheRegistry()
    {
        var gate = new TaskCompletionSource<SuiteProcessResult>();
        var runner = FakeSuiteRunner.Blocking(gate);
        var registry = new ListRunsCountingRunRegistry(new InMemoryRunRegistry());
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        var firstTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));

        var scansBefore = registry.ListRunsCallCount;

        var rejected = Assert.IsType<RunSuiteOutcome.AlreadyRunning>(
            await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None));

        Assert.Equal(scansBefore, registry.ListRunsCallCount);

        // Still the right id, not merely a cheap one.
        var active = Assert.Single(registry.ListRuns(), entry => entry.Status == RunRegistryStatus.Running);
        Assert.Equal(active.RunId, rejected.ActiveRunId);

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        Assert.IsType<RunSuiteOutcome.Completed>(await firstTask);
    }

    /// <summary>
    /// A malformed newest <c>running</c> entry ends the search rather than advancing it: the
    /// rejection reports NO run id, never the id of an older run that is not the one in the way.
    /// </summary>
    /// <remarks>
    /// The older well-formed entry below is the whole test. With a scan that skipped the malformed
    /// head, the caller would be handed <c>run-</c>…<c>aaaa</c> — a real, well-formed, correlatable
    /// id belonging to a DIFFERENT run — and would poll it, wait on it, and eventually act on it.
    /// "Cannot name the active run" is an honest answer; naming the wrong one is not.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenTheNewestRunningEntryHasAMalformedRunId_OmitsTheRunIdRatherThanNamingAnOlderRun()
    {
        var olderWellFormedRunId = "run-" + new string('a', 32);
        var registry = new FixedListingRunRegistry(
        [
            RunningEntry("../../etc/passwd", DateTimeOffset.UtcNow),
            RunningEntry(olderWellFormedRunId, DateTimeOffset.UtcNow.AddMinutes(-5)),
        ]);

        var orchestrator = CreateOrchestrator(
            FakeSuiteRunner.NeverExpectedToRun(),
            runRegistry: registry,
            runLock: new StubRunLock(new RunLockResult.HeldByAnotherRun()));

        var rejected = Assert.IsType<RunSuiteOutcome.AlreadyRunning>(
            await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None));

        Assert.Null(rejected.ActiveRunId);
        Assert.DoesNotContain(olderWellFormedRunId, rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The classic hoisted-return wedge, pinned on BOTH rejection paths: a call refused by the lock
    /// must not leave this process's own single-flight flag set. If it did, the FIRST rejection would
    /// permanently disable the server — every later call would answer <c>AlreadyRunning</c> even with
    /// the lock free and nothing running.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AfterALockRejection_ASubsequentCallIsNotWedgedAtAlreadyRunning(bool unavailable)
    {
        var firstAnswer = unavailable
            ? new RunLockResult.Unavailable(new UnauthorizedAccessException("denied"))
            : (RunLockResult)new RunLockResult.HeldByAnotherRun();

        var runLock = new ScriptedRunLock(firstAnswer);
        var runner = FakeSuiteRunner.Succeeding([], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0);
        var orchestrator = CreateOrchestrator(runner, runLock: runLock);

        var first = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        if (unavailable)
        {
            Assert.IsType<RunSuiteOutcome.RunNotRecorded>(first);
        }
        else
        {
            Assert.IsType<RunSuiteOutcome.AlreadyRunning>(first);
        }

        // The lock now grants the claim. Anything other than a real run here means the rejection
        // above kept the in-process flag (or the cached run id) alive after returning.
        runLock.Next = new RunLockResult.Acquired(new NoOpDisposable());

        var second = await orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        Assert.IsType<RunSuiteOutcome.Completed>(second);
        Assert.Equal(1, runner.InvocationCount);
    }

    private static RunRegistryEntry RunningEntry(string runId, DateTimeOffset startedAt) => new(
        RunId: runId,
        Status: RunRegistryStatus.Running,
        Outcome: null,
        StartedAtUtc: startedAt,
        FinishedAtUtc: null,
        SpecPaths: ["suite.e2e.yaml"],
        EventsFilePath: "events.jsonl",
        Labels: new Dictionary<string, string>());

    /// <summary>
    /// An <see cref="IRunLock"/> that always answers the same way — for the two cases whose SUBJECT
    /// is what the orchestrator does with an answer, not how the real lock arrives at one.
    /// </summary>
    private sealed class StubRunLock(RunLockResult result) : IRunLock
    {
        public RunLockResult TryAcquire() => result;
    }

    /// <summary>
    /// A lock whose answer the test changes between calls — for the "a rejection must not wedge the
    /// next call" cases, which need a refusal followed by a grant.
    /// </summary>
    private sealed class ScriptedRunLock(RunLockResult first) : IRunLock
    {
        public RunLockResult Next { get; set; } = first;

        public RunLockResult TryAcquire() => Next;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Wraps a real registry and counts <see cref="IRunRegistry.ListRuns"/> calls, so a test can
    /// assert the rejection path did not scan.
    /// </summary>
    private sealed class ListRunsCountingRunRegistry(IRunRegistry inner) : IRunRegistry
    {
        private int _listRunsCallCount;

        public int ListRunsCallCount => Volatile.Read(ref _listRunsCallCount);

        public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
            inner.StartRun(specPaths, labels);

        public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
            inner.RecordStatusTransition(runId, status, outcome);

        public RunRegistryEntry? TryGetRun(string runId) => inner.TryGetRun(runId);

        public IReadOnlyList<RunRegistryEntry> ListRuns()
        {
            Interlocked.Increment(ref _listRunsCallCount);
            return inner.ListRuns();
        }
    }

    /// <summary>
    /// A registry whose <see cref="IRunRegistry.ListRuns"/> returns exactly what the test planted —
    /// the only way to present an entry no production writer would ever mint (a malformed run id in a
    /// hand-edited <c>run.json</c>, which the file-backed registry reads off a directory this process
    /// does not exclusively own).
    /// </summary>
    private sealed class FixedListingRunRegistry(IReadOnlyList<RunRegistryEntry> entries) : IRunRegistry
    {
        public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
            throw new NotSupportedException("This registry exists to be listed, never written.");

        public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
            throw new NotSupportedException("This registry exists to be listed, never written.");

        public RunRegistryEntry? TryGetRun(string runId) =>
            entries.FirstOrDefault(entry => string.Equals(entry.RunId, runId, StringComparison.Ordinal));

        public IReadOnlyList<RunRegistryEntry> ListRuns() => entries;
    }

    // ── US-S3-01: what the run REGISTRY ends up holding, on every path a run can take ────────────
    //
    // These assert on the registry rather than on the response, and that is the point. The response's
    // verdict has been covered since REQ-006; what US-S3-01 added is a PERSISTED record that a later
    // explain_run/list_runs projects from, and a taxonomy invariant only asserted on the response is
    // not asserted on the surface that outlives the call.

    [Fact]
    public async Task RunAsync_RunnerThrows_RecordsTheRunCompletedInconclusive_AndRethrowsTheOriginalException()
    {
        var registry = new InMemoryRunRegistry();
        var failure = new InvalidTimeZoneException("the runner blew up in a way nobody anticipated");
        var orchestrator = CreateOrchestrator(FakeSuiteRunner.Throwing(failure), runRegistry: registry);

        // The ORIGINAL exception reaches the caller — not a bookkeeping failure that replaced it, and
        // not a swallowed one. The registry write in the catch arm is wrapped in its own swallowing
        // try/catch precisely so it can never become the exception the caller diagnoses.
        var thrown = await Assert.ThrowsAsync<InvalidTimeZoneException>(
            () => orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None));
        Assert.Same(failure, thrown);

        // And the entry is not left stuck at `running`: an exception escaping the run means it reached
        // NO verdict, which is exactly what Inconclusive means (§12.1) — never Fail, which would
        // assert a defect nobody observed.
        var entry = Assert.Single(registry.ListRuns());
        Assert.Equal(RunRegistryStatus.Completed, entry.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), entry.Outcome);
        Assert.NotNull(entry.FinishedAtUtc);
    }

    [Fact]
    public async Task RunAsync_CallerCancelled_RecordsInconclusiveInTheRegistry_NeverFail()
    {
        var registry = new InMemoryRunRegistry();
        var runner = FakeSuiteRunner.ObservingCancellation(TimeSpan.FromMilliseconds(200), () => { });
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        using var cts = new CancellationTokenSource();
        var runTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, cts.Token);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));
        cts.Cancel();

        Assert.IsType<RunSuiteOutcome.Completed>(await runTask);

        var entry = Assert.Single(registry.ListRuns());
        Assert.Equal(RunRegistryStatus.Completed, entry.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), entry.Outcome);
        Assert.NotEqual(nameof(RunVerdict.Fail), entry.Outcome);
    }

    [Fact]
    public async Task RunAsync_TimeoutExpires_RecordsInconclusiveInTheRegistry_NeverFail()
    {
        var registry = new InMemoryRunRegistry();
        var runner = FakeSuiteRunner.ObservingCancellation(TimeSpan.FromMilliseconds(100), () => { });
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), null, RunSuiteOrchestrator.MinTimeoutSeconds, null, CancellationToken.None);

        Assert.True(Assert.IsType<RunSuiteOutcome.Completed>(outcome).Result.TimedOut);

        // EDGE-002's taxonomy invariant, now asserted where it PERSISTS: a run abandoned on its own
        // timeout budget is Inconclusive in the registry too, so a later explain_run/list_runs cannot
        // report it as a test failure.
        var entry = Assert.Single(registry.ListRuns());
        Assert.Equal(RunRegistryStatus.Completed, entry.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), entry.Outcome);
        Assert.NotEqual(nameof(RunVerdict.Fail), entry.Outcome);
    }

    [Fact]
    public async Task RunAsync_RecordsTheRunAsRunningWhileItIsInFlight_AndCompletedOnceItEnds()
    {
        var registry = new InMemoryRunRegistry();
        var gate = new TaskCompletionSource<SuiteProcessResult>();
        var runner = FakeSuiteRunner.Blocking(gate);
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        var runTask = orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        await WaitUntilAsync(() => runner.InvocationCount == 1, TimeSpan.FromSeconds(15));

        // Write point 1 of 2: the entry exists and says `running` BEFORE the run has finished — which
        // is the whole difference from the retired ILastRunTracker, and what makes a crashed server's
        // attempted run still discoverable. MostRecentFinishedRun must NOT see it yet.
        var inFlight = Assert.Single(registry.ListRuns());
        Assert.Equal(RunRegistryStatus.Running, inFlight.Status);
        Assert.Null(inFlight.Outcome);
        Assert.Null(inFlight.FinishedAtUtc);
        Assert.Null(registry.MostRecentFinishedRun());

        gate.SetResult(new SuiteProcessResult(0, RunTermination.CompletedNormally));
        Assert.IsType<RunSuiteOutcome.Completed>(await runTask);

        // Write point 2 of 2: the SAME entry, transitioned — never a second row.
        var completed = Assert.Single(registry.ListRuns());
        Assert.Equal(inFlight.RunId, completed.RunId);
        Assert.Equal(RunRegistryStatus.Completed, completed.Status);
        Assert.Equal(nameof(RunVerdict.Pass), completed.Outcome);
        Assert.Equal(completed.RunId, registry.MostRecentFinishedRun()?.RunId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_RegistryStorageFailure_ReturnsRunNotRecordedWithoutInvokingRunner(bool diskFull)
    {
        // Both members of the caught family, so neither arm can be dropped unnoticed: a full volume
        // (IOException) and a read-only root (UnauthorizedAccessException) must land on the SAME
        // catalogued outcome.
        var registry = diskFull ? UnwritableRunRegistry.WithDiskFull() : UnwritableRunRegistry.WithAccessDenied();
        var runner = FakeSuiteRunner.NeverExpectedToRun();
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        var outcome = await orchestrator.RunAsync(
            FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);

        // The registry write is run_suite's first disk-touching action, and it happens before the
        // spawn — so a storage failure must end the call as a catalogued error with nothing run, not
        // as a bare framework exception escaping the tool handler.
        var notRecorded = Assert.IsType<RunSuiteOutcome.RunNotRecorded>(outcome);
        Assert.Contains("could not be recorded", notRecorded.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    /// <summary>
    /// M1, the success path: a storage fault in the COMPLETING registry write must not destroy a
    /// verdict the engine already produced.
    /// </summary>
    /// <remarks>
    /// Unguarded, that write rethrew through the outer catch and the caller got an uncoded framework
    /// exception in place of the result — while the entry stayed <c>running</c> regardless, so failing
    /// bought nothing (a peer review's MAJOR finding). This is the opposite asymmetry to
    /// <see cref="RunAsync_RegistryStorageFailure_ReturnsRunNotRecordedWithoutInvokingRunner"/>: a
    /// failed <c>StartRun</c> means NOTHING RAN and the caller is told (VFX-E-1502); a failed
    /// completing write means the run happened and only its record is missing.
    /// </remarks>
    [Fact]
    public async Task RunAsync_CompletingRegistryWriteFails_StillReturnsTheVerdictTheRunProduced()
    {
        var registry = UnwritableRunRegistry.FailingOnTransitionOnly();
        var runner = FakeSuiteRunner.Succeeding([], """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""", 0);
        var orchestrator = CreateOrchestrator(runner, runRegistry: registry);

        using var stdout = new ConsoleOutCapture();
        var originalError = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);

        RunSuiteOutcome outcome;
        try
        {
            outcome = await orchestrator.RunAsync(
                FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // The verdict survives the bookkeeping failure — that is the whole fix.
        Assert.Equal(nameof(RunVerdict.Pass), Assert.IsType<RunSuiteOutcome.Completed>(outcome).Result.Verdict);

        // Anti-vacuity: the guarded write really was attempted and really did throw.
        Assert.Equal(1, registry.TransitionAttemptCount);
        Assert.Equal(RunRegistryStatus.Running, Assert.Single(registry.ListRuns()).Status);

        // Not silent, and not on the JSON-RPC channel. The message names the run id and the exception
        // TYPE only — never its Message, which for a BCL filesystem exception routinely embeds a path.
        Assert.Contains(nameof(IOException), stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("registry", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not enough space", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, stdout.Writer.ToString());
    }

    /// <summary>
    /// M1, the failure path: when the run itself threw AND the bookkeeping write then failed too, the
    /// caller must still receive the ORIGINAL exception — never the storage one that replaced it.
    /// </summary>
    [Fact]
    public async Task RunAsync_RunnerThrowsAndTheRegistryWriteAlsoFails_RethrowsTheRunnersOwnException()
    {
        var registry = UnwritableRunRegistry.FailingOnTransitionOnly();
        var failure = new InvalidTimeZoneException("the runner blew up in a way nobody anticipated");
        var orchestrator = CreateOrchestrator(FakeSuiteRunner.Throwing(failure), runRegistry: registry);

        var thrown = await Assert.ThrowsAsync<InvalidTimeZoneException>(
            () => orchestrator.RunAsync(FixturePath("good-suite.e2e.yaml"), null, null, null, CancellationToken.None));

        // Same instance, not merely the same type: the bookkeeping IOException must not have become
        // the thing the caller diagnoses.
        Assert.Same(failure, thrown);
        Assert.Equal(1, registry.TransitionAttemptCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static RunSuiteOrchestrator CreateOrchestrator(
        ISuiteRunner runner,
        IVouchfxCli? cli = null,
        IRunRegistry? runRegistry = null,
        Workspace? workspace = null,
        IRunLock? runLock = null) =>
        new(
            new CliPinVerifier(cli ?? FakeVouchfxCli.ReportingVersion("1.0.0-alpha.9"), Pin),
            runner,
            runRegistry ?? new InMemoryRunRegistry(),
            workspace,
            runLock);

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
