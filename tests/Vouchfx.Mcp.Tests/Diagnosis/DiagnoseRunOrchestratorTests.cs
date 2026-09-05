using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tools;

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
        var orchestrator = CreateOrchestrator(new InMemoryRunRegistry());

        var outcome = await orchestrator.DiagnoseAsync(null, CancellationToken.None);

        var noRun = Assert.IsType<DiagnoseRunOutcome.NoRunToExplain>(outcome);
        Assert.Contains("run_suite", noRun.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnoseAsync_MissingFile_ReturnsEventsFileNotFound()
    {
        var orchestrator = CreateOrchestrator(new InMemoryRunRegistry());
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jsonl");

        var outcome = await orchestrator.DiagnoseAsync(missingPath, CancellationToken.None);

        Assert.IsType<DiagnoseRunOutcome.EventsFileNotFound>(outcome);
    }

    [Fact]
    public async Task DiagnoseAsync_UncPath_ReturnsInvalidPath()
    {
        var orchestrator = CreateOrchestrator(new InMemoryRunRegistry());

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
            var registry = StubRunRegistry.WithCompletedRun(path, "Fail");
            var orchestrator = CreateOrchestrator(registry);

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

    /// <summary>
    /// The <c>diagnose_run</c> counterpart to
    /// <c>ExplainRunOrchestratorTests.MaximalTierZeroDiagnosis_FitsTheBudgetButItsEnvelopeExceedsTheCap</c>
    /// — the Sprint-4 re-budget baseline for THIS tool, not just <c>explain_run</c>. Both tools reuse
    /// the identical <see cref="ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes"/> /
    /// <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/> constants, but
    /// <see cref="DiagnoseRunOrchestrator"/> budgets a LARGER candidate against that same 32,768&#160;B
    /// half-budget — the diagnosis PLUS Fail proposals PLUS environment guidance, all three — so its
    /// own maximal stage-0 shape, and its own envelope-to-bare multiplier, were never measured before
    /// this test. MINOR-2 from the Sprint-1 close review.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Construction, and why it differs from <c>explain_run</c>'s sweep:</b> ten Fail steps (the
    /// most <see cref="FailProposalBuilder.MaxProposals"/> allows, and also
    /// <c>ExplainRunOrchestrator</c>'s own tier-0 notable-step cap) each carry a step-completion
    /// observation of 1,100 'x' characters, with NO step-attempt events. Attempts were swept too and
    /// discarded from this construction: an attempt's observation feeds only the bare
    /// <see cref="Diagnosis"/> (via <c>StepAttemptDiagnosis</c>) and is never read by
    /// <see cref="FailProposalBuilder.BuildProposals"/>, so every byte spent on attempts competes with
    /// the SAME 32,768&#160;B budget as the step observations that also drive the ten proposals'
    /// rationale and patch text — pure ballast for this test's purpose. The step observation is 1,100
    /// chars rather than <see cref="FailProposalBuilder.MaxRationaleChars"/> (500, the point past
    /// which a proposal's own rationale/patch text stops growing) precisely because that extra length
    /// still grows the BARE <see cref="Diagnosis"/> copy of the same observation (capped separately, at
    /// 2,000 chars, by explain_run's own tier 0) even once the proposal text has saturated — sweeping
    /// confirmed the true one-byte-precision boundary sits at 1,153 chars, but 1,100 is used instead
    /// for the same reason explain_run's own sweep did not pin an exact byte boundary either: an
    /// events-file temp path (embedded verbatim in <see cref="Diagnosis.EventsFilePath"/>) is
    /// machine- and username-length-dependent, so a boundary a few bytes wide would flip stage on a
    /// differently-pathed CI runner. 1,100 chars leaves 533&#160;B of headroom under the 32,768&#160;B
    /// budget — the same order of magnitude as explain_run's own 539&#160;B headroom at its tier-0
    /// boundary — deliberately, not by coincidence.
    /// </para>
    /// <para>
    /// <b>MEASURED on this input</b> (this machine): bare candidate (<see cref="DiagnoseRunResult"/> —
    /// diagnosis + 10 full proposals + empty guidance) <b>32,235&#160;B</b>, under the 32,768&#160;B
    /// budget; full <c>CallToolResult</c> envelope <b>67,057&#160;B</b> against the 65,536&#160;B cap,
    /// i.e. <b>1,521&#160;B over</b>; envelope-to-bare multiplier <b>2.080</b>. This is LOWER than
    /// explain_run's measured 2.213, not higher, despite the proposals' unified-diff patch text
    /// carrying denser quote/backslash escaping per byte than a plain observation string: the ten
    /// proposals' rationale and patch text is capped at
    /// <see cref="FailProposalBuilder.MaxRationaleChars"/>/<see cref="FailProposalBuilder.MaxPatchChars"/>
    /// regardless of how large the source observation is, so at this input's size a smaller SHARE of
    /// the bare candidate is escaping-dense text than in explain_run's all-observation payload — the
    /// escaping density is real, but proposal capping dilutes rather than dominates it here. As with
    /// explain_run, the absolute byte counts move with the temp path's length (part of
    /// <see cref="Diagnosis.EventsFilePath"/>) and are not asserted directly; the RELATIONSHIPS —
    /// stage 0 accepted, bare candidate under budget, envelope over the cap, multiplier above 2.0 —
    /// are machine-independent and are what this test pins.
    /// </para>
    /// <para>
    /// <b>Not fixed here, deliberately</b> — same Sprint-4 resourceUri hand-off rationale as
    /// <see cref="ExplainRunOrchestrator.MaxDiagnosisResponseBytes"/>'s own remarks; this test only
    /// records the baseline. No production budget constant or behaviour changes with this fix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DiagnoseAsync_MaximalStageZeroInput_FitsTheDiagnoseBudgetButItsEnvelopeExceedsTheCap()
    {
        var stepObservation = new string('x', 1_100);
        var events = new StringBuilder();

        for (var step = 0; step < 10; step++)
        {
            events.Append(JsonSerializer.Serialize(new
            {
                type = "step-completed",
                stepId = $"assert-step-{step:D2}",
                verdict = "FAIL",
                durationMs = 1234,
                observation = stepObservation,
            })).Append('\n');
        }

        events.Append("""{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""");

        var path = WriteTempEventsFile(events.ToString());
        try
        {
            var orchestrator = CreateOrchestrator(new InMemoryRunRegistry());
            var outcome = await orchestrator.DiagnoseAsync(path, CancellationToken.None);
            var result = Assert.IsType<DiagnoseRunOutcome.Diagnosed>(outcome).Result;

            // 1. Stage 0 (BuildResult's un-shrunk candidate) was genuinely selected: the bare
            // diagnosis itself isn't truncated, all ten Fail steps got a proposal, and every proposal
            // still carries its FULL unified-diff patch — none of the three shrink stages fired.
            Assert.False(result.Diagnosis.ResponseTruncated);
            Assert.Equal(10, result.Diagnosis.NotableSteps.Count);
            Assert.Equal(10, result.Proposals.Count);
            Assert.All(result.Proposals, p => Assert.Contains("--- a/", p.Patch, StringComparison.Ordinal));
            Assert.All(result.Proposals, p => Assert.DoesNotContain("omitted", p.Patch, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(result.EnvironmentGuidance);

            // 2. The bare candidate satisfies the SAME budget BuildResult actually measures against
            // (diagnosis + proposals + guidance combined, not just the diagnosis).
            var probeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var candidateBytes = JsonSerializer.SerializeToUtf8Bytes(result, probeOptions).Length;
            Assert.True(
                candidateBytes <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
                $"Expected the bare diagnose_run candidate within the "
                + $"{ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes}-byte budget, got {candidateBytes}.");

            // 3. ...and yet the real envelope still busts the public cap, exactly like explain_run's
            // own documented breach.
            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                StructuredToolResult.Success(result), probeOptions).Length;
            Assert.True(
                envelopeBytes > ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
                $"Expected the envelope to still exceed the {ExplainRunOrchestrator.MaxDiagnosisResponseBytes}-byte "
                + $"cap (the documented, Sprint-4-owned breach), got {envelopeBytes}. If this now FITS, the budget "
                + "was fixed -- update this test's remarks together with the fix.");

            // 4. The multiplier the /2 budget assumes (2.0) is not the real one here either.
            Assert.True(
                (double)envelopeBytes / candidateBytes > 2.0,
                $"Expected the envelope-to-bare multiplier above 2.0 (escaping overhead), got "
                + $"{(double)envelopeBytes / candidateBytes:F3}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<DiagnoseRunResult> DiagnoseAsync(string events)
    {
        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = CreateOrchestrator(new InMemoryRunRegistry());
            var outcome = await orchestrator.DiagnoseAsync(path, CancellationToken.None);
            return Assert.IsType<DiagnoseRunOutcome.Diagnosed>(outcome).Result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static DiagnoseRunOrchestrator CreateOrchestrator(IRunRegistry registry) =>
        new(new ExplainRunOrchestrator(registry));

    private static string WriteTempEventsFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"diagnose-run-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
