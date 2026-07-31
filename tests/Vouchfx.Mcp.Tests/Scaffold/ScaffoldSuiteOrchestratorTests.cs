using Vouchfx.Mcp;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Scaffold;

namespace Vouchfx.Mcp.Tests.Scaffold;

/// <summary>
/// Covers <see cref="ScaffoldSuiteOrchestrator"/> (Spec B / REQ-007) against
/// <see cref="FakeVouchfxCli"/> — no real CLI required.
/// </summary>
public class ScaffoldSuiteOrchestratorTests
{
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    [Fact]
    public async Task ScaffoldAsync_MultiTypeIntent_ReturnsYamlWithTypesAndIds()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.Json));

        var outcome = await orchestrator.ScaffoldAsync(
            [
                new ScaffoldStepRequest("get-api", "http.rest", "GET api"),
                new ScaffoldStepRequest("check-db", "db-assert.postgres"),
            ],
            [new ScaffoldServiceRequest("api", "traefik/whoami")],
            [new ScaffoldDependencyRequest("db", "postgres")]);

        var completed = Assert.IsType<ScaffoldSuiteOutcome.Completed>(outcome);
        var yaml = completed.Result.Yaml;
        Assert.Contains("http.rest", yaml, StringComparison.Ordinal);
        Assert.Contains("db-assert.postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("get-api", yaml, StringComparison.Ordinal);
        Assert.Contains("check-db", yaml, StringComparison.Ordinal);
        Assert.Contains("Machine-drafted", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api", yaml, StringComparison.Ordinal);
        Assert.Contains("postgres", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScaffoldAsync_UnknownType_ReturnsScaffoldFailedNamingType()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.Json));

        var outcome = await orchestrator.ScaffoldAsync(
            [new ScaffoldStepRequest("x", "nope.fake")],
            services: null,
            dependencies: null);

        var failed = Assert.IsType<ScaffoldSuiteOutcome.ScaffoldFailed>(outcome);
        Assert.Contains("nope.fake", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScaffoldAsync_EmptySteps_ReturnsInvalidArgument()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.Json));

        var outcome = await orchestrator.ScaffoldAsync([], null, null);

        var invalid = Assert.IsType<ScaffoldSuiteOutcome.InvalidArgument>(outcome);
        Assert.Contains("at least one step", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScaffoldAsync_CliNotFound_ReturnsCliUnavailable()
    {
        var orchestrator = CreateOrchestrator(FakeVouchfxCli.NotFound());

        var outcome = await orchestrator.ScaffoldAsync(
            [new ScaffoldStepRequest("a", "http.rest")],
            null,
            null);

        var unavailable = Assert.IsType<ScaffoldSuiteOutcome.CliUnavailable>(outcome);
        Assert.Contains("not found", unavailable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScaffoldAsync_ScaffoldNotSupported_ReturnsCliUnavailable()
    {
        // Pin-ok CLI that does not implement scaffold (maps to CliInvocationResult.NotLaunched, i.e.
        // FailureReason == NotFound, for unknown commands).
        var cli = FakeVouchfxCli.ReportingVersion(CliVersionNormaliser.Normalise(Pin.Version));
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.ScaffoldAsync(
            [new ScaffoldStepRequest("a", "http.rest")],
            null,
            null);

        var unavailable = Assert.IsType<ScaffoldSuiteOutcome.CliUnavailable>(outcome);
        Assert.Contains("scaffold", unavailable.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScaffoldAsync_CliTimedOut_ReturnsScaffoldFailedNamingTheBudgetNotInstall()
    {
        // Consistency fix alongside PlanCoverageOrchestrator's identical split: a scaffold
        // invocation that overran its own budget must not tell the user to install/update a CLI
        // that is present, pinned, and working — see CliLaunchFailureReason's own remarks.
        var cli = FakeVouchfxCli.WithInvocationHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.TimedOut);
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.ScaffoldAsync(
            [new ScaffoldStepRequest("a", "http.rest")], null, null);

        var failed = Assert.IsType<ScaffoldSuiteOutcome.ScaffoldFailed>(outcome);
        Assert.Contains("15-second", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("install", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScaffoldAsync_CliOutputCapExceeded_ReturnsScaffoldFailedNamingTheCapNotInstall()
    {
        var cli = FakeVouchfxCli.WithInvocationHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args => CliInvocationResult.OutputCapExceeded);
        var orchestrator = CreateOrchestrator(cli);

        var outcome = await orchestrator.ScaffoldAsync(
            [new ScaffoldStepRequest("a", "http.rest")], null, null);

        var failed = Assert.IsType<ScaffoldSuiteOutcome.ScaffoldFailed>(outcome);
        Assert.Contains("1 MB", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("install", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH", failed.Message, StringComparison.Ordinal);
    }

    private static ScaffoldSuiteOrchestrator CreateOrchestrator(IVouchfxCli cli) =>
        new(new CliPinVerifier(cli, Pin), cli, Pin);
}
