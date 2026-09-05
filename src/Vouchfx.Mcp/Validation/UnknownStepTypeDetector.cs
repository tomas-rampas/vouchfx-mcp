using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The ONE unknown-step-type detector, shared by <c>validate_suite</c>'s two channels: the schema
/// pass's cross-check (<see cref="SuiteValidator"/>) and the semantic pass's enriched restatement
/// (<c>Validation/Semantics/UnknownStepTypeRule</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Extracted, not duplicated</b> — the sprint spec's instruction for VFX-D-1201 is to REUSE the
/// existing detection logic rather than write a second detector, and this type is where that reuse
/// lives. Both channels walk the same steps, apply the same
/// <see cref="StepTypeCatalogue.Find(string)"/> lookup, and derive the same instance path; what
/// differs is only what each one says about the result (see <see cref="SuggestClosest"/>).
/// </para>
/// <para>
/// <b>Why the cross-check exists at all</b> (moved here with the code it explains): a step whose
/// type matches none of the composed schema's <c>const</c> clauses satisfies every one of them
/// VACUOUSLY, so JSON Schema evaluation alone reports no error for it. The vocabulary has to be
/// checked separately, against the catalogue.
/// </para>
/// </remarks>
internal static class UnknownStepTypeDetector
{
    /// <summary>
    /// The longest <c>type</c> string <see cref="SuggestClosest"/> will do edit-distance work for.
    /// See that method's remarks for the measurement behind it.
    /// </summary>
    public const int MaxSuggestibleTypeLength = 128;

    /// <summary>One step whose <c>type</c> names nothing the engine's catalogue defines.</summary>
    /// <param name="StepIndex">The step's 0-based position in the document's <c>steps</c> array.</param>
    /// <param name="Type">The type string the step declared — <b>caller-supplied, unsanitised</b>.</param>
    public readonly record struct UnknownStepType(int StepIndex, string Type)
    {
        /// <summary>The JSON Pointer both channels locate this finding by.</summary>
        public string InstancePath => $"/steps/{StepIndex}/type";
    }

    /// <summary>
    /// Every step in <paramref name="root"/> whose <c>type</c> is a string the catalogue does not
    /// know, in document order.
    /// </summary>
    /// <remarks>
    /// A step that is not an object, has no <c>type</c>, or whose <c>type</c> is not a string
    /// contributes nothing: those are schema violations the schema pass reports, and a second,
    /// differently-worded complaint from here would be noise.
    /// </remarks>
    public static List<UnknownStepType> Detect(JsonElement root)
    {
        var found = new List<UnknownStepType>();

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.Object &&
                step.TryGetProperty("type", out var typeProperty) &&
                typeProperty.ValueKind == JsonValueKind.String)
            {
                var type = typeProperty.GetString()!;
                if (StepTypeCatalogue.Find(type) is null)
                {
                    found.Add(new UnknownStepType(index, type));
                }
            }

            index++;
        }

        return found;
    }

    /// <summary>
    /// The catalogue entry closest to <paramref name="type"/> by Levenshtein edit distance — spec
    /// §5.5's own instruction for VFX-D-1201 ("suggest closest by Levenshtein").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No distance threshold, deliberately.</b> The sprint spec's first Gherkin scenario uses
    /// <c>mq-expect.nonexistent-provider</c> — 16 edits away from its nearest real neighbour — and
    /// requires a suggestion for it, so any cutoff tight enough to suppress a nonsense input would
    /// also suppress the case the story is specified against. The message wording carries the
    /// hedge instead ("the closest known type by edit distance"), which is true of every input; a
    /// silent threshold would have made the message's confidence depend on a number nobody sees.
    /// </para>
    /// <para>
    /// Ties break on the catalogue's own ordering, which <see cref="StepTypeCatalogue.All"/> sorts
    /// by dotted type name — so the suggestion is deterministic across runs and machines rather
    /// than dependent on enumeration luck.
    /// </para>
    /// <para>
    /// <b>A LENGTH bail-out, and it is a denial-of-service fix rather than a quality one.</b>
    /// <see cref="Distance"/> is O(|a|×|b|) and the catalogue has 25 entries, so the work here is
    /// 25 × |type| × |catalogue entry| — linear in the caller's input, which is the property
    /// <see cref="Distance"/>'s own remarks claim. It is linear with a large constant, and a
    /// YAML alias can hand the parser a <c>type</c> string far larger than the file that declared
    /// it: a 4.4&#160;MB suite whose every step's <c>type</c> is a YAML alias of one 4.4&#160;MB
    /// scalar measured <b>48.0 seconds</b> end to end (down to <b>1.9 s</b> with the bail-out)
    /// against the validation worker's 10-second wall clock, surfacing as
    /// VFX-E-1150 (a killed worker) rather than as a slow rule. The amplifier is
    /// <see cref="YamlSafetyGuard.MaxAliasCount"/>, which is deliberately NOT tightened: it is a
    /// parity-sensitive number the engine shares, and narrowing it to fix a suggestion heuristic
    /// would change what this server accepts. The bail-out is here instead, where it costs nothing:
    /// a real <c>family.provider</c> is under 30 characters, the catalogue's longest entry is
    /// shorter still, and the rendered identifier in the message caps at 64 anyway — so
    /// <see cref="MaxSuggestibleTypeLength"/> characters is far past any input a suggestion could
    /// help with. Past it there is no suggestion, and VFX-D-1201 is still reported without one (the
    /// message has a no-suggestion form for exactly this).
    /// </para>
    /// </remarks>
    public static string? SuggestClosest(string type)
    {
        if (type.Length > MaxSuggestibleTypeLength)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in StepTypeCatalogue.All)
        {
            var distance = Distance(type, candidate.Type);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate.Type;
            }
        }

        return best;
    }

    /// <summary>
    /// Levenshtein edit distance between <paramref name="a"/> and <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rolling rows rather than a full matrix: both operands here are short (a step type, and a
    /// catalogue entry), but the left one is CALLER-SUPPLIED suite content bounded only by
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>, and a full <c>m×n</c> matrix against a 2 MB
    /// "type" string would allocate proportionally to the product.
    /// </para>
    /// <para>
    /// <b>The memory is O(|b|), not O(min(m,n))</b> — the rows are sized off <paramref name="b"/>
    /// alone, with no swap to put the shorter operand there. That is fine, and it is fine for a
    /// reason worth stating rather than a coincidence: every caller passes a catalogue entry as
    /// <paramref name="b"/> and the caller's own text as <paramref name="a"/>, so the two rows are
    /// bounded by the longest step type the engine defines (tens of characters) no matter how large
    /// the input is. TIME is still O(|a|×|b|), which is what
    /// <see cref="SuggestClosest"/>'s length bail-out bounds.
    /// </para>
    /// </remarks>
    internal static int Distance(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;

                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
