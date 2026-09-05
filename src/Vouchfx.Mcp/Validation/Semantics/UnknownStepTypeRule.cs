using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1201 — a step's <c>type</c> names nothing the engine's catalogue defines, reported with
/// the closest known type by Levenshtein distance (spec §5.5's own instruction for this code).
/// </summary>
/// <remarks>
/// <para>
/// <b>THE CHANNEL DECISION, recorded where a reviewer will look for it.</b> The sprint spec asks
/// that VFX-D-1201 be "mapped to the existing <c>unknown-step-type</c> finding, not duplicated —
/// the same Levenshtein-suggestion logic ... reused, migrated onto the VFX-D-1201 code rather than
/// re-implemented as a second detector", and US-S2-02's seam header forecast that the code would
/// MOVE out of the schema <c>errors</c> array into this one. It does not move. Both channels carry
/// it, from one shared detector (<see cref="UnknownStepTypeDetector"/>). Three facts decided that:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The schema pass structurally needs its own unknown-type findings.</b>
/// <c>SuiteValidator.SuppressUnevaluatedPropertiesCascade</c> takes them as an ARGUMENT: an
/// unregistered step type withholds every <c>if</c>/<c>then</c> annotation, so the
/// <c>unevaluatedProperties: false</c> cascade that rc.4 introduced cannot be judged from the schema
/// errors alone. Removing 1201 from the schema pass would not relocate a finding; it would break a
/// noise-suppression pass that four other measured behaviours depend on.
/// </description></item>
/// <item><description>
/// <b>Two consumers read the SCHEMA channel only, and both would regress.</b> <c>run_suite</c>'s
/// EDGE-003 pre-flight goes through <c>ValidateFile</c> → <c>AsValidationResult()</c>, which carries
/// <c>{valid, errors}</c> and nothing else: move 1201 out and <c>run_suite</c> would spawn the
/// engine on a suite it currently refuses. And US-S2-06's agreement oracle compares this channel
/// against <c>vouchfx validate</c> on the engine's 55-fixture rejected corpus, asserting 33
/// byte-identical / 13 enriched / 0 differing — a finding leaving the array is a deviation exactly
/// as a semantic finding leaking INTO it would be, and the sprint's own exit checklist calls that a
/// blocker.
/// </description></item>
/// <item><description>
/// <b>What the spec actually forbids is a second DETECTOR and a second CODE, and neither exists.</b>
/// <see cref="UnknownStepTypeDetector"/> is the single detection site; this rule and the schema pass
/// both call it; the code, its meaning and its <c>docs/errors/VFX-D-1201.md</c> page are unchanged.
/// The channel-separation criterion is a statement about the two ARRAYS ("the two never merge into
/// one list, so a host can filter schema-blocking issues from authoring-quality warnings
/// independently") — which holds: each array is complete and correct on its own terms, and a host
/// filtering one from the other gets exactly what the criterion promises. The compatibility
/// criterion ("path-only callers see no change beyond documented additions") holds in the strongest
/// available sense: the schema channel is byte-for-byte what it was.
/// </description></item>
/// </list>
/// <para>
/// <b>The two renderings differ, and that is the point.</b> The schema channel's message is the one
/// the agreement oracle compares against the engine's own, so it keeps its exact
/// "Unknown step type 'X'. Known types: …" wording; enriching it would have moved the 33/13/0
/// baseline. This channel adds the closest-match suggestion — this server's own advice, in this
/// server's own channel, where an oracle comparing against the engine has no claim on the wording.
/// </para>
/// <para>
/// <b>Severity is <c>warning</c>, not <c>error</c></b>, even though the engine will certainly reject
/// the suite. The verdict that says so is the schema channel's, which already carries this finding;
/// this channel is advice, and only spec §5.5's one explicitly-error code (1207) flips
/// <c>SuiteAnalysis.Valid</c>. See <see cref="VfxCodeCatalogue"/>'s note above the 1202-1211 block.
/// </para>
/// </remarks>
internal sealed class UnknownStepTypeRule : ISemanticRule
{
    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.UnknownStepType;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        foreach (var unknown in UnknownStepTypeDetector.Detect(context.Document))
        {
            var path = SuitePath.Step(unknown.StepIndex).Property("type");
            var suggestion = UnknownStepTypeDetector.SuggestClosest(unknown.Type);

            // Both the offending type and the suggestion go through SemanticFinding.Identifier: the
            // first is caller-supplied suite content (and could be a reference), the second is an
            // in-repo catalogue constant that is bounded anyway. One helper for both, so no future
            // edit has to remember which was which.
            var message = suggestion is null
                ? $"Unknown step type {SemanticFinding.Identifier(unknown.Type)}. "
                    + "Call list_step_types to see every type the pinned engine defines."
                : $"Unknown step type {SemanticFinding.Identifier(unknown.Type)}. The closest known "
                    + $"type by edit distance is {SemanticFinding.Identifier(suggestion)}; call "
                    + "list_step_types to see them all.";

            findings.Add(SemanticFinding.Create(
                context, Code, SemanticFinding.Warning, message, path));
        }

        return findings;
    }
}
