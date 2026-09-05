using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// US-S3-03's <c>get_run_status</c> at the unit seam: the argument bounds, the registry lookup, and
/// the acceptance criterion that matters most — the answer IS the registry's entry, not a copy of it
/// that could come to disagree.
/// </summary>
public class GetRunStatusOrchestratorTests
{
    private const string EventsFilePath = "/tmp/vouchfx/run-events.jsonl";

    /// <summary>ASCII BEL — a control character, written as a code point so no test source file carries one.</summary>
    private static readonly char Bell = (char)0x07;

    /// <summary>ASCII ESC — the lead byte of an ANSI terminal escape sequence.</summary>
    private static readonly char Escape = (char)0x1B;

    [Fact]
    public void Get_CompletedRun_ReturnsTheRegistryEntryItself_NotACopyOfIt()
    {
        var registry = new StubRunRegistry();
        var recorded = registry.AddCompletedRun(EventsFilePath, nameof(RunVerdict.Pass));

        var outcome = new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId));

        var found = Assert.IsType<GetRunStatusOutcome.Found>(outcome);

        // AC-001: "sourced from the persisted registry — not a second, divergent status model". This
        // is REFERENCE equality on purpose: any projection, however faithful today, is a second
        // declaration of these fields that a later edit can move out from under the registry. Nothing
        // weaker would catch that.
        //
        // The ONE legitimate way this can become a `with`-copy is a spec path that needed escaping on
        // egress (see Get_RunWhoseSpecPathCarriesControlCharacters_ReturnsThePathSanitised, and its
        // companion pinning this identity for the ordinary case). This fixture's default path is plain
        // ASCII, so nothing is copied here.
        Assert.Same(recorded, found.Result.Run);
    }

    [Fact]
    public void Get_CompletedRun_ReportsTheStatusAndOutcomeTheRegistryHolds()
    {
        // Gherkin (US-S3-03): "Given a run has completed with outcome Pass … Then run.status is
        // 'completed' and run.outcome is 'Pass'."
        var registry = new StubRunRegistry();
        var recorded = registry.AddCompletedRun(EventsFilePath, nameof(RunVerdict.Pass));

        var found = Assert.IsType<GetRunStatusOutcome.Found>(
            new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId)));

        Assert.Equal(RunRegistryStatus.Completed, found.Result.Run.Status);
        Assert.Equal(nameof(RunVerdict.Pass), found.Result.Run.Outcome);
        Assert.NotNull(found.Result.Run.FinishedAtUtc);
    }

    [Fact]
    public void Get_RunStillInFlight_IsReturnedAsRunningWithNoOutcome_NotWithheld()
    {
        // A `running` entry is a legitimate answer, not an absence: polling a run in flight is the
        // whole reason this tool exists. Contrast explain_run, which deliberately defaults PAST an
        // in-flight run because its events file is still being written.
        var registry = new StubRunRegistry();
        var recorded = registry.AddRunningRun(EventsFilePath);

        var found = Assert.IsType<GetRunStatusOutcome.Found>(
            new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId)));

        Assert.Equal(RunRegistryStatus.Running, found.Result.Run.Status);
        Assert.Null(found.Result.Run.Outcome);
        Assert.Null(found.Result.Run.FinishedAtUtc);
    }

    [Fact]
    public void Get_CancelledRun_ReportsTheCancelledStatusAndItsInconclusiveOutcome()
    {
        var registry = new StubRunRegistry();
        var recorded = registry.AddCancelledRun(EventsFilePath);

        var found = Assert.IsType<GetRunStatusOutcome.Found>(
            new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId)));

        Assert.Equal(RunRegistryStatus.Cancelled, found.Result.Run.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), found.Result.Run.Outcome);
    }

    [Fact]
    public void Get_UnknownRunId_IsRunNotFound_AndTheMessageNamesWhereTheRegistryLives()
    {
        var registry = new StubRunRegistry();
        registry.AddCompletedRun(EventsFilePath);

        var outcome = new GetRunStatusOrchestrator(registry)
            .Get(new GetRunStatusRequest("run-00000000000000000000000000000000"));

        var notFound = Assert.IsType<GetRunStatusOutcome.RunNotFound>(outcome);

        // The registry's SCOPE is the actionable part of this refusal: a host that started the run
        // against a server launched without --workspace is looking for a run a restart discarded.
        Assert.Contains("--workspace", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("list_runs", notFound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_UnknownRunIdCarryingControlCharacters_SanitisesTheEcho()
    {
        var hostileRunId = "run-" + Bell + Escape + "[2Jevil";

        var notFound = Assert.IsType<GetRunStatusOutcome.RunNotFound>(
            new GetRunStatusOrchestrator(new StubRunRegistry()).Get(new GetRunStatusRequest(hostileRunId)));

        // The id reaches a caller-facing message without ever having been verified as one this server
        // minted — it is, by definition, not in the registry — so it goes through the same echo
        // sanitiser every other unverified caller string does: every character outside printable ASCII
        // comes back as a literal six-character escape, so an ANSI clear-screen sequence cannot reach
        // a terminal that renders this message.
        Assert.DoesNotContain(Bell, notFound.Message);
        Assert.DoesNotContain(Escape, notFound.Message);
        Assert.Contains("u0007", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("u001B", notFound.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_MissingOrBlankRunId_IsAnInvalidArgument_NotARunNotFound(string? runId)
    {
        // Deliberately NOT VFX-E-1505: "you sent no id" and "no run has that id" are different facts
        // with different fixes, and collapsing them would tell a host to go looking for a run when the
        // problem is its own request. Mirrors get_run_events' identical split.
        var outcome = new GetRunStatusOrchestrator(new StubRunRegistry()).Get(new GetRunStatusRequest(runId));

        var invalid = Assert.IsType<GetRunStatusOutcome.InvalidArgument>(outcome);
        Assert.Contains("runId", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_OverLongRunId_IsRefusedBeforeTheRegistryIsTouched()
    {
        var registry = new ThrowingOnLookupRegistry();

        var outcome = new GetRunStatusOrchestrator(registry)
            .Get(new GetRunStatusRequest(new string('r', RunLifecycleLimits.MaxRunIdChars + 1)));

        Assert.IsType<GetRunStatusOutcome.InvalidArgument>(outcome);

        // The bound exists so a hostile argument never reaches a lookup or a message; a registry that
        // throws on any lookup is how that is proven rather than assumed.
        Assert.False(registry.WasQueried);
    }

    // ── specPaths egress (a security review's MINOR finding) ─────────────────────────────────────

    /// <summary>
    /// A glob-resolved suite path carrying control characters comes back ESCAPED — the one
    /// transformation between the registry's entry and this tool's response.
    /// </summary>
    /// <remarks>
    /// Non-vacuous by construction: the planted name carries an ANSI clear-screen sequence, which is
    /// what a third-party file name can do to a host's terminal if it is relayed raw. Since US-S3-02
    /// these paths arrive through globs, so their file-name half is whatever was on disk rather than
    /// anything the caller typed — and on Unix a file name may contain any byte but <c>/</c> and NUL.
    /// The REGISTRY still stores the raw path (that is the record of what actually ran, and an escaped
    /// path would not open); only the wire representation is escaped.
    /// </remarks>
    [Fact]
    public void Get_RunWhoseSpecPathCarriesControlCharacters_ReturnsThePathSanitised()
    {
        var hostilePath = "/repo/e2e/" + Escape + "[2J" + Bell + "evil.e2e.yaml";

        var registry = new StubRunRegistry();
        var recorded = registry.AddCompletedRun(EventsFilePath, specPaths: [hostilePath]);

        var found = Assert.IsType<GetRunStatusOutcome.Found>(
            new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId)));

        var served = Assert.Single(found.Result.Run.SpecPaths);
        Assert.DoesNotContain(Escape, served);
        Assert.DoesNotContain(Bell, served);
        Assert.Contains("u001B", served, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("u0007", served, StringComparison.Ordinal);

        // Escaped, not dropped: the host must still be able to tell WHICH file ran.
        Assert.Contains("evil.e2e.yaml", served, StringComparison.Ordinal);

        // The stored entry is untouched — the escaping is at egress only.
        Assert.Equal([hostilePath], registry.TryGetRun(recorded.RunId)!.SpecPaths);

        // Every other field still comes straight off the entry: the transformation is one field wide.
        Assert.Equal(recorded.RunId, found.Result.Run.RunId);
        Assert.Equal(recorded.Status, found.Result.Run.Status);
        Assert.Equal(recorded.Outcome, found.Result.Run.Outcome);
        Assert.Equal(recorded.StartedAtUtc, found.Result.Run.StartedAtUtc);
        Assert.Equal(recorded.EventsFilePath, found.Result.Run.EventsFilePath);
    }

    /// <summary>
    /// The ordinary path — nothing to escape — comes back as the registry's OWN instance, which is
    /// how AC-001's "not a second, divergent status model" stays literally true rather than merely
    /// nearly so.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="Get_CompletedRun_ReturnsTheRegistryEntryItself_NotACopyOfIt"/>,
    /// stated separately because the egress escaping introduced the only way that reference identity
    /// can legitimately break. Both branches are pinned so neither can be "simplified" into always
    /// copying (which would remove the guarantee) or never copying (which would remove the escaping).
    /// </remarks>
    [Fact]
    public void Get_RunWhoseSpecPathsNeedNoEscaping_ReturnsTheRegistryEntryInstanceUnchanged()
    {
        var registry = new StubRunRegistry();
        var recorded = registry.AddCompletedRun(EventsFilePath, specPaths: ["/repo/e2e/orders.e2e.yaml"]);

        var found = Assert.IsType<GetRunStatusOutcome.Found>(
            new GetRunStatusOrchestrator(registry).Get(new GetRunStatusRequest(recorded.RunId)));

        Assert.Same(recorded, found.Result.Run);
    }
}
