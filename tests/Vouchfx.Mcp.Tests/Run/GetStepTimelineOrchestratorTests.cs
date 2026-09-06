using System.Text.Json;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="GetStepTimelineOrchestrator"/>'s unit tests (US-S3-06): the story's three Gherkin
/// scenarios, the attempt-outcome mapping's every branch, the <c>specPath</c> adjudication, and the
/// MEASURED budget figures the type's own remarks quote.
/// </summary>
/// <remarks>
/// Driven against the orchestrator directly rather than through the MCP harness, for the reason
/// <c>GetRunEventsOrchestratorTests</c> gives: proving budget arithmetic over a multi-hundred-attempt
/// fixture through a JSON-RPC round trip would establish nothing the arguments do not already
/// determine, and would cost a server per case. <c>RealGetStepTimelineMcpTests</c> owns the
/// wire-facing goldens, including the truncation-immunity comparison against a real
/// <c>explain_run</c> call.
/// </remarks>
public class GetStepTimelineOrchestratorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private const string StubSpecPath = "stub.e2e.yaml";

    /// <summary>The engine's four verdict wire tokens — none of which may appear as an attempt outcome.</summary>
    private static readonly string[] WireTokens = ["PASS", "FAIL", "ENV_ERROR", "INCONCLUSIVE"];

    /// <summary>The three-value attempt vocabulary, as the enum's own constants.</summary>
    private static readonly string[] AttemptOutcomes =
        [StepAttemptOutcome.Matched, StepAttemptOutcome.Unmatched, StepAttemptOutcome.Error];

    /// <summary>Mirrors the orchestrator's own size probe, so a measured figure here is the one it enforces.</summary>
    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    // ── Gherkin 1: a long timeline is returned in full ───────────────────────────────────────────

    /// <summary>
    /// The story's FIRST Gherkin scenario: "a step with 40 RETRY attempts, more than explain_run's
    /// largest tier (10 notableSteps, 10 attempts) would retain … attempts contains all 40 entries
    /// … no attempt is elided or replaced with a placeholder".
    /// </summary>
    /// <remarks>
    /// The tier figure the scenario quotes is not restated as a literal here — it is read out of
    /// <c>ExplainRunOrchestrator</c>'s own behaviour by the companion test below, so a future change
    /// to those tiers cannot leave this comment claiming a number the code no longer uses.
    /// </remarks>
    [Fact]
    public async Task FortyAttempts_AreAllReturned_WithNoneElidedOrPlaceheld()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 40, observationChars: 1_000));

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Equal(40, result.Attempts.Count);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.OmittedAttemptCount);

        // Not elided: the engine's own one-based counter is present and complete, 1..40, in order.
        Assert.Equal(Enumerable.Range(1, 40).ToArray(), result.Attempts.Select(a => a.N).ToArray());

        // Not placeholder-replaced: every attempt carries its own real tMs and a real outcome from
        // the three-value enum, not a marker object.
        Assert.All(result.Attempts, attempt =>
        {
            Assert.True(attempt.TMs > 0);
            Assert.Contains(attempt.Outcome, AttemptOutcomes);
        });
    }

    /// <summary>
    /// The AC behind that scenario — "a test proves that a timeline long enough to be truncated by
    /// explain_run's tiers is nonetheless returned in full by get_step_timeline" — asserted as a
    /// COMPARISON against the real <c>ExplainRunOrchestrator</c> over the SAME events file, rather
    /// than against a hardcoded tier number.
    /// </summary>
    [Fact]
    public async Task TheSameTimelineExplainRunTruncates_ComesBackWholeHere()
    {
        var eventsPath = WriteEvents(RetryTimeline(attempts: 40, observationChars: 1_000));
        var registry = StubRunRegistry.WithCompletedRun(eventsPath);
        var runId = registry.ListRuns()[0].RunId;

        var explained = await new ExplainRunOrchestrator(registry).ExplainAsync(eventsPath, CancellationToken.None);
        var diagnosis = Assert.IsType<ExplainRunOutcome.Diagnosed>(explained).Diagnosis;
        var explainedStep = Assert.Single(diagnosis.NotableSteps);

        // Non-vacuity, and the premise of the whole comparison: explain_run really does shorten it.
        Assert.True(
            explainedStep.Attempts.Count < 40,
            "explain_run returned the whole timeline, so this comparison proves nothing. Its tiers "
            + "changed; re-size this fixture above the largest of them.");
        Assert.True(explainedStep.OmittedAttemptCount > 0);

        var timeline = await FoundAsync(
            new GetStepTimelineOrchestrator(registry),
            new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Equal(40, timeline.Attempts.Count);
        Assert.Equal(explainedStep.Attempts.Count + explainedStep.OmittedAttemptCount, timeline.Attempts.Count);
    }

    /// <summary>
    /// The budget INVERSION itself: under pressure this tool shortens evidence TEXT and keeps the
    /// list, which is the opposite of what <c>explain_run</c> does with the same input.
    /// </summary>
    [Fact]
    public async Task UnderBudgetPressure_EvidenceTextShrinksAndTheAttemptListDoesNot()
    {
        // 40 attempts × 1000 characters of observation is ~40 KB of evidence alone, comfortably over
        // the effective budget, so a tier below the first MUST have been taken.
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 40, observationChars: 1_000));

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Equal(40, result.Attempts.Count);
        Assert.True(result.ObservedCapped, "The evidence text was expected to be capped at this size.");
        Assert.False(result.Truncated, "The attempt LIST must not shrink before the evidence text does.");
        Assert.True(SerialisedBytes(result) <= GetStepTimelineOrchestrator.EffectiveTimelineBudgetBytes);

        // The orchestrator's remarks quote 12,114 B for exactly this fixture. Asserted as a BAND
        // rather than an equality: the figure depends on the compact tier's 200-character cap and on
        // the payload's field set, both of which a legitimate change may move slightly, but a change
        // that moves it OUT of this band has changed something the remarks describe.
        Assert.InRange(SerialisedBytes(result), 11_500, 12_800);
    }

    /// <summary>
    /// The MEASURED figure this type's remarks quote — recomputed here rather than trusted from the
    /// comment, per this codebase's rule that a measured claim is pinned by the thing that measures it.
    /// </summary>
    [Fact]
    public async Task TheMinimalTier_FitsFarMoreAttemptsThanExplainRunsLargestTier()
    {
        // No observation text at all, so the minimal tier's own per-attempt cost is what is being
        // measured. 2000 attempts is well past what the budget can hold, which is the point: the
        // result's own OmittedAttemptCount reports where the boundary actually fell.
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 2_000, observationChars: 0));

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.True(result.Truncated);
        Assert.Equal(2_000 - result.Attempts.Count, result.OmittedAttemptCount);
        Assert.True(SerialisedBytes(result) <= GetStepTimelineOrchestrator.EffectiveTimelineBudgetBytes);

        // The claim being pinned: an order of magnitude beyond explain_run's largest tier of ten, and
        // far beyond what an exponential-backoff poll loop against a real step timeout produces. The
        // orchestrator's remarks name 469 for this fixture; the band accommodates the small variation
        // an independent re-measurement found (470) without accepting a figure that would make the
        // "47x explain_run's largest tier" claim false.
        Assert.InRange(result.Attempts.Count, 440, 500);
        Assert.True(
            result.Attempts.Count > 100,
            $"Only {result.Attempts.Count} attempts fit the minimal tier. The type's remarks claim "
            + "several hundred; either the shape grew a field or the budget changed, and the remarks "
            + "need re-measuring.");
    }

    // ── The projection is BOUNDED, not merely eventually short (security MAJOR-1) ────────────────

    /// <summary>
    /// A security review's MAJOR finding, pinned: <c>BuildTimeline</c> used to serialise the WHOLE
    /// attempt list at all three tiers before <c>FitAttemptList</c> could shorten it, so the work a
    /// call did was set by the events file rather than by the response budget — measured at ~1.9 GB of
    /// allocation and 2.3 s to produce a 16 KB answer from a 10,000-attempt timeline. Every tier probe
    /// is now capped at <c>MaxFittableAttempts + 1</c>, the point past which no list can fit however
    /// short its entries are.
    /// </summary>
    /// <remarks>
    /// The allocation bound is asserted loosely and deliberately so: it is there to catch a return of
    /// the unbounded shape (which exceeded it by two orders of magnitude), not to pin an allocation
    /// figure that ordinary serializer changes would move. The ARITHMETIC assertions beside it are the
    /// exact ones — a bounded probe must not cost the result its honesty about what was dropped.
    /// </remarks>
    [Fact]
    public async Task ATenThousandAttemptTimeline_IsProjectedWithBoundedWorkAndAnHonestOmittedCount()
    {
        // 10,000 attempts each carrying a 1,000-character observation: ~10 MB of evidence text, which
        // the unbounded version serialised three times over before shortening anything.
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 10_000, observationChars: 1_000));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Honest about what it dropped: the omitted count is computed against the FULL stream count,
        // never against the probe cap, so the two still sum to what the events file held.
        Assert.True(result.Truncated);
        Assert.Equal(10_000, result.Attempts.Count + result.OmittedAttemptCount);
        Assert.True(SerialisedBytes(result) <= GetStepTimelineOrchestrator.EffectiveTimelineBudgetBytes);

        // And still a useful answer, not a degenerate empty one.
        Assert.NotEmpty(result.Attempts);
        Assert.Equal(Enumerable.Range(1, result.Attempts.Count).ToArray(), result.Attempts.Select(a => a.N).ToArray());

        Assert.True(
            allocated < 200_000_000,
            $"Projecting a 10,000-attempt timeline allocated {allocated:N0} bytes. The tier probes are "
            + "supposed to be capped at MaxFittableAttempts + 1 (~547 attempts) — this is the unbounded "
            + "shape returning, which measured ~1.9 GB.");
    }

    // ── Gherkin 2: the attempt outcome vocabulary ────────────────────────────────────────────────

    /// <summary>
    /// The story's SECOND Gherkin scenario: "a step's third attempt observed a value that did not
    /// match the expected key … that attempt's outcome is exactly 'unmatched' … no attempt's outcome
    /// field ever contains 'Pass', 'Fail', 'EnvironmentError', or 'Inconclusive'".
    /// </summary>
    [Fact]
    public async Task AnUnmatchedAttempt_IsExactlyUnmatched_AndNoOutcomeCarriesTheVerdictTaxonomy()
    {
        // Attempts 1-3 are the engine's FAIL wire token (a poll that looked and did not find);
        // attempt 4 is PASS. The third attempt is the one the scenario names.
        var events = string.Join('\n',
            """{"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":100,"outcome":"FAIL"}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":2,"tMs":300,"outcome":"FAIL"}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":3,"tMs":700,"outcome":"FAIL","observation":{"expected":"orderId","got":null}}""",
            """{"type":"step-attempt","stepId":"poll-order","attempt":4,"tMs":1500,"outcome":"PASS"}""",
            """{"type":"step-completed","stepId":"poll-order","verdict":"PASS","durationMs":1500}""");

        var (orchestrator, runId) = Given(events);
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        var third = result.Attempts.Single(a => a.N == 3);
        Assert.Equal("unmatched", third.Outcome);

        // The whole-payload sweep the scenario's second Then asks for — over every attempt, not just
        // the third, and naming all four taxonomy words rather than the one under test.
        foreach (var attempt in result.Attempts)
        {
            foreach (var verdictWord in new[]
                     {
                         nameof(RunVerdict.Pass), nameof(RunVerdict.Fail),
                         nameof(RunVerdict.EnvironmentError), nameof(RunVerdict.Inconclusive),
                     })
            {
                Assert.DoesNotContain(verdictWord, attempt.Outcome, StringComparison.Ordinal);
            }
        }

        // And the complement, so this is not passing merely because the mapping produces nothing: the
        // step's own conclusion IS allowed to name the taxonomy, because a step's conclusion is a
        // verdict. That is the distinction the scenario is about.
        Assert.Equal("matched", result.Attempts.Single(a => a.N == 4).Outcome);
        Assert.Contains(nameof(RunVerdict.Pass), result.Conclusion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wire tokens are not relayed either — a second half of the same vocabulary rule, since
    /// <c>get_run_events</c> DOES relay them and a host consuming both tools must not find them here.
    /// </summary>
    [Fact]
    public async Task NoAttemptOutcome_CarriesAnEngineWireToken()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 5, observationChars: 0));
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.All(result.Attempts, attempt =>
            Assert.DoesNotContain(attempt.Outcome, WireTokens, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every branch of <see cref="GetStepTimelineOrchestrator.MapAttemptOutcome"/>, driven directly:
    /// the mapping is the story's core design decision, so each fold is pinned individually rather
    /// than inferred from a fixture that happens to exercise some of them.
    /// </summary>
    [Theory]
    [InlineData("PASS", StepAttemptOutcome.Matched, false)]
    [InlineData("FAIL", StepAttemptOutcome.Unmatched, false)]
    [InlineData("ENV_ERROR", StepAttemptOutcome.Error, true)]
    [InlineData("INCONCLUSIVE", StepAttemptOutcome.Error, true)]
    [InlineData(null, StepAttemptOutcome.Error, true)]
    [InlineData("SOMETHING_NEWER", StepAttemptOutcome.Error, true)]
    public void MapAttemptOutcome_FoldsEachWireTokenWhereItsRationaleSays(
        string? rawToken, string expectedOutcome, bool expectsError)
    {
        var parsed = RunVerdictExtensions.ParseWireToken(rawToken)?.ToString();
        var attempt = new StepAttempt(1, 100, parsed, Observation: null, RawOutcome: rawToken);

        var (outcome, error) = GetStepTimelineOrchestrator.MapAttemptOutcome(attempt);

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectsError, error is not null);

        // An unrecognised token is echoed in the explanation rather than guessed at or dropped — the
        // additive-frozen event contract's own requirement.
        if (rawToken == "SOMETHING_NEWER")
        {
            Assert.Contains("SOMETHING_NEWER", error!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// "No outcome recorded" and "an outcome token this build does not know" are DIFFERENT states,
    /// and <see cref="StepAttempt.RawOutcome"/> exists precisely so they stay distinguishable. Both
    /// map to <c>error</c>; their explanations must not be interchangeable.
    /// </summary>
    [Fact]
    public void AnAbsentOutcome_AndAnUnrecognisedOne_AreExplainedDifferently()
    {
        var absent = GetStepTimelineOrchestrator.MapAttemptOutcome(
            new StepAttempt(1, 100, null, null, RawOutcome: null));
        var unknown = GetStepTimelineOrchestrator.MapAttemptOutcome(
            new StepAttempt(1, 100, null, null, RawOutcome: "MAYBE"));

        Assert.Equal(StepAttemptOutcome.Error, absent.Outcome);
        Assert.Equal(StepAttemptOutcome.Error, unknown.Outcome);
        Assert.NotEqual(absent.Error, unknown.Error);
        Assert.Contains("no outcome", absent.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not recognise", unknown.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The event's OWN error text wins over this server's composed explanation.</summary>
    [Fact]
    public void AnEventSuppliedErrorString_IsPreferredOverThisServersOwnSentence()
    {
        var (outcome, error) = GetStepTimelineOrchestrator.MapAttemptOutcome(
            new StepAttempt(1, 100, nameof(RunVerdict.EnvironmentError), null, RawOutcome: "ENV_ERROR", Error: "container exited 137"));

        Assert.Equal(StepAttemptOutcome.Error, outcome);
        Assert.Equal("container exited 137", error);
    }

    // ── Gherkin 3: verifyMode ONCE ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The story's THIRD Gherkin scenario: "a step ran with verifyMode 'ONCE' and succeeded on its
    /// single attempt … verifyMode is 'ONCE' … attempts contains exactly one entry".
    /// </summary>
    [Fact]
    public async Task ASingleAttemptStep_ReportsVerifyModeOnceWithExactlyOneAttempt()
    {
        var events = string.Join('\n',
            """{"type":"step-attempt","stepId":"check-health","attempt":1,"tMs":42,"outcome":"PASS"}""",
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":42}""");

        var (orchestrator, runId) = Given(events);
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "check-health"));

        Assert.Equal(StepVerifyMode.Once, result.VerifyMode);
        Assert.Single(result.Attempts);
        Assert.Equal(StepAttemptOutcome.Matched, result.Attempts[0].Outcome);
    }

    [Fact]
    public async Task MoreThanOneAttempt_ReportsVerifyModeRetry()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 3, observationChars: 0));
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Equal(StepVerifyMode.Retry, result.VerifyMode);
    }

    /// <summary>
    /// A step with a completion event and NO attempt events — the ordinary shape of an IMMEDIATE
    /// step — is a SUCCESS with an empty timeline and a null verifyMode, never an error and never a
    /// fabricated <c>ONCE</c>.
    /// </summary>
    [Fact]
    public async Task AStepWithNoAttemptEvents_IsASuccessWithAnEmptyTimelineAndNoVerifyMode()
    {
        var (orchestrator, runId) = Given(
            """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":42}""");

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "check-health"));

        Assert.Empty(result.Attempts);
        Assert.Null(result.VerifyMode);
        Assert.Contains(
            "no individual attempt events recorded", result.Conclusion, StringComparison.OrdinalIgnoreCase);
    }

    // ── The fields this build does not source ────────────────────────────────────────────────────

    /// <summary>
    /// <c>delayMs</c> and <c>timeoutMs</c> come back as explicit nulls rather than synthesised values —
    /// the adjudication this story's own remarks record, pinned so a later "helpful" derivation cannot
    /// land unnoticed. <c>at</c> is null here only because this FIXTURE emits no <c>ts</c>; the pinned
    /// engine does emit one (see <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>), and the relay of
    /// it is pinned by the test below.
    /// </summary>
    [Fact]
    public async Task TheUnsourcedFields_AreNullRatherThanSynthesised()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 4, observationChars: 0));
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Null(result.TimeoutMs);
        Assert.All(result.Attempts, attempt =>
        {
            Assert.Null(attempt.At);
            Assert.Null(attempt.DelayMs);

            // And the substitute that IS sourced is present, so the nulls above are an honest
            // absence rather than a timeline with no time in it at all.
            Assert.True(attempt.TMs > 0);
        });
    }

    /// <summary>
    /// An event carrying an absolute timestamp has it relayed rather than ignored. <b>This is the
    /// PRODUCTION path, not a forward-compatibility one</b> — the pinned engine emits <c>ts</c> on
    /// every event (measured by <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>), which is what
    /// this repository's documentation once denied. The fixture below is shaped like a real line;
    /// <c>OnTheWire_AnAttemptTimestampTheEngineEmitted_IsRelayedAsAString</c> pins the same fact at the
    /// wire level with a verbatim one.
    /// </summary>
    [Fact]
    public async Task AnEventCarryingATimestamp_HasItRelayed()
    {
        var (orchestrator, runId) = Given(
            """{"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":10,"ts":"2026-09-05T10:00:00Z","outcome":"PASS"}""");

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.Equal("2026-09-05T10:00:00Z", Assert.Single(result.Attempts).At);
    }

    // ── The specPath adjudication ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASpecPathTheRunNeverCovered_IsRefused()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 2, observationChars: 0));

        var outcome = await orchestrator.GetAsync(
            new GetStepTimelineRequest(runId, "some-other-suite.e2e.yaml", "poll-order"), CancellationToken.None);

        var refusal = Assert.IsType<GetStepTimelineOutcome.SpecPathNotInRun>(outcome);
        Assert.Contains("get_run_status", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A single-suite run's timeline IS attributable, and says so.</summary>
    [Fact]
    public async Task ASingleSuiteRun_ReportsTheTimelineAsAttributedToThatSuite()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 2, observationChars: 0));
        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        Assert.True(result.SpecPathAttributed);
        Assert.Equal(StubSpecPath, result.SpecPath);
    }

    /// <summary>
    /// A MULTI-suite run's is not, and the result says so in the flag AND in words — the honest
    /// contract this story adjudicated, since the concatenated stream carries no per-suite attribution.
    /// </summary>
    [Fact]
    public async Task AMultiSuiteRun_ReportsTheTimelineAsUnattributedAndSaysSoInTheConclusion()
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(
            WriteEvents(RetryTimeline(attempts: 2, observationChars: 0)),
            specPaths: ["checkout.e2e.yaml", "orders.e2e.yaml"]);
        var runId = registry.ListRuns()[0].RunId;

        var result = await FoundAsync(
            new GetStepTimelineOrchestrator(registry),
            new GetStepTimelineRequest(runId, "orders.e2e.yaml", "poll-order"));

        Assert.False(result.SpecPathAttributed);
        Assert.Contains("no per-suite attribution", result.Conclusion, StringComparison.Ordinal);

        // Still validated: naming a suite the run did not cover is refused even in this mode, which is
        // the half of the argument that DOES do work.
        Assert.IsType<GetStepTimelineOutcome.SpecPathNotInRun>(
            await new GetStepTimelineOrchestrator(registry).GetAsync(
                new GetStepTimelineRequest(runId, "billing.e2e.yaml", "poll-order"), CancellationToken.None));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AStepIdTheRunNeverRecorded_IsRefusedRatherThanAnsweredWithAnEmptyTimeline()
    {
        var (orchestrator, runId) = Given(RetryTimeline(attempts: 2, observationChars: 0));

        var outcome = await orchestrator.GetAsync(
            new GetStepTimelineRequest(runId, StubSpecPath, "no-such-step"), CancellationToken.None);

        var refusal = Assert.IsType<GetStepTimelineOutcome.StepNotInRun>(outcome);
        Assert.Contains("no-such-step", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRunId_IsRefusedWithTheSharedMessage()
    {
        var orchestrator = new GetStepTimelineOrchestrator(new StubRunRegistry());

        var outcome = await orchestrator.GetAsync(
            new GetStepTimelineRequest("run-0000000000000000000000000000cafe", StubSpecPath, "s"),
            CancellationToken.None);

        var refusal = Assert.IsType<GetStepTimelineOutcome.RunNotFound>(outcome);

        // The SHARED wording, so a host reading two tools' refusals reads one fact.
        Assert.Contains("list_runs", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, StubSpecPath, "s")]
    [InlineData("run-1", null, "s")]
    [InlineData("run-1", StubSpecPath, null)]
    [InlineData("run-1", " ", "s")]
    [InlineData("run-1", StubSpecPath, " ")]
    public void AMissingOrBlankArgument_IsRefusedBeforeTheRegistryIsTouched(
        string? runId, string? specPath, string? stepId)
    {
        Assert.NotNull(
            GetStepTimelineOrchestrator.ValidateArguments(new GetStepTimelineRequest(runId, specPath, stepId)));
    }

    [Fact]
    public async Task AMissingEventsFile_IsRefusedWithTheRunIdNamed()
    {
        var registry = StubRunRegistry.WithCompletedRun(
            Path.Combine(Path.GetTempPath(), $"never-written-{Guid.NewGuid():N}.jsonl"));
        var runId = registry.ListRuns()[0].RunId;

        var outcome = await new GetStepTimelineOrchestrator(registry).GetAsync(
            new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"), CancellationToken.None);

        Assert.IsType<GetStepTimelineOutcome.EventsFileNotFound>(outcome);
    }

    // ── Evidence relay ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An attempt's observation is relayed as the engine wrote it (modulo this server's own display
    /// sanitising), never re-redacted and never re-interpreted.
    /// </summary>
    [Fact]
    public async Task AnAttemptsObservation_IsRelayedAsEvidence()
    {
        var (orchestrator, runId) = Given(
            """{"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":10,"outcome":"FAIL","observation":{"expected":"READY","got":"PENDING"}}""");

        var result = await FoundAsync(orchestrator, new GetStepTimelineRequest(runId, StubSpecPath, "poll-order"));

        var observed = Assert.Single(result.Attempts).Observed;
        Assert.NotNull(observed);
        Assert.Contains("PENDING", observed, StringComparison.Ordinal);
        Assert.False(result.ObservedCapped);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A RETRY timeline: <paramref name="attempts"/> polls of <c>poll-order</c>, followed by the
    /// step's own completion event.
    /// </summary>
    /// <param name="succeeds">
    /// <see langword="true"/> for a poll loop that found what it was waiting for on its last attempt;
    /// <see langword="false"/> (the default) for one that exhausted its budget without matching — the
    /// shape a long RETRY timeline actually has in the field, and the one <c>explain_run</c> considers
    /// NOTABLE (it lists only non-<c>Pass</c> steps), which the truncation comparison depends on.
    /// </param>
    private static string RetryTimeline(int attempts, int observationChars, bool succeeds = false)
    {
        var observation = observationChars > 0
            ? ",\"observation\":{\"got\":\"" + new string('x', observationChars) + "\"}"
            : string.Empty;

        var lines = Enumerable.Range(1, attempts).Select(n =>
        {
            var outcome = succeeds && n == attempts ? "PASS" : "FAIL";
            return $$"""{"type":"step-attempt","stepId":"poll-order","attempt":{{n}},"tMs":{{n * 100}},"outcome":"{{outcome}}"{{observation}}}""";
        });

        var verdict = succeeds ? "PASS" : "FAIL";
        return string.Join(
            '\n',
            lines.Append($$"""{"type":"step-completed","stepId":"poll-order","verdict":"{{verdict}}","durationMs":9000}"""));
    }

    private (GetStepTimelineOrchestrator Orchestrator, string RunId) Given(string eventsFileContent)
    {
        var registry = StubRunRegistry.WithCompletedRun(WriteEvents(eventsFileContent));
        return (new GetStepTimelineOrchestrator(registry), registry.ListRuns()[0].RunId);
    }

    private string WriteEvents(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"get-step-timeline-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<GetStepTimelineResult> FoundAsync(
        GetStepTimelineOrchestrator orchestrator, GetStepTimelineRequest request)
    {
        var outcome = await orchestrator.GetAsync(request, CancellationToken.None);
        return Assert.IsType<GetStepTimelineOutcome.Found>(outcome).Result;
    }

    private static int SerialisedBytes(GetStepTimelineResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temp file this test could not remove is not a test failure.
            }
        }

        GC.SuppressFinalize(this);
    }
}
