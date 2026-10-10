using System.Text.Json;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Diagnosis;

public sealed class CaptureMetadataDiagnosticsTests
{
    // Verbatim step-completed record from the measured 2026-09-15 rc.5 probe retained by #86.
    // This is NOT a newly measured rc.6 fixture; the private channel consumes its frozen-v1 shape.
    internal const string MeasuredRc5Step = """
        {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-09-15T17:30:14.7235166+00:00","runId":"4a880c4c9d5344bba54d1a172b3a632f","stepId":"capture-present-and-missing","verdict":"INCONCLUSIVE","durationMs":13,"captured":[{"name":"hostname","path":"$.hostname","matched":true},{"name":"missing","path":"$.doesNotExist","matched":false}],"observation":{"captureUnmet":"missing"}}
        """;

    [Theory]
    [InlineData("observation_case_alias")]
    [InlineData("observation_case_alias_no_unmet")]
    [InlineData("escaped_observation_case_alias")]
    [InlineData("stepid_case_alias")]
    [InlineData("type_exact_duplicate")]
    [InlineData("verdict_exact_duplicate")]
    public async Task AmbiguousRootAuthority_DoesNotAuthorizeDiscardedCaptureMetadata(string control)
    {
        var events = PeerAliasEvent(control);
        var result = await Diagnose(events);
        var reason = Assert.Single(result.Diagnosis.NotableSteps).Reason!;
        Assert.DoesNotContain("declared capture", reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.discarded", reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain(result.SpecEditProposals, proposal => proposal.Scope == SpecEditScopes.Capture);

        var ordinary = SuiteEventParser.Parse(events);
        var diagnostics = SuiteEventParser.ParseForCaptureDiagnostics(events);
        Assert.Single(ordinary.Steps);
        Assert.Equal(JsonSerializer.Serialize(ordinary.Steps), JsonSerializer.Serialize(diagnostics.Steps));
        Assert.Empty(diagnostics.CaptureDeclarations);
    }

    [Theory]
    [InlineData("root-type")]
    [InlineData("root-verdict")]
    [InlineData("root-run-id")]
    [InlineData("root-captured")]
    [InlineData("observation-capture-unmet")]
    [InlineData("entry-name")]
    [InlineData("entry-path")]
    [InlineData("entry-matched")]
    [InlineData("entry-escaped-name")]
    public void DecodedCaseAliasDuplicates_RefuseOptionalMetadataOnly(string control)
    {
        var metadata = """[{"name":"missing","path":"$.own","matched":false}]""";
        metadata = control switch
        {
            "entry-name" => metadata.Replace("\"name\":\"missing\"", "\"name\":\"missing\",\"Name\":\"other\"", StringComparison.Ordinal),
            "entry-path" => metadata.Replace("\"path\":\"$.own\"", "\"path\":\"$.own\",\"Path\":\"$.other\"", StringComparison.Ordinal),
            "entry-matched" => metadata.Replace("\"matched\":false", "\"matched\":false,\"Matched\":true", StringComparison.Ordinal),
            "entry-escaped-name" => metadata.Replace("\"name\":\"missing\"", "\"name\":\"missing\",\"\\u004eame\":\"other\"", StringComparison.Ordinal),
            _ => metadata,
        };
        var events = Event("probe", metadata);
        events = control switch
        {
            "root-type" => events[..^1] + ",\"Type\":\"step-completed\"}",
            "root-verdict" => events[..^1] + ",\"Verdict\":\"INCONCLUSIVE\"}",
            "root-run-id" => events[..^1] + ",\"runId\":\"first\",\"RunId\":\"second\"}",
            "root-captured" => events[..^1] + ",\"Captured\":[]}",
            "observation-capture-unmet" => events.Replace("\"captureUnmet\":\"missing\"", "\"captureUnmet\":\"missing\",\"CaptureUnmet\":\"other\"", StringComparison.Ordinal),
            _ => events,
        };
        var ordinary = SuiteEventParser.Parse(events);
        var diagnostics = SuiteEventParser.ParseForCaptureDiagnostics(events);
        Assert.Single(diagnostics.Steps);
        Assert.Empty(diagnostics.CaptureDeclarations);
        Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("declared capture", VerdictReasonClassifier.ClassifyStep(diagnostics.Steps[0], diagnostics)!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueRootAliases_FollowPrimaryBindingAndUnknownPropertiesRemainOptional()
    {
        var events = Event("probe", """[{"name":"missing","path":"$.own","matched":false,"future":"{\"Name\":1,\"name\":2}"}]""")
            .Replace("\"stepId\"", "\"StepId\"", StringComparison.Ordinal)
            .Replace("\"observation\"", "\"\\u004fbservation\"", StringComparison.Ordinal)
            .Replace("\"type\"", "\"Type\"", StringComparison.Ordinal)
            .Replace("\"verdict\"", "\"Verdict\"", StringComparison.Ordinal);
        events = events[..^1] + ",\"future\":{\"name\":1,\"Name\":2}}";
        var ordinary = SuiteEventParser.Parse(events);
        var diagnostics = SuiteEventParser.ParseForCaptureDiagnostics(events);
        Assert.Single(diagnostics.CaptureDeclarations);
        Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(diagnostics));
        Assert.Contains("$.own", VerdictReasonClassifier.ClassifyStep(diagnostics.Steps[0], diagnostics)!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_MeasuredCaptureMiss_NamesItsOwnJsonPath()
    {
        var result = await Explain(MeasuredRc5Step);
        var reason = Assert.Single(result.NotableSteps).Reason!;
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Contains("$.doesNotExist", reason.Hint, StringComparison.Ordinal);
        Assert.Contains("declared capture missing", reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.hostname", reason.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnose_MeasuredCaptureMiss_TargetsItsDeclaringStepAndNamesThePath()
    {
        var result = await Diagnose(MeasuredRc5Step);
        var proposal = Assert.Single(result.SpecEditProposals);
        Assert.Equal("capture-present-and-missing", proposal.StepId);
        Assert.Equal(SpecEditScopes.Capture, proposal.Scope);
        Assert.Contains("$.doesNotExist", proposal.Rationale, StringComparison.Ordinal);
        Assert.Contains("$.doesNotExist", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.Contains("'missing':", proposal.SuggestedEdit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObservationOnlyLegacyRecord_KeepsClassificationWithoutDeclaringStepAttribution()
    {
        var result = await Diagnose(Event("consumer", null));
        var reason = Assert.Single(result.Diagnosis.NotableSteps).Reason!;
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.Contains("missing", reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("declared capture", reason.Hint, StringComparison.Ordinal);
        Assert.Empty(result.SpecEditProposals);
    }

    [Fact]
    public async Task ConsumingStep_WithOtherCaptures_DoesNotPatchItsUpstreamsDeclaration()
    {
        var result = await Diagnose(Event("consumer", """[{"name":"other","path":"$.consumer","matched":false}]"""));
        var reason = Assert.Single(result.Diagnosis.NotableSteps).Reason!;
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.DoesNotContain("declared capture", reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.consumer", reason.Hint, StringComparison.Ordinal);
        Assert.Empty(result.SpecEditProposals);
    }

    [Fact]
    public async Task DuplicateOrMixedCaptureNames_DoNotEstablishADeclaration()
    {
        var result = await Diagnose(Event("probe", """
            [{"name":"missing","path":"$.first","matched":false},{"name":"missing","path":"$.second","matched":true}]
            """));
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, Assert.Single(result.Diagnosis.NotableSteps).Reason!.Kind);
        Assert.Empty(result.SpecEditProposals);
    }

    [Fact]
    public async Task CappedStepIdCollision_DoesNotJoinCaptureMetadataFromAnotherEvent()
    {
        var prefix = new string('s', 2000);
        var events = Event(prefix + "first", """[{"name":"missing","path":"$.first","matched":false}]""")
            + "\n" + Event(prefix + "second", """[{"name":"missing","path":"$.second","matched":false}]""");
        var result = await Explain(events);
        Assert.Equal(2, result.NotableSteps.Count);
        Assert.Equal(result.NotableSteps[0].StepId, result.NotableSteps[1].StepId);
        Assert.Contains("$.first", result.NotableSteps[0].Reason!.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.second", result.NotableSteps[0].Reason!.Hint, StringComparison.Ordinal);
        Assert.Contains("$.second", result.NotableSteps[1].Reason!.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.first", result.NotableSteps[1].Reason!.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"not a list\"")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\"missing\",\"path\":\"$.bad\",\"matched\":\"false\"}]")]
    [InlineData("[{\"name\":\"missing\",\"path\":null,\"matched\":false}]")]
    [InlineData("[{\"name\":23,\"path\":\"$.bad\",\"matched\":false}]")]
    [InlineData("[{\"name\":\"missing\",\"path\":\"$.bad\",\"matched\":true}]")]
    [InlineData("[{\"name\":\"missing\",\"name\":\"other\",\"path\":\"$.bad\",\"matched\":false}]")]
    [InlineData("[{\"name\":\"missing\",\"\\u006eame\":\"other\",\"path\":\"$.bad\",\"matched\":false}]")]
    [InlineData("[{\"name\":\"missing\",\"path\":\"$.first\",\"matched\":false},{\"name\":\"missing\",\"path\":\"$.second\",\"matched\":false}]")]
    [InlineData("[{\"name\":\"\\uD800\",\"path\":\"$.bad\",\"matched\":false}]")]
    public async Task MalformedAmbiguousOrNonMatchingMetadata_KeepsTheLegacyOutcomeWithoutAPatch(string captured)
    {
        var result = await Diagnose(Event("probe", captured));
        var step = Assert.Single(result.Diagnosis.NotableSteps);
        Assert.Equal("Inconclusive", step.Verdict);
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, step.Reason!.Kind);
        Assert.Contains("reported unmet capture missing", step.Reason.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.bad", step.Reason.Hint, StringComparison.Ordinal);
        Assert.Empty(result.SpecEditProposals);
    }

    [Fact]
    public async Task UnknownPropertiesValuesAndFutureEvents_AreNotRelayedIntoCaptureDiagnostics()
    {
        const string canary = "CAPTURE_VALUE_MUST_NEVER_BE_RELAYED";
        const string secretCanary = "SECRET_REFERENCE_CANARY_MUST_NEVER_BE_RELAYED";
        var events = "{\"type\":\"reproducibility-envelope\",\"secretReferences\":[\"" + secretCanary + "\"]}\n"
            + Event("probe", """[{"name":"missing","path":"$.own","matched":false,"value":"CAPTURE_VALUE_MUST_NEVER_BE_RELAYED","future":{"payload":"ignored"}}]""")
            + "\n{\"type\":\"future-capture-event\",\"captured\":[{\"name\":\"" + canary + "\"}]}";
        var result = await Diagnose(events);
        Assert.Contains("$.own", Assert.Single(result.Diagnosis.NotableSteps).Reason!.Hint, StringComparison.Ordinal);
        Assert.Single(result.SpecEditProposals);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretCanary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secretReferences", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeControlAndQuotePath_IsSanitisedAndBoundedWithoutResolvingIt()
    {
        var path = "$.é'\n\u001b" + new string('p', 400);
        var metadata = JsonSerializer.Serialize(new[] { new { name = "missing", path, matched = false } });
        var result = await Diagnose(Event("probe", metadata));
        var reason = Assert.Single(result.Diagnosis.NotableSteps).Reason!;
        Assert.DoesNotContain(reason.Hint, character => character < 0x20);
        Assert.Contains("\\u00e9", reason.Hint, StringComparison.Ordinal);
        Assert.Contains("\\u000a", reason.Hint, StringComparison.Ordinal);
        Assert.EndsWith("…", reason.Evidence!.CapturePath, StringComparison.Ordinal);
        Assert.Equal(CaptureMetadataParser.MaxPathChars, reason.Evidence.CapturePath!.Length);
        Assert.True(reason.Hint.Length <= VerdictReasonClassifier.MaxHintChars);
        var proposal = Assert.Single(result.SpecEditProposals);
        Assert.Contains("Declared extractor:", proposal.SuggestedEdit, StringComparison.Ordinal);
        Assert.DoesNotContain(proposal.SuggestedEdit, character => character is '\u001b' or '\r');
    }

    [Fact]
    public async Task SanitisationCollisionInNames_IsAmbiguousEvenWhenRawNamesDiffer()
    {
        var metadata = JsonSerializer.Serialize(new[]
        {
            new { name = "a\n", path = "$.first", matched = false },
            new { name = "a\\u000a", path = "$.second", matched = false },
        });
        var result = await Diagnose(Event("probe", metadata, "a\n"));
        Assert.Empty(result.SpecEditProposals);
        Assert.DoesNotContain("$.first", Assert.Single(result.Diagnosis.NotableSteps).Reason!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualOutcomesAcrossEventsAndParses_UseReferenceIdentityAndCannotLeakMetadata()
    {
        var first = Event("same", """[{"name":"missing","path":"$.first","matched":false}]""");
        var second = Event("same", """[{"name":"missing","path":"$.second","matched":false}]""");
        var summary = SuiteEventParser.ParseForCaptureDiagnostics(first + "\n" + second);
        Assert.Equal(summary.Steps[0], summary.Steps[1]); // Records are value-equal.
        Assert.Equal(2, summary.CaptureDeclarations.Count);
        Assert.Contains("$.first", VerdictReasonClassifier.ClassifyStep(summary.Steps[0], summary)!.Hint, StringComparison.Ordinal);
        Assert.Contains("$.second", VerdictReasonClassifier.ClassifyStep(summary.Steps[1], summary)!.Hint, StringComparison.Ordinal);
        var another = SuiteEventParser.ParseForCaptureDiagnostics(second);
        var unrelated = VerdictReasonClassifier.ClassifyStep(summary.Steps[0], another)!;
        Assert.DoesNotContain("$.second", unrelated.Hint, StringComparison.Ordinal);
        Assert.False(unrelated.Evidence!.CaptureTargetVerified);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<StepOutcome, CaptureDeclaration>)summary.CaptureDeclarations).Clear());
    }

    [Fact]
    public void DefaultParser_RetainsNoCaptureChannelAndStepWireShapeIsIdentical()
    {
        var ordinary = SuiteEventParser.Parse(MeasuredRc5Step);
        var diagnostics = SuiteEventParser.ParseForCaptureDiagnostics(MeasuredRc5Step);
        Assert.Empty(ordinary.CaptureDeclarations);
        Assert.Single(diagnostics.CaptureDeclarations);
        Assert.Equal(JsonSerializer.Serialize(ordinary.Steps), JsonSerializer.Serialize(diagnostics.Steps));
        Assert.Equal(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(diagnostics));
        Assert.DoesNotContain("CaptureDeclarations", JsonSerializer.Serialize(diagnostics), StringComparison.Ordinal);
        Assert.False(VerdictReasonClassifier.ClassifyStep(ordinary.Steps[0], ordinary)!.Evidence!.CaptureTargetVerified);
    }

    [Fact]
    public void EventAndSummaryWorkLimits_DoNotDropStepOutcomesOrLegacyClassification()
    {
        var list = Enumerable.Range(0, CaptureMetadataParser.MaxEntries + 1)
            .Select(index => new { name = index == 0 ? "missing" : "other-" + index, path = "$.own", matched = false });
        var tooManyEntries = SuiteEventParser.ParseForCaptureDiagnostics(Event("probe", JsonSerializer.Serialize(list)));
        Assert.Single(tooManyEntries.Steps);
        Assert.Empty(tooManyEntries.CaptureDeclarations);

        var oversized = Event("probe", """[{"name":"missing","path":"$.own","matched":false}]""");
        oversized = oversized[..^1] + ",\"future\":\"" + new string('x', CaptureMetadataParser.MaxEventChars) + "\"}";
        var tooLargeEvent = SuiteEventParser.ParseForCaptureDiagnostics(oversized);
        Assert.Single(tooLargeEvent.Steps);
        Assert.Empty(tooLargeEvent.CaptureDeclarations);

        var events = string.Join("\n", Enumerable.Range(0, CaptureMetadataParser.MaxEventsWithMetadata + 1)
            .Select(index => Event("step-" + index, """[{"name":"missing","path":"$.own","matched":false}]""")));
        var capped = SuiteEventParser.ParseForCaptureDiagnostics(events);
        Assert.Equal(CaptureMetadataParser.MaxEventsWithMetadata + 1, capped.Steps.Count);
        Assert.Equal(CaptureMetadataParser.MaxEventsWithMetadata, capped.CaptureDeclarations.Count);
        var fallback = VerdictReasonClassifier.ClassifyStep(capped.Steps[^1], capped)!;
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, fallback.Kind);
        Assert.Contains("reported unmet", fallback.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamesAndStepIdsThatCannotSurviveFragmentRendering_DoNotProduceATargetedPatch()
    {
        var metadata = JsonSerializer.Serialize(new[] { new { name = "é", path = "$.own", matched = false } });
        var unicodeName = await Diagnose(Event("probe", metadata, "é"));
        Assert.Contains("$.own", Assert.Single(unicodeName.Diagnosis.NotableSteps).Reason!.Hint, StringComparison.Ordinal);
        Assert.Empty(unicodeName.SpecEditProposals);

        var longId = await Diagnose(Event(new string('s', 121), """[{"name":"missing","path":"$.own","matched":false}]"""));
        Assert.Contains("$.own", Assert.Single(longId.Diagnosis.NotableSteps).Reason!.Hint, StringComparison.Ordinal);
        Assert.Empty(longId.SpecEditProposals);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("path")]
    [InlineData("entry-properties")]
    [InlineData("root-properties")]
    public void IndividualMetadataWorkBounds_PreserveTheStepAndFallback(string limit)
    {
        var name = limit == "name" ? new string('n', CaptureMetadataParser.MaxNameChars + 1) : "missing";
        var path = limit == "path" ? new string('p', 2001) : "$.own";
        var item = JsonSerializer.Serialize(new { name, path, matched = false });
        if (limit == "entry-properties")
        {
            item = item[..^1] + string.Concat(Enumerable.Range(0, 17).Select(index => ",\"future-" + index + "\":null")) + "}";
        }

        var events = Event("probe", "[" + item + "]", name);
        if (limit == "root-properties")
        {
            events = events[..^1] + string.Concat(Enumerable.Range(0, 65).Select(index => ",\"future-" + index + "\":null")) + "}";
        }

        var summary = SuiteEventParser.ParseForCaptureDiagnostics(events);
        Assert.Single(summary.Steps);
        Assert.Empty(summary.CaptureDeclarations);
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, VerdictReasonClassifier.ClassifyStep(summary.Steps[0], summary)!.Kind);
    }

    [Fact]
    public void ListBoundaryAndCaseDistinctNames_AreAcceptedWithoutOverRefusal()
    {
        var entries = Enumerable.Range(0, CaptureMetadataParser.MaxEntries)
            .Select(index => new { name = index == 0 ? "missing" : index == 1 ? "Missing" : "other-" + index, path = "$.own", matched = false });
        var summary = SuiteEventParser.ParseForCaptureDiagnostics(Event("probe", JsonSerializer.Serialize(entries)));
        Assert.Single(summary.CaptureDeclarations);
        Assert.Contains("$.own", VerdictReasonClassifier.ClassifyStep(summary.Steps[0], summary)!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitisedStepIdCollision_KeepsEachEventsOwnCapturePath()
    {
        var first = Event("s\n", """[{"name":"missing","path":"$.first","matched":false}]""");
        var second = Event("s\\u000a", """[{"name":"missing","path":"$.second","matched":false}]""");
        var result = await Explain(first + "\n" + second);
        Assert.Equal(result.NotableSteps[0].StepId, result.NotableSteps[1].StepId);
        Assert.Contains("$.first", result.NotableSteps[0].Reason!.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.second", result.NotableSteps[0].Reason!.Hint, StringComparison.Ordinal);
        Assert.Contains("$.second", result.NotableSteps[1].Reason!.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("$.first", result.NotableSteps[1].Reason!.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesThatCollideAfterDisplayCapping_DoNotEstablishADeclaration()
    {
        var prefix = new string('é', 20);
        var entries = JsonSerializer.Serialize(new[]
        {
            new { name = prefix + "a", path = "$.first", matched = false },
            new { name = prefix + "b", path = "$.second", matched = false },
        });
        var summary = SuiteEventParser.ParseForCaptureDiagnostics(Event("probe", entries, prefix + "a"));
        Assert.Single(summary.Steps);
        Assert.Empty(summary.CaptureDeclarations);
        var reason = VerdictReasonClassifier.ClassifyStep(summary.Steps[0], summary)!;
        Assert.Equal(VerdictReasonKinds.CaptureUnmet, reason.Kind);
        Assert.False(reason.Evidence!.CaptureTargetVerified);
        Assert.DoesNotContain("$.first", reason.Hint, StringComparison.Ordinal);
    }

    internal static string PeerAliasEvent(string control)
    {
        // Exact synthetic peer controls; the primary Web serializer accepts these duplicate/alias
        // fields. Optional private metadata must not grant authority to a discarded binding.
        const string baseline = """
            {"type":"step-completed","stepId":"probe","verdict":"INCONCLUSIVE","durationMs":13,"captured":[{"name":"missing","path":"$.discarded","matched":false}],"observation":{"captureUnmet":"missing"}}
            """;
        return control switch
        {
            "observation_case_alias" => baseline[..^1] + ",\"Observation\":{\"captureUnmet\":\"other\"}}",
            "observation_case_alias_no_unmet" => baseline[..^1] + ",\"Observation\":{\"reason\":\"timeout\"}}",
            "escaped_observation_case_alias" => baseline[..^1] + ",\"\\u004fbservation\":{\"captureUnmet\":\"other\"}}",
            "stepid_case_alias" => baseline[..^1] + ",\"StepId\":\"consumer\"}",
            "type_exact_duplicate" => baseline.Replace("\"type\":\"step-completed\"", "\"type\":\"scenario-completed\",\"type\":\"step-completed\"", StringComparison.Ordinal),
            "verdict_exact_duplicate" => baseline.Replace("\"verdict\":\"INCONCLUSIVE\"", "\"verdict\":\"PASS\",\"verdict\":\"INCONCLUSIVE\"", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };
    }

    internal static string Event(string stepId, string? captured, string captureName = "missing")
    {
        var metadata = captured is null ? string.Empty : ",\"captured\":" + captured;
        return $$"""
            {"v":1,"schemaVersion":"v1","type":"step-completed","stepId":{{JsonSerializer.Serialize(stepId)}},"verdict":"INCONCLUSIVE","durationMs":13,"observation":{"captureUnmet":{{JsonSerializer.Serialize(captureName)}}}{{metadata}}}
            """;
    }

    internal static async Task<DiagnoseRunResult> Diagnose(string events)
    {
        var path = WriteEvents(events);
        try
        {
            var outcome = await new DiagnoseRunOrchestrator(new ExplainRunOrchestrator(new InMemoryRunRegistry()))
                .DiagnoseAsync(path, CancellationToken.None);
            return Assert.IsType<DiagnoseRunOutcome.Diagnosed>(outcome).Result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static async Task<Vouchfx.Mcp.Diagnosis.Diagnosis> Explain(string events)
    {
        var path = WriteEvents(events);
        try
        {
            var outcome = await new ExplainRunOrchestrator(new InMemoryRunRegistry())
                .ExplainAsync(path, CancellationToken.None);
            return Assert.IsType<ExplainRunOutcome.Diagnosed>(outcome).Diagnosis;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteEvents(string events)
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-capture-diagnostics-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, events);
        return path;
    }
}
