using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 9 (REQ-010) end to end, through the same in-memory MCP harness the other
/// <c>Real*McpTests</c> classes use: vouchfx-mcp is a RELAY, never a redaction authority and never
/// a secret resolver. The engine (§17) is the sole redaction authority — the <c>--events</c> JSON
/// Lines fields this server parses are already redacted at their source, and every field this
/// server relays is bounded and control-character-sanitised for display, never re-redacted or
/// resolved. What REQ-010 additionally demands, and what these tests lock as a regression guard, is
/// that this server never independently surfaces ITS OWN process environment through any response
/// or progress notification, across the whole tool surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests prove, and what they deliberately do NOT prove:</b> a sentinel value placed
/// in THIS SERVER's own process environment — never read by any production code path (the todo 9
/// audit found exactly two <see cref="Environment.GetEnvironmentVariable(string)"/> call sites in
/// this whole codebase, both in <c>VouchfxCliPathResolver</c>, both reading only <c>PATH</c>/
/// <c>PATHEXT</c> to locate the <c>vouchfx</c> executable, neither ever echoed into a response) —
/// must never surface in any tool response or progress notification. That is a REGRESSION LOCK
/// against a future change accidentally dumping environment state into agent-facing output. It is
/// NOT the real end-to-end proof that a live <c>${secret:env/...}</c> reference resolved by the
/// REAL engine CLI never leaks; that proof needs the real CLI, Docker, and a secret-using sample
/// suite, and is DEFERRED to todo 13 — this todo is deliberately unit-level only.
/// </para>
/// <para>
/// <b>The child CLI process correctly INHERITS this server's environment</b> (never an explicit
/// <c>ProcessStartInfo.Environment</c> injection — see <see cref="SecretHygieneSourceGuardTests"/>)
/// so that a suite's own <c>${secret:env/X}</c> reference can resolve inside the engine at
/// step-execution time (§17). These tests do not, and must not, assert that inheritance is somehow
/// disabled — a real <c>run_suite</c> call legitimately hands the child CLI this server's full
/// environment, sentinel included. They assert only that THIS SERVER never independently surfaces
/// that environment through a response/notification channel of its own — the FakeSuiteRunner/
/// FakeVouchfxCli used throughout never spawn a real child at all, so there is nothing here that
/// could accidentally prove the wrong thing by relying on inheritance succeeding.
/// </para>
/// </remarks>
public class RealSecretHygieneMcpTests
{
    private const string SentinelVariableName = "VOUCHFX_MCP_TEST_SENTINEL";

    // ── B1: the whole tool surface, one sentinel, one sweep ─────────────────────────────────────

    [Fact]
    public async Task AcrossTheWholeToolSurface_ServerEnvironmentSentinel_NeverAppearsInAnyResponseOrNotification()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sentinel = UniqueSentinel();

        const string startingLine = "Starting DCP...";
        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var runner = FakeSuiteRunner.Succeeding([startingLine], events, exitCode: 0);

        // Set BEFORE the harness is even constructed (a review fix): a regression that somehow
        // snapshotted the environment at SERVER CONSTRUCTION time, rather than reading it live at
        // response-building time, would otherwise pass this test falsely by missing a sentinel set
        // only after StartAsync had already returned.
        SetSentinel(sentinel);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

            var progressUpdates = new ConcurrentBag<ProgressNotificationValue>();
            var progress = new Progress<ProgressNotificationValue>(progressUpdates.Add);

            // Every one of the six advertised tools, driven through the REAL MCP round trip —
            // deliberately not just the CLI-dependent ones (run_suite, explain_run): REQ-010 covers
            // "any tool result", and the audit's whole point was confirming the schema/catalogue/
            // docs tools are just as clean as the CLI-facing ones, not assuming it. Each call also
            // asserts a MINIMAL positive-content check, not just IsError:false (a review fix): a
            // silent validation-worker-failed/tool-error would still sweep clean of the sentinel and
            // pass this test falsely — proving SUBSTANTIVE output came back is what makes the
            // sentinel's absence below meaningful, rather than a coincidence of nothing having run.
            var validate = await harness.Client.CallToolAsync(
                "validate_suite",
                new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
                cancellationToken: cts.Token);
            Assert.False(validate.IsError ?? false);
            var validatePayload = validate.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.True(validatePayload.GetProperty("valid").GetBoolean());

            var listTypes = await harness.Client.CallToolAsync("list_step_types", cancellationToken: cts.Token);
            Assert.False(listTypes.IsError ?? false);
            var listTypesPayload = listTypes.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            var allTypes = listTypesPayload.GetProperty("families").EnumerateArray()
                .SelectMany(f => f.GetProperty("types").EnumerateArray())
                .Select(t => t.GetProperty("type").GetString())
                .ToArray();
            Assert.Contains("http.rest", allTypes);

            var describe = await harness.Client.CallToolAsync(
                "describe_step_type",
                new Dictionary<string, object?> { ["type"] = "http.rest" },
                cancellationToken: cts.Token);
            Assert.False(describe.IsError ?? false);
            var describePayload = describe.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("http.rest", describePayload.GetProperty("type").GetString());
            Assert.Contains(
                describePayload.GetProperty("fields").EnumerateArray(),
                f => f.GetProperty("name").GetString() == "method" && f.GetProperty("required").GetBoolean());

            var search = await harness.Client.CallToolAsync(
                "search_docs",
                new Dictionary<string, object?> { ["query"] = "verifyMode" },
                cancellationToken: cts.Token);
            Assert.False(search.IsError ?? false);
            var searchPayload = search.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.NotEmpty(searchPayload.GetProperty("matches").EnumerateArray());

            var run = await harness.Client.CallToolAsync(
                "run_suite",
                new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
                progress,
                cancellationToken: cts.Token);
            Assert.False(run.IsError ?? false);
            var runPayload = run.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("Pass", runPayload.GetProperty("verdict").GetString());
            Assert.NotEmpty(runPayload.GetProperty("steps").EnumerateArray());

            var explain = await harness.Client.CallToolAsync("explain_run", cancellationToken: cts.Token);
            Assert.False(explain.IsError ?? false);
            var explainPayload = explain.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            Assert.Equal("Pass", explainPayload.GetProperty("verdict").GetString());
            Assert.False(string.IsNullOrWhiteSpace(explainPayload.GetProperty("summary").GetString()));

            foreach (var result in new[] { validate, listTypes, describe, search, run, explain })
            {
                AssertNoSentinel(result, sentinel);
            }

            // Progress delivery is a separate wire channel from the tool result itself (confirmed
            // during REQ-006's own design — see RealRunSuiteMcpTests) — waited for so the sweep below
            // is over content that genuinely arrived (proving relayed progress is itself substantive),
            // not just whatever happened to have landed by the time the tool calls above returned.
            await WaitUntilAsync(
                () => progressUpdates.Any(u => (u.Message ?? string.Empty).Contains(startingLine, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            foreach (var update in progressUpdates)
            {
                Assert.DoesNotContain(sentinel, update.Message ?? string.Empty, StringComparison.Ordinal);
            }
        }
        finally
        {
            ClearSentinel();
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── B2: run_suite — child-sourced content is relayed; the server's own environment is not ──

    [Fact]
    public async Task RunSuite_ChildSourcedContentIsRelayedUnredacted_ButServerEnvironmentSentinelNeverAppears()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var envSentinel = UniqueSentinel();

        // Deliberately NOT derived from the environment at all: stands in for content the CHILD
        // process's own stdout or events file happened to carry (an already-redacted marker, a raw
        // observation, or — in a badly-behaved engine build — a genuine leak). REQ-010's contract is
        // explicit: the ENGINE is the redaction authority, and this server relays whatever the child
        // produced (bounded, control-character sanitised) rather than attempting a second redaction
        // pass of its own. Proving that relay is faithful is exactly as important here as proving
        // the server's OWN environment never leaks — conflating "mcp adds a new leak" with "the
        // child's own content, as designed, passed through" would be the wrong lesson to encode.
        const string childOutputMarker = "child-stdout-marker-9f3ac2";
        var events = $$"""
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50,"observation":"{{childOutputMarker}}"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var runner = FakeSuiteRunner.Succeeding([$"child said: {childOutputMarker}"], events, exitCode: 0);

        // Set BEFORE the harness is even constructed — see B1's identical rationale (a review fix):
        // closes the startup-snapshot window a regression could otherwise exploit.
        SetSentinel(envSentinel);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

            var progressUpdates = new ConcurrentBag<ProgressNotificationValue>();
            var progress = new Progress<ProgressNotificationValue>(progressUpdates.Add);

            var result = await harness.Client.CallToolAsync(
                "run_suite",
                new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
                progress,
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);

            // The child's own content is relayed through, unredacted by this server — the contract
            // is "pass through the engine's already-redacted fields", not "attempt a second
            // redaction pass" (that would be REQ-010 scope creep this server must not attempt; see
            // this class's own remarks and the CONTEXT the todo was scoped under).
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            var steps = payload.GetProperty("steps").EnumerateArray().ToArray();
            var step = Assert.Single(steps);
            Assert.Contains(childOutputMarker, step.GetProperty("observation").GetString(), StringComparison.Ordinal);

            await WaitUntilAsync(() => !progressUpdates.IsEmpty, TimeSpan.FromSeconds(5));
            Assert.Contains(progressUpdates, u => (u.Message ?? string.Empty).Contains(childOutputMarker, StringComparison.Ordinal));

            // The defining assertion: THIS SERVER's own environment never leaks into the response or
            // its progress, even though a run genuinely happened and genuinely relayed real,
            // child-sourced content — proving the leak vector under test is the child's own output
            // (the engine's responsibility), never this server's environment (this server's own).
            AssertNoSentinel(result, envSentinel);
            foreach (var update in progressUpdates)
            {
                Assert.DoesNotContain(envSentinel, update.Message ?? string.Empty, StringComparison.Ordinal);
            }
        }
        finally
        {
            ClearSentinel();
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── B3: explain_run — an already-redacted events file is relayed as-is, never re-resolved ──

    [Fact]
    public async Task ExplainRun_EventsFileFieldsAreAlreadyRedacted_RelaysTheRedactedFormAndNeverReadsProcessEnvironment()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var envSentinel = UniqueSentinel();

        // Shaped exactly like a genuine engine redaction marker (§17: Vars.Secrets.Resolve returns a
        // SecretString with no value-returning ToString()/IFormattable) — NOT a raw secret.
        // explain_run's whole job is to relay whatever the events file already carries; it must
        // never attempt to resolve, unmask, or otherwise reinterpret this, only pass it through.
        const string redactedMarker = "***REDACTED***";
        var eventsPath = Path.Combine(Path.GetTempPath(), $"secret-hygiene-explain-run-{Guid.NewGuid():N}.jsonl");
        var eventsContent = $$"""
            {"type":"step-completed","stepId":"send-webhook","verdict":"FAIL","durationMs":80,"observation":"Authorization: {{redactedMarker}}"}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            """;
        await File.WriteAllTextAsync(eventsPath, eventsContent, cts.Token);

        SetSentinel(envSentinel);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token);

            var result = await harness.Client.CallToolAsync(
                "explain_run",
                new Dictionary<string, object?> { ["eventsPath"] = eventsPath },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            var notableSteps = payload.GetProperty("notableSteps").EnumerateArray().ToArray();
            var step = Assert.Single(notableSteps);
            Assert.Contains(redactedMarker, step.GetProperty("observation").GetString(), StringComparison.Ordinal);

            // explain_run is PURE read + parse + diagnose (see ExplainRunOrchestrator's remarks): it
            // never spawns a process and never touches the process environment at all, so the
            // sentinel has no path into its response regardless of what the events file itself
            // contains.
            AssertNoSentinel(result, envSentinel);
        }
        finally
        {
            ClearSentinel();
            File.Delete(eventsPath);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string UniqueSentinel() => $"VOUCHFX-MCP-SECRET-HYGIENE-SENTINEL-{Guid.NewGuid():N}";

    /// <summary>
    /// Sets the sentinel for the CURRENT test's own scope. Every caller pairs this with
    /// <see cref="ClearSentinel"/> in a <c>finally</c> block — <see cref="Environment.SetEnvironmentVariable(string, string?)"/>
    /// mutates process-wide state, so leaving it set would risk bleeding into another test.
    /// </summary>
    private static void SetSentinel(string sentinel) => Environment.SetEnvironmentVariable(SentinelVariableName, sentinel);

    private static void ClearSentinel() => Environment.SetEnvironmentVariable(SentinelVariableName, null);

    private static void AssertNoSentinel(CallToolResult result, string sentinel)
    {
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                Assert.DoesNotContain(sentinel, text.Text, StringComparison.Ordinal);
            }
        }

        if (result.StructuredContent is { } structured)
        {
            Assert.DoesNotContain(sentinel, structured.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }
}
