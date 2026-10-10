using System.Text.Json;
using System.Text.Json.Nodes;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Xunit.Sdk;

namespace Vouchfx.Mcp.Tests;

public class RequiredStepAttemptEvidenceTests
{
    private const string CompleteEvidence = """
        [
          {"type":"step-attempt","stepId":"retry-probe","outcome":"FAIL"},
          {"type":"step-completed","stepId":"immediate-probe","verdict":"PASS"},
          {"type":"step-completed","stepId":"retry-probe","verdict":"INCONCLUSIVE","observation":{"reason":"retry-timeout","attempts":1}},
          {"type":"scenario-completed","scenarioId":"step-attempt envelope probe","verdict":"INCONCLUSIVE"}
        ]
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OptionalMode_IsAnExplicitDiscoverySkip(string? mode)
    {
        var attribute = new RequiredRealProbeFactAttribute(mode);

        Assert.Contains("evidence was not requested", attribute.Skip);
        Assert.Contains($"{RequiredRealProbeFactAttribute.EnvironmentVariable}=1", attribute.Skip);
    }

    [Fact]
    public void RequiredMode_AcceptsTheExpectedInconclusiveRetryTimeout()
    {
        Assert.Null(new RequiredRealProbeFactAttribute("1").Skip);
        RequiredStepAttemptEvidence.RequireMode("1");
        RequiredStepAttemptEvidence.RequirePin(new CliPinResult.Ok("1.0.0-rc.6"), "v1.0.0-rc.6");
        RequiredStepAttemptEvidence.RequireProcess(new SuiteProcessResult(4, RunTermination.CompletedNormally), true, "");
        RequiredStepAttemptEvidence.RequireEvents(ParseEvents(CompleteEvidence));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("typo")]
    public void InvalidOptIn_IsNotSkippedAndCannotDowngradeRequiredEvidence(string mode)
    {
        Assert.Null(new RequiredRealProbeFactAttribute(mode).Skip);
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireMode(mode));

        Assert.Contains("[mode]", failure.Message);
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("version-mismatch")]
    [InlineData("unparseable")]
    public void MissingOrUnverifiablePinnedCli_FailsRequiredEvidence(string outcome)
    {
        CliPinResult result = outcome switch
        {
            "not-found" => new CliPinResult.NotFound("CLI unavailable"),
            "version-mismatch" => new CliPinResult.VersionMismatch("9.0.0", "v1.0.0-rc.6", "Wrong CLI"),
            _ => new CliPinResult.Unparseable("garbage", "Not a version"),
        };

        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequirePin(result, "v1.0.0-rc.6"));

        Assert.Contains("[cli-pin]", failure.Message);
        Assert.Contains(result.GetType().Name, failure.Message);
        Assert.Contains("v1.0.0-rc.6", failure.Message);
    }

    [Theory]
    [InlineData(RunTermination.Aborted, 4)]
    [InlineData(RunTermination.CompletedNormally, null)]
    [InlineData(RunTermination.CompletedNormally, 0)]
    [InlineData(RunTermination.CompletedNormally, 1)]
    [InlineData(RunTermination.CompletedNormally, 2)]
    [InlineData(RunTermination.CompletedNormally, 3)]
    public void AbortedOrUnexpectedProcessExit_FailsEvenWithAnEventsFile(RunTermination termination, int? exitCode)
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireProcess(
            new SuiteProcessResult(exitCode, termination), true, "topology did not execute"));

        Assert.Contains("[process]", failure.Message);
        Assert.Contains("Expected CompletedNormally, exit 4", failure.Message);
        Assert.Contains("topology did not execute", failure.Message);
    }

    [Fact]
    public void ExpectedProcessExitWithoutEvents_IsMissingEvidence()
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireProcess(
            new SuiteProcessResult(4, RunTermination.CompletedNormally), false, "events write failed"));

        Assert.Contains("[events-file]", failure.Message);
    }

    [Theory]
    [InlineData("step-attempt", "retry-probe", "[retry-attempts]")]
    [InlineData("step-completed", "immediate-probe", "[step-completed]")]
    [InlineData("step-completed", "retry-probe", "[step-completed]")]
    [InlineData("scenario-completed", null, "[scenario-completed]")]
    public void UnexecutedRequiredStepOrScenario_FailsInsteadOfPassingPartialEvidence(
        string type, string? stepId, string expectedStage)
    {
        var events = ParseEvents(CompleteEvidence).Where(e =>
            e.GetProperty("type").GetString() != type
            || (stepId is not null && e.GetProperty("stepId").GetString() != stepId)).ToList();

        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(events));

        Assert.Contains(expectedStage, failure.Message);
    }

    [Theory]
    [InlineData("step-completed", "immediate-probe", "FAIL", "[step-completed]")]
    [InlineData("step-completed", "immediate-probe", "INCONCLUSIVE", "[step-completed]")]
    [InlineData("step-completed", "immediate-probe", "ENV_ERROR", "[step-completed]")]
    [InlineData("step-completed", "retry-probe", "PASS", "[step-completed]")]
    [InlineData("step-completed", "retry-probe", "FAIL", "[step-completed]")]
    [InlineData("step-completed", "retry-probe", "ENV_ERROR", "[step-completed]")]
    [InlineData("scenario-completed", null, "PASS", "[scenario-completed]")]
    [InlineData("scenario-completed", null, "FAIL", "[scenario-completed]")]
    [InlineData("scenario-completed", null, "ENV_ERROR", "[scenario-completed]")]
    public void WrongRequiredVerdict_FailsEvenWhenCompletionEventsExist(
        string type, string? stepId, string verdict, string expectedStage)
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(
            ChangeEvent(type, stepId, e => e["verdict"] = verdict)));

        Assert.Contains(expectedStage, failure.Message);
    }

    [Theory]
    [InlineData("[{\"type\":\"environment-error\"}]", "[events-environment]")]
    [InlineData("[]", "[retry-attempts]")]
    [InlineData("[{\"type\":\"step-attempt\",\"stepId\":\"unrelated-step\"}]", "[retry-attempts]")]
    [InlineData("[null,3,{\"type\":3,\"stepId\":null}]", "[events-shape]")]
    public void EnvironmentErrorEmptyOrUnrelatedEvents_CannotCountAsExecution(string json, string expectedStage)
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(ParseEvents(json)));

        Assert.Contains(expectedStage, failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("retry-exhausted")]
    [InlineData("cancelled")]
    [InlineData("environment-error")]
    public void InconclusiveWithAnUnexpectedReason_IsNotTheRequiredRetryTimeout(string? reason)
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(
            ChangeEvent("step-completed", "retry-probe", e => e["observation"]!["reason"] = reason)));

        Assert.Contains("[retry-timeout]", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRetryObservationOrReason_CannotEstablishTimeoutEvidence(bool keepObservation)
    {
        var events = ChangeEvent("step-completed", "retry-probe", e =>
        {
            if (keepObservation)
            {
                e["observation"]!.AsObject().Remove("reason");
            }
            else
            {
                e.Remove("observation");
            }
        });

        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(events));

        Assert.Contains("[retry-timeout]", failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PASS")]
    [InlineData("INCONCLUSIVE")]
    [InlineData("ENV_ERROR")]
    public void UnexpectedAttemptOutcome_CannotEstablishTheUnmatchedRetryLoop(string? outcome)
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(
            ChangeEvent("step-attempt", "retry-probe", e => e["outcome"] = outcome)));

        Assert.Contains("[retry-attempts]", failure.Message);
    }

    [Theory]
    [InlineData("step-completed", "immediate-probe", "[step-completed]")]
    [InlineData("step-completed", "retry-probe", "[step-completed]")]
    [InlineData("scenario-completed", null, "[scenario-completed]")]
    public void DuplicateRequiredCompletionEvents_AreAmbiguousEvidence(string type, string? stepId, string expectedStage)
    {
        var events = ParseEvents(CompleteEvidence);
        events.Add(Assert.Single(events, e => e.GetProperty("type").GetString() == type
            && (stepId is null || e.GetProperty("stepId").GetString() == stepId)));

        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(events));

        Assert.Contains(expectedStage, failure.Message);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[3]")]
    [InlineData("[{\"type\":3}]")]
    [InlineData("[{}]")]
    public void MalformedExtraEvent_InvalidatesOtherwiseCompleteEvidence(string malformedJson)
    {
        var events = ParseEvents(CompleteEvidence);
        events.AddRange(ParseEvents(malformedJson));

        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(events));

        Assert.Contains("[events-shape]", failure.Message);
    }

    [Fact]
    public void CompletionOfAnUnrelatedScenario_CannotReplaceTheRequiredScenario()
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireEvents(
            ParseEvents(CompleteEvidence.Replace("step-attempt envelope probe", "another scenario", StringComparison.Ordinal))));

        Assert.Contains("[scenario-completed]", failure.Message);
    }

    [Fact]
    public void ProcessFailureDiagnostic_IsBoundedAndControlCharactersAreEscaped()
    {
        var failure = Assert.Throws<XunitException>(() => RequiredStepAttemptEvidence.RequireProcess(
            new SuiteProcessResult(3, RunTermination.CompletedNormally), false, new string('\u0001', 30_000)));

        Assert.True(failure.Message.Length <= RequiredStepAttemptEvidence.MaxDiagnosticChars);
        Assert.DoesNotContain('\u0001', failure.Message);
        Assert.Contains("[process]", failure.Message);
        Assert.EndsWith("[truncated]", failure.Message);
    }

    private static List<JsonElement> ParseEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static List<JsonElement> ChangeEvent(string type, string? stepId, Action<JsonObject> change)
    {
        var events = JsonNode.Parse(CompleteEvidence)!.AsArray();
        var target = Assert.Single(events.Select(e => e!.AsObject()), e => e["type"]!.GetValue<string>() == type
            && (stepId is null || e["stepId"]!.GetValue<string>() == stepId));
        change(target);
        return ParseEvents(events.ToJsonString());
    }
}
