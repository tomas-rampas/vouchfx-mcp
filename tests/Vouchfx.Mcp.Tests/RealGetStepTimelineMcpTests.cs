using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-06's wire-facing goldens for <c>get_step_timeline</c>, driven over the real MCP protocol
/// through <see cref="McpTestHarness"/>: the shape a real client sees, the truncation-immunity
/// comparison against a real <c>explain_run</c> call over the SAME run, the attempt vocabulary as it
/// appears on the wire, and every structured refusal's code.
/// </summary>
/// <remarks>
/// The budget arithmetic, the outcome mapping's every branch and the argument bounds are covered
/// exhaustively against <see cref="GetStepTimelineOrchestrator"/> directly in
/// <c>Run/GetStepTimelineOrchestratorTests.cs</c>; these tests confirm what a HOST observes, which is
/// a different question. In particular the immunity comparison is repeated here at the wire level on
/// purpose: the story's AC is about what a host receives from two tools, and a unit-level comparison
/// of two orchestrators does not establish that the SERVER hands both answers over intact.
/// </remarks>
public class RealGetStepTimelineMcpTests
{
    /// <summary>The four verdict-taxonomy words, none of which may appear as an attempt outcome.</summary>
    private static readonly string[] VerdictWords =
        [nameof(RunVerdict.Pass), nameof(RunVerdict.Fail), nameof(RunVerdict.EnvironmentError), nameof(RunVerdict.Inconclusive)];

    /// <summary>
    /// The story's first Gherkin scenario over the REAL wire, and its acceptance criterion in the
    /// form the criterion is actually written: the same run, read by both tools, with
    /// <c>explain_run</c> demonstrably shortening the timeline and <c>get_step_timeline</c>
    /// demonstrably not.
    /// </summary>
    [Fact]
    public async Task ATimelineExplainRunTruncates_ComesBackWholeFromGetStepTimeline()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(RetryTimeline(attempts: 40));
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var explained = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = path },
                cancellationToken: cts.Token);
            Assert.False(explained.IsError ?? false);

            var explainedStep = (explained.StructuredContent
                ?? throw new InvalidOperationException("Expected StructuredContent."))
                .GetProperty("notableSteps")[0];
            var explainedAttempts = explainedStep.GetProperty("attempts").GetArrayLength();
            var explainedOmitted = explainedStep.GetProperty("omittedAttemptCount").GetInt32();

            // The premise, asserted rather than assumed: without it the comparison below proves
            // nothing at all.
            Assert.True(
                explainedAttempts < 40,
                "explain_run returned the whole 40-attempt timeline over the wire, so this comparison "
                + "is vacuous. Its tiers changed; re-size the fixture above the largest of them.");
            Assert.True(explainedOmitted > 0);

            var timeline = await CallAsync(harness, Arguments(entry.RunId, entry.SpecPaths[0], "poll-order"), cts.Token);
            var attempts = timeline.GetProperty("attempts");

            Assert.Equal(40, attempts.GetArrayLength());
            Assert.Equal(explainedAttempts + explainedOmitted, attempts.GetArrayLength());
            Assert.False(timeline.GetProperty("truncated").GetBoolean());
            Assert.Equal(0, timeline.GetProperty("omittedAttemptCount").GetInt32());

            // Nothing elided or placeheld: the engine's own counter, complete and in order.
            Assert.Equal(
                Enumerable.Range(1, 40).ToArray(),
                attempts.EnumerateArray().Select(a => a.GetProperty("n").GetInt32()).ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The story's second Gherkin scenario over the wire, including the sweep it asks for: no
    /// attempt's <c>outcome</c> ever carries a verdict-taxonomy word, and the third attempt's is
    /// exactly <c>unmatched</c>.
    /// </summary>
    [Fact]
    public async Task OnTheWire_AttemptOutcomesUseTheThreeValueEnumAndNeverTheVerdictTaxonomy()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(string.Join('\n',
            """{"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":100,"outcome":"FAIL"}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":2,"tMs":300,"outcome":"FAIL"}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":3,"tMs":700,"outcome":"FAIL","observation":{"expected":"orderId","got":null}}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":4,"tMs":1500,"outcome":"PASS"}""",
            """{"type":"step-completed","stepId":"poll-order","verdict":"PASS","durationMs":1500}"""));
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var timeline = await CallAsync(harness, Arguments(entry.RunId, entry.SpecPaths[0], "poll-order"), cts.Token);
            var attempts = timeline.GetProperty("attempts").EnumerateArray().ToArray();

            Assert.Equal("unmatched", attempts.Single(a => a.GetProperty("n").GetInt32() == 3).GetProperty("outcome").GetString());
            Assert.Equal("matched", attempts.Single(a => a.GetProperty("n").GetInt32() == 4).GetProperty("outcome").GetString());

            foreach (var attempt in attempts)
            {
                var outcome = attempt.GetProperty("outcome").GetString()!;
                foreach (var word in VerdictWords)
                {
                    Assert.DoesNotContain(word, outcome, StringComparison.Ordinal);
                }

                // Nor the engine's wire tokens, which get_run_events DOES relay — a host consuming
                // both tools must not find them here.
                Assert.DoesNotContain("PASS", outcome, StringComparison.Ordinal);
                Assert.DoesNotContain("FAIL", outcome, StringComparison.Ordinal);
            }

            // The complement, so this is not passing because the payload says nothing: the STEP's
            // conclusion does name the taxonomy, because a step's conclusion is a verdict.
            Assert.Contains(
                nameof(RunVerdict.Pass), timeline.GetProperty("conclusion").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>The story's third Gherkin scenario over the wire.</summary>
    [Fact]
    public async Task OnTheWire_ASingleAttemptStepReportsVerifyModeOnceWithOneAttempt()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(string.Join('\n',
            """{"type":"step-attempt","stepId":"check-health","attempt":1,"tMs":42,"outcome":"PASS"}""",
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":42}"""));
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var timeline = await CallAsync(harness, Arguments(entry.RunId, entry.SpecPaths[0], "check-health"), cts.Token);

            Assert.Equal("ONCE", timeline.GetProperty("verifyMode").GetString());
            Assert.Equal(1, timeline.GetProperty("attempts").GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// A field this server has no value for reaches the wire as an explicit JSON <c>null</c> — not
    /// omitted, and not filled with a plausible number. Asserted at the wire level because "explicit
    /// null, not absent" is a claim about serialisation, which only the wire settles.
    /// </summary>
    /// <remarks>
    /// <b>Note what this fixture is and is not.</b> Its events carry no <c>ts</c>, so <c>at</c> is null
    /// here — that is a property of THIS fixture, not of the pinned engine, which does emit <c>ts</c> on
    /// every event (measured by <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>; this test class's
    /// name refers to the real MCP wire protocol, never a real engine). The companion below carries a
    /// timestamp and proves the populated case reaches the wire as a string. <c>delayMs</c> and
    /// <c>timeoutMs</c> are the ones that really are null for every input this build can be given.
    /// </remarks>
    [Fact]
    public async Task OnTheWire_AnUnsourcedFieldIsAnExplicitNullRatherThanOmitted()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(RetryTimeline(attempts: 3));
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var timeline = await CallAsync(harness, Arguments(entry.RunId, entry.SpecPaths[0], "poll-order"), cts.Token);

            Assert.True(timeline.TryGetProperty("timeoutMs", out var timeoutMs));
            Assert.Equal(JsonValueKind.Null, timeoutMs.ValueKind);

            foreach (var attempt in timeline.GetProperty("attempts").EnumerateArray())
            {
                Assert.True(attempt.TryGetProperty("at", out var at));
                Assert.Equal(JsonValueKind.Null, at.ValueKind);
                Assert.True(attempt.TryGetProperty("delayMs", out var delayMs));
                Assert.Equal(JsonValueKind.Null, delayMs.ValueKind);

                // The substitute IS present, so the nulls above are an honest absence rather than a
                // timeline with no time in it.
                Assert.True(attempt.GetProperty("tMs").GetInt64() > 0);
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The POPULATED case on the wire: an events file shaped like the pinned engine's own — every line
    /// carrying <c>ts</c> — reaches a host with <c>at</c> as a JSON STRING, not a null and not a
    /// reformatted instant. The fixture line is verbatim from the probe run recorded in
    /// <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>, so this holds the production shape without
    /// needing the engine installed.
    /// </summary>
    [Fact]
    public async Task OnTheWire_AnAttemptTimestampTheEngineEmitted_IsRelayedAsAString()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        const string engineShapedEvents =
            """
            {"v":1,"schemaVersion":"v1","type":"step-attempt","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f92f64205341bead3d1680e4cd8c31","stepId":"poll-order","attempt":1,"tMs":6,"outcome":"FAIL","observation":{"exists":{"expected":true,"actual":false}}}
            {"v":1,"schemaVersion":"v1","type":"step-attempt","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f92f64205341bead3d1680e4cd8c31","stepId":"poll-order","attempt":2,"tMs":5,"outcome":"FAIL","observation":{"exists":{"expected":true,"actual":false}}}
            {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f92f64205341bead3d1680e4cd8c31","stepId":"poll-order","verdict":"INCONCLUSIVE","durationMs":10018,"observation":{"reason":"retry-timeout","attempts":2}}
            """;

        var (registry, path) = GivenRun(engineShapedEvents);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var timeline = await CallAsync(harness, Arguments(entry.RunId, entry.SpecPaths[0], "poll-order"), cts.Token);

            foreach (var attempt in timeline.GetProperty("attempts").EnumerateArray())
            {
                var at = attempt.GetProperty("at");
                Assert.Equal(JsonValueKind.String, at.ValueKind);
                Assert.Equal("2026-09-05T22:21:12.3829238+00:00", at.GetString());
            }

            // Unchanged by the timestamp's presence: these two have no source on any event this
            // build parses, whatever else the line carries.
            Assert.Equal(JsonValueKind.Null, timeline.GetProperty("timeoutMs").ValueKind);
            foreach (var attempt in timeline.GetProperty("attempts").EnumerateArray())
            {
                Assert.Equal(JsonValueKind.Null, attempt.GetProperty("delayMs").ValueKind);
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ASpecPathTheRunNeverCovered_IsRefusedWithVfxE1509()
    {
        await AssertRefusedAsync(
            RetryTimeline(attempts: 2),
            (runId, _) => Arguments(runId, "not-part-of-this-run.e2e.yaml", "poll-order"),
            "VFX-E-1509");
    }

    [Fact]
    public async Task AStepIdTheRunNeverRecorded_IsRefusedWithVfxE1510()
    {
        await AssertRefusedAsync(
            RetryTimeline(attempts: 2),
            (runId, specPath) => Arguments(runId, specPath, "no-such-step"),
            "VFX-E-1510");
    }

    [Fact]
    public async Task AnUnknownRunId_IsRefusedWithVfxE1505()
    {
        await AssertRefusedAsync(
            RetryTimeline(attempts: 2),
            (_, specPath) => Arguments("run-0000000000000000000000000000cafe", specPath, "poll-order"),
            "VFX-E-1505");
    }

    [Fact]
    public async Task ABlankStepId_IsRefusedWithVfxE1006()
    {
        await AssertRefusedAsync(
            RetryTimeline(attempts: 2),
            (runId, specPath) => Arguments(runId, specPath, "   "),
            "VFX-E-1006");
    }

    [Fact]
    public async Task GetStepTimeline_IsAdvertisedAsAReadOnlyTool()
    {
        // US-S3-04's AC: read-only tools stay lock-free, and this annotation is what tells a host it
        // is safe to call concurrently (spec §4.6). RunLockSourceGuardTests holds the other half —
        // that no read-only tool can take the lock at all.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == "get_step_timeline");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task AssertRefusedAsync(
        string eventsFileContent,
        Func<string, string, Dictionary<string, object?>> arguments,
        string expectedCode)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (registry, path) = GivenRun(eventsFileContent);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);
            var entry = registry.ListRuns()[0];

            var result = await harness.Client.CallToolAsync(
                "get_step_timeline", arguments(entry.RunId, entry.SpecPaths[0]), cancellationToken: cts.Token);

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

    /// <summary>A failing RETRY poll loop: every attempt unmatched, and a <c>Fail</c> step verdict.</summary>
    /// <remarks>
    /// Deliberately failing rather than passing: <c>explain_run</c> lists only NON-<c>Pass</c> steps
    /// in <c>notableSteps</c>, so a passing fixture would give the truncation comparison nothing to
    /// compare against.
    /// </remarks>
    private static string RetryTimeline(int attempts)
    {
        const string observation = ",\"observation\":{\"got\":\"PENDING\"}";

        var lines = Enumerable.Range(1, attempts).Select(n =>
            $$"""{"type":"step-attempt","stepId":"poll-order","attempt":{{n}},"tMs":{{n * 100}},"outcome":"FAIL"{{observation}}}""");

        return string.Join(
            '\n',
            lines.Append("""{"type":"step-completed","stepId":"poll-order","verdict":"FAIL","durationMs":9000}"""));
    }

    private static Dictionary<string, object?> Arguments(string? runId, string? specPath, string? stepId) =>
        new(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["specPath"] = specPath,
            ["stepId"] = stepId,
        };

    private static async Task<JsonElement> CallAsync(
        McpTestHarness harness, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(
            "get_step_timeline", arguments, cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false, DescribeFailure(result));
        return result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
    }

    private static string DescribeFailure(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "(no content)";

    /// <summary>A registry holding one completed run whose events file the caller must delete.</summary>
    private static (StubRunRegistry Registry, string Path) GivenRun(string eventsFileContent)
    {
        var path = Path.Combine(Path.GetTempPath(), $"real-get-step-timeline-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, eventsFileContent);
        return (StubRunRegistry.WithCompletedRun(path), path);
    }
}
