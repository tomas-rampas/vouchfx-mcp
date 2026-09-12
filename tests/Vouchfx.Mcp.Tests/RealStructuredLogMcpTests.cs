using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S6-05's wire-level assertions: the run-lifecycle log records are real JSON on stderr, their
/// <c>runId</c> is the one <c>get_run_status</c> reports for the same run, and stdout stays empty.
/// </summary>
/// <remarks>
/// Joins <see cref="SpanAssertionGroup"/> because <see cref="ConsoleErrorCapture"/> and
/// <see cref="ConsoleOutCapture"/> both redirect PROCESS-global streams.
/// </remarks>
[Collection(SpanAssertionGroup.Name)]
public class RealStructuredLogMcpTests
{
    /// <summary>
    /// The captured records belonging to <paramref name="runId"/>, in capture order.
    /// </summary>
    /// <remarks>
    /// Filtering by the run's OWN id rather than by "has a runId field" is what makes these
    /// assertions immune to a concurrent server's records appearing in the same process-global
    /// capture — the same scoping the span tests get from <c>workspace.hash</c>. The class's
    /// collection already serialises it against everything else (see <see cref="SpanAssertionGroup"/>),
    /// so this is belt and braces; it is cheap, and it makes each assertion state which run it is
    /// about instead of relying on the reader knowing only one ran.
    /// </remarks>
    private static JsonElement[] RecordsForRun(ConsoleErrorCapture stderr, string runId) =>
        stderr.JsonLines()
            .Where(record =>
                record.TryGetProperty("runId", out var recorded) && recorded.GetString() == runId)
            .ToArray();

    private const string PassingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    private const string ValidSuiteYaml = """
        steps:
          - id: fine
            type: http.rest
            target: api
            method: GET
            path: /x
        """;

    /// <summary>
    /// AC4, the correlation proof: the runId on a log record emitted during the run equals the runId
    /// <c>get_run_status</c> reports for that same run.
    /// </summary>
    /// <remarks>
    /// This is the AC that had no carrier before this story — measured at the premise check, all
    /// four pre-existing log sites fired before <c>host.RunAsync()</c>, so no log line could ever
    /// coincide with a run. The two run-lifecycle records added here are what make it provable, and
    /// asserting against <c>get_run_status</c>'s own answer rather than against the tool result's
    /// runId is deliberate: it is the registry read the AC names, so a divergence between the log's
    /// id and the registry's would fail here rather than pass on a shared source.
    /// </remarks>
    [Fact]
    public async Task ALogRecordsRunId_EqualsTheRunIdGetRunStatusReportsForTheSameRun()
    {
        using var stderr = new ConsoleErrorCapture();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "passing.e2e.yaml");
        await File.WriteAllTextAsync(suitePath, ValidSuiteYaml.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        var runId = Assert.IsType<JsonElement>(runResult.StructuredContent).GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));

        // The registry's own answer, through the tool the AC names.
        var statusResult = await harness.Client.CallToolAsync(
            "get_run_status",
            new Dictionary<string, object?> { ["runId"] = runId },
            cancellationToken: cts.Token);

        // get_run_status serialises the registry ENTRY under a "run" property rather than projecting
        // its fields to the top level (US-S3-03's "not a second, divergent status model"), so the id
        // lives one level down — reading it from the entry itself is what makes this the registry's
        // own answer rather than a reshaped copy.
        var reportedRunId = Assert.IsType<JsonElement>(statusResult.StructuredContent)
            .GetProperty("run").GetProperty("runId").GetString();

        var runScopedRecords = RecordsForRun(stderr, runId!);

        Assert.NotEmpty(runScopedRecords);

        foreach (var record in runScopedRecords)
        {
            Assert.Equal(reportedRunId, record.GetProperty("runId").GetString());
            Assert.True(RunRegistryCore.IsWellFormedRunId(record.GetProperty("runId").GetString()));
        }
    }

    [Fact]
    public async Task ARunEmitsAStartedAndACompletedRecord_TheCompletedOneCarryingTheTaxonomyVerdict()
    {
        using var stderr = new ConsoleErrorCapture();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "passing.e2e.yaml");
        await File.WriteAllTextAsync(suitePath, ValidSuiteYaml.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        var runId = Assert.IsType<JsonElement>(runResult.StructuredContent).GetProperty("runId").GetString();

        var messages = RecordsForRun(stderr, runId!)
            .Select(record => record.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(messages, m => m.Contains("run started", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("run completed", StringComparison.Ordinal));

        // The started record names the suite COUNT — the one piece of scale information available
        // without naming a caller-supplied path.
        var started = Assert.Single(messages, m => m.Contains("run started", StringComparison.Ordinal));
        Assert.Contains("1 suite(s)", started, StringComparison.Ordinal);

        // The TAXONOMY verdict, never a wire token — CLAUDE.md's hard invariant. PASS/FAIL/ENV_ERROR/
        // INCONCLUSIVE are the engine's wire vocabulary and must not surface here.
        var completed = Assert.Single(messages, m => m.Contains("run completed", StringComparison.Ordinal));

        Assert.Contains("Pass", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("ENV_ERROR", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("INCONCLUSIVE", completed, StringComparison.Ordinal);

        // And the duration, which is the other half of what makes this record operationally useful —
        // a verdict with no wall-clock tells an operator nothing about a slow run.
        Assert.Matches(@"\d+ ms", completed);
    }

    [Fact]
    public async Task TheSeqOrdinal_IncreasesAcrossARunsRecords()
    {
        using var stderr = new ConsoleErrorCapture();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "passing.e2e.yaml");
        await File.WriteAllTextAsync(suitePath, ValidSuiteYaml.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        var runId = Assert.IsType<JsonElement>(runResult.StructuredContent).GetProperty("runId").GetString();

        var seqs = RecordsForRun(stderr, runId!)
            .Select(record => record.GetProperty("seq").GetInt32())
            .ToArray();

        Assert.NotEmpty(seqs);
        Assert.Equal(seqs.OrderBy(s => s).ToArray(), seqs);
        Assert.Equal(1, seqs[0]);
    }

    [Fact]
    public async Task EveryStderrLineDuringARun_IsASingleJsonObject_AndStdoutStaysByteEmpty()
    {
        // Gherkin 1, at the harness seam. RealServerProcessTests covers the same invariant against
        // the real spawned process over real stdio; this covers it with the structured logging
        // ACTIVE and a run in flight, which is the combination the AC names.
        using var consoleOut = new ConsoleOutCapture();
        using var stderr = new ConsoleErrorCapture();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "passing.e2e.yaml");
        await File.WriteAllTextAsync(suitePath, ValidSuiteYaml.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        // JsonLines() fails the test naming any line that is not a JSON object.
        Assert.NotEmpty(stderr.JsonLines());
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NoLogRecord_CarriesTheSuitesContentOrAnyPathBeyondWhatTheMessageNames()
    {
        // Gherkin 3's shape, with a canary in the suite: a record must never carry suite text.
        const string canary = "CANARY-DO-NOT-LEAK";

        using var stderr = new ConsoleErrorCapture();
        using var temp = new TempWorkspace();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Directory.CreateDirectory(temp.Workspace.SpecsDir);
        var suitePath = Path.Combine(temp.Workspace.SpecsDir, "canary.e2e.yaml");
        var canarySuite = $"""
            steps:
              - id: canary-step
                type: http.rest
                target: api
                method: GET
                path: /{canary}
            """;
        await File.WriteAllTextAsync(suitePath, canarySuite.ReplaceLineEndings("\n"), cts.Token);

        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0),
            workspace: temp.Workspace);

        var runResult = await harness.Client.CallToolAsync(
            "run_suite",
            new Dictionary<string, object?> { ["path"] = suitePath },
            cancellationToken: cts.Token);

        var runId = Assert.IsType<JsonElement>(runResult.StructuredContent).GetProperty("runId").GetString();

        // Hygiene is asserted over EVERY captured record, not just this run's — a leak through a
        // startup banner or another component's record would be exactly as real.
        var records = stderr.JsonLines();

        // Anti-vacuity: without a run-scoped record present this would pass with logging removed.
        Assert.NotEmpty(RecordsForRun(stderr, runId!));

        foreach (var record in records)
        {
            foreach (var field in record.EnumerateObject())
            {
                var value = field.Value.ToString();

                Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
                Assert.DoesNotContain(temp.Workspace.Root, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
