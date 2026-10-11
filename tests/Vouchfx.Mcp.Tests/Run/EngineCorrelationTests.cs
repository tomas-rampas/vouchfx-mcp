using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

public class EngineCorrelationTests
{
    private static readonly string[] SecretCanary = ["canary"];
    private static readonly string[] CaseDistinctIds = ["a", "A"];
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("{}")]
    [InlineData("[\"not-an-id\"]")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"line\\nfeed\"")]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"caf\\u00e9\"")]
    public void UnsupportedOptionalId_PreservesTheEventAndReportsPartialCorrelation(string runId)
    {
        var summary = SuiteEventParser.Parse($$"""{"type":"step-completed","stepId":"check","verdict":"FAIL","runId":{{runId}}}""");
        Assert.Equal("Fail", Assert.Single(summary.Steps).Verdict);
        Assert.True(summary.SawStepEvent);
        Assert.Empty(summary.EngineCorrelation!.RunIds);
        Assert.True(summary.EngineCorrelation.Partial);
    }

    [Theory]
    [InlineData("\"first\",\"runId\":\"second\"")]
    [InlineData("\"first\",\"runId\":\"first\"")]
    [InlineData("null,\"runId\":\"second\"")]
    [InlineData("\"first\",\"RunId\":\"second\"")]
    public void DuplicateIdProperties_AreOmittedWithoutApplyingLastWins(string valueAndDuplicate)
    {
        var summary = SuiteEventParser.Parse($$"""{"type":"scenario-completed","verdict":"PASS","runId":{{valueAndDuplicate}}}""");
        Assert.Equal(RunVerdict.Pass, summary.AggregateVerdict);
        Assert.Empty(summary.EngineCorrelation!.RunIds);
        Assert.True(summary.EngineCorrelation.Partial);
    }

    [Fact]
    public void TooLongIds_AreOmittedRatherThanCollapsingDistinctPrefixes()
    {
        var prefix = new string('a', EngineCorrelation.MaxRunIdChars);
        var oversized = new string('x', 1_000_000);
        var events = string.Join('\n', new[] { prefix, prefix + "1", prefix + "2", oversized }
            .Select(id => JsonSerializer.Serialize(new { type = "scenario-completed", verdict = "PASS", runId = id })));
        var correlation = SuiteEventParser.Parse(events).EngineCorrelation!;
        Assert.Equal(prefix, Assert.Single(correlation.RunIds));
        Assert.True(correlation.Partial);
    }

    [Fact]
    public void DistinctIds_AreOrdinalAndBoundedWithVisibleOmission()
    {
        var ids = Enumerable.Range(0, EngineCorrelation.MaxRunIds + 1).Select(i => $"engine-{i}").ToArray();
        var events = string.Join('\n', ids.Concat([ids[0], "ENGINE-0"])
            .Select(id => JsonSerializer.Serialize(new { type = "future-event", runId = id, secretReferences = SecretCanary })));
        var correlation = SuiteEventParser.Parse(events).EngineCorrelation!;
        Assert.Equal(ids.Take(EngineCorrelation.MaxRunIds), correlation.RunIds);
        Assert.True(correlation.Partial);
        Assert.DoesNotContain("canary", JsonSerializer.Serialize(correlation), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)correlation.RunIds).Add("mutated"));

        var caseDistinct = SuiteEventParser.Parse("""{"type":"future","runId":"a"}""" + "\n" + """{"type":"future","runId":"A"}""").EngineCorrelation!;
        Assert.Equal(CaseDistinctIds, caseDistinct.RunIds);
        Assert.False(caseDistinct.Partial);
    }

    [Fact]
    public void AParseThatStopsAtTheExistingLineLimit_DoesNotClaimCompleteCorrelation()
    {
        var first = """{"type":"scenario-completed","verdict":"PASS","runId":"first"}""";
        var summary = SuiteEventParser.Parse(first + "\n" + new string('\n', 1_999_999) + """{"type":"future","runId":"after-limit"}""");
        Assert.Equal("first", Assert.Single(summary.EngineCorrelation!.RunIds));
        Assert.True(summary.EngineCorrelation.Partial);
    }

    [Fact]
    public void MalformedNeighbor_IsIgnoredWithoutClaimingCompleteObservedIds()
    {
        var summary = SuiteEventParser.Parse("not-json\n" + """{"type":"scenario-completed","verdict":"PASS","runId":"valid"}""");
        Assert.Equal(RunVerdict.Pass, summary.AggregateVerdict);
        Assert.Equal("valid", Assert.Single(summary.EngineCorrelation!.RunIds));
        Assert.True(summary.EngineCorrelation.Partial);
        Assert.Null(SuiteEventParser.Parse("not-json\n" + """{"type":"scenario-completed","verdict":"PASS"}""").EngineCorrelation);
    }

    [Fact]
    public void OldPositionalConstructorsAndDeconstruction_KeepTheirShapeAndOmitAbsentCorrelation()
    {
        var summary = new SuiteRunSummary(null, [], [], new Dictionary<string, IReadOnlyList<StepAttempt>>(), new Dictionary<string, StepStartedInfo>());
        var (verdict, steps, errors, attempts, declarations) = summary;
        Assert.Null(verdict);
        Assert.Empty(steps);
        Assert.Empty(errors);
        Assert.Empty(attempts);
        Assert.Empty(declarations);
        var diagnosis = new Vouchfx.Mcp.Diagnosis.Diagnosis("Pass", "meaning", "summary", 0, 0, [], 0, [], 0, "events", false, false, []);
        var timeline = new GetStepTimelineResult("suite", "step", null, null, null, [], "summary", false, 0, false, true);
        var artifacts = new GetRunArtifactsResult("run-" + new string('0', 32), "reports", false, null, null, null, null, 200, []);
        foreach (var payload in new object[] { summary, diagnosis, timeline, artifacts })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, WebOptions));
            Assert.False(document.RootElement.TryGetProperty("engineCorrelation", out _));
        }

        Assert.Equal(13, typeof(Vouchfx.Mcp.Diagnosis.Diagnosis).GetMethod("Deconstruct")!.GetParameters().Length);
        Assert.Equal(11, typeof(GetStepTimelineResult).GetMethod("Deconstruct")!.GetParameters().Length);
        Assert.Equal(9, typeof(GetRunArtifactsResult).GetMethod("Deconstruct")!.GetParameters().Length);
    }
}
