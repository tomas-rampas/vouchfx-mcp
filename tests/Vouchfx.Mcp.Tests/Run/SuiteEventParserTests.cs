using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>Covers <see cref="SuiteEventParser"/> directly — parsing leniency, RETRY attempt-count tracking, verdict elevation, and sanitisation.</summary>
public class SuiteEventParserTests
{
    [Fact]
    public void Parse_EmptyContent_ReturnsNullAggregateAndNoSteps()
    {
        var summary = SuiteEventParser.Parse(string.Empty);

        Assert.Null(summary.AggregateVerdict);
        Assert.Empty(summary.Steps);
        Assert.Empty(summary.EnvironmentErrors);
    }

    [Fact]
    public void Parse_WhitespaceOnlyContent_ReturnsNullAggregateAndNoSteps()
    {
        var summary = SuiteEventParser.Parse("   \n\n  \r\n");

        Assert.Null(summary.AggregateVerdict);
        Assert.Empty(summary.Steps);
    }

    [Fact]
    public void Parse_MalformedJsonLine_IsSkippedAndDoesNotAffectSubsequentLines()
    {
        const string content = """
            not valid json at all {{{
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(RunVerdict.Pass, summary.AggregateVerdict);
    }

    [Fact]
    public void Parse_UnrecognisedEventType_IsSkippedForwardCompatibly()
    {
        const string content = """
            {"type":"some-future-event-type","somethingNew":42}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(RunVerdict.Pass, summary.AggregateVerdict);
    }

    [Fact]
    public void Parse_UnrecognisedVerdictToken_DoesNotElevateOrThrow()
    {
        const string content = """{"type":"scenario-completed","scenarioId":"s1","verdict":"SOMETHING_NEW"}""";

        var summary = SuiteEventParser.Parse(content);

        Assert.Null(summary.AggregateVerdict);
    }

    [Theory]
    [InlineData("PASS", RunVerdict.Pass)]
    [InlineData("FAIL", RunVerdict.Fail)]
    [InlineData("ENV_ERROR", RunVerdict.EnvironmentError)]
    [InlineData("INCONCLUSIVE", RunVerdict.Inconclusive)]
    public void Parse_SingleScenarioCompleted_AggregateMatchesItsVerdict(string wireToken, RunVerdict expected)
    {
        var content = $$"""{"type":"scenario-completed","scenarioId":"s1","verdict":"{{wireToken}}"}""";

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(expected, summary.AggregateVerdict);
    }

    [Fact]
    public void Parse_MultipleScenarios_ElevatesToTheHighestPrecedenceVerdict()
    {
        // §12.1 precedence: EnvironmentError > Fail > Inconclusive > Pass. A Pass followed by a Fail
        // followed by an EnvironmentError must elevate all the way up, never get masked by order.
        const string content = """
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            {"type":"scenario-completed","scenarioId":"s2","verdict":"FAIL"}
            {"type":"scenario-completed","scenarioId":"s3","verdict":"ENV_ERROR"}
            """;

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(RunVerdict.EnvironmentError, summary.AggregateVerdict);
    }

    [Fact]
    public void Parse_FailNeverMaskedByALaterPassingScenario()
    {
        const string content = """
            {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
            {"type":"scenario-completed","scenarioId":"s2","verdict":"PASS"}
            """;

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(RunVerdict.Fail, summary.AggregateVerdict);
    }

    [Fact]
    public void Parse_StepAttempt_TracksTheHighestAttemptNumberSeen()
    {
        const string content = """
            {"type":"step-attempt","stepId":"poll-order","attempt":1}
            {"type":"step-attempt","stepId":"poll-order","attempt":2}
            {"type":"step-attempt","stepId":"poll-order","attempt":3}
            {"type":"step-completed","stepId":"poll-order","verdict":"PASS","durationMs":900}
            """;

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.Equal(3, step.AttemptCount);
    }

    [Fact]
    public void Parse_StepAttempt_OutOfOrderDuplicateDoesNotInflateTheCount()
    {
        const string content = """
            {"type":"step-attempt","stepId":"poll-order","attempt":3}
            {"type":"step-attempt","stepId":"poll-order","attempt":1}
            {"type":"step-completed","stepId":"poll-order","verdict":"PASS","durationMs":900}
            """;

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.Equal(3, step.AttemptCount);
    }

    [Fact]
    public void Parse_StepCompletedWithNoPriorAttemptEvent_DefaultsAttemptCountToOne()
    {
        const string content = """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""";

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.Equal(1, step.AttemptCount);
    }

    [Fact]
    public void Parse_MultipleSteps_PreservesFileOrder()
    {
        const string content = """
            {"type":"step-completed","stepId":"first","verdict":"PASS","durationMs":10}
            {"type":"step-completed","stepId":"second","verdict":"FAIL","durationMs":20}
            """;

        var summary = SuiteEventParser.Parse(content);

        Assert.Equal(["first", "second"], summary.Steps.Select(s => s.StepId));
        Assert.Equal(["Pass", "Fail"], summary.Steps.Select(s => s.Verdict));
    }

    [Fact]
    public void Parse_EnvironmentErrorEvent_IsCaptured()
    {
        const string content = """
            {"type":"environment-error","errorKind":"HealthGate","resourceName":"orders-db","detail":"timed out waiting for healthy"}
            """;

        var summary = SuiteEventParser.Parse(content);

        var error = Assert.Single(summary.EnvironmentErrors);
        Assert.Equal("HealthGate", error.ErrorKind);
        Assert.Equal("orders-db", error.ResourceName);
        Assert.Equal("timed out waiting for healthy", error.Detail);
    }

    [Fact]
    public void Parse_StepIdWithControlCharacters_IsSanitisedInTheResult()
    {
        // A step id ultimately traces back to caller-supplied suite YAML — untrusted input, same
        // threat model as everywhere else in this codebase (TextSanitiser's own remarks). Built via
        // JsonSerializer itself (never hand-typed JSON escaping in this source file), so the
        // control character reaches the parser exactly the way a real, well-formed JSON writer
        // would encode it — a JSON string escape, never a raw embedded byte.
        var controlCharacter = Convert.ToChar(0x1B);
        var rawStepId = "evil" + controlCharacter + "step";
        var content = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = rawStepId,
            verdict = "PASS",
            durationMs = 1,
        });

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.DoesNotContain(controlCharacter, step.StepId);
        Assert.Equal(TextSanitiser.SanitiseForDisplay(rawStepId), step.StepId);
    }

    [Fact]
    public void Parse_InvokesNarrationCallbackOnceForEachRecognisedEventInFileOrder()
    {
        const string content = """
            {"type":"step-attempt","stepId":"check-health","attempt":1}
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":10}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;
        var narrations = new List<string>();

        SuiteEventParser.Parse(content, narrations.Add);

        Assert.Equal(3, narrations.Count);
        Assert.Contains(narrations, n => n.Contains("attempt 1", StringComparison.Ordinal));
        Assert.Contains(narrations, n => n.Contains("check-health", StringComparison.Ordinal) && n.Contains("Pass", StringComparison.Ordinal));
        Assert.Contains(narrations, n => n.Contains("s1", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NullNarrationCallback_DoesNotThrow()
    {
        const string content = """{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}""";

        var summary = SuiteEventParser.Parse(content, onNarration: null);

        Assert.Equal(RunVerdict.Pass, summary.AggregateVerdict);
    }

    // ── REQ-007: RETRY attempt timelines and observation/diff evidence (used by explain_run) ─────

    [Fact]
    public void Parse_StepAttempts_AreKeptInFileOrderUnderTheStepsSanitisedId()
    {
        const string content = """
            {"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":100}
            {"type":"step-attempt","stepId":"poll-order","attempt":2,"tMs":300,"outcome":"INCONCLUSIVE"}
            {"type":"step-completed","stepId":"poll-order","verdict":"INCONCLUSIVE","durationMs":300}
            """;

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        var attempts = Assert.Single(summary.AttemptsByStepId, pair => pair.Key == step.StepId).Value;
        Assert.Equal(2, attempts.Count);
        Assert.Equal(1, attempts[0].Attempt);
        Assert.Equal(100, attempts[0].TMs);
        Assert.Null(attempts[0].Outcome);
        Assert.Equal(2, attempts[1].Attempt);
        Assert.Equal("Inconclusive", attempts[1].Outcome);
    }

    [Fact]
    public void Parse_StepWithNoAttemptEvents_HasNoEntryInAttemptsByStepId()
    {
        const string content = """{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""";

        var summary = SuiteEventParser.Parse(content);

        Assert.Empty(summary.AttemptsByStepId);
    }

    [Fact]
    public void Parse_StepCompletedObservation_IsCapturedAsSanitisedRawJsonText()
    {
        var content = JsonSerializer.Serialize(new
        {
            type = "step-completed",
            stepId = "assert-order-status",
            verdict = "FAIL",
            durationMs = 120,
            observation = new { column = "status", expected = "SHIPPED", actual = "PENDING" },
        });

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.NotNull(step.Observation);
        Assert.Contains("SHIPPED", step.Observation);
        Assert.Contains("PENDING", step.Observation);
    }

    [Fact]
    public void Parse_StepAttemptObservation_IsCapturedAsSanitisedRawJsonText()
    {
        var content = JsonSerializer.Serialize(new
        {
            type = "step-attempt",
            stepId = "poll-order",
            attempt = 1,
            tMs = 50,
            observation = new { matched = 0 },
        });

        var summary = SuiteEventParser.Parse(content);

        var attempts = Assert.Single(summary.AttemptsByStepId).Value;
        var attempt = Assert.Single(attempts);
        Assert.NotNull(attempt.Observation);
        Assert.Contains("matched", attempt.Observation);
    }

    [Fact]
    public void Parse_EnvironmentErrorFieldsLongerThanTheParseTimeCap_AreTruncated()
    {
        var hugeDetail = new string('d', 5_000);
        var content = JsonSerializer.Serialize(new
        {
            type = "environment-error",
            errorKind = "Provision",
            resourceName = "orders-db",
            detail = hugeDetail,
        });

        var summary = SuiteEventParser.Parse(content);

        var error = Assert.Single(summary.EnvironmentErrors);
        Assert.NotNull(error.Detail);
        Assert.True(error.Detail!.Length < hugeDetail.Length, "Expected the detail field to be capped at parse time.");
    }

    [Fact]
    public void Parse_HugeStepIdWithMultipleAttempts_AttemptCountReflectsTheCappedKeyConsistently()
    {
        // A review-found gap: the attempt-COUNT dictionary was keyed by the RAW (uncapped) stepId
        // while StepOutcome.StepId itself already used the CAPPED value -- retaining an
        // unbounded-length raw string as a dictionary KEY (exactly the memory bloat
        // MaxLabelCharsAtParse exists to prevent) and risking the count being RECORDED under one
        // key but LOOKED UP under another. A step with several attempts under a huge stepId proves
        // the fix: if recording and lookup used different keys, the count would silently default
        // back to 1 instead of reflecting the real attempt total.
        var hugeStepId = new string('s', 200_000);
        var content = string.Join('\n',
        [
            JsonSerializer.Serialize(new { type = "step-attempt", stepId = hugeStepId, attempt = 1, tMs = 10 }),
            JsonSerializer.Serialize(new { type = "step-attempt", stepId = hugeStepId, attempt = 2, tMs = 20 }),
            JsonSerializer.Serialize(new { type = "step-attempt", stepId = hugeStepId, attempt = 3, tMs = 30 }),
            JsonSerializer.Serialize(new { type = "step-completed", stepId = hugeStepId, verdict = "PASS", durationMs = 30 }),
        ]);

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.True(step.StepId.Length < 3_000, $"Expected stepId to be capped, was {step.StepId.Length} characters.");
        Assert.Equal(3, step.AttemptCount);

        // AttemptsByStepId (the RETRY timeline) is keyed by the SAME capped id -- confirms both
        // dictionaries agree on the key, closing the recording-vs-lookup mismatch.
        var attempts = Assert.Single(summary.AttemptsByStepId, pair => pair.Key == step.StepId).Value;
        Assert.Equal(3, attempts.Count);
    }

    // ── Wrong-shaped opportunistic fields must not cost the whole LINE (US-S3-06 regression) ─────

    /// <summary>
    /// A gatekeeper review's MAJOR finding, pinned. US-S3-06 added <c>error</c>, <c>ts</c> and
    /// <c>at</c> probes to <c>RunEvent</c>; typed as <c>string?</c>, an event carrying any of them in
    /// another JSON shape — <c>"ts": 123</c> is the obvious one, epoch milliseconds being a common
    /// JSON Lines spelling — made <c>JsonSerializer.Deserialize</c> throw for the WHOLE line, and this
    /// parser's tolerance is per line, so the line was dropped. On a <c>step-completed</c> event that
    /// costs the step its verdict and the run its aggregate one: a forward-compatible field spelling
    /// would have silently changed <c>run_suite</c>'s answer. The fields are
    /// <see cref="System.Text.Json.JsonElement"/> now, so a wrong shape costs only itself.
    /// </summary>
    [Theory]
    [InlineData("123")]                       // epoch millis, a number
    [InlineData("{\"iso\":\"2026-01-01\"}")]  // a structured instant
    [InlineData("null")]
    [InlineData("true")]
    public void Parse_StepCompletedWithANonStringTimestamp_StillProducesItsStepOutcome(string tsJson)
    {
        var content =
            $$"""{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":42,"ts":{{tsJson}}}""";

        var summary = SuiteEventParser.Parse(content);

        var step = Assert.Single(summary.Steps);
        Assert.Equal("check-health", step.StepId);
        Assert.Equal(nameof(RunVerdict.Pass), step.Verdict);
        Assert.Equal(42, step.DurationMs);
    }

    /// <summary>
    /// The same tolerance on the ATTEMPT side, and the complement it implies: the wrongly-shaped field
    /// reads as ABSENT (never as some coerced rendering of the number), while every other field on the
    /// line — including the <c>observation</c> evidence beside it — survives intact.
    /// </summary>
    [Fact]
    public void Parse_StepAttemptWithANonStringTimestampAndError_KeepsTheAttemptAndNullsOnlyThoseFields()
    {
        var content = string.Join('\n',
            """{"type":"step-attempt","stepId":"poll-order","attempt":1,"tMs":10,"outcome":"FAIL","ts":1757110872382,"error":{"code":7},"observation":{"got":"PENDING"}}""",
            """{"type":"step-completed","stepId":"poll-order","verdict":"FAIL","durationMs":10}""");

        var summary = SuiteEventParser.Parse(content);

        var attempt = Assert.Single(Assert.Single(summary.AttemptsByStepId).Value);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal(10, attempt.TMs);
        Assert.Equal(nameof(RunVerdict.Fail), attempt.Outcome);
        Assert.Contains("PENDING", attempt.Observation!, StringComparison.Ordinal);

        // Absent, not coerced: nothing invents "1757110872382" as a timestamp string.
        Assert.Null(attempt.At);
        Assert.Null(attempt.Error);

        // And the step-completed line beside it is unharmed.
        Assert.Single(summary.Steps);
    }

    /// <summary>
    /// The positive half, MEASURED against the pinned engine (see
    /// <c>RealStepAttemptEnvelopeAgainstPinnedCliTests</c>): a real <c>step-attempt</c> line carries a
    /// string <c>ts</c>, and it is relayed. This fixture is a verbatim line from that probe run, so a
    /// change in how the parser reads the field fails here without needing the engine installed.
    /// </summary>
    [Fact]
    public void Parse_AVerbatimPinnedEngineStepAttemptLine_RelaysItsTimestamp()
    {
        const string line =
            """{"v":1,"schemaVersion":"v1","type":"step-attempt","ts":"2026-09-05T22:21:12.3829238+00:00","runId":"50f92f64205341bead3d1680e4cd8c31","stepId":"retry-probe","attempt":1,"tMs":6,"outcome":"FAIL","observation":{"exists":{"expected":true,"actual":false}}}""";

        var summary = SuiteEventParser.Parse(line);

        var attempt = Assert.Single(Assert.Single(summary.AttemptsByStepId).Value);
        Assert.Equal("2026-09-05T22:21:12.3829238+00:00", attempt.At);
        Assert.Equal(6, attempt.TMs);
        Assert.Equal(nameof(RunVerdict.Fail), attempt.Outcome);
    }
}
