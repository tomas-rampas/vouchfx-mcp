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
}
