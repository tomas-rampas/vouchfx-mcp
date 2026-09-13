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
        // AND output". `commentsDropped` is that output half. Since issue #85 it is a PER-DOCUMENT
        // fact — this suite has comments, so normalizing it really does lose them.
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

    /// <summary>
    /// <b>Issue #85, the defect itself, at the pipeline level.</b> Found by the Sprint 5 M4
    /// acceptance drill: <c>commentsDropped</c> came back <see langword="true"/> for a document
    /// containing ZERO comments. It was derived as <c>NormalizedYaml is not null</c> — a property of
    /// the PIPELINE ("canonical text was produced, and producing it is the act that drops comments")
    /// rather than the per-document fact a reader takes the name for ("YOUR document lost
    /// comments"). Both readings are defensible about the ACT; only one is true about the OUTCOME,
    /// and a suite with nothing to lose loses nothing.
    /// </summary>
    [Fact]
    public void NormaliseYaml_ForADocumentWithNoComments_DoesNotClaimCommentsWereDropped()
    {
        const string CommentFree = """
            metadata:
              name: comment-free
              owner: platform
              tags: [smoke]
            steps:
              - id: a
                type: http.rest
                target: orders-api
                method: GET
                path: /orders
            """;

        // Anti-vacuity: the fixture really has no comment character at all, so a failure below is
        // about the flag rather than about the fixture drifting.
        Assert.DoesNotContain("#", CommentFree, StringComparison.Ordinal);

        var normalised = SuiteValidator.NormaliseYaml(CommentFree, ValidationLevel.Full);

        // Normalization genuinely RAN — this is not the "nothing was produced" case wearing the same
        // answer. That distinction is the whole point: the old derivation would say true here.
        Assert.NotNull(normalised.NormalizedYaml);
        Assert.Null(normalised.NormalizationRefused);

        Assert.False(normalised.CommentsDropped);
    }

    /// <summary>
    /// The eight comment positions below all set the flag: leading, trailing on a line, on its own
    /// line inside a block sequence, after the last line, trailing a key whose value is on the
    /// following line, between documents, inside a flow collection, and on a CRLF line. A fix that
    /// only noticed leading comments would satisfy the headline case and quietly under-report the
    /// rest.
    /// </summary>
    /// <remarks>
    /// <b>Eight cases, not an exhaustive enumeration of YAML.</b> These are the positions this DSL's
    /// suites plausibly use; the detector delegates position-recognising to the tokeniser precisely
    /// so that it does not depend on this list being complete.
    /// <para>
    /// <b>The between-documents case is DETECTOR-ONLY coverage.</b> A multi-document stream never
    /// reaches normalization — <c>YamlLineResolver.TryParseYamlRoot</c> yields a single mapping root
    /// and a suite is one document — so that fixture pins <see cref="SuiteNormalizer.ContainsComment"/>
    /// itself and must not be read as end-to-end evidence about <c>normalize_suite</c>. It earns its
    /// place because the detector is a general text scan whose behaviour on a <c>---</c> boundary
    /// should be known rather than assumed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("leading", "# about this suite\nmetadata:\n  name: c\n  owner: platform\nsteps:\n  - id: a\n    type: http.rest\n")]
    [InlineData("trailing on a line", "metadata:\n  name: c   # who owns it\n  owner: platform\nsteps:\n  - id: a\n    type: http.rest\n")]
    [InlineData("own line inside a sequence", "metadata:\n  name: c\n  owner: platform\nsteps:\n  # the only call\n  - id: a\n    type: http.rest\n")]
    [InlineData("after the last line", "metadata:\n  name: c\n  owner: platform\nsteps:\n  - id: a\n    type: http.rest\n# tail note\n")]
    [InlineData("trailing a key whose value is below", "metadata:\n  name: c\n  owner: platform\nsteps: # the calls\n  - id: a\n    type: http.rest\n")]
    [InlineData("between documents", "metadata:\n  name: c\n  owner: platform\nsteps:\n  - id: a\n    type: http.rest\n---\n# second document\nmetadata:\n  name: d\n")]
    // Inside a FLOW collection, where the comment interrupts a multi-line flow sequence. `tags` is
    // the one flow collection real suites routinely use, so this is the shape most likely to appear.
    [InlineData("inside a flow collection", "metadata:\n  name: c\n  tags: [smoke, # note\n    fast]\n  owner: platform\n")]
    // CRLF: the rest of the validation pipeline is deliberately CRLF-insensitive, and detection must
    // be too — a Windows-authored suite loses comments exactly as a LF one does.
    [InlineData("on a CRLF line", "metadata:\r\n  name: c   # note\r\n  owner: platform\r\n")]
    public void ContainsComment_ForEveryPositionYamlAllowsAComment_IsTrue(string position, string yaml)
    {
        Assert.True(
            SuiteNormalizer.ContainsComment(yaml),
            $"A comment written {position} must be detected. Source:\n{yaml}");
    }

    /// <summary>
    /// <b>The scanner-not-a-string-scan proof.</b> Every case here contains a <c>#</c> that is NOT a
    /// comment — it is scalar CONTENT, and normalization preserves it. A plain text scan for
    /// <c>'#'</c> would call all six of them comments and re-introduce the exact false positive issue
    /// #85 removed, pointing the other way; only the YAML tokeniser can tell them apart.
    /// </summary>
    [Theory]
    [InlineData("inside a double-quoted scalar", "metadata:\n  name: \"a # not a comment\"\n")]
    [InlineData("inside a single-quoted scalar", "metadata:\n  name: 'a # not a comment'\n")]
    [InlineData("unspaced in a plain scalar", "metadata:\n  name: colour#ffaa00\n")]
    [InlineData("a URL fragment", "metadata:\n  name: https://x.test/p#frag\n")]
    [InlineData("a line inside a block scalar", "metadata:\n  name: c\n  note: |\n    # this is content\n    and so is this\n")]
    // The flow-collection counterpart of the positive case above: unspaced inside `[...]` the '#'
    // is part of the plain scalar, not a comment introducer. A naive "is it in a flow collection"
    // heuristic would get this and the positive case backwards.
    [InlineData("unspaced inside a flow collection", "metadata:\n  name: c\n  tags: [a#b]\n")]
    public void ContainsComment_ForAHashThatIsNotAComment_IsFalse(string shape, string yaml)
    {
        // Anti-vacuity: each fixture really does contain the character, so this is a statement about
        // the tokeniser rather than about fixtures that quietly lost their '#'.
        Assert.Contains("#", yaml, StringComparison.Ordinal);

        Assert.False(
            SuiteNormalizer.ContainsComment(yaml),
            $"A '#' {shape} is content, not a comment. Source:\n{yaml}");
    }

    /// <summary>
    /// The end-to-end consequence of the theory above, through the real pipeline rather than the
    /// detector alone: a suite whose only <c>#</c> is inside a quoted value is normalized, keeps that
    /// value byte-for-byte, and reports no loss.
    /// </summary>
    [Fact]
    public void NormaliseYaml_ForAHashInsideAQuotedValue_NormalisesItWithoutClaimingACommentWasLost()
    {
        const string HashInValue = """
            metadata:
              name: hash-in-value
              owner: platform
              tags: [smoke]
            steps:
              - id: a
                type: http.rest
                target: orders-api
                method: GET
                path: "/orders#recent"
            """;

        var normalised = SuiteValidator.NormaliseYaml(HashInValue, ValidationLevel.Full);

        Assert.NotNull(normalised.NormalizedYaml);
        Assert.False(normalised.CommentsDropped);

        // The '#' survived, which is precisely why calling it a comment would have been wrong.
        Assert.Contains("/orders#recent", normalised.NormalizedYaml!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The non-normalized path (<c>normalize</c> absent or false) is unchanged by issue #85: no
    /// canonical text was produced, so nothing was lost, EVEN THOUGH the document has comments. This
    /// is the half that was already correct and must stay that way — the fix added a condition, it
    /// did not remove one.
    /// </summary>
    [Fact]
    public void AnalyseYaml_ForACommentedSuiteWithoutNormalisation_ReportsNoCommentLoss()
    {
        const string Commented = """
            # this suite has comments
            metadata:
              name: commented
              owner: platform
            steps:
              - id: a
                type: http.rest
            """;

        // The detector would say true about this text — so the false below is the AND with
        // NormalizedYaml doing its job, not an accident of a comment-free fixture.
        Assert.True(SuiteNormalizer.ContainsComment(Commented));

        var notNormalised = SuiteNormalization.WithoutCanonicalYaml(
            SuiteValidator.AnalyseYaml(Commented, ValidationLevel.Full));

        Assert.Null(notNormalised.NormalizedYaml);
        Assert.False(notNormalised.CommentsDropped);
    }

    /// <summary>
    /// <b>The AND is structural, not a call-site convention.</b> Half of <c>commentsDropped</c> is
    /// still derived: the stored bit is only "the input had a comment", and the getter re-applies
    /// "…and there is canonical text" on every read. So even a caller that explicitly sets the flag
    /// on a result carrying no text cannot make it claim a loss — which is what keeps the worker's
    /// value from being trusted whole when the parent deserialises it.
    /// </summary>
    [Fact]
    public void CommentsDropped_CannotClaimALossOnAResultThatCarriesNoCanonicalText()
    {
        var analysis = SuiteValidator.AnalyseYaml(
            "metadata:\n  name: a\n  owner: platform\nsteps:\n  - id: a\n    type: http.rest\n",
            ValidationLevel.Full);

        Assert.False(new SuiteNormalization(null, analysis) { CommentsDropped = true }.CommentsDropped);

        var refused = SuiteNormalization.RefusedCanonicalYaml(
            analysis, SuiteNormalization.CanonicalTextDidNotReParse);
        Assert.False((refused with { CommentsDropped = true }).CommentsDropped);

        // …and the same bit DOES survive when there is text, so the assertions above are about the
        // guard rather than about the setter being ignored altogether.
        Assert.True(new SuiteNormalization("metadata: {}\n", analysis) { CommentsDropped = true }.CommentsDropped);
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
