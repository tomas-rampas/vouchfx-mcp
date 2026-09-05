using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Validation;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests.Normalization;

/// <summary>
/// US-S2-04's canonical-form rules, and the sprint exit checklist's idempotence requirement
/// (<c>normalize(normalize(x)) == normalize(x)</c>) across the whole fixture corpus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two properties are asserted over the SAME corpus, and both are load-bearing.</b> Idempotence
/// alone is satisfied by a normalizer that destroys the document identically every time; meaning
/// preservation alone is satisfied by one that does nothing useful. Together they say "this
/// transformation is a stable formatting choice over an unchanged document" — which is the only
/// claim that makes it safe for a host to write the result over the author's file.
/// </para>
/// <para>
/// <b>Meaning preservation is asserted on the SCALARS, not by <see cref="YamlNode"/> equality, and
/// the difference is measured rather than theoretical.</b> <c>YamlNode.Equals</c> ignores scalar
/// STYLE (measured on the pinned library: a plain <c>plain</c> and a double-quoted <c>"plain"</c>
/// compare EQUAL, while a tagged <c>!!str x</c> and an untagged <c>x</c> do not). So node equality
/// cannot see the one class of change that would silently retype a value — <c>"007"</c> becoming
/// plain <c>007</c>, the integer 7. This class therefore walks both documents and compares
/// <c>(Value, Tag)</c> pair by pair, and separately holds every style transition to the canonical
/// form's own rule, with the quoted→plain direction forbidden outright.
/// </para>
/// <para>
/// <b>The runtime emission gate uses node equality on purpose, and that is not a contradiction.</b>
/// <see cref="SuiteNormalizer"/> compares the re-parsed output with an untouched snapshot to prove it
/// did not corrupt the document, and there it WANTS the style blindness — re-quoting is a change it
/// is allowed to make. This class asserts the strictly stronger property the gate is not the right
/// place for.
/// </para>
/// </remarks>
public class SuiteNormalizerTests
{
    /// <summary>
    /// The fixture corpus both corpus-wide properties run over. Deliberately spans every YAML
    /// construct the normalizer makes a decision about — key ordering, all four scalar styles, flow
    /// and block collections, empty collections, anchors/aliases and a merge key, literal and folded
    /// block scalars, non-ASCII text, non-BMP text the emitter will not write plain, tagged empty
    /// scalars the emitter will not write plain either, a scalar long enough that the emitter would
    /// fold it at its default width, and the numeric/boolean look-alikes whose quoting carries their
    /// TYPE.
    /// </summary>
    public static TheoryData<string, string> Corpus()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, yaml) in CorpusEntries)
        {
            data.Add(name, yaml);
        }

        return data;
    }

    private static readonly (string Name, string Yaml)[] CorpusEntries =
    [
        ("keys-out-of-schema-order", """
            steps:
              - with:
                  url: http://api/health
                type: http.rest
                id: check
            environment:
              services:
                api:
                  image: api:1
            metadata:
              tags: [smoke]
              name: checkout
              owner: platform
            """),

        ("quoting-variants", """
            metadata:
              name: 'single quoted'
              owner: "double quoted"
              description: plain unquoted
            steps:
              - id: a
                type: http.rest
            """),

        ("numeric-and-boolean-lookalikes", """
            metadata:
              name: "007"
              owner: 'yes'
              description: 'on'
            steps:
              - id: a
                type: http.rest
                timeout: 30
                continueOnFailure: true
            """),

        ("anchors-aliases-and-a-merge-key", """
            defaults: &defaults
              verifyMode: RETRY
              timeout: 30
            steps:
              - id: a
                type: http.rest
                <<: *defaults
              - id: b
                type: http.rest
                <<: *defaults
            """),

        ("literal-and-folded-block-scalars", """
            metadata:
              name: blocks
              description: |
                first line
                second line
            steps:
              - id: a
                type: http.rest
                with:
                  body: >
                    folded one
                    folded two
            """),

        ("unicode", """
            metadata:
              name: "café ☕ — 注文"
              owner: Ünïcödé
            steps:
              - id: a
                type: http.rest
            """),

        // Measured: YamlDotNet's emitter will not write a plain scalar containing non-BMP text — it
        // comes back double-quoted with \U escapes. The VALUE is unchanged, which is what the meaning
        // property checks; this entry is here so that stays true, and stays idempotent, by test.
        ("non-bmp-text-the-emitter-will-not-write-plain", """
            metadata:
              name: shipped 🚀 today
              owner: "🙂 quoted already"
            steps:
              - id: a
                type: http.rest
            """),

        // The other measured escalation: an EMPTY scalar carrying an explicit tag. The emitter must
        // write something after the tag and picks '' — a single-quoted scalar, which the canonical
        // quoting rule would rewrite on the NEXT pass, breaking byte idempotence unless pass 1
        // already double-quotes it. Three shapes: the standard tag, a local tag, and a tag carrying
        // an anchor.
        ("tagged-empty-scalars", """
            metadata:
              name: !!str
              owner: !Custom
              description: !!str &tagged-anchor
            steps:
              - id: a
                type: http.rest
            """),

        ("flow-and-empty-collections", """
            metadata:
              tags: [smoke, fast]
            environment:
              services: {}
              dependencies: {}
            steps: []
            """),

        ("keys-the-schema-does-not-declare", """
            steps:
              - id: a
                type: http.rest
                with:
                  zebra: 1
                  alpha: 2
                  method: GET
                  mango: 3
            """),

        ("a-scalar-long-enough-to-fold-at-the-default-width", """
            metadata:
              description: this description is comfortably longer than the eighty columns YamlDotNet's emitter folds a plain scalar at when its width is left at the default
            steps:
              - id: a
                type: http.rest
            """),

        ("deeply-nested-mappings", """
            steps:
              - id: a
                type: http.rest
                with:
                  headers:
                    outer:
                      middle:
                        inner:
                          leaf: value
            """),

        ("a-secret-literal", """
            steps:
              - id: a
                type: http.rest
                with:
                  headers:
                    authorization: AKIAIOSFODNN7EXAMPLE
            """),

        ("an-already-canonical-document", """
            metadata:
              name: canonical
              owner: platform
            steps:
              - id: a
                type: http.rest
            """),
    ];

    // ── The corpus-wide properties (the sprint exit-checklist items) ────────────────────────────

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Normalise_IsIdempotent_AcrossTheWholeFixtureCorpus(string name, string yaml)
    {
        var once = Normalise(yaml);
        Assert.NotNull(once);

        var twice = Normalise(once!);

        // Byte-identical, not merely equivalent: the sprint exit checklist's own wording, and the
        // only version of this property a host can rely on when deciding whether re-running
        // normalize_suite would dirty a file it already wrote.
        Assert.True(
            string.Equals(once, twice, StringComparison.Ordinal),
            $"Corpus entry '{name}' is not idempotent.\n--- first pass ---\n{once}\n--- second pass ---\n{twice}");
    }

    /// <summary>
    /// <b>The runtime invariant, asserted here as a property.</b> This is not only a fixture check:
    /// <see cref="SuiteNormalizer"/> enforces the same claim on EVERY call, at runtime, by re-parsing
    /// the text it emitted and comparing it with an untouched snapshot of the input before returning
    /// it. What the corpus adds is strength — the gate uses node equality (blind to style, which it
    /// must be, because re-quoting is legitimate); this walks the scalars and holds the style
    /// transitions to the canonical rule as well.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Normalise_PreservesTheDocumentsMeaning_AcrossTheWholeFixtureCorpus(string name, string yaml)
    {
        var normalized = Normalise(yaml);
        Assert.NotNull(normalized);

        var before = YamlLineResolver.TryParseYamlRoot(yaml);
        var after = YamlLineResolver.TryParseYamlRoot(normalized!);

        Assert.NotNull(before);
        Assert.NotNull(after);

        // Document order on both sides would compare a REORDERED mapping against its source and fail
        // for the very change this normalizer exists to make. Sorting each side's scalars by
        // (Tag, Style-independent Value) makes the comparison order-insensitive while staying exact
        // about the values and tags themselves.
        var beforeScalars = ScalarsOf(before!).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();
        var afterScalars = ScalarsOf(after!).OrderBy(s => s.Key, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            beforeScalars.Select(s => s.Key),
            afterScalars.Select(s => s.Key));

        // The style rule, direction by direction. Everything here is about the ONE boundary that
        // carries a value's resolved type.
        foreach (var (key, style, _) in afterScalars)
        {
            var beforeStyle = beforeScalars.First(s => s.Key == key).Style;

            Assert.True(
                style != ScalarStyle.Plain || beforeStyle == ScalarStyle.Plain,
                $"Corpus entry '{name}': scalar {key} went from {beforeStyle} to Plain. Unquoting is "
                + "the one style change that can retype a value ('\"007\"' plain is the integer 7) "
                + "and the canonical form never makes it.");

            Assert.True(
                beforeStyle != ScalarStyle.SingleQuoted || style == ScalarStyle.DoubleQuoted,
                $"Corpus entry '{name}': scalar {key} was SingleQuoted and came back {style}; the "
                + "canonical quoting rule rewrites it double-quoted.");
        }

        // A plain scalar is allowed to come back quoted ONLY because the emitter refused to write it
        // plain. That is checked against the emitter itself rather than against a copy of the
        // normalizer's own predicate — asking the library the same question the production rule asks
        // it, so the two cannot drift into agreeing with each other and both being wrong.
        foreach (var (key, style, _) in afterScalars.Where(s => s.Style != ScalarStyle.Plain))
        {
            var source = beforeScalars.First(s => s.Key == key);
            if (source.Style != ScalarStyle.Plain)
            {
                continue;
            }

            Assert.False(
                TheEmitterCanWriteThisPlain(source.Node),
                $"Corpus entry '{name}': scalar {key} was Plain and came back {style}, but the "
                + "emitter is willing to write it plain — so this is an unforced retype, not an "
                + "escalation.");
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Normalise_AlwaysEmitsLfLineEndingsAndExactlyOneTrailingNewline(string name, string yaml)
    {
        var normalized = Normalise(yaml);
        Assert.NotNull(normalized);

        Assert.DoesNotContain('\r', normalized!);
        Assert.EndsWith("\n", normalized, StringComparison.Ordinal);
        Assert.False(
            normalized!.EndsWith("\n\n", StringComparison.Ordinal),
            $"Corpus entry '{name}' ends with more than one newline.");
    }

    // ── Rule-by-rule coverage of the canonical form ─────────────────────────────────────────────

    [Fact]
    public void Normalise_OrdersAMappingsKeysByTheVendoredSchemasDeclaredOrder()
    {
        var normalized = Normalise("""
            steps:
              - id: a
                type: http.rest
            variables:
              region: eu
            environment:
              services: {}
            metadata:
              name: n
            """);

        // The composed schema's root `properties` declares metadata, environment, variables, steps —
        // in that order. Nothing about the SOURCE order survives.
        Assert.Equal(
            ["metadata", "environment", "variables", "steps"],
            TopLevelKeys(normalized!));
    }

    [Fact]
    public void Normalise_OrdersAStepsKeysByTheStepDefinitionsDeclaredOrder()
    {
        var normalized = Normalise("""
            steps:
              - continueOnFailure: false
                timeout: 30
                type: http.rest
                id: check
                capture: token
            """);

        // $defs/step declares id, type, description, capture, verifyMode, timeout, continueOnFailure.
        Assert.Equal(
            ["id", "type", "capture", "timeout", "continueOnFailure"],
            KeysOfFirstStep(normalized!));
    }

    [Fact]
    public void Normalise_LeavesKeysTheSchemaDoesNotDeclareInTheirSourceOrder()
    {
        var normalized = Normalise("""
            steps:
              - id: a
                type: http.rest
                with:
                  zebra: 1
                  alpha: 2
                  mango: 3
            """);

        // NOT alphabetised: `with`'s contents are the author's own data (headers, query parameters,
        // an arbitrary request body), and the schema has no opinion about their order, so neither
        // does this normalizer. Sorting them would scramble authored data for no canonical gain.
        var with = (YamlMappingNode)FirstStep(normalized!).Children[new YamlScalarNode("with")];
        Assert.Equal(["zebra", "alpha", "mango"], with.Children.Keys.Select(k => k.ToString()));
    }

    [Fact]
    public void Normalise_PutsSchemaDeclaredKeysBeforeUndeclaredOnes()
    {
        var normalized = Normalise("""
            steps:
              - unknownOne: 1
                type: http.rest
                unknownTwo: 2
                capture:
                  token: "$.t"
                id: a
            """);

        // Three of five keys are step fields — a strong majority — so the step definition's declared
        // order applies and the two it does not declare follow, in source order.
        Assert.Equal(
            ["id", "type", "capture", "unknownOne", "unknownTwo"],
            KeysOfFirstStep(normalized!));
    }

    [Fact]
    public void Normalise_LeavesAMappingAlone_WhenExactlyHalfItsKeysAreSchemaDeclared()
    {
        // The strong-majority guard is STRICTLY more than half, and this is the boundary that makes
        // that a decision rather than an accident. A mapping the schema describes only half of is a
        // coin toss about whose data it is, and the wrong call rewrites an author's content — so the
        // tie goes to leaving it alone.
        var normalized = Normalise("""
            steps:
              - unknownOne: 1
                type: http.rest
                unknownTwo: 2
                id: a
            """);

        Assert.Equal(["unknownOne", "type", "unknownTwo", "id"], KeysOfFirstStep(normalized!));
    }

    // ── The author-data guards (the measured key-order defect) ──────────────────────────────────

    /// <summary>
    /// Every one of these was MEASURED being rewritten before <see cref="CanonicalKeyOrder"/> grew
    /// its two guards, on suites the schema accepts. The keys collide with schema field names by
    /// coincidence — a header called <c>id</c>, a service called <c>image</c>, a JSON body property
    /// called <c>target</c> — and the mapping was reordered as though the schema described it.
    /// Reordering a request's headers is a change to the author's data; reordering a JSON body can
    /// change what the request means to the server receiving it.
    /// </summary>
    public static TheoryData<string, string, string[]> AuthorDataCollisions() => new()
    {
        // Free-form containers the schema itself declares (guard 2).
        {
            "headers",
            """
            steps:
              - id: call
                type: http.rest
                headers:
                  zebra: "1"
                  id: "2"
                  alpha: "3"
                  type: "4"
                  name: "5"
            """,
            ["zebra", "id", "alpha", "type", "name"]
        },
        {
            "body",
            """
            steps:
              - id: call
                type: http.rest
                body:
                  zebra: 1
                  target: 2
                  alpha: 3
            """,
            ["zebra", "target", "alpha"]
        },
        {
            "variables",
            """
            variables:
              zebra: "1"
              name: "2"
              alpha: "3"
            steps:
              - id: a
                type: http.rest
            """,
            ["zebra", "name", "alpha"]
        },
        {
            "capture",
            """
            steps:
              - id: call
                type: http.rest
                capture:
                  zebra: "$.a"
                  path: "$.b"
                  alpha: "$.c"
            """,
            ["zebra", "path", "alpha"]
        },
        // NOT a declared free-form container — `expect` is shaped in some step branches and
        // unconstrained in others, so it is the strong-majority test (guard 1) that saves it here:
        // one shared key out of three is not a description of this mapping.
        {
            "expect",
            """
            steps:
              - id: call
                type: http.rest
                expect:
                  zebra: 1
                  status: 200
                  alpha: 3
            """,
            ["zebra", "status", "alpha"]
        },
    };

    [Theory]
    [MemberData(nameof(AuthorDataCollisions))]
    public void Normalise_NeverReordersAnAuthorDataMappingWhoseKeysCollideWithSchemaFieldNames(
        string container, string yaml, string[] expectedSourceOrder)
    {
        var normalized = Normalise(yaml);
        Assert.NotNull(normalized);

        Assert.Equal(expectedSourceOrder, KeysUnder(normalized!, container));
    }

    [Fact]
    public void Normalise_LeavesAServicesMapAloneEvenWhenAServiceIsNamedAfterASchemaField()
    {
        // Measured: a service literally called `image` used to be hoisted to the front of
        // environment.services, because `image` is a field of the service definition one level down.
        var normalized = Normalise("""
            environment:
              services:
                zebra:
                  image: z:1
                image:
                  image: i:1
                alpha:
                  image: a:1
            steps:
              - id: a
                type: http.rest
            """);

        Assert.Equal(["zebra", "image", "alpha"], KeysUnder(normalized!, "services"));
    }

    [Fact]
    public void Normalise_StillOrdersMappingsAStrongMajorityOfWhoseKeysTheSchemaDeclares()
    {
        // The positive control for both guards: they must not have turned canonical ordering off.
        // The step below matches the http.rest branch on 3 of its 5 keys and is reordered; the root
        // matches on 2 of 2.
        var normalized = Normalise("""
            steps:
              - path: /orders
                method: GET
                type: http.rest
                target: orders-api
                id: call
            metadata:
              tags: [smoke]
              name: checkout
              owner: platform
            """);

        Assert.Equal(["metadata", "steps"], TopLevelKeys(normalized!));
        Assert.Equal(["id", "type", "target", "method", "path"], KeysOfFirstStep(normalized!));
        Assert.Equal(["name", "owner", "tags"], KeysUnder(normalized!, "metadata"));
    }

    // ── Sequences, quoting, layout, anchors ─────────────────────────────────────────────────────

    [Fact]
    public void Normalise_NeverReordersASequence()
    {
        var normalized = Normalise("""
            steps:
              - id: zulu
                type: http.rest
              - id: alpha
                type: http.rest
            """);

        var steps = (YamlSequenceNode)Root(normalized!).Children[new YamlScalarNode("steps")];
        Assert.Equal(
            ["zulu", "alpha"],
            steps.Children.Select(s => ((YamlMappingNode)s).Children[new YamlScalarNode("id")].ToString()));
    }

    [Fact]
    public void Normalise_RewritesSingleQuotedScalarsAsDoubleQuoted()
    {
        var normalized = Normalise("""
            metadata:
              name: 'single'
            steps: []
            """);

        Assert.Contains("name: \"single\"", normalized!, StringComparison.Ordinal);
    }

    [Theory]
    // Each of these resolves to a NON-string when written plain, and to a string when quoted. The
    // normalizer must never move a scalar across that boundary in either direction — which is why
    // the quoting rule normalises only the CHOICE of quote character, never quoted-vs-plain.
    [InlineData("'yes'", "\"yes\"")]
    [InlineData("'007'", "\"007\"")]
    [InlineData("\"3.14\"", "\"3.14\"")]
    [InlineData("plainword", "plainword")]
    [InlineData("30", "30")]
    [InlineData("true", "true")]
    public void Normalise_NeverMovesAScalarAcrossTheQuotedUnquotedBoundary(string written, string expected)
    {
        var normalized = Normalise($"""
            metadata:
              name: {written}
            steps: []
            """);

        Assert.Contains($"name: {expected}", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_DoubleQuotesATaggedEmptyScalarSoTheFirstPassIsAlreadyTheFixpoint()
    {
        // Measured on the pinned library: left alone, `!!str` (a plain EMPTY scalar with an explicit
        // tag) is emitted as `!!str ''`, which the single→double rule would rewrite on pass 2 —
        // idempotence broken by the emitter rather than by this type's own rules.
        var normalized = Normalise("""
            metadata:
              name: !!str
            steps: []
            """);

        Assert.Contains("name: !!str \"\"", normalized!, StringComparison.Ordinal);
        Assert.DoesNotContain("''", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_LeavesAnUntaggedEmptyScalarPlain_BecauseQuotingItWouldRetypeNullToTheEmptyString()
    {
        var normalized = Normalise("""
            metadata:
              name:
            steps: []
            """);

        // The emitter writes `name: ` — key, colon, and the empty plain scalar that is the implicit
        // null. Asserted with the trailing space it actually produces rather than the one it would be
        // tidier to produce.
        Assert.Contains("name: \n", normalized!, StringComparison.Ordinal);
        Assert.DoesNotContain("name: \"\"", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_ConvertsNonEmptyFlowCollectionsToBlockStyle()
    {
        var normalized = Normalise("""
            metadata:
              tags: [smoke, fast]
            steps: []
            """);

        Assert.Contains("tags:\n    - smoke\n    - fast\n", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_LeavesEmptyCollectionsInTheirCompactFlowForm()
    {
        var normalized = Normalise("""
            environment:
              services: {}
            steps: []
            """);

        Assert.Contains("services: {}", normalized!, StringComparison.Ordinal);
        Assert.Contains("steps: []", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_IndentsBlockSequencesUnderTheirKey()
    {
        var normalized = Normalise("""
            steps:
              - id: a
                type: http.rest
            """);

        Assert.Contains("steps:\n  - id: a\n", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_PreservesAnchorsAndAliasesRatherThanExpandingThem()
    {
        var normalized = Normalise("""
            defaults: &defaults
              verifyMode: RETRY
            steps:
              - id: a
                type: http.rest
                <<: *defaults
            """);

        Assert.Contains("&defaults", normalized!, StringComparison.Ordinal);
        Assert.Contains("*defaults", normalized!, StringComparison.Ordinal);

        // Exactly one anchored definition: an expansion would have written verifyMode twice.
        Assert.Equal(1, CountOccurrences(normalized!, "verifyMode"));
    }

    [Fact]
    public void Normalise_MovesTheAnchorDefinitionWithItsNodeWhenReorderingChangesWhichKeyComesFirst()
    {
        // An anchor belongs to a NODE, not to a key. Measured: when reordering moves the aliased
        // node's first emission, the `&name` definition moves with it and the `*name` reference
        // follows. The graph is identical — but a host diffing the text sees the anchor on a
        // different line, so this is pinned rather than left to be rediscovered.
        var normalized = Normalise("""
            steps:
              - unknownLater: &shared value
                type: http.rest
                id: *shared
            """);

        Assert.NotNull(normalized);
        Assert.Equal(["id", "type", "unknownLater"], KeysOfFirstStep(normalized!));

        // `id` is now emitted first, so it carries the definition and the later key carries the alias.
        Assert.Contains("id: &shared value", normalized!, StringComparison.Ordinal);
        Assert.Contains("unknownLater: *shared", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_NeverFoldsALongScalarOntoASecondLine()
    {
        const string LongValue =
            "this description is comfortably longer than the eighty columns YamlDotNet's emitter " +
            "folds a plain scalar at when its width is left at the default";

        var normalized = Normalise($"""
            metadata:
              description: {LongValue}
            steps: []
            """);

        Assert.Contains($"description: {LongValue}\n", normalized!, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_EmitsNoExplicitDocumentEndMarker()
    {
        var normalized = Normalise("""
            steps:
              - id: a
                type: http.rest
            """);

        // YamlStream.Save writes an explicit "..." terminator. It is valid YAML and harmless, but a
        // suite file the host writes back should look like every other suite file in the repo.
        Assert.DoesNotContain("\n...", normalized!, StringComparison.Ordinal);
    }

    // ── The emission gate ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shape that made this gate necessary. Measured before it existed: an alias used as a
    /// mapping KEY was emitted as <c>*k:</c>, the emitted text did not parse at all, and it was
    /// returned to the host as <c>normalizedYaml</c> beside a <c>valid: true</c> verdict — an
    /// invitation to overwrite a good file with garbage.
    /// </summary>
    [Fact]
    public void Normalise_WhenTheEmittedTextWouldNotReParse_RefusesInsteadOfReturningCorruption()
    {
        const string AliasAsKey = """
            metadata:
              name: alias-key
            steps:
              - id: call
                type: http.rest
                body:
                  anchor: &k v
                  nested:
                    *k : value
            """;

        // Anti-vacuity: the input itself is perfectly parseable, so the refusal below is about the
        // OUTPUT, not about a suite that never parsed.
        Assert.NotNull(YamlLineResolver.TryParseYamlRoot(AliasAsKey));

        var normalized = SuiteNormalizer.NormaliseText(AliasAsKey, out var refusedReason);

        Assert.Null(normalized);
        Assert.Equal(SuiteNormalization.CanonicalTextDidNotReParse, refusedReason);
    }

    [Fact]
    public void Normalise_ForAnOrdinaryDocument_ReportsNoRefusal()
    {
        var normalized = SuiteNormalizer.NormaliseText("""
            metadata:
              name: fine
            steps:
              - id: a
                type: http.rest
            """,
            out var refusedReason);

        Assert.NotNull(normalized);
        Assert.Null(refusedReason);
    }

    [Fact]
    public void Normalise_ReturnsNullWhenThereIsNoMappingRootToNormalise()
    {
        // A document whose root is a SEQUENCE is not a suite: there is nothing meaningful to
        // canonicalise, and the validation channel is where that is explained. Not a refusal —
        // nothing was emitted for the gate to reject.
        var normalized = SuiteNormalizer.NormaliseText("- a\n- b\n", out var refusedReason);

        Assert.Null(normalized);
        Assert.Null(refusedReason);
    }

    [Fact]
    public void Normalise_ReturnsNullForANullRoot()
    {
        Assert.Null(SuiteNormalizer.Normalise(null, null, out var refusedReason));
        Assert.Null(refusedReason);
    }

    // ── The comment-preservation decision checkpoint (spec open decision #2, outcome (b)) ───────

    /// <summary>
    /// The fixture that RECORDS the closed decision: on the pinned YamlDotNet, normalization drops
    /// comments. See <see cref="SuiteNormalizer"/>'s own remarks for the measured evidence behind
    /// choosing outcome (b) over (a), and <c>NormalizeSuiteTool</c> for the opt-in that fact forced.
    /// </summary>
    [Fact]
    public void Normalise_DropsComments_WhichIsWhyNormalisationIsOptIn()
    {
        var normalized = Normalise("""
            # a leading file comment
            metadata:
              name: commented   # a trailing comment
            # a comment before steps
            steps:
              - id: a
                type: http.rest
            """);

        Assert.DoesNotContain("#", normalized!, StringComparison.Ordinal);
        Assert.Contains("name: commented", normalized!, StringComparison.Ordinal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string? Normalise(string yaml) => SuiteNormalizer.NormaliseText(yaml, out _);

    private static YamlMappingNode Root(string yaml) =>
        YamlLineResolver.TryParseYamlRoot(yaml)
        ?? throw new InvalidOperationException("Expected a mapping root.");

    private static IEnumerable<string> TopLevelKeys(string yaml) =>
        Root(yaml).Children.Keys.Select(k => k.ToString());

    private static YamlMappingNode FirstStep(string yaml) =>
        (YamlMappingNode)((YamlSequenceNode)Root(yaml).Children[new YamlScalarNode("steps")])[0];

    private static IEnumerable<string> KeysOfFirstStep(string yaml) =>
        FirstStep(yaml).Children.Keys.Select(k => k.ToString());

    /// <summary>The keys of the first mapping in the document found under <paramref name="containerKey"/>.</summary>
    private static IEnumerable<string> KeysUnder(string yaml, string containerKey)
    {
        var container = FindMapping(Root(yaml), containerKey)
            ?? throw new InvalidOperationException($"No mapping found under '{containerKey}'.");

        return container.Children.Keys.Select(k => k.ToString());
    }

    private static YamlMappingNode? FindMapping(YamlNode node, string key)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key.ToString() == key && pair.Value is YamlMappingNode match)
                    {
                        return match;
                    }
                }

                return mapping.Children.Values.Select(v => FindMapping(v, key)).FirstOrDefault(m => m is not null);

            case YamlSequenceNode sequence:
                return sequence.Children.Select(v => FindMapping(v, key)).FirstOrDefault(m => m is not null);

            default:
                return null;
        }
    }

    /// <summary>
    /// Every scalar in the document as a comparable <c>(Value, Tag)</c> key plus its style. The key
    /// includes the tag because <see cref="YamlNode"/> equality does too, and excludes the style
    /// because the style is what the caller is checking separately.
    /// </summary>
    private static IEnumerable<(string Key, ScalarStyle Style, YamlScalarNode Node)> ScalarsOf(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                var tag = scalar.Tag.IsEmpty ? "<none>" : scalar.Tag.Value;
                yield return ($"[{tag}] {scalar.Value}", scalar.Style, scalar);
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    foreach (var found in ScalarsOf(child))
                    {
                        yield return found;
                    }
                }

                break;

            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    foreach (var found in ScalarsOf(pair.Key).Concat(ScalarsOf(pair.Value)))
                    {
                        yield return found;
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Asks the EMITTER, not the normalizer, whether a scalar with this value and tag can be written
    /// plain — by writing it plain and seeing what comes back.
    /// </summary>
    private static bool TheEmitterCanWriteThisPlain(YamlScalarNode scalar)
    {
        var probe = new YamlScalarNode(scalar.Value) { Style = ScalarStyle.Plain };
        if (!scalar.Tag.IsEmpty)
        {
            probe.Tag = scalar.Tag;
        }

        var document = new YamlMappingNode { { new YamlScalarNode("k"), probe } };
        var writer = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(document)).Save(
            new Emitter(writer, EmitterSettings.Default.WithBestWidth(int.MaxValue).WithNewLine("\n")),
            assignAnchors: false);

        var root = YamlLineResolver.TryParseYamlRoot(writer.ToString());
        return root?.Children[new YamlScalarNode("k")] is YamlScalarNode { Style: ScalarStyle.Plain };
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
