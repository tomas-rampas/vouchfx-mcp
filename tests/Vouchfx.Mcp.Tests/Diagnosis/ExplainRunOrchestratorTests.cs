using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tools;

// This test file's own namespace (Vouchfx.Mcp.Tests.Diagnosis) shares a leaf segment with the
// production type Vouchfx.Mcp.Diagnosis.Diagnosis — an unqualified "Diagnosis" inside this file
// resolves to the enclosing NAMESPACE segment, not the imported TYPE, so every reference to the
// type below goes via this alias instead.
using DiagnosisResult = Vouchfx.Mcp.Diagnosis.Diagnosis;

namespace Vouchfx.Mcp.Tests.Diagnosis;

/// <summary>
/// Covers <see cref="ExplainRunOrchestrator"/> — REQ-007's pure read + parse + diagnose pipeline —
/// entirely against synthetic events files built in-test, so nothing here depends on the real
/// <c>vouchfx</c> CLI or Docker being installed, and no suite is ever actually run.
/// </summary>
public class ExplainRunOrchestratorTests
{
    private static readonly JsonSerializerOptions ResponseSizeProbeOptions = new(JsonSerializerDefaults.Web);

    // ── The four verdicts stay distinct in the diagnosis ────────────────────────────────────────

    [Fact]
    public async Task ExplainAsync_FailingSample_NamesTheFailingStepAndTheMismatch()
    {
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "assert-order-status",
            verdict = "FAIL",
            durationMs = 120,
            observation = new { column = "status", expected = "SHIPPED", actual = "PENDING" },
        }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("Fail", diagnosis.Verdict);
        Assert.Contains("genuine defect", diagnosis.CategoryMeaning, StringComparison.OrdinalIgnoreCase);
        var step = Assert.Single(diagnosis.NotableSteps);
        Assert.Equal("assert-order-status", step.StepId);
        Assert.Equal("Fail", step.Verdict);
        Assert.NotNull(step.Observation);
        Assert.Contains("SHIPPED", step.Observation);
        Assert.Contains("PENDING", step.Observation);
        Assert.Contains("assert-order-status", diagnosis.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainAsync_EnvironmentErrorSample_ReportsInfrastructureProblemNeverAsFail()
    {
        var events = """
            {"type":"environment-error","errorKind":"Provision","resourceName":"docker-daemon","detail":"Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"ENV_ERROR"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("EnvironmentError", diagnosis.Verdict);
        Assert.NotEqual("Fail", diagnosis.Verdict);
        Assert.Contains("NOT a test defect", diagnosis.CategoryMeaning, StringComparison.Ordinal);
        Assert.Contains("no test defect is implied", diagnosis.Summary, StringComparison.OrdinalIgnoreCase);
        var error = Assert.Single(diagnosis.EnvironmentErrors);
        Assert.Equal("docker-daemon", error.ResourceName);
        Assert.Contains("docker", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplainAsync_EnvironmentErrorWithNoScenarioEvent_StillClassifiesAsEnvironmentErrorViaStepFallback()
    {
        // No scenario-completed event at all -- ComputeEffectiveVerdict must fall through to
        // "at least one environment-error event exists" rather than reporting NoRecognisableEvents.
        const string events = """{"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"timed out waiting for healthy"}""";

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("EnvironmentError", diagnosis.Verdict);
    }

    [Fact]
    public async Task ExplainAsync_FailStepPlusEnvironmentErrorAndNoScenarioCompleted_ClassifiesAsEnvironmentErrorNeverFail()
    {
        // A review-found taxonomy BLOCKER: a realistic abort sequence (a step fails, then the
        // container running it dies, aborting the run before scenario-completed is ever emitted)
        // must classify as EnvironmentError per §12.1 precedence (EnvironmentError > Fail) -- an
        // earlier version of ComputeEffectiveVerdict returned the step-elevated Fail result WITHOUT
        // ever consulting EnvironmentErrors, misclassifying exactly this shape as a product defect.
        const string events = """
            {"type":"step-completed","stepId":"assert-order-status","verdict":"FAIL","durationMs":80}
            {"type":"environment-error","errorKind":"Provision","resourceName":"orders-db","detail":"container exited unexpectedly"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("EnvironmentError", diagnosis.Verdict);
        Assert.NotEqual("Fail", diagnosis.Verdict);
        Assert.Contains("no test defect is implied", diagnosis.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT a test defect", diagnosis.CategoryMeaning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainAsync_InconclusiveSample_SurfacesTheRetryAttemptTimeline()
    {
        const string events = """
            {"type":"step-attempt","stepId":"poll-order-status","attempt":1,"tMs":100}
            {"type":"step-attempt","stepId":"poll-order-status","attempt":2,"tMs":300}
            {"type":"step-attempt","stepId":"poll-order-status","attempt":3,"tMs":900,"outcome":"INCONCLUSIVE"}
            {"type":"step-completed","stepId":"poll-order-status","verdict":"INCONCLUSIVE","durationMs":900}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"INCONCLUSIVE"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("Inconclusive", diagnosis.Verdict);
        Assert.Contains("Neither a pass nor a defect is implied", diagnosis.CategoryMeaning, StringComparison.Ordinal);
        var step = Assert.Single(diagnosis.NotableSteps);
        Assert.Equal("poll-order-status", step.StepId);
        Assert.Equal(3, step.Attempts.Count);
        Assert.Equal(1, step.Attempts[0].Attempt);
        Assert.Equal(100, step.Attempts[0].TMs);
        Assert.Null(step.Attempts[0].Outcome);
        Assert.Equal(3, step.Attempts[2].Attempt);
        Assert.Equal("Inconclusive", step.Attempts[2].Outcome);
        Assert.Equal(0, step.OmittedAttemptCount);
    }

    [Fact]
    public async Task ExplainAsync_PassOnlySample_SummarisesAllPassedWithNoNotableSteps()
    {
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("Pass", diagnosis.Verdict);
        Assert.Contains("all assertions held", diagnosis.CategoryMeaning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(diagnosis.NotableSteps);
        Assert.Equal(1, diagnosis.TotalStepCount);
        Assert.Equal(1, diagnosis.PassedStepCount);
        Assert.Contains("passed", diagnosis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── Session last-run default (REQ-007) ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExplainAsync_NoEventsPath_DefaultsToTheLastRunTrackerEntry()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";
        var path = WriteTempEventsFile(events);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path, "Pass");
            var orchestrator = new ExplainRunOrchestrator(registry);

            var outcome = await orchestrator.ExplainAsync(null, CancellationToken.None);

            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);
            Assert.Equal("Pass", diagnosed.Diagnosis.Verdict);
            Assert.Equal(path, diagnosed.Diagnosis.EventsFilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExplainAsync_WhitespaceOnlyEventsPath_AlsoDefaultsToTheLastRunTrackerEntry()
    {
        const string events = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";
        var path = WriteTempEventsFile(events);
        try
        {
            var registry = StubRunRegistry.WithCompletedRun(path, "Pass");
            var orchestrator = new ExplainRunOrchestrator(registry);

            var outcome = await orchestrator.ExplainAsync("   ", CancellationToken.None);

            Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The compatibility rule US-S3-01 must not break: the retired <c>ILastRunTracker</c> recorded a
    /// run only at COMPLETION, so <c>explain_run</c> called while a run was in flight defaulted to the
    /// previous FINISHED one. The registry records at run START as well, so without
    /// <see cref="RunRegistryExtensions.MostRecentFinishedRun"/>'s filter this default would silently
    /// become "diagnose the run happening right now" — against an events file the engine is still
    /// appending to, or has not created yet.
    /// </summary>
    /// <remarks>
    /// Asserted here, at the ORCHESTRATOR, rather than only on the registry extension
    /// (<c>RunRegistryTests.MostRecentFinishedRun_SkipsARunStillInFlight</c>): the filter is only
    /// load-bearing if <c>explain_run</c>'s default actually applies it, and it is this call that
    /// could regress by reaching for <c>MostRecentRun</c> instead.
    /// </remarks>
    [Fact]
    public async Task ExplainAsync_NoEventsPath_SkipsARunStillInFlight_AndDefaultsToTheLastFinishedRun()
    {
        const string finishedEvents = """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";
        var finishedPath = WriteTempEventsFile(finishedEvents);

        // The in-flight run's events file deliberately holds a DIFFERENT verdict, and is written in
        // full: if the default ever reached for it, the assertions below would report Pass rather
        // than merely failing to find a file — which would be a far weaker signal.
        var inFlightPath = WriteTempEventsFile("""{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""");

        try
        {
            var registry = new StubRunRegistry();
            registry.AddCompletedRun(finishedPath, nameof(RunVerdict.Fail));
            registry.AddRunningRun(inFlightPath);

            var outcome = await new ExplainRunOrchestrator(registry).ExplainAsync(null, CancellationToken.None);

            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);
            Assert.Equal(finishedPath, diagnosed.Diagnosis.EventsFilePath);
            Assert.Equal("Fail", diagnosed.Diagnosis.Verdict);

            // The in-flight run IS the most recent run — it is only the FINISHED filter that keeps it
            // out of the default. Asserted so a future change that made it terminal would fail here
            // rather than make this test vacuously pass.
            Assert.Equal(inFlightPath, registry.MostRecentRun()?.EventsFilePath);
        }
        finally
        {
            File.Delete(finishedPath);
            File.Delete(inFlightPath);
        }
    }

    [Fact]
    public async Task ExplainAsync_NoEventsPathAndNoPriorRun_ReturnsCleanNoRunToExplainError()
    {
        var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

        var outcome = await orchestrator.ExplainAsync(null, CancellationToken.None);

        var noRun = Assert.IsType<ExplainRunOutcome.NoRunToExplain>(outcome);
        Assert.Contains("run_suite", noRun.Message, StringComparison.Ordinal);
    }

    // ── Bad eventsPath: distinct structured errors, never a crash, never a spawn ────────────────

    [Fact]
    public async Task ExplainAsync_MissingFile_ReturnsEventsFileNotFound()
    {
        var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jsonl");

        var outcome = await orchestrator.ExplainAsync(missingPath, CancellationToken.None);

        Assert.IsType<ExplainRunOutcome.EventsFileNotFound>(outcome);
    }

    [Fact]
    public async Task ExplainAsync_UncPath_ReturnsInvalidPathWithoutTouchingTheFilesystem()
    {
        var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

        var outcome = await orchestrator.ExplainAsync(@"\\attacker-host\share\events.jsonl", CancellationToken.None);

        Assert.IsType<ExplainRunOutcome.InvalidPath>(outcome);
    }

    [Fact]
    public async Task ExplainAsync_UnparseableGarbageContent_ReturnsNoRecognisableEvents()
    {
        var path = WriteTempEventsFile("this is not json at all\nneither is this {{{\nnor this ]]]");
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);

            Assert.IsType<ExplainRunOutcome.NoRecognisableEvents>(outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExplainAsync_EmptyFile_ReturnsNoRecognisableEvents()
    {
        var path = WriteTempEventsFile(string.Empty);
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);

            Assert.IsType<ExplainRunOutcome.NoRecognisableEvents>(outcome);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── EDGE-004: unknown fields/event types are tolerated (v1 additive-frozen contract) ────────

    [Fact]
    public async Task ExplainAsync_UnknownFieldsAndUnknownEventType_ParsesAndDiagnosesNormally()
    {
        const string events = """
            {"type":"some-brand-new-event-type-from-a-future-engine","somethingTotallyNew":{"nested":true}}
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50,"aFieldThatDoesNotExistYet":"ignored"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS","yetAnotherUnknownField":123}
            """;

        var diagnosis = await DiagnoseAsync(events);

        Assert.Equal("Pass", diagnosis.Verdict);
        Assert.Equal(1, diagnosis.TotalStepCount);
    }

    // ── The 64KB response cap holds regardless of source size ───────────────────────────────────

    [Fact]
    public async Task ExplainAsync_EventsFileAtLeast50MB_ProducesResponseUnder64KB()
    {
        // A padded-but-genuine step-completed FAIL line, repeated until the file is comfortably
        // >= EventsFileReader.MaxEventsFileBytes (50MB) -- proving BuildDiagnosis's tiered cap
        // guarantee holds for a source events file at the very scale the read boundary itself caps
        // at, not just for a "reasonably small" suite.
        var padding = new string('x', 1_900);
        var lineTemplate = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "s",
            verdict = "FAIL",
            durationMs = 1,
            observation = new { note = padding },
        }) + "\n";
        var linesNeeded = (int)(EventsFileReader.MaxEventsFileBytes / Encoding.UTF8.GetByteCount(lineTemplate)) + 100;

        var builder = new StringBuilder(linesNeeded * lineTemplate.Length);
        for (var i = 0; i < linesNeeded; i++)
        {
            builder.Append(lineTemplate);
        }

        var events = builder.ToString();
        Assert.True(
            Encoding.UTF8.GetByteCount(events) >= EventsFileReader.MaxEventsFileBytes,
            "Test precondition failed: the synthetic events file must be at least 50MB.");

        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);

            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);
            var serialisedBytes = JsonSerializer.SerializeToUtf8Bytes(diagnosed.Diagnosis, ResponseSizeProbeOptions);

            // Measured against the PRECISE budget BuildDiagnosis actually enforces (half of
            // MaxDiagnosisResponseBytes — see that constant's own remarks on why: the full wire
            // envelope serialises this same payload TWICE), not just the looser, doubled public
            // constant.
            Assert.True(
                serialisedBytes.Length < ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
                $"Expected a bare diagnosis under {ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes} bytes, got {serialisedBytes.Length}.");
            Assert.Equal("Fail", diagnosed.Diagnosis.Verdict);
            Assert.True(diagnosed.Diagnosis.ResponseTruncated);
            Assert.True(diagnosed.Diagnosis.OmittedNotableStepCount > 0);

            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(StructuredToolResult.Success(diagnosed.Diagnosis), ResponseSizeProbeOptions);
            Assert.True(
                envelopeBytes.Length < ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
                $"Expected the full CallToolResult envelope under {ExplainRunOrchestrator.MaxDiagnosisResponseBytes} bytes, got {envelopeBytes.Length}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExplainAsync_HugeStepId_IsCappedAtParseTimeAndResponseStaysUnder64KB()
    {
        // A review-found BLOCKER: SuiteEventParser sanitised stepId but did not length-cap it (unlike
        // observation/errorKind/resourceName/detail), and BuildDiagnosis's minimal tier was assumed
        // to fit "by construction" without ever measuring it. A single line with a ~200,000-character
        // stepId — well under the 50MB whole-file read cap, so it is NOT caught by that boundary at
        // all — used to yield a response roughly 3x over the 64KB cap.
        var hugeStepId = new string('s', 200_000);
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = hugeStepId,
            verdict = "FAIL",
            durationMs = 1,
        }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);

            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);
            var serialisedBytes = JsonSerializer.SerializeToUtf8Bytes(diagnosed.Diagnosis, ResponseSizeProbeOptions);

            // Measured against the PRECISE budget BuildDiagnosis actually enforces (half of
            // MaxDiagnosisResponseBytes) — see ExplainAsync_EventsFileAtLeast50MB_... above for why.
            Assert.True(
                serialisedBytes.Length < ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
                $"Expected a bare diagnosis under {ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes} bytes, got {serialisedBytes.Length}.");

            // Proves the ROOT CAUSE is fixed (the stepId is genuinely capped at the source), not
            // merely masked by some other layer: the step's own id in the diagnosis is far shorter
            // than the 200,000 characters supplied.
            var step = Assert.Single(diagnosed.Diagnosis.NotableSteps);
            Assert.True(step.StepId.Length < 3_000, $"Expected stepId to be capped, was {step.StepId.Length} characters.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExplainAsync_HugeStepId_FullEnvelopeAlsoStaysUnder64KB()
    {
        // StructuredToolResult.Success serialises the SAME payload TWICE (a text Content block AND
        // StructuredContent) -- confirms the FULL on-the-wire envelope, not just the bare diagnosis,
        // stays under the cap for the pathological input above.
        //
        // This input is pathological in the SIZE OF ONE FIELD, not in the size of the response: the
        // 200,000-char stepId is capped at parse time, leaving a single-step diagnosis whose full
        // envelope MEASURES 9,973 B -- 55,563 B, or 85%, under the cap. So this test is a genuine
        // guard on parse-time capping and nothing else. An earlier comment here claimed it was the
        // assertion that would "go red first" if the envelope budget were breached; that was false
        // (it has 85% headroom and cannot go red on budget grounds), and the real boundary case --
        // a maximal-tier-0 diagnosis, whose envelope ALREADY exceeds the cap -- is covered by
        // MaximalTierZeroDiagnosis_FitsTheBudgetButItsEnvelopeExceedsTheCap below.
        var hugeStepId = new string('s', 200_000);
        var events = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = hugeStepId,
            verdict = "FAIL",
            durationMs = 1,
        }) + "\n" + """{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}""";

        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());
            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);
            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome);

            var toolResult = StructuredToolResult.Success(diagnosed.Diagnosis);
            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(toolResult, ResponseSizeProbeOptions);

            Assert.True(
                envelopeBytes.Length < ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
                $"Expected the full CallToolResult envelope under {ExplainRunOrchestrator.MaxDiagnosisResponseBytes} bytes, got {envelopeBytes.Length}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real envelope boundary case, and the Sprint-4 re-budget baseline (US-S1-02 review fix):
    /// a diagnosis that the tiering ACCEPTS at tier 0 — i.e. one that satisfies
    /// <c>EffectiveDiagnosisBudgetBytes</c> and reports no truncation — nevertheless produces a full
    /// <c>CallToolResult</c> envelope that EXCEEDS <c>MaxDiagnosisResponseBytes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MEASURED on this input</b> (found by sweeping observation sizes up to the point where the
    /// tiers drop from tier 0 to tier 1 — the last configuration that still fits): bare diagnosis
    /// <b>32,229 B</b> (under the 32,768 B halved budget); envelope <b>71,335 B</b> against a
    /// 65,536 B cap, i.e. <b>5,799 B over</b>; envelope-to-bare ratio <b>2.213</b>, not the 2.0 the
    /// <c>/2</c> budget models.
    /// </para>
    /// <para>
    /// <b>The breach is PRE-EXISTING and is NOT caused by US-S1-02's meta stamp.</b> The same input
    /// measured without any <c>meta</c> gives <b>70,951 B</b> — already 5,415 B over. <c>meta</c>
    /// accounts for <b>384 B</b>, i.e. <b>6.6%</b> of the overage. The dominant cause is that the
    /// duplicated text <c>Content</c> block is a JSON-ESCAPED STRING on the wire (every quote and
    /// backslash in the payload re-escaped, the whole thing quoted) rather than a second verbatim
    /// copy, plus the <c>CallToolResult</c> wrapper's own fields.
    /// </para>
    /// <para>
    /// <b>Sprint 4 owns the fix</b>, and the sanctioned answer is a <c>resourceUri</c> hand-off —
    /// return large evidence as an MCP resource the host fetches on demand so the inline response
    /// shrinks — explicitly NOT raising the cap, which would export the cost to every host's context
    /// window. Until then this test documents the breach rather than asserting it away.
    /// </para>
    /// <para>
    /// <b>Why ratios and directions, not the literal byte counts:</b> <c>meta</c> embeds
    /// <c>workspaceRoot</c>, whose length differs per machine and per install, so the absolute
    /// numbers above are this-machine measurements and would make a brittle assertion. The
    /// relationships they demonstrate — tier 0 accepted, budget satisfied, cap exceeded, multiplier
    /// above 2, meta a small minority of the overage — are machine-independent, so those are what is
    /// asserted. The bound on meta's share is deliberately loose (25%) for the same reason: a long
    /// install path must not turn this into a flaky test, while still failing loudly if meta ever
    /// became a material cause.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MaximalTierZeroDiagnosis_FitsTheBudgetButItsEnvelopeExceedsTheCap()
    {
        // Ten failing steps x ten attempts each, with the largest observations that still let tier 0
        // (10 steps / 10 attempts) be selected -- one notch larger and BuildDiagnosis falls to tier 1.
        var stepObservation = new string('x', 450);
        var attemptObservation = new string('y', 190);
        var events = new StringBuilder();

        for (var step = 0; step < 10; step++)
        {
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                events.Append(JsonSerializer.Serialize(new
                {
                    type = "step-attempt",
                    stepId = $"assert-step-{step:D2}",
                    attempt,
                    verdict = "FAIL",
                    durationMs = 123,
                    observation = attemptObservation,
                })).Append('\n');
            }

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
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());
            var diagnosed = Assert.IsType<ExplainRunOutcome.Diagnosed>(
                await orchestrator.ExplainAsync(path, CancellationToken.None));
            var diagnosis = diagnosed.Diagnosis;

            // 1. The tiering genuinely accepted this at tier 0 -- it is not a truncated fallback.
            Assert.False(diagnosis.ResponseTruncated);
            Assert.Equal(10, diagnosis.NotableSteps.Count);

            // 2. The bare diagnosis satisfies the budget BuildDiagnosis actually enforces.
            var bareBytes = JsonSerializer.SerializeToUtf8Bytes(diagnosis, ResponseSizeProbeOptions).Length;
            Assert.True(
                bareBytes <= ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes,
                $"Expected the bare diagnosis within the {ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes}-byte budget, got {bareBytes}.");

            // 3. ...and yet the real envelope busts the public cap. This is the finding.
            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
                StructuredToolResult.Success(diagnosis), ResponseSizeProbeOptions).Length;
            Assert.True(
                envelopeBytes > ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
                $"Expected the envelope to still exceed the {ExplainRunOrchestrator.MaxDiagnosisResponseBytes}-byte cap "
                + $"(the documented, Sprint-4-owned breach), got {envelopeBytes}. If this now FITS, the budget was "
                + "fixed -- update ExplainRunOrchestrator.MaxDiagnosisResponseBytes' remarks and this test together.");

            // 4. The multiplier the /2 budget assumes (2.0) is not the real one.
            Assert.True(
                (double)envelopeBytes / bareBytes > 2.0,
                $"Expected the envelope-to-bare multiplier above 2.0 (escaping overhead), got {(double)envelopeBytes / bareBytes:F3}.");

            // 5. meta is a small minority of the overage -- the breach is not its doing.
            var withoutMeta = MeasureEnvelopeWithoutMeta(diagnosis);
            var overage = envelopeBytes - ExplainRunOrchestrator.MaxDiagnosisResponseBytes;
            var metaContribution = envelopeBytes - withoutMeta;
            Assert.True(
                withoutMeta > ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
                $"Expected the cap to be exceeded even WITHOUT meta (proving the breach pre-dates US-S1-02), got {withoutMeta}.");
            Assert.True(
                metaContribution < overage / 4,
                $"Expected meta to be well under a quarter of the {overage}-byte overage, got {metaContribution}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The pre-US-S1-02 envelope shape, rebuilt by hand: the same payload as a text
    /// <c>Content</c> block plus <c>StructuredContent</c>, with no <c>meta</c> stamp. Exists only so
    /// the test above can attribute the overage between the pre-existing escaping cost and the
    /// stamp.
    /// </summary>
    private static int MeasureEnvelopeWithoutMeta(DiagnosisResult diagnosis)
    {
        var payloadJson = JsonSerializer.Serialize(diagnosis, typeof(DiagnosisResult), StructuredToolResult.Options);

        return JsonSerializer.SerializeToUtf8Bytes(
            new ModelContextProtocol.Protocol.CallToolResult
            {
                Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = payloadJson }],
                StructuredContent = JsonSerializer.SerializeToElement(
                    diagnosis, typeof(DiagnosisResult), StructuredToolResult.Options),
            },
            ResponseSizeProbeOptions).Length;
    }

    [Fact]
    public async Task ExplainAsync_HugeEventsPath_ErrorResponseStaysUnderTheEnvelopeCap()
    {
        // A review-found gap: the error branches (missing/unreadable/invalid-path) echoed the FULL
        // caller-supplied eventsPath with no length cap at all — an implausibly long path would
        // yield an oversized tool ERROR response, undermining the 64KB envelope cap the success
        // path already enforces (a simple response-size DoS).
        var hugePath = new string('p', 200_000);
        var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

        var outcome = await orchestrator.ExplainAsync(hugePath, CancellationToken.None);

        // A 200,000-character plain string is not UNC-rooted, so PathSafetyGuard lets it through
        // and File.Exists (which never throws for an implausible path — it degrades to false)
        // resolves this to "not found", exactly like any other missing file.
        var notFound = Assert.IsType<ExplainRunOutcome.EventsFileNotFound>(outcome);
        Assert.True(notFound.Message.Length < 2_000, $"Expected a bounded error message, was {notFound.Message.Length} characters.");

        // Confirm the FULL tool-level error response — as the agent actually receives it — also
        // stays comfortably under the 64KB envelope cap.
        var errorResult = StructuredToolResult.Error(
            VfxCodeCatalogue.CreateError(VfxCodeCatalogue.EventsFileNotFound, notFound.Message));
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(errorResult, ResponseSizeProbeOptions);
        Assert.True(
            envelopeBytes.Length < ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
            $"Expected the error envelope under {ExplainRunOrchestrator.MaxDiagnosisResponseBytes} bytes, got {envelopeBytes.Length}.");
    }

    [Fact]
    public async Task ExplainAsync_HugeUncEventsPath_ErrorResponseStaysUnderTheEnvelopeCap()
    {
        // The SAME gap, exercised via the InvalidPath branch specifically (which previously reused
        // PathSafetyGuard's own message, built from the RAW uncapped path).
        var hugeUncPath = @"\\attacker-host\share\" + new string('p', 200_000);
        var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());

        var outcome = await orchestrator.ExplainAsync(hugeUncPath, CancellationToken.None);

        var invalidPath = Assert.IsType<ExplainRunOutcome.InvalidPath>(outcome);
        Assert.True(invalidPath.Message.Length < 2_000, $"Expected a bounded error message, was {invalidPath.Message.Length} characters.");

        var errorResult = StructuredToolResult.Error(
            VfxCodeCatalogue.CreateError(VfxCodeCatalogue.PathOutsideWorkspace, invalidPath.Message));
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(errorResult, ResponseSizeProbeOptions);
        Assert.True(
            envelopeBytes.Length < ExplainRunOrchestrator.MaxDiagnosisResponseBytes,
            $"Expected the error envelope under {ExplainRunOrchestrator.MaxDiagnosisResponseBytes} bytes, got {envelopeBytes.Length}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<DiagnosisResult> DiagnoseAsync(string events)
    {
        var path = WriteTempEventsFile(events);
        try
        {
            var orchestrator = new ExplainRunOrchestrator(new InMemoryRunRegistry());
            var outcome = await orchestrator.ExplainAsync(path, CancellationToken.None);
            return Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome).Diagnosis;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempEventsFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"explain-run-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }
}
