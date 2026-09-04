using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// US-S2-02: the <c>summary</c> block <c>validate_suite</c> v2 returns, and the <c>level</c>
/// selector that gates which passes run.
/// </summary>
/// <remarks>
/// Driven through <see cref="SuiteValidator.AnalyseYaml(string, ValidationLevel)"/> rather than
/// against <see cref="SuiteSummaryBuilder"/> in isolation, deliberately: the acceptance criterion
/// is that the summary is derived from the SINGLE parse the schema pass already performs, so the
/// test that matters is the one that goes through that same entry point. The builder's own
/// <see cref="System.Text.Json.JsonElement"/>-level behaviour is fully observable from here.
/// </remarks>
public class SuiteSummaryTests
{
    /// <summary>
    /// A schema-valid two-step suite with one capture, one service, one dependency, and one
    /// <c>{placeholder}</c> usage — the Gherkin scenario 1 shape, plus the environment block the
    /// services/dependencies fields are derived from.
    /// </summary>
    private const string TwoStepSuiteWithCapture = """
        metadata:
          name: "Inline summary probe"
          owner: "platform-team"

        environment:
          services:
            orders-api:
              image: "example/orders-api:1.0"
          dependencies:
            orders-db:
              type: postgres

        steps:
          - id: create-order
            type: http.rest
            target: orders-api
            method: POST
            path: /orders
            capture:
              orderId: "$.id"

          - id: fetch-order
            type: http.rest
            target: orders-api
            method: GET
            path: /orders/{orderId}
        """;

    [Fact]
    public void AnalyseYaml_TwoStepSuiteWithOneCapture_ReportsStepCountAndCaptureName()
    {
        var analysis = SuiteValidator.AnalyseYaml(TwoStepSuiteWithCapture, ValidationLevel.Full);

        Assert.True(analysis.Valid, string.Join("; ", analysis.Errors.Select(e => $"{e.Code} {e.Message}")));

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);
        Assert.Equal(2, summary.Steps);
        Assert.Contains("orderId", summary.Captures);
    }

    [Fact]
    public void AnalyseYaml_NamesServicesAndDependenciesFromTheEnvironmentBlock()
    {
        var analysis = SuiteValidator.AnalyseYaml(TwoStepSuiteWithCapture, ValidationLevel.Full);

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);
        Assert.Equal(["orders-api"], summary.Services);
        Assert.Equal(["orders-db"], summary.Dependencies);
    }

    [Fact]
    public void AnalyseYaml_CollectsDistinctStepTypesInFirstAppearanceOrder()
    {
        const string yaml = """
            steps:
              - id: b
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
              - id: a
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: c
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 2"
                expect:
                  rowCount: 1
            """;

        var summary = Assert.IsType<SuiteSummary>(SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full).Summary);

        // Distinct, and in the order the document first mentions each type — never sorted, so a
        // reader can line the list up against the suite they are looking at.
        Assert.Equal(["db-assert.postgres", "http.rest"], summary.StepTypes);
        Assert.Equal(3, summary.Steps);
    }

    [Fact]
    public void AnalyseYaml_CollectsPlaceholderTokensAndNeverSecretReferences()
    {
        // ${secret:...} is NOT a placeholder: it is the engine's secret-reference syntax, which this
        // server must never resolve and must never echo (CLAUDE.md's secret-hygiene invariant). The
        // brace scan must therefore skip a '{' introduced by '$'.
        const string yaml = """
            steps:
              - id: fetch
                type: http.rest
                target: orders-api
                method: GET
                path: /orders/{orderId}
                headers:
                  Authorization: "Bearer ${secret:vault/api-token}"
                  X-Trace: "{traceId}"
            """;

        var summary = Assert.IsType<SuiteSummary>(SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full).Summary);

        Assert.Contains("orderId", summary.Placeholders);
        Assert.Contains("traceId", summary.Placeholders);
        Assert.DoesNotContain(summary.Placeholders, p => p.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(summary.Placeholders, p => p.Contains("vault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyseYaml_CollectsTheReservedPrefixInterpolationForms()
    {
        // {svc::<name>.<field>} and {conn::<name>} are documented engine interpolation forms (see
        // vendored/language-reference.md). Excluding ':' from the placeholder charset silently
        // dropped every one of them — i.e. most suites with an environment block. The ':' is
        // admitted; the '$' guard is what still keeps ${secret:…} out, and '/' still keeps a store
        // path out even if that guard were ever removed.
        const string yaml = """
            steps:
              - id: fetch
                type: http.rest
                target: orders-api
                method: GET
                path: /orders
                headers:
                  X-Endpoint: "{svc::receiver}"
                  X-Conn: "{conn::orders-db}"
                  Authorization: "Bearer ${secret:vault/api-token}"
            """;

        var summary = Assert.IsType<SuiteSummary>(SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full).Summary);

        Assert.Contains("svc::receiver", summary.Placeholders);
        Assert.Contains("conn::orders-db", summary.Placeholders);
        Assert.DoesNotContain(summary.Placeholders, p => p.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(summary.Placeholders, p => p.Contains("vault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnalyseYaml_SecretReferenceUsedAsANAME_IsNeverEchoedIntoTheSummary()
    {
        // The secret-hygiene rule applies to NAMES as well as values. Nothing stops an author
        // calling a capture variable — or a service, dependency, or step type — after a secret
        // reference, and those four lists are built from identifiers taken verbatim out of the
        // document. Echoing one back publishes the caller's secret STORE LAYOUT (source and path) in
        // a tool result, on an otherwise valid:true call. Measured before the fix: the capture name
        // below came back in summary.captures verbatim.
        const string yaml = """
            environment:
              services:
                ${secret:vault/service-name}:
                  image: "example/api:1.0"
              dependencies:
                ${secret:vault/db-name}:
                  type: postgres

            steps:
              - id: fetch
                type: http.rest
                target: orders-api
                method: GET
                path: /orders
                capture:
                  ${secret:vault/prod-db-password}: "$.token"
                  orderId: "$.id"
            """;

        var summary = Assert.IsType<SuiteSummary>(SuiteValidator.AnalyseYaml(yaml, ValidationLevel.Full).Summary);

        // The benign sibling still comes through — the rule drops the offending NAME, not the list.
        Assert.Contains("orderId", summary.Captures);

        foreach (var name in summary.Captures
            .Concat(summary.Services)
            .Concat(summary.Dependencies)
            .Concat(summary.StepTypes)
            .Concat(summary.Placeholders))
        {
            Assert.DoesNotContain("secret", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vault", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnalyseYaml_UnparseableYaml_ReportsTheParseDiagnosticAndNoSummary()
    {
        // No parse means no facts to summarise — a summary invented from a document that was never
        // built would be a fabrication, so the field is simply absent.
        var analysis = SuiteValidator.AnalyseYaml("steps:\n  - id: a\n   type: broken\n", ValidationLevel.Full);

        Assert.False(analysis.Valid);
        Assert.Null(analysis.Summary);
        Assert.Contains(analysis.Errors, e => e.Code == "VFX-D-1102");
    }

    // ── level routing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnalyseYaml_SchemaLevel_StillReportsSchemaViolations()
    {
        var analysis = SuiteValidator.AnalyseYaml(BadSuite, ValidationLevel.Schema);

        Assert.False(analysis.Valid);
        Assert.Contains(analysis.Errors, e => e.Code == "VFX-D-1101" || e.Code == "VFX-D-1201");
    }

    [Fact]
    public void AnalyseYaml_SemanticLevel_SkipsSchemaEvaluationButStillSummarises()
    {
        // level "semantic" runs ONLY the semantic pass — the schema violations the same document
        // carries must not appear. The parse itself is not part of either pass: it is the shared
        // input both consume, so the summary is still produced.
        var analysis = SuiteValidator.AnalyseYaml(BadSuite, ValidationLevel.Semantic);

        Assert.True(analysis.Valid);
        Assert.Empty(analysis.Errors);

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);
        Assert.Equal(2, summary.Steps);
    }

    [Theory]
    [InlineData(ValidationLevel.Schema)]
    [InlineData(ValidationLevel.Semantic)]
    [InlineData(ValidationLevel.Full)]
    public void AnalyseYaml_EveryLevel_ReturnsAnEmptySemanticDiagnosticsChannel(ValidationLevel level)
    {
        // US-S2-02 builds the SEAM; US-S2-03 fills it with rules VFX-D-1201…1211. Until then the
        // channel is present and empty at every level — and it is a channel of its own, never
        // merged into the schema `errors` array.
        var analysis = SuiteValidator.AnalyseYaml(TwoStepSuiteWithCapture, level);

        Assert.Empty(analysis.SemanticDiagnostics);
    }

    /// <summary>
    /// Every YAML-bomb defence against every level: three guards × three levels, enumerated rather
    /// than sampled.
    /// </summary>
    /// <remarks>
    /// The claim is "a level can never switch a guard off", and <see cref="YamlSafetyGuard"/> runs
    /// THREE independent checks in order (size, nesting, anchor/alias). Exercising only the nesting
    /// one proved a third of the claim while reading as though it proved all of it — the size check
    /// short-circuits before the others, so an ordering change could disable a downstream guard
    /// without failing a nesting-only test.
    /// </remarks>
    [Theory]
    [InlineData(ValidationLevel.Schema, BombShape.Size, "VFX-D-1103")]
    [InlineData(ValidationLevel.Semantic, BombShape.Size, "VFX-D-1103")]
    [InlineData(ValidationLevel.Full, BombShape.Size, "VFX-D-1103")]
    [InlineData(ValidationLevel.Schema, BombShape.Nesting, "VFX-D-1104")]
    [InlineData(ValidationLevel.Semantic, BombShape.Nesting, "VFX-D-1104")]
    [InlineData(ValidationLevel.Full, BombShape.Nesting, "VFX-D-1104")]
    [InlineData(ValidationLevel.Schema, BombShape.AnchorAlias, "VFX-D-1105")]
    [InlineData(ValidationLevel.Semantic, BombShape.AnchorAlias, "VFX-D-1105")]
    [InlineData(ValidationLevel.Full, BombShape.AnchorAlias, "VFX-D-1105")]
    public void AnalyseYaml_EveryYamlBombDefence_AppliesAtEveryLevel(
        ValidationLevel level, BombShape shape, string expectedCode)
    {
        var analysis = SuiteValidator.AnalyseYaml(BombFor(shape), level);

        Assert.False(analysis.Valid);
        var error = Assert.Single(analysis.Errors);
        Assert.Equal(expectedCode, error.Code);

        // No document was ever built, so there is nothing to summarise — at any level.
        Assert.Null(analysis.Summary);
    }

    /// <summary>The three inputs <see cref="YamlSafetyGuard"/> rejects, one per guard.</summary>
    public enum BombShape
    {
        /// <summary>Over <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>.</summary>
        Size,

        /// <summary>Nested deeper than <see cref="YamlSafetyGuard.MaxNestingDepth"/>.</summary>
        Nesting,

        /// <summary>More anchors/aliases than the caps allow — the "billion laughs" shape.</summary>
        AnchorAlias,
    }

    private static string BombFor(BombShape shape) => shape switch
    {
        // One byte past the cap, so the size guard rejects it on length alone without parsing.
        BombShape.Size => new string('a', (int)YamlSafetyGuard.MaxSuiteSizeBytes + 1),

        // Flow collections, well inside the size cap but far past the depth cap.
        BombShape.Nesting => new string('[', 20_000) + new string(']', 20_000),

        // The billion-laughs opening: more anchors AND more aliases than either cap allows, in a
        // document that is otherwise tiny — which is the whole point of the attack and the whole
        // reason the guard counts rather than measures.
        BombShape.AnchorAlias => BuildAnchorAliasBomb(),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown bomb shape."),
    };

    private static string BuildAnchorAliasBomb()
    {
        var builder = new System.Text.StringBuilder("steps: []\n");

        for (var i = 0; i <= YamlSafetyGuard.MaxAnchorCount; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"a{i}: &n{i} laugh\n");
        }

        for (var i = 0; i <= YamlSafetyGuard.MaxAliasCount; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"b{i}: *n{i}\n");
        }

        return builder.ToString();
    }

    /// <summary>Two steps: one with an unregistered type, one missing required http.rest fields.</summary>
    private const string BadSuite = """
        steps:
          - id: unknown-step
            type: totally.unknown
            target: something
          - id: incomplete-http
            type: http.rest
            target: orders-api
        """;
}
