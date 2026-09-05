using Vouchfx.Mcp.Validation;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Normalization;

/// <summary>
/// Renders an already-parsed suite document in this server's canonical form — stable key order,
/// consistent quoting, one block-style layout — as the text <c>normalize_suite</c> hands back to the
/// host (US-S2-04). <b>Nothing here writes a file</b>; the host decides whether and where to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately <c>internal</c>.</b> <see cref="Normalise"/> MUTATES the graph it is handed and
/// assumes that graph came from <see cref="YamlLineResolver.TryParseYamlRoot"/> over text that has
/// already cleared <see cref="YamlSafetyGuard"/> — two preconditions a public API cannot state and
/// cannot enforce. Its sibling <see cref="CanonicalKeyOrder"/> is internal for the same reason, and
/// the test assembly reaches both through <c>InternalsVisibleTo</c>. The supported entry point is
/// <c>SuiteValidator.NormaliseYaml</c>, which owns both preconditions.
/// </para>
/// <para>
/// <b>THE COMMENT-PRESERVATION DECISION (spec open decision #2), closed as outcome (b): comments are
/// DROPPED, and normalization therefore ships OFF by default behind
/// <c>normalize_suite</c>'s opt-in <c>normalize</c> flag, with the loss stated on the RESULT
/// (<see cref="SuiteNormalization.CommentsDropped"/>) as well as in the tool's description.</b> The
/// story required this be evaluated on the PINNED YamlDotNet (18.1.0 — fleet-pinned to the engine's;
/// not bumpable here) rather than assumed. It was, by probe, and outcome (a) was rejected on three
/// measured findings:
/// </para>
/// <list type="number">
/// <item><description><b>The only structural DOM YamlDotNet ships cannot carry comments at all.</b>
/// A <c>Parser</c> built over <c>new Scanner(reader, skipComments: false)</c> does emit
/// <c>Comment</c> events, with a position and an <c>IsInline</c> flag — but handing that parser to
/// <c>YamlStream.Load</c> throws <c>ArgumentException: The current event is of an unsupported type</c>.
/// Comment preservation therefore cannot be a setting on the existing pipeline: it requires a
/// bespoke event-to-DOM builder re-implementing anchors, aliases, merge keys, tags and styles —
/// roughly the size of <c>RepresentationModel</c> itself — before the first comment could be
/// attached to anything.</description></item>
/// <item><description><b>The emitter silently CORRUPTS documents when an inline comment is emitted in
/// the position the parser reports it in.</b> Measured, and the position DEPENDS ON WHAT THE COMMENT
/// TRAILS: for <c>k: v # c</c> the <c>Comment</c> event arrives AFTER the value scalar, but for a
/// comment trailing the KEY (<c>k: # c</c>, value on the following line) it arrives BETWEEN the key
/// and the value. Replaying the second shape in event order produces <c>k: # c v</c>, which
/// re-parses with <c>k</c> mapped to the EMPTY string — the value is swallowed into the comment. A
/// comment-preserving normalizer would therefore have to decide, per comment, which side of a node
/// the parser meant. The failure mode of getting one wrong is not ugly output; it is a step losing a
/// field in text the host is invited to write over the author's file.</description></item>
/// <item><description><b>Comment-to-node association survives reordering only by guesswork.</b>
/// Measured on the same probe: a comment written on its own line inside a sequence's indentation
/// arrives BEFORE that sequence's <c>SequenceStart</c>, so "the node this comment belongs to" is
/// already a heuristic before a single key is reordered — and reordering keys is the entire point of
/// this type.</description></item>
/// </list>
/// <para>
/// Spec §8.4 anticipates exactly this outcome and states the fallback: document that normalization
/// drops comments and default it to false. That is what shipped — see <c>NormalizeSuiteTool</c>'s
/// description, which says so to the host in the words the host reads. If a future YamlDotNet gains
/// a comment-carrying representation model, outcome (a) becomes available and this decision should
/// be re-taken; until then, silently discarding an author's comments on an opt-OUT basis would be
/// the one behaviour this repository's read-only posture cannot justify.
/// </para>
/// <para>
/// <b>THE CANONICAL FORM.</b> Every rule below is a total, deterministic function of the parsed
/// document, which is what makes <c>normalize(normalize(x)) == normalize(x)</c> hold byte-for-byte
/// (asserted across the whole fixture corpus in <c>SuiteNormalizerTests</c>):
/// </para>
/// <list type="number">
/// <item><description><b>Key order</b> — within each mapping, keys the vendored composed schema
/// declares come first, in that schema's own declared order; keys it does not declare follow, in
/// their original source order. Mappings the schema has no shape opinion about — the free-form
/// author-data containers, and any mapping the best-matching schema group describes only weakly —
/// are left in source order entirely. See <see cref="CanonicalKeyOrder"/>, which owns both
/// exclusions.</description></item>
/// <item><description><b>Sequence order is never touched.</b> A suite's step order is its
/// meaning.</description></item>
/// <item><description><b>Quoting</b> — a single-quoted scalar is rewritten double-quoted; every
/// other scalar style is left as authored, with the one exception below. Quoted is never unquoted,
/// because that boundary carries the value's resolved TYPE: <c>'yes'</c> written plain becomes a
/// boolean and <c>"007"</c> written plain becomes the integer 7. Normalising the CHOICE of quote
/// character is a formatting decision; normalising quoted-vs-plain would be a semantic one. The
/// exception is not a choice this type makes but one the emitter forces: a scalar the emitter cannot
/// render in the style it was authored in is written double-quoted rather than left to escalate. Two
/// measured cases — an EMPTY scalar carrying an explicit tag (<c>!!str</c> with no value), which the
/// emitter would otherwise render single-quoted, re-entering this rule on the next pass and breaking
/// byte idempotence; and a plain scalar containing characters the emitter will not write plain
/// (measured: non-BMP text such as an emoji comes back as a double-quoted <c>\U…</c> escape). In
/// both cases the VALUE is unchanged — only how it is spelled.</description></item>
/// <item><description><b>Layout</b> — every non-empty mapping and sequence is emitted in block
/// style, two-space indented, with block sequences indented under their key. Empty collections keep
/// their compact <c>{}</c>/<c>[]</c> form, which is the only thing block style can render them
/// as anyway.</description></item>
/// <item><description><b>No folding</b> — the emitter's width is unbounded, so a long scalar is
/// never wrapped onto a second line at a column that depends on how deeply it happened to be
/// nested.</description></item>
/// <item><description><b>Anchors and aliases are preserved as authored</b>, never expanded — an
/// expansion would change the document's size and, on a self-referential graph, its termination.
/// Note that an anchor travels with its NODE, not with its key: when reordering moves the aliased
/// node's first occurrence, the <c>&amp;name</c> definition moves with it and the <c>*name</c>
/// reference follows (measured — see <c>SuiteNormalizerTests</c>). The graph is
/// identical.</description></item>
/// <item><description><b>LF line endings, one trailing newline, no explicit <c>...</c> document-end
/// marker</b> — so the same suite normalises to the same bytes on Windows and on Linux CI, which
/// idempotence as a BYTE equality requires.</description></item>
/// </list>
/// <para>
/// <b>THE EMISSION GATE, and why the canonical text is verified rather than trusted.</b> Emitting a
/// parsed graph is not a total operation: YamlDotNet's emitter has shapes it cannot render back into
/// text that means the same thing. The one measured here is an ALIAS IN KEY POSITION — a legal,
/// schema-VALID suite of the form
/// <code>
/// body:
///   anchor: &amp;k v
///   nested:
///     *k : value
/// </code>
/// emitted a nested mapping whose only key is <c>*k:</c>, which does not re-parse at all
/// (<c>SemanticErrorException: did not find expected key</c>); a variant with an alias to a MAPPING
/// in key position re-parsed to a different graph. Before this gate existed, that text was returned
/// to the host as <c>normalizedYaml</c> next to <c>"valid": true</c> — an invitation to overwrite a
/// good file with garbage. So the emitted text is now PROVED before it is returned: it must re-parse,
/// and the graph it re-parses to must equal the untouched snapshot of the input
/// (<see cref="NormaliseText"/> parses its input twice for exactly this — one graph to mutate, one to
/// compare against). Failing either half returns no text at all plus the reason, which
/// <see cref="SuiteNormalization"/> carries to the host. Refusing is always available and always
/// safe; the suite is unchanged and the verdict is unaffected.
/// </para>
/// <para>
/// <b>Why the comparison uses <see cref="YamlNode"/>'s own value equality</b> and not something
/// stricter: it is exactly blind to the two things this type deliberately changes and sensitive to
/// everything it must not. Measured on the pinned library — mapping ORDER is not part of it (a
/// reordered mapping compares equal), scalar STYLE is not part of it (a re-quoted scalar compares
/// equal), while a changed scalar value, a dropped key, an expanded alias, or a changed TAG all
/// break it. The corpus test applies a strictly stronger comparison than this gate does, for the
/// separate purpose of catching a quoted↔plain retype that would be meaning-preserving here but is
/// not a change this type is allowed to make.
/// </para>
/// <para>
/// <b>Parsing the emitter's own output is safe from the forward-alias hazard</b> that makes parsing
/// arbitrary text risky, but only because of an EMITTER property rather than a guard: YamlDotNet
/// writes an anchor at the node's first emission, so every <c>*name</c> in text this type produced is
/// preceded by its <c>&amp;name</c>. This is worth stating because it is the reason no separate
/// safety check wraps the gate's re-parse.
/// </para>
/// <para>
/// <b>Why this parses its own copy of the text</b> (see <see cref="NormaliseText"/>) instead of
/// sharing <c>SuiteValidator</c>'s already-parsed document: the rules above MUTATE the node graph —
/// child order and scalar styles — and the graph the validation passes hold is their line/column
/// authority (<see cref="YamlLineResolver"/>). Handing a reordered graph to a location resolver is a
/// defect waiting for the first rule that reads the document in order. The parses are bounded (three
/// per normalize call — the graph to mutate, the snapshot to compare against, and the emitted text —
/// of input that has already cleared every <see cref="YamlSafetyGuard"/> check), not per-error, so
/// they are not the quadratic cost the pipeline's single-parse discipline exists to prevent.
/// </para>
/// <para>
/// <b>Measured cost near the input cap, and what it is NOT caused by.</b> Timed on one developer
/// host (Release build, current engine pin, 2026-09-05), level <c>full</c>, invoking the worker
/// directly (so no client timeout applies), on uniform <c>http.rest</c> suites (medians):
/// <list type="bullet">
/// <item><description>0.5 MB — 2.1 s validate, 2.6 s with normalization;</description></item>
/// <item><description>1.5 MB — 5.2 s, 5.7 s;</description></item>
/// <item><description>2.0 MB (at <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, the cap) —
/// 6.5 s, 7.0 s (slowest of three normalize runs 7.24 s).</description></item>
/// </list>
/// So <b>normalization is a ~0.5 s / ~10% surcharge, not the tipping point</b>: a worst-case suite
/// AT the cap completes BOTH the validate pass and the slower normalize path within
/// <c>ValidationWorkerClient.DefaultTimeout</c> (10 s), ~3 s under the budget. The 2 MB cap
/// (<see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>) is deliberately set so that an ADMITTED suite is
/// expected to finish; VFX-E-1150 is now reachable essentially only through transient host load / CPU
/// contention, not suite size. The hardened wall clock is deliberately not relaxed for this tool — it
/// exists for uninterruptible parser spins, and widening it would trade a real defence for a marginal
/// one; the reconciliation lives in the size cap instead (see <c>YamlSafetyGuard</c>). The practical
/// ceiling is documented to callers too (see <c>NormalizeSuiteTool</c>'s description and
/// <c>docs/tools-and-resources.md</c>).
/// </para>
/// </remarks>
internal static class SuiteNormalizer
{
    /// <summary>
    /// Emitter configuration for the canonical form: two-space indent, block sequences indented
    /// under their key, LF line endings, and an effectively unbounded line width so nothing folds.
    /// </summary>
    /// <remarks>
    /// <see cref="EmitterSettings.WithNewLine"/> is not cosmetic: a <see cref="StringWriter"/> writes
    /// <see cref="Environment.NewLine"/> by default, which would make the same suite normalise to
    /// CRLF bytes on Windows and LF bytes on Linux — and idempotence is asserted, and promised to
    /// hosts, as a BYTE equality.
    /// </remarks>
    private static readonly EmitterSettings CanonicalEmitterSettings = EmitterSettings.Default
        .WithBestWidth(int.MaxValue)
        .WithNewLine("\n")
        .WithIndentedSequences();

    /// <summary>
    /// Parses <paramref name="yamlText"/> and renders it in canonical form, or returns
    /// <see langword="null"/> when it is not a document this server can canonicalise (unparseable, or
    /// a root that is not a mapping — neither of which is a suite) or when the emission gate refused
    /// the text it produced.
    /// </summary>
    /// <param name="yamlText">The suite text, already cleared by <see cref="YamlSafetyGuard"/>.</param>
    /// <param name="refusedReason">
    /// <see langword="null"/> unless the gate refused — in which case one of
    /// <see cref="SuiteNormalization"/>'s two reason constants. "There was no document" is NOT a
    /// refusal and leaves this <see langword="null"/>: the validation channel already explains it.
    /// </param>
    /// <remarks>
    /// <b>The input is parsed TWICE, on purpose.</b> One graph is mutated and emitted; the other is
    /// the untouched semantic snapshot the emitted text is proved against. Sharing one graph would
    /// mean comparing the output with something this type had already rewritten, which proves
    /// nothing. The second parse is the cost of the gate and was measured as acceptable: it replaces
    /// the two speculative re-parses the document-end tidy-up used to perform, so the total parse
    /// count per call is unchanged at three.
    /// <para>
    /// The parses go through <see cref="YamlLineResolver.TryParseYamlRoot"/>, which already swallows
    /// every YamlDotNet parse failure into a <see langword="null"/> — so this method inherits
    /// <c>SuiteValidator</c>'s never-throws contract rather than restating a second try/catch that
    /// could drift from it.
    /// </para>
    /// </remarks>
    public static string? NormaliseText(string yamlText, out string? refusedReason)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        var mutable = YamlLineResolver.TryParseYamlRoot(yamlText);
        if (mutable is null)
        {
            refusedReason = null;
            return null;
        }

        return Normalise(mutable, YamlLineResolver.TryParseYamlRoot(yamlText), out refusedReason);
    }

    /// <summary>
    /// Renders <paramref name="root"/> in canonical form, or <see langword="null"/> when there is no
    /// mapping root to canonicalise or the emission gate refused the result.
    /// </summary>
    /// <param name="root">The graph to canonicalise. <b>Mutated</b> — see the remarks.</param>
    /// <param name="meaningSnapshot">
    /// An independent parse of the SAME text, never mutated, that the emitted text must compare equal
    /// to. Passing <see langword="null"/> weakens the gate to "the output must re-parse" and is only
    /// meaningful for a caller that has no text to snapshot.
    /// </param>
    /// <param name="refusedReason">See <see cref="NormaliseText"/>.</param>
    /// <remarks>
    /// <b>Mutates <paramref name="root"/></b> — child order and scalar styles — because the
    /// canonical form is expressed by re-emitting YamlDotNet's own representation model rather than
    /// by a hand-written event walker. Using the library's own serialiser is the deliberate choice:
    /// this text may be written over an author's file, and a bespoke emitter is new, untested code on
    /// exactly the path where a mistake is least recoverable. Callers must therefore own the graph
    /// they pass; <see cref="NormaliseText"/> owns its own, which is why it is the production entry
    /// point.
    /// <para>
    /// <b>Termination.</b> <see cref="Canonicalise"/> is guarded by a reference-identity visited set,
    /// so a graph in which an anchored node reaches itself is walked once, not forever. The emit and
    /// the gate's comparison are YamlDotNet's own, and both are bounded by that library's recursion
    /// limit, which raises <see cref="YamlException"/> rather than overflowing the stack — caught
    /// below and turned into a refusal. In production a self-referential document never gets this
    /// far: <c>SuiteValidator.NormaliseYaml</c> only calls in when the analysis produced a summary,
    /// and building that summary converts the document to JSON, which rejects a cyclic graph first
    /// (measured: VFX-D-1102, "too much recursion when traversing the object graph", with
    /// <c>normalizedYaml</c> null). That ordering is pinned by
    /// <c>SuiteNormalisationPipelineTests</c>.
    /// </para>
    /// </remarks>
    public static string? Normalise(
        YamlMappingNode? root, YamlMappingNode? meaningSnapshot, out string? refusedReason)
    {
        refusedReason = null;

        if (root is null)
        {
            return null;
        }

        try
        {
            // Reference equality, not YamlNode's value equality: two structurally identical mappings
            // are Equal but are separate nodes that both need canonicalising, while an ALIAS is the
            // same node reached twice and must be visited once — both to avoid pointless work and, on
            // a self-referential graph, to terminate at all.
            Canonicalise(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance), insideAuthorData: false);

            var text = TidyDocumentEnd(Emit(root));

            // ── The emission gate. Both halves, in this order, because the first is a precondition
            // of the second: text that does not parse has no graph to compare.
            //
            // Re-parsing EMITTER output is safe from the forward-alias hazard that makes parsing
            // arbitrary text risky, and only because of an emitter property rather than a guard:
            // YamlDotNet writes an anchor at its node's FIRST emission, so every *name here is
            // preceded by its &name. Nothing checks that; it is a fact about the writer whose output
            // this is.
            var reparsed = YamlLineResolver.TryParseYamlRoot(text);
            if (reparsed is null)
            {
                refusedReason = SuiteNormalization.CanonicalTextDidNotReParse;
                return null;
            }

            if (meaningSnapshot is not null && !meaningSnapshot.Equals(reparsed))
            {
                refusedReason = SuiteNormalization.CanonicalTextChangedTheDocument;
                return null;
            }

            return text;
        }
        catch (YamlException)
        {
            // The emitter, or YamlDotNet's own recursion guard inside the comparison. Either way the
            // canonical text is not available and saying so is the whole point of the gate — this
            // method's never-throws contract is what SuiteValidator.NormaliseYaml relies on.
            refusedReason = SuiteNormalization.CanonicalTextDidNotReParse;
            return null;
        }
    }

    private static string Emit(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        var writer = new StringWriter { NewLine = "\n" };
        stream.Save(new Emitter(writer, CanonicalEmitterSettings), assignAnchors: false);

        return writer.ToString();
    }

    /// <summary>
    /// Applies the ordering, quoting, and layout rules to <paramref name="node"/> and everything
    /// beneath it.
    /// </summary>
    /// <param name="node">The node to canonicalise.</param>
    /// <param name="visited">Reference-identity set of nodes already handled.</param>
    /// <param name="insideAuthorData">
    /// Whether <paramref name="node"/> was reached through a key the schema declares as a free-form
    /// container — request headers, a JSON body, environment variables, the services map. Once true
    /// it stays true for everything below, because everything below a free-form container is the
    /// author's own data too. Only key ORDERING is suppressed by it; quoting and layout are
    /// presentation and still apply. See <see cref="CanonicalKeyOrder.IsAuthorDataContainer"/> for how
    /// the set of such keys is derived.
    /// </param>
    private static void Canonicalise(YamlNode node, HashSet<YamlNode> visited, bool insideAuthorData)
    {
        if (!visited.Add(node))
        {
            return;
        }

        switch (node)
        {
            case YamlScalarNode scalar:
                if (scalar.Style == ScalarStyle.SingleQuoted || !CanBeEmittedInItsAuthoredStyle(scalar))
                {
                    scalar.Style = ScalarStyle.DoubleQuoted;
                }

                break;

            case YamlSequenceNode sequence:
                if (sequence.Children.Count > 0)
                {
                    sequence.Style = SequenceStyle.Block;
                }

                foreach (var item in sequence.Children)
                {
                    Canonicalise(item, visited, insideAuthorData);
                }

                break;

            case YamlMappingNode mapping:
                if (mapping.Children.Count > 0)
                {
                    mapping.Style = MappingStyle.Block;
                }

                if (!insideAuthorData)
                {
                    ReorderKeys(mapping);
                }

                foreach (var pair in mapping.Children)
                {
                    Canonicalise(pair.Key, visited, insideAuthorData);
                    Canonicalise(
                        pair.Value,
                        visited,
                        insideAuthorData
                            || (pair.Key is YamlScalarNode { Value: { } name }
                                && CanonicalKeyOrder.IsAuthorDataContainer(name)));
                }

                break;

            default:
                // YamlAliasNode: a placeholder YamlDotNet only produces for an alias it could not
                // resolve. There is nothing beneath it and no style to canonicalise.
                break;
        }
    }

    /// <summary>
    /// Whether the emitter will render <paramref name="scalar"/> in the style it was parsed in, or
    /// silently substitute another one.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ScalarStyle.Plain"/> is at risk, and only one shape of it, measured on the
    /// pinned library: an EMPTY value carrying an explicit tag. <c>name: !!str</c> parses as a plain
    /// empty scalar tagged <c>tag:yaml.org,2002:str</c>, but the emitter must write something after
    /// the tag and chooses <c>''</c> — a single-quoted scalar, which the rule above would rewrite on
    /// the next pass, breaking byte idempotence (measured: pass 1 <c>!!str ''</c>, pass 2
    /// <c>!!str ""</c>). Forcing the double quote here makes pass 1 already the fixpoint. An empty
    /// plain scalar with NO tag is untouched on purpose: it is the implicit null, and quoting it
    /// would retype it to the empty string.
    /// <para>
    /// Other escalations exist and are deliberately NOT predicted here — a plain scalar containing
    /// non-BMP characters comes back double-quoted with <c>\U…</c> escapes (measured). Those are
    /// already fixpoints, because the emitted style is one this method accepts; the corpus
    /// idempotence property is what would catch it if a future library version changed that.
    /// </para>
    /// </remarks>
    private static bool CanBeEmittedInItsAuthoredStyle(YamlScalarNode scalar) =>
        scalar.Style != ScalarStyle.Plain
        || !string.IsNullOrEmpty(scalar.Value)
        || scalar.Tag.IsEmpty;

    /// <summary>
    /// Rewrites <paramref name="mapping"/>'s children in <see cref="CanonicalKeyOrder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clear-and-re-add on the SAME mapping, re-inserting the ORIGINAL key and value node objects:
    /// reference identity is preserved for every child, so an anchored node reached through two
    /// parents is still one node afterwards and still emits as an anchor plus an alias rather than
    /// being expanded twice.
    /// </para>
    /// <para>
    /// Mappings whose keys are not all scalars are left completely alone. YAML permits a sequence or
    /// a mapping as a key; the schema declares no such shape, so there is no canonical order to
    /// impose and rendering one to a string to sort by would be inventing an ordering the schema
    /// never expressed.
    /// </para>
    /// </remarks>
    private static void ReorderKeys(YamlMappingNode mapping)
    {
        if (mapping.Children.Count < 2)
        {
            return;
        }

        var pairs = mapping.Children.ToList();
        if (pairs.Exists(pair => pair.Key is not YamlScalarNode { Value: not null }))
        {
            return;
        }

        var canonicalOrder = CanonicalKeyOrder.Order(
            [.. pairs.Select(pair => ((YamlScalarNode)pair.Key).Value!)]);

        var byKey = new Dictionary<string, List<KeyValuePair<YamlNode, YamlNode>>>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var key = ((YamlScalarNode)pair.Key).Value!;
            if (!byKey.TryGetValue(key, out var bucket))
            {
                bucket = [];
                byKey[key] = bucket;
            }

            bucket.Add(pair);
        }

        mapping.Children.Clear();
        foreach (var key in canonicalOrder)
        {
            // A bucket, not a single entry: two keys can share a NAME while carrying different TAGS
            // (`a:` and `!!str a:` are distinct YamlScalarNodes and both survive the parse), so a
            // plain dictionary lookup would silently drop one of them. Draining the bucket in order
            // preserves every pair. Measured: style alone does NOT produce this case — `a:` and
            // `"a":` in one mapping is a duplicate-key parse ERROR, because YamlNode equality ignores
            // style but not tags.
            var bucket = byKey[key];
            var pair = bucket[0];
            bucket.RemoveAt(0);

            mapping.Children.Add(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// Removes the explicit <c>...</c> document-end marker <c>YamlStream.Save</c> always writes, and
    /// normalises the trailing newline.
    /// </summary>
    /// <remarks>
    /// <b>A pure text operation, and it no longer verifies itself.</b> It used to re-parse both the
    /// marked and the stripped text and keep the marker if they disagreed — a local check that
    /// silently discarded a <see langword="null"/> parse, which is how the alias-in-key-position
    /// corruption reached the wire wearing a trailing <c>...</c>. The whole-document gate in
    /// <see cref="Normalise"/> subsumes it: whatever this returns is re-parsed and compared against
    /// the input snapshot before anything is handed back, so stripping the marker is proved with the
    /// rest of the emission rather than on its own. A bare <c>...</c> at column 0 also cannot be
    /// block-scalar content (block content is always indented past its key), so the strip is safe by
    /// argument as well — but the gate is what makes it safe by proof.
    /// </remarks>
    private static string TidyDocumentEnd(string emitted)
    {
        var trimmed = emitted.TrimEnd('\n');
        const string DocumentEndMarker = "\n...";

        return trimmed.EndsWith(DocumentEndMarker, StringComparison.Ordinal)
            ? trimmed[..^DocumentEndMarker.Length] + "\n"
            : trimmed + "\n";
    }
}
