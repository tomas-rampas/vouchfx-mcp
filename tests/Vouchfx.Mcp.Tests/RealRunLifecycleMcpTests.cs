using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-03's three tools over the REAL MCP wire protocol: <c>get_run_status</c>, <c>cancel_run</c>
/// and <c>list_runs</c>, including every Gherkin scenario the story states — most importantly the
/// negative one, that a cancelled run reports <c>Inconclusive</c> and never <c>Fail</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Real*</c> means the real wire, never the real engine.</b> The harness hosts the production
/// server (through the same <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/> production
/// startup uses) over in-memory paired streams, with a <see cref="FakeSuiteRunner"/> in place of the
/// <c>vouchfx</c> CLI — so nothing here needs an engine or Docker, per CLAUDE.md's hard rule.
/// </para>
/// <para>
/// <b>The cancellation cases are deterministic, with no sleeps anywhere.</b> A run is parked by a
/// runner that waits on its own cancellation token, and the test learns the run is in flight by
/// POLLING <c>list_runs</c> over the wire until a <c>running</c> entry appears — which is itself part
/// of what is being proven (a read-only tool answering while a run is in progress), so the handshake
/// costs no extra machinery and asserts something on the way past.
/// </para>
/// </remarks>
public class RealRunLifecycleMcpTests : IDisposable
{
    private const string ValidSuiteYaml = """
        metadata:
          name: "Orders API health smoke test"
          owner: "platform-team"

        steps:
          - id: check-health
            type: http.rest
            description: "Confirms the health endpoint responds successfully."
            target: orders-api
            method: GET
            path: /health
        """;

    private const string PassingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    /// <summary>
    /// A failing stream, used ONLY to prove the negative scenario is not vacuous: a run that would
    /// have reported <c>Fail</c> had it been allowed to finish still reports <c>Inconclusive</c> when
    /// it is cancelled first.
    /// </summary>
    private const string FailingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"FAIL","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
        """;

    private readonly string _sandbox;
    private readonly string _suitePath;

    public RealRunLifecycleMcpTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);

        _suitePath = Path.Combine(_sandbox, "orders.e2e.yaml");
        File.WriteAllText(_suitePath, ValidSuiteYaml);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    // ── get_run_status ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The story's first Gherkin scenario, over the wire and off a REAL <c>run_suite</c> call —
    /// "Given a run has completed with outcome 'Pass' … Then run.status is 'completed' And
    /// run.outcome is 'Pass'."
    /// </summary>
    [Fact]
    public async Task GetRunStatus_AfterARealRunSuiteCall_ReportsTheRunTheRegistryRecorded()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0));

        var run = await CallAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
        Assert.False(run.IsError ?? false);

        var runId = Payload(run).GetProperty("runId").GetString();
        Assert.NotNull(runId);

        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = runId }, cts.Token);
        Assert.False(status.IsError ?? false);

        var recorded = Payload(status).GetProperty("run");
        Assert.Equal(runId, recorded.GetProperty("runId").GetString());
        Assert.Equal(RunRegistryStatus.Completed, recorded.GetProperty("status").GetString());
        Assert.Equal(nameof(RunVerdict.Pass), recorded.GetProperty("outcome").GetString());
        Assert.NotEqual(JsonValueKind.Null, recorded.GetProperty("finishedAt").ValueKind);

        // The hand-off that makes this tool useful rather than merely informative: the events path it
        // reports is the one run_suite wrote and the one get_run_events/explain_run read. Asserting
        // equality here is what proves "one registry entry, one source of truth" over the wire rather
        // than at the unit seam.
        Assert.Equal(
            Payload(run).GetProperty("eventsFilePath").GetString(),
            recorded.GetProperty("eventsFilePath").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task GetRunStatus_ReportsTheLabelsRunSuiteRecorded_SoListRunsLabelFilterIsUsable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0));

        var run = await CallAsync(
            harness,
            "run_suite",
            new()
            {
                ["path"] = _suitePath,
                ["labels"] = new Dictionary<string, object?> { ["trigger"] = "agent:author", ["iteration"] = "3" },
            },
            cts.Token);

        var runId = Payload(run).GetProperty("runId").GetString();
        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = runId }, cts.Token);

        var labels = Payload(status).GetProperty("run").GetProperty("labels");
        Assert.Equal("agent:author", labels.GetProperty("trigger").GetString());
        Assert.Equal("3", labels.GetProperty("iteration").GetString());
    }

    /// <summary>
    /// A recorded spec path carrying an ANSI escape reaches the host ESCAPED — the one transformation
    /// between the registry's entry and this tool's response, asserted on the actual serialised JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is planted through an injected registry rather than by creating such a file, because
    /// Windows will not create one and the exposure is a Unix one: since US-S3-02 these paths can come
    /// from a GLOB, so their file-name half is whatever was on disk, and on Linux and macOS that may
    /// be any byte but <c>/</c> and NUL. Planting it is what lets the assertion run on every platform
    /// while still exercising the real serialisation path end to end.
    /// </para>
    /// <para>
    /// Asserted on the raw JSON text, not on a parsed string: the question is what BYTES reach the
    /// host's terminal or log, and a parsed value would answer a different one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetRunStatus_SpecPathCarryingAnAnsiEscape_ReachesTheHostEscaped()
    {
        const char escape = (char)0x1B;
        var hostilePath = "/repo/e2e/" + escape + "[2Jevil.e2e.yaml";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var registry = new StubRunRegistry();
        var recorded = registry.AddCompletedRun(
            Path.Combine(_sandbox, "events.jsonl"), specPaths: [hostilePath]);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = recorded.RunId }, cts.Token);
        Assert.False(status.IsError ?? false);

        var served = Assert.Single(
            Payload(status).GetProperty("run").GetProperty("specPaths").EnumerateArray()).GetString();
        Assert.NotNull(served);

        Assert.DoesNotContain(escape, served);
        Assert.Contains("u001B", served, StringComparison.OrdinalIgnoreCase);

        // Escaped, not dropped: the host must still be able to tell which file ran.
        Assert.Contains("evil.e2e.yaml", served, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRunStatus_UnknownRunId_IsASingleVfxE1505ErrorObject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallAsync(
            harness, "get_run_status", new() { ["runId"] = "run-00000000000000000000000000000000" }, cts.Token);

        Assert.True(result.IsError ?? false);

        var error = Payload(result);
        Assert.Equal(VfxCodeCatalogue.RunNotFound, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    // ── cancel_run ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The story's mandatory negative scenario</b>: "Given a run is currently active and has not
    /// yet reached a step failure … Then after the grace period the run's final status is 'cancelled'
    /// And the run's outcome is 'Inconclusive' And the run's outcome is never 'Fail'."
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Non-vacuous by construction:</b> the parked runner is scripted to write a FAILING event
    /// stream, so a `Fail` is the verdict this run would have reported had it been allowed to finish.
    /// Nothing but the cancellation makes the answer <c>Inconclusive</c> — a test using a passing
    /// stream would have proven only that a cancelled run does not report <c>Pass</c>.
    /// </para>
    /// <para>
    /// <b>The graceful stop itself is not re-proven here, deliberately.</b> <c>cancel_run</c> fires
    /// the very cancellation token <c>ISuiteRunner.RunAsync</c> is already running under (asserted at
    /// the unit seam by <c>CancelRunOrchestratorTests</c>), and what
    /// <c>VouchfxCliSuiteRunner</c> does with that token — close the child's stdin per
    /// <c>--shutdown-on-stdin-eof</c>, wait the grace period, then kill the process tree — is
    /// exercised against a REAL child process by <c>VouchfxCliSuiteRunnerTests</c> and the
    /// <c>StdinEofChildFixture</c>. Repeating it here would need the real engine. What this test adds
    /// is the leg those two cannot cover: that the token reaches the runner from a SEPARATE MCP call,
    /// and that the taxonomy survives the trip.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CancelRun_ActiveRun_EndsItAsCancelledWithInconclusive_AndNeverFail()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.ObservingCancellation(
                TimeSpan.Zero, () => stopRequested.TrySetResult()));

        // Park a run. Deliberately NOT awaited yet — it cannot complete until something cancels it,
        // which is the whole point.
        var runCall = CallAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);

        var runId = await WaitForRunningRunIdAsync(harness, cts.Token);

        var cancel = await CallAsync(harness, "cancel_run", new() { ["runId"] = runId }, cts.Token);
        Assert.False(cancel.IsError ?? false);
        Assert.Equal(CancelRunStatus.Cancelled, Payload(cancel).GetProperty("status").GetString());

        // The runner observed the stop through its own token — the graceful-stop signal, not a kill
        // this tool performed itself.
        await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);

        var run = await runCall;
        Assert.False(run.IsError ?? false);

        var runPayload = Payload(run);
        Assert.Equal(nameof(RunVerdict.Inconclusive), runPayload.GetProperty("verdict").GetString());
        Assert.NotEqual(nameof(RunVerdict.Fail), runPayload.GetProperty("verdict").GetString());

        // `cancelled`, not `timedOut`: before this story the only discriminator EDGE-002 had was the
        // MCP caller's own token, so a cancel_run-driven stop would have been reported as a timeout.
        Assert.True(runPayload.GetProperty("cancelled").GetBoolean());
        Assert.False(runPayload.GetProperty("timedOut").GetBoolean());

        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = runId }, cts.Token);
        var recorded = Payload(status).GetProperty("run");

        // US-S3-01 declared `cancelled` and left it unreachable; this story is what writes it.
        Assert.Equal(RunRegistryStatus.Cancelled, recorded.GetProperty("status").GetString());
        Assert.Equal(nameof(RunVerdict.Inconclusive), recorded.GetProperty("outcome").GetString());
        Assert.NotEqual(nameof(RunVerdict.Fail), recorded.GetProperty("outcome").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    /// <summary>
    /// The same scenario with a runner whose suite would genuinely have FAILED — the non-vacuity
    /// proof for the case above, and the direct answer to "is Inconclusive just what this fixture
    /// always says?".
    /// </summary>
    [Fact]
    public async Task CancelRun_ActiveRunThatWouldHaveFailed_StillReportsInconclusive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Anti-vacuity leg one: prove this fixture's stream really does produce Fail when the run is
        // allowed to finish.
        await using (var control = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], FailingEventsFileContent, exitCode: 1)))
        {
            var finished = await CallAsync(control, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
            Assert.Equal(nameof(RunVerdict.Fail), Payload(finished).GetProperty("verdict").GetString());
        }

        // Leg two: the identical stream, cancelled before the run reaches it.
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.ObservingCancellation(TimeSpan.Zero, static () => { }));

        var runCall = CallAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
        var runId = await WaitForRunningRunIdAsync(harness, cts.Token);

        await CallAsync(harness, "cancel_run", new() { ["runId"] = runId }, cts.Token);

        var run = await runCall;
        Assert.Equal(nameof(RunVerdict.Inconclusive), Payload(run).GetProperty("verdict").GetString());
    }

    /// <summary>
    /// The story's third Gherkin scenario: "Given a run has already completed … Then the tool
    /// result's isError field is false And status is 'already_finished'."
    /// </summary>
    [Fact]
    public async Task CancelRun_AlreadyFinishedRun_IsReportedNotErrored()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0));

        var run = await CallAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
        var runId = Payload(run).GetProperty("runId").GetString();

        var cancel = await CallAsync(harness, "cancel_run", new() { ["runId"] = runId }, cts.Token);

        // isError FALSE — asserted explicitly because the AC names it: a host polling a run it means
        // to cancel loses this race routinely, and an error would make ordinary behaviour look broken.
        Assert.False(cancel.IsError ?? false);
        Assert.Equal(CancelRunStatus.AlreadyFinished, Payload(cancel).GetProperty("status").GetString());
        Assert.Equal(runId, Payload(cancel).GetProperty("runId").GetString());

        // And the run's own record is untouched: cancelling a finished run must not rewrite the
        // verdict it genuinely reached.
        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = runId }, cts.Token);
        Assert.Equal(RunRegistryStatus.Completed, Payload(status).GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(nameof(RunVerdict.Pass), Payload(status).GetProperty("run").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task CancelRun_UnknownRunId_IsASingleVfxE1505ErrorObject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallAsync(
            harness, "cancel_run", new() { ["runId"] = "run-00000000000000000000000000000000" }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal(VfxCodeCatalogue.RunNotFound, Payload(result).GetProperty("code").GetString());
    }

    /// <summary>
    /// The phantom-entry stance over the wire: an entry left saying <c>running</c> by a server that
    /// never wrote its completion, with the workspace's lock free. <c>VFX-E-1508</c>, not a pretended
    /// cancellation and not a fabricated <c>already_finished</c>.
    /// </summary>
    [Fact]
    public async Task CancelRun_RunningEntryWithAFreeWorkspaceLock_IsReportedAsStaleResidue()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var root = Path.Combine(_sandbox, "phantom-workspace");
        Directory.CreateDirectory(root);
        var workspace = Workspace.Resolve(root);

        // Written through the PRODUCTION registry, in production order, and then simply abandoned —
        // exactly the residue a SIGKILLed server leaves, without needing to kill one.
        var registry = new FileRunRegistry(workspace.OutputDir, workspace);
        var abandoned = registry.StartRun([_suitePath]);

        await using var harness = await McpTestHarness.StartAsync(cts.Token, workspace: workspace);

        // Read-only tools report it exactly as the registry has it — deliberately, since establishing
        // liveness means taking the lock and they must never be able to make a run_suite call fail.
        var status = await CallAsync(harness, "get_run_status", new() { ["runId"] = abandoned.RunId }, cts.Token);
        Assert.False(status.IsError ?? false);
        Assert.Equal(RunRegistryStatus.Running, Payload(status).GetProperty("run").GetProperty("status").GetString());

        // cancel_run is where the question is settled.
        var cancel = await CallAsync(harness, "cancel_run", new() { ["runId"] = abandoned.RunId }, cts.Token);
        Assert.True(cancel.IsError ?? false);

        var error = Payload(cancel);
        Assert.Equal(VfxCodeCatalogue.StaleRunEntry, error.GetProperty("code").GetString());

        // Not retryable: no reaper will clear this, so telling a host to try again would be a lie.
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    // ── list_runs ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The story's fourth Gherkin scenario at its own numbers: "Given 250 runs exist in the registry
    /// … When the host calls list_runs with limit 100 … Then exactly 100 runs are returned And
    /// nextCursor is present … Then the next 100 runs are returned, none overlapping the first page."
    /// </summary>
    /// <remarks>
    /// The 250 runs are seeded through an injected registry rather than by making 250 <c>run_suite</c>
    /// calls: this test's subject is the PAGINATION contract over the wire, and driving a quarter of a
    /// thousand runs through the whole gate sequence would add minutes to the suite to prove nothing
    /// this does not.
    /// </remarks>
    [Fact]
    public async Task ListRuns_With250Runs_PaginatesWithAnOpaqueCursorAndNoOverlap()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var registry = new StubRunRegistry();
        for (var i = 0; i < 250; i++)
        {
            registry.AddCompletedRun(Path.Combine(_sandbox, $"events-{i}.jsonl"));
        }

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var first = Payload(await CallAsync(harness, "list_runs", new() { ["limit"] = 100 }, cts.Token));
        Assert.Equal(100, first.GetProperty("runs").GetArrayLength());

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        // Opaque: a host must be able to round-trip it through its own storage, and must learn nothing
        // from it. base64url only — no padding, nothing a JSON string or a URL has to escape.
        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);

        var second = Payload(await CallAsync(
            harness, "list_runs", new() { ["limit"] = 100, ["cursor"] = cursor }, cts.Token));
        Assert.Equal(100, second.GetProperty("runs").GetArrayLength());

        var firstIds = IdsOf(first);
        var secondIds = IdsOf(second);
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));

        var third = Payload(await CallAsync(
            harness,
            "list_runs",
            new() { ["limit"] = 100, ["cursor"] = second.GetProperty("nextCursor").GetString() },
            cts.Token));
        Assert.Equal(50, third.GetProperty("runs").GetArrayLength());

        // Absent, not null — the field is omitted entirely on the last page (JsonIgnore
        // WhenWritingNull), so a host testing for presence gets the right answer.
        Assert.False(third.TryGetProperty("nextCursor", out _));

        Assert.Equal(250, firstIds.Concat(secondIds).Concat(IdsOf(third)).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task ListRuns_ReturnsExactlyTheFiveFieldsSpecSection58Names()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var registry = new StubRunRegistry();
        registry.AddCompletedRun(Path.Combine(_sandbox, "events.jsonl"));

        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        var runs = Payload(await CallAsync(harness, "list_runs", null, cts.Token)).GetProperty("runs");
        var item = Assert.Single(runs.EnumerateArray());

        Assert.Equal(
            ["finishedAt", "outcome", "runId", "startedAt", "status"],
            item.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ListRuns_LabelFilter_SelectsOnlyMatchingRuns()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0));

        await CallAsync(
            harness,
            "run_suite",
            new()
            {
                ["path"] = _suitePath,
                ["labels"] = new Dictionary<string, object?> { ["trigger"] = "ci" },
            },
            cts.Token);

        var wanted = await CallAsync(
            harness,
            "run_suite",
            new()
            {
                ["path"] = _suitePath,
                ["labels"] = new Dictionary<string, object?> { ["trigger"] = "agent:author" },
            },
            cts.Token);
        var wantedRunId = Payload(wanted).GetProperty("runId").GetString();
        Assert.NotNull(wantedRunId);

        var filtered = Payload(await CallAsync(
            harness, "list_runs", new() { ["label"] = "trigger=agent:author" }, cts.Token));

        Assert.Equal([wantedRunId], IdsOf(filtered));

        // And the bare-key form widens to both, which is the whole reason it exists.
        var byKey = Payload(await CallAsync(harness, "list_runs", new() { ["label"] = "trigger" }, cts.Token));
        Assert.Equal(2, byKey.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task ListRuns_CursorFromGetRunEvents_IsRefusedAsVfxE1506_NotDecodedIntoAWrongPage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var eventsPath = Path.Combine(_sandbox, "events.jsonl");
        File.WriteAllText(eventsPath, PassingEventsFileContent);

        var registry = StubRunRegistry.WithCompletedRun(eventsPath);
        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: registry);

        // A REAL get_run_events cursor, minted over the wire by the other paginated tool — not a
        // hand-built string, so this proves the scope discrimination rather than base64 validation.
        var events = Payload(await CallAsync(
            harness,
            "get_run_events",
            new() { ["runId"] = registry.ListRuns()[0].RunId, ["limit"] = 1 },
            cts.Token));
        var foreignCursor = events.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(foreignCursor));

        var result = await CallAsync(harness, "list_runs", new() { ["cursor"] = foreignCursor }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal(VfxCodeCatalogue.InvalidCursor, Payload(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListRuns_OutOfRangeLimit_IsRefusedAsVfxE1006_NotClamped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await CallAsync(harness, "list_runs", new() { ["limit"] = 5000 }, cts.Token);

        Assert.True(result.IsError ?? false);
        Assert.Equal(VfxCodeCatalogue.InvalidToolArgument, Payload(result).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListRuns_EmptyRegistry_IsAnEmptySuccessfulPage_NotAnError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token, runRegistry: new StubRunRegistry());

        var result = await CallAsync(harness, "list_runs", null, cts.Token);

        Assert.False(result.IsError ?? false);
        Assert.Empty(Payload(result).GetProperty("runs").EnumerateArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <c>list_runs</c> over the wire until a <c>running</c> entry appears, and returns its id.
    /// </summary>
    /// <remarks>
    /// A deterministic handshake rather than a sleep — and one that asserts something on the way past,
    /// since a read-only tool answering while a run is in flight is itself part of spec §4.6's
    /// contract. Bounded by the caller's own token, so a run that never starts fails the test by
    /// timing out rather than by hanging forever.
    /// </remarks>
    private static async Task<string> WaitForRunningRunIdAsync(
        McpTestHarness harness, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = Payload(await CallAsync(harness, "list_runs", new() { ["limit"] = 10 }, cancellationToken));

            foreach (var run in page.GetProperty("runs").EnumerateArray())
            {
                if (string.Equals(
                        run.GetProperty("status").GetString(), RunRegistryStatus.Running, StringComparison.Ordinal))
                {
                    return run.GetProperty("runId").GetString()!;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Unreachable.");
    }

    /// <summary>
    /// One tool call, as a <see cref="Task{TResult}"/>.
    /// </summary>
    /// <remarks>
    /// The SDK returns a <see cref="ValueTask{TResult}"/>, which may be consumed exactly once and
    /// must not be stored — and the cancellation cases here deliberately hold an unfinished
    /// <c>run_suite</c> call in a local while they drive other tools against it. <c>AsTask</c> is the
    /// documented way to make that legal rather than merely usually-fine (CA2012).
    /// </remarks>
    private static Task<CallToolResult> CallAsync(
        McpTestHarness harness,
        string toolName,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments ?? [], cancellationToken: cancellationToken).AsTask();

    private static JsonElement Payload(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent.Value;
    }

    private static string[] IdsOf(JsonElement page) =>
        [.. page.GetProperty("runs").EnumerateArray().Select(run => run.GetProperty("runId").GetString()!)];
}
