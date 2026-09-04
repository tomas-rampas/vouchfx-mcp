using System.Text.RegularExpressions;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03 at the PASS level: the rule set as a whole, the channel separation it must preserve, and
/// the single-parse discipline the whole seam is shaped around.
/// </summary>
public class SemanticPassTests
{
    /// <summary>
    /// One suite engineered to trip every ENABLED rule at once — Gherkin scenario 7's fixture.
    /// </summary>
    /// <remarks>
    /// Deliberately also schema-INVALID in one place (the <c>mq-expect.kafka</c> step omits its
    /// required <c>match</c>), because Gherkin scenario 6 needs a document carrying both a schema
    /// error and a semantic finding to prove the two arrays never merge.
    /// </remarks>
    private const string EveryCodeSuite = """
        environment:
          services:
            orders-api:
              image: orders:1.0
          dependencies:
            orders-db:
              type: postgres
        steps:
          - id: fetch-order
            type: http.rest
            target: orders-api
            method: GET
            path: /orders/{orderId}
            headers:
              authorization: "AKIAIOSFODNN7EXAMPLE"
          - id: create-order
            type: http.rest
            target: ordres-api
            method: POST
            path: /orders
            capture:
              orderId: "$.id"
              unusedOne: "$.total"
          - id: create-order
            type: mq-expect.nonexistent-provider
            target: broker
            topic: orders
          - id: consume
            type: mq-expect.kafka
            target: broker
            topic: orders
          - id: retry-poll
            type: webhook-listen.http
            listener: callbacks
            match:
              path: /hooks/orders
            verifyMode: RETRY
        """;

    [Fact]
    public void EveryEnabledRuleIsRegisteredExactlyOnce_AndTheDisabledOneIsNot()
    {
        // The registry is append-only and its ORDER is the reported order (see SemanticAnalyser.Rules).
        Assert.Equal(
            [
                VfxCodeCatalogue.UnknownStepType,
                VfxCodeCatalogue.DanglingTargetReference,
                VfxCodeCatalogue.PlaceholderUsedBeforeDefinition,
                VfxCodeCatalogue.UnusedCapture,
                VfxCodeCatalogue.UndeclaredDependencyType,
                VfxCodeCatalogue.RetryTimeoutPolicy,
                VfxCodeCatalogue.SecretLiteralInSuite,
                VfxCodeCatalogue.DuplicateStepId,
                VfxCodeCatalogue.AsyncStepWithoutRetry,
                VfxCodeCatalogue.MetadataIncomplete,
            ],
            SemanticAnalyser.Rules.Select(rule => rule.Code));
    }

    [Fact]
    public void OneSuiteTripsEveryEnabledCode_AndNeverTheDisabledOne()
    {
        // Gherkin scenario 7's first clause, and the coverage claim the story's acceptance criteria
        // make: all ten enabled codes reachable from one document.
        var analysis = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Full);

        var codes = analysis.SemanticDiagnostics.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            SemanticAnalyser.Rules.Select(rule => rule.Code).ToHashSet(StringComparer.Ordinal),
            codes);

        Assert.DoesNotContain(VfxCodeCatalogue.TopologyCrossCheck, codes);
    }

    [Fact]
    public void TheSchemaErrorAndTheSemanticFindingNeverShareOneArray()
    {
        // Gherkin scenario 6. The mq-expect.kafka step above omits its required `match`, so the
        // schema channel has something to say — and it must say it THERE and only there.
        var analysis = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Full);

        Assert.NotEmpty(analysis.Errors);
        Assert.NotEmpty(analysis.SemanticDiagnostics);

        // The one code both channels legitimately carry is VFX-D-1201, by the adjudicated channel
        // decision recorded on UnknownStepTypeRule — every OTHER schema code is absent from the
        // semantic channel and every other semantic code is absent from the schema channel.
        var schemaCodes = analysis.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var semanticCodes = analysis.SemanticDiagnostics.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Equal([VfxCodeCatalogue.UnknownStepType], schemaCodes.Intersect(semanticCodes, StringComparer.Ordinal));
        Assert.Contains(VfxCodeCatalogue.SchemaViolation, schemaCodes);
        Assert.DoesNotContain(VfxCodeCatalogue.SchemaViolation, semanticCodes);
    }

    [Fact]
    public void LevelSchema_RunsNoRuleAtAll()
    {
        var analysis = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Schema);

        Assert.Empty(analysis.SemanticDiagnostics);
        Assert.NotEmpty(analysis.Errors);
    }

    [Fact]
    public void LevelSemantic_RunsNoSchemaEvaluationAtAll()
    {
        var analysis = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Semantic);

        Assert.Empty(analysis.Errors);
        Assert.NotEmpty(analysis.SemanticDiagnostics);
    }

    [Fact]
    public void ASemanticWarningNeverFlipsTheVerdict_ButASemanticErrorDoes()
    {
        // The reconciliation this story had to make between `SuiteAnalysis.Valid` (v1: "the schema
        // channel is empty") and the spec's own Gherkin for VFX-D-1207 ("And ok is false"). The
        // dividing line is SEVERITY, and it is asserted from both sides here.
        var warningsOnly = SuiteValidator.AnalyseYaml(
            """
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
            """,
            ValidationLevel.Full);

        Assert.NotEmpty(warningsOnly.SemanticDiagnostics);
        Assert.DoesNotContain(warningsOnly.SemanticDiagnostics, d => d.Severity == "error");
        Assert.True(warningsOnly.Valid);

        var withSemanticError = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Semantic);

        Assert.Contains(withSemanticError.SemanticDiagnostics, d => d.Severity == "error");
        Assert.False(withSemanticError.Valid);
        Assert.Empty(withSemanticError.Errors);
    }

    [Fact]
    public void EveryFindingCarriesTheCataloguesOwnDocsUrl()
    {
        var analysis = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Semantic);

        Assert.All(analysis.SemanticDiagnostics, finding =>
            Assert.Equal(VfxCodeCatalogue.DocsUrlFor(finding.Code), finding.DocsUrl));
    }

    /// <summary>
    /// A suite of <paramref name="steps"/> steps, each declaring one capture nothing ever uses — so
    /// the finding count is exactly the step count.
    /// </summary>
    private static string SuiteWithOneUnusedCapturePerStep(int steps) =>
        "metadata:\n  owner: t\n  tags: [x]\nsteps:\n" + string.Concat(
            Enumerable.Range(0, steps).Select(i =>
                $"  - id: s{i}\n"
                + "    type: http.rest\n"
                + "    target: orders-api\n"
                + "    method: GET\n"
                + $"    path: /orders/{i}\n"
                + "    capture:\n"
                + $"      unused{i}: \"$.id\"\n"));

    [Fact]
    public void TheSemanticChannelIsCappedAndSaysSoWhenItBites()
    {
        // The measured hazard: a semantic finding is per-NODE for some rules, so nothing about a
        // VALID document bounds how many it produces. A 3.3 MB valid suite produced 200 000 findings
        // and a 94 MB result — across a worker pipe capped at 50 MB, and into a host's context
        // window. What must stay bounded is the WIRE, exactly as SuiteSummaryBuilder.MaxEntriesPerList
        // states for the digest.
        using var over = SemanticRuleFixture.For(
            SuiteWithOneUnusedCapturePerStep(SemanticAnalyser.MaxPublishedFindings + 25));

        var outcome = over.RunWithOutcome(new UnusedCaptureRule());

        Assert.Equal(SemanticAnalyser.MaxPublishedFindings, outcome.Findings.Count);
        Assert.True(outcome.Truncated);
    }

    [Fact]
    public void ASuiteExactlyAtTheCapIsNotReportedAsTruncated()
    {
        // The off-by-one that makes the flag worth having: a document with exactly the cap's worth
        // of findings is COMPLETE, and a consumer inferring incompleteness from a count of exactly
        // 1 000 would be wrong about it. That is why the flag exists rather than the length.
        using var atCap = SemanticRuleFixture.For(
            SuiteWithOneUnusedCapturePerStep(SemanticAnalyser.MaxPublishedFindings));

        var outcome = atCap.RunWithOutcome(new UnusedCaptureRule());

        Assert.Equal(SemanticAnalyser.MaxPublishedFindings, outcome.Findings.Count);
        Assert.False(outcome.Truncated);
    }

    [Fact]
    public void TheTruncationFlagReachesTheAnalysisResult()
    {
        // End of the wire, not just the seam: SuiteAnalysis is what crosses the worker boundary and
        // what a host reads, so the flag has to be carried there or the cap is silent.
        var analysis = SuiteValidator.AnalyseYaml(
            SuiteWithOneUnusedCapturePerStep(SemanticAnalyser.MaxPublishedFindings + 25),
            ValidationLevel.Semantic);

        Assert.Equal(SemanticAnalyser.MaxPublishedFindings, analysis.SemanticDiagnostics.Count);
        Assert.True(analysis.SemanticDiagnosticsTruncated);

        // A small suite is not truncated, and says so — the flag is not stuck on.
        var small = SuiteValidator.AnalyseYaml(EveryCodeSuite, ValidationLevel.Semantic);
        Assert.False(small.SemanticDiagnosticsTruncated);
    }

    [Fact]
    public void NoRuleSourceFileCanParseAnythingItself()
    {
        // THE single-parse assertion, and it is deliberately STRUCTURAL rather than a stopwatch.
        //
        // The measured hazard is on record: an earlier revision that re-parsed per finding took 31.9
        // SECONDS on a 2 000-error suite against the validation worker's 10-second wall clock, and
        // it surfaced as VFX-E-1150 (a killed worker) rather than as a slow rule — so a timing test
        // would be both flaky and, when it did fail, uninformative about the cause. What actually
        // makes a re-parse impossible is that a rule is handed the ALREADY-PARSED document and never
        // the text: SemanticAnalysisContext carries no YAML string, and there is no Analyse overload
        // that takes one. This scan is what keeps that true against a rule author who reaches for a
        // parser anyway (by reading a file, or by re-serialising the JsonElement and re-parsing it).
        //
        // Derived from SOURCE rather than from a hand-maintained list, following
        // SecretHygieneSourceGuardTests' pattern, for the same fail-closed reason.
        string[] forbidden =
        [
            "JsonDocument.Parse",
            "YamlStream",
            "YamlToJsonConverter",
            "TryParseYamlRoot",
            "File.Read",
            "File.Open",
        ];

        var validationDir = Path.Combine(RepoRoot.FullName, "src", "Vouchfx.Mcp", "Validation");
        var semanticsDir = Path.Combine(validationDir, "Semantics");

        // Semantics/ plus the three HELPERS that live outside it and are called from inside a rule's
        // Evaluate. The directory boundary is not the contract boundary: PlaceholderScanner,
        // UnknownStepTypeDetector and DependencyKinds all run per-call on rule-supplied input, so a
        // parse added to any of them would reintroduce the measured hazard from a file the original
        // scan never looked at.
        string[] helpers = ["PlaceholderScanner.cs", "UnknownStepTypeDetector.cs", "DependencyKinds.cs"];

        var files = Directory.EnumerateFiles(semanticsDir, "*.cs", SearchOption.AllDirectories)
            .Concat(helpers.Select(name => Path.Combine(validationDir, name)))
            .ToArray();

        // Anti-vacuity: a wrong path must fail loudly rather than scan nothing. Asserted for the
        // helpers individually, because a renamed helper would otherwise silently drop out of scope.
        Assert.NotEmpty(files);
        Assert.All(files, path => Assert.True(File.Exists(path), $"Scanned file '{path}' does not exist."));

        var offenders = files
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .SelectMany(file => forbidden
                .Where(token => file.Text.Contains(token, StringComparison.Ordinal))
                .Where(token => !IsTheSanctionedVendoredSchemaParse(file.Path, token))
                .Select(token => $"{Path.GetFileName(file.Path)}: {token}"))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A semantic rule must consume the already-parsed document the seam hands it, never parse "
            + $"anything itself (see Validation/Semantics/SemanticAnalysis.cs's header): {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// The ONE allowed parse in the scanned set: <c>DependencyKinds</c>' static initialiser reading
    /// the EMBEDDED VENDORED SCHEMA once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction the ban is actually about is WHAT is parsed, not whether a parse exists.</b>
    /// The measured hazard is re-parsing the CALLER'S SUITE — untrusted content up to
    /// <c>YamlSafetyGuard.MaxSuiteSizeBytes</c>, once per finding, inside a 10-second worker budget
    /// (31.9 s on a 2 000-error suite). <c>DependencyKinds.Load</c> parses a fixed, in-assembly
    /// manifest resource exactly once for the process lifetime, before any call arrives, and can
    /// never touch a suite: it takes no input at all. <see cref="StepTypeCatalogue"/> has done the
    /// same thing since v1 for the same reason.
    /// </para>
    /// <para>
    /// Encoded as a file-and-token pair rather than as a blanket exemption for the file, so a
    /// <c>File.Read</c> or a <c>YamlStream</c> appearing in <c>DependencyKinds</c> tomorrow still
    /// fails — only the schema-resource parse is sanctioned.
    /// </para>
    /// </remarks>
    private static bool IsTheSanctionedVendoredSchemaParse(string filePath, string token) =>
        Path.GetFileName(filePath) == "DependencyKinds.cs" &&
        token == "JsonDocument.Parse" &&
        File.ReadAllText(filePath).Contains("GetManifestResourceStream", StringComparison.Ordinal);

    [Fact]
    public void TheSeamStillOffersNoTextTakingEntryPoint()
    {
        // The other half of the structural argument: even a rule that WANTED to re-parse has nothing
        // to re-parse from. Asserted against the real type rather than trusted to a reader.
        var textCarrying = typeof(SemanticAnalysisContext)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        // SourceName is the suite's IDENTITY (a path, or the inline marker), not its content — it is
        // what lets a finding carry a DiagnosticLocation. Nothing else on the context is a string.
        Assert.Equal(["SourceName"], textCarrying);

        Assert.DoesNotContain(
            typeof(SemanticAnalyser).GetMethods(),
            method => method.Name == nameof(SemanticAnalyser.Analyse)
                && method.GetParameters().Any(p => p.ParameterType == typeof(string)));
    }

    [Fact]
    public void EverySemanticCodeIsScannableFromSrcAndCatalogued()
    {
        // The completeness gate's own precondition, restated for THIS story's ten new codes: each
        // must appear as a VFX-D-12xx literal in src/ (the catalogue constant is the site) and carry
        // an entry. The docs-page half is ErrorCatalogueFilesystemParityTests' job.
        var semanticRange = new Regex(@"^VFX-D-12\d{2}$", RegexOptions.Compiled);
        var catalogued = VfxCodeCatalogue.All
            .Select(entry => entry.Code)
            .Where(code => semanticRange.IsMatch(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "VFX-D-1201", "VFX-D-1202", "VFX-D-1203", "VFX-D-1204", "VFX-D-1205", "VFX-D-1206",
                "VFX-D-1207", "VFX-D-1208", "VFX-D-1209", "VFX-D-1210", "VFX-D-1211",
            ],
            catalogued);
    }

    /// <summary>Mirrors <c>SecretHygieneSourceGuardTests.RepoRoot</c> exactly — see that property's remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");

            return testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");
        }
    }
}
