using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Spec C / M2 Healer end-to-end through the in-memory MCP harness: <c>diagnose_run</c> returns
/// Fail proposals, EnvironmentError guidance, and keeps <c>explain_run</c> working.
/// </summary>
public class RealDiagnoseRunMcpTests
{
    /// <summary>
    /// The order the mixed fixture's spec-edit proposals arrive in: environment-error records
    /// FIRST (they name the root cause and must never be starved by a step cascade), then
    /// step-derived ones in events order, each step contributing all of its scopes together.
    /// </summary>
    private static readonly string[] ExpectedScopeOrder = ["environment", "timeouts", "match"];

    /// <summary>
    /// US-S4-03's golden wire test: one response carrying BOTH proposal kinds — the existing
    /// Fail-only <c>proposals</c> unchanged, and the new <c>specEditProposals</c> beside them.
    /// </summary>
    /// <remarks>
    /// The fixture mixes a Fail step (which may only ever produce a review proposal) with an
    /// environment error and an Inconclusive step (which may only ever produce spec-edit proposals),
    /// so the two lists' PARTITION is visible on the wire rather than only in the builder's unit
    /// tests. <c>classificationHints</c> is deliberately not asserted here: it is display-only text,
    /// and a host branches on <c>reason.kind</c>.
    /// </remarks>
    [Fact]
    public async Task DiagnoseRun_MixedFixture_ReturnsSpecEditProposalsAlongsideUnchangedFailProposals()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-superset-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
            {"type":"step-attempt","stepId":"expect-order-event","attempt":1,"tMs":100,"outcome":"FAIL","observation":{"matched":false,"seen":"order_id"}}
            {"type":"step-completed","stepId":"expect-order-event","verdict":"INCONCLUSIVE","durationMs":1300,"observation":{"reason":"retry-timeout","attempts":1}}
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """,
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

            // The existing Fail-only list, unchanged in shape: the Fail step and only the Fail step.
            var proposals = payload.GetProperty("proposals").EnumerateArray().ToArray();
            var failProposal = Assert.Single(proposals);
            Assert.Equal("check-balance", failProposal.GetProperty("stepId").GetString());
            Assert.Contains("--- a/", failProposal.GetProperty("patch").GetString()!, StringComparison.Ordinal);

            // ...and the new list beside it, for the OTHER two outcome categories.
            var specEdits = payload.GetProperty("specEditProposals").EnumerateArray().ToArray();
            Assert.Equal(3, specEdits.Length);

            var scopes = specEdits.Select(p => p.GetProperty("scope").GetString()!).ToArray();
            Assert.Equal(ExpectedScopeOrder, scopes);

            // No Fail step appears in the spec-edit list, on the wire.
            Assert.DoesNotContain("check-balance", specEdits.Select(p => p.GetProperty("stepId").GetString()));

            // The environment-scoped entry concerns a RECORD, not a step, so its stepId is an
            // explicit null rather than a fabricated identifier.
            var environmentEdit = specEdits.Single(p => p.GetProperty("scope").GetString() == "environment");
            Assert.Equal(JsonValueKind.Null, environmentEdit.GetProperty("stepId").ValueKind);
            Assert.Contains("environment:", environmentEdit.GetProperty("suggestedEdit").GetString()!, StringComparison.Ordinal);

            // Every fragment is YAML advice, never a diff against a file this server cannot see.
            foreach (var specEdit in specEdits)
            {
                var suggestedEdit = specEdit.GetProperty("suggestedEdit").GetString()!;
                Assert.StartsWith("# Review-only suggestion", suggestedEdit, StringComparison.Ordinal);
                Assert.DoesNotContain("--- a/", suggestedEdit, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(specEdit.GetProperty("rationale").GetString()));
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The omitted counters reach the wire in camelCase, and a fixture where the LADDER dropped
    /// every proposal shows them carrying a non-zero value — the strongest form of the pin, since a
    /// counter that only ever reads 0 would be indistinguishable from an absent one.
    /// </summary>
    /// <remarks>
    /// The semantics being pinned are "produced by this run but not in this response": both causes
    /// (the builders' 10-proposal cap and the response ladder's own drops) land in the same number,
    /// so a host reading it never has to know which happened.
    /// </remarks>
    [Fact]
    public async Task DiagnoseRun_StructuredContent_CarriesTheOmittedCountersInCamelCase()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // Small run: everything fits, so both counters are a real, asserted zero.
        var smallPath = Path.Combine(Path.GetTempPath(), $"real-diagnose-counters-small-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            smallPath,
            """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"health gate timed out after 30000ms"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """,
            cts.Token);

        // Large run: the ladder empties both lists, so the counters carry what was dropped.
        var largePath = Path.Combine(Path.GetTempPath(), $"real-diagnose-counters-large-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            largePath,
            Vouchfx.Mcp.Tests.Diagnosis.DiagnoseRunOrchestratorTests.BuildHonestFanOutEvents(
                observationChars: 2_400, stepIdChars: 1_400, pollSteps: 2, failSteps: 5),
            cts.Token);

        try
        {
            var small = await CountersOf(harness, smallPath, cts.Token);
            Assert.Equal(0, small.Omitted);
            Assert.Equal(0, small.OmittedSpecEdits);
            Assert.True(small.HadProposals, "The small fixture must actually return proposals.");

            var large = await CountersOf(harness, largePath, cts.Token);
            Assert.False(large.HadProposals, "The large fixture's lists must have been emptied by the ladder.");
            Assert.True(
                large.Omitted > 0 && large.OmittedSpecEdits > 0,
                $"Expected non-zero counters after the ladder emptied both lists, got "
                + $"{large.Omitted}/{large.OmittedSpecEdits}.");
        }
        finally
        {
            File.Delete(smallPath);
            File.Delete(largePath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static async Task<(int Omitted, int OmittedSpecEdits, bool HadProposals)> CountersOf(
        McpTestHarness harness, string eventsPath, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(
            "diagnose_run",
            new Dictionary<string, object?> { ["eventsPath"] = eventsPath },
            cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        // camelCase on the wire, like every other field this server emits.
        var omitted = payload.GetProperty("omittedProposalCount").GetInt32();
        var omittedSpecEdits = payload.GetProperty("omittedSpecEditProposalCount").GetInt32();
        var hadProposals =
            payload.GetProperty("proposals").EnumerateArray().Any()
            || payload.GetProperty("specEditProposals").EnumerateArray().Any();

        return (omitted, omittedSpecEdits, hadProposals);
    }

    /// <summary>
    /// Gherkin 6: nothing is auto-applied. The tool's own <c>ReadOnly</c> metadata is true, and the
    /// suite file on disk is byte-identical after a call that returns both proposal kinds.
    /// </summary>
    [Fact]
    public async Task DiagnoseRun_ReturningBothProposalKinds_TouchesNoSuiteFileAndStaysReadOnly()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var suitePath = Path.Combine(Path.GetTempPath(), $"real-diagnose-suite-{Guid.NewGuid():N}.e2e.yaml");
        await File.WriteAllTextAsync(suitePath, "metadata:\n  name: untouched\n", cts.Token);
        var before = await File.ReadAllBytesAsync(suitePath, cts.Token);
        var writtenAt = File.GetLastWriteTimeUtc(suitePath);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-readonly-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """
            {"type":"step-completed","stepId":"check-balance","verdict":"FAIL","durationMs":120,"observation":{"expected":"120.00","actual":"95.00"}}
            {"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"manifest for ghcr.io/acme/orders-api:latest not found"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """,
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.NotEmpty(payload.GetProperty("proposals").EnumerateArray().ToArray());
            Assert.NotEmpty(payload.GetProperty("specEditProposals").EnumerateArray().ToArray());

            Assert.Equal(before, await File.ReadAllBytesAsync(suitePath, cts.Token));
            Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(suitePath));

            var tool = Assert.Single(
                await harness.Client.ListToolsAsync(cancellationToken: cts.Token),
                t => t.Name == "diagnose_run");
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        }
        finally
        {
            File.Delete(path);
            File.Delete(suitePath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_FailFixture_ReturnsProposalWithPatch()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-run-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80,"observation":{"expected":"SHIPPED","actual":"PENDING"}}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""",
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("Fail", payload.GetProperty("diagnosis").GetProperty("verdict").GetString());
            var proposals = payload.GetProperty("proposals").EnumerateArray().ToArray();
            Assert.NotEmpty(proposals);
            Assert.Equal("assert-order-status", proposals[0].GetProperty("stepId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(proposals[0].GetProperty("patch").GetString()));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_EnvironmentErrorFixture_EmptyProposalsAndGuidancePresent()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-diagnose-env-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"environment-error","errorKind":"ImagePull","resourceName":"orders-api","detail":"pull access denied"}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""",
            cts.Token);
        try
        {
            var result = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("EnvironmentError", payload.GetProperty("diagnosis").GetProperty("verdict").GetString());
            Assert.Empty(payload.GetProperty("proposals").EnumerateArray().ToArray());
            Assert.NotEmpty(payload.GetProperty("environmentGuidance").EnumerateArray().ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ExplainRun_StillWorks_AlongsideDiagnoseRun()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var path = Path.Combine(Path.GetTempPath(), $"real-explain-still-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(
            path,
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""" +
            "\n" +
            """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""",
            cts.Token);
        try
        {
            var explain = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);
            Assert.False(explain.IsError ?? false);
            Assert.Equal("Pass", explain.StructuredContent!.Value.GetProperty("verdict").GetString());

            var diagnose = await harness.Client.CallToolAsync(
                "diagnose_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);
            Assert.False(diagnose.IsError ?? false);
            Assert.Equal("Pass", diagnose.StructuredContent!.Value.GetProperty("diagnosis").GetProperty("verdict").GetString());
            Assert.Empty(diagnose.StructuredContent!.Value.GetProperty("proposals").EnumerateArray().ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task DiagnoseRun_BadPath_ReturnsToolErrorAndServerStillListsEveryTool()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "diagnose_run",
            new Dictionary<string, object?> { ["eventsPath"] = @"\\attacker-host\share\events.jsonl" },
            cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var toolsAfterError = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(18, toolsAfterError.Count);
        Assert.Contains(toolsAfterError, t => t.Name == "diagnose_run");
        Assert.Contains(toolsAfterError, t => t.Name == "explain_run");

        Assert.Empty(consoleOut.Writer.ToString());
    }
}
