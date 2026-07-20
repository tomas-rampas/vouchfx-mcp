using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Tokens;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Rejects untrusted <c>.e2e.yaml</c> text that could crash the whole server process, entirely by
/// inspecting the text — before <see cref="YamlToJsonConverter"/> (or any other YamlDotNet entry
/// point that builds an object graph) ever sees a byte of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pre-parse guard, not a try/catch (B1):</b> two proven attack vectors against
/// YamlDotNet 16.3.0 cannot be handled after the fact.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Deep nesting.</b> A document with thousands of nested flow brackets, block-sequence
/// indicators, or indentation levels native-stack-overflows YamlDotNet's recursive-descent
/// <c>Parser</c> (<c>Parser.ParseNode</c>) before its own <c>MaximumRecursionLevelReachedException</c>
/// guard ever fires — and that guard, it turns out, isn't even in the parsing path to begin with:
/// inspecting YamlDotNet 16.3.0's own source (the exact pinned commit,
/// <c>ae480660f4fb26f3eb0b41c1d1fcf21c0e9d9e73</c>) shows <c>RecursionLevel</c>/
/// <c>MaximumRecursionLevelReachedException</c> are used ONLY by <c>YamlNode.AllNodes</c> in the
/// <c>RepresentationModel</c> (walking an already-built node tree) — never by the <c>Parser</c>
/// that <see cref="YamlToJsonConverter"/> actually drives, and never exposed as a configurable
/// option on <c>DeserializerBuilder</c>. A native <see cref="StackOverflowException"/> cannot be
/// caught in .NET — by the time it would happen, no <c>try</c>/<c>catch</c> anywhere in this
/// process can help. The only fix is to never let the recursive-descent Parser see text shaped
/// like this at all.
/// </description></item>
/// <item><description>
/// <b>"Billion laughs".</b> A handful of YAML anchors, each aliased multiple times by the next,
/// expands a tiny file into an enormous one once <c>SerializerBuilder().JsonCompatible().Serialize()</c>
/// re-expands every alias in full (aliases MUST re-expand in JSON — JSON has no equivalent of a
/// YAML alias). A proof of concept using roughly 8 anchor/alias tokens expands 218 bytes to
/// ~17 MB; the same shape scales to gigabytes with a few more levels. YamlDotNet 16.3.0 exposes
/// no alias-count or expansion limit on <c>DeserializerBuilder</c> to bound this (confirmed
/// against the same pinned source — no such option exists), so counting raw anchor/alias tokens
/// before parsing is the only available defence, not "defence in depth" on top of a library
/// setting that would otherwise carry the load.
/// </description></item>
/// </list>
/// <para>
/// <b>History — why nesting depth is now bounded via YamlDotNet's own Scanner, not a hand-rolled
/// character scan.</b> Four rounds of adversarial review found four successive gaps in an
/// earlier, hand-rolled character-by-character depth scan: it tracked block-indentation depth and
/// flow-bracket depth as two independent maxima instead of a combined total; it had no case at all
/// for YAML's compact block-sequence-chaining notation (<c>- - - x</c>); and each fix raised the
/// obvious next question of what OTHER YAML nesting construct (e.g. <c>?</c> complex mapping
/// keys) the hand-rolled scan might still be missing, since it re-implemented YAML's block/flow
/// grammar by hand rather than delegating to YamlDotNet. Rather than continue patching
/// construct-by-construct, <see cref="ComputeMaxNestingDepth"/> now consumes YamlDotNet's own
/// low-level <c>Scanner</c> — a tokeniser, NOT the recursive-descent <c>Parser</c> — and counts
/// <c>BlockSequenceStart</c>/<c>BlockMappingStart</c>/<c>FlowSequenceStart</c>/<c>FlowMappingStart</c>
/// tokens against their matching <c>BlockEnd</c>/<c>FlowSequenceEnd</c>/<c>FlowMappingEnd</c>
/// tokens. This delegates ALL YAML grammar recognising nesting (block sequences, compact chaining,
/// <c>?</c> complex keys, flow collections, indentation) to YamlDotNet itself, so no construct can
/// be missed — the concern shifts entirely to "does the Scanner ITSELF stay safe to run on hostile
/// input", which was verified empirically (a scratchpad probe, never committed) before this
/// change was made: the Scanner alone — never constructing a <c>Parser</c> or <c>Deserializer</c>
/// — survived 2000-deep versions of all three proven attack shapes (nested flow brackets, block
/// indentation, compact dash-chains) in under 60 ms each, correctly exposing the start/end tokens
/// needed to count depth, while the equivalent full <c>Deserializer.Deserialize</c> call did not
/// complete within 15 seconds for any of the three (the known-unsafe recursive-descent path this
/// guard exists to keep untrusted input away from). Anchor/alias counting is unaffected by this
/// change and remains the simpler, lower-risk hand-rolled scan in <see cref="CountAnchorsAndAliases"/>
/// — it was never the subject of an adversarial finding across all four review rounds.
/// </para>
/// </remarks>
public static class YamlSafetyGuard
{
    /// <summary>
    /// Maximum accepted suite size, in UTF-8 bytes. Real <c>.e2e.yaml</c> suites are small,
    /// hand-authored documents — even a large, multi-step suite is well under 100 KB. 5 MB gives
    /// 50x+ headroom over any plausible legitimate suite while bounding the worst case: an
    /// oversized file is rejected by its length alone, before <c>File.ReadAllText</c> (or this
    /// guard's own scan) ever has to process it.
    /// </summary>
    public const long MaxSuiteSizeBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Maximum accepted nesting depth, measured as the combined open depth of block sequences,
    /// block mappings, flow sequences, and flow mappings along the same root-to-leaf path (see
    /// <see cref="ComputeMaxNestingDepth"/>). A real <c>.e2e.yaml</c> document is shallow: root
    /// -&gt; <c>steps[]</c> -&gt; step object -&gt; at most one or two further levels (e.g.
    /// <c>expect.row</c>, <c>avro.schema</c>). 64 gives roughly 8-10x headroom over any legitimate
    /// document's structural depth while remaining far short of the depths proven to
    /// native-stack-overflow YamlDotNet's recursive-descent Parser.
    /// </summary>
    public const int MaxNestingDepth = 64;

    /// <summary>
    /// Maximum accepted count of YAML anchor (<c>&amp;name</c>) declarations. This DSL has no
    /// documented or actual use for anchors — every fixture and every real suite in this repo
    /// uses zero. The proven "billion laughs" proof of concept needs only a handful of anchor
    /// levels (roughly 8) to reach ~17 MB from 218 bytes; capping at 10 sits just above that
    /// proven shape while leaving a little headroom for a hypothetical, currently nonexistent,
    /// legitimate use.
    /// </summary>
    public const int MaxAnchorCount = 10;

    /// <summary>
    /// Maximum accepted count of YAML alias (<c>*name</c>) references. Exponential expansion
    /// needs many alias REFERENCES, not just anchor definitions — the proven proof of concept's
    /// ~17 MB result comes from each anchor level being aliased several times by the next, so its
    /// total alias count is well above its anchor count. 10 is nowhere near enough for any
    /// meaningful expansion, which is exactly the point.
    /// </summary>
    public const int MaxAliasCount = 10;

    /// <summary>
    /// Runs every guard below, in order (size, then nesting depth, then anchor/alias counts), and
    /// returns the first violation found, or <see langword="null"/> if <paramref name="yamlText"/>
    /// passes all of them and is safe to hand to <see cref="YamlToJsonConverter"/>.
    /// </summary>
    public static SuiteValidationError? Check(string yamlText)
    {
        return CheckSize(yamlText)
            ?? CheckNestingDepth(yamlText)
            ?? CheckAnchorsAndAliases(yamlText);
    }

    public static SuiteValidationError? CheckSize(string yamlText)
    {
        var byteCount = Encoding.UTF8.GetByteCount(yamlText);
        if (byteCount <= MaxSuiteSizeBytes)
        {
            return null;
        }

        return new SuiteValidationError(
            "too-large",
            null,
            $"The suite is {byteCount:N0} bytes, which exceeds the {MaxSuiteSizeBytes:N0}-byte limit.",
            null,
            null);
    }

    public static SuiteValidationError? CheckNestingDepth(string yamlText)
    {
        var depth = ComputeMaxNestingDepth(yamlText);
        if (depth <= MaxNestingDepth)
        {
            return null;
        }

        return new SuiteValidationError(
            "too-deep",
            null,
            $"The suite nests at least {depth:N0} levels deep (block sequences, block mappings, " +
            $"and flow collections combined), which exceeds the {MaxNestingDepth:N0}-level limit.",
            null,
            null);
    }

    public static SuiteValidationError? CheckAnchorsAndAliases(string yamlText)
    {
        var (anchorCount, aliasCount) = CountAnchorsAndAliases(yamlText);
        if (anchorCount <= MaxAnchorCount && aliasCount <= MaxAliasCount)
        {
            return null;
        }

        return new SuiteValidationError(
            "alias-limit",
            null,
            $"The suite declares {anchorCount:N0} YAML anchor(s) and uses {aliasCount:N0} " +
            $"alias reference(s), exceeding the limit of {MaxAnchorCount:N0} anchors / " +
            $"{MaxAliasCount:N0} aliases. This DSL does not use YAML anchors/aliases; a small " +
            "number of them can expand a tiny file into a huge one once re-serialised.",
            null,
            null);
    }

    /// <summary>
    /// Computes the maximum combined nesting depth by consuming YamlDotNet's own low-level
    /// <see cref="Scanner"/> token stream directly — never constructing a <c>Parser</c> or
    /// <c>Deserializer</c> (see this type's remarks for the empirical evidence that the Scanner
    /// alone is safe to run on hostile input where the full pipeline is not).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts <see cref="BlockSequenceStart"/>/<see cref="BlockMappingStart"/>/
    /// <see cref="FlowSequenceStart"/>/<see cref="FlowMappingStart"/> tokens against their
    /// matching <see cref="BlockEnd"/>/<see cref="FlowSequenceEnd"/>/<see cref="FlowMappingEnd"/>
    /// tokens — the Scanner already emits exactly one start/end token pair per real nesting level
    /// for EVERY YAML nesting construct (ordinary indentation, compact block-sequence chaining,
    /// <c>?</c> complex mapping keys, flow collections), so this single counter needs no
    /// construct-specific logic of its own.
    /// </para>
    /// <para>
    /// <b>Early exit:</b> returns the moment depth exceeds <see cref="MaxNestingDepth"/>, rather
    /// than consuming the rest of the token stream. This bounds the worst case to scanning at
    /// most <see cref="MaxNestingDepth"/> + 1 start tokens even if the Scanner were ever slow (not
    /// just deep) for some future adversarial shape.
    /// </para>
    /// <para>
    /// <b>If the Scanner itself throws</b> (a <see cref="YamlException"/> for text it cannot even
    /// tokenise), this returns 0 rather than propagating — this guard's only job is bounding depth
    /// before the real parse, and if depth cannot be determined at all there is nothing to report
    /// FROM THIS CHECK. The real <see cref="YamlToJsonConverter.Convert"/> is built on the exact
    /// same underlying tokeniser and will hit the identical failure at the identical point,
    /// reported as a proper <c>yaml-parse</c> error by <c>SuiteValidator</c>'s own catch for
    /// that — it can never see further into the document than this pre-check did, so no depth
    /// this pre-check missed because of the exception can reach it either.
    /// </para>
    /// </remarks>
    private static int ComputeMaxNestingDepth(string yamlText)
    {
        try
        {
            var scanner = new Scanner(new StringReader(yamlText));
            var depth = 0;

            while (scanner.MoveNext())
            {
                switch (scanner.Current)
                {
                    case BlockSequenceStart:
                    case BlockMappingStart:
                    case FlowSequenceStart:
                    case FlowMappingStart:
                        depth++;
                        if (depth > MaxNestingDepth)
                        {
                            return depth;
                        }

                        break;

                    case BlockEnd:
                    case FlowSequenceEnd:
                    case FlowMappingEnd:
                        if (depth > 0)
                        {
                            depth--;
                        }

                        break;
                }
            }

            return depth;
        }
        catch (YamlException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts raw YAML anchor (<c>&amp;name</c>) and alias (<c>*name</c>) tokens via a single
    /// linear character scan with no recursion. Quoted scalars (single- or double-quoted) and
    /// <c>#</c> comments are tracked so that <c>&amp;</c>/<c>*</c> characters inside string
    /// content (e.g. a URL query string) or a comment are not mistaken for real anchor/alias
    /// syntax. Deliberately NOT migrated to Scanner tokens alongside <see cref="ComputeMaxNestingDepth"/>:
    /// this hand-rolled scan was never the subject of an adversarial finding across four review
    /// rounds, unlike depth counting — see this type's remarks.
    /// </summary>
    private static (int AnchorCount, int AliasCount) CountAnchorsAndAliases(string text)
    {
        var anchorCount = 0;
        var aliasCount = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var previousChar = '\n'; // Treats the start of the text like the start of a line.

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            switch (c)
            {
                case '\'' when !inDoubleQuote:
                    inSingleQuote = !inSingleQuote;
                    break;

                case '"' when !inSingleQuote && !IsEscaped(text, i):
                    inDoubleQuote = !inDoubleQuote;
                    break;

                case '#' when !inSingleQuote && !inDoubleQuote && IsTokenBoundary(previousChar):
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue; // Leave the newline itself for the next iteration.

                case '&' or '*' when !inSingleQuote && !inDoubleQuote && IsTokenBoundary(previousChar) &&
                                      i + 1 < text.Length && IsAnchorNameCharacter(text[i + 1]):
                    if (c == '&')
                    {
                        anchorCount++;
                    }
                    else
                    {
                        aliasCount++;
                    }

                    break;
            }

            previousChar = c;
            i++;
        }

        return (anchorCount, aliasCount);
    }

    /// <summary>
    /// A character that could legitimately precede a YAML anchor/alias/comment introducer:
    /// whitespace, a line start, or a punctuation character that begins a new value position.
    /// Used to avoid mistaking, say, the '&amp;' in a URL query string ("...?a=1&amp;b=2") for an
    /// anchor — that '&amp;' is preceded by a digit, not a token boundary.
    /// </summary>
    private static bool IsTokenBoundary(char c) => c is ' ' or '\t' or '\n' or '\r' or '[' or '{' or ',' or ':' or '-';

    private static bool IsAnchorNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    /// <summary>
    /// Whether the double-quote character at <paramref name="index"/> is escaped by an odd number
    /// of immediately preceding backslashes (so <c>\"</c> is escaped, but <c>\\"</c> is not — the
    /// backslash itself was escaped, and the quote is a real, unescaped one).
    /// </summary>
    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        var i = index - 1;
        while (i >= 0 && text[i] == '\\')
        {
            backslashes++;
            i--;
        }

        return backslashes % 2 == 1;
    }
}
