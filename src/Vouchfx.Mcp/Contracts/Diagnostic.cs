using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Contracts;

// Vouchfx.Mcp.Contracts — Diagnostic (Sprint 1 / US-S1-03, spec §5 shared-types preamble).
//
// The canonical shape for "here is a finding about the suite/run" — a Diagnostic is DATA, not an
// error: spec §4.4 draws this line explicitly ("Diagnostics (validation/compile) are not errors:
// they are returned as data (ok: false, diagnostics: [...]) so hosts can iterate. Errors are
// reserved for 'the call itself could not be performed'"). Concretely: a suite that fails schema
// validation is a SUCCESSFUL validate_suite call (isError: false) whose result carries one or more
// Diagnostic entries — never a VfxError.cs failure. The counterpart record, VfxError, is the "the
// call could not be performed" half of the same rule; see that file's header for the reverse case.
//
// This story (US-S1-03) is deliberately just the record + its source-generated JSON context + its
// construction-time code/severity validation — nothing in src/ constructs a Diagnostic yet. US-S1-04
// is the migration that starts emitting these from the nine existing tools (starting with
// `suite-invalid`, which the sprint plan calls out by name as the highest-risk mapping precisely
// because it is the existing precedent for this "diagnostics are data" rule).
//
// DiagnosticLocation and DiagnosticFix carry only scalar/nested-record fields (no JsonElement), so —
// unlike VfxError.cs — plain positional records are equality-safe here and get free structural
// Equals(). Diagnostic itself needs construction-time validation the same way VfxError does, so it
// is written the same way: an explicit constructor plus get-only properties (see VfxError.cs's
// header for why a positional record's primary constructor cannot host that validation).

/// <summary>
/// A source location a <see cref="Diagnostic"/> points at (spec §5 shared-types preamble).
/// </summary>
/// <param name="File">The file path the diagnostic concerns, relative to the workspace/suite root.</param>
/// <param name="Line">The 1-based line number the diagnostic starts at.</param>
/// <param name="Column">The 1-based column number the diagnostic starts at.</param>
/// <param name="EndLine">The 1-based line number the diagnostic ends at, or <see langword="null"/> for a single point.</param>
/// <param name="EndColumn">The 1-based column number the diagnostic ends at, or <see langword="null"/> for a single point.</param>
public sealed record DiagnosticLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("endLine")] int? EndLine,
    [property: JsonPropertyName("endColumn")] int? EndColumn);

/// <summary>
/// A candidate fix for a <see cref="Diagnostic"/> (spec §5 shared-types preamble).
/// </summary>
/// <param name="Description">A human-readable description of the fix.</param>
/// <param name="Replacement">
/// The literal replacement text to apply, when this fix is machine-applicable, or
/// <see langword="null"/> when it is advisory only (a human must decide how to act on it).
/// </param>
public sealed record DiagnosticFix(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("replacement")] string? Replacement);

/// <summary>
/// The canonical shape for a finding about the suite/run, returned as DATA on a successful call
/// (spec §4.4's "diagnostics are data, not errors" rule — see this file's header remarks). A tool
/// that reports one or more <see cref="Diagnostic"/> entries still completes with <c>isError: false</c>;
/// only a genuinely unrecoverable call failure is a <see cref="VfxError"/>.
/// </summary>
public sealed record Diagnostic
{
    /// <summary>The only three severities spec §5's shared-types preamble defines — anything else is rejected at construction.</summary>
    public static readonly IReadOnlyCollection<string> ValidSeverities = ["error", "warning", "info"];

    /// <summary>
    /// A <c>VFX-D-####</c> code whose 4-digit number falls inside one of the reserved ranges from
    /// spec §4.4 (validated at construction by <see cref="VfxCode.Validate"/> — see that type's
    /// remarks for the full range table, including the deliberately-unreserved 1800-1899 gap).
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; }

    /// <summary>
    /// One of the three literal strings <c>"error"</c>, <c>"warning"</c>, or <c>"info"</c> — any
    /// other value is rejected at construction (see <see cref="ValidSeverities"/>). This severity is
    /// the finding's own classification and is orthogonal to <see cref="Code"/>'s <c>VFX-D-</c>
    /// prefix: a diagnostic is never an error in the <see cref="VfxError"/> sense regardless of this
    /// value.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; }

    /// <summary>A human-readable, one-line explanation of the finding.</summary>
    [JsonPropertyName("message")]
    public string Message { get; }

    /// <summary>The source location this finding concerns, or <see langword="null"/> when not location-scoped.</summary>
    [JsonPropertyName("location")]
    public DiagnosticLocation? Location { get; }

    /// <summary>A JSONPath/YAML path into the suite, e.g. <c>"$.steps[2].match.key"</c>, or <see langword="null"/> when not applicable.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; }

    /// <summary>A candidate fix, machine-applicable when its <see cref="DiagnosticFix.Replacement"/> is set, or <see langword="null"/>.</summary>
    [JsonPropertyName("fix")]
    public DiagnosticFix? Fix { get; }

    /// <summary>A link to this code's catalogue entry, or <see langword="null"/> before that catalogue exists.</summary>
    [JsonPropertyName("docsUrl")]
    public string? DocsUrl { get; }

    /// <summary>
    /// Validates <see cref="Code"/> (must be <c>VFX-D-####</c>, number inside a reserved range —
    /// see <see cref="VfxCode.Validate"/>), <see cref="Severity"/> (must be one of
    /// <see cref="ValidSeverities"/>), and <see cref="Message"/> (must be non-blank) at construction.
    /// </summary>
    [JsonConstructor]
    public Diagnostic(
        string code,
        string severity,
        string message,
        DiagnosticLocation? location,
        string? path,
        DiagnosticFix? fix,
        string? docsUrl)
    {
        VfxCode.Validate(code, "VFX-D-", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(message, nameof(message));

        if (!ValidSeverities.Contains(severity, StringComparer.Ordinal))
        {
            // `severity` is caller-supplied and unbounded at this point — VfxCode.SanitiseForEcho
            // caps and sanitises it before it goes anywhere near an exception message a host might
            // display (same treatment VfxCode.Validate gives a malformed `code`).
            throw new ArgumentException(
                $"Severity must be one of: {string.Join(", ", ValidSeverities)}. Got: '{VfxCode.SanitiseForEcho(severity)}'.",
                nameof(severity));
        }

        Code = code;
        Severity = severity;
        Message = message;
        Location = location;
        Path = path;
        Fix = fix;
        DocsUrl = docsUrl;
    }
}

/// <summary>
/// The source-generated System.Text.Json serialization context for <see cref="Diagnostic"/> (and
/// its nested <see cref="DiagnosticLocation"/>/<see cref="DiagnosticFix"/> shapes) — no
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
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(DiagnosticLocation))]
[JsonSerializable(typeof(DiagnosticFix))]
internal sealed partial class DiagnosticJsonContext : JsonSerializerContext
{
}
