using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-07's wire-facing goldens for <c>get_run_artifacts</c>, driven over the real MCP protocol
/// through <see cref="McpTestHarness"/>: the partial shape a real client sees, the two claims that are
/// purely about SERIALISATION (an omitted <c>reports.html</c>, an explicit null <c>health</c>), the
/// AC-004 half that lives in the tool description, and every structured refusal's code.
/// </summary>
/// <remarks>
/// The argument bounds, the folding of environment-error events and the response bound are covered
/// exhaustively against <see cref="GetRunArtifactsOrchestrator"/> directly in
/// <c>Run/GetRunArtifactsOrchestratorTests.cs</c>; these tests confirm what a HOST observes, which is a
/// different question — an omitted field and a null one are indistinguishable in a record and are not
/// on the wire.
/// </remarks>
public class RealGetRunArtifactsMcpTests
{
    /// <summary>A run whose environment failed in two places — the story's second Gherkin scenario.</summary>
    private const string FailedEnvironmentEvents =
        """
        {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"pull access denied"}
        {"type":"environment-error","errorKind":"Provision","resourceName":"orders-db","detail":"container exited unexpectedly"}
        {"type":"scenario-completed","scenarioId":"place-order","verdict":"ENV_ERROR"}
        """;

    /// <summary>The story's FIRST Gherkin scenario over the real wire, all three clauses.</summary>
    [Fact]
    public async Task OnTheWire_LogsAreAnEmptyArrayWithPartialTrue_AndTheDescriptionSaysWhy()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var artifacts = await CallAsync(harness, Arguments(entry.RunId, kind: "logs"), cts.Token);

            Assert.Equal(0, artifacts.GetProperty("logs").GetArrayLength());
            Assert.True(artifacts.GetProperty("partial").GetBoolean());

            // The scenario's third clause — "the tool's own description states full log access awaits
            // upstream ask U4" — which is US-S3-07's AC-004 half that lives in code rather than docs.
            var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
            var description = Assert.Single(tools, t => t.Name == "get_run_artifacts").Description!;

            Assert.Contains("U4", description, StringComparison.Ordinal);
            Assert.Contains("'logs' is ALWAYS an empty array", description, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The story's SECOND Gherkin scenario over the wire, and the one claim only the wire can settle:
    /// <c>health</c> arrives as an explicit JSON <c>null</c> rather than being omitted, so a host reads
    /// "not observed" at the field instead of having to notice an absence.
    /// </summary>
    [Fact]
    public async Task OnTheWire_EnvironmentIdentifiersArriveWithHealthAsAnExplicitNull()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var artifacts = await CallAsync(harness, Arguments(entry.RunId, kind: "environment"), cts.Token);
            var environment = artifacts.GetProperty("environment");
            var resources = environment.GetProperty("resources").EnumerateArray().ToArray();

            Assert.Equal(2, resources.Length);
            Assert.Equal("orders-api", resources[0].GetProperty("id").GetString());
            Assert.Equal("orders-db", resources[1].GetProperty("id").GetString());

            foreach (var resource in resources)
            {
                Assert.True(resource.TryGetProperty("health", out var health));
                Assert.Equal(JsonValueKind.Null, health.ValueKind);
                Assert.Equal("unclassified", resource.GetProperty("role").GetString());
            }

            // Neither spec array claims an identifier the stream did not classify.
            Assert.Equal(0, environment.GetProperty("services").GetArrayLength());
            Assert.Equal(0, environment.GetProperty("dependencies").GetArrayLength());
            Assert.True(artifacts.GetProperty("partial").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>The story's THIRD Gherkin scenario over the wire, verbatim: isError, and the maximum named.</summary>
    [Fact]
    public async Task OnTheWire_TailLines10000_IsAnErrorStatingTheMaximumIs5000()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var arguments = Arguments(entry.RunId);
            arguments["tailLines"] = 10_000;

            var result = await harness.Client.CallToolAsync(
                "get_run_artifacts", arguments, cancellationToken: cts.Token);

            Assert.True(result.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains("5000", content.Text, StringComparison.Ordinal);
            Assert.Contains("VFX-E-1006", content.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The other serialisation-only claim: the engine-written report fields are OMITTED, not null.
    /// A null would read as "this server looked for the engine's HTML report and found none"; the
    /// absence says "this server has no way to know where it is" — see <c>RunReportArtifacts.Html</c>.
    /// </summary>
    [Fact]
    public async Task OnTheWire_TheEngineWrittenReportFieldsAreOmittedRatherThanNull()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var artifacts = await CallAsync(harness, Arguments(entry.RunId, kind: "reports"), cts.Token);
            var reports = artifacts.GetProperty("reports");

            Assert.False(reports.TryGetProperty("html", out _));
            Assert.False(reports.TryGetProperty("junit", out _));

            // And the one artefact this build really has IS there, so the absences above are an honest
            // inventory rather than an empty section.
            var events = reports.GetProperty("events");
            Assert.Equal(path, events.GetProperty("path").GetString());
            Assert.True(events.GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, events.GetProperty("resourceUri").ValueKind);

            // Both omissions are still explained, at the fields they concern.
            var gapFields = artifacts.GetProperty("gaps").EnumerateArray()
                .Select(g => g.GetProperty("field").GetString()).ToArray();
            Assert.Contains("reports.html", gapFields);
            Assert.Contains("reports.junit", gapFields);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// A section the caller did not select is absent from the payload entirely — the property that
    /// makes an empty <c>logs</c> readable at all, since "you did not ask" and "there are none" would
    /// otherwise look identical.
    /// </summary>
    [Fact]
    public async Task OnTheWire_AnUnselectedSectionIsAbsentRatherThanEmpty()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var reportsOnly = await CallAsync(harness, Arguments(entry.RunId, kind: "reports"), cts.Token);
            Assert.True(reportsOnly.TryGetProperty("reports", out _));
            Assert.False(reportsOnly.TryGetProperty("logs", out _));
            Assert.False(reportsOnly.TryGetProperty("environment", out _));
            Assert.Equal("reports", reportsOnly.GetProperty("kind").GetString());

            var everything = await CallAsync(harness, Arguments(entry.RunId), cts.Token);
            Assert.True(everything.TryGetProperty("reports", out _));
            Assert.True(everything.TryGetProperty("logs", out _));
            Assert.True(everything.TryGetProperty("environment", out _));
            Assert.Equal("all", everything.GetProperty("kind").GetString());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task AnUnknownRunId_IsRefusedWithVfxE1505()
    {
        await AssertRefusedAsync(_ => Arguments("run-0000000000000000000000000000cafe"), "VFX-E-1505");
    }

    [Fact]
    public async Task AnUnknownKind_IsRefusedWithVfxE1006()
    {
        await AssertRefusedAsync(runId => Arguments(runId, kind: "screenshots"), "VFX-E-1006");
    }

    [Fact]
    public async Task ABlankRunId_IsRefusedWithVfxE1006()
    {
        await AssertRefusedAsync(_ => Arguments("   "), "VFX-E-1006");
    }

    [Fact]
    public async Task GetRunArtifacts_IsAdvertisedAsAReadOnlyTool()
    {
        // US-S3-04's AC: read-only tools stay lock-free, and this annotation is what tells a host it
        // is safe to call concurrently (spec §4.6). RunLockSourceGuardTests holds the other half —
        // that no read-only tool can take the lock at all.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == "get_run_artifacts");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task AssertRefusedAsync(
        Func<string, Dictionary<string, object?>> arguments, string expectedCode)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(FailedEnvironmentEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var result = await harness.Client.CallToolAsync(
                "get_run_artifacts", arguments(entry.RunId), cancellationToken: cts.Token);

            Assert.True(result.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains(expectedCode, content.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static Dictionary<string, object?> Arguments(string? runId, string? kind = null) =>
        kind is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["runId"] = runId }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runId"] = runId,
                ["kind"] = kind,
            };

    private static async Task<JsonElement> CallAsync(
        McpTestHarness harness, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(
            "get_run_artifacts", arguments, cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false, DescribeFailure(result));
        return result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
    }

    private static string DescribeFailure(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no content)";

    /// <summary>A registry holding one completed run whose events file the caller must delete.</summary>
    private static (StubRunRegistry Registry, string Path) GivenRun(string eventsFileContent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"real-get-run-artifacts-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, eventsFileContent);
        return (StubRunRegistry.WithCompletedRun(path), path);
    }
}
