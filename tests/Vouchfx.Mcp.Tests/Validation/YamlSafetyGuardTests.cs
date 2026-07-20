using System.Text;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="YamlSafetyGuard"/> in isolation — this type never calls YamlDotNet, so every
/// test here is safe to run even with a deliberately hostile input: if the guard had a bug that
/// let something dangerous through, nothing downstream of these tests would ever try to parse it.
/// </summary>
public class YamlSafetyGuardTests
{
    [Fact]
    public void Check_OrdinaryShallowSuite_ReturnsNull()
    {
        const string yaml = """
            metadata:
              name: "Health check smoke test"
            steps:
              - id: check-health
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                expect:
                  status: 200
            """;

        Assert.Null(YamlSafetyGuard.Check(yaml));
    }

    [Fact]
    public void CheckSize_TextOverTheByteLimit_ReturnsTooLarge()
    {
        var oversized = new string('a', (int)YamlSafetyGuard.MaxSuiteSizeBytes + 1);

        var error = YamlSafetyGuard.CheckSize(oversized);

        Assert.NotNull(error);
        Assert.Equal("too-large", error!.Kind);
    }

    [Fact]
    public void CheckSize_TextAtOrUnderTheByteLimit_ReturnsNull()
    {
        var atLimit = new string('a', (int)YamlSafetyGuard.MaxSuiteSizeBytes);

        Assert.Null(YamlSafetyGuard.CheckSize(atLimit));
    }

    // ── B1: deep nesting (proven native StackOverflowException vector) ────────────────────────

    [Fact]
    public void CheckNestingDepth_20000NestedFlowBrackets_ReturnsTooDeep_WithoutTouchingYamlDotNet()
    {
        // The proven crash shape from the security review: ~20,000 nested flow brackets
        // native-stack-overflows YamlDotNet's scanner before its own recursion guard can fire.
        // This guard must reject it from the raw text alone — it never constructs a YamlStream,
        // a Deserializer, or anything else from YamlDotNet, so there is nothing here that could
        // reproduce that crash even if this test's premise were wrong.
        var deeplyNested = new string('[', 20_000) + new string(']', 20_000);

        var error = YamlSafetyGuard.CheckNestingDepth(deeplyNested);

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);
    }

    [Fact]
    public void CheckNestingDepth_FlowBracketsAtOrUnderTheLimit_ReturnsNull()
    {
        var atLimit = new string('[', YamlSafetyGuard.MaxNestingDepth) + new string(']', YamlSafetyGuard.MaxNestingDepth);

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(atLimit));
    }

    [Fact]
    public void CheckNestingDepth_FlowBracketsOneOverTheLimit_ReturnsTooDeep()
    {
        var overLimit = new string('[', YamlSafetyGuard.MaxNestingDepth + 1) + new string(']', YamlSafetyGuard.MaxNestingDepth + 1);

        var error = YamlSafetyGuard.CheckNestingDepth(overLimit);

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);
    }

    [Fact]
    public void CheckNestingDepth_MixedBlockThenFlowNesting_MeasuresTheCombinedDepthNotEitherAlone()
    {
        // The accounting bypass from the security review's adversarial re-review: ~63 levels of
        // block-mapping indentation whose innermost value then opens ~63 levels of flow
        // brackets. EITHER alone (63 block levels; or 63 flow levels with no block nesting at
        // all) sits AT/under the 64-level limit and would pass; only the combined total (~126,
        // roughly double the limit) correctly exceeds it — proving depth is tracked as one
        // running total across block AND flow structure, via the Scanner's own
        // BlockMappingStart/FlowSequenceStart token stream, not as two independent maxima (an
        // earlier hand-rolled version of this guard did exactly that and missed this shape).
        // Constructed at ~126 here, deliberately far under YamlDotNet's own measured
        // full-Deserialize crash threshold, so it is this guard's rejection under test, not a
        // real parse — this text is never handed to YamlToJsonConverter or anything else that
        // builds an object graph.
        const int blockLevels = 63;
        const int flowLevels = 63;

        var builder = new StringBuilder();
        for (var level = 0; level < blockLevels; level++)
        {
            builder.Append(' ', level * 2).Append('k').Append(level).Append(':');
            if (level == blockLevels - 1)
            {
                builder.Append(' ').Append('[', flowLevels).Append(']', flowLevels);
            }

            builder.Append('\n');
        }

        var mixedNesting = builder.ToString();

        // Neither contributor alone would be rejected — establishes that what follows really is
        // testing the COMBINATION, not just "block indentation alone" or "flow brackets alone".
        var blockOnlyBuilder = new StringBuilder();
        for (var level = 0; level < blockLevels; level++)
        {
            blockOnlyBuilder.Append(' ', level * 2).Append('k').Append(level);
            blockOnlyBuilder.Append(level == blockLevels - 1 ? ": b\n" : ":\n");
        }

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(blockOnlyBuilder.ToString()));

        var flowOnly = new string('[', flowLevels) + new string(']', flowLevels);
        Assert.Null(YamlSafetyGuard.CheckNestingDepth(flowOnly));

        var error = YamlSafetyGuard.CheckNestingDepth(mixedNesting);

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);

        // The Scanner-based counter early-exits the instant depth crosses the limit (see
        // ComputeMaxNestingDepth's remarks) rather than consuming the rest of the token stream,
        // so the reported depth is always exactly MaxNestingDepth + 1 here — not the shape's true
        // ~126 peak. That is deliberate: it bounds worst-case scanning to a fixed small number of
        // tokens regardless of how much deeper a hostile document goes.
        Assert.Contains($"{YamlSafetyGuard.MaxNestingDepth + 1}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckNestingDepth_DeeplyIncreasingBlockIndentation_ReturnsTooDeep()
    {
        // Every level's key has NO inline value except the innermost one - unambiguous, valid
        // nested block mappings, depth levels deep ("a:\n  a:\n    a: b\n..."). An earlier
        // version of this test used a different, degenerate shape instead - "a: b" (WITH an
        // inline scalar value) repeated at each increasing indent - which turned out not to be
        // how YAML expresses nesting at all (giving "a" a complete value and then following it
        // with MORE-indented content has no home in the grammar) and, discovered while
        // validating the Scanner-based depth counter this test now exercises, hangs YamlDotNet's
        // own Scanner even at a small depth — not because of genuine deep recursion, but because
        // it is a malformed/degenerate input the real Scanner spends a long time trying to make
        // sense of. Confirmed via a scratchpad probe, never committed.
        const int depth = YamlSafetyGuard.MaxNestingDepth + 10;
        var builder = new StringBuilder();
        for (var level = 0; level < depth; level++)
        {
            builder.Append(' ', level * 2).Append('a').Append(level == depth - 1 ? ": b\n" : ":\n");
        }

        var error = YamlSafetyGuard.CheckNestingDepth(builder.ToString());

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);
    }

    [Fact]
    public void CheckNestingDepth_FlowBracketsInsideAQuotedString_AreNotCountedAsNesting()
    {
        // A quoted JSON payload embedded as a scalar (very plausible for e.g. mq-publish's
        // 'payload' field) must not trip the flow-bracket counter — those brackets are string
        // content, not real YAML nesting.
        var nestedJsonInsideQuotes = "payload: '" + new string('[', 200) + new string(']', 200) + "'\n";

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(nestedJsonInsideQuotes));
    }

    // ── B1: compact block-sequence chaining ("- - - x") ────────────────────────────────────────

    [Fact]
    public void CheckNestingDepth_65ChainedCompactSequenceIndicators_ReturnsTooDeep()
    {
        // The adversarial-probing discovery: YAML's compact in-line notation lets nested block
        // sequences chain on ONE line with no indentation increase and no flow brackets at all —
        // "- - - - x" is a sequence containing a sequence containing a sequence containing the
        // scalar "x". 65 chained indicators is invisible to plain indentation/flow-bracket
        // tracking (one line, one indent, zero brackets) but is 65 real levels of nesting.
        var chained = string.Concat(Enumerable.Repeat("- ", 65)) + "x";

        var error = YamlSafetyGuard.CheckNestingDepth(chained);

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);
    }

    [Fact]
    public void CheckNestingDepth_CompactChainCombinedWithBlockIndentationAndFlowBrackets_MeasuresTheCombinedDepth()
    {
        // A chain nested beneath real block indentation, whose own innermost entry then opens
        // flow brackets: all three contributors (block indentation, compact chaining, flow
        // brackets) must combine along the same path, not just two of the three.
        //
        // The chain sits on its OWN line, indented one level deeper than the last mapping key —
        // NOT smooshed onto "key: - - - ..." on the same line. A mapping value is not YAML
        // syntax for introducing a block-sequence chain inline after a colon; a block sequence's
        // "-" indicators must start their own (appropriately indented) content position, exactly
        // like the everyday "key:\n  - item" shape, just chained instead of a single dash.
        const int blockLevels = 10;
        const int chainLevels = 30;
        const int flowLevels = 30;

        var builder = new StringBuilder();
        for (var level = 0; level < blockLevels; level++)
        {
            builder.Append(' ', level * 2).Append('k').Append(level).Append(":\n");
        }

        builder.Append(' ', blockLevels * 2);
        for (var chain = 0; chain < chainLevels; chain++)
        {
            builder.Append("- ");
        }

        builder.Append('[', flowLevels).Append(']', flowLevels).Append('\n');

        var mixed = builder.ToString();

        // None of the three contributors alone (10 block levels; 30 chained indicators; 30 flow
        // brackets) would be rejected — establishes the combination is what triggers this, not
        // any one part.
        var blockOnlyBuilder = new StringBuilder();
        for (var level = 0; level < blockLevels; level++)
        {
            blockOnlyBuilder.Append(' ', level * 2).Append('k').Append(level);
            blockOnlyBuilder.Append(level == blockLevels - 1 ? ": b\n" : ":\n");
        }

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(blockOnlyBuilder.ToString()));
        Assert.Null(YamlSafetyGuard.CheckNestingDepth(string.Concat(Enumerable.Repeat("- ", chainLevels)) + "x"));
        Assert.Null(YamlSafetyGuard.CheckNestingDepth(new string('[', flowLevels) + new string(']', flowLevels)));

        var error = YamlSafetyGuard.CheckNestingDepth(mixed);

        Assert.NotNull(error);
        Assert.Equal("too-deep", error!.Kind);

        // Early exit (see ComputeMaxNestingDepth's remarks): the reported depth is always
        // exactly MaxNestingDepth + 1, not this shape's true ~70 peak (10 block levels + 30
        // chained indicators + 30 flow brackets, roughly).
        Assert.Contains($"{YamlSafetyGuard.MaxNestingDepth + 1}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckNestingDepth_FlatSiblingSequenceOfManyItems_IsNotMistakenForDeepNesting()
    {
        // 200 SIBLING "- item" lines at the same indentation: this is a list of 200 things, one
        // level deep — not 200 levels of nesting. Each line must independently measure a chain of
        // exactly 1, not an ever-growing running total.
        var builder = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            builder.Append("- item").Append(i).Append('\n');
        }

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(builder.ToString()));
    }

    [Theory]
    [InlineData("key: a-b-c")]
    [InlineData("key: -5")]
    [InlineData("key: 2026-07-20")]
    public void CheckNestingDepth_ScalarsContainingDashes_AreNotMistakenForSequenceIndicators(string yaml)
    {
        // None of these lines' dashes are sequence-entry indicators: each is preceded by a letter
        // or digit (mid-scalar), not a token boundary, and the line's first non-space character
        // is 'k' (from "key"), not '-' — chain detection never even looks at them.
        Assert.Null(YamlSafetyGuard.CheckNestingDepth(yaml));
    }

    [Fact]
    public void CheckNestingDepth_DashInsideQuotesAndInsideAComment_IsNotMistakenForSequenceIndicators()
    {
        const string yaml = """
            # - - - fake nesting in a comment
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: "- - - fake nesting in a quoted string"
            """;

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(yaml));
    }

    [Fact]
    public void CheckNestingDepth_DocumentStartAndEndMarkers_AreNotMistakenForSequenceIndicators()
    {
        // '---' (document start) is three dashes with NO space between them — the first dash is
        // immediately followed by another dash, not whitespace, so it is never recognised as a
        // sequence indicator at all; '...' (document end) contains no dashes and is unaffected.
        const string yaml = """
            ---
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /health
            ...
            """;

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(yaml));
    }

    [Fact]
    public void CheckNestingDepth_SequenceItemValueThatIsAQuotedStringWithDashes_CountsOnlyTheRealIndicator()
    {
        // "- " is the one real indicator here; the quoted content that follows just happens to
        // look like more chaining, but must not be mistaken for it.
        const string yaml = "- \"- - - fake nesting\"\n";

        Assert.Null(YamlSafetyGuard.CheckNestingDepth(yaml));
    }

    // ── B1: anchor/alias expansion ("billion laughs") ──────────────────────────────────────────

    [Fact]
    public void CheckAnchorsAndAliases_BillionLaughsShapedDocument_ReturnsAliasLimit()
    {
        // Mirrors the proven proof of concept's shape: each anchor level is aliased several
        // times by the next, so alias references vastly outnumber anchor declarations — this is
        // what actually drives exponential expansion, not the anchor count alone.
        var billionLaughs = BuildAnchorChain(anchorLevels: 6, referencesPerLevel: 4);

        var error = YamlSafetyGuard.CheckAnchorsAndAliases(billionLaughs);

        Assert.NotNull(error);
        Assert.Equal("alias-limit", error!.Kind);
    }

    [Fact]
    public void CheckAnchorsAndAliases_ACoupleOfAnchorsAndAliases_ReturnsNull()
    {
        const string yaml = """
            defaults: &defaults
              retries: 3
            steps:
              - id: s1
                type: http.rest
                target: *defaults
            """;

        Assert.Null(YamlSafetyGuard.CheckAnchorsAndAliases(yaml));
    }

    [Fact]
    public void CheckAnchorsAndAliases_AmpersandAndAsteriskInsideRealisticContent_AreNotMiscounted()
    {
        // Realistic suite content routinely contains literal '&' and '*' characters that are NOT
        // YAML anchors/aliases: a URL query string, a SQL "SELECT *", and a bitwise/boolean "&&"
        // in a WHERE clause. None of these are preceded by a token boundary immediately followed
        // by a valid anchor-name character in the way real "&name"/"*name" syntax is.
        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: api
                path: "/search?a=1&b=2&c=3"
              - id: query-db
                type: db-assert.postgres
                target: db
                query: "SELECT * FROM orders WHERE status = 'shipped' AND flag = 1 & 2"
            """;

        Assert.Null(YamlSafetyGuard.CheckAnchorsAndAliases(yaml));
    }

    [Fact]
    public void CheckAnchorsAndAliases_CommentContainingAnchorLikeText_IsNotCounted()
    {
        const string yaml = """
            # &not-an-anchor *not-an-alias either
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /health
            """;

        Assert.Null(YamlSafetyGuard.CheckAnchorsAndAliases(yaml));
    }

    /// <summary>
    /// Builds a "billion laughs"-shaped YAML document: <paramref name="anchorLevels"/> anchors,
    /// each (after the first) aliasing the previous level <paramref name="referencesPerLevel"/>
    /// times — the real proof of concept's shape, just parametrised and kept small enough that
    /// even if the guard somehow failed to reject it, evaluating it would still be cheap. This
    /// generator is used only to build INPUT for <see cref="YamlSafetyGuard"/>'s own text-based
    /// checks — never passed anywhere near YamlDotNet.
    /// </summary>
    private static string BuildAnchorChain(int anchorLevels, int referencesPerLevel)
    {
        if (anchorLevels <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("a0: &a0 \"x\"\n");

        for (var level = 1; level <= anchorLevels; level++)
        {
            builder.Append('a').Append(level).Append(": &a").Append(level).Append(" [");
            for (var reference = 0; reference < referencesPerLevel; reference++)
            {
                if (reference > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("*a").Append(level - 1);
            }

            builder.Append("]\n");
        }

        return builder.ToString();
    }
}
