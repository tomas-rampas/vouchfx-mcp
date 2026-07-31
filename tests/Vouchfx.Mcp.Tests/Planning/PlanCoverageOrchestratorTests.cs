using Vouchfx.Mcp;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Planning;

namespace Vouchfx.Mcp.Tests.Planning;

/// <summary>
/// Covers <see cref="PlanCoverageOrchestrator"/> (Spec D / M3 Planner, REQ-012) against
/// <see cref="FakeVouchfxCli"/> — no real CLI required. The engine release carrying <c>vouchfx
/// plan</c> is not published yet (see ENGINE_PIN), so every test here is CLI-independent by
/// construction, mirroring how <c>ScaffoldSuiteOrchestratorTests</c> covers Spec B ahead of a
/// scaffold-capable published pin.
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
        // Pin-ok CLI that does not implement `plan` (maps to NotLaunched for unknown commands) —
        // the exact shape the currently-pinned ENGINE_PIN (predates the M3 Planner) is in today.
        var cli = FakeVouchfxCli.ReportingVersion(CliVersionNormaliser.Normalise(Pin.Version));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.PlanAsync("suites/", null, null, null, null, null);

        var unavailable = Assert.IsType<PlanCoverageOutcome.CliUnavailable>(outcome);
        Assert.Contains("plan", unavailable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dotnet tool install", unavailable.Message, StringComparison.Ordinal);
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
    public async Task PlanAsync_InvalidPathWithCancellationRequested_StillReturnsPromptlyWithoutHanging()
    {
        // REQ-012: "an invalid suite path returns a structured tool error, not a hang" — proven by a
        // bounded wait; local argument-safety validation runs before any async CLI call, so this
        // resolves synchronously regardless of the token's state.
        var cli = CountingCli.Wrap(FakeVouchfxCli.NotFound());
        var orchestrator = CreateOrchestrator(cli);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var outcome = await orchestrator.PlanAsync("-rf", null, null, null, null, null, cts.Token);

        Assert.IsType<PlanCoverageOutcome.InvalidArgument>(outcome);
        Assert.Equal(0, cli.CallCount);
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

    private static PlanCoverageOrchestrator CreateOrchestrator(IVouchfxCli cli) =>
        new(new CliPinVerifier(cli, Pin), cli, Pin);

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
            IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.RunAsync(arguments, maxStreamBytes, cancellationToken);
        }
    }
}
