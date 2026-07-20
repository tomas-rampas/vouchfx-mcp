namespace Vouchfx.Mcp.Validation;

/// <summary>
/// One field of a step type's own schema fragment (the <c>then</c> block matched once a step's
/// <c>type</c> equals this type's dotted <c>family.provider</c> name).
/// </summary>
/// <param name="Name">The field name, e.g. <c>method</c>.</param>
/// <param name="Type">
/// The JSON Schema <c>type</c> keyword's value for this field (e.g. <c>string</c>,
/// <c>integer</c>, <c>object</c>), or <see langword="null"/> when the schema declares no
/// <c>type</c> for it (e.g. <c>http.rest</c>'s <c>body</c>, which accepts any JSON value).
/// </param>
/// <param name="Description">The field's schema description, or <see langword="null"/> if absent.</param>
/// <param name="Required">
/// <see langword="true"/> when this field is unconditionally required for the step type (present
/// in the schema's flat <c>required</c> array). Always <see langword="false"/> for a step type
/// whose <c>then</c> block instead uses <c>oneOf</c> (see <see cref="StepTypeInfo.RequiredOneOf"/>)
/// — no single field is unconditionally required there.
/// </param>
public sealed record StepFieldInfo(string Name, string? Type, string? Description, bool Required);

/// <summary>
/// One vouchfx step type — a dotted <c>family.provider</c> — derived from the embedded composed
/// JSON Schema (see <see cref="StepTypeCatalogue"/>), never from a hand-maintained list.
/// </summary>
/// <param name="Type">The full dotted type name, e.g. <c>db-assert.postgres</c>.</param>
/// <param name="Family">The part before the dot, e.g. <c>db-assert</c>.</param>
/// <param name="Provider">The part after the dot, e.g. <c>postgres</c>.</param>
/// <param name="Description">
/// A one-line description of the step type, taken from the schema's <c>then</c> block's own
/// <c>description</c> keyword when present, or <see langword="null"/> when the schema carries
/// none for this type (most of the 25 types have no type-level description — only per-field
/// ones; see STEP 0's schema-structure notes).
/// </param>
/// <param name="Fields">
/// Every field this step type's own schema fragment declares, each flagged required or optional.
/// Does not include the common step envelope fields (<c>id</c>, <c>type</c>, <c>description</c>,
/// <c>capture</c>, <c>verifyMode</c>, <c>timeout</c>, <c>continueOnFailure</c>) that every step
/// type shares regardless of family/provider.
/// </param>
/// <param name="RequiredOneOf">
/// <see langword="null"/> for every step type except <c>script.csharp</c>, whose schema fragment
/// has no flat <c>required</c> array at all — instead a <c>oneOf</c> of single-field
/// <c>required</c> groups (here, <c>["code"]</c> or <c>["file"]</c>), meaning at least one of
/// those groups' fields must be present rather than any one field being unconditionally
/// required. When non-null, every field in <see cref="Fields"/> has <see cref="StepFieldInfo.Required"/>
/// <see langword="false"/>, and this property carries the real constraint instead.
/// </param>
public sealed record StepTypeInfo(
    string Type,
    string Family,
    string Provider,
    string? Description,
    IReadOnlyList<StepFieldInfo> Fields,
    IReadOnlyList<IReadOnlyList<string>>? RequiredOneOf);
