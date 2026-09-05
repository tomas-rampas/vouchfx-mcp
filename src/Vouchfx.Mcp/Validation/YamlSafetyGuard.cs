using System.Text;
using Vouchfx.Mcp.Contracts;
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
/// <b>Why a pre-parse guard, not a try/catch (B1).</b> The two proven attack vectors below are
/// bounded by inspecting the text, before any object graph is built. Their calculus is NOT the
/// same, and this comment is kept honest about which is which on the current pin (YamlDotNet
/// 18.1.0, re-measured this session — not carried over unverified from the 16.3.0 the guard was
/// first written against): the billion-laughs vector still genuinely cannot be handled after the
/// fact, while deep nesting IS caught by the library itself on 18.1.0, so the depth cap is now
/// fail-fast defence-in-depth there. Separately, the per-line length cap this guard also runs
/// (see <see cref="MaxLineLength"/>) defends the guard's OWN <c>Scanner</c> against a distinct,
/// still-live tokeniser pathology that neither library setting addresses.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Deep nesting.</b> A document with thousands of nested flow brackets, block-sequence
/// indicators, or indentation levels drives YamlDotNet's recursive-descent <c>Parser</c> deep.
/// <b>On the pinned YamlDotNet 18.1.0 the library catches this itself:</b> the default
/// <c>DeserializerBuilder</c> — the exact configuration <see cref="YamlToJsonConverter"/> drives —
/// applies a maximum-recursion bound and raises a catchable <c>MaximumRecursionLevelReachedException</c>
/// long before any native stack overflow (measured on 18.1.0: a nested-flow document first throws
/// at depth 131, orders of magnitude short of a native <see cref="StackOverflowException"/>), and
/// <c>DeserializerBuilder.WithMaximumRecursion(int)</c> exposes that bound as a configurable option.
/// This is a CHANGE from the 16.3.0 behaviour the original guard was written against — where the
/// recursive descent could native-stack-overflow before any guard fired, and a native
/// <see cref="StackOverflowException"/> cannot be caught in .NET — and it was re-measured on the
/// current pin rather than assumed. The pre-parse depth cap is therefore RETAINED as fail-fast
/// defence-in-depth: it rejects at <see cref="MaxNestingDepth"/> with a uniform <c>VFX-D-####</c>
/// diagnostic before the library's own limit is reached, rather than leaning on library-internal
/// exception behaviour that has already shifted once across a version bump — it is no longer the
/// sole barrier against an uncatchable crash, because on 18.1.0 there is none for this shape.
/// </description></item>
/// <item><description>
/// <b>"Billion laughs".</b> A handful of YAML anchors, each aliased multiple times by the next,
/// expands a tiny file into an enormous one once <c>SerializerBuilder().JsonCompatible().Serialize()</c>
/// re-expands every alias in full (aliases MUST re-expand in JSON — JSON has no equivalent of a
/// YAML alias). A proof of concept using roughly 8 anchor/alias tokens expands 218 bytes to
/// ~17 MB; the same shape scales to gigabytes with a few more levels. <b>This is the vector that
/// genuinely cannot be handled after the fact:</b> the blow-up is memory exhaustion during
/// re-serialisation, not a catchable exception, and YamlDotNet 18.1.0 exposes no alias-count or
/// expansion limit on <c>DeserializerBuilder</c> to bound it — re-confirmed on the current pin,
/// where the <c>WithMaximumRecursion(int)</c> option that DOES catch deep nesting (above) bounds
/// parser depth only and leaves alias expansion entirely unbounded (measured: a billion-laughs
/// shape re-serialises unbounded even with a low maximum-recursion set). Counting raw anchor/alias
/// tokens before parsing is the only available defence, not "defence in depth" on top of a library
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
/// needed to count depth. (When that migration was measured on 16.3.0 the equivalent full
/// <c>Deserializer.Deserialize</c> call did not complete within 15 seconds for any of the three;
/// on the current 18.1.0 pin that same call instead throws a catchable
/// <c>MaximumRecursionLevelReachedException</c> at the library's default depth bound — see this
/// type's deep-nesting bullet above — but the Scanner remains the right tool here regardless,
/// because it yields a clean depth COUNT and a uniform <c>VFX-D-####</c> diagnostic rather than a
/// library exception this guard would then have to translate.) Anchor/alias counting is unaffected by this
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
    /// document's structural depth while remaining below the pinned YamlDotNet 18.1.0's own default
    /// parser recursion bound (measured: the default deserializer first throws
    /// <c>MaximumRecursionLevelReachedException</c> at flow depth 131), so this guard's uniform
    /// <c>VFX-D-####</c> diagnostic fires before the library would raise its own exception — see this
    /// type's deep-nesting remark for why that bound is a change from the 16.3.0 native-stack-overflow
    /// behaviour the guard was first written against.
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
    /// Maximum accepted length, in characters, of any single line of suite text (a run of
    /// non-<c>\n</c> characters). Unlike the size / nesting / anchor caps above, this one exists to
    /// defend the guard's OWN <see cref="ComputeMaxNestingDepth"/> Scanner, not just the downstream
    /// parse — which is why it MUST run before <see cref="CheckNestingDepth"/> (see
    /// <see cref="Check"/>). YamlDotNet 18.1.0's <c>Scanner</c> tracks every candidate simple key up
    /// to a fixed internal bound of 1024 characters, and a plain-scalar mapping key LONGER than that
    /// turns the tokeniser pathological: a 1024-character key scans in single-digit milliseconds, a
    /// 1025-character key does not complete at all (measured on the pinned 18.1.0, issue #71 — a suite whose single
    /// mapping key was ~2 KB drove the isolated validation worker past its 10-second wall clock and
    /// was killed at &gt;90 s, surfacing as VFX-E-1150 on EVERY validation of that suite). A giant
    /// unbroken key is a giant unbroken run of non-newline characters on one line, so a per-line
    /// length cap is the natural construct-agnostic bound — and <see cref="CheckLineLength"/>
    /// measures it with a cheap linear character scan, NEVER the Scanner, so this check can never
    /// itself hit the pathology it guards against (the same property that lets it run first).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 512 is half of YamlDotNet's own 1024 simple-key bound — a deliberate 2x margin below the
    /// measured cliff — while sitting far above any legitimate line: the longest line in any real
    /// <c>.e2e.yaml</c> suite in this repo is 78 characters, and the longest line in any YAML file
    /// here at all (a CI workflow) is 228, so 512 clears real content by 2x-6x. Verified safe by
    /// timing: a suite whose longest line is exactly 512 characters scans in single-digit
    /// milliseconds, nowhere near the 1025-character cliff.
    /// </para>
    /// <para>
    /// The hard ceiling is ~1024: a 1024-character key still scans (measured, single-digit ms), and
    /// only a 1025th character tips it over — so 512 is a lever, not a boundary, and ~1000 would be
    /// the safe setting if a legitimate long-value-line false positive ever surfaced. It has not, and
    /// 512 stays: the value-line remedies below make any legitimate long line trivially reformattable,
    /// so the 2x margin costs nothing real while giving a wide buffer against the cliff.
    /// </para>
    /// <para>
    /// Only plain-scalar KEYS trip the Scanner pathology — a 2 KB plain, quoted, or block-scalar
    /// VALUE scans in ~6 ms — but the cap is deliberately construct-agnostic: distinguishing a key
    /// from a value would require the very parse this pre-check exists to avoid. So an over-long
    /// VALUE line is rejected too, exactly as the anchor/alias cap rejects legitimate-but-unusual
    /// anchors. The remedy depends on WHAT the value is, and the two cases are not interchangeable:
    /// <list type="bullet">
    /// <item><description><b>Whitespace-insignificant content</b> (inline JSON, prose) — use a block
    /// scalar (<c>|</c> or <c>&gt;</c>), whose content spreads over short, indented lines.</description></item>
    /// <item><description><b>An opaque, unbreakable single token</b> (base64, a JWT, a hash, a signed
    /// URL) — do NOT use a block scalar: <c>&gt;</c> folds each line break to a SPACE and <c>|</c>
    /// inserts a NEWLINE, both of which silently CHANGE the token's value, so the suite would validate
    /// but carry the wrong payload. The lossless remedy is a DOUBLE-QUOTED scalar with backslash
    /// line-continuation — <c>key: "AAAA\</c> then a newline, indentation, and <c>BBBB"</c> —
    /// which YAML reassembles to exactly <c>AAAABBBB</c> (the escaped break and the continuation
    /// line's leading whitespace are both removed), keeping every physical line under the cap without
    /// altering the value. Verified by round-trip probe on the pinned YamlDotNet 18.1.0: a 650-char
    /// opaque token split this way across multiple lines re-parses byte-for-byte identical for every
    /// chunk size and indent tried, whereas the same token in a <c>&gt;</c> or <c>|</c> block scalar
    /// comes back with inserted spaces/newlines.</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public const int MaxLineLength = 512;

    /// <summary>
    /// Runs every guard below, in order (size, then per-line length, then nesting depth, then
    /// anchor/alias counts), and returns the first violation found, or <see langword="null"/> if
    /// <paramref name="yamlText"/> passes all of them and is safe to hand to
    /// <see cref="YamlToJsonConverter"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckLineLength"/> is placed BEFORE <see cref="CheckNestingDepth"/> deliberately
    /// and load-bearingly: <see cref="CheckNestingDepth"/> constructs a YamlDotNet <c>Scanner</c>,
    /// which is itself the thing that hangs on an over-long mapping key (see
    /// <see cref="MaxLineLength"/>). The line-length scan is a pure character scan that cannot hang,
    /// so it must get the first look at that shape and reject it before the Scanner ever runs.
    /// </remarks>
    public static SuiteValidationError? Check(string yamlText)
    {
        return CheckSize(yamlText)
            ?? CheckLineLength(yamlText)
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
            VfxCodeCatalogue.SuiteFileTooLarge,
            null,
            $"The suite is {byteCount:N0} bytes, which exceeds the {MaxSuiteSizeBytes:N0}-byte limit.",
            null,
            null);
    }

    /// <summary>
    /// Rejects text containing any single line longer than <see cref="MaxLineLength"/> characters,
    /// via a single linear character scan with no recursion and no YamlDotNet involvement — the
    /// defence against the over-long-mapping-key Scanner pathology described on
    /// <see cref="MaxLineLength"/>. A carriage return (<c>\r</c>) is treated as part of a CRLF line
    /// ending rather than line content, so a CRLF-encoded suite's lines measure identically to an
    /// LF-encoded one's (the same CRLF-insensitivity the rest of the validation pipeline relies on).
    /// Only <c>\n</c> resets the line counter: a lone <c>\r</c> (classic-Mac line ending) is treated
    /// as non-breaking, so a <c>\r</c>-delimited document is measured as one long line and may be
    /// over-rejected. That is deliberate and safe in one direction only — it rejects MORE, never
    /// fewer, documents, so it can never let an over-long line reach the Scanner; a genuine
    /// <c>\r</c>-only suite is vanishingly unlikely here and would fail the YAML parse regardless.
    /// </summary>
    /// <remarks>
    /// Early-exits the instant a line crosses the limit rather than scanning to the end of the
    /// document. For a REJECTING input the worst case is therefore <see cref="MaxLineLength"/> + 1
    /// characters of the first offending line — mirroring <see cref="ComputeMaxNestingDepth"/>'s early
    /// exit. An ACCEPTING document has no early exit and is scanned in full, but that is still a single
    /// linear O(n) character pass bounded by <see cref="MaxSuiteSizeBytes"/> (which <see cref="Check"/>
    /// enforces first), so the work this pre-check does is bounded on every input, hostile or not.
    /// </remarks>
    public static SuiteValidationError? CheckLineLength(string yamlText)
    {
        var lineLength = 0;
        foreach (var c in yamlText)
        {
            switch (c)
            {
                case '\n':
                    lineLength = 0;
                    continue;

                case '\r':
                    continue;
            }

            lineLength++;
            if (lineLength > MaxLineLength)
            {
                return new SuiteValidationError(
                    VfxCodeCatalogue.SuiteLineTooLong,
                    null,
                    $"The suite contains a line longer than the {MaxLineLength:N0}-character " +
                    "per-line limit. An over-long single line — most often a huge unbroken mapping " +
                    "key — can drive the YAML tokeniser pathological, so it is rejected before the " +
                    "suite is parsed. Break the content across multiple lines. For a long " +
                    "whitespace-insignificant value (inline JSON or prose) use a block scalar (| or " +
                    ">). For an opaque unbreakable token (base64, a JWT, a hash, a signed URL) do NOT " +
                    "use a block scalar — it would alter the value — but a double-quoted scalar with " +
                    "backslash line-continuation (key: \"AAAA\\<newline> BBBB\") reassembles to the " +
                    "exact original value.",
                    null,
                    null);
            }
        }

        return null;
    }

    public static SuiteValidationError? CheckNestingDepth(string yamlText)
    {
        var depth = ComputeMaxNestingDepth(yamlText);
        if (depth <= MaxNestingDepth)
        {
            return null;
        }

        return new SuiteValidationError(
            VfxCodeCatalogue.SuiteNestingTooDeep,
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
            VfxCodeCatalogue.SuiteAliasLimitExceeded,
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
