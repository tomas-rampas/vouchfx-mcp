using System.Globalization;
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
    /// diagnosis + 10 full proposals + empty guidance + empty spec-edit proposals)
    /// <b>32,423&#160;B</b>, under the 32,768&#160;B budget; full <c>CallToolResult</c> envelope
    /// <b>67,553&#160;B</b> against the 65,536&#160;B cap, i.e. <b>2,017&#160;B over</b>;
    /// envelope-to-bare multiplier <b>2.083</b>.
    /// </para>
    /// <para>
    /// <b>US-S4-03 added 23&#160;B to that candidate</b> (32,400 → 32,423), the cost of an empty
    /// <c>specEditProposals</c> array: this fixture's ten <c>Fail</c> steps can never produce a
    /// spec-edit proposal, which is the story's own partition holding rather than an accident of the
    /// fixture. <b>Headroom is now 345&#160;B</b>, down from 368. The trend across the sprint is the
    /// point — 533 → 368 → 345 — and US-S4-04, which extends the shrink ladder, is where it stops
    /// being absorbed field by field.
    /// </para>
    /// <para>
    /// <b>US-S4-02 moved those numbers, and this is the only place its change is visible to
    /// <c>diagnose_run</c>.</b> The candidate grew <b>32,235&#160;B → 32,400&#160;B (+165&#160;B)</b>
    /// and the envelope <b>67,057&#160;B → 67,497&#160;B (+440&#160;B)</b>, purely by INHERITANCE:
    /// <c>DiagnoseRunResult</c> carries the same <see cref="Diagnosis"/> <c>explain_run</c> returns,
    /// which now also carries <c>classificationHints</c> and a per-item <c>reason</c>. On THIS input
    /// the classification is empty (these ten Fail steps carry observation text with no expected/
    /// observed pair, so the rule table declines to classify them — <b>0 hints</b>), so the whole
    /// +165&#160;B is the empty array plus ten explicit <c>"reason": null</c> fields; a fixture whose
    /// steps DO classify would cost more. Headroom under the budget is now <b>368&#160;B</b>, down
    /// from 533&#160;B — thin, and US-S4-03's own proposals land in this same candidate, so that
    /// story must re-measure rather than assume. The multiplier is LOWER than
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

            // US-S4-03's partition, pinned on the measurement fixture itself: ten Fail steps produce
            // ten review proposals and ZERO spec-edit proposals. This is also what makes the +23 B
            // figure recorded above attributable to the empty array alone.
            Assert.Empty(result.SpecEditProposals);

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

    // ── US-S4-04: the four shrink stages, each MEASURED ─────────────────────────────────────────

    /// <summary>
    /// The HONEST maximal fan-out this sprint asked for: ten notable steps, EVERY one classified,
    /// producing both proposal kinds at once — and its measured baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second maximal fixture rather than reusing the one above.</b> That fixture's ten Fail
    /// steps carry observation text the rule table declines to classify, so it measures a payload
    /// with ZERO reasons, ZERO classification hints and ZERO spec-edit proposals — it understates
    /// Sprint 4's own additions by roughly 6&#160;KB (a peer-review finding). This one carries five
    /// Fail steps with real expected/actual evidence (each yielding an <c>assertion</c> reason AND a
    /// <c>FailProposal</c>) and five Inconclusive RETRY steps with observed values (each yielding a
    /// <c>timeout</c> reason AND both a <c>timeouts</c> and a <c>match</c> spec-edit proposal), plus
    /// environment errors contributing their own reasons and hints.
    /// </para>
    /// <para>
    /// <b>MEASURED (this machine).</b> The numbers are recorded rather than asserted — an events-file
    /// temp path rides in <c>Diagnosis.EventsFilePath</c>, so absolute byte counts move per machine;
    /// the RELATIONSHIPS below are what this test pins. Sprint trend for the older, understated
    /// fixture: candidate 32,235&#160;B (pre-S4) → 32,400 (US-S4-02) → 32,423 (US-S4-03), headroom
    /// 533 → 368 → 345&#160;B. The honest fan-out here is a DIFFERENT input and is not comparable to
    /// that series; its own figures are emitted by this test's failure message when the ladder moves,
    /// and the assertions below fix what must stay true regardless.
    /// </para>
    /// <para>
    /// <b>US-S4-04's own measured baseline — the four stages, each forced by a tuned variant of THIS
    /// fixture and each serialised, never assumed</b> (observation chars / step-id chars ⇒ stage,
    /// measured bytes, Fail proposals, spec-edit proposals):
    /// </para>
    /// <list type="table">
    /// <listheader><term>Fixture</term><description>Stage and measurement</description></listheader>
    /// <item><term>200 / 12</term><description>stage 1 (full) — <b>22,824 B</b>, 5 Fail + 10 spec-edit</description></item>
    /// <item><term>2,400 / 1,800</term><description>stage 2 (bodies elided) — <b>28,979 B</b>, 3 + 6 (3,789 B of headroom)</description></item>
    /// <item><term>2,400 / 100</term><description>stage 3 (rationales elided) — <b>31,564 B</b>, 5 + 10</description></item>
    /// <item><term>2,400 / 250</term><description>stage 4 (lists emptied) — <b>29,578 B</b>, 0 + 0</description></item>
    /// <item><term>100 / 2,000</term><description>stage 4, raw-id residual — <b>26,673 B</b>, 0 + 0</description></item>
    /// <item><term>2,400 / 1,100, 1 poll step, 2 extra seed errors</term><description>stage 3, dedup case — 4 environment-scoped proposals before the ladder, <b>1</b> after</description></item>
    /// </list>
    /// <para>
    /// <b>The stage-2 row moved when this fixture began interleaving its steps</b> (m8): that is the
    /// ONLY fixture below tier 0, so changing which steps survive the tier changed both its size and
    /// its fan-out — 24,362&#160;B / 5+2 became 28,979&#160;B / 3+6. The other four rows reproduce to
    /// the byte. A re-measurement, not a re-derivation: the figures above were read off the running
    /// code, which is the whole discipline this story exists to enforce.
    /// </para>
    /// <para>
    /// Every one is under the 32,768&#160;B budget, and the window case
    /// (<see cref="Stage4Overflow_FallsBackToTheEmergencyMinimalDiagnosis"/>) covers the one input
    /// class that stage 4 itself cannot fit. The stage boundaries sit roughly 1&#160;KB of input
    /// apart (the stage-3 dedup fixture sits mid-band, with ±75 step-id characters ≈ ±1,600&#160;B of
    /// tolerance either side), so the ±50&#160;B a differently-pathed runner contributes cannot move a
    /// fixture across one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DiagnoseAsync_HonestMaximalFanOut_ClassifiesEveryStepAndStaysWithinBudget()
    {
        var result = await DiagnoseAsync(BuildHonestFanOutEvents(observationChars: 200, stepIdChars: 12));

        // Every notable step really is classified — the property the older fixture lacks.
        Assert.Equal(10, result.Diagnosis.NotableSteps.Count);
        Assert.All(result.Diagnosis.NotableSteps, s => Assert.NotNull(s.Reason));
        Assert.NotEmpty(result.Diagnosis.ClassificationHints);

        // ...and both proposal kinds are populated at once.
        Assert.NotEmpty(result.Proposals);
        Assert.NotEmpty(result.SpecEditProposals);

        var measured = SerialisedBytes(result);
        Assert.True(
            measured <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
            $"Honest fan-out measured {measured} B against the "
            + $"{ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes} B budget.");
    }

    /// <summary>
    /// STAGE 2: bodies elided, identities kept — for BOTH lists, at the same boundary. A
    /// <c>FailProposal</c> loses its patch and a <c>SpecEditProposal</c> loses its
    /// <c>suggestedEdit</c>; both keep a capped rationale.
    /// </summary>
    [Fact]
    public async Task Stage2_ElidesBothPatchBodiesAndSuggestedEditBodies_Together()
    {
        // TUNED, by sweeping: large enough that full bodies bust the budget, small enough that
        // eliding them fits. The neighbouring stages sit ~1 KB of input away on either side (see the
        // measured table on the honest-fan-out test), so a differently-pathed runner cannot shift
        // this into a different stage; if it ever does, the assertions below name which stage was
        // reached rather than failing obscurely.
        var result = await DiagnoseAsync(BuildHonestFanOutEvents(observationChars: 2_400, stepIdChars: 1_800));

        Assert.NotEmpty(result.Proposals);
        Assert.NotEmpty(result.SpecEditProposals);

        Assert.All(result.Proposals, p => Assert.Equal(
            "# (patch omitted to fit the diagnose_run response budget; see events file)", p.Patch));
        Assert.All(result.SpecEditProposals, p => Assert.Equal(
            "# (suggested edit omitted to fit the diagnose_run response budget; see events file)", p.SuggestedEdit));

        // Stage 2, not stage 3: the rationales are still the real ones (capped), not the fixed
        // truncation notice, and a spec edit still names its scope.
        Assert.All(result.Proposals, p => Assert.DoesNotContain("truncated for response budget", p.Rationale, StringComparison.Ordinal));
        Assert.All(result.SpecEditProposals, p => Assert.DoesNotContain("truncated for response budget", p.Rationale, StringComparison.Ordinal));
        Assert.All(result.SpecEditProposals, p => Assert.Contains(p.Scope, (IReadOnlySet<string>)SpecEditScopes.All));

        // A STEP-ATTRIBUTED proposal must traverse this stage, not only the null-id environment ones
        // (a review found the fixture proved the rationale-cap path for environment scopes alone).
        // A step-attributed entry is the case where the capped rationale is a real hint rather than
        // an environment-error sentence, and where the id survives while its body does not.
        var stepAttributed = result.SpecEditProposals
            .Where(p => p.StepId is not null && p.Scope == SpecEditScopes.Timeouts)
            .ToList();
        Assert.NotEmpty(stepAttributed);
        Assert.All(stepAttributed, p => Assert.Contains("poll-", p.StepId!, StringComparison.Ordinal));

        // ...and the MATCH scope too — the pair a timeout step produces, both surviving the stage.
        Assert.Contains(result.SpecEditProposals, p => p.Scope == SpecEditScopes.Match);

        // The rationale cap really bites on a step-attributed entry: the match rationale is the
        // longest the builder produces, so it is the one that gets cut to 120 here.
        Assert.All(
            result.SpecEditProposals,
            p => Assert.True(
                p.Rationale.Length <= 120,
                $"Stage 2 must cap every rationale; '{p.Scope}' measured {p.Rationale.Length} characters."));

        AssertWithinBudget(result, "stage 2");
    }

    /// <summary>
    /// STAGE 3: rationales elided too — both lists reduced to identities, with a spec edit keeping
    /// its scope (four characters of closed vocabulary a host can still act on).
    /// </summary>
    [Fact]
    public async Task Stage3_ElidesBothRationales_AndKeepsOnlyIdentitiesAndScope()
    {
        var result = await DiagnoseAsync(BuildHonestFanOutEvents(observationChars: 2_400, stepIdChars: 100));

        Assert.NotEmpty(result.Proposals);
        Assert.NotEmpty(result.SpecEditProposals);

        Assert.All(result.Proposals, p => Assert.Equal(
            "Fail proposal truncated for response budget; see events file.", p.Rationale));
        Assert.All(result.Proposals, p => Assert.Equal("# (omitted)", p.Patch));
        Assert.All(result.SpecEditProposals, p => Assert.Equal(
            "Spec-edit proposal truncated for response budget; see events file.", p.Rationale));
        Assert.All(result.SpecEditProposals, p => Assert.Equal("# (omitted)", p.SuggestedEdit));
        Assert.All(result.SpecEditProposals, p => Assert.Contains(p.Scope, (IReadOnlySet<string>)SpecEditScopes.All));

        AssertWithinBudget(result, "stage 3");
    }

    /// <summary>
    /// STAGE 4: all three lists emptied TOGETHER — and this is also the stage that bounds the raw
    /// step-id residual, since <see cref="SpecEditProposal.StepId"/> is deliberately uncapped and no
    /// amount of BODY elision above touches it.
    /// </summary>
    /// <remarks>
    /// Ledger item (c): ten proposals carrying 2,000-character raw ids are ~20&#160;KB that stages 2
    /// and 3 cannot shed. This test proves the sanctioned fix works — the measured path gets under
    /// budget — and that it gets there by EMPTYING the lists rather than by trimming ids.
    /// </remarks>
    [Fact]
    public async Task Stage4_EmptiesAllThreeListsTogether_AndBoundsTheRawStepIdResidual()
    {
        var result = await DiagnoseAsync(BuildHonestFanOutEvents(observationChars: 2_400, stepIdChars: 250));

        Assert.Empty(result.Proposals);
        Assert.Empty(result.SpecEditProposals);
        Assert.Empty(result.EnvironmentGuidance);

        // Reached stage 4, not the emergency fallback: the diagnosis itself is intact.
        Assert.NotEmpty(result.Diagnosis.NotableSteps);

        // ...and the emptying is a CHOICE the ladder made, not an absence of material — both
        // builders produce proposals from this very diagnosis. Without this, "empty" would be
        // consistent with "there was nothing to say", which is a different claim entirely.
        Assert.NotEmpty(FailProposalBuilder.BuildProposals(result.Diagnosis));
        Assert.NotEmpty(SpecEditProposalBuilder.BuildProposals(result.Diagnosis));

        AssertWithinBudget(result, "stage 4");
    }

    /// <summary>
    /// Stage 3 DEDUPLICATES spec edits by (stepId, scope): once the rationale is a fixed notice and
    /// the body is <c>"# (omitted)"</c>, two entries sharing an id and a scope are byte-identical, and
    /// emitting several copies at the moment bytes are scarcest is pure waste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The environment scope is where this bites: every environment-scoped proposal carries a
    /// <see langword="null"/> step id, so a run with several environment errors produces several
    /// records that stage 3 renders identical. Nothing is lost — what distinguished them was the text
    /// this stage has already elided.
    /// </para>
    /// <para>
    /// <b>The poll-step count is 1 for a measured reason, not for tidiness.</b> A review found the
    /// first version of this test VACUOUS: with five poll steps producing two proposals each, the
    /// builder's <c>MaxProposals</c> cap (10) was reached before the loop ever got to the environment
    /// errors, so ZERO environment-scoped proposals existed at any stage and the assertion collapsed
    /// to a comparison of two empty sets — deleting the deduplication would not have failed it. One
    /// poll step leaves room under the cap, and the assertions below now measure the DROP: several
    /// identical records before the ladder, exactly one after it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stage3_DeduplicatesSpecEditsThatItRendersIdentical()
    {
        var events = BuildHonestFanOutEvents(
            observationChars: 2_400, stepIdChars: 1_100, extraSeedErrors: 2, pollSteps: 1);
        var result = await DiagnoseAsync(events);

        // Stage 3 really is the stage under test.
        Assert.All(result.SpecEditProposals, p => Assert.Equal(
            "Spec-edit proposal truncated for response budget; see events file.", p.Rationale));

        // BEFORE the ladder: several environment-scoped, null-id proposals exist for this diagnosis.
        var beforeLadder = SpecEditProposalBuilder.BuildProposals(result.Diagnosis)
            .Count(p => p.Scope == SpecEditScopes.Environment);
        Assert.Equal(4, beforeLadder);

        // AFTER it: stage 3 renders them byte-identical, so exactly one survives.
        Assert.Single(result.SpecEditProposals, p => p.Scope == SpecEditScopes.Environment);

        AssertWithinBudget(result, "stage 3 (deduplicated)");
    }

    /// <summary>
    /// Ledger item (c), measured end to end: a run whose steps carry 2,000-character ids — the
    /// residual <see cref="SpecEditProposal.StepId"/> deliberately does not cap — still produces a
    /// result within budget, and it gets there by the sanctioned route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ONE bound acts here, and it is this ladder's stage 4.</b> An earlier version of this remark
    /// claimed <c>explain_run</c>'s tiering fired first and starved the proposal lists; that was
    /// measurably false and a review caught it. On this fixture the diagnosis is accepted at TIER 0
    /// intact — ten notable steps, <c>responseTruncated: false</c> — and both builders WOULD produce
    /// their full fan-out from it (5 Fail + 10 spec-edit = 15 proposals carrying 2,000-character
    /// ids). The assertions below prove that material existed rather than inferring it from the
    /// outcome. Stage 4 alone absorbs the residual, which is the STRONGER result for the ladder: it
    /// does not depend on an upstream accident. (What drops the sibling fixture's tier is observation
    /// SIZE, not id length.)
    /// </para>
    /// <para>Measured: <b>26,673 B</b> on this machine, inside budget, with both lists empty.</para>
    /// </remarks>
    [Fact]
    public async Task RawStepIdResidual_IsBoundedByTheLadder_NotByCappingTheCorrelationKey()
    {
        var result = await DiagnoseAsync(BuildHonestFanOutEvents(observationChars: 100, stepIdChars: 2_000));

        // The diagnosis came through the tiering INTACT — no upstream starvation.
        Assert.False(result.Diagnosis.ResponseTruncated);
        Assert.Equal(10, result.Diagnosis.NotableSteps.Count);

        // ...and the material really was there: both builders produce their full fan-out from this
        // very diagnosis, 15 proposals carrying 2,000-character ids between them.
        Assert.Equal(5, FailProposalBuilder.BuildProposals(result.Diagnosis).Count);
        Assert.Equal(10, SpecEditProposalBuilder.BuildProposals(result.Diagnosis).Count);

        // The ladder is what removed them — by emptying the LISTS, never by truncating a correlation
        // key: the diagnosis still carries the full ids it always did.
        Assert.Empty(result.Proposals);
        Assert.Empty(result.SpecEditProposals);
        Assert.Contains(result.Diagnosis.NotableSteps, s => s.StepId.Length > 1_000);

        AssertWithinBudget(result, "raw step-id residual");
    }

    /// <summary>
    /// The window case, driven for real: a diagnosis landing in <c>[32,692..32,768]</c> makes even
    /// stage 4 overflow (the fixed 77-byte wrapper), and the measured ladder falls back to
    /// <c>explain_run</c>'s own emergency-minimal shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The window is SEARCHED FOR at runtime, not hardcoded.</b> It is 77 bytes wide and the
    /// events-file temp path rides inside the diagnosis, so a fixed fixture length would land in it
    /// on one machine and miss on another. The loop below grows one environment-error detail a
    /// character at a time, measuring the REAL diagnosis <c>ExplainRunOrchestrator</c> produces,
    /// until it lands in the window — and asserts it found one, so a failure to reach the case is
    /// reported rather than silently passing.
    /// </para>
    /// <para>
    /// <b>The sweep's own margin, measured.</b> The stride is 2&#160;B per pad character — the padded
    /// detail is counted TWICE in a diagnosis, once in <c>environmentErrors[0].detail</c> and once in
    /// the summary that quotes it — and the window is hit at pads <b>428–465</b> on this machine. The
    /// swept range is deliberately much wider than that band ([300, 700) ≈ 0.2&#160;s), so roughly
    /// ±200&#160;B of per-machine variation — chiefly the temp path riding inside
    /// <c>eventsFilePath</c> — still lands somewhere inside it.
    /// </para>
    /// <para>
    /// US-S4-03 could only bound this stage by arithmetic and said so; this is the measurement that
    /// closes it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Stage4Overflow_FallsBackToTheEmergencyMinimalDiagnosis()
    {
        const int budget = ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes;
        const int wrapperBytes = 77;

        string? windowEvents = null;
        var windowDiagnosisBytes = 0;

        for (var pad = 300; pad < 700 && windowEvents is null; pad++)
        {
            var events = BuildWindowProbeEvents(pad);
            var diagnosis = await ExplainAsync(events);
            var measured = SerialisedBytes(diagnosis);

            if (measured > budget - wrapperBytes && measured <= budget)
            {
                windowEvents = events;
                windowDiagnosisBytes = measured;
            }
        }

        // An explicit branch, not an interpolated Assert.True message: building that message runs two
        // more sweeps AND an Assert.IsType inside ExplainAsync, so on the very path that is supposed
        // to explain a miss it could throw a type-assertion failure instead (a review finding). Here
        // the extra measurements happen ONLY when the sweep actually missed.
        if (windowEvents is null)
        {
            Assert.Fail(
                $"No probe landed the diagnosis in the {wrapperBytes}-byte window "
                + $"[{budget - wrapperBytes + 1}..{budget}]; the window case could not be exercised. "
                + $"Range swept: {SerialisedBytes(await ExplainAsync(BuildWindowProbeEvents(300)))} B at pad 300 to "
                + $"{SerialisedBytes(await ExplainAsync(BuildWindowProbeEvents(699)))} B at pad 699.");
        }

        // Precondition proven: the diagnosis itself fits, so explain_run returned it intact...
        Assert.InRange(windowDiagnosisBytes, budget - wrapperBytes + 1, budget);

        var result = await DiagnoseAsync(windowEvents);

        // ...and yet stage 4 could not fit it, so the ladder fell back to the emergency shape —
        // recognisable by explain_run's own last-resort summary and its emptied collections.
        Assert.Empty(result.Proposals);
        Assert.Empty(result.SpecEditProposals);
        Assert.Empty(result.EnvironmentGuidance);
        Assert.Empty(result.Diagnosis.NotableSteps);
        Assert.Empty(result.Diagnosis.EnvironmentErrors);
        Assert.Empty(result.Diagnosis.ClassificationHints);
        Assert.Contains("too large to return with per-step detail", result.Diagnosis.Summary, StringComparison.Ordinal);
        Assert.True(result.Diagnosis.ResponseTruncated);

        AssertWithinBudget(result, "stage 4 overflow fallback");
    }

    private static void AssertWithinBudget(DiagnoseRunResult result, string stage)
    {
        var measured = SerialisedBytes(result);
        Assert.True(
            measured <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
            $"{stage} measured {measured} B against the "
            + $"{ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes} B budget.");
    }

    private static readonly JsonSerializerOptions LadderProbeOptions = new(JsonSerializerDefaults.Web);

    private static int SerialisedBytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, LadderProbeOptions).Length;

    private static async Task<Vouchfx.Mcp.Diagnosis.Diagnosis> ExplainAsync(string events)
    {
        var path = WriteTempEventsFile(events);
        try
        {
            var outcome = await new ExplainRunOrchestrator(new InMemoryRunRegistry())
                .ExplainAsync(path, CancellationToken.None);
            return Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome).Diagnosis;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Ten notable steps, all classified: five Fail (assertion evidence ⇒ a reason AND a Fail
    /// proposal) and five Inconclusive RETRY steps with observed values (⇒ a timeout reason AND both
    /// spec-edit scopes), plus two environment errors carrying their own reasons.
    /// </summary>
    private static string BuildHonestFanOutEvents(
        int observationChars,
        int stepIdChars,
        int extraSeedErrors = 0,
        int pollSteps = 5)
    {
        var padding = new string('x', observationChars);
        var idPadding = new string('i', Math.Max(0, stepIdChars - 12));
        var events = new StringBuilder();

        // INTERLEAVED, deliberately: notable steps are kept in file order, so grouping all five Fail
        // steps first would let every tier below tier 0 drop the Inconclusive ones wholesale — and a
        // fixture whose spec-edit proposals vanish the moment the ladder engages cannot exercise the
        // stages it exists to test (a review found exactly that: stage 2 was only ever proven for
        // null-id environment proposals). Alternating keeps both kinds represented at every tier.
        for (var i = 0; i < 5; i++)
        {
            var failStepId = $"check-{i:D2}-{idPadding}";
            events.Append(JsonSerializer.Serialize(new
            {
                type = "step-completed",
                stepId = failStepId,
                verdict = "FAIL",
                durationMs = 120,
                observation = new { expected = $"E{i}", actual = $"A{i}", note = padding },
            })).Append('\n');

            if (i >= pollSteps)
            {
                continue;
            }

            var pollStepId = $"poll-{i:D2}-{idPadding}";
            events.Append(JsonSerializer.Serialize(new
            {
                type = "step-attempt",
                stepId = pollStepId,
                attempt = 1,
                tMs = 100,
                outcome = "FAIL",
                observation = new { matched = false, note = padding },
            })).Append('\n');
            events.Append(JsonSerializer.Serialize(new
            {
                type = "step-completed",
                stepId = pollStepId,
                verdict = "INCONCLUSIVE",
                durationMs = 1300,
                observation = new { reason = "retry-timeout", note = padding },
            })).Append('\n');
        }

        events.Append("""{"type":"environment-error","errorKind":"HealthGate","resourceName":"events","detail":"health gate timed out after 30000ms"}""").Append('\n');
        events.Append("""{"type":"environment-error","errorKind":"Seed","resourceName":"orders-db","detail":"relation orders does not exist"}""").Append('\n');

        // Extra seed errors, for the stage-3 dedup case: each yields another environment-scoped,
        // null-id proposal that stage 3 renders byte-identical to its siblings.
        for (var i = 0; i < extraSeedErrors; i++)
        {
            events.Append(CultureInfo.InvariantCulture, $$"""{"type":"environment-error","errorKind":"Seed","resourceName":"orders-db-extra-{{i}}","detail":"relation orders does not exist"}""").Append('\n');
        }

        events.Append("""{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""");

        return events.ToString();
    }

    /// <summary>
    /// A fixture whose diagnosis size can be tuned ONE BYTE at a time, for the window search: the
    /// bulk comes from environment-error labels (which no tier trims), and <paramref name="pad"/>
    /// grows one detail character by character.
    /// </summary>
    private static string BuildWindowProbeEvents(int pad)
    {
        var hugeLabel = new string('L', 3_000);
        var events = new StringBuilder();

        for (var i = 0; i < 4; i++)
        {
            events.Append(JsonSerializer.Serialize(new
            {
                type = "step-completed",
                stepId = new string('s', 1_900) + i,
                verdict = "INCONCLUSIVE",
                durationMs = 10,
            })).Append('\n');
        }

        for (var i = 0; i < 4; i++)
        {
            events.Append(JsonSerializer.Serialize(new
            {
                type = "environment-error",
                errorKind = hugeLabel,
                resourceName = hugeLabel,
                detail = i == 0 ? hugeLabel[..(1_500 + pad)] : hugeLabel,
            })).Append('\n');
        }

        events.Append("""{"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}""");
        return events.ToString();
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
