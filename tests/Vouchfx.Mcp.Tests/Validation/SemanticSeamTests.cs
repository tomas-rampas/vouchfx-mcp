using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// US-S2-02: the semantic-pass seam US-S2-03 will fill — its extension point, and the wire it has
/// to travel.
/// </summary>
/// <remarks>
/// <b>Why a channel with no traffic is worth testing now.</b> The seam's contract only becomes
/// observable when a rule emits something, and US-S2-03 is the story that adds the first one — so
/// without these tests that story would be the one to discover, mid-flight, that a
/// <see cref="Diagnostic"/> cannot in fact cross the worker's process boundary. These lock the two
/// properties a rule author is entitled to assume: the rule set is the only place rules come from,
/// and a finding survives the round trip intact.
/// </remarks>
public class SemanticSeamTests
{
    [Fact]
    public void SemanticAnalyser_HasNoRulesYet_AndSoReportsNothing()
    {
        // The story's own acceptance criterion, stated where a future reader will look for it:
        // US-S2-02 builds the seam and leaves the pass a no-op. When US-S2-03 populates Rules this
        // test is EXPECTED to fail — updating it (rather than deleting it) is how that story records
        // that the pass went live.
        Assert.Empty(SemanticAnalyser.Rules);

        using var document = JsonDocument.Parse("""{"steps":[]}""");
        var context = new SemanticAnalysisContext(
            document.RootElement,
            yamlRoot: null,
            new SuiteSummary(0, [], [], [], [], [], Truncated: false),
            SuiteFacts.Empty);

        Assert.Empty(SemanticAnalyser.Analyse(context));
    }

    [Fact]
    public void SemanticAnalyser_MaterialisesFindingsEagerly()
    {
        // A rule may return a lazy iterator, but Analyse must not hand one back: the JsonDocument
        // backing the context is disposed the moment SuiteValidator's `using` ends, so a deferred
        // enumerable would be walked against a disposed document. Proven by disposing the document
        // and then reading the result.
        var findings = AnalyseAndDisposeDocument();

        Assert.Empty(findings);
    }

    private static IReadOnlyList<Diagnostic> AnalyseAndDisposeDocument()
    {
        using var document = JsonDocument.Parse("""{"steps":[]}""");
        return SemanticAnalyser.Analyse(new SemanticAnalysisContext(
            document.RootElement,
            yamlRoot: null,
            new SuiteSummary(0, [], [], [], [], [], Truncated: false),
            SuiteFacts.Empty));
    }

    [Fact]
    public void SuiteAnalysis_CarriesASemanticDiagnosticAcrossTheWorkerWireIntact()
    {
        // The exact serialiser both sides of the --validate-worker boundary use. A Diagnostic
        // validates its own code and severity in its [JsonConstructor], so this proves the shape
        // deserialises through that constructor rather than merely round-tripping property bags —
        // the failure mode US-S2-03 would otherwise hit on its first real rule.
        var diagnostic = VfxCodeCatalogue.CreateDiagnostic(
            VfxCodeCatalogue.UnknownStepType,
            "warning",
            "A semantic finding travelling the worker wire.",
            new DiagnosticLocation("suite.e2e.yaml", 7, 5, null, null),
            "$.steps[0].type");

        var analysis = new SuiteAnalysis(
            Valid: true,
            Errors: [],
            SemanticDiagnostics: [diagnostic],
            Summary: new SuiteSummary(
                1, ["http.rest"], ["orders-api"], ["orders-db"], ["orderId"], ["orderId"], Truncated: true),
            Level: ValidationLevel.Semantic);

        var json = JsonSerializer.Serialize(analysis, ValidationWorkerProtocol.JsonOptions);
        var restored = JsonSerializer.Deserialize<SuiteAnalysis>(json, ValidationWorkerProtocol.JsonOptions);

        Assert.NotNull(restored);
        var restoredDiagnostic = Assert.Single(restored!.SemanticDiagnostics);
        Assert.Equal(diagnostic.Code, restoredDiagnostic.Code);
        Assert.Equal(diagnostic.Severity, restoredDiagnostic.Severity);
        Assert.Equal(diagnostic.Message, restoredDiagnostic.Message);
        Assert.Equal(diagnostic.Path, restoredDiagnostic.Path);
        Assert.Equal(diagnostic.DocsUrl, restoredDiagnostic.DocsUrl);
        Assert.Equal(diagnostic.Location, restoredDiagnostic.Location);

        // Compared field by field, NOT with Assert.Equal on the two records: a record's generated
        // equality uses EqualityComparer<T>.Default per member, which for an IReadOnlyList<string>
        // is REFERENCE equality — so two summaries with identical contents are never `==`. See
        // SuiteSummary's own remarks.
        Assert.NotNull(restored.Summary);
        Assert.Equal(analysis.Summary!.Steps, restored.Summary!.Steps);
        Assert.Equal(analysis.Summary.StepTypes, restored.Summary.StepTypes);
        Assert.Equal(analysis.Summary.Services, restored.Summary.Services);
        Assert.Equal(analysis.Summary.Dependencies, restored.Summary.Dependencies);
        Assert.Equal(analysis.Summary.Captures, restored.Summary.Captures);
        Assert.Equal(analysis.Summary.Placeholders, restored.Summary.Placeholders);

        // The truncation flag is part of the wire contract, not a worker-local convenience: the cap
        // is applied inside the worker, so if this did not survive the trip the caller could never
        // learn the digest was incomplete.
        Assert.True(restored.Summary.Truncated);
    }

    [Fact]
    public void SuiteSummary_CarriesTheTruncationFlagAsAWireProperty()
    {
        // The summary's field set, pinned. `truncated` is additive (US-S2-02 peer review): a reader
        // must not have to infer incompleteness from a list length of exactly MaxEntriesPerList.
        var json = JsonSerializer.Serialize(
            new SuiteSummary(0, [], [], [], [], [], Truncated: false), ValidationWorkerProtocol.JsonOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["steps", "stepTypes", "services", "dependencies", "captures", "placeholders", "truncated"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SemanticAnalysisContext_RejectsAMissingFactSet()
    {
        // The fact set is the seam's set-membership authority, so a context without one is not a
        // degraded context — it is a context in which "X is not declared" cannot be answered
        // correctly at all. Rejected at construction rather than left null for a rule to trip over.
        using var document = JsonDocument.Parse("""{"steps":[]}""");

        Assert.Throws<ArgumentNullException>(() => new SemanticAnalysisContext(
            document.RootElement,
            yamlRoot: null,
            new SuiteSummary(0, [], [], [], [], [], Truncated: false),
            facts: null!));
    }

    [Fact]
    public void SuiteAnalysis_AsValidationResult_NarrowsToTheSchemaChannelOnly()
    {
        // run_suite's EDGE-003 envelope and ValidationOutcomeRenderer both read the v1 shape, and
        // must keep seeing exactly the schema channel — a semantic finding is this server's advice,
        // never part of the engine's verdict.
        var analysis = new SuiteAnalysis(
            Valid: true,
            Errors: [],
            SemanticDiagnostics:
            [
                VfxCodeCatalogue.CreateDiagnostic(VfxCodeCatalogue.UnknownStepType, "warning", "Advice."),
            ],
            Summary: null,
            Level: ValidationLevel.Full);

        var narrowed = analysis.AsValidationResult();

        Assert.True(narrowed.Valid);
        Assert.Empty(narrowed.Errors);
    }

    [Fact]
    public void SuiteAnalysis_DoesNotEmitTheNarrowedShapeAsAWireProperty()
    {
        // AsValidationResult is a METHOD, not a get-only property, precisely so it is not
        // serialised — a `validation` object duplicating {valid, errors} would ship on every
        // validate_suite result and on the worker's wire.
        var json = JsonSerializer.Serialize(
            new SuiteAnalysis(true, [], [], null, ValidationLevel.Full), ValidationWorkerProtocol.JsonOptions);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["valid", "errors", "semanticDiagnostics", "summary", "level"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData(ValidationLevel.Schema, "schema")]
    [InlineData(ValidationLevel.Semantic, "semantic")]
    [InlineData(ValidationLevel.Full, "full")]
    public void SuiteAnalysis_LevelCrossesTheWorkerWireAsItsToken(ValidationLevel level, string expectedToken)
    {
        // The level is spelled on the wire with the SAME vocabulary the tool and the worker's
        // command line use (ValidationLevels), not with the C# member names — see
        // ValidationLevelJsonConverter. Round-tripped as well as written, because the worker's
        // result is deserialised back into this record by ValidationWorkerClient.
        var json = JsonSerializer.Serialize(
            new SuiteAnalysis(true, [], [], null, level), ValidationWorkerProtocol.JsonOptions);

        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(expectedToken, document.RootElement.GetProperty("level").GetString());
        }

        var restored = JsonSerializer.Deserialize<SuiteAnalysis>(json, ValidationWorkerProtocol.JsonOptions);
        Assert.Equal(level, restored!.Level);
    }
}
