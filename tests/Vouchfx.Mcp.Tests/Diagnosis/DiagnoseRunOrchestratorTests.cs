using System.Text.Json;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Diagnosis;

/// <summary>
/// Covers <see cref="DiagnoseRunOrchestrator"/> / Spec C (M2 Healer) against synthetic JSONL
/// events files — Fail proposals, EnvironmentError guidance-only, Pass/Inconclusive empty patches,
/// and error-path parity with <c>explain_run</c>. No CLI or Docker dependency.
/// </summary>
public class DiagnoseRunOrchestratorTests
{
    [Fact]
    public async Task DiagnoseAsync_FailWithObservation_ReturnsAtLeastOneProposalWithNonEmptyPatch()
    {
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "assert-order-status",
            verdict = "FAIL",
            durationMs = 120,
            observation = new { column = "status", expected = "SHIPPED", actual = "PENDING" },
        }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

        var result = await DiagnoseAsync(events);

        Assert.Equal("Fail", result.Diagnosis.Verdict);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("assert-order-status", proposal.StepId);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Rationale));
        Assert.Contains("SHIPPED", proposal.Rationale, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Patch));
        Assert.Contains("assert-order-status", proposal.Patch, StringComparison.Ordinal);
        Assert.Contains("--- a/", proposal.Patch, StringComparison.Ordinal);
        Assert.Contains("do not auto-apply", proposal.Patch, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.EnvironmentGuidance);
    }

    [Fact]
    public async Task DiagnoseAsync_EnvironmentError_ReturnsZeroProposalsAndNonEmptyGuidance()
    {
        const string events = """
            {"type":"environment-error","errorKind":"Provision","resourceName":"docker-daemon","detail":"Cannot connect to the Docker daemon"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var result = await DiagnoseAsync(events);

        Assert.Equal("EnvironmentError", result.Diagnosis.Verdict);
        Assert.Empty(result.Proposals);
        Assert.NotEmpty(result.EnvironmentGuidance);
        Assert.Contains(
            result.EnvironmentGuidance,
            line => line.Contains("docker-daemon", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("not a test defect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnoseAsync_Pass_ReturnsZeroProposals()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        var result = await DiagnoseAsync(events);

        Assert.Equal("Pass", result.Diagnosis.Verdict);
        Assert.Empty(result.Proposals);
        Assert.Empty(result.EnvironmentGuidance);
        Assert.Contains("passed", result.Diagnosis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnoseAsync_Inconclusive_ReturnsZeroPatches()
    {
        const string events = """
            {"type":"step-attempt","stepId":"poll-order-status","attempt":1,"tMs":100}
            {"type":"step-attempt","stepId":"poll-order-status","attempt":2,"tMs":300}
            {"type":"step-completed","stepId":"poll-order-status","verdict":"INCONCLUSIVE","durationMs":900}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var result = await DiagnoseAsync(events);

        Assert.Equal("Inconclusive", result.Diagnosis.Verdict);
        Assert.Empty(result.Proposals);
        // Guidance is allowed for Inconclusive, but must not be suite-rewrite patches.
        Assert.All(result.EnvironmentGuidance, line =>
            Assert.DoesNotContain("--- a/", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_FailWithoutObservation_EmitsNoProposal()
    {
        const string events = """
            {"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;

        var result = await DiagnoseAsync(events);

        Assert.Equal("Fail", result.Diagnosis.Verdict);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public async Task DiagnoseAsync_MixedFailAndEnvironmentError_ProposalsOnlyForFailSteps()
    {
        // Overall elevates to EnvironmentError (§12.1), but Fail steps with observation still
        // get review proposals (EDGE-003); env errors get guidance without patches.
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "assert-order-status",
            verdict = "FAIL",
            durationMs = 80,
            observation = new { expected = "ok", actual = "bad" },
        }) + "\n" + """
            {"type":"environment-error","errorKind":"Provision","resourceName":"orders-db","detail":"container exited unexpectedly"}
            """;

        var result = await DiagnoseAsync(events);

        Assert.Equal("EnvironmentError", result.Diagnosis.Verdict);
        var proposal = Assert.Single(result.Proposals);
        Assert.Equal("assert-order-status", proposal.StepId);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Patch));
        Assert.NotEmpty(result.EnvironmentGuidance);
        Assert.Contains(result.EnvironmentGuidance, g => g.Contains("orders-db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_NoEventsPathAndNoPriorRun_ReturnsCleanError()
    {
        var orchestrator = CreateOrchestrator(new LastRunTracker());

        var outcome = await orchestrator.DiagnoseAsync(null, CancellationToken.None);

        var noRun = Assert.IsType<DiagnoseRunOutcome.NoRunToExplain>(outcome);
        Assert.Contains("run_suite", noRun.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnoseAsync_MissingFile_ReturnsEventsFileNotFound()
    {
        var orchestrator = CreateOrchestrator(new LastRunTracker());
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jsonl");

        var outcome = await orchestrator.DiagnoseAsync(missingPath, CancellationToken.None);

        Assert.IsType<DiagnoseRunOutcome.EventsFileNotFound>(outcome);
    }

    [Fact]
    public async Task DiagnoseAsync_UncPath_ReturnsInvalidPath()
    {
        var orchestrator = CreateOrchestrator(new LastRunTracker());

        var outcome = await orchestrator.DiagnoseAsync(@"\\attacker-host\share\events.jsonl", CancellationToken.None);

        Assert.IsType<DiagnoseRunOutcome.InvalidPath>(outcome);
    }

    [Fact]
    public async Task DiagnoseAsync_DefaultsToLastRunTrackerWhenEventsPathOmitted()
    {
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "assert-x",
            verdict = "FAIL",
            durationMs = 10,
            observation = new { note = "mismatch" },
        }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

        var path = WriteTempEventsFile(events);
        try
        {
            var tracker = new LastRunTracker();
            tracker.RecordRun(path, "Fail");
            var orchestrator = CreateOrchestrator(tracker);

            var outcome = await orchestrator.DiagnoseAsync(null, CancellationToken.None);

            var diagnosed = Assert.IsType<DiagnoseRunOutcome.Diagnosed>(outcome);
            Assert.Equal("Fail", diagnosed.Result.Diagnosis.Verdict);
            Assert.NotEmpty(diagnosed.Result.Proposals);
            Assert.Equal(path, diagnosed.Result.Diagnosis.EventsFilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DiagnoseAsync_DoesNotWriteSuiteFiles()
    {
        // REQ-005: read-only — only temp events files for reading; no modified suite on disk.
        var tempDir = Path.Combine(Path.GetTempPath(), $"diagnose-ro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var suitePath = Path.Combine(tempDir, "suite.e2e.yaml");
            await File.WriteAllTextAsync(suitePath, "steps: []\n");
            var before = await File.ReadAllTextAsync(suitePath);

            var events = JsonSerializer.Serialize(new
            {
                type = "step-completed",
                stepId = "s1",
                verdict = "FAIL",
                durationMs = 1,
                observation = new { x = 1 },
            }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

            _ = await DiagnoseAsync(events);

            Assert.Equal(before, await File.ReadAllTextAsync(suitePath));
            Assert.Equal(
                ["suite.e2e.yaml"],
                Directory.GetFiles(tempDir).Select(f => Path.GetFileName(f)!).ToArray());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task<DiagnoseRunResult> DiagnoseAsync(string events)
    {
        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = CreateOrchestrator(new LastRunTracker());
            var outcome = await orchestrator.DiagnoseAsync(path, CancellationToken.None);
            return Assert.IsType<DiagnoseRunOutcome.Diagnosed>(outcome).Result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DiagnoseRunOrchestrator CreateOrchestrator(ILastRunTracker tracker) =>
        new(new ExplainRunOrchestrator(tracker));

    private static string WriteTempEventsFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diagnose-run-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
