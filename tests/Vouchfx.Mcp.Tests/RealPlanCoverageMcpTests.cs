using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spec D / M3 Planner — REQ-012: <c>plan_coverage</c> end-to-end through the in-memory MCP harness,
/// using <see cref="FakeVouchfxCli"/> so CI does not require the pinned engine to be installed on
/// the runner — no test in this repository's CI has a real <c>vouchfx</c> on PATH. The pin itself is
/// no longer the reason: <c>ENGINE_PIN</c> names a release at or beyond v1.0.0-rc.3, the first to
/// ship <c>vouchfx plan</c>.
/// For the complementary check that MCP and the real pinned CLI cannot drift, see
/// <c>RealPlanCoverageAgainstPinnedCliTests</c>, which drives the actual installed binary.
/// Covers all four REQ-012 acceptance criteria: the tool appears in <c>tools/list</c>; a finding's
/// hand-off hints feed <c>scaffold_suite</c> unchanged; an invalid suite path returns a structured
/// tool error rather than a hang; and (see <c>McpServerSkeletonTests.PlanCoverage_Schema_*</c>) the
/// advertised schema carries no free-text parameter.
/// </summary>
public class RealPlanCoverageMcpTests
{
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

    // ── REQ-012 acceptance #1: the tool appears in tools/list ───────────────────────────────────

    [Fact]
    public async Task PlanCoverage_AppearsInToolsList()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);

        Assert.Contains(tools, t => t.Name == "plan_coverage");
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Happy path: findings + inventory relayed verbatim ───────────────────────────────────────

    [Fact]
    public async Task PlanCoverage_ValidSuite_ReturnsInventoryAndFindings()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pinVersion = CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version);
        var cli = FakeVouchfxCli.WithPlanHandler(
            pinVersion, _ => CliInvocationResult.Completed(0, SampleReportJson, string.Empty));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var result = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = "suites/" },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.");

        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        var suites = payload.GetProperty("inventory").GetProperty("suites").EnumerateArray().ToArray();
        Assert.Single(suites);
        Assert.Equal("checkout.e2e.yaml", suites[0].GetProperty("path").GetString());

        var findings = payload.GetProperty("findings").EnumerateArray().ToArray();
        var finding = Assert.Single(findings);
        Assert.Equal("dependency-missing-step-type", finding.GetProperty("kind").GetString());
        Assert.Equal("assert-orders-db", finding.GetProperty("suggestedStepId").GetString());
        Assert.Equal(
            "db-assert.postgres",
            Assert.Single(finding.GetProperty("suggestedTypes").EnumerateArray()).GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── REQ-012 acceptance #3: invalid suite path -> structured error, not a hang ───────────────

    [Fact]
    public async Task PlanCoverage_InvalidSuitePath_ReturnsStructuredToolErrorWithoutHanging()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pinVersion = CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version);
        var cli = FakeVouchfxCli.WithPlanHandler(
            pinVersion,
            _ => CliInvocationResult.Completed(
                2, string.Empty, "Suite path 'does-not-exist/' does not exist."));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = "does-not-exist/" },
            cancellationToken: cts.Token);
        stopwatch.Stop();

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("does-not-exist", content.Text, StringComparison.Ordinal);

        // Bounded well under the test's own 20s cancellation budget: a genuine hang would have
        // caused the call above to throw on cts.Token instead of returning at all.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected a structured error to return promptly, took {stopwatch.Elapsed}.");

        // Not a crash: the server must still be responsive afterwards.
        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PlanCoverage_EmptySuiteFolder_ReturnsStructuredToolError()
    {
        // EDGE-009: an empty suite folder is a usage error (CLI exit 2), not an empty success.
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pinVersion = CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version);
        var cli = FakeVouchfxCli.WithPlanHandler(
            pinVersion,
            _ => CliInvocationResult.Completed(
                2, string.Empty, "No *.e2e.yaml suites were discovered under 'empty/'."));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var result = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = "empty/" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("empty/", content.Text, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task PlanCoverage_CliNotFound_ReturnsInstallCommandErrorWithoutCrashingServer()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: FakeVouchfxCli.NotFound());

        var result = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = "suites/" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("not found on PATH", content.Text, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global vouchfx --version", content.Text, StringComparison.Ordinal);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(9, tools.Count);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── REQ-012 acceptance #2: hand-off hints feed scaffold_suite unchanged ─────────────────────

    [Fact]
    public async Task PlanCoverage_HandOffHints_FeedScaffoldSuiteUnchanged()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pinVersion = CliVersionNormaliser.Normalise(McpTestHarness.DefaultTestPin.Version);
        // WithPlanHandler also enables the Spec B scaffold fake (see its own remarks), so one CLI
        // fake drives both tools in this single hand-off test.
        var cli = FakeVouchfxCli.WithPlanHandler(
            pinVersion, _ => CliInvocationResult.Completed(0, SampleReportJson, string.Empty));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, vouchfxCli: cli);

        var planResult = await harness.Client.CallToolAsync(
            "plan_coverage",
            new Dictionary<string, object?> { ["path"] = "suites/" },
            cancellationToken: cts.Token);

        Assert.False(planResult.IsError ?? false);
        var planPayload = planResult.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.");
        var finding = Assert.Single(planPayload.GetProperty("findings").EnumerateArray());

        var suggestedStepId = finding.GetProperty("suggestedStepId").GetString()
            ?? throw new InvalidOperationException("Expected a non-null suggestedStepId.");
        var suggestedType = Assert.Single(finding.GetProperty("suggestedTypes").EnumerateArray()).GetString()
            ?? throw new InvalidOperationException("Expected a non-null suggestedTypes[0].");

        // The hand-off itself: the finding's OWN hint values, spliced directly into scaffold_suite's
        // steps[].id / steps[].type arguments — no re-derivation, no re-parsing. If either tool's
        // field shape drifted from the other, this MCP round trip (which binds directly onto
        // ScaffoldStepRequest[]) would fail here rather than silently accepting mismatched data.
        var scaffoldResult = await harness.Client.CallToolAsync(
            "scaffold_suite",
            new Dictionary<string, object?>
            {
                ["steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = suggestedStepId, ["type"] = suggestedType },
                },
            },
            cancellationToken: cts.Token);

        Assert.False(scaffoldResult.IsError ?? false);
        var yaml = scaffoldResult.StructuredContent!.Value.GetProperty("yaml").GetString()!;
        Assert.Contains(suggestedStepId, yaml, StringComparison.Ordinal);
        Assert.Contains(suggestedType, yaml, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
