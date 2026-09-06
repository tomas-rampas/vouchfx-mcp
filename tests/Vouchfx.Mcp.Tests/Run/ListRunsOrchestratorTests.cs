using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// US-S3-03's <c>list_runs</c> at the unit seam: the filters, the page bounds, the cursor's
/// timestamp position, and — driven from <see cref="OpaqueCursorContract"/> — the SHARED cursor
/// contract this tool must satisfy identically to <c>get_run_events</c>.
/// </summary>
/// <remarks>
/// The sprint's exit checklist requires "one cursor implementation, verified by a shared unit-test
/// fixture, not two". The <c>Cursor_*</c> cases below are that fixture, driven with
/// <see cref="CursorScopes.ListRuns"/> and bindings composed from THIS tool's own filters; adding a
/// case to <see cref="OpaqueCursorContract"/> strengthens both tools at once.
/// </remarks>
public class ListRunsOrchestratorTests
{
    private const string EventsFilePath = "/tmp/vouchfx/run-events.jsonl";

    // ── Filters ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void List_NoFilters_ReturnsEveryRunNewestFirst()
    {
        var registry = new StubRunRegistry();
        var oldest = registry.AddCompletedRun(EventsFilePath);
        var middle = registry.AddCompletedRun(EventsFilePath);
        var newest = registry.AddRunningRun(EventsFilePath);

        var page = PageOf(registry, new ListRunsRequest());

        Assert.Equal(
            [newest.RunId, middle.RunId, oldest.RunId],
            page.Runs.Select(run => run.RunId).ToArray());
    }

    [Fact]
    public void List_ProjectsExactlyTheFiveFieldsSpecSection58Names()
    {
        var registry = new StubRunRegistry();
        var entry = registry.AddCompletedRun(
            EventsFilePath,
            nameof(RunVerdict.Fail),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["trigger"] = "agent:author" });

        var item = Assert.Single(PageOf(registry, new ListRunsRequest()).Runs);

        Assert.Equal(entry.RunId, item.RunId);
        Assert.Equal(RunRegistryStatus.Completed, item.Status);
        Assert.Equal(nameof(RunVerdict.Fail), item.Outcome);
        Assert.Equal(entry.StartedAtUtc, item.StartedAtUtc);
        Assert.Equal(entry.FinishedAtUtc, item.FinishedAtUtc);

        // Spec §5.8 types the list items as a five-field Pick. The registry entry carries three more
        // (specPaths, eventsFilePath, labels) and they are deliberately NOT here — both because the
        // spec says so and because a 2000-entry page of caller-supplied paths and labels would have
        // no bounded size. get_run_status is where a host reads them.
        var itemProperties = typeof(RunListItem)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["FinishedAtUtc", "Outcome", "RunId", "StartedAtUtc", "Status"],
            itemProperties);
    }

    [Fact]
    public void List_LabelKeyEqualsValue_MatchesOnlyRunsCarryingThatExactPair()
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "agent:author")));
        var wanted = registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci")));
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("other", "ci")));

        var page = PageOf(registry, new ListRunsRequest(Label: "trigger=ci"));

        Assert.Equal([wanted.RunId], page.Runs.Select(run => run.RunId).ToArray());
    }

    [Fact]
    public void List_BareLabelKey_MatchesAnyValueForThatKey()
    {
        // The form §5.8 leaves unstated and this story adjudicates: "every run this agent triggered,
        // whatever the iteration" is the obvious query over §5.7's own example labels, and would be
        // inexpressible without it.
        var registry = new StubRunRegistry();
        var first = registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "agent:author")));
        var second = registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci")));
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("iteration", "3")));

        var page = PageOf(registry, new ListRunsRequest(Label: "trigger"));

        Assert.Equal([second.RunId, first.RunId], page.Runs.Select(run => run.RunId).ToArray());
    }

    [Fact]
    public void List_LabelMatchingIsExactAndOrdinal_NeverAPrefixOrCaseInsensitiveMatch()
    {
        // A label is correlated by a machine, not searched by a human. A filter that silently widened
        // would hand a host runs it did not ask about, which is worse than returning none.
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("Trigger", "CI")));
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci-nightly")));

        Assert.Empty(PageOf(registry, new ListRunsRequest(Label: "trigger=ci")).Runs);
        Assert.Empty(PageOf(registry, new ListRunsRequest(Label: "TRIGGER")).Runs);
    }

    [Fact]
    public void List_Since_ExcludesRunsStartedStrictlyBeforeIt_AndKeepsTheBoundaryRun()
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath);
        var boundary = registry.AddCompletedRun(EventsFilePath);
        var after = registry.AddCompletedRun(EventsFilePath);

        var page = PageOf(registry, new ListRunsRequest(Since: boundary.StartedAtUtc.ToString("O")));

        // Inclusive: "since T" is a lower bound a host expects to include a run that started exactly
        // at T — the same convention every at-or-after filter uses.
        Assert.Equal([after.RunId, boundary.RunId], page.Runs.Select(run => run.RunId).ToArray());
    }

    [Fact]
    public void List_SinceWithNoOffset_IsReadAsUtc_NotAsTheServersLocalZone()
    {
        // A bare timestamp means different instants on the host and on the agent's machine, and
        // silently resolving it against whatever zone the SERVER happens to run in would filter by a
        // boundary the caller never named. Asserted by picking a value whose UTC and local readings
        // differ wherever the suite runs with a non-zero offset, and requiring the UTC reading.
        var registry = new StubRunRegistry();
        var only = registry.AddCompletedRun(EventsFilePath);

        var atTheRunsOwnInstant = only.StartedAtUtc.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);

        var page = PageOf(registry, new ListRunsRequest(Since: atTheRunsOwnInstant));

        Assert.Single(page.Runs);
    }

    [Fact]
    public void List_LabelAndSince_AreBothApplied()
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci")));
        var boundary = registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "manual")));
        var wanted = registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci")));

        var page = PageOf(
            registry, new ListRunsRequest(Label: "trigger=ci", Since: boundary.StartedAtUtc.ToString("O")));

        Assert.Equal([wanted.RunId], page.Runs.Select(run => run.RunId).ToArray());
    }

    // ── Paging ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void List_MoreRunsThanTheLimit_ReturnsExactlyTheLimitAndACursor_AndTheNextPageDoesNotOverlap()
    {
        // Gherkin (US-S3-03), at the unit seam and at the spec's own numbers: "Given 250 runs exist …
        // When the host calls list_runs with limit 100 … Then exactly 100 runs are returned, And
        // nextCursor is present … Then the next 100 runs are returned, none overlapping the first."
        var registry = new StubRunRegistry();
        for (var i = 0; i < 250; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var orchestrator = new ListRunsOrchestrator(registry);

        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 100));
        Assert.Equal(100, firstPage.Runs.Count);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = PageOf(orchestrator, new ListRunsRequest(Limit: 100, Cursor: firstPage.NextCursor));
        Assert.Equal(100, secondPage.Runs.Count);
        Assert.NotNull(secondPage.NextCursor);

        var thirdPage = PageOf(orchestrator, new ListRunsRequest(Limit: 100, Cursor: secondPage.NextCursor));
        Assert.Equal(50, thirdPage.Runs.Count);

        // Absent, not null, on the last page — the look-ahead is what makes that true, so a host never
        // learns the walk is over by fetching an empty page.
        Assert.Null(thirdPage.NextCursor);

        var walked = firstPage.Runs.Concat(secondPage.Runs).Concat(thirdPage.Runs)
            .Select(run => run.RunId).ToArray();
        Assert.Equal(250, walked.Length);
        Assert.Equal(250, walked.Distinct(StringComparer.Ordinal).Count());

        // And the walk is the registry's own newest-first order, not merely a partition of it.
        Assert.Equal(registry.ListRuns().Select(entry => entry.RunId).ToArray(), walked);
    }

    [Fact]
    public void List_RunsAddedBetweenPages_DoNotShiftThePageOrDuplicateARow()
    {
        // THE reason the position is a timestamp rather than an index. Every new run lands at the HEAD
        // of a newest-first list, so an index position would slide by exactly the number of runs
        // started mid-walk and re-serve that many rows. The trade — mid-walk runs are not inserted
        // into a walk in progress — is asserted here too, so it is a decision rather than a surprise.
        var registry = new StubRunRegistry();
        for (var i = 0; i < 6; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var orchestrator = new ListRunsOrchestrator(registry);
        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 3));

        var startedMidWalk = registry.AddCompletedRun(EventsFilePath);

        var secondPage = PageOf(orchestrator, new ListRunsRequest(Limit: 3, Cursor: firstPage.NextCursor));

        var walked = firstPage.Runs.Concat(secondPage.Runs).Select(run => run.RunId).ToArray();
        Assert.Equal(6, walked.Length);
        Assert.Equal(6, walked.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(startedMidWalk.RunId, walked);
    }

    [Fact]
    public void List_LimitMayChangeBetweenPages_BecauseItIsNotBoundIntoTheCursor()
    {
        // `limit` is deliberately absent from every binding (OpaqueCursor.ComposeBinding's rule): a
        // host that shrinks its page size mid-walk is not making an error, and refusing it would be a
        // bug in this server rather than a protection.
        var registry = new StubRunRegistry();
        for (var i = 0; i < 5; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var orchestrator = new ListRunsOrchestrator(registry);
        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 2));

        var secondPage = PageOf(orchestrator, new ListRunsRequest(Limit: 3, Cursor: firstPage.NextCursor));

        Assert.Equal(3, secondPage.Runs.Count);
        Assert.Empty(secondPage.Runs.Select(run => run.RunId).Intersect(
            firstPage.Runs.Select(run => run.RunId), StringComparer.Ordinal));
    }

    [Fact]
    public void List_EmptyRegistry_IsAnEmptyPageWithNoCursor_NotAnError()
    {
        var page = PageOf(new StubRunRegistry(), new ListRunsRequest());

        Assert.Empty(page.Runs);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void List_LastPageThatExactlyFillsTheLimit_CarriesNoCursor()
    {
        // Without the look-ahead this page would carry a cursor and the caller would learn the walk
        // was over only by fetching nothing.
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath);
        registry.AddCompletedRun(EventsFilePath);

        var page = PageOf(registry, new ListRunsRequest(Limit: 2));

        Assert.Equal(2, page.Runs.Count);
        Assert.Null(page.NextCursor);
    }

    // ── Argument bounds ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ListRunsOrchestrator.MaxLimit + 1)]
    public void List_OutOfRangeLimit_IsRefusedRatherThanClamped(int limit)
    {
        var outcome = new ListRunsOrchestrator(new StubRunRegistry()).List(new ListRunsRequest(Limit: limit));

        var invalid = Assert.IsType<ListRunsOutcome.InvalidArgument>(outcome);
        Assert.Contains("limit", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_DefaultLimit_IsSpecSection45sTwoHundred()
    {
        var registry = new StubRunRegistry();
        for (var i = 0; i < ListRunsOrchestrator.DefaultLimit + 1; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var page = PageOf(registry, new ListRunsRequest());

        Assert.Equal(ListRunsOrchestrator.DefaultLimit, page.Runs.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-13-45")]
    [InlineData("")]
    public void List_UnparseableSince_IsAnInvalidArgument(string since)
    {
        var outcome = new ListRunsOrchestrator(new StubRunRegistry()).List(new ListRunsRequest(Since: since));

        var invalid = Assert.IsType<ListRunsOutcome.InvalidArgument>(outcome);
        Assert.Contains("since", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=value")]
    [InlineData("   ")]
    [InlineData("=")]
    public void List_LabelFilterWithNoKey_IsRefused(string label)
    {
        // A key-less filter cannot match any label run_suite would have accepted (RunLabelRules
        // refuses a blank key at the write side), so accepting it would only mean scanning the whole
        // registry to answer "no".
        var outcome = new ListRunsOrchestrator(new StubRunRegistry()).List(new ListRunsRequest(Label: label));

        var invalid = Assert.IsType<ListRunsOutcome.InvalidArgument>(outcome);
        Assert.Contains("label", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_OverLongLabelFilter_IsRefused()
    {
        var outcome = new ListRunsOrchestrator(new StubRunRegistry())
            .List(new ListRunsRequest(Label: new string('k', RunLifecycleLimits.MaxLabelFilterChars + 1)));

        Assert.IsType<ListRunsOutcome.InvalidArgument>(outcome);
    }

    // ── The documented tick-tie residual, pinned rather than left to be discovered ────────────────

    /// <summary>
    /// Two runs sharing an exact <c>startedAt</c> tick, with the page boundary falling between them:
    /// the second is DROPPED from the walk. This is the residual
    /// <see cref="ListRunsOrchestrator"/>'s remarks document, asserted so the documented behaviour and
    /// the real behaviour cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driven through the internal <see cref="ListRunsOrchestrator.BuildPage"/>, deliberately</b>
    /// — the state it needs is unreachable through <see cref="ListRunsOrchestrator.List"/>, because
    /// every registry in this repo (production and fixture alike) hands out strictly increasing
    /// timestamps and so cannot produce a tie at all. That is precisely WHY the residual is a residual;
    /// it also means the only honest way to exercise it is to feed the pager the snapshot a pair of
    /// concurrent server processes could, in principle, produce against one workspace.
    /// </para>
    /// <para>
    /// <b>A dropped row is the accepted cost of a TIMESTAMP position</b>, chosen over an index
    /// position that would re-serve rows whenever runs are added mid-walk. This asserts the cost is
    /// the one documented — one row silently missing — and not something worse, such as an infinite
    /// walk or a duplicated row.
    /// </para>
    /// </remarks>
    [Fact]
    public void BuildPage_TwoRunsSharingAStartedAtTick_DropsTheSecondAtAPageBoundary()
    {
        var tied = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

        // Newest first, as ListRuns promises. The middle two share a tick.
        var newestFirst = RunListing.Complete(
        [
            EntryStartedAt("run-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", tied.AddMinutes(1)),
            EntryStartedAt("run-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", tied),
            EntryStartedAt("run-cccccccccccccccccccccccccccccccc", tied),
            EntryStartedAt("run-dddddddddddddddddddddddddddddddd", tied.AddMinutes(-1)),
        ]);

        Assert.Null(ListRunsOrchestrator.ValidateArguments(new ListRunsRequest(), out var filters, out _));

        // Page one ends ON the tie: the last row returned is the FIRST of the two tied runs.
        var first = ListRunsOrchestrator.BuildPage(newestFirst, filters, limit: 2, startBeforeTicks: long.MaxValue);
        Assert.Equal(
            ["run-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "run-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"],
            first.Runs.Select(run => run.RunId).ToArray());
        Assert.NotNull(first.NextCursor);

        // Page two resumes STRICTLY BEFORE that boundary tick — so run-c, which shares it, is skipped.
        var second = ListRunsOrchestrator.BuildPage(newestFirst, filters, limit: 2, startBeforeTicks: tied.UtcTicks);
        Assert.Equal(["run-dddddddddddddddddddddddddddddddd"], second.Runs.Select(run => run.RunId).ToArray());

        // The walk still TERMINATES and serves no row twice — the two failure modes that would make
        // this residual unacceptable rather than merely documented.
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Runs.Select(run => run.RunId).Intersect(
            second.Runs.Select(run => run.RunId), StringComparer.Ordinal));
    }

    // ── Cursor refusals (this tool's own wiring of the shared type) ───────────────────────────────

    [Fact]
    public void List_CursorMintedUnderDifferentFilters_IsRefusedRatherThanMisapplied()
    {
        var registry = new StubRunRegistry();
        for (var i = 0; i < 4; i++)
        {
            registry.AddCompletedRun(EventsFilePath, labels: Labels(("trigger", "ci")));
        }

        var orchestrator = new ListRunsOrchestrator(registry);
        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 2, Label: "trigger=ci"));

        var outcome = orchestrator.List(new ListRunsRequest(Limit: 2, Cursor: firstPage.NextCursor));

        var refused = Assert.IsType<ListRunsOutcome.InvalidCursor>(outcome);
        Assert.Contains("list_runs", refused.Message, StringComparison.Ordinal);

        // The carry-in the peer review asked for: `limit` is exempt from the binding, so a host that
        // legitimately resized its pages must not be sent hunting for a filter it never changed.
        Assert.Contains("'limit'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Changing <c>since</c> mid-walk refuses the cursor, <b>through the production
    /// <see cref="ListRunsOrchestrator.List"/> path</b> rather than against a composed binding.
    /// </summary>
    /// <remarks>
    /// The label half of this rule was already covered end to end; <c>since</c> was covered only at
    /// the binding level, where a bug in how <c>List</c> derives its filters — parsing the timestamp
    /// differently on the two calls, say, or forgetting to pass it into the binding at all — would go
    /// unseen (a gatekeeper review's MINOR finding). Both pages are driven through the real entry
    /// point here, so the whole chain (parse → bind → encode → decode → compare) is what is asserted.
    /// The second call's <c>since</c> is a DIFFERENT instant rather than a different spelling of the
    /// same one — the latter is bound identically on purpose, and is the case below.
    /// </remarks>
    [Fact]
    public void List_SinceChangedMidWalk_IsRefusedThroughTheProductionPath()
    {
        var registry = new StubRunRegistry();
        for (var i = 0; i < 4; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var orchestrator = new ListRunsOrchestrator(registry);
        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 2, Since: "2020-01-01T00:00:00Z"));
        Assert.NotNull(firstPage.NextCursor);

        var outcome = orchestrator.List(
            new ListRunsRequest(Limit: 2, Cursor: firstPage.NextCursor, Since: "2021-01-01T00:00:00Z"));

        var refused = Assert.IsType<ListRunsOutcome.InvalidCursor>(outcome);
        Assert.Contains("list_runs", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two spellings of the SAME instant share a cursor — the reason <c>since</c> is bound as its
    /// parsed value rather than as the caller's text.
    /// </summary>
    [Fact]
    public void List_SinceRespelledAsAnEquivalentOffset_KeepsTheCursorValid()
    {
        var registry = new StubRunRegistry();
        for (var i = 0; i < 4; i++)
        {
            registry.AddCompletedRun(EventsFilePath);
        }

        var orchestrator = new ListRunsOrchestrator(registry);
        var firstPage = PageOf(orchestrator, new ListRunsRequest(Limit: 2, Since: "2020-01-01T00:00:00Z"));

        // Same instant, different offset notation — an identical result set, so refusing it would be a
        // bug in this server rather than a protection.
        var second = PageOf(
            orchestrator,
            new ListRunsRequest(Limit: 2, Cursor: firstPage.NextCursor, Since: "2020-01-01T01:00:00+01:00"));

        Assert.Equal(2, second.Runs.Count);
        Assert.Empty(second.Runs.Select(run => run.RunId).Intersect(
            firstPage.Runs.Select(run => run.RunId), StringComparer.Ordinal));
    }

    [Fact]
    public void List_CursorMintedByGetRunEvents_IsRefusedAsAScopeMismatch()
    {
        // Both tools' cursors are the same shape of string, both come back as `nextCursor`, and both
        // are minted from a run-lifecycle call — so a host juggling two page walks has nothing but its
        // own bookkeeping to keep them apart. Without the scope, get_run_events' LINE INDEX would
        // decode here as a startedAt tick.
        var foreign = OpaqueCursor.Encode(
            CursorScopes.RunEvents, OpaqueCursor.ComposeBinding("run-x", null, null), position: 12);

        var outcome = new ListRunsOrchestrator(new StubRunRegistry())
            .List(new ListRunsRequest(Cursor: foreign));

        Assert.IsType<ListRunsOutcome.InvalidCursor>(outcome);
    }

    [Fact]
    public void List_BlankCursor_IsRefusedRatherThanTreatedAsAbsent()
    {
        // Serving page one for an unverifiable cursor would hand a caller a duplicate page dressed as
        // a continuation — the one pagination failure that silently corrupts a host's accumulation.
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath);

        Assert.IsType<ListRunsOutcome.InvalidCursor>(
            new ListRunsOrchestrator(registry).List(new ListRunsRequest(Cursor: "   ")));
    }

    // ── The SHARED cursor contract (the sprint's "one implementation, one fixture" checklist) ─────

    [Fact]
    public void Cursor_RoundTripsUnderThisToolsScopeAndBinding() =>
        OpaqueCursorContract.AssertRoundTrips(CursorScopes.ListRuns, BindingFor("trigger", "ci", sinceTicks: 42));

    [Fact]
    public void Cursor_IsOpaque_AndLeaksNeitherTheLabelNorTheSinceBoundary() =>
        OpaqueCursorContract.AssertOpaque(
            CursorScopes.ListRuns,
            BindingFor("trigger", "agent:author", sinceTicks: 638_000_000_000_000_000),
            position: 638_000_000_000_000_000,
            "trigger",
            "agent:author",
            "638000000000000000");

    [Fact]
    public void Cursor_FilterBindingIsEnforced() =>
        OpaqueCursorContract.AssertFilterBindingIsEnforced(
            CursorScopes.ListRuns,
            BindingFor("trigger", "ci", sinceTicks: null),
            BindingFor("trigger", "manual", sinceTicks: null));

    [Fact]
    public void Cursor_LabelKeyAndValueAreBoundSeparately_SoTheyCannotCollide() =>
        // `a=b` as a whole key must not bind identically to key `a` with value `b`; length-prefixed
        // separate parts are what makes that true.
        OpaqueCursorContract.AssertFilterBindingIsEnforced(
            CursorScopes.ListRuns,
            BindingFor("a", "b", sinceTicks: null),
            BindingFor("a=b", null, sinceTicks: null));

    [Fact]
    public void Cursor_ScopeIsSinglePurpose() =>
        OpaqueCursorContract.AssertScopeIsSinglePurpose(
            CursorScopes.ListRuns, CursorScopes.RunEvents, BindingFor(null, null, sinceTicks: null));

    [Fact]
    public void Cursor_MalformedInputIsRefusedWithoutThrowing() =>
        OpaqueCursorContract.AssertMalformedInputIsRefusedWithoutThrowing(
            CursorScopes.ListRuns, BindingFor("trigger", "ci", sinceTicks: null));

    [Fact]
    public void Cursor_TamperingIsRefused() =>
        OpaqueCursorContract.AssertTamperingIsRefused(
            CursorScopes.ListRuns, BindingFor("trigger", "ci", sinceTicks: null));

    // ── The registry's scan cap, surfaced as `truncated` (US-S3-06's second rider) ───────────────

    /// <summary>
    /// A registry that reports its scan as CAPPED puts <c>truncated: true</c> on every page of the
    /// walk — the fact issue #80 measured (2,000 runs invisible at 12,000, with nothing in the
    /// response saying so) and this rider closes.
    /// </summary>
    /// <remarks>
    /// <b>Driven through an injected registry rather than 10,001 real run directories, deliberately.</b>
    /// Reaching <see cref="FileRunRegistry.MaxRunsScanned"/> honestly costs ten thousand directory
    /// creations per case and would add minutes to the suite to establish a boolean that
    /// <c>RunRegistryTests.FileRunRegistry_ReportsWhetherItsScanStoppedAtTheRunCap</c> already proves
    /// against the real directory walk at a small, injected cap — including the boundary case. What
    /// THIS test owns is the other half: that the orchestrator RELAYS the flag rather than deriving
    /// one, on every page, and that a complete scan reports false.
    /// </remarks>
    [Fact]
    public void ACappedRegistryScan_SetsTruncatedOnEveryPageOfTheWalk()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => EntryStartedAt(
                $"run-{i:D32}", new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero).AddMinutes(-i)))
            .ToArray();

        var capped = new FixedListingRegistry(new RunListing(entries, scanCapped: true));
        var orchestrator = new ListRunsOrchestrator(capped);

        var first = PageOf(orchestrator, new ListRunsRequest(Limit: 2));
        Assert.True(first.Truncated);
        Assert.NotNull(first.NextCursor);

        // Every page, not just the first: each page re-scans, and each scan stops at the same bound.
        var second = PageOf(orchestrator, new ListRunsRequest(Limit: 2, Cursor: first.NextCursor));
        Assert.True(second.Truncated);

        // The complement, so the assertion above is not passing on a constant: an uncapped listing of
        // the SAME entries reports false.
        var complete = new FixedListingRegistry(RunListing.Complete(entries));
        Assert.False(PageOf(new ListRunsOrchestrator(complete), new ListRunsRequest(Limit: 2)).Truncated);
    }

    /// <summary>
    /// <c>truncated</c> and <c>nextCursor</c> answer DIFFERENT questions, and the combination the
    /// docs tell a host to watch for — capped scan, no cursor — is reachable and correct.
    /// </summary>
    [Fact]
    public void ACappedScanWhoseLastPageIsComplete_ReportsTruncatedWithNoCursor()
    {
        var entries = Enumerable.Range(0, 2)
            .Select(i => EntryStartedAt(
                $"run-{i:D32}", new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero).AddMinutes(-i)))
            .ToArray();

        var result = PageOf(
            new ListRunsOrchestrator(new FixedListingRegistry(new RunListing(entries, scanCapped: true))),
            new ListRunsRequest(Limit: 200));

        Assert.Null(result.NextCursor);
        Assert.True(result.Truncated);
    }

    /// <summary>A registry that serves one fixed listing — the seam the two cases above need.</summary>
    private sealed class FixedListingRegistry(RunListing listing) : IRunRegistry
    {
        public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
            throw new NotSupportedException("This registry exists to be listed, never written.");

        public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
            throw new NotSupportedException("This registry exists to be listed, never written.");

        public RunRegistryEntry? TryGetRun(string runId) =>
            listing.FirstOrDefault(entry => string.Equals(entry.RunId, runId, StringComparison.Ordinal));

        public RunListing ListRuns() => listing;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Composes a binding by CALLING <c>ListRunsOrchestrator.ComposeBinding</c> — the production
    /// method itself, so the contract cases above exercise this tool's real binding shape and cannot
    /// drift from it.
    /// </summary>
    /// <remarks>
    /// This used to be a hand-copied replica assembling the same three parts in the same order (a
    /// gatekeeper review's MINOR finding). A replica keeps passing after the production method changes
    /// the ORDER of its parts, adds one, or stops binding <c>since</c> as parsed ticks — every one of
    /// which is a real cursor-compatibility change that these cases exist to catch. <c>internal</c>
    /// plus <c>InternalsVisibleTo</c> is what makes calling it possible, and the same arrangement
    /// <c>ValidateArguments</c>/<c>BuildPage</c> already use for the same reason.
    /// </remarks>
    private static string BindingFor(string? labelKey, string? labelValue, long? sinceTicks) =>
        ListRunsOrchestrator.ComposeBinding(
            labelKey,
            labelValue,
            sinceTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);

    /// <summary>
    /// One completed entry at an EXACT <paramref name="startedAtUtc"/> — the only way to build the
    /// tied-timestamp snapshot above, since every registry in this repo refuses to produce one.
    /// </summary>
    private static RunRegistryEntry EntryStartedAt(string runId, DateTimeOffset startedAtUtc) =>
        new(
            RunId: runId,
            Status: RunRegistryStatus.Completed,
            Outcome: nameof(RunVerdict.Pass),
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: startedAtUtc.AddSeconds(1),
            SpecPaths: ["stub.e2e.yaml"],
            EventsFilePath: EventsFilePath,
            Labels: new Dictionary<string, string>(StringComparer.Ordinal));

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static ListRunsResult PageOf(IRunRegistry registry, ListRunsRequest request) =>
        PageOf(new ListRunsOrchestrator(registry), request);

    private static ListRunsResult PageOf(ListRunsOrchestrator orchestrator, ListRunsRequest request) =>
        Assert.IsType<ListRunsOutcome.Paged>(orchestrator.List(request)).Result;
}
