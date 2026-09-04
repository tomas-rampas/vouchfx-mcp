using System.Collections.Frozen;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Spec §5.2's <c>requiredResources</c> for one step type — the dependency KINDS a step of that
/// type needs declared in <c>environment.dependencies</c> — derived, never hand-listed (US-S2-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>A second READER of one table, deliberately not a second copy of it.</b> The step-type →
/// dependency-kind mapping lives on
/// <see cref="UndeclaredDependencyRule.RequiredDependencyKinds"/>, where US-S2-03 put it because
/// VFX-D-1205 was the first thing that needed it, and where
/// <c>UndeclaredDependencyRuleTests</c> already gates it in BOTH directions against the vendored
/// schema (every mapped type exists; every unmapped type is one of the six documented
/// absent-by-design cases). Copying those nineteen rows here would mean an <c>ENGINE_PIN</c> bump
/// could leave the diagnostic and the catalogue tools telling a host two different stories about the
/// same step type. So this type reads that table and adds nothing to it; the direction of the
/// dependency is the pragmatic one (the newer caller reaches for the established, already-gated
/// table) rather than the tidy one.
/// </para>
/// <para>
/// <b>The three outcomes mean three different things, and collapsing any two would fabricate a
/// fact.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// A NON-EMPTY list — e.g. <c>mq-expect.kafka</c> → <c>["kafka"]</c> — is a derived requirement.
/// (Always exactly one kind today: no step type in the pinned catalogue needs two. The signature is
/// a list because spec §5.2's is, and because a future provider needing two costs no wire change.)
/// </description></item>
/// <item><description>
/// An EMPTY list is the derived ANSWER "this type needs no dependency kind" — the six types
/// <c>UndeclaredDependencyRule</c> documents as absent by design (<c>http.rest</c>,
/// <c>script.csharp</c>, …). Spec §5.2 types the field as a non-optional <c>string[]</c>, so an
/// empty array is its native way of saying "none", and stating it is strictly more useful to a host
/// than staying silent.
/// </description></item>
/// <item><description>
/// <see langword="null"/> is "this server cannot say" — a step type the vendored composed schema
/// does not define, most plausibly one the live engine gained ahead of a
/// <c>sync-vendored.ps1 -Update</c> resync. The catalogue tools OMIT the field entirely in that
/// case. Returning <c>[]</c> instead would tell the host the new provider needs no infrastructure,
/// which is a guess — precisely what sprint-00-overview.md §3's gated-feature stances forbid.
/// </description></item>
/// </list>
/// <para>
/// <b>Why the vendored schema, and not the live catalogue, defines the domain.</b> The answer is
/// derived from vendored artefacts pinned to a specific engine commit; the live catalogue can be a
/// different (though version-matched) surface. Keying the lookup on what the SCHEMA knows is what
/// makes the <see langword="null"/> arm meaningful — it fires exactly when the two disagree, which
/// is exactly when this repo genuinely has no basis for an answer.
/// </para>
/// </remarks>
internal static class RequiredResourceCatalogue
{
    /// <summary>
    /// One entry per step type the vendored composed schema defines. Built once: the tables it
    /// reads are themselves immutable and schema-derived, so the answers cannot change at run time.
    /// </summary>
    private static readonly FrozenDictionary<string, IReadOnlyList<string>> ByStepType = Build();

    /// <summary>
    /// The dependency kinds <paramref name="stepType"/> requires, an empty list when it requires
    /// none, or <see langword="null"/> when this server has no basis for an answer — see this
    /// type's remarks for why those three are kept distinct.
    /// </summary>
    public static IReadOnlyList<string>? For(string stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);

        return ByStepType.TryGetValue(stepType, out var resources) ? resources : null;
    }

    private static FrozenDictionary<string, IReadOnlyList<string>> Build()
    {
        // Shared by every absent-by-design type: an immutable empty array, so no caller can be
        // handed a list it could mutate into a fabricated requirement.
        IReadOnlyList<string> none = Array.Empty<string>();

        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var stepType in StepTypeCatalogue.All)
        {
            entries[stepType.Type] =
                UndeclaredDependencyRule.RequiredDependencyKinds.TryGetValue(stepType.Type, out var kind)
                    ? [kind]
                    : none;
        }

        // Ordinal throughout, matching the engine's own step-type and dependency-kind lookups (see
        // DependencyKinds.Load): 'Http.Rest' is a miss there and must be a miss here.
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
