using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Contracts;

// Vouchfx.Mcp.Contracts — VfxError (Sprint 1 / US-S1-03, spec §4.4).
//
// The canonical shape for "the call itself could not be performed" — every tool error this server
// returns (isError: true) is, or will be once US-S1-04 migrates every existing ad-hoc `kind` string
// onto this record, a single JSON object matching this shape exactly. The counterpart is
// Diagnostic.cs: a VfxError means the call failed; a Diagnostic means the call succeeded and is
// reporting a finding about the suite/run as DATA (spec §4.4: "Diagnostics ... are not errors: they
// are returned as data ... Errors are reserved for 'the call itself could not be performed'").
//
// This story (US-S1-03) is deliberately just the record + its source-generated JSON context + its
// construction-time code validation — nothing in src/ constructs a VfxError yet. US-S1-04 is the
// migration that starts emitting these from the nine existing tools.
//
// Deliberately NOT a positional record: a positional record's primary constructor cannot run
// caller-supplied validation logic (there is no C# syntax for a validating body on a record's
// primary constructor — only `init` accessors and explicit constructors can), and construction-time
// code validation is this story's whole point. An explicit constructor plus get-only properties
// gives the same immutability with room for VfxCode.Validate to run and throw.

/// <summary>
/// The canonical shape for a tool call that could not be performed (spec §4.4). Every field beyond
/// <see cref="Code"/>, <see cref="Message"/>, and <see cref="Retryable"/> is optional and, when
/// absent, is omitted from the serialised JSON entirely (not emitted as <c>null</c>) — every byte
/// here is paid twice, once in the <c>TextContentBlock</c> and once in <c>StructuredContent</c>
/// (see <see cref="Vouchfx.Mcp.Tools.StructuredToolResult.Success(object)"/>), so an absent optional
/// field costs nothing on the wire rather than a few bytes of <c>"docsUrl":null</c> noise.
/// </summary>
public sealed record VfxError
{
    /// <summary>
    /// A <c>VFX-E-####</c> code whose 4-digit number falls inside one of the reserved ranges from
    /// spec §4.4 (validated at construction by <see cref="VfxCode.Validate"/> — see that type's
    /// remarks for the full range table, including the deliberately-unreserved 1800-1899 gap).
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; }

    /// <summary>A human-readable, one-line explanation of the failure.</summary>
    [JsonPropertyName("message")]
    public string Message { get; }

    /// <summary>
    /// Structured, tool-specific extra data about the failure (e.g. the offending path, the active
    /// <c>runId</c> for a <c>VFX-E-1501 RunInProgress</c>), or <see langword="null"/> when the
    /// message alone is sufficient. Modelled as <see cref="JsonElement"/> — an arbitrary JSON value
    /// — because spec §4.4 deliberately leaves this field's shape open per call site rather than
    /// pinning one record type every error must awkwardly share.
    /// </summary>
    /// <remarks>
    /// <b>Normative constraint on every future call site that populates this field</b> (CLAUDE.md's
    /// secret-hygiene invariant, restated here because this is the one field on this record whose
    /// shape a call site controls): content must already be redacted by the time it reaches this
    /// constructor. This server is a relay, never a redaction authority — the engine is the SOLE
    /// redaction authority (already-redacted event fields are relayed bounded and sanitised, never
    /// re-redacted or re-resolved) — so <see cref="Details"/> must never carry this process's raw
    /// environment, a <c>${secret:...}</c> resolution, or any other unredacted secret material. It
    /// must also stay reasonably small: every byte here is paid twice on the wire (see this record's
    /// own summary), so <see cref="Details"/> is for a few structured facts (a path, a <c>runId</c>,
    /// a threshold), never a dump of a large payload.
    /// </remarks>
    [JsonPropertyName("details")]
    public JsonElement? Details { get; }

    /// <summary>
    /// A link to this code's catalogue entry (<c>explain_diagnostic</c> /
    /// <c>vouchfx-docs:///errors/{code}</c>, landing in US-S1-05), or <see langword="null"/> before
    /// that catalogue exists.
    /// </summary>
    [JsonPropertyName("docsUrl")]
    public string? DocsUrl { get; }

    /// <summary>
    /// Whether retrying the same call, unchanged, might succeed (e.g. <c>true</c> for
    /// <c>VFX-E-1501 RunInProgress</c>; <c>false</c> for a missing file, which will not fix itself).
    /// </summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; }

    /// <summary>
    /// The source-generated deserialisation constructor, and the one every hand-written call site
    /// ultimately runs through. Validates <see cref="Code"/> (must be <c>VFX-E-####</c>, number
    /// inside a reserved range — see <see cref="VfxCode.Validate"/>) and <see cref="Message"/>
    /// (must be non-blank).
    /// </summary>
    [JsonConstructor]
    public VfxError(string code, string message, JsonElement? details, string? docsUrl, bool retryable)
    {
        VfxCode.Validate(code, "VFX-E-", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(message, nameof(message));

        Code = code;
        Message = message;
        Details = details;
        DocsUrl = docsUrl;
        Retryable = retryable;
    }

    /// <summary>
    /// Constructs a <see cref="VfxError"/> with no <see cref="Details"/> or <see cref="DocsUrl"/>
    /// — the common case for a call-site that has neither yet.
    /// </summary>
    public VfxError(string code, string message, bool retryable)
        : this(code, message, details: null, docsUrl: null, retryable)
    {
    }

    /// <summary>
    /// Structural equality, replacing the record-synthesised member-wise <see cref="Equals(VfxError?)"/>
    /// only for <see cref="Details"/>: <see cref="JsonElement"/> has no value equality of its own
    /// (two elements parsed from equal JSON compare unequal by default, since each wraps a
    /// reference to its own parsing <see cref="JsonDocument"/>) — comparing <see cref="JsonElement.GetRawText"/>
    /// instead gives the semantically-correct answer here because both sides of every comparison in
    /// this server always flow through the same serializer with the same options, so raw-text
    /// equality and value equality coincide (this is NOT a general-purpose deep-equals).
    /// </summary>
    public bool Equals(VfxError? other) =>
        other is not null
        && Code == other.Code
        && Message == other.Message
        && DocsUrl == other.DocsUrl
        && Retryable == other.Retryable
        && DetailsEqual(Details, other.Details);

    /// <inheritdoc cref="Equals(VfxError?)"/>
    public override int GetHashCode() => HashCode.Combine(Code, Message, DocsUrl, Retryable);

    private static bool DetailsEqual(JsonElement? left, JsonElement? right) =>
        (left is null && right is null)
        || (left is not null && right is not null && left.Value.GetRawText() == right.Value.GetRawText());
}

/// <summary>
/// The source-generated System.Text.Json serialization context for <see cref="VfxError"/> — no
/// reflection-based (<c>JsonSerializer.Serialize(object)</c> without an explicit
/// <c>System.Text.Json.Serialization.Metadata.JsonTypeInfo&lt;T&gt;</c>) path is used anywhere this
/// type is serialised.
/// </summary>
// Both PropertyNamingPolicy (CamelCase) and every property's own explicit [JsonPropertyName] are
// set deliberately, even though either alone would already produce the correct camelCase wire
// names — belt-and-braces so neither a removed [JsonPropertyName] attribute nor a future change to
// this policy silently reshapes the wire casing without a test catching it.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VfxError))]
internal sealed partial class VfxErrorJsonContext : JsonSerializerContext
{
}
