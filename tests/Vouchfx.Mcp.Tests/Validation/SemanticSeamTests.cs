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

    /// <summary>
    /// The reference shape a rule must never echo, spelled once so every case below quotes the same
    /// literal — and so the "not one character of it survives" assertions have something exact to
    /// test against.
    /// </summary>
    private const string SecretReference = "${secret:vault/prod-db-password}";

    [Theory]
    // The two surfaces the fourth round's guard already covered...
    [InlineData("Capture '" + SecretReference + "' is never used.", null, null, null, "Message")]
    [InlineData("Capture is never used.", "$.steps[0].capture['" + SecretReference + "']", null, null, "Path")]
    // ...and the two the FIFTH round found missing. A Fix is rule-composed, wire-serialised prose in
    // exactly the way a Message is, and Replacement is the one field a host may apply verbatim — so
    // "here is the corrected line" was, until now, an unguarded door out of the fact set.
    [InlineData(
        "Capture is never used.", null, "Delete the capture '" + SecretReference + "'.", null, "Fix.Description")]
    [InlineData(
        "Capture is never used.", null, "Delete the unused capture.", "name: " + SecretReference, "Fix.Replacement")]
    public void SemanticAnalyser_FailsTheCallWhenARuleEchoesASecretReference(
        string message,
        string? path,
        string? fixDescription,
        string? fixReplacement,
        string expectedOffendingField)
    {
        // The MAJOR finding from the fourth peer-review round, made structural, and widened by the
        // fifth. SuiteFacts deliberately retains `${secret:…}`-shaped identifiers so a rule can
        // answer "is this capture declared?" — which means the most NATURAL way to write US-S2-03's
        // first rule ("capture X is never used", interpolating a fact-set entry) would publish the
        // caller's secret store layout on a valid:true result. Analyse is the one choke point every
        // finding crosses, so the check lives there and this test drives a real rule through it.
        using var document = JsonDocument.Parse("""{"steps":[]}""");
        var rule = new FakeRule(VfxCodeCatalogue.CreateDiagnostic(
            VfxCodeCatalogue.UnknownStepType,
            "warning",
            message,
            location: null,
            path: path,
            fix: fixDescription is null ? null : new DiagnosticFix(fixDescription, fixReplacement)));

        var thrown = Assert.Throws<SemanticRuleContractViolationException>(() => SemanticAnalyser.Analyse(
            new SemanticAnalysisContext(
                document.RootElement,
                yamlRoot: null,
                new SuiteSummary(0, [], [], [], [], [], Truncated: false),
                SuiteFacts.Empty),
            [rule]));

        // A DEDICATED type, not a bare InvalidOperationException: Program.cs's --validate-worker
        // catch prints this one's Message (and only this one's) precisely because it is content-free
        // by construction, so the operator sees the rule code and the field instead of
        // "crashed: InvalidOperationException.". Still an InvalidOperationException underneath, so
        // every existing boundary that catches that keeps behaving as it did.
        Assert.IsAssignableFrom<InvalidOperationException>(thrown);

        // Names the offending rule AND the offending field, so the defect is fixable from the log
        // alone...
        Assert.Contains(rule.Code, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(rule.Code, thrown.RuleCode);
        Assert.Equal(expectedOffendingField, thrown.OffendingField);
        Assert.Contains(expectedOffendingField, thrown.Message, StringComparison.Ordinal);

        // ...and carries not one character of the reference itself. An exception message reaches a
        // log; reproducing the reference there would be the same disclosure, relocated.
        Assert.DoesNotContain("secret:", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prod-db-password", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticRuleContractViolationException_BoundsWhatAThrowSiteCanPutInItsMessage()
    {
        // The worker-boundary-shaped half of the same finding: Program.cs prints THIS type's Message
        // verbatim to stderr while every other exception gets only its type name, and that asymmetry
        // is only safe if "content-free" is a property of the type rather than a habit of its throw
        // sites. It has no constructor taking free text, and the two identifiers it does take are
        // SanitiseForEcho-bounded — so even a throw site passing hostile input can neither flood the
        // operator's log nor smuggle a control sequence into it.
        // Built numerically rather than typed as a literal, so this source file stays printable
        // ASCII (the same trick SuiteValidator's own control-character cases use): what is under
        // test is what the sanitiser does with the character, not what an editor does with it.
        var bel = new string((char)0x07, 1);

        var thrown = new SemanticRuleContractViolationException(
            bel + new string('x', 500), nameof(Diagnostic.Message));

        // The control character is escaped into a printable literal, never emitted raw...
        Assert.DoesNotContain(bel, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("\\u0007", thrown.Message, StringComparison.Ordinal);

        // ...and the 64-character cap bites, so a 500-character "rule code" cannot become 500
        // characters of log.
        Assert.DoesNotContain(new string('x', 100), thrown.Message, StringComparison.Ordinal);
        Assert.EndsWith("…", thrown.RuleCode, StringComparison.Ordinal);
        Assert.Contains(thrown.RuleCode, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticAnalyser_LetsAFindingWhoseLocationFileContainsAReferenceThrough()
    {
        // The documented NEGATIVE of the guard, pinned rather than merely asserted in prose:
        // DiagnosticLocation.File is the CALLER's own suite path echoed back, not prose the rule
        // composed, so a workspace directory that happens to contain `${` must not crash every
        // finding on every suite under it. (No rule can populate File today — the context carries no
        // suite path — which is exactly why the exclusion needs a test rather than a reader's trust.)
        using var document = JsonDocument.Parse("""{"steps":[]}""");
        var finding = VfxCodeCatalogue.CreateDiagnostic(
            VfxCodeCatalogue.UnknownStepType,
            "warning",
            "Capture 'orderId' is never used.",
            new DiagnosticLocation("/home/dev/${weird}/suite.e2e.yaml", 3, 1, null, null),
            path: "$.steps[0].capture.orderId");

        var findings = SemanticAnalyser.Analyse(
            new SemanticAnalysisContext(
                document.RootElement,
                yamlRoot: null,
                new SuiteSummary(0, [], [], [], [], [], Truncated: false),
                SuiteFacts.Empty),
            [new FakeRule(finding)]);

        Assert.Same(finding, Assert.Single(findings));
    }

    [Fact]
    public void SemanticAnalyser_RejectsARuleThatYieldsANullFinding()
    {
        // A rule yielding a null element is breaking its contract too. The guard states that
        // contract (ArgumentNullException naming the parameter) instead of letting the field reads
        // produce a bare NullReferenceException at the same worker boundary.
        using var document = JsonDocument.Parse("""{"steps":[]}""");

        Assert.Throws<ArgumentNullException>(() => SemanticAnalyser.Analyse(
            new SemanticAnalysisContext(
                document.RootElement,
                yamlRoot: null,
                new SuiteSummary(0, [], [], [], [], [], Truncated: false),
                SuiteFacts.Empty),
            [new NullYieldingRule()]));
    }

    [Fact]
    public void SemanticAnalyser_LetsACleanFindingThroughUnchanged()
    {
        // The guard is a filter on ONE shape, not a general narrowing of what a rule may say: a
        // finding that names a bounded identifier flows through untouched, in rule order.
        using var document = JsonDocument.Parse("""{"steps":[]}""");
        var clean = VfxCodeCatalogue.CreateDiagnostic(
            VfxCodeCatalogue.UnknownStepType,
            "warning",
            "Capture 'orderId' is never used.",
            location: null,
            path: "$.steps[0].capture.orderId");

        var findings = SemanticAnalyser.Analyse(
            new SemanticAnalysisContext(
                document.RootElement,
                yamlRoot: null,
                new SuiteSummary(0, [], [], [], [], [], Truncated: false),
                SuiteFacts.Empty),
            [new FakeRule(clean)]);

        Assert.Same(clean, Assert.Single(findings));
    }

    /// <summary>
    /// A rule that emits exactly what it is handed — the only way to exercise
    /// <see cref="SemanticAnalyser"/>'s hygiene gate before US-S2-03 ships a real rule, and
    /// deliberately a rule rather than a direct call to the guard, so the test proves the CHOKE
    /// POINT rejects rather than that a private helper would have.
    /// </summary>
    private sealed class FakeRule(Diagnostic finding) : ISemanticRule
    {
        public string Code => VfxCodeCatalogue.UnknownStepType;

        public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context) => [finding];
    }

    /// <summary>
    /// A rule that yields a null element — the shape <c>ISemanticRule</c>'s signature permits but
    /// its contract does not, and the reason the guard states that contract explicitly.
    /// </summary>
    private sealed class NullYieldingRule : ISemanticRule
    {
        public string Code => VfxCodeCatalogue.UnknownStepType;

        public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context) => [null!];
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
