using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// One field of a step type's own schema fragment (the <c>then</c> block matched once a step's
/// <c>type</c> equals this type's dotted <c>family.provider</c> name).
/// </summary>
/// <param name="Name">The field name, e.g. <c>method</c>.</param>
/// <param name="Type">
/// The JSON Schema <c>type</c> keyword's value for this field (e.g. <c>string</c>,
/// <c>integer</c>, <c>object</c>), or <see langword="null"/> when the schema declares no
/// <c>type</c> for it (e.g. <c>http.rest</c>'s <c>body</c>, which accepts any JSON value) or when
/// the entry was built from the live engine catalogue export (which carries field names only).
/// </param>
/// <param name="Description">The field's schema description, or <see langword="null"/> if absent.</param>
/// <param name="Required">
/// <see langword="true"/> when this field is unconditionally required for the step type (present
/// in the schema's flat <c>required</c> array, or listed in the live catalogue's
/// <c>requiredFields</c>). Always <see langword="false"/> for a step type whose <c>then</c> block
/// instead uses <c>oneOf</c> (see <see cref="StepTypeInfo.RequiredOneOf"/>) — no single field is
/// unconditionally required there.
/// </param>
public sealed record StepFieldInfo(string Name, string? Type, string? Description, bool Required);

/// <summary>
/// One vouchfx step type — a dotted <c>family.provider</c> — from the live engine catalogue export
/// (preferred for catalogue tools) or derived from the embedded composed JSON Schema (used by
/// <see cref="SuiteValidator"/>'s unknown-type check and as the offline schema vocabulary).
/// </summary>
/// <param name="Type">The full dotted type name, e.g. <c>db-assert.postgres</c>.</param>
/// <param name="Family">The part before the dot, e.g. <c>db-assert</c>.</param>
/// <param name="Provider">The part after the dot, e.g. <c>postgres</c>.</param>
/// <param name="Description">
/// A one-line description of the step type. For the live catalogue this is the engine's
/// <see cref="FamilyIntent"/>; for schema-derived entries it is the schema <c>then.description</c>
/// when present, otherwise <see langword="null"/>.
/// </param>
/// <param name="Fields">
/// Every type-specific field, each flagged required or optional. Does not include the common step
/// envelope fields (<c>id</c>, <c>type</c>, <c>description</c>, <c>capture</c>, <c>verifyMode</c>,
/// <c>timeout</c>, <c>continueOnFailure</c>) that every step type shares regardless of
/// family/provider. Live-catalogue entries carry field names only (Type/Description are
/// <see langword="null"/>).
/// </param>
/// <param name="RequiredOneOf">
/// <see langword="null"/> for every step type except schema-derived <c>script.csharp</c>, whose
/// schema fragment has no flat <c>required</c> array at all — instead a <c>oneOf</c> of
/// single-field <c>required</c> groups (here, <c>["code"]</c> or <c>["file"]</c>). Live catalogue
/// entries always leave this <see langword="null"/> (the engine export flattens field names into
/// required/optional lists).
/// </param>
/// <param name="RequiredFields">
/// Type-specific required field names (bar B / Spec A). Same names as the required subset of
/// <see cref="Fields"/>.
/// </param>
/// <param name="OptionalFields">
/// Type-specific optional field names (bar B / Spec A).
/// </param>
/// <param name="CaptureSupported">
/// Whether the language allows a <c>capture</c> block on steps of this type (bar B).
/// </param>
/// <param name="FamilyIntent">
/// Short human-readable one-liner describing the family's purpose (bar B), sufficient for an
/// agent to choose a family.
/// </param>
public sealed record StepTypeInfo(
    string Type,
    string Family,
    string Provider,
    string? Description,
    IReadOnlyList<StepFieldInfo> Fields,
    IReadOnlyList<IReadOnlyList<string>>? RequiredOneOf,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyList<string> OptionalFields,
    bool CaptureSupported,
    string FamilyIntent)
{
    /// <summary>
    /// Spec §5.2's <c>requiredResources</c> (US-S2-05): the dependency KINDS a step of this type
    /// needs declared in <c>environment.dependencies</c>. An empty list means "none, derived";
    /// <see langword="null"/> means "not derivable here", and is OMITTED from the wire rather than
    /// emitted as <c>[]</c> — see <see cref="RequiredResourceCatalogue"/> for why those differ.
    /// </summary>
    /// <remarks>
    /// <b>Non-positional, and default-null, on purpose.</b> This is catalogue ENRICHMENT the two
    /// catalogue tools attach at the point of answering (<c>info with { RequiredResources = … }</c>),
    /// not something the parsers produce: neither <see cref="StepCatalogueParser"/> (which reads a
    /// live engine export that does not carry it) nor <see cref="StepTypeCatalogue"/> (whose
    /// consumer is <see cref="SuiteValidator"/>'s unknown-type check, which has no use for it) sets
    /// it. Adding it as an eleventh positional parameter would have forced both of them — and every
    /// test that constructs a <see cref="StepTypeInfo"/> — to pass a value they have no opinion
    /// about.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredResources { get; init; }
}
