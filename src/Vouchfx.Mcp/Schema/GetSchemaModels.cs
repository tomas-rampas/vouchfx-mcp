using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Schema;

// Vouchfx.Mcp.Schema — get_schema's wire shape and outcome union (Sprint 2 / US-S2-01).
//
// The acceptance criteria fix the success shape as { meta, schemaVersion, section, jsonSchema?,
// summary? }. `meta` is NOT a member of the record below: it is attached at
// Tools/StructuredToolResult.Success, the one choke point every successful tool result travels
// through (see that type's remarks for why a per-payload field would be the wrong home) — and
// SerialiseWithMeta throws if a payload declares its own `meta`, so adding one here would fail
// loudly rather than duplicate silently.
//
// `diagnostics` is the ONE addition to the stated shape, and only ever appears on the live-mismatch
// path: the same criteria require a cross-verification mismatch to be surfaced "as a Diagnostic,
// never silently", and the stated shape has no other field that could carry one. It is null (and
// therefore ABSENT from the wire, not emitted as null) on every other path, so the ordinary result
// a host sees is exactly the stated shape.

/// <summary>
/// <c>get_schema</c>'s success payload: the addressed section of the vouchfx composed JSON Schema,
/// as a schema document and/or a markdown digest.
/// </summary>
/// <param name="SchemaVersion">
/// The language schema version the served document declares — the embedded vendored schema's own
/// <c>x-vouchfx-schema-version</c> marker (see
/// <see cref="Vouchfx.Mcp.Validation.VendoredSchemaVersion"/>), identical to the <c>meta</c>
/// stamp's own <c>schemaVersion</c>. Repeated at the top level deliberately: the criteria name it
/// on this shape, and a host reading a schema result should not have to know that provenance is
/// stamped separately.
/// </param>
/// <param name="Section">
/// The section token exactly as the caller wrote it (or <c>"full"</c> when omitted), so a host
/// batching several calls can correlate results without tracking request order.
/// </param>
/// <param name="JsonSchema">
/// The addressed subtree as a JSON Schema document, or <see langword="null"/> when
/// <c>format: "summary"</c> was requested. Nested as real JSON, never a JSON-encoded string: a host
/// that wants to feed it to a schema validator should not have to unwrap it first.
/// </param>
/// <param name="Summary">
/// The markdown digest (see <see cref="SchemaSummaryRenderer"/>), or <see langword="null"/> for the
/// default <c>format: "json-schema"</c>. The two are mutually exclusive by design: returning the
/// whole ~150&#160;KB document alongside a digest the caller asked for INSTEAD of it would defeat
/// the digest's only purpose.
/// </param>
/// <param name="Diagnostics">
/// Findings about the served document itself — today, exactly one possible entry:
/// <see cref="VfxCodeCatalogue.LiveSchemaMismatch"/>, when a pinned CLI was present and its
/// <c>vouchfx schema</c> export disagreed with the embedded vendored copy. <see langword="null"/>
/// (and omitted from the wire) whenever there is nothing to report, INCLUDING the ordinary offline
/// case: no CLI means no comparison was made, which is not a finding.
/// </param>
public sealed record GetSchemaResult(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("jsonSchema")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? JsonSchema,
    [property: JsonPropertyName("summary")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Summary,
    [property: JsonPropertyName("diagnostics")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Diagnostic>? Diagnostics);

/// <summary>Outcome of a <c>get_schema</c> call.</summary>
/// <remarks>
/// A closed union with a private constructor, matching <c>PlanCoverageOutcome</c> and
/// <c>ScaffoldSuiteOutcome</c>. Note what is NOT a case here: "the pinned CLI is unavailable".
/// <c>get_schema</c> is CLI-OPTIONAL — a missing engine yields <see cref="Completed"/> from the
/// embedded vendored schema, the same way <c>validate_suite</c> and <c>search_docs</c> already work
/// offline — so there is no <c>CliUnavailable</c> case to render and no
/// <see cref="VfxCodeCatalogue.EngineCliUnavailable"/> path out of this tool.
/// </remarks>
public abstract record GetSchemaOutcome
{
    private GetSchemaOutcome()
    {
    }

    /// <summary>The section was resolved and rendered.</summary>
    public sealed record Completed(GetSchemaResult Result) : GetSchemaOutcome;

    /// <summary>An argument value was not one this tool accepts (today: an unknown <c>format</c>).</summary>
    public sealed record InvalidArgument(string Message) : GetSchemaOutcome;

    /// <summary>The <c>section</c> token addressed nothing in the composed schema.</summary>
    public sealed record SectionNotFound(string Message) : GetSchemaOutcome;
}
