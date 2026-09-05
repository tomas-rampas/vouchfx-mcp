using System.Globalization;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-06's empirical questions, ANSWERED BY MEASUREMENT against the real pinned engine: what does a
/// <c>step-attempt</c> event actually carry, and does an IMMEDIATE step produce one at all?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists: a claim was inferred and then repeated as fact.</b> US-S3-06 shipped saying
/// spec §5.10's <c>Attempt.at</c> is "<see langword="null"/> at the currently pinned engine, always" —
/// across five comment and documentation sites — on the strength of the story's own SYNTHETIC fixtures,
/// none of which emitted a timestamp. Nothing had ever looked at a real events file. This class looks,
/// and the story's remarks, field documentation and tool description now follow what it found rather
/// than the other way round.
/// </para>
/// <para>
/// <b>WHAT IT FOUND — the verbatim lines, recorded here as the primary record.</b> A real RETRY suite
/// (one <c>cache-assert.redis</c> poll loop against an absent key, plus one IMMEDIATE step) run against
/// <c>vouchfx 1.0.0-rc.4+be12ebd1</c> on 2026-09-05 produced a 15-line events file containing the
/// lines below. NOTE (recorded so this file cannot itself drift the way it exists to prevent): the
/// verbatim record was captured from a 10-second variant of this probe (<c>"timeoutMs":10000</c>, 8
/// attempt lines); the COMMITTED suite polls for 3&#160;s (<c>timeout: 3s</c>, asserted as 3,000&#160;ms
/// below) and yields fewer attempts — every finding is window-length-independent.
/// <code>
/// {"v":1,"schemaVersion":"v1","type":"step-started","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f9…","stepId":"immediate-probe","kind":"cache-assert.redis","verifyMode":"IMMEDIATE"}
/// {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f9…","stepId":"immediate-probe","verdict":"PASS","durationMs":8,"observation":{"matched":true,"op":"exists"}}
/// {"v":1,"schemaVersion":"v1","type":"step-started","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f9…","stepId":"retry-probe","kind":"cache-assert.redis","verifyMode":"RETRY","timeoutMs":10000}
/// {"v":1,"schemaVersion":"v1","type":"step-attempt","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f9…","stepId":"retry-probe","attempt":1,"tMs":6,"outcome":"FAIL","observation":{"exists":{"expected":true,"actual":false}}}
/// </code>
/// Four findings, each of which corrected something this repository had written down:
/// <list type="number">
/// <item><description>
/// <b><c>step-attempt</c> DOES carry <c>ts</c></b> — a 33-character ISO-8601 instant with offset, on
/// every line. So <c>StepTimelineAttempt.At</c> is POPULATED in production; the "always null" claim was
/// false, and the relay path (<c>SuiteEventParser.HandleStepAttempt</c>) had been correct all along.
/// </description></item>
/// <item><description>
/// <b><c>ts</c> is a report-render stamp, not a per-attempt instant.</b> All 8 attempt lines of a
/// ten-second polling window share one identical value, and the whole 15-event file holds only 3
/// distinct ones — the engine buffers its stream and stamps as it writes. It is relayed verbatim, and
/// the tool description now tells hosts not to difference two of them.
/// </description></item>
/// <item><description>
/// <b>An IMMEDIATE step emits NO <c>step-attempt</c> event at all</b> — <c>step-started</c> then
/// <c>step-completed</c>, nothing between. This answers the spec reviewer's open question: a null
/// <c>verifyMode</c> with an empty <c>attempts</c> list is the COMMON real-world shape for a
/// non-retrying step, and <c>ONCE</c> is reported for the narrower population of steps that really did
/// record exactly one attempt event.
/// </description></item>
/// <item><description>
/// <b><c>step-started</c> carries <c>timeoutMs</c> and the DECLARED <c>verifyMode</c>.</b> Spec §5.10's
/// <c>timeoutMs</c> therefore has a source in the v1 stream after all — on an event type
/// <see cref="SuiteEventParser"/> does not handle, so this build still reports it as null, but as a
/// statement about this build rather than about the contract. Recorded for the follow-up that widens
/// the shared parser; deliberately not taken here, since that parse feeds three other tools.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Also settled in passing: <c>tMs</c> is PER-ATTEMPT duration, not cumulative elapsed.</b> The eight
/// attempts reported 6, 5, 6, 19, 18, 6, 6, 6 ms inside a ten-second window — non-monotonic, and summing
/// to a fraction of it. <c>StepTimelineAttempt.DelayMs</c>'s refusal to subtract consecutive <c>tMs</c>
/// values was previously "the documentation does not settle which reading applies"; it is now "the
/// subtraction is measurably not a backoff".
/// </para>
/// <para>
/// <b>Gating: two preconditions, both silent passes when unmet</b> — the established pattern (see
/// <see cref="RealPlanCoverageAgainstPinnedCliTests"/>, and CLAUDE.md's instruction not to invent a
/// second skip mechanism). First the production <see cref="CliPinVerifier"/> against the real PATH, so
/// a machine without the pinned engine — every CI runner, deliberately — passes trivially. Second, and
/// unique to this class among the <c>*AgainstPinnedCliTests</c>, a CONTAINER RUNTIME: this is the only
/// test in the suite that runs a real suite end to end, and a suite needs Docker. A run that produces no
/// attempt events is therefore reported as an environment precondition rather than asserted against —
/// the same distinction the server's own verdict taxonomy draws between an EnvironmentError and a Fail,
/// applied to the test itself.
/// </para>
/// </remarks>
public class RealStepAttemptEnvelopeAgainstPinnedCliTests : IDisposable
{
    /// <summary>
    /// The probe suite. A redis dependency (the smallest the engine offers) with one IMMEDIATE step and
    /// one RETRY step whose key never appears, so the poll loop runs to its timeout and emits several
    /// attempts. <c>imagePullPolicy: Missing</c> so a host that already has the image pays nothing.
    /// </summary>
    private const string ProbeSuite = """
        metadata:
          name: "step-attempt envelope probe"
          owner: "vouchfx-mcp"
          tags:
            - probe

        environment:
          imagePullPolicy: Missing
          dependencies:
            cache:
              type: redis

        steps:
          - id: immediate-probe
            type: cache-assert.redis
            description: "A single-attempt IMMEDIATE step (the default verifyMode)."
            target: cache
            operation: exists
            key: never-written
            expect:
              exists: false

          - id: retry-probe
            type: cache-assert.redis
            description: "A RETRY poll loop that never matches, so it runs to its timeout."
            target: cache
            verifyMode: RETRY
            timeout: 3s
            operation: exists
            key: never-written
            expect:
              exists: true
        """;

    /// <summary>
    /// Generous, because the budget covers a container topology's whole lifecycle (pull-if-missing,
    /// start, health, the 3-second poll window, teardown) — measured at ~25 s on a warm host — not just
    /// the polling this test cares about.
    /// </summary>
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(6);

    private readonly ITestOutputHelper _testOutput;
    private readonly string _workingDirectory;

    public RealStepAttemptEnvelopeAgainstPinnedCliTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput ?? throw new ArgumentNullException(nameof(testOutput));
        _workingDirectory = Path.Combine(
            Path.GetTempPath(), $"vouchfx-mcp-attempt-envelope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public async Task StepAttemptEvents_FromARealPinnedEngineRun_CarryTheEnvelopeThisServersDocumentationClaims()
    {
        using var cts = new CancellationTokenSource(RunBudget);

        var events = await RunProbeSuiteAsync(cts.Token);
        if (events is null)
        {
            return; // Skipped — RunProbeSuiteAsync said why.
        }

        var attempts = EventsOfType(events, "step-attempt");
        Assert.NotEmpty(attempts);

        // ── Finding 1: `ts` is present, and it is a STRING ───────────────────────────────────────
        //
        // The whole reason the "always null" claim was wrong. Asserted on every attempt line, not on
        // one: a field present only sometimes would be a different and more awkward fact.
        foreach (var attempt in attempts)
        {
            Assert.True(
                attempt.TryGetProperty("ts", out var ts),
                "No 'ts' on a step-attempt event. If the pinned engine has STOPPED emitting it, "
                + "StepTimelineAttempt.At's documentation (which now says it is populated) and "
                + "get_step_timeline's tool description both need re-measuring against this run.");
            Assert.Equal(JsonValueKind.String, ts.ValueKind);
            Assert.True(
                DateTimeOffset.TryParse(ts.GetString(), out _),
                $"'ts' was not a parseable instant: '{ts.GetString()}'.");

            // The cap MaxAtChars (40) is sized against this; 33 was measured.
            Assert.True(ts.GetString()!.Length <= 40, $"'ts' was {ts.GetString()!.Length} characters.");
        }

        // ── Finding 2: `ts` does NOT vary per attempt — it is a report-render stamp ───────────────
        //
        // Recorded rather than asserted as an equality: this is a property of how the engine writes
        // its buffered stream, and a future engine that stamped per event would be an IMPROVEMENT
        // that must not fail this test. What matters to the contract is that tMs is what orders the
        // timeline, which the next assertion covers.
        var distinctTimestamps = attempts
            .Select(a => a.GetProperty("ts").GetString())
            .Distinct(StringComparer.Ordinal)
            .Count();
        _testOutput.WriteLine(
            $"MEASURED: {attempts.Count} step-attempt events carried {distinctTimestamps} distinct 'ts' "
            + $"value(s); {EventsOfType(events, null).Count} events in the file carried "
            + $"{events.Select(e => e.TryGetProperty("ts", out var t) ? t.GetString() : null).Distinct(StringComparer.Ordinal).Count()} distinct.");

        // ── The two fields that really are absent ────────────────────────────────────────────────
        foreach (var attempt in attempts)
        {
            Assert.False(
                attempt.TryGetProperty("at", out _),
                "The engine emitted spec §5.10's own 'at' spelling. The parser prefers 'ts' and falls "
                + "back to 'at', so nothing breaks — but the documentation naming 'ts' as the spelling "
                + "the engine uses needs updating.");
            Assert.False(
                attempt.TryGetProperty("delayMs", out _),
                "The engine emitted a per-attempt 'delayMs'. StepTimelineAttempt.DelayMs is documented "
                + "as structurally unsourceable and reported as an explicit null; it can now be relayed.");
        }

        // ── Finding 4: timeoutMs and the DECLARED verifyMode live on step-started ────────────────
        //
        // Pinned because GetStepTimelineResult.TimeoutMs's documentation now cites it: the null it
        // reports is a fact about THIS BUILD's parser, not about the v1 contract, and that distinction
        // is only honest while the field really is on the wire.
        var started = EventsOfType(events, "step-started");
        var retryStarted = Assert.Single(started, e => e.GetProperty("stepId").GetString() == "retry-probe");
        Assert.Equal("RETRY", retryStarted.GetProperty("verifyMode").GetString());
        Assert.Equal(3_000, retryStarted.GetProperty("timeoutMs").GetInt32());

        // ── tMs is per-attempt duration, not cumulative elapsed ─────────────────────────────────
        //
        // The measurement that upgraded DelayMs's rationale from "unverified" to "measurably wrong".
        // A cumulative figure would be non-decreasing across a step's attempts and would approach the
        // polling window; these do neither.
        var tMsValues = attempts.Select(a => a.GetProperty("tMs").GetInt64()).ToArray();
        _testOutput.WriteLine($"MEASURED: tMs across the RETRY step's attempts = [{string.Join(", ", tMsValues)}].");
        Assert.True(
            tMsValues.Sum() < 3_000,
            $"The attempts' tMs values sum to {tMsValues.Sum()} ms, at or beyond the 3,000 ms polling "
            + "window — consistent with a CUMULATIVE reading, which is the opposite of what "
            + "StepTimelineAttempt.DelayMs's remarks now record. Re-measure them.");
    }

    /// <summary>
    /// The IMMEDIATE half, and the spec reviewer's open question: does a single-attempt IMMEDIATE step
    /// emit a <c>step-attempt</c> event? <b>Measured: no.</b> So <c>verifyMode: null</c> with an empty
    /// timeline is the ordinary shape for such a step, which is what the tool description now says.
    /// </summary>
    [Fact]
    public async Task AnImmediateStep_EmitsNoAttemptEvent_SoItsTimelineIsEmptyAndItsVerifyModeNull()
    {
        using var cts = new CancellationTokenSource(RunBudget);

        var events = await RunProbeSuiteAsync(cts.Token);
        if (events is null)
        {
            return; // Skipped — RunProbeSuiteAsync said why.
        }

        // It ran, and it completed — so an absence of attempt events below is a real property of how
        // the engine reports an IMMEDIATE step, not a step that never happened.
        var completed = EventsOfType(events, "step-completed");
        var immediateCompleted =
            Assert.Single(completed, e => e.GetProperty("stepId").GetString() == "immediate-probe");
        Assert.Equal("PASS", immediateCompleted.GetProperty("verdict").GetString());

        Assert.DoesNotContain(
            EventsOfType(events, "step-attempt"),
            e => e.GetProperty("stepId").GetString() == "immediate-probe");

        // And what THIS SERVER makes of that file: an empty timeline and a null verifyMode, delivered
        // as a SUCCESS. Driven through the real parser and orchestrator over the real events file, so
        // the documented "ordinary shape" is the shape a host actually receives.
        var eventsPath = Path.Combine(_workingDirectory, "events.jsonl");
        var registry = StubRunRegistry.WithCompletedRun(eventsPath);

        // The registry's OWN recorded suite path, not the probe file's name: the orchestrator matches
        // specPath against what the run recorded (VFX-E-1509 otherwise), and this stub records its own
        // default rather than the file this test happened to write.
        var entry = registry.ListRuns()[0];
        var specPath = entry.SpecPaths[0];

        var outcome = await new GetStepTimelineOrchestrator(registry).GetAsync(
            new GetStepTimelineRequest(entry.RunId, specPath, "immediate-probe"),
            CancellationToken.None);

        var result = Assert.IsType<GetStepTimelineOutcome.Found>(outcome).Result;
        Assert.Empty(result.Attempts);
        Assert.Null(result.VerifyMode);
        Assert.False(result.Truncated);

        // The RETRY step through the same file, for contrast: a real timeline, with `at` populated —
        // the end-to-end proof of finding 1, through the production projection rather than the parser
        // alone.
        var retryOutcome = await new GetStepTimelineOrchestrator(registry).GetAsync(
            new GetStepTimelineRequest(entry.RunId, specPath, "retry-probe"),
            CancellationToken.None);

        var retry = Assert.IsType<GetStepTimelineOutcome.Found>(retryOutcome).Result;
        Assert.Equal(StepVerifyMode.Retry, retry.VerifyMode);
        Assert.All(retry.Attempts, attempt => Assert.False(
            string.IsNullOrWhiteSpace(attempt.At),
            "get_step_timeline reported a null 'at' from an events file that carries 'ts' on every "
            + "line. The relay in SuiteEventParser.HandleStepAttempt has regressed."));

        // Still null, and still for the reasons their own documentation now gives.
        Assert.Null(retry.TimeoutMs);
        Assert.All(retry.Attempts, attempt => Assert.Null(attempt.DelayMs));
    }

    // ── Running the probe, or explaining why it could not ───────────────────────────────────────

    /// <summary>
    /// Runs the probe suite through the PRODUCTION <see cref="VouchfxCliSuiteRunner"/> and returns the
    /// parsed event objects, or <see langword="null"/> when an environment precondition was unmet — in
    /// which case the reason is written to the test output and the caller returns a silent pass.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>?> RunProbeSuiteAsync(CancellationToken cancellationToken)
    {
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());

        // Precondition 1 — the PRODUCTION gate, reused rather than reinvented: no installed CLI
        // matching ENGINE_PIN means this test has nothing real to measure against.
        var pinCheck = await new CliPinVerifier(new VouchfxCliProcessRunner(), pin).VerifyAsync(cancellationToken);
        if (pinCheck is not CliPinResult.Ok)
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): no installed vouchfx CLI matches ENGINE_PIN ({pin.Version}). "
                + $"Gate outcome: {pinCheck.GetType().Name}.");
            return null;
        }

        var suitePath = Path.Combine(_workingDirectory, "probe.e2e.yaml");
        var eventsPath = Path.Combine(_workingDirectory, "events.jsonl");
        await File.WriteAllTextAsync(suitePath, ProbeSuite, cancellationToken);

        var outputLines = new List<string>();
        var run = await new VouchfxCliSuiteRunner().RunAsync(
            new SuiteRunSpec(suitePath, [], eventsPath), outputLines.Add, cancellationToken);

        if (run.Termination != RunTermination.CompletedNormally || !File.Exists(eventsPath))
        {
            _testOutput.WriteLine(
                $"SKIPPED (not a failure): the probe run did not complete normally "
                + $"({run.Termination}, exit {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}). A container "
                + "runtime is required to run a real suite; this is the only test in the suite that "
                + $"needs one. Last output: {string.Join(" | ", outputLines.TakeLast(3))}");
            return null;
        }

        var events = (await File.ReadAllLinesAsync(eventsPath, cancellationToken))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        // Precondition 2 — a container runtime. An environment error, or a run that reached no
        // attempt at all, means the engine never got as far as the behaviour under measurement. That
        // is exactly the EnvironmentError-vs-Fail distinction the server itself refuses to blur,
        // applied to this test: it is not evidence that the envelope changed.
        if (events.Any(e => e.GetProperty("type").GetString() == "environment-error")
            || !events.Any(e => e.GetProperty("type").GetString() == "step-attempt"))
        {
            _testOutput.WriteLine(
                "SKIPPED (not a failure): the run produced no step-attempt events (an environment "
                + "error, or a topology that never started). A container runtime is required.");
            return null;
        }

        return events;
    }

    /// <summary>Every event of one <c>type</c>, or every event when <paramref name="type"/> is null.</summary>
    private static List<JsonElement> EventsOfType(IReadOnlyList<JsonElement> events, string? type) =>
        events.Where(e => type is null || e.GetProperty("type").GetString() == type).ToList();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory this test could not remove is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
