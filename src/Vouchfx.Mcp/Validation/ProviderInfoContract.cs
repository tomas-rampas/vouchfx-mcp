using System.Collections.Frozen;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The explicit split of spec §5.2's <c>ProviderInfo</c> field list into what the catalogue tools
/// (<c>list_step_types</c>, <c>describe_step_type</c>) derive, and what they deliberately leave out
/// because it is not an engine fact (US-S2-05; upstream ask U5, which engine v1.0.0-rc.6 delivered).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the split is a constant and not a comment.</b> US-S2-05's requirement is not merely that
/// an underivable field is absent from the wire — absence is trivially achieved by forgetting to add
/// it. It is that the boundary is a named, checkable fact, so that (a) a reader can tell "we do not
/// report this, on purpose" from "nobody got round to it", and (b) moving a field across is a
/// deliberate edit that <c>ProviderInfoContractTests</c> forces someone to make consciously. The
/// partition — disjoint, and covering <see cref="SpecFields"/> exactly — is asserted there. Until
/// engine v1.0.0-rc.6 the second set was the five fields pending upstream ask U5; U5 landed four of
/// them, and the fifth was never going to come from the engine.
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
/// <c>requiredResources</c> — derived by US-S2-05 from data this server already holds: the vendored
/// composed schema's step-type set crossed with the step-type → dependency-kind table
/// <c>UndeclaredDependencyRule</c> already gates in both directions against that same schema. See
/// <see cref="RequiredResourceCatalogue"/>. The engine does not report it, and does not need to.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>tier</c>, <c>supportsVerifyMode</c>, <c>example</c>, <c>docsUrl</c> — derived since engine
/// v1.0.0-rc.6, whose <c>list --json</c> reports <c>tier</c>, <c>supportedVerifyModes</c>,
/// <c>docsUrl</c> and <c>example</c> per step type (vouchfx#556, the U5 ask). Each is RELAYED, never
/// composed here: <c>supportsVerifyMode</c> is <see langword="true"/> exactly when the engine's
/// list names <c>RETRY</c>, which is the spec's "RETRY-capable"; <c>example</c> is the engine's own
/// scaffolded suite; <c>docsUrl</c> is the engine's language-reference link. Before rc.6 each was
/// omitted because nothing in this repository could derive it (the schema declares
/// <c>verifyMode</c> once, on the common step envelope; it carries no examples; and there is no
/// per-provider docs page), and a field the engine reports as null is still omitted rather than
/// defaulted. <c>describe_step_type</c> carries all four; <c>list_step_types</c> carries all but
/// <c>example</c>, which would make the cheap list the expensive one.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>vouched</c> — NOT reported, and not pending anything. It is the provider hub's maintainer-awarded
/// badge for a Community provider, not an engine fact: the engine deliberately leaves it out of
/// <c>list --json</c> (vouchfx#556), and every step type the pinned engine lists is a Core one, to
/// which the badge does not apply. Reporting it would mean this server guessing a hub decision.
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
    /// The spec §5.2 fields the catalogue tools populate — see this type's remarks for what each is
    /// derived from and under which name it reaches the wire.
    /// </summary>
    public static FrozenSet<string> DerivedToday { get; } = new[]
    {
        "stepType",
        "family",
        "provider",
        "tier",
        "summary",
        "parameters",
        "supportsVerifyMode",
        "requiredResources",
        "example",
        "docsUrl",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The spec §5.2 fields that are OMITTED from every catalogue result because they belong to the
    /// provider hub rather than the engine — never defaulted, never guessed.
    /// </summary>
    public static FrozenSet<string> HubOwned { get; } = new[]
    {
        "vouched",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The sentence both catalogue tools' descriptions carry, telling a host which spec §5.2 field is
    /// absent and why.
    /// </summary>
    /// <remarks>
    /// COMPOSED from <see cref="HubOwned"/> rather than written out twice in prose, so a field leaving
    /// that set cannot leave a tool description still claiming it is absent. The wording avoids every
    /// <see cref="DerivedToday"/> name as a whole word — including the common noun "provider" —
    /// because <c>ProviderInfoContractTests</c> reads a whole-word match as a claim that a populated
    /// field is absent.
    /// </remarks>
    public static string AbsentFieldsNotice { get; } =
        "Deliberately absent, never defaulted or guessed: the ProviderInfo record also lists "
        + string.Join(", ", SpecFields.Where(HubOwned.Contains))
        + ", the Vouched badge the vouchfx-providers hub awards to a specific version of a Community "
        + "package. That is a hub decision rather than an engine fact, so the pinned engine's "
        + "`vouchfx list --json` deliberately does not emit it.";
}
