using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers todo 9 (REQ-010) end to end, through the same in-memory MCP harness the other
/// <c>Real*McpTests</c> classes use: vouchfx-mcp is a RELAY, never a redaction authority and never
/// a secret resolver. The engine (§17) is the sole redaction authority — the <c>--events</c> JSON
/// Lines fields this server parses are already redacted at their source, and every field this
/// server relays is bounded and control-character-sanitised for display, never re-redacted or
/// resolved. What REQ-010 additionally demands, and what these tests lock as a regression guard, is
/// that this server never independently surfaces ITS OWN process environment through any tool
/// result, progress notification, or RESOURCE (REQ-010's own wording names all three) — across the
/// whole tool AND resource surface.
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
/// <para>
/// <b>Progress-notification timing (a two-round CI-only diagnosis):</b> a <c>notifications/progress</c>
/// message is delivered to a test's <see cref="IProgress{T}"/> sink via a SEPARATE, asynchronous
/// dispatch path from the tool call's own response — confirmed directly against the MCP C# SDK's own
/// session-handler implementation, whose message loop dispatches every incoming message (both a
/// notification and the eventual response) as an independent, unawaited ("fire-and-forget") task
/// specifically so the read loop is never blocked by a slow handler, with its own code comments
/// acknowledging the resulting handlers can complete "out of order". There is therefore NO guarantee
/// that a progress notification's handler has finished running — let alone that
/// <see cref="Progress{T}"/> has finished marshalling it to a collection's own <c>Add</c> call, a THIRD
/// layer of asynchrony on top of the SDK's own — by the time <c>CallToolAsync</c> returns, and no
/// guarantee of DELIVERY ORDER between two notifications either.
/// </para>
/// <para>
/// <b>Consequently, exactly ONE test owns the progress channel's own timing-dependent proof:</b>
/// <c>RunSuite_ChildSourcedContentIsRelayedUnredacted_...</c> (B2) waits for its OWN specific
/// condition (never merely "something arrived") with a GENEROUS bound
/// (<see cref="ProgressWaitTimeout"/> — generous because there is no CPU-bound work on the delivery
/// path, a same-process in-memory pipe, so genuine delivery is expected in low single-digit
/// milliseconds even under CI contention; the bound exists to survive a slow/loaded runner, not
/// because delivery is expected to take anywhere near it). An EARLIER version of
/// <c>AcrossTheWholeToolSurface_...</c> (B1) duplicated a similar wait for its OWN
/// <c>run_suite</c> call's progress — and was found, on a second round of Linux CI failures, to
/// genuinely time out even at a 30-second bound: duplicating a progress-timing wait in TWO tests only
/// doubled the flake surface without adding coverage B2 did not already provide. B1 therefore contains
/// NO timing-dependent wait for anything at all: every assertion it makes is against a value that is
/// already fully materialised the instant its owning <c>await</c> completes (a tool result, a
/// resource listing, a resource read) — see B1's own remarks at its declaration for the deterministic
/// surface it covers instead, and B2's own remarks for exactly what of the progress channel it proves
/// so B1 does not have to.
/// </para>
/// <para>
/// <b>Third round — B2 itself still flaked on Linux CI, even at the 30-second bound above, and the
/// ACTUAL root cause turned out to be client-side, not merely "slow delivery":</b> decompiling
/// ModelContextProtocol.Core 1.4.1 (the exact pinned version) shows <c>McpClient</c>'s own
/// <c>CallToolAsync(string, IReadOnlyDictionary&lt;string, object?&gt;?, IProgress&lt;ProgressNotificationValue&gt;?, RequestOptions?, CancellationToken)</c>
/// convenience overload registers a TEMPORARY "notifications/progress" handler, then unregisters it
/// in a <c>finally</c> block the INSTANT its own <c>tools/call</c> response arrives. Combined with the
/// fire-and-forget dispatch described above, that unregistration can race — and win — against the
/// dispatch of a progress notification the server had already sent before its response: if the
/// response's own dispatch task resolves the awaited call first, the handler is disposed before the
/// notification's dispatch task ever runs, and <c>NotificationHandlers.InvokeHandlers</c> silently
/// finds nothing registered and invokes nothing. The notification is not late in that case — it is
/// PERMANENTLY dropped, which is exactly why raising B2's bound to 30 seconds never helped: there was
/// nothing left to wait for. B1 and B2 both now use <see cref="ProgressCapture.CallAsync"/> instead of
/// the SDK's convenience overload — it registers its own handler directly and keeps it alive
/// independently of the call's own request/response lifecycle, closing the race entirely rather than
/// giving it a longer window. See <see cref="ProgressCapture"/>'s own remarks for the full mechanism.
/// </para>
/// </remarks>
public class RealSecretHygieneMcpTests
{
    private const string SentinelVariableName = "VOUCHFX_MCP_TEST_SENTINEL";

    /// <summary>
    /// The bound every progress-notification wait below uses (a CI fix — see this class's own
    /// remarks on why delivery is asynchronous relative to the tool call's own return). Generous
    /// because there is no genuine CPU-bound work on the delivery path (an in-memory pipe, not a
    /// network hop) — real delivery is expected in low single-digit milliseconds even under a loaded
    /// CI runner, so this bound is headroom for contention, never an expected duration.
    /// </summary>
    private static readonly TimeSpan ProgressWaitTimeout = TimeSpan.FromSeconds(30);

    // ── B1: the whole tool surface, one sentinel, one sweep ─────────────────────────────────────

    /// <remarks>
    /// <b>Deterministic by construction — NO timing-dependent wait anywhere in this test</b> (a CI
    /// fix; see this class's own remarks for the full two-round diagnosis). Every assertion here is
    /// against a value that is already fully materialised the instant its owning <c>await</c>
    /// completes: the six tools' own structured results, the resources listing, and each resource's
    /// read content. This is deliberately B1's ENTIRE non-vacuousness proof — <c>run_suite</c>'s
    /// PROGRESS channel specifically (as opposed to its final result, asserted here like every other
    /// tool's) is the dedicated, separately-owned job of
    /// <c>RunSuite_ChildSourcedContentIsRelayedUnredacted_...</c> (B2), which waits correctly (an
    /// exact condition, a generous bound) for exactly that. B1 still exercises <c>run_suite</c> WITH a
    /// <see cref="Progress{T}"/> sink wired up (proving the plumbing itself does not error), and still
    /// sweeps whatever progress happened to have arrived by the time the call returned — but strictly
    /// BEST-EFFORT: zero is an accepted outcome, nothing here ever waits or requires a specific
    /// message to be present, so this test cannot time out on progress delivery, full stop.
    /// </remarks>
    [Fact]
    public async Task AcrossTheWholeToolSurface_ServerEnvironmentSentinel_NeverAppearsInAnyResponseOrNotification()
    {
        using var consoleOut = new ConsoleOutCapture();
        // An ordinary bound (no timing-dependent wait exists in this test to budget extra headroom
        // for) — matches the other tools-only Real*McpTests classes' own convention for a test that
        // drives several ordinary tool calls plus a handful of resource calls.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sentinel = UniqueSentinel();

        const string events = """
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var runner = FakeSuiteRunner.Succeeding(["Starting DCP..."], events, exitCode: 0);

        // Set BEFORE the harness is even constructed (a review fix): a regression that somehow
        // snapshotted the environment at SERVER CONSTRUCTION time, rather than reading it live at
        // response-building time, would otherwise pass this test falsely by missing a sentinel set
        // only after StartAsync had already returned.
        SetSentinel(sentinel);
        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token, suiteRunner: runner);

            // ConcurrentBag, not List: the SDK dispatches each incoming notification as an
            // independent, unawaited task (see this class's own remarks), so the handler
            // ProgressCapture registers can genuinely be invoked from multiple threads concurrently
            // — a plain List<T> is not thread-safe under concurrent Add and would be a latent
            // corruption/exception risk on a true-parallel Linux runner. ConcurrentBag's own
            // enumerator (used by every foreach/LINQ call over progressUpdates below) is safe to use
            // concurrently with further adds — it never throws on concurrent mutation — so no
            // separate snapshot/lock is needed either.
            var progressUpdates = new ConcurrentBag<ProgressNotificationValue>();

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

            // ProgressCapture.CallAsync, not harness.Client.CallToolAsync(..., IProgress<...>, ...)
            // — see this class's own remarks and ProgressCapture's own remarks: the SDK convenience
            // overload's progress handler is unregistered the instant its own response arrives, which
            // races (and can permanently lose to) the message loop's independent dispatch of an
            // already-received progress notification. B1 does not WAIT on progress (see below), so
            // that race was never this test's own flake source, but there is no reason to keep the
            // one racy call site in this file now that ProgressCapture exists.
            var (run, runProgressRegistration) = await ProgressCapture.CallAsync(
                harness.Client,
                "run_suite",
                new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
                progressUpdates,
                cts.Token);
            await using var _ = runProgressRegistration;
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

            // REQ-010's own wording names RESOURCES alongside tool results and progress
            // notifications ("No tool result, progress notification, or resource...") — a review
            // fix: the sweep above only covered tools/progress. Both vendored-document resources are
            // swept here too, over BOTH resources/list metadata and resources/read content, each with
            // a substantive-content check first so the sentinel's absence is meaningful rather than
            // trivially true over an empty/missing listing or read.
            var resourceList = await harness.Client.ListResourcesAsync(cancellationToken: cts.Token);
            Assert.Equal(2, resourceList.Count);
            foreach (var resource in resourceList)
            {
                Assert.False(string.IsNullOrWhiteSpace(resource.Uri));
                Assert.DoesNotContain(sentinel, resource.Uri ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, resource.Name ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, resource.Description ?? string.Empty, StringComparison.Ordinal);
            }

            foreach (var uri in new[] { VendoredDocuments.LanguageReference.ResourceUri, VendoredDocuments.Recipes.ResourceUri })
            {
                var resourceRead = await harness.Client.ReadResourceAsync(uri, cancellationToken: cts.Token);
                var content = Assert.Single(resourceRead.Contents);
                var textContent = Assert.IsType<TextResourceContents>(content);
                Assert.False(string.IsNullOrWhiteSpace(textContent.Text));
                Assert.DoesNotContain(sentinel, textContent.Text, StringComparison.Ordinal);
            }

            // BEST-EFFORT ONLY — no wait, no required condition, cannot time out (a CI fix; see this
            // class's own remarks and this method's own <remarks> for the full rationale). The
            // progress CHANNEL's non-vacuousness (proving relayed content genuinely arrives) and its
            // sentinel-absence are B2's dedicated, already-covered job — duplicating a timing-dependent
            // wait for the SAME thing here only doubled the flake surface without adding coverage.
            // Whatever landed in progressUpdates by the time the tool calls above returned — including
            // legitimately NOTHING, if dispatch simply had not run yet — is swept for the sentinel; an
            // empty collection makes this loop a no-op, which is fine: it is not this test's job to
            // prove the collection is non-empty.
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
        // Generous enough to comfortably absorb a full ProgressWaitTimeout wait for the progress
        // assertion below, on top of the run_suite call's own ordinary latency — THIS test owns the
        // progress channel's timing-dependent proof (see this class's own remarks), so its overall
        // budget genuinely needs to accommodate ProgressWaitTimeout, unlike B1's.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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

            // ConcurrentBag, not List — see B1's identical rationale on why a plain List<T> would be a
            // latent thread-safety bug here (concurrent, SDK-dispatched Add calls).
            //
            // ProgressCapture.CallAsync, not harness.Client.CallToolAsync(..., IProgress<...>, ...)
            // (a CI fix — see this class's own remarks and ProgressCapture's own remarks for the full,
            // now-CONFIRMED mechanism): the SDK convenience overload unregisters its progress handler
            // the instant its own response arrives, racing the message loop's independent dispatch of
            // an already-received-but-not-yet-processed progress notification. That race can
            // PERMANENTLY drop a notification (not merely delay it) if the response's own dispatch
            // task wins — which is exactly why the two-round diagnosis below still timed out even at
            // a 30-second bound: the notification was gone, not late. ProgressCapture keeps its own
            // registration alive independently of this call's request/response lifecycle, so the
            // WaitUntilAsync below waits for something that can actually still arrive.
            var progressUpdates = new ConcurrentBag<ProgressNotificationValue>();

            var (result, progressRegistration) = await ProgressCapture.CallAsync(
                harness.Client,
                "run_suite",
                new Dictionary<string, object?> { ["path"] = FixturePath("good-suite.e2e.yaml") },
                progressUpdates,
                cts.Token);
            await using var _ = progressRegistration;

            Assert.False(result.IsError ?? false);

            // The child's own content is relayed through, unredacted by this server — the contract
            // is "pass through the engine's already-redacted fields", not "attempt a second
            // redaction pass" (that would be REQ-010 scope creep this server must not attempt; see
            // this class's own remarks and the CONTEXT the todo was scoped under).
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            var steps = payload.GetProperty("steps").EnumerateArray().ToArray();
            var step = Assert.Single(steps);
            Assert.Contains(childOutputMarker, step.GetProperty("observation").GetString(), StringComparison.Ordinal);

            // Waits on the SPECIFIC marker, not merely "something arrived" (a CI fix — the previous
            // "wait for non-empty, then assert the specific marker" shape was unsound: the SDK
            // dispatches queued messages as independent, unawaited tasks that can complete OUT OF
            // ORDER (see this class's own remarks), so "non-empty" could already be satisfied by some
            // OTHER progress message while this specific one was still in flight, failing the very
            // next line with no further wait at all).
            await WaitUntilAsync(
                () => progressUpdates.Any(u => (u.Message ?? string.Empty).Contains(childOutputMarker, StringComparison.Ordinal)),
                ProgressWaitTimeout,
                () => DescribeProgressForDiagnostics(progressUpdates));
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

    /// <summary>
    /// Renders whatever progress arrived by the time a <see cref="WaitUntilAsync"/> call gave up, for
    /// that timeout's own failure message — so a genuine future regression (a message that truly never
    /// arrives, as opposed to this test's own timing) is diagnosable directly from the test output
    /// rather than requiring a re-run with extra logging bolted on.
    /// </summary>
    private static string DescribeProgressForDiagnostics(IEnumerable<ProgressNotificationValue> progressUpdates)
    {
        var messages = progressUpdates.Select(u => u.Message ?? "(no message)").ToArray();
        return messages.Length == 0 ? "(no progress notifications arrived at all)" : string.Join(" | ", messages);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="describeStateForTimeoutMessage">
    /// Invoked ONLY on timeout, to enrich the failure message with whatever state is relevant (e.g.
    /// <see cref="DescribeProgressForDiagnostics"/>) — never on the success path, so it costs nothing
    /// when the condition is met promptly, which is the overwhelming common case.
    /// </param>
    private static async Task WaitUntilAsync(
        Func<bool> condition, TimeSpan timeout, Func<string>? describeStateForTimeoutMessage = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (condition())
        {
            return;
        }

        var state = describeStateForTimeoutMessage?.Invoke();
        var stateSuffix = string.IsNullOrEmpty(state) ? string.Empty : $" State at timeout: {state}";
        Assert.Fail($"Condition was not met within {timeout}.{stateSuffix}");
    }
}
