using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="GetRunEventsOrchestrator"/>'s unit tests (US-S3-05): filter-then-paginate, the opaque
/// cursor's behaviour end to end through the tool's own binding, every structured refusal, and the
/// MEASURED response-budget figures the type's own remarks quote.
/// </summary>
/// <remarks>
/// Driven against the orchestrator directly rather than through the MCP harness, for the reason
/// <c>ExplainRunOrchestratorTests</c> gives: proving pagination arithmetic over a 5000-event fixture
/// through a JSON-RPC round trip would establish nothing the arguments do not already determine, and
/// would cost a server per case. <c>RealGetRunEventsMcpTests</c> owns the wire-facing goldens.
/// </remarks>
public class GetRunEventsOrchestratorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    [Fact]
    public async Task Filters_AreAppliedBeforePaging_SoLimitBoundsMatchesNotLinesScanned()
    {
        // The story's second Gherkin scenario, verbatim: 5000 total events, 40 of which match both
        // filters, limit 10.
        var (orchestrator, runId) = Given(FiveThousandEventsWithFortyMatches());

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(
            runId, Types: ["step-attempt"], StepId: "verify-order", Limit: 10));

        Assert.Equal(10, result.Events.Count);
        Assert.NotNull(result.NextCursor);
        Assert.All(result.Events, e =>
        {
            Assert.Equal("step-attempt", e.GetProperty("type").GetString());
            Assert.Equal("verify-order", e.GetProperty("stepId").GetString());
        });
    }

    [Fact]
    public async Task TheSecondPage_ContinuesWithoutOverlapAndTheWalkTerminates()
    {
        var (orchestrator, runId) = Given(FiveThousandEventsWithFortyMatches());
        var request = new GetRunEventsRequest(runId, Types: ["step-attempt"], StepId: "verify-order", Limit: 10);

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(orchestrator, request with { Cursor = cursor });
            seen.AddRange(page.Events.Select(e => e.GetProperty("attempt").GetInt32()));
            cursor = page.NextCursor;
            pages++;

            // A page walk that cannot terminate is the one pagination bug that turns a bounded call
            // into an unbounded one; 40 matches at 10 a page is 4.
            Assert.True(pages <= 10, "The page walk did not terminate.");
        }
        while (cursor is not null);

        Assert.Equal(4, pages);
        Assert.Equal(40, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(Enumerable.Range(1, 40), seen);
    }

    [Fact]
    public async Task NextCursor_IsAbsentOnTheLastPage_SoAHostNeverLearnsTheWalkIsOverByFetchingNothing()
    {
        // The look-ahead's whole purpose: nextCursor present ⇒ a further matching event genuinely
        // exists. Exactly 10 matches with limit 10 must NOT hand back a cursor.
        var lines = Enumerable.Range(1, 10)
            .Select(i => $$"""{"type":"step-attempt","stepId":"s","attempt":{{i}}}""");
        var (orchestrator, runId) = Given(string.Join('\n', lines));

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: ["step-attempt"], Limit: 10));

        Assert.Equal(10, result.Events.Count);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task NoFilters_ReturnsEveryEventInFileOrder()
    {
        var (orchestrator, runId) = Given("""
            {"type":"run-started"}
            {"type":"step-attempt","stepId":"a","attempt":1}
            {"type":"step-completed","stepId":"a","verdict":"PASS"}
            """);

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(
            ["run-started", "step-attempt", "step-completed"],
            result.Events.Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task UnparseableAndNonObjectLines_AreSkippedRatherThanFailingTheCall()
    {
        // The same tolerance SuiteEventParser applies: one malformed or forward-incompatible line
        // must never make an otherwise-good run's events unreadable.
        var (orchestrator, runId) = Given("""
            {"type":"a"}
            not json at all
            ["an array, not an event"]

            {"type":"b"}
            """);

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(["a", "b"], result.Events.Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task AnEmptyEventsFile_ReturnsAnEmptyPageWithNoCursor_NotAnError()
    {
        // "The run produced no events" is an ANSWER, not a failure to answer — the same distinction
        // VfxCodeCatalogue's header draws. explain_run's VFX-E-1602 is deliberately NOT reused here:
        // that tool cannot produce a DIAGNOSIS from nothing, whereas an empty page is a complete and
        // correct answer to "give me this run's events".
        var (orchestrator, runId) = Given(string.Empty);

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Empty(result.Events);
        Assert.Null(result.NextCursor);
        Assert.False(string.IsNullOrWhiteSpace(result.EventSchemaVersion));
    }

    [Fact]
    public async Task EventSchemaVersion_FallsBackToTheVendoredSchemaVersionWhenTheStreamDeclaresNone()
    {
        // The FALLBACK path — a stream carrying no version marker at all, which is what an older
        // engine's events file, or one truncated before its first complete line, looks like. It is
        // NOT what the pinned engine produces; see the test below, which is the production path.
        var (orchestrator, runId) = Given("""{"type":"step-completed","stepId":"a","verdict":"PASS"}""");

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(VendoredSchemaVersion.Value, result.EventSchemaVersion);
    }

    [Fact]
    public async Task EventSchemaVersion_ReadsTheMarkerThePinnedEngineActuallyWrites()
    {
        // MEASURED against the real pinned CLI (v1.0.0-rc.4), not inferred: running a suite with
        // `--events` produces lines that begin
        //
        //   {"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"…","runId":"aac1e428…",…}
        //
        // so `schemaVersion` IS declared, the probe DOES fire in production, and "v1" — not the
        // vendored schema version — is what a caller receives. An earlier comment here claimed the
        // opposite, inferred from SuiteEventParser ignoring the field; a spec review caught it by
        // running the engine. The fixture below carries that real prefix verbatim, including the
        // engine's own bare-hex runId, which is deliberately NOT this server's run- prefixed id.
        var (orchestrator, runId) = Given("""
            {"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-09-05T19:01:12.2959442+00:00","runId":"aac1e4287a8a4d68a5c51d9e40b2f0c0","scenarioId":"Orders API smoke test"}
            {"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-09-05T19:01:12.2959442+00:00","runId":"aac1e4287a8a4d68a5c51d9e40b2f0c0","scenarioId":"Orders API smoke test","verdict":"INCONCLUSIVE","counts":{"pass":0,"fail":0,"envError":0,"inconclusive":1}}
            """);

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal("v1", result.EventSchemaVersion);

        // Worth stating because it is a genuine trap for anyone reading this field's tests: at the
        // PINNED engine the declared marker and the vendored fallback happen to be the same string,
        // "v1", so no assertion on the VALUE can tell which path produced it. The test above
        // (EventSchemaVersion_PrefersSpecSection511sOwnSpellingOverTheEnginesCurrentOne) is what
        // separates them, by declaring a version the fallback could never return.
        Assert.Equal(VendoredSchemaVersion.Value, result.EventSchemaVersion);

        // The engine's own runId rides through untouched — it is just another relayed field, and it
        // is a different identifier from the one this tool was called with.
        var engineRunId = result.Events[0].GetProperty("runId").GetString();
        Assert.Equal("aac1e4287a8a4d68a5c51d9e40b2f0c0", engineRunId);
        Assert.NotEqual(runId, engineRunId);
    }

    [Fact]
    public async Task EventSchemaVersion_PrefersSpecSection511sOwnSpellingOverTheEnginesCurrentOne()
    {
        // `eventSchemaVersion` is the spec's name for this concept and the engine writes
        // `schemaVersion`; probing the spec's spelling first means a future engine adopting it wins,
        // additively and with no contract change.
        var (orchestrator, runId) = Given("""
            {"v":1,"schemaVersion":"v1","eventSchemaVersion":"v2","type":"run-started"}
            {"type":"step-completed","stepId":"a","verdict":"PASS"}
            """);

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal("v2", result.EventSchemaVersion);
    }

    [Fact]
    public async Task EventSchemaVersion_IsTheSameOnEveryPageOfOneRun()
    {
        // A version derived from whatever happened to be on the current page would be a field whose
        // meaning depended on the cursor.
        var lines = Enumerable.Repeat("""{"type":"run-started","eventSchemaVersion":"v2"}""", 1)
            .Concat(Enumerable.Range(1, 20).Select(i => $$"""{"type":"step-attempt","stepId":"s","attempt":{{i}}}"""));
        var (orchestrator, runId) = Given(string.Join('\n', lines));
        var request = new GetRunEventsRequest(runId, Types: ["step-attempt"], Limit: 5);

        var first = await PageAsync(orchestrator, request);
        var second = await PageAsync(orchestrator, request with { Cursor = first.NextCursor });

        Assert.Equal("v2", first.EventSchemaVersion);
        Assert.Equal(first.EventSchemaVersion, second.EventSchemaVersion);
    }

    [Fact]
    public async Task ACursorFromDifferentFilters_IsRefusedRatherThanSilentlyMisapplied()
    {
        var (orchestrator, runId) = Given(FiveThousandEventsWithFortyMatches());

        var first = await PageAsync(orchestrator, new GetRunEventsRequest(
            runId, Types: ["step-attempt"], StepId: "verify-order", Limit: 5));

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runId, Types: ["step-completed"], StepId: "verify-order", Limit: 5, Cursor: first.NextCursor),
            CancellationToken.None);

        var refusal = Assert.IsType<GetRunEventsOutcome.InvalidCursor>(outcome);
        Assert.Contains("filters", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACursorFromADifferentRun_IsRefused()
    {
        // runId is part of the binding precisely so a cursor cannot be replayed across runs.
        var registry = new StubRunRegistry();
        var runA = registry.AddCompletedRun(WriteEvents(FiveThousandEventsWithFortyMatches())).RunId;
        var runB = registry.AddCompletedRun(WriteEvents(FiveThousandEventsWithFortyMatches())).RunId;
        var orchestrator = new GetRunEventsOrchestrator(registry);

        var first = await PageAsync(orchestrator, new GetRunEventsRequest(runA, Limit: 5));
        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runB, Limit: 5, Cursor: first.NextCursor),
            CancellationToken.None);

        Assert.IsType<GetRunEventsOutcome.InvalidCursor>(outcome);
    }

    [Fact]
    public async Task AForeignOrTamperedCursor_IsRefusedAsAStructuredErrorRatherThanThrowingOrRestarting()
    {
        var (orchestrator, runId) = Given("""{"type":"a"}""");

        foreach (var cursor in new[] { "not-a-cursor", "!!!!", new string('A', 4096) })
        {
            var outcome = await orchestrator.GetAsync(
                new GetRunEventsRequest(runId, Cursor: cursor), CancellationToken.None);

            // NOT a silent restart from page one: that would hand a caller a duplicate page dressed
            // as a continuation, which a host appending pages could not detect.
            Assert.IsType<GetRunEventsOutcome.InvalidCursor>(outcome);
        }
    }

    [Fact]
    public async Task ReorderingOrRepeatingTheTypesFilter_KeepsTheCursorValid()
    {
        // The binding is over the SORTED SET, not the caller's array: two calls naming the same types
        // in a different order select an identical result set, so refusing the second would be a
        // false alarm.
        var (orchestrator, runId) = Given(FiveThousandEventsWithFortyMatches());

        var first = await PageAsync(orchestrator, new GetRunEventsRequest(
            runId, Types: ["step-attempt", "step-completed"], Limit: 5));
        var second = await PageAsync(orchestrator, new GetRunEventsRequest(
            runId, Types: ["step-completed", "step-attempt", "step-attempt"], Limit: 5, Cursor: first.NextCursor));

        Assert.NotEmpty(second.Events);
    }

    [Fact]
    public async Task ChangingOnlyTheLimit_KeepsTheCursorValid()
    {
        // A host shrinking its page size mid-walk is not making an error; binding `limit` would make
        // that a refusal for no reason a caller could predict.
        var (orchestrator, runId) = Given(FiveThousandEventsWithFortyMatches());

        var first = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: ["step-attempt"], Limit: 5));
        var second = await PageAsync(orchestrator, new GetRunEventsRequest(
            runId, Types: ["step-attempt"], Limit: 2, Cursor: first.NextCursor));

        Assert.Equal(2, second.Events.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GetRunEventsOrchestrator.MaxLimit + 1)]
    [InlineData(int.MaxValue)]
    public async Task AnOutOfRangeLimit_IsRefusedRatherThanClamped(int limit)
    {
        // Refused, not clamped: a caller who asked for 5000 and silently got 2000 would reasonably
        // read the short page as "that is all there was".
        var (orchestrator, runId) = Given("""{"type":"a"}""");

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runId, Limit: limit), CancellationToken.None);

        var refusal = Assert.IsType<GetRunEventsOutcome.InvalidArgument>(outcome);
        Assert.Contains("limit", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(GetRunEventsOrchestrator.MaxLimit)]
    public async Task TheLimitBoundsThemselves_AreAccepted(int limit)
    {
        var (orchestrator, runId) = Given("""{"type":"a"}""");

        Assert.IsType<GetRunEventsOutcome.Paged>(
            await orchestrator.GetAsync(new GetRunEventsRequest(runId, Limit: limit), CancellationToken.None));
    }

    [Fact]
    public async Task OmittingTheLimit_UsesSpecSection45sDefaultOf200()
    {
        var lines = Enumerable.Range(1, GetRunEventsOrchestrator.DefaultLimit + 50)
            .Select(i => $$"""{"type":"e","n":{{i}}}""");
        var (orchestrator, runId) = Given(string.Join('\n', lines));

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(GetRunEventsOrchestrator.DefaultLimit, result.Events.Count);
        Assert.NotNull(result.NextCursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingRunId_IsRefusedAsAnInvalidArgument(string? runId)
    {
        var orchestrator = new GetRunEventsOrchestrator(new StubRunRegistry());

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runId), CancellationToken.None);

        Assert.IsType<GetRunEventsOutcome.InvalidArgument>(outcome);
    }

    [Fact]
    public async Task AnUnknownRunId_IsRunNotFound_NotEventsFileNotFound()
    {
        // The two are different facts with different remedies — see VFX-E-1505's catalogue entry.
        var orchestrator = new GetRunEventsOrchestrator(new StubRunRegistry());

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest("run-does-not-exist"), CancellationToken.None);

        Assert.IsType<GetRunEventsOutcome.RunNotFound>(outcome);
    }

    [Fact]
    public async Task ARegisteredRunWhoseEventsFileIsGone_IsEventsFileNotFound_NotRunNotFound()
    {
        // The registry's record outlives its event stream when the file is deleted or the output
        // directory is cleaned; saying "no such run" there would be a lie the caller would act on.
        var registry = new StubRunRegistry();
        var missing = Path.Combine(Path.GetTempPath(), $"get-run-events-missing-{Guid.NewGuid():N}.jsonl");
        var runId = registry.AddCompletedRun(missing).RunId;
        var orchestrator = new GetRunEventsOrchestrator(registry);

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runId), CancellationToken.None);

        var notFound = Assert.IsType<GetRunEventsOutcome.EventsFileNotFound>(outcome);
        Assert.Contains("events file", notFound.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUncEventsPath_IsRejectedByTheSharedPathGuard()
    {
        // No exemption for a registry-supplied path — the same rule ExplainRunOrchestrator records.
        var registry = new StubRunRegistry();
        var runId = registry.AddCompletedRun(@"\\attacker-host\share\events.jsonl").RunId;
        var orchestrator = new GetRunEventsOrchestrator(registry);

        var outcome = await orchestrator.GetAsync(
            new GetRunEventsRequest(runId), CancellationToken.None);

        Assert.IsType<GetRunEventsOutcome.InvalidPath>(outcome);
    }

    [Fact]
    public async Task AnOverlongFilterValue_IsRefused()
    {
        var (orchestrator, runId) = Given("""{"type":"a"}""");
        var overlong = new string('x', GetRunEventsOrchestrator.MaxFilterValueChars + 1);

        Assert.IsType<GetRunEventsOutcome.InvalidArgument>(await orchestrator.GetAsync(
            new GetRunEventsRequest(runId, StepId: overlong), CancellationToken.None));
        Assert.IsType<GetRunEventsOutcome.InvalidArgument>(await orchestrator.GetAsync(
            new GetRunEventsRequest(runId, Types: [overlong]), CancellationToken.None));
    }

    [Fact]
    public async Task TooManyTypeFilters_AreRefused()
    {
        var (orchestrator, runId) = Given("""{"type":"a"}""");
        var tooMany = Enumerable.Range(0, GetRunEventsOrchestrator.MaxTypeFilters + 1)
            .Select(i => i.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        Assert.IsType<GetRunEventsOutcome.InvalidArgument>(await orchestrator.GetAsync(
            new GetRunEventsRequest(runId, Types: tooMany), CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyTypesArray_MeansNoTypeFilterRatherThanMatchNothing()
    {
        // A host that built its filter list dynamically and produced [] means "no restriction", not
        // "return nothing" — the latter would be a silently empty result with no error to act on.
        var (orchestrator, runId) = Given("""{"type":"a"}""");

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: []));

        Assert.Single(result.Events);
    }

    // ── Response budget (risk 4: MEASURED by serialising, never assumed) ────────────────────────

    [Fact]
    public async Task ThePayloadBudget_IsEnforcedByMeasurementEvenWhenLimitWouldAllowMore()
    {
        // 2000 realistic events would serialise to hundreds of KB; the byte budget — not `limit` — is
        // what actually bounds this page, and it is measured rather than reasoned about.
        var lines = Enumerable.Range(1, GetRunEventsOrchestrator.MaxLimit)
            .Select(i => $$"""{"type":"step-attempt","stepId":"verify-order","attempt":{{i}},"tMs":{{i * 7}},"observation":{"matched":false,"expected":"CONFIRMED","actual":"PENDING"}""" + "}");
        var (orchestrator, runId) = Given(string.Join('\n', lines));

        var result = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Limit: GetRunEventsOrchestrator.MaxLimit));
        var measured = GetRunEventsOrchestrator.SerialisedByteCount(result);

        Assert.True(
            result.Events.Count < GetRunEventsOrchestrator.MaxLimit,
            "This fixture is supposed to be budget-bound, not limit-bound — if every event now fits, "
            + "the per-event size or the budget changed and the figures on EffectiveEventsBudgetBytes "
            + "need re-measuring.");
        Assert.NotNull(result.NextCursor);
        Assert.True(
            measured <= GetRunEventsOrchestrator.EffectiveEventsBudgetBytes + CursorAndVersionOverheadAllowanceBytes,
            $"The page measured {measured} B against a {GetRunEventsOrchestrator.EffectiveEventsBudgetBytes} B "
            + "budget for its events array.");

        // The figures quoted on EffectiveEventsBudgetBytes' remarks. MEASURED on this fixture:
        // 224 events, 32,827 B, 146.5 B per event — so the full 2000 `limit` would have been about
        // 293,000 B, roughly 9x the budget. Pinned as RANGES rather than as those literals: the
        // absolute counts move with this fixture's own field lengths and with any field added to
        // GetRunEventsResult, whereas the order of magnitude is the fact the budget actually rests
        // on, and the whole point of the check is that it is measured rather than assumed.
        var bytesPerEvent = measured / (double)result.Events.Count;
        Assert.InRange(bytesPerEvent, 100, 400);
        Assert.InRange(result.Events.Count, 100, 400);
    }

    [Fact]
    public void TheForwardProgressInequality_IsWhatMakesTheGuaranteeTrue()
    {
        // A gatekeeper review's M3: the "admit the first match whatever it costs" branch in BuildPage
        // is DEFENSIVE and currently unreachable, so a test that leans on it proves nothing. What
        // actually guarantees a page is never empty while a matching event remains is this
        // inequality — no relayed event can exceed MaxEventBytes, and MaxEventBytes is a fraction of
        // an empty page's budget — so it is asserted directly. If a future re-tune inverts it, this
        // fails here rather than as an infinite page walk in a host.
        Assert.True(
            RawEventRelay.MaxEventBytes < GetRunEventsOrchestrator.EffectiveEventsBudgetBytes,
            $"MaxEventBytes ({RawEventRelay.MaxEventBytes}) must stay below "
            + $"EffectiveEventsBudgetBytes ({GetRunEventsOrchestrator.EffectiveEventsBudgetBytes}), or a "
            + "single event could exhaust an empty page's budget and the walk could stall.");
    }

    [Fact]
    public async Task AnEventTooLargeToRelay_BecomesAMarkerAndTheWalkStillAdvancesPastIt()
    {
        // The successor to a test that asserted forward progress against a "huge" event which was
        // never huge: its 20,000-character string was capped to 2000 BEFORE the byte check, so both
        // events fit page one and nothing was being tested (deleting the guard still passed it).
        // What genuinely exceeds the 4 KB cap is BREADTH, which no per-string cap shrinks.
        var wide = string.Join(',', Enumerable.Range(0, 40).Select(i => $"\"f{i}\":\"{new string('x', 300)}\""));
        var (orchestrator, runId) = Given(string.Join('\n', [
            $$"""{"type":"e","stepId":"s","attempt":1,{{wide}}}""",
            """{"type":"e","n":2}""",
        ]));

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Limit: GetRunEventsOrchestrator.MaxLimit));

        // Both events come back — the oversized one as the marker, which is small — so the page walk
        // never stalls on it and the caller can see THAT something was there.
        Assert.Equal(2, page.Events.Count);
        Assert.True(page.Events[0].GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.Equal("e", page.Events[0].GetProperty("type").GetString());
        Assert.Equal(2, page.Events[1].GetProperty("n").GetInt32());
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task TheByteBudget_BindsMidPage_AndTheNextPageResumesAtExactlyTheEventThatDidNotFit()
    {
        // A gatekeeper review's M3b. `limit` is set to the maximum so it cannot be what stops the
        // page: the 32 KB budget is, and each of these events is ~2 KB, so roughly sixteen fit.
        var (orchestrator, runId) = Given(TwentyTwoKilobyteEvents());
        var request = new GetRunEventsRequest(runId, Limit: GetRunEventsOrchestrator.MaxLimit);

        var first = await PageAsync(orchestrator, request);

        Assert.InRange(first.Events.Count, 1, TwoKilobyteEventCount - 1);
        Assert.NotNull(first.NextCursor);

        var second = await PageAsync(orchestrator, request with { Cursor = first.NextCursor });

        // No overlap and no gap: the next page starts at exactly the event the budget refused.
        Assert.Equal(
            first.Events.Count + 1,
            second.Events[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task ABudgetBoundWalk_ConcatenatesToExactlyTheMatchSet_OnceEachAndInOrder()
    {
        // The property that actually matters to a host appending pages, asserted over a walk the
        // BUDGET drives rather than `limit`: every matching event exactly once, in file order, with
        // nothing duplicated across a page boundary and nothing lost at one.
        var (orchestrator, runId) = Given(TwentyTwoKilobyteEvents());
        var request = new GetRunEventsRequest(runId, Limit: GetRunEventsOrchestrator.MaxLimit);

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PageAsync(orchestrator, request with { Cursor = cursor });
            Assert.NotEmpty(page.Events);
            seen.AddRange(page.Events.Select(e => e.GetProperty("n").GetInt32()));
            cursor = page.NextCursor;

            Assert.True(++pages <= TwoKilobyteEventCount, "The budget-bound page walk did not terminate.");
        }
        while (cursor is not null);

        Assert.True(pages > 1, "This fixture is supposed to need more than one page; re-measure it.");
        Assert.Equal(Enumerable.Range(1, TwoKilobyteEventCount), seen);
    }

    // ── Bounded work and undecodable lines (a security review's BLOCKER and MAJOR) ──────────────

    [Fact]
    public async Task ALineCarryingALoneSurrogateEscape_DoesNotKillTheRunsEventsForever()
    {
        // MEASURED: `"\ud800"` parses and then throws InvalidOperationException on decode. Uncaught,
        // it escaped the tool — and because the walk over a file is deterministic, that ONE line made
        // every page of that run fail permanently. Now it is one marker among ordinary events.
        var (orchestrator, runId) = Given("""
            {"type":"a","n":1}
            {"type":"b","v":"\ud800"}
            {"type":"c","n":3}
            """);

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(3, page.Events.Count);
        Assert.True(page.Events[1].GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.Equal("c", page.Events[2].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ALoneSurrogateOnANonMatchingLine_DoesNotBreakAFilteredCallThatNeverWantedIt()
    {
        // The nastier half of the same bug: the filter reads `type` off EVERY line, so a poisoned
        // line failed calls that had explicitly filtered it out.
        var (orchestrator, runId) = Given("""
            {"type":"\ud800","payload":"noise"}
            {"type":"wanted","n":1}
            """);

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: ["wanted"]));

        Assert.Equal("wanted", Assert.Single(page.Events).GetProperty("type").GetString());
    }

    [Fact]
    public async Task APoisonedPropertyNAME_IsSkippedByAFilteredPage_RatherThanThrowingAtTheLookup()
    {
        // The residual half of the same BLOCKER. The earlier fix caught the throw on READING an
        // undecodable string; this one is the throw on the LOOKUP, because TryGetProperty must
        // unescape a candidate NAME to compare it. `Matches` looks up the four-byte "type" on every
        // line, so a line carrying the six-byte escaped name `"\ud800"` — a line the caller filtered
        // OUT — killed the whole call before the wanted line was ever reached.
        var (orchestrator, runId) = Given("""
            {"type":"step-attempt","\ud800":1}
            {"type":"wanted","n":1}
            """);

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: ["wanted"]));

        Assert.Equal(1, Assert.Single(page.Events).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task APoisonedPropertyNAME_IsRelayedAsAMarkerOnAnUnfilteredPage()
    {
        // …and unfiltered, the same line reaches BuildTruncationMarker, whose own label lookups are
        // the other throw site. One marker among ordinary events, exactly as an undecodable VALUE
        // already produced.
        var (orchestrator, runId) = Given("""
            {"type":"a","n":1}
            {"type":"step-attempt","\ud800":1}
            {"type":"c","n":3}
            """);

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(3, page.Events.Count);
        Assert.True(page.Events[1].GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.Equal("c", page.Events[2].GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheVersionProbe_SurvivesAPoisonedPropertyNameLongEnoughToCollideWithItsMarkers()
    {
        // The probe's own throw site, and it needs a LONGER poisoned name than the filter path does:
        // it looks up "eventSchemaVersion" (18 bytes) and "schemaVersion" (13), so a six-byte escaped
        // name is ruled out on length and never decoded. Padded to 18 bytes on the wire, both lookups
        // decode — and both threw, before any event of this run was considered.
        var (orchestrator, runId) = Given(string.Join('\n', [
            """{"type":"a","\ud800aaaaaaaaaaaa":1}""",
            """{"v":1,"schemaVersion":"v1","type":"step-completed","stepId":"a"}""",
        ]));

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        // Resolution still SUCCEEDS from the second line: the poisoned line contributes no version
        // and is stepped over, rather than ending the probe (or the call).
        Assert.Equal("v1", page.EventSchemaVersion);
        Assert.Equal(2, page.Events.Count);
    }

    [Fact]
    public async Task TheVersionProbe_SurvivesAPoisonedLeadingWindow()
    {
        // ResolveEventSchemaVersion reads the first 50 lines and reads a raw string off each; before
        // the fix, one undecodable line inside that window failed every page of the run before any
        // event was even considered.
        var lines = Enumerable.Repeat("""{"type":"\ud800"}""", 50)
            .Append("""{"v":1,"schemaVersion":"v1","type":"step-completed","stepId":"a"}""");
        var (orchestrator, runId) = Given(string.Join('\n', lines));

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        // The marker window pushed the declaring line past the 50-line probe, so the fallback is what
        // is reported — the point being that a value comes back at all rather than an exception.
        Assert.Equal(VendoredSchemaVersion.Value, page.EventSchemaVersion);
        Assert.NotEmpty(page.Events);
    }

    [Fact]
    public async Task AnOverLongLine_IsReportedAsALabellessMarkerOnAnUnfilteredPage_NeverParsed()
    {
        // Bounding the OUTPUT left the WORK unbounded: a 50 MB line still had to be parsed in full to
        // discover it relayed to a 62-byte marker. Past MaxEventLineChars it is refused before the
        // parser sees it — which is also why the marker cannot carry a type label.
        var overLong = $$"""{"type":"huge","blob":"{{new string('x', RawEventRelay.MaxEventLineChars)}}"}""";
        var (orchestrator, runId) = Given(string.Join('\n', [overLong, """{"type":"small","n":2}"""]));

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId));

        Assert.Equal(2, page.Events.Count);
        Assert.True(page.Events[0].GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.False(page.Events[0].TryGetProperty("type", out _));
        Assert.Equal(
            RawEventRelay.ByteCountOf(overLong),
            page.Events[0].GetProperty(RawEventRelay.OriginalBytesMarkerProperty).GetInt32());
        Assert.Equal("small", page.Events[1].GetProperty("type").GetString());

        // Nothing was dropped: the line IS in the page, as the marker, so the page is complete and
        // `truncated` stays false. This is the contrast that gives the filtered sibling's `true` its
        // meaning.
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task AnOverLongLine_IsPassedOverWhenAFilterIsActive_AndThePageSaysSoViaTruncated()
    {
        // Its type was never read, so claiming it matches a `types` filter would put an event of
        // unknown kind into a timeline the caller narrowed on purpose.
        var overLong = $$"""{"type":"wanted","blob":"{{new string('x', RawEventRelay.MaxEventLineChars)}}"}""";
        var (orchestrator, runId) = Given(string.Join('\n', [overLong, """{"type":"wanted","n":2}"""]));

        var page = await PageAsync(orchestrator, new GetRunEventsRequest(runId, Types: ["wanted"]));

        Assert.Equal(2, Assert.Single(page.Events).GetProperty("n").GetInt32());

        // …and the skip is REPORTED. Passing the line over is correct; doing it silently was not: an
        // event whose match status is unknowable was dropped from the answer, and with no cursor owed
        // a host would read the short page as the whole filtered timeline.
        Assert.True(page.Truncated);
    }

    // ── `truncated`: the two ways a scan stops short of the stream's end ────────────────────────

    [Fact]
    public async Task AnOrdinaryPage_IsNotMarkedTruncated()
    {
        var (orchestrator, runId) = Given("""{"type":"a"}""");

        Assert.False((await PageAsync(orchestrator, new GetRunEventsRequest(runId))).Truncated);
    }

    [Fact]
    public void TheReaderCap_IsReportedAsTruncated_SoAnAbsentCursorIsNotReadAsCompletion()
    {
        // Cause 1: EventsFileReader stopped at its 50 MB cap. Driven through the page builder's own
        // seam rather than by writing a 50 MB file, which would measure the filesystem rather than
        // this behaviour.
        var page = BuildPageDirectly("""{"type":"a"}""", contentTruncated: true);

        Assert.True(page.Truncated);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void TheLineCap_IsReportedAsTruncated_Likewise()
    {
        // Cause 2: the line backstop ended the scan. Driven through the same seam with a small cap —
        // reaching the real 2,000,000 honestly costs two million parses and proves nothing extra.
        var content = string.Join('\n', Enumerable.Range(1, 5).Select(i => $$"""{"type":"e","n":{{i}}}"""));

        var capped = BuildPageDirectly(content, contentTruncated: false, maxLines: 3);
        Assert.True(capped.Truncated);
        Assert.Equal(3, capped.Events.Count);

        // …and the same content read without the cap is NOT truncated, so the flag tracks the cap
        // rather than the fixture.
        var whole = BuildPageDirectly(content, contentTruncated: false);
        Assert.False(whole.Truncated);
        Assert.Equal(5, whole.Events.Count);
    }

    [Fact]
    public void TheLineCap_IsNotReportedWhenTheFileEndsExactlyAtIt()
    {
        // Reaching the cap is only truncation if a further line actually existed — otherwise every
        // exactly-sized file would claim to be incomplete.
        var content = string.Join('\n', Enumerable.Range(1, 3).Select(i => $$"""{"type":"e","n":{{i}}}"""));

        Assert.False(BuildPageDirectly(content, contentTruncated: false, maxLines: 3).Truncated);
    }

    /// <summary>
    /// Slack allowed above the events-array budget for the two small scalar fields the result also
    /// carries (<c>eventSchemaVersion</c> and <c>nextCursor</c>) plus JSON punctuation — the budget
    /// is spent against the EVENTS, which is where all the variable size is.
    /// </summary>
    private const int CursorAndVersionOverheadAllowanceBytes = 512;

    // ── Fixtures and helpers ───────────────────────────────────────────────────────────────────

    /// <summary>How many ~2&#160;KB events <see cref="TwentyTwoKilobyteEvents"/> writes.</summary>
    /// <remarks>
    /// Chosen so the 32&#160;KB budget binds partway through — about sixteen fit — while the whole set
    /// still needs only a couple of pages, which is what makes the boundary observable in one walk.
    /// </remarks>
    private const int TwoKilobyteEventCount = 20;

    /// <summary>
    /// <see cref="TwoKilobyteEventCount"/> events of roughly 2&#160;KB each, numbered <c>n: 1..N</c>.
    /// </summary>
    /// <remarks>
    /// The payload string is deliberately UNDER <see cref="RawEventRelay.MaxStringChars"/>: a longer
    /// one would be capped to 2000 characters before the byte budget ever saw it, which is exactly
    /// how the predecessor of these tests came to assert nothing at all.
    /// </remarks>
    private static string TwentyTwoKilobyteEvents() =>
        string.Join('\n', Enumerable.Range(1, TwoKilobyteEventCount).Select(i =>
            $$"""{"type":"step-attempt","stepId":"s","n":{{i}},"blob":"{{new string('x', 1_900)}}"}"""));

    /// <summary>
    /// Drives <see cref="GetRunEventsOrchestrator.BuildPage"/> directly, through the SAME
    /// <c>ValidateArguments</c> the production path uses, so the filters are the real ones rather
    /// than a test-local imitation.
    /// </summary>
    /// <remarks>
    /// The registry, path guard and bounded read are all upstream of the page builder and covered by
    /// the request-level tests above; what this seam buys is the two arguments no fixture can supply
    /// cheaply — the reader's truncation flag (a 50&#160;MB file) and the line backstop (two million
    /// lines).
    /// </remarks>
    private static GetRunEventsResult BuildPageDirectly(
        string content,
        bool contentTruncated,
        int maxLines = GetRunEventsOrchestrator.MaxLinesProcessed)
    {
        var refusal = GetRunEventsOrchestrator.ValidateArguments(
            new GetRunEventsRequest("run-seam"), out var filters, out var limit);
        Assert.Null(refusal);

        return GetRunEventsOrchestrator.BuildPage(
            content, filters, limit, startLine: 0, contentTruncated, maxLines);
    }

    /// <summary>
    /// The story's Gherkin fixture: 5000 events of which exactly 40 are <c>step-attempt</c> for
    /// <c>verify-order</c>. The matches are SCATTERED through the file, not clustered at the front —
    /// a filter applied after paging would pass a fixture where they happened to come first.
    /// </summary>
    private static string FiveThousandEventsWithFortyMatches()
    {
        var lines = new List<string>(5000);
        var matches = 0;
        for (var i = 0; i < 5000; i++)
        {
            if (i % 125 == 60 && matches < 40)
            {
                matches++;
                lines.Add($$"""{"type":"step-attempt","stepId":"verify-order","attempt":{{matches}},"tMs":{{i}}}""");
                continue;
            }

            // Near-misses on purpose: the right type with the wrong step, and the right step with the
            // wrong type. A filter that ANDed incorrectly would pick these up.
            lines.Add((i % 3) switch
            {
                0 => $$"""{"type":"step-attempt","stepId":"other-step","attempt":{{i}}}""",
                1 => $$"""{"type":"step-completed","stepId":"verify-order","verdict":"PASS","durationMs":{{i}}}""",
                _ => $$"""{"type":"log","message":"line {{i}}"}""",
            });
        }

        Assert.Equal(40, matches);
        return string.Join('\n', lines);
    }

    private (GetRunEventsOrchestrator Orchestrator, string RunId) Given(string eventsFileContent)
    {
        var registry = StubRunRegistry.WithCompletedRun(WriteEvents(eventsFileContent));
        return (new GetRunEventsOrchestrator(registry), registry.ListRuns()[0].RunId);
    }

    private string WriteEvents(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"get-run-events-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<GetRunEventsResult> PageAsync(
        GetRunEventsOrchestrator orchestrator, GetRunEventsRequest request)
    {
        var outcome = await orchestrator.GetAsync(request, CancellationToken.None);
        return Assert.IsType<GetRunEventsOutcome.Paged>(outcome).Result;
    }

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
                // Best-effort cleanup of a temp file this test wrote; a locked file is not a failure
                // of anything under test.
            }
        }

        GC.SuppressFinalize(this);
    }
}
