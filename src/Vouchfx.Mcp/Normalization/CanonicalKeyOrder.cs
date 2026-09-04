using System.Text.Json;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Normalization;

/// <summary>
/// The canonical order <see cref="SuiteNormalizer"/> writes a mapping's keys in, derived from the
/// vendored composed schema's own <c>properties</c> declarations rather than from a hand-written
/// list (US-S2-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the schema and not an opinion.</b> The engine's schema already states, for every object
/// shape in the language, which fields exist and in what order its authors chose to declare them —
/// <c>metadata, environment, variables, steps</c> at the root; <c>id, type, description, capture,
/// verifyMode, timeout, continueOnFailure</c> for a step; <c>target, method, path, headers, body,
/// expect</c> for an <c>http.rest</c> step specifically. That is a better canonical order than
/// anything invented here, it is drift-gated against the engine at the pinned commit (see
/// <c>vendored/README.md</c>), and it means advancing <c>ENGINE_PIN</c> updates this ordering for
/// free instead of leaving a stale second copy behind.
/// </para>
/// <para>
/// <b>Group selection, not a schema WALK.</b> Knowing which schema subtree governs a given mapping
/// would mean walking the document and the schema in lockstep, through <c>$ref</c>, <c>$defs</c>,
/// <c>allOf</c> and the <c>if</c>/<c>then</c> discriminators that <c>SuiteValidator</c> spends five
/// noise-suppression passes taming. This type does something far cheaper: it collects every
/// <c>properties</c> object in the schema as a candidate GROUP, and for each mapping picks the group
/// sharing the most key names with it.
/// </para>
/// <para>
/// <b>The cost of a wrong guess is NOT harmless, which is why two guards bound it.</b> The original
/// version of this type claimed that a mispicked group merely produced "a slightly different, still
/// deterministic order". That was wrong, and measurably so: a mapping of the author's OWN data whose
/// keys happen to collide with schema field names was silently rewritten. Measured before the
/// guards — a request's <c>headers: {zebra, id, alpha, type, name}</c> came back as
/// <c>{id, type, zebra, alpha, name}</c> because <c>id</c> and <c>type</c> are step fields; a
/// <c>services</c> map came back reordered because one service was named <c>image</c>; a JSON
/// <c>body</c> came back reordered because one of its properties was named <c>target</c>. Reordering
/// an author's headers or JSON body is not a formatting choice — for a JSON body it can change what
/// the request means to the server receiving it. Both guards below exist to make that impossible:
/// </para>
/// <list type="number">
/// <item><description><b>A STRONG MAJORITY of the mapping's keys must be ones the winning group
/// declares</b> — <c>shared * 2 &gt; keys.Count</c>. A group that describes fewer than half a
/// mapping's keys is not describing that mapping; it collided with it. This is what turns the three
/// measured cases above into no-ops (headers: 2 of 5; services: 1 of 3; body: 1 of 3) while leaving
/// every genuine shape untouched (a root mapping matches 4 of 4, a metadata block 3 of 3, an
/// <c>http.rest</c> step 5 of 7).</description></item>
/// <item><description><b>A mapping reached through a free-form container key is never reordered at
/// all</b>, whatever its keys happen to be — see <see cref="IsAuthorDataContainer"/>. The majority
/// test alone would still reorder a two-key <c>headers: {id, type}</c>, and a container the schema
/// itself declares as free-form is a place where the schema has said, in its own words, that the key
/// names are the author's to choose.</description></item>
/// </list>
/// <para>
/// <b>A selected group is ranked together with its ANCESTORS, outermost first</b>, and that detail
/// is what makes the output read correctly rather than merely deterministically. A JSON Schema
/// <c>properties</c> object nested inside another one is a REFINEMENT of it: the <c>http.rest</c>
/// branch at <c>$defs/step/allOf/*/then</c> adds <c>target/method/path/…</c> to the fields
/// <c>$defs/step</c> already declares. Ranking by the best-matching group ALONE would win that match
/// on four keys and then emit <c>target, method, path, headers, id, type</c> — pushing a step's
/// identity to the bottom, which is exactly backwards from how anyone writes one. Ranking the whole
/// prefix chain, outermost to innermost, yields <c>id, type, target, method, path, headers</c>: the
/// general fields first, the type-specific ones after, which is the order the schema itself is
/// structured in.
/// </para>
/// <para>
/// <b>Ties in group selection are broken deterministically</b> — most shared keys, then the SMALLEST
/// group (the more specific match), then the earliest in schema document order — because idempotence
/// depends on this function being total and stable, not merely reasonable.
/// </para>
/// <para>
/// <b>Keys no group in the chain declares keep their SOURCE order, appended after the ones that
/// do.</b> They are the author's own data and the schema has no opinion about them, so neither does
/// this. Alphabetising them would be just as deterministic and just as idempotent, and would scramble
/// authored content for no canonical gain.
/// </para>
/// </remarks>
internal static class CanonicalKeyOrder
{
    /// <summary>
    /// One <c>properties</c> object from the composed schema.
    /// </summary>
    /// <param name="Path">
    /// The slash-joined location of the schema object DECLARING it (e.g. <c>/$defs/step</c>,
    /// <c>/$defs/step/allOf/7/then</c>), used only to establish the ancestor chain below. The root
    /// schema's own path is the empty string.
    /// </param>
    /// <param name="Properties">Its property names mapped to their declaration index.</param>
    /// <param name="ChainRanks">
    /// The merged rank table for this group and every ancestor of it, ranked outermost-first — the
    /// table <see cref="Order"/> actually sorts by. Precomputed once per group at start-up rather
    /// than rebuilt per mapping.
    /// </param>
    private sealed record PropertyGroup(
        string Path,
        IReadOnlyDictionary<string, int> Properties,
        IReadOnlyDictionary<string, int> ChainRanks);

    private static readonly (PropertyGroup[] Groups, HashSet<string> AuthorDataContainers) Schema = BuildSchemaModel();

    private static PropertyGroup[] Groups => Schema.Groups;

    /// <summary>
    /// For each property name, the indices of the <see cref="Groups"/> declaring it. Lets
    /// <see cref="Order"/> consider only groups that could possibly match, rather than scanning all
    /// ~150 of them per mapping — the whole document is walked once per normalization, and a 5 MB
    /// suite has a great many mappings.
    /// </summary>
    private static readonly Dictionary<string, int[]> GroupsByPropertyName = IndexGroups(Groups);

    /// <summary>
    /// Whether <paramref name="keyName"/> is a key the composed schema declares as a FREE-FORM
    /// container — a place whose child keys are the author's to name, so a mapping found under it is
    /// author data and <see cref="SuiteNormalizer"/> must not reorder it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the schema, not listed by hand.</b> A declared property counts as free-form
    /// when its subschema declares no <c>properties</c> and no <c>$ref</c> of its own — so the schema
    /// states no shape for it — and its <c>type</c>, if it states one, permits an object. A NAME is
    /// treated as a free-form container when EVERY declaration of that name in the schema is
    /// free-form; a name that is free-form in one place and shaped in another (measured:
    /// <c>metadata</c>, which is <c>$ref</c>-shaped at the root and a free-form attribute map inside
    /// a messaging step; <c>expect</c>, likewise) is left to the majority test instead, which is
    /// precisely the case that test is for.
    /// </para>
    /// <para>
    /// Measured against the pinned <c>vendored/composed-schema.v1.json</c>, the derivation yields 19
    /// names: <c>attributes, body, capture, dependencies, document, env, expectProperties, headers,
    /// item, json, labels, parameters, properties, record, row, schemaVersion, seed, services,
    /// variables</c>. That covers every container a review of this code named — with two honest
    /// corrections to the list a human would have written: <c>query</c> and <c>params</c> are NOT in
    /// it, because this schema declares <c>query</c> as a string and declares no <c>params</c> at
    /// all, so no mapping can legitimately appear under either. <c>SchemaDerivedKeyOrderTests</c>
    /// asserts the derived set against the vendored file so advancing <c>ENGINE_PIN</c> updates it
    /// rather than leaving this paragraph stale.
    /// </para>
    /// <para>
    /// A subschema that is a JSON boolean rather than an object (JSON Schema's <c>true</c>/<c>false</c>
    /// forms) is IGNORED by the fold, not counted against the name. Measured: the vendored schema
    /// declares <c>item: false</c> — forbidding the property in that branch — alongside a free-form
    /// <c>item</c> attribute map in another, and treating the boolean as a shape opinion would have
    /// silently dropped a real container from the derived set.
    /// </para>
    /// </remarks>
    public static bool IsAuthorDataContainer(string keyName)
    {
        ArgumentNullException.ThrowIfNull(keyName);

        return Schema.AuthorDataContainers.Contains(keyName);
    }

    /// <summary>
    /// Returns <paramref name="sourceOrderKeys"/> reordered into canonical order — schema-declared
    /// keys first, in the best-matching group's chain order, then the rest in source order — or the
    /// keys unchanged when no group describes the mapping well enough to be trusted with it.
    /// </summary>
    /// <remarks>
    /// A pure function of the key list alone: given the same keys it returns the same order every
    /// time, which is precisely what makes <c>normalize(normalize(x)) == normalize(x)</c> hold.
    /// </remarks>
    public static IReadOnlyList<string> Order(IReadOnlyList<string> sourceOrderKeys)
    {
        ArgumentNullException.ThrowIfNull(sourceOrderKeys);

        if (sourceOrderKeys.Count < 2)
        {
            return sourceOrderKeys;
        }

        var group = SelectBestGroup(sourceOrderKeys, out var shared);

        // The strong-majority guard — see this type's remarks for the three measured cases it turns
        // back into no-ops. Strictly MORE than half, so a group describing exactly half a mapping
        // (which is a coin toss about whose data it is) does not qualify.
        if (group is null || shared * 2 <= sourceOrderKeys.Count)
        {
            return sourceOrderKeys;
        }

        // OrderBy is documented as a STABLE sort, so keys sharing a rank — every key the chain does
        // not declare, all of which rank int.MaxValue — keep their source order without needing a
        // second ordering term.
        return
        [
            .. sourceOrderKeys.OrderBy(
                key => group.ChainRanks.TryGetValue(key, out var rank) ? rank : int.MaxValue),
        ];
    }

    /// <summary>
    /// The schema <c>properties</c> group that best describes a mapping with these keys, or
    /// <see langword="null"/> when no group declares any of them.
    /// </summary>
    /// <param name="keys">The mapping's keys, in source order.</param>
    /// <param name="shared">How many of <paramref name="keys"/> the winning group declares.</param>
    /// <remarks>
    /// Matching is against a group's OWN properties, never its chain: a chain includes the root
    /// schema, whose four keys would otherwise make every group look like a partial match for every
    /// mapping and flatten the selection.
    /// </remarks>
    private static PropertyGroup? SelectBestGroup(IReadOnlyList<string> keys, out int shared)
    {
        shared = 0;

        var candidates = new Dictionary<int, int>();
        foreach (var key in keys)
        {
            if (!GroupsByPropertyName.TryGetValue(key, out var groupIndices))
            {
                continue;
            }

            foreach (var groupIndex in groupIndices)
            {
                candidates[groupIndex] = candidates.TryGetValue(groupIndex, out var count) ? count + 1 : 1;
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestIndex = -1;
        var bestShared = 0;
        var bestSize = 0;

        foreach (var (groupIndex, sharedWithGroup) in candidates)
        {
            var size = Groups[groupIndex].Properties.Count;

            // Most shared keys wins; then the smaller (more specific) group; then the earlier one in
            // schema document order. Every comparison is total, so the winner does not depend on the
            // dictionary's enumeration order.
            var better = sharedWithGroup > bestShared
                || (sharedWithGroup == bestShared && size < bestSize)
                || (sharedWithGroup == bestShared && size == bestSize && groupIndex < bestIndex);

            if (bestIndex < 0 || better)
            {
                bestIndex = groupIndex;
                bestShared = sharedWithGroup;
                bestSize = size;
            }
        }

        shared = bestShared;
        return Groups[bestIndex];
    }

    /// <summary>
    /// Collects every <c>properties</c> object in the composed schema, breadth-first from the root,
    /// precomputes each one's ancestor-chain rank table, and derives the free-form container names
    /// <see cref="IsAuthorDataContainer"/> answers from.
    /// </summary>
    /// <remarks>
    /// Breadth-first rather than depth-first so the ROOT's own properties are found first and the
    /// language's top-level shape is group 0 — which only matters for the earliest-group tie-break,
    /// but a tie-break that is stated must actually be the one implemented. One walk produces both
    /// outputs because both are reading the same <c>properties</c> declarations for different
    /// questions, and two walks would be two chances to disagree about which declarations exist.
    /// </remarks>
    private static (PropertyGroup[] Groups, HashSet<string> AuthorDataContainers) BuildSchemaModel()
    {
        using var schema = VendoredComposedSchema.Parse();

        var paths = new List<string>();
        var declarations = new List<IReadOnlyDictionary<string, int>>();

        // name -> "every declaration of it seen so far is free-form". A single shaped declaration
        // disqualifies the name permanently, which is why this is an AND-fold rather than a set.
        var freeFormByName = new Dictionary<string, bool>(StringComparer.Ordinal);

        var queue = new Queue<(JsonElement Element, string Path)>();
        queue.Enqueue((schema.RootElement, string.Empty));

        while (queue.Count > 0)
        {
            var (element, path) = queue.Dequeue();

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("properties", out var properties)
                        && properties.ValueKind == JsonValueKind.Object)
                    {
                        var group = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var property in properties.EnumerateObject())
                        {
                            // A duplicate name inside one `properties` object is impossible in valid
                            // JSON-as-parsed (System.Text.Json keeps the last), but TryAdd rather than
                            // an indexer keeps the FIRST declaration's index if one ever appeared.
                            group.TryAdd(property.Name, group.Count);

                            // A BOOLEAN subschema is skipped rather than folded in. `false` FORBIDS
                            // the property in that branch; it states nothing about the shape of an
                            // object under the name elsewhere, so counting it as "shaped" would
                            // disqualify a genuine container. Measured: the composed schema declares
                            // `item: false` in one branch and a free-form attribute map in another.
                            if (property.Value.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var free = DeclaresAFreeFormObject(property.Value);
                            freeFormByName[property.Name] =
                                free && freeFormByName.GetValueOrDefault(property.Name, true);
                        }

                        if (group.Count > 0)
                        {
                            paths.Add(path);
                            declarations.Add(group);
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        queue.Enqueue((property.Value, path + "/" + property.Name));
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        queue.Enqueue((item, path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        index++;
                    }

                    break;

                default:
                    break;
            }
        }

        // A schema carrying no `properties` object at all would silently disable canonical ordering
        // and leave every mapping in source order — a change nothing else in this repo would notice.
        // It is a packaging fault of the same class as a missing version marker, so it fails loudly
        // and eagerly, in this static initialiser, exactly as VendoredSchemaVersion's read does.
        if (declarations.Count == 0)
        {
            throw new InvalidOperationException(
                "The embedded composed schema declares no 'properties' object, so no canonical key "
                + "order can be derived from it.");
        }

        var groups = new PropertyGroup[declarations.Count];
        for (var i = 0; i < declarations.Count; i++)
        {
            groups[i] = new PropertyGroup(paths[i], declarations[i], BuildChainRanks(i, paths, declarations));
        }

        var authorDataContainers = freeFormByName
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        return (groups, authorDataContainers);
    }

    /// <summary>
    /// Whether <paramref name="subschema"/> states no shape for the object it governs — no
    /// <c>properties</c>, no <c>$ref</c>, and a <c>type</c> (if present) that permits an object.
    /// </summary>
    private static bool DeclaresAFreeFormObject(JsonElement subschema)
    {
        if (subschema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (subschema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            return false;
        }

        if (subschema.TryGetProperty("$ref", out _))
        {
            return false;
        }

        if (!subschema.TryGetProperty("type", out var type))
        {
            // No `type` at all is the widest schema there is — `body` is declared exactly this way —
            // so an object is permitted and nothing constrains its keys.
            return true;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.ValueEquals("object"),
            JsonValueKind.Array => type.EnumerateArray().Any(
                entry => entry.ValueKind == JsonValueKind.String && entry.ValueEquals("object")),
            _ => false,
        };
    }

    /// <summary>
    /// Merges group <paramref name="groupIndex"/> and every ancestor of it into one rank table,
    /// outermost first — see this type's remarks for why the chain, and not the group alone, is what
    /// <see cref="Order"/> sorts by.
    /// </summary>
    /// <remarks>
    /// Ancestry is a PATH-SEGMENT prefix test, never a raw string prefix: <c>/$defs/step</c> must not
    /// be treated as an ancestor of a hypothetical <c>/$defs/stepGroup</c>. Ancestors are visited
    /// shortest-path first, and <c>TryAdd</c> keeps the OUTERMOST declaration's rank when the same
    /// name is declared at two depths — the general field's position wins over the refinement's,
    /// which is the whole point of the ordering.
    /// </remarks>
    private static Dictionary<string, int> BuildChainRanks(
        int groupIndex,
        IReadOnlyList<string> paths,
        IReadOnlyList<IReadOnlyDictionary<string, int>> declarations)
    {
        var ownPath = paths[groupIndex];

        var chain = Enumerable.Range(0, declarations.Count)
            .Where(candidate => candidate == groupIndex || IsAncestorPath(paths[candidate], ownPath))
            .OrderBy(candidate => paths[candidate].Length)
            .ThenBy(candidate => candidate);

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in chain)
        {
            foreach (var name in declarations[candidate].OrderBy(pair => pair.Value).Select(pair => pair.Key))
            {
                ranks.TryAdd(name, ranks.Count);
            }
        }

        return ranks;
    }

    private static bool IsAncestorPath(string candidate, string path) =>
        candidate.Length < path.Length
        && (candidate.Length == 0 || path.StartsWith(candidate + "/", StringComparison.Ordinal));

    private static Dictionary<string, int[]> IndexGroups(PropertyGroup[] groups)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            foreach (var name in groups[groupIndex].Properties.Keys)
            {
                if (!index.TryGetValue(name, out var indices))
                {
                    indices = [];
                    index[name] = indices;
                }

                indices.Add(groupIndex);
            }
        }

        return index.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }
}
