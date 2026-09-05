using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Normalization;

/// <summary>
/// US-S2-04's pipeline seam: <c>normalize_suite</c> must produce its <c>validation</c> field from
/// the SAME <see cref="SuiteValidator"/> pipeline <c>validate_suite</c> runs — not a second,
/// parallel one — and from the SAME single parse the canonical YAML is rendered off.
/// </summary>
/// <remarks>
/// <b>The secret gate lives here rather than in <see cref="SuiteNormalizerTests"/>:</b> the
/// normalizer itself has no idea what a secret is, and deliberately so. What US-S2-04's Gherkin
/// actually requires is a property of the COMPOSITION — that a suite carrying a secret literal can
/// never come back with canonical YAML and a silent validation channel — and the only place that
/// composition exists is here.
/// </remarks>
public class SuiteNormalisationPipelineTests
{
    /// <summary>
    /// Spec §4.8's secret shapes, in the exact spellings <c>SecretLiteralRuleTests</c> already
    /// pins — reused rather than re-invented so this corpus cannot drift into testing shapes the
    /// rule does not actually detect.
    /// </summary>
    public static TheoryData<string, string> SecretLiteralCorpus() => new()
    {
        { "aws-access-key-id", "AKIAIOSFODNN7EXAMPLE" },
        { "aws-temporary-key-id", "ASIAIOSFODNN7EXAMPLE" },
        { "pem-private-key", "-----BEGIN RSA PRIVATE KEY-----" },
        { "connection-string-password", "Server=db;Database=orders;User Id=sa;Password=hunter2-not-a-real-one;" },
        { "high-entropy-token", "Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MGFiY2RlZmdoaWprbG1ub3A=" },
    };

    [Theory]
    [MemberData(nameof(SecretLiteralCorpus))]
    public void NormaliseYaml_WithASecretLiteral_AlwaysSurfacesVfxD1207AlongsideTheCanonicalYaml(
        string name, string literal)
    {
        var normalisation = SuiteValidator.NormaliseYaml($"""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /orders
                headers:
                  authorization: "{literal}"
            """,
            ValidationLevel.Full);

        // The story's Gherkin: normalize_suite never returns normalizedYaml for a suite containing a
        // detected secret literal WITHOUT also surfacing the diagnostic. Both halves are asserted —
        // withholding the YAML is not the contract, and neither is emitting it silently.
        Assert.Contains(
            normalisation.Validation.SemanticDiagnostics,
            d => d.Code == "VFX-D-1207");
        Assert.NotNull(normalisation.NormalizedYaml);

        // And the literal is still there, byte-for-byte: normalization is a formatting pass, not a
        // redaction pass. This server is not the redaction authority (CLAUDE.md) — the diagnostic is
        // how the host is told, and silently rewriting the author's value would be a far worse
        // failure than reporting it.
        Assert.Contains(literal, normalisation.NormalizedYaml!, StringComparison.Ordinal);

        // The schema accepts every one of these suites, so the only thing that can have moved the
        // verdict is the semantic finding itself — which is the claim being made.
        Assert.Empty(normalisation.Validation.Errors);

        // Only the three STRUCTURAL shapes are severity "error" and so flip the verdict; the
        // entropy heuristic is a "warning" by design (a guess must not fail a build), and it must
        // still be reported. Asserted per arm rather than blanket-false, so this corpus cannot
        // quietly start claiming the warning arm is an error.
        var finding = Assert.Single(
            normalisation.Validation.SemanticDiagnostics, d => d.Code == "VFX-D-1207");
        Assert.Equal(
            finding.Severity == "error",
            !normalisation.Validation.Valid);
        Assert.True(
            finding.Severity is "error" or "warning",
            $"'{name}' reported VFX-D-1207 at an unexpected severity '{finding.Severity}'.");
    }

    [Fact]
    public void NormaliseYaml_CarriesTheFullValidateSuiteShapedAnalysis()
    {
        // US-S2-04's fourth Gherkin scenario: schema-valid, one unused capture.
        var normalisation = SuiteValidator.NormaliseYaml("""
            metadata:
              name: unused-capture
              owner: platform
              tags: [smoke]
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

        Assert.NotNull(normalisation.NormalizedYaml);
        Assert.Equal(ValidationLevel.Full, normalisation.Validation.Level);
        Assert.NotNull(normalisation.Validation.Summary);
        Assert.Contains("orderId", normalisation.Validation.Summary!.Captures);
        Assert.Contains(normalisation.Validation.SemanticDiagnostics, d => d.Code == "VFX-D-1204");
    }

    [Fact]
    public void NormaliseYaml_ForUnparseableYaml_ReportsTheParseErrorAndNoCanonicalYaml()
    {
        var normalisation = SuiteValidator.NormaliseYaml("steps: [\n  - id: a\n", ValidationLevel.Full);

        Assert.Null(normalisation.NormalizedYaml);
        Assert.False(normalisation.Validation.Valid);
        Assert.NotEmpty(normalisation.Validation.Errors);
    }

    [Fact]
    public void NormaliseYaml_ForASafetyGuardRejection_ReportsTheRejectionAndNoCanonicalYaml()
    {
        // Deep nesting never reaches the normalizer for the same reason it never reaches the schema
        // pass: YamlSafetyGuard runs before any YamlDotNet call at all, and NormaliseYaml gates the
        // canonical render on a document having actually been built.
        var normalisation = SuiteValidator.NormaliseYaml(BuildDeeplyNested(400), ValidationLevel.Full);

        Assert.Null(normalisation.NormalizedYaml);
        Assert.False(normalisation.Validation.Valid);
    }

    /// <summary>
    /// <b>Refusal, not corruption — the whole-document emission gate, seen from the pipeline.</b>
    /// Measured before the gate existed: this exact suite validated <c>valid: true</c> and came back
    /// with a <c>normalizedYaml</c> that does not parse (the alias key emits as <c>*k:</c> and the
    /// nested mapping loses its key), trailing a stray <c>...</c> marker the old local re-parse had
    /// silently kept after discarding its own null result. A host asked to write that text would have
    /// replaced a good suite with garbage.
    /// </summary>
    [Fact]
    public void NormaliseYaml_WhenTheCanonicalTextWouldNotReParse_RefusesItAndSaysWhy()
    {
        var normalisation = SuiteValidator.NormaliseYaml("""
            metadata:
              name: alias-key
              owner: platform
              tags: [smoke]
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                body:
                  anchor: &k v
                  nested:
                    *k : value
            """,
            ValidationLevel.Full);

        // The suite itself is fine — schema-valid, fully analysed. Only this server's emitter could
        // not render it faithfully, which is why the refusal is a payload field and not a VFX code.
        Assert.True(normalisation.Validation.Valid);
        Assert.NotNull(normalisation.Validation.Summary);

        Assert.Null(normalisation.NormalizedYaml);
        Assert.Equal(SuiteNormalization.CanonicalTextDidNotReParse, normalisation.NormalizationRefused);
        Assert.False(normalisation.CommentsDropped);
    }

    /// <summary>
    /// <b>The ordering obligation between the two gates.</b> A self-referential (anchor-cycle)
    /// document is legal YAML and YamlDotNet parses it happily — but converting it to JSON to build
    /// the suite summary does not, and that failure comes FIRST. So the normalizer's own
    /// emit-and-re-parse gate is never reached with a cyclic graph, and this test is what keeps that
    /// ordering from being rearranged by accident.
    /// </summary>
    [Theory]
    [InlineData("a: &x\n  self: *x\nsteps: []\n")]
    [InlineData("steps: &s\n  - *s\n")]
    public void NormaliseYaml_ForASelfReferentialDocument_IsRejectedBeforeTheNormalizerIsEverAsked(string yaml)
    {
        // Anti-vacuity: the cycle really does parse, so what follows is about the pipeline's handling
        // of it rather than about YamlDotNet refusing to build the graph.
        Assert.NotNull(YamlLineResolver.TryParseYamlRoot(yaml));

        var normalisation = SuiteValidator.NormaliseYaml(yaml, ValidationLevel.Full);

        Assert.Null(normalisation.NormalizedYaml);
        Assert.False(normalisation.Validation.Valid);

        // Summary null is the gate that stopped it: the canonical render was never attempted, so
        // there is no emission to have refused.
        Assert.Null(normalisation.Validation.Summary);
        Assert.Null(normalisation.NormalizationRefused);
        Assert.Contains(normalisation.Validation.Errors, e => e.Code == "VFX-D-1102");
    }

    [Fact]
    public void NormaliseYaml_MarksTheCommentLossOnTheResultItself()
    {
        // Outcome (b) of spec open decision #2 requires the loss be documented "in its description
        // AND output". `commentsDropped` is that output half, and it is true on exactly the responses
        // that carry canonical text — because on the pinned library, producing the text and losing
        // the comments are the same act.
        const string Commented = """
            # what this suite is for
            metadata:
              name: commented   # and who owns it
            steps:
              - id: a
                type: http.rest
            """;

        var normalised = SuiteValidator.NormaliseYaml(Commented, ValidationLevel.Full);

        Assert.NotNull(normalised.NormalizedYaml);
        Assert.True(normalised.CommentsDropped);
        Assert.DoesNotContain("#", normalised.NormalizedYaml!, StringComparison.Ordinal);

        // …and the half that loses nothing never claims a loss.
        Assert.False(SuiteNormalization.WithoutCanonicalYaml(normalised.Validation).CommentsDropped);
    }

    [Fact]
    public void AnalyseYaml_IsUnchangedByTheNormalisationOverload()
    {
        const string Yaml = """
            metadata:
              name: parity
            steps:
              - id: a
                type: http.rest
            """;

        var analysis = SuiteValidator.AnalyseYaml(Yaml, ValidationLevel.Full);
        var normalisation = SuiteValidator.NormaliseYaml(Yaml, ValidationLevel.Full);

        // The whole point of threading a flag rather than forking the pipeline: asking for canonical
        // YAML must not perturb a single field of the verdict validate_suite would have returned.
        Assert.Equal(analysis.Valid, normalisation.Validation.Valid);
        Assert.Equal(analysis.Errors.Count, normalisation.Validation.Errors.Count);
        Assert.Equal(
            analysis.SemanticDiagnostics.Select(d => d.Code),
            normalisation.Validation.SemanticDiagnostics.Select(d => d.Code));
        Assert.Equal(analysis.Level, normalisation.Validation.Level);
    }

    private static string BuildDeeplyNested(int depth)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            builder.Append(new string(' ', i * 2)).Append("k:\n");
        }

        builder.Append(new string(' ', depth * 2)).Append("leaf\n");
        return builder.ToString();
    }
}
