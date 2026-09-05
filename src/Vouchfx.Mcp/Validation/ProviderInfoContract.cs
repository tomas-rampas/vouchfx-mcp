using System.Collections.Frozen;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The explicit split of spec §5.2's <c>ProviderInfo</c> field list into what the catalogue tools
/// (<c>list_step_types</c>, <c>describe_step_type</c>) derive from data this server already holds,
/// and what waits on upstream ask <b>U5</b> (US-S2-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the split is a constant and not a comment.</b> US-S2-05's requirement is not merely that
/// the gated fields are absent from the wire — absence is trivially achieved by forgetting to add
/// them. It is that the boundary is a named, checkable fact, so that (a) a reader can tell "we
/// cannot derive this" from "nobody got round to it", and (b) when U5 lands, moving a field across
/// is a deliberate edit that <c>ProviderInfoContractTests</c> forces someone to make consciously.
/// The partition — disjoint, and covering <see cref="SpecFields"/> exactly — is asserted there.
/// </para>
/// <para>
/// <b>Field-by-field, with the evidence.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>stepType</c>, <c>family</c>, <c>provider</c>, <c>summary</c>, <c>parameters</c> — derived,
/// and derived since REQ-010: the engine's live <c>vouchfx list --json</c> export carries all of
/// them (see <see cref="StepCatalogueParser"/>'s documented wire shape). This server spells
/// <c>stepType</c> as <c>type</c>, <c>summary</c> as <c>familyIntent</c>/<c>description</c>, and
/// <c>parameters</c> as the <c>requiredFields</c>/<c>optionalFields</c>/<c>fields</c> triple —
/// names fixed before this story and load-bearing for existing hosts, so they are NOT duplicated
/// under a second, spec-shaped spelling.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>requiredResources</c> — derived by US-S2-05, and the one field this story moves OUT of
/// sprint-00-overview.md §3's U5 list. See <see cref="RequiredResourceCatalogue"/>: the vendored
/// composed schema's step-type set crossed with the step-type → dependency-kind table
/// <c>UndeclaredDependencyRule</c> already gates in both directions against that same schema. No
/// engine change is needed for it, so gating it would have been a self-inflicted gap.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>supportsVerifyMode</c> — GATED. MEASURED against <c>vendored/composed-schema.v1.json</c>:
/// <c>verifyMode</c> is declared exactly once, on <c>$defs.step.properties</c> — the common step
/// envelope every type shares — and by no per-step-type <c>then</c> branch. So the schema says
/// only that the KEYWORD is legal everywhere, which would make a schema-derived
/// <c>supportsVerifyMode</c> the constant <see langword="true"/>. Spec §5.2 means something
/// narrower by it ("RETRY-capable"), and emitting <see langword="true"/> for every provider would
/// be exactly the defaulted value the story forbids. (<c>AsyncVerifyModeRule</c>'s async-family
/// prefixes are a heuristic about when RETRY is ADVISABLE, not a statement of engine capability,
/// and are deliberately not laundered into this field.)
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>example</c> — GATED. MEASURED: the composed schema carries no <c>examples</c> keyword at all,
/// and <c>vendored/recipes.md</c> is organised by end-to-end scenario ("Publish and consume a Kafka
/// event"), not by step type, so there is no per-type minimal snippet to relay. Composing one here
/// would be this server authoring YAML, which it does not do.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>tier</c>, <c>vouched</c> — GATED. Engine-owned facts (a registry tier and a maintainer-awarded
/// badge) with no representation anywhere in this repo: not in <c>list --json</c>'s parsed shape,
/// not in the vendored schema. Nothing to derive them FROM, at any level of effort.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>docsUrl</c> — GATED, and the one gap worth restating because it is nearly derivable. Sprint 1
/// established a docs-page convention for DIAGNOSTIC codes (<c>docs/errors/VFX-*.md</c>, gated
/// bidirectionally by <c>ErrorCatalogueFilesystemParityTests</c>), but nothing equivalent exists for
/// PROVIDERS: there is no <c>docs/providers/{family}.{provider}.md</c> in this repo, and
/// sprint-00-overview.md §4 risk 6 records that no sprint introduces one — it is expected to arrive
/// with the U5 landing and is maintainer-owned. Deriving a URL from a convention that does not exist
/// would point every provider at a 404, so the field stays absent.
/// <c>ProviderInfoContractTests</c> watches the repo for that directory appearing.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class ProviderInfoContract
{
    /// <summary>
    /// Spec §5.2's <c>ProviderInfo</c> members, in the order the spec declares them.
    /// </summary>
    /// <remarks>
    /// The ORDER is the spec's, not alphabetical, so a reader can diff this against §5.2 by eye.
    /// <c>ProviderInfoContractTests</c> holds its own independent transcription of the same list and
    /// compares the two — a single shared copy could not catch this one drifting.
    /// </remarks>
    public static IReadOnlyList<string> SpecFields { get; } =
    [
        "stepType",
        "family",
        "provider",
        "tier",
        "vouched",
        "summary",
        "parameters",
        "supportsVerifyMode",
        "requiredResources",
        "example",
        "docsUrl",
    ];

    /// <summary>
    /// The spec §5.2 fields the catalogue tools populate today, with no engine change — see this
    /// type's remarks for what each is derived from and under which name it reaches the wire.
    /// </summary>
    public static FrozenSet<string> DerivedToday { get; } = new[]
    {
        "stepType",
        "family",
        "provider",
        "summary",
        "parameters",
        "requiredResources",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The spec §5.2 fields that are OMITTED from every catalogue result — never defaulted, never
    /// guessed — until upstream ask U5 lands (sprint-00-overview.md §3).
    /// </summary>
    /// <remarks>
    /// Five, not the ask's six: <c>requiredResources</c> is derivable here today and is populated —
    /// see this type's remarks. When U5 ships, each field moves from this set to
    /// <see cref="DerivedToday"/> as it becomes real; the partition test makes that a deliberate act.
    /// </remarks>
    public static FrozenSet<string> U5Gated { get; } = new[]
    {
        "tier",
        "vouched",
        "supportsVerifyMode",
        "example",
        "docsUrl",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The sentence both catalogue tools' descriptions carry, telling a host which spec §5.2 fields
    /// are absent and why.
    /// </summary>
    /// <remarks>
    /// COMPOSED from <see cref="U5Gated"/> rather than written out twice in prose, so a field
    /// leaving the gated set cannot leave a tool description still claiming it is pending. Listed in
    /// <see cref="SpecFields"/> order so the sentence reads the way the spec does.
    /// </remarks>
    public static string U5PendingNotice { get; } =
        "Deliberately absent, never defaulted or guessed: the ProviderInfo record also lists "
        + string.Join(", ", SpecFields.Where(U5Gated.Contains))
        + ", which the pinned engine's `vouchfx list --json` does not emit — they are pending "
        + "upstream ask U5.";
}
