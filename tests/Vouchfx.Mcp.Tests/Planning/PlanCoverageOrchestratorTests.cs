using Vouchfx.Mcp;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Planning;

namespace Vouchfx.Mcp.Tests.Planning;

/// <summary>
/// Covers <see cref="PlanCoverageOrchestrator"/> (Spec D / M3 Planner, REQ-012) against
/// <see cref="FakeVouchfxCli"/> — no real CLI required. The engine release carrying <c>vouchfx
/// plan</c> (v1.0.0-rc.3) IS published, and ENGINE_PIN is at or beyond it, but every test here
/// is still CLI-independent by construction — this repo's own tests never depend on a real CLI
/// being installed on the machine running them, mirroring how <c>ScaffoldSuiteOrchestratorTests</c>
/// covers Spec B the same way. <see cref="RealPlanCoverageAgainstPinnedCliTests"/> is the one place
/// that deliberately DOES exercise the real, installed CLI when one matching ENGINE_PIN is present.
/// </summary>
public class PlanCoverageOrchestratorTests
{
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    private const string SampleReportJson = """
        {
          "schemaVersion": 1,
          "engineVersion": "1.0.0-test",
          "thresholds": { "staleDays": 30, "flakyMinRuns": 2, "fragileMinEnvErrors": 2, "inconclusiveMin": 2 },
          "inventory": {
            "suites": [ { "path": "checkout.e2e.yaml", "scenarioId": "checkout-flow", "name": "checkout-flow", "stepCount": 2 } ],
            "services": [ "api" ],
            "dependencies": [ { "name": "orders-db", "type": "postgres", "suite": "checkout.e2e.yaml" } ],
            "stepTypes": [ "db-assert.postgres", "http.rest" ],
            "runCount": 1,
            "firstEventTs": "2026-01-01T00:00:00+00:00",
            "lastEventTs": "2026-01-01T00:05:00+00:00",
            "skippedEventLines": 0,
            "unmatchedObservations": 0,
            "unanalysableSuites": [],
            "unmappableDependencies": []
          },
          "findings": [
            {
              "kind": "dependency-missing-step-type",
              "suite": "checkout.e2e.yaml",
              "stepId": null,
              "target": "orders-db",
              "targetKind": "dependency",
              "suggestedTypes": ["db-assert.postgres"],
              "suggestedStepId": "assert-orders-db",
              "ambiguous": false,
              "ambiguityReason": null,
              "history": null,
              "detail": "Dependency 'orders-db' (postgres) has no analysed step of a candidate asserting type.",
              "relatedSuites": []
            }
          ]
        }
        """;

    // ── Happy path ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_ValidSuite_ReturnsCompletedReportWithFindings()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty)));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var completed = Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.Equal(1, completed.Result.SchemaVersion);
        Assert.Equal("1.0.0-test", completed.Result.EngineVersion);
        Assert.Equal(30, completed.Result.Thresholds.StaleDays);
        Assert.Single(completed.Result.Inventory.Suites);
        Assert.Equal("checkout.e2e.yaml", completed.Result.Inventory.Suites[0].Path);
        var finding = Assert.Single(completed.Result.Findings);
        Assert.Equal("dependency-missing-step-type", finding.Kind);
        Assert.Equal("db-assert.postgres", Assert.Single(finding.SuggestedTypes));
        Assert.Equal("assert-orders-db", finding.SuggestedStepId);
        // 2, not 1: CliPinVerifier's own handshake calls TryGetVersionOutputAsync once, then the
        // orchestrator's own `plan` invocation calls RunAsync once more.
        Assert.Equal(2, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_NeverPassesFailOnGap_AndAlwaysPassesJson()
    {
        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", "events/", 10, 3, 4, 5);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.NotNull(capturedArguments);
        Assert.Equal("plan", capturedArguments![0]);
        Assert.Equal("suites/", capturedArguments[1]);
        Assert.Contains("--json", capturedArguments);
        Assert.DoesNotContain("--fail-on-gap", capturedArguments);
        Assert.DoesNotContain("--output", capturedArguments);
        Assert.Contains("--events", capturedArguments);
        Assert.Contains("events/", capturedArguments);
        Assert.Contains("--stale-days", capturedArguments);
        Assert.Contains("10", capturedArguments);
        Assert.Contains("--flaky-min-runs", capturedArguments);
        Assert.Contains("3", capturedArguments);
        Assert.Contains("--fragile-min-env-errors", capturedArguments);
        Assert.Contains("4", capturedArguments);
        Assert.Contains("--inconclusive-min", capturedArguments);
        Assert.Contains("5", capturedArguments);
    }

    [Fact]
    public async Task PlanAsync_NoEventsOrThresholdOverrides_OmitsOptionalArguments()
    {
        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.Equal(["plan", "suites/", "--json"], capturedArguments);
    }

    // ── Argument safety ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-rf")]
    [InlineData("--danger")]
    [InlineData("-C:/anything")]
    public async Task PlanAsync_PathBeginningWithDash_ReturnsInvalidArgumentWithoutInvokingCli(string path)
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync(path, null, null, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("begin with", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_EventsPathBeginningWithDash_ReturnsInvalidArgumentWithoutInvokingCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", "--rm", null, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("eventsPath", invalid.Message, StringComparison.Ordinal);
        Assert.Contains("begin with", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_EmptyOrWhitespacePath_ReturnsInvalidArgumentWithoutInvokingCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("   ", null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Equal(0, cli.CallCount);
    }

    // ── Threshold validation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_NegativeStaleDays_ReturnsInvalidArgumentWithoutInvokingCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, -1, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("staleDays", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task PlanAsync_FlakyMinRunsBelowOne_ReturnsInvalidArgumentWithoutInvokingCli(int flakyMinRuns)
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, flakyMinRuns, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("flakyMinRuns", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_FragileMinEnvErrorsBelowOne_ReturnsInvalidArgumentWithoutInvokingCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, 0, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("fragileMinEnvErrors", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_InconclusiveMinBelowOne_ReturnsInvalidArgumentWithoutInvokingCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, 0);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("inconclusiveMin", invalid.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    // ── CLI availability ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_CliNotFound_ReturnsCliUnavailable()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.NotFound());

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var unavailable = Assert.IsType<PlanCoverageOutcome.CliUnavailable>(outcome);
        Assert.Contains("not found", unavailable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_CliVersionMismatch_ReturnsCliUnavailable()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.ReportingVersion("1.0.0-alpha.1"));

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var unavailable = Assert.IsType<PlanCoverageOutcome.CliUnavailable>(outcome);
        Assert.Contains(Pin.Version, unavailable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_PlanNotSupported_ReturnsCliUnavailable()
    {
        // Pin-ok CLI that does not implement `plan` (maps to CliInvocationResult.NotLaunched, i.e.
        // FailureReason == NotFound, for unknown commands) — a defensive case, not the shape of the
        // currently-pinned ENGINE_PIN (the pinned engine DOES implement the M3 Planner — shipped in
        // v1.0.0-rc.3 and present in every release since): a hypothetical future CLI whose --version
        // matches the pin but whose binary is otherwise broken/incomplete must still be reported as
        // CliUnavailable, never mistaken for a TimedOut/OutputCapExceeded engine-side failure.
        var cli = FakeVouchfxCli.ReportingVersion(CliVersionNormaliser.Normalise(Pin.Version));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var unavailable = Assert.IsType<PlanCoverageOutcome.CliUnavailable>(outcome);
        Assert.Contains("plan", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet tool install", unavailable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_CliTimedOut_ReturnsPlanFailedNamingTheBudgetNotInstall()
    {
        // MAJOR review fix: a `plan` invocation that legitimately overran its own (longer) budget
        // must NOT tell the user to install/update a CLI that is present, pinned, and working — see
        // CliLaunchFailureReason's own remarks.
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.TimedOut);
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var failed = Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
        Assert.Contains("60-second", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Narrow", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("install", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_CliOutputCapExceeded_ReturnsPlanFailedNamingTheCapNotInstall()
    {
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.OutputCapExceeded);
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var failed = Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
        Assert.Contains("4 MB", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Narrow", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("install", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_PassesPlanTimeoutExplicitlyRatherThanTheSharedDefault()
    {
        // Compile-/behaviour-level guard that PlanCoverageOrchestrator actually threads
        // VouchfxCliProcessRunner.PlanTimeout through to IVouchfxCli.RunAsync's own `timeout`
        // parameter, rather than silently falling back to the shared 15-second DefaultTimeout a
        // large suite/history analysis could plausibly exceed.
        TimeSpan? capturedTimeout = null;
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty));
        var capturingCli = new TimeoutCapturingCli(cli, t => capturedTimeout = t);
        var orchestrator = CreateOrchestrator(capturingCli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.Equal(VouchfxCliProcessRunner.PlanTimeout, capturedTimeout);
    }

    // ── Engine-reported failures ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_UsageErrorExitCode_ReturnsInvalidArgumentNamingThePath()
    {
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(
                2, string.Empty, "Suite path 'does-not-exist/' does not exist."));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("does-not-exist/", null, null, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("does-not-exist", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_EmptySuiteFolderUsageError_ReturnsInvalidArgument()
    {
        // EDGE-009: an empty suite folder is a usage error (exit 2), not an empty successful report.
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(
                2, string.Empty, "No *.e2e.yaml suites were discovered under 'empty-folder/'."));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("empty-folder/", null, null, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Contains("empty-folder", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_CatalogueMetadataExitCode_ReturnsPlanFailed()
    {
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(
                3, string.Empty, "Registered provider 'x.y' lacks a schema fragment (catalogue metadata incomplete)."));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var failed = Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
        Assert.Contains("catalogue", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_EmptyStdoutOnExitZero_ReturnsPlanFailed()
    {
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, string.Empty, string.Empty));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var failed = Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
        Assert.Contains("empty stdout", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_MalformedJsonOnExitZero_ReturnsPlanFailed()
    {
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, "{ this is not valid json", string.Empty));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var failed = Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
        Assert.Contains("could not be parsed", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_UnexpectedNonZeroExitCode_ReturnsPlanFailed()
    {
        // Defensive: exit 5 (--fail-on-gap's own code) is unreachable in production since this
        // orchestrator never passes that flag (see PlanCoverageOrchestrator's remarks), but a
        // future engine surprise must still be handled as a failure, never mistaken for success.
        var cli = FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(5, string.Empty, "unexpected"));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.PlanFailed>(outcome);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanAsync_InvalidPathWithLiveCancellationToken_ReturnsInvalidArgumentSynchronouslyWithoutInvokingCli()
    {
        // MINOR review fix: renamed from "...WithCancellationRequested_StillReturnsPromptly..." —
        // that name implied cancellation actually fired during this test, but the 5-second
        // CancelAfter below never elapses (the test returns in microseconds via the leading-'-'
        // guard in PlanCoverageOrchestrator.PlanAsync), so nothing was ever cancelled. What this
        // DOES prove: REQ-012's "an invalid suite path returns a structured tool error, not a hang"
        // holds even with a live (not-yet-cancelled) token in hand — local argument-safety
        // validation runs before any async CLI call, so this resolves synchronously regardless of
        // the token's state. See PlanAsync_CancelledDuringCliInvocation_PropagatesOperationCanceledException
        // below for a token that is actually cancelled mid-call.
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var outcome = await orchestrator.PlanAsync("-rf", null, null, null, null, null, cts.Token);

        Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_CancelledDuringCliInvocation_PropagatesOperationCanceledException()
    {
        // MINOR review fix: a GENUINE cancellation test — the token is cancelled WHILE
        // _cli.RunAsync is in flight (CancelDuringRunAsyncCli never returns until the token fires),
        // not before the call is even made.
        //
        // This pins TODAY's actual contract: unlike RunSuiteOrchestrator.RunAsync (which catches
        // cancellation during its own CLI invocation and maps it to a Completed outcome with
        // Cancelled=true — see RunSuiteOrchestratorTests' EDGE-002 section), PlanCoverageOrchestrator
        // .PlanAsync does NOT catch cancellation that fires mid-invocation: the
        // OperationCanceledException propagates out of PlanAsync uncaught, and from there out of
        // PlanCoverageTool.HandleAsync uncaught too (the MCP SDK's own tool-dispatch plumbing is
        // what ultimately turns that into a protocol-level response, not this orchestrator). This
        // is a deliberate record of the CURRENT contract, not an endorsement that it is the ideal
        // one — if that behaviour is intentionally changed in future (e.g. to mirror run_suite's
        // Cancelled outcome instead of throwing), update this test to assert the NEW contract
        // explicitly rather than deleting it.
        var cli = new CancelDuringRunAsyncCli(CliVersionNormaliser.Normalise(Pin.Version));
        var orchestrator = CreateOrchestrator(cli);
        using var cts = new CancellationTokenSource();

        var planTask = orchestrator.PlanAsync("suites/", null, null, null, null, null, cts.Token);
        cts.Cancel();

        // ThrowsAnyAsync (not ThrowsAsync): Task.Delay(Infinite, cancellationToken) — the mechanism
        // CancelDuringRunAsyncCli uses to genuinely await the token — throws its own
        // TaskCanceledException, a DERIVED type of OperationCanceledException, not the base type
        // itself. Both are "an uncaught cancellation propagated", which is exactly the contract
        // being pinned; asserting the exact derived type would overspecify an implementation detail
        // of how CancelDuringRunAsyncCli happens to observe the token.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planTask);
    }

    // ── Issue #76: PathSafetyGuard on both path arguments ───────────────────────────────────────

    /// <summary>
    /// The UNC arm, on the parameter that reaches <c>vouchfx plan</c>'s argv as its first operand.
    /// <c>CallCount == 0</c> is the load-bearing assertion: the refusal happens before the pin
    /// handshake, so not even the version probe is spawned — which is what makes "the engine never
    /// performed the SMB/NTLM handshake" a fact about this code rather than about the fake.
    /// </summary>
    [Theory]
    [InlineData(@"\\attacker-host\share\suites")]
    [InlineData("//attacker-host/share/suites")]
    [InlineData(@"\\?\UNC\attacker-host\share\suites")]
    public async Task PlanAsync_UncPath_IsRejectedWithoutInvokingTheCli(string uncPath)
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty)));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync(uncPath, null, null, null, null, null);

        var rejected = Assert.IsType<PlanCoverageOutcome.PathRejected>(outcome);
        Assert.Contains("network/UNC", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_UncEventsPath_IsRejectedWithoutInvokingTheCli()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty)));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync(
            "suites/", @"\\attacker-host\share\events.jsonl", null, null, null, null);

        var rejected = Assert.IsType<PlanCoverageOutcome.PathRejected>(outcome);
        Assert.Contains("network/UNC", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_PathEscapingTheRoot_IsRejected()
    {
        using var temp = new TempWorkspace();
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty)));
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync(
            Path.Combine(temp.Root, "..", "elsewhere"), null, null, null, null, null);

        var rejected = Assert.IsType<PlanCoverageOutcome.PathRejected>(outcome);
        Assert.Contains("outside the configured workspace root", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    [Fact]
    public async Task PlanAsync_NoWorkspaceConfigured_TheSameEscapingPath_ReachesTheCliUnchanged()
    {
        // The paired compatibility half: containment is workspace-gated here exactly as it is
        // everywhere else, so a caller who never opted in sees the identical input analysed.
        using var temp = new TempWorkspace();
        var escaping = Path.Combine(temp.Root, "..", "elsewhere");

        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync(escaping, null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.Equal(escaping, capturedArguments![1]);
    }

    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_EventsPathEscapingTheRoot_IsRejected()
    {
        using var temp = new TempWorkspace();
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.Completed(0, SampleReportJson, string.Empty)));
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync(
            temp.Root, Path.Combine(temp.Root, "..", "elsewhere", "events.jsonl"), null, null, null, null);

        var rejected = Assert.IsType<PlanCoverageOutcome.PathRejected>(outcome);
        Assert.Contains("outside the configured workspace root", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(0, cli.CallCount);
    }

    /// <summary>
    /// The guard must check the string the ENGINE is handed, not the one the caller typed — so a
    /// workspace-relative path is rebased onto the root and it is the REBASED value that lands in
    /// argv. Asserted on both parameters, since both are spliced into the same argument list.
    /// </summary>
    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_RelativePathsAreRebasedOntoTheRootInArgv()
    {
        using var temp = new TempWorkspace();

        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync("suites", "history.jsonl", null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.NotNull(capturedArguments);
        Assert.Equal(Path.Combine(temp.Root, "suites"), capturedArguments![1]);
        Assert.Contains(Path.Combine(temp.Root, "history.jsonl"), capturedArguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_BlankEventsPath_IsStillSilentlyOmittedRatherThanRejected()
    {
        // The guard runs under EXACTLY the condition that puts eventsPath on argv. A whitespace-only
        // value has always been dropped, and guarding it anyway would turn an ignored argument into
        // a rejection for no security gain (a blank string never reaches the engine at all).
        using var temp = new TempWorkspace();

        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync(temp.Root, "   ", null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.DoesNotContain("--events", capturedArguments!);
    }

    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_PathInsideTheRoot_ReachesTheCli()
    {
        // Anti-vacuity: the guard must not simply refuse everything.
        using var temp = new TempWorkspace();

        List<string>? capturedArguments = null;
        var cli = CountingCli.Wrap(FakeVouchfxCli.WithPlanHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                capturedArguments = args.ToList();
                return CliInvocationResult.Completed(0, SampleReportJson, string.Empty);
            }));
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync(temp.Root, null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.Completed>(outcome);
        Assert.Equal(temp.Root, capturedArguments![1]);
    }

    /// <summary>
    /// The leading-<c>-</c> guard still runs FIRST, and still reports VFX-E-1006's
    /// <see cref="PlanCoverageOutcome.InvalidArgument"/> rather than the new path code. Order
    /// matters: rebasing <c>-rf</c> onto a workspace root would produce a path that no longer begins
    /// with <c>-</c>, quietly laundering an argument-injection attempt into an accepted one.
    /// </summary>
    [Fact]
    public async Task PlanAsync_WorkspaceConfigured_PathBeginningWithDash_IsStillInvalidArgument()
    {
        using var temp = new TempWorkspace();
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli, temp.Workspace);

        var outcome = await orchestrator.PlanAsync("-rf", null, null, null, null, null);

        Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Equal(0, cli.CallCount);
    }

    /// <summary>
    /// The adjacent uncapped-echo fix that landed with the guard: the leading-<c>-</c> branches used
    /// to splice the caller's path into the message at full length, so an implausibly long argument
    /// produced an oversized tool ERROR — the very hole
    /// <c>PathSafetyGuard.MaxDisplayedPathChars</c> exists to close on the branches it owns.
    /// </summary>
    [Fact]
    public async Task PlanAsync_AbsurdlyLongPathBeginningWithDash_EchoesABoundedMessage()
    {
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync(
            "-" + new string('a', 200_000), null, null, null, null, null);

        var invalid = Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);

        // 1,000 characters of path plus this branch's own fixed wording — orders of magnitude below
        // the raw argument, which is the property being pinned, not an exact byte count.
        Assert.True(
            invalid.Message.Length < 2_000,
            $"Expected a bounded message; got {invalid.Message.Length} characters.");
        Assert.Equal(0, cli.CallCount);
    }

    /// <summary>A throwaway workspace root on disk, deleted with the test.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _sandbox;

        public TempWorkspace()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-plan-guard-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_sandbox, "workspace-a");
            Directory.CreateDirectory(Root);
            Workspace = Workspace.Resolve(Root);
        }

        public string Root { get; }

        public Workspace Workspace { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_sandbox, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Temp-directory hygiene only.
            }
        }
    }

    // ── Hand-off hint compatibility with scaffold_suite (REQ-012 / REQ-007) ─────────────────────

    [Fact]
    public void HandOffHint_SuggestedStepIdAndFirstSuggestedType_ConstructScaffoldStepRequestDirectly()
    {
        // Compile-time drift guard: if either PlanCoverageFinding's hint fields or
        // ScaffoldStepRequest's constructor shape ever changed incompatibly, this line fails the
        // BUILD, not just a runtime assertion — exactly what REQ-012's "hand-off hints feed
        // scaffold_suite unchanged" acceptance criterion is guarding against.
        var finding = new PlanCoverageFinding(
            Kind: "dependency-missing-step-type",
            Suite: "checkout.e2e.yaml",
            StepId: null,
            Target: "orders-db",
            TargetKind: "dependency",
            SuggestedTypes: ["db-assert.postgres"],
            SuggestedStepId: "assert-orders-db",
            Ambiguous: false,
            AmbiguityReason: null,
            History: null,
            Detail: "Dependency 'orders-db' has no analysed step of a candidate asserting type.",
            RelatedSuites: []);

        // No re-derivation: the finding's own hint VALUES become the scaffold request's id/type.
        var stepRequest = new Vouchfx.Mcp.Scaffold.ScaffoldStepRequest(
            finding.SuggestedStepId!, finding.SuggestedTypes[0]);

        Assert.Equal("assert-orders-db", stepRequest.Id);
        Assert.Equal("db-assert.postgres", stepRequest.Type);
    }

    private static PlanCoverageOrchestrator CreateOrchestrator(IVouchfxCli cli, Workspace? workspace = null) =>
        new(new CliPinVerifier(cli, Pin), cli, Pin, workspace);

    /// <summary>
    /// A thin counting decorator over <see cref="IVouchfxCli"/> so "the CLI was never invoked" tests
    /// (argument-safety and threshold-validation guards) can assert exactly <c>0</c> calls, rather
    /// than inferring it indirectly from the returned outcome's shape — mirrors
    /// <c>FakeSuiteRunner.InvocationCount</c>'s identical rationale in <c>RunSuiteOrchestratorTests</c>.
    /// </summary>
    private sealed class CountingCli : IVouchfxCli
    {
        private readonly IVouchfxCli _inner;

        private CountingCli(IVouchfxCli inner) => _inner = inner;

        public int CallCount { get; private set; }

        public static CountingCli Wrap(IVouchfxCli inner) => new(inner);

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.TryGetVersionOutputAsync(cancellationToken);
        }

        public Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.TryRunStdoutAsync(arguments, maxStreamBytes, cancellationToken);
        }

        public Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.RunAsync(arguments, maxStreamBytes, timeout, cancellationToken);
        }
    }

    /// <summary>
    /// A thin decorator over <see cref="IVouchfxCli"/> that records the <c>timeout</c> value passed
    /// to <see cref="RunAsync"/>, so a test can assert the orchestrator threads its own explicit
    /// budget through rather than silently relying on the implementation's default.
    /// </summary>
    private sealed class TimeoutCapturingCli : IVouchfxCli
    {
        private readonly IVouchfxCli _inner;
        private readonly Action<TimeSpan?> _onRunAsyncTimeout;

        public TimeoutCapturingCli(IVouchfxCli inner, Action<TimeSpan?> onRunAsyncTimeout)
        {
            _inner = inner;
            _onRunAsyncTimeout = onRunAsyncTimeout;
        }

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
            _inner.TryGetVersionOutputAsync(cancellationToken);

        public Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default) =>
            _inner.TryRunStdoutAsync(arguments, maxStreamBytes, cancellationToken);

        public Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            _onRunAsyncTimeout(timeout);
            return _inner.RunAsync(arguments, maxStreamBytes, timeout, cancellationToken);
        }
    }

    /// <summary>
    /// An <see cref="IVouchfxCli"/> whose <see cref="RunAsync"/> genuinely awaits the supplied
    /// cancellation token rather than returning instantly, so
    /// <see cref="PlanAsync_CancelledDuringCliInvocation_PropagatesOperationCanceledException"/> can
    /// cancel WHILE the call is in flight — mirroring what a real, slow <c>vouchfx plan</c>
    /// invocation would look like — rather than merely handing an already-cancelled token to a fake
    /// that resolves synchronously regardless.
    /// </summary>
    private sealed class CancelDuringRunAsyncCli : IVouchfxCli
    {
        private readonly string _versionOutput;

        public CancelDuringRunAsyncCli(string versionOutput) => _versionOutput = versionOutput;

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_versionOutput);

        public Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by PlanAsync_CancelledDuringCliInvocation_....");

        public async Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Unreachable: Task.Delay(Timeout.Infinite, cancellationToken) only ever completes " +
                "by throwing OperationCanceledException once cancellationToken fires.");
        }
    }
}
