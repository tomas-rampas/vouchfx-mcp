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

    // ── the internal fact set, and the truncation flag ─────────────────────────────────────────
    //
    // These go through SuiteSummaryBuilder.Build directly rather than through AnalyseYaml, because
    // the fact set is the one product of the walk that deliberately does NOT travel on
    // SuiteAnalysis: it is handed to the semantic pass inside the worker and discarded. Build is the
    // only place both halves are observable at once, which is the property under test.

    [Fact]
    public void Build_ASecretNamedCapture_IsAFACTEvenThoughItIsNeverPublished()
    {
        // The seam's whole reason for having a fact set. `summary.captures` drops this name for
        // hygiene — correctly, it would publish the caller's secret store layout — but a US-S2-03
        // rule computing `placeholders \ captures` off that filtered list would then conclude
        // `{${secret:…}}` names nothing, and emit a wrong VFX-D finding on a valid suite. The fact
        // set keeps every name the document really declares; it never leaves the worker process.
        const string yaml = """
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

        using var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        Assert.Contains("${secret:vault/prod-db-password}", digest.Facts.Captures);
        Assert.Contains("orderId", digest.Facts.Captures);

        Assert.DoesNotContain("${secret:vault/prod-db-password}", digest.Summary.Captures);
        Assert.Contains("orderId", digest.Summary.Captures);

        // A name dropped for hygiene is not truncation: that filter applies at every size, so a flag
        // that flipped for it would report "incomplete" on a healthy suite.
        Assert.False(digest.Summary.Truncated);
    }

    [Fact]
    public void Build_CollectsRootVariableNamesIntoTheFactSet_AndNowhereOnTheWire()
    {
        // The composed schema makes root `variables` a first-class name-keyed declaration surface,
        // so "this placeholder names nothing" must be decided against captures ∪ variables. The wire
        // summary has no `variables` field (the spec fixes its shape at six lists), which is exactly
        // why a rule cannot be left to read the summary for this.
        const string yaml = """
            variables:
              region: "eu-west-1"
              tenant: "acme"

            steps:
              - id: fetch
                type: http.rest
                target: orders-api
                method: GET
                path: /orders/{region}
            """;

        using var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        Assert.Equal(["region", "tenant"], digest.Facts.Variables.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains("region", digest.Facts.Placeholders);

        // Nothing published names a variable — the set exists for rules, not for the caller.
        Assert.DoesNotContain(
            "tenant",
            digest.Summary.StepTypes
                .Concat(digest.Summary.Services)
                .Concat(digest.Summary.Dependencies)
                .Concat(digest.Summary.Captures)
                .Concat(digest.Summary.Placeholders));
    }

    [Fact]
    public void Build_MoreNamesThanTheCap_TruncatesTheWireListAndKeepsEveryFact()
    {
        // The second half of the lossy-digest problem: past MaxEntriesPerList the published list
        // silently stops. A rule deciding set membership from it would call every capture past the
        // thousandth undeclared. The fact set is uncapped, and `truncated` tells the CALLER that the
        // list they were given is short.
        const int excess = 5;
        var builder = new System.Text.StringBuilder("""
            steps:
              - id: fetch
                type: http.rest
                target: orders-api
                method: GET
                path: /orders
                capture:

            """);

        for (var i = 0; i < SuiteSummaryBuilder.MaxEntriesPerList + excess; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"      v{i}: \"$.f{i}\"\n");
        }

        using var document = YamlToJsonConverter.Convert(builder.ToString());
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        Assert.Equal(SuiteSummaryBuilder.MaxEntriesPerList, digest.Summary.Captures.Count);
        Assert.True(digest.Summary.Truncated);

        // Completeness, not merely "more than the cap": every declared name is present, including
        // the ones past it, which is what makes the set safe to answer "is this declared?" with.
        Assert.Equal(SuiteSummaryBuilder.MaxEntriesPerList + excess, digest.Facts.Captures.Count);
        Assert.Contains($"v{SuiteSummaryBuilder.MaxEntriesPerList + excess - 1}", digest.Facts.Captures);
        Assert.DoesNotContain($"v{SuiteSummaryBuilder.MaxEntriesPerList + excess - 1}", digest.Summary.Captures);
    }

    [Fact]
    public void Build_AnOrdinarySuite_ReportsTruncatedFalse()
    {
        using var document = YamlToJsonConverter.Convert(TwoStepSuiteWithCapture);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        Assert.False(digest.Summary.Truncated);
    }

    [Fact]
    public void AnalyseYaml_ReportsTruncatedFalseForASuiteWellInsideTheCap()
    {
        // The flag travels on the analysis the tool actually returns, not only on the builder's own
        // output — the caller's copy is the one that matters.
        var summary = Assert.IsType<SuiteSummary>(
            SuiteValidator.AnalyseYaml(TwoStepSuiteWithCapture, ValidationLevel.Full).Summary);

        Assert.False(summary.Truncated);
    }

    // ── per-entry length bound (#72) ───────────────────────────────────────────────────────────
    //
    // The 1 000-entry cap bounds the NUMBER of names a wire list carries; it does nothing about the
    // LENGTH of a single name. An alias-amplified suite whose one step `type` is a multi-MB scalar
    // would otherwise echo that whole string into `summary.stepTypes[0]` verbatim — result-size
    // amplification that slips straight past the entry-count cap. MaxEntryLength bounds each entry;
    // over-long entries are CLIPPED (kept, with a visible ellipsis) so the digest still signals the
    // name's presence, and clipping — like a list cut — sets Truncated because the wire value is no
    // longer byte-faithful to the document. The fact set stays whole: it is the set-membership
    // authority a rule reads, and clipping it would reintroduce the false-negative Truncated warns of.

    [Fact]
    public void Build_AStepTypeLongerThanTheCap_IsClippedOnTheWireButKeptWholeInFacts()
    {
        var longType = new string('a', SuiteSummaryBuilder.MaxEntryLength + 200);
        var yaml = $"""
            steps:
              - id: fetch
                type: "{longType}"
                target: t
            """;

        using var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        // The wire entry is bounded to the cap — never the whole multi-KB string.
        var published = Assert.Single(digest.Summary.StepTypes);
        Assert.True(
            published.Length <= SuiteSummaryBuilder.MaxEntryLength,
            $"Published entry was {published.Length} chars, over the {SuiteSummaryBuilder.MaxEntryLength} cap.");

        // Clipped, not omitted: the prefix survives (so the digest still says a type exists), and a
        // trailing ellipsis marks it as shortened rather than a genuinely cap-length name.
        Assert.NotEqual(longType, published);
        Assert.StartsWith(longType[..(SuiteSummaryBuilder.MaxEntryLength - 1)], published, StringComparison.Ordinal);
        Assert.EndsWith("…", published, StringComparison.Ordinal);

        // A clipped entry makes the digest unfaithful, so the flag is raised — same contract as a
        // list cut: "do not treat this digest as a complete, exact representation".
        Assert.True(digest.Summary.Truncated);

        // The fact set keeps the WHOLE name, uncapped — proving the bound is wire-only.
        Assert.Contains(longType, digest.Facts.StepTypes);
        Assert.DoesNotContain(longType, digest.Summary.StepTypes);
    }

    [Fact]
    public void Build_ANameExactlyAtTheCapLength_IsPublishedWholeAndDoesNotTruncate()
    {
        // The boundary: a name whose length is exactly the cap is NOT over-long, so it is published
        // verbatim (no ellipsis) and does not raise Truncated. This is what keeps a genuinely
        // cap-length legitimate name distinguishable from a clipped one — the marker only ever
        // appears on a name that was actually shortened.
        var atCap = new string('a', SuiteSummaryBuilder.MaxEntryLength);
        var yaml = $"""
            steps:
              - id: fetch
                type: "{atCap}"
                target: t
            """;

        using var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        Assert.Equal([atCap], digest.Summary.StepTypes);
        Assert.False(digest.Summary.Truncated);
    }

    [Fact]
    public void Build_AnAstralCharStraddlingTheClipBoundary_IsClippedWithoutASplitSurrogatePair()
    {
        // MINOR-1: the clip prefix is taken by UTF-16 code UNIT (AsSpan(0, MaxEntryLength - 1)). If an
        // astral character — one encoded as a surrogate PAIR, e.g. U+1F600 😀 = D83D DE00 — straddles
        // the last prefix unit and the one past it, a naive cut ends the prefix on a LONE high
        // surrogate. That is invalid UTF-16 that only survives serialisation because System.Text.Json
        // silently substitutes U+FFFD for it. The rune-aware clip backs the prefix off one unit so a
        // pair is never split. Positioned so the astral char occupies indices MaxEntryLength-2 /
        // MaxEntryLength-1: with prefixLen = MaxEntryLength - 1, the char at prefixLen-1 is the pair's
        // HIGH surrogate, which is exactly the boundary the backoff exists for.
        var name = new string('a', SuiteSummaryBuilder.MaxEntryLength - 2) + "\U0001F600" + new string('b', 50);
        var yaml = $"""
            steps:
              - id: fetch
                type: "{name}"
                target: t
            """;

        using var document = YamlToJsonConverter.Convert(yaml);
        var digest = SuiteSummaryBuilder.Build(document.RootElement);

        var published = Assert.Single(digest.Summary.StepTypes);

        // Still bounded, still flagged, still marked as clipped — the boundary case does not weaken
        // any of the clip's other guarantees.
        Assert.True(published.Length <= SuiteSummaryBuilder.MaxEntryLength);
        Assert.True(digest.Summary.Truncated);
        Assert.EndsWith("…", published, StringComparison.Ordinal);

        // The load-bearing assertion: the last character before the ellipsis marker is NOT a lone
        // high surrogate — the pair was dropped whole, not split.
        var lastContentChar = published[^2];
        Assert.False(
            char.IsHighSurrogate(lastContentChar),
            "The clipped prefix ended on a lone high surrogate — the astral pair was split.");

        // And the value is well-formed UTF-16: a System.Text.Json round trip introduces no U+FFFD
        // replacement character, which is what a split pair would have forced.
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<string>(
            System.Text.Json.JsonSerializer.Serialize(published));
        Assert.Equal(published, roundTripped);
        Assert.DoesNotContain('�', published);
    }

    [Fact]
    public void AnalyseYaml_AnOverLongStepType_ComesBackBoundedAndFlaggedThroughTheAnalysis()
    {
        // The bound holds through the in-process analysis entry point, not only through
        // SuiteSummaryBuilder called directly: AnalyseYaml is what validate_suite's pipeline runs, and
        // an over-long entry stays bounded across it. This path is PURE in-process — no JsonSerializer
        // and no worker process boundary is crossed here (that end-to-end round trip is pinned by
        // RealValidationWorkerProcessTests instead). The type is sized over the entry cap (128) but
        // well under YamlSafetyGuard's per-line guard (512), so it is the 128-char ENTRY cap — not the
        // line-length check — that shortens it.
        var longType = new string('a', SuiteSummaryBuilder.MaxEntryLength + 200);
        var analysis = SuiteValidator.AnalyseYaml($"""
            steps:
              - id: fetch
                type: "{longType}"
                target: t
            """, ValidationLevel.Full);

        var summary = Assert.IsType<SuiteSummary>(analysis.Summary);
        var published = Assert.Single(summary.StepTypes);
        Assert.True(published.Length <= SuiteSummaryBuilder.MaxEntryLength);
        Assert.EndsWith("…", published, StringComparison.Ordinal);
        Assert.True(summary.Truncated);
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
    public void AnalyseYaml_EveryLevel_KeepsTheSemanticChannelSeparateFromTheSchemaOne(ValidationLevel level)
    {
        // US-S2-02 built the SEAM and this test asserted the channel was empty at every level;
        // US-S2-03 filled it, so what is left to assert here is the property that outlives the
        // rules: the semantic channel is a channel OF ITS OWN, never merged into the schema
        // `errors` array, and it is empty at ValidationLevel.Schema because that level runs no
        // rules — not because there is nothing to say about this document.
        var analysis = SuiteValidator.AnalyseYaml(TwoStepSuiteWithCapture, level);

        if (level == ValidationLevel.Schema)
        {
            Assert.Empty(analysis.SemanticDiagnostics);
        }
        else
        {
            Assert.NotEmpty(analysis.SemanticDiagnostics);
        }

        // Whatever each channel carries, no code appears in both — with the single, adjudicated
        // exception of VFX-D-1201, which both channels render from one shared detector (see
        // Validation/Semantics/UnknownStepTypeRule's remarks). This fixture has no unknown types,
        // so here the two sets are strictly disjoint.
        var schemaCodes = analysis.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var semanticCodes = analysis.SemanticDiagnostics.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(schemaCodes.Intersect(semanticCodes, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every YAML-bomb defence against every level: four guards × three levels, enumerated rather
    /// than sampled.
    /// </summary>
    /// <remarks>
    /// The claim is "a level can never switch a guard off", and <see cref="YamlSafetyGuard"/> runs
    /// FOUR independent checks in order (size, per-line length, nesting, anchor/alias). Exercising
    /// only the nesting one proved a quarter of the claim while reading as though it proved all of
    /// it — the size and line-length checks short-circuit before the others, so an ordering change
    /// could disable a downstream guard without failing a nesting-only test.
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
    [InlineData(ValidationLevel.Schema, BombShape.LineLength, "VFX-D-1107")]
    [InlineData(ValidationLevel.Semantic, BombShape.LineLength, "VFX-D-1107")]
    [InlineData(ValidationLevel.Full, BombShape.LineLength, "VFX-D-1107")]
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

        /// <summary>A single line longer than <see cref="YamlSafetyGuard.MaxLineLength"/> — issue #71.</summary>
        LineLength,
    }

    private static string BombFor(BombShape shape) => shape switch
    {
        // One byte past the cap, so the size guard rejects it on length alone without parsing.
        BombShape.Size => new string('a', (int)YamlSafetyGuard.MaxSuiteSizeBytes + 1),

        // Flow collections, well inside the size cap but far past the depth cap. NEWLINE-SEPARATED
        // brackets (one per line), NOT one 40,000-char line: a newline in flow context is just
        // whitespace, so this nests exactly 20,000 deep, but every line stays a single character —
        // well under the per-line cap — so it flows past the line-length guard and reaches the
        // nesting guard this row is about, rather than being short-circuited as VFX-D-1107 (#71).
        BombShape.Nesting =>
            string.Concat(Enumerable.Repeat("[\n", 20_000)) + string.Concat(Enumerable.Repeat("]\n", 20_000)),

        // The billion-laughs opening: more anchors AND more aliases than either cap allows, in a
        // document that is otherwise tiny — which is the whole point of the attack and the whole
        // reason the guard counts rather than measures.
        BombShape.AnchorAlias => BuildAnchorAliasBomb(),

        // A single line one character past the per-line cap — the over-long-mapping-key shape from
        // issue #71, minimised to the guard's boundary. Well inside the size cap, no nesting, no
        // anchors: only the line-length guard rejects it.
        BombShape.LineLength => new string('a', YamlSafetyGuard.MaxLineLength + 1),

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
