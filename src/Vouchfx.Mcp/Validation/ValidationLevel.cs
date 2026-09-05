using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Which of <c>validate_suite</c>'s two passes run for a call (Sprint 2 / US-S2-02's <c>level</c>
/// selector).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the level does NOT gate:</b> reading the suite, <see cref="PathSafetyGuard"/>,
/// <see cref="YamlSafetyGuard"/>, the YAML→JSON conversion, or the process-isolation boundary.
/// Those are not a "pass" — they are the shared input both passes consume, and every one of them is
/// a safety property rather than a diagnostic one. A level that could switch a YAML-bomb defence off
/// would be a bypass with a friendly name; there is deliberately no such value.
/// </para>
/// <para>
/// <b>Ordering is not a hierarchy.</b> <see cref="Semantic"/> is not "more than"
/// <see cref="Schema"/>: it runs a DIFFERENT pass, and <see cref="Full"/> is the only value that
/// runs both. A caller asking for <see cref="Semantic"/> against a schema-invalid suite gets no
/// schema errors, by design — that is the point of being able to ask for one pass at a time.
/// </para>
/// </remarks>
[JsonConverter(typeof(ValidationLevelJsonConverter))]
public enum ValidationLevel
{
    /// <summary>Only <see cref="SuiteValidator"/>'s JSON Schema pipeline (validate_suite v1's behaviour).</summary>
    Schema,

    /// <summary>Only the semantic-rules pass (US-S2-03's rules; a no-op until they land).</summary>
    Semantic,

    /// <summary>Both passes. The default when a caller does not name one.</summary>
    Full,
}

/// <summary>
/// The wire tokens for <see cref="ValidationLevel"/> — the three literal strings the
/// <c>validate_suite</c> tool and the <c>--validate-worker</c> command line both speak.
/// </summary>
/// <remarks>
/// One parser, shared by the tool boundary and the worker's own argument handling, for the same
/// non-drift reason <see cref="ValidationWorkerProtocol"/> exists at all: a level that parsed one way
/// in the server and another in the child would be a silent behaviour split across a process
/// boundary rather than a compile error.
/// </remarks>
public static class ValidationLevels
{
    /// <summary>The <see cref="ValidationLevel.Schema"/> token.</summary>
    public const string Schema = "schema";

    /// <summary>The <see cref="ValidationLevel.Semantic"/> token.</summary>
    public const string Semantic = "semantic";

    /// <summary>The <see cref="ValidationLevel.Full"/> token, and the default when none is supplied.</summary>
    public const string Full = "full";

    /// <summary>Every advertised token, in the order the tool description lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Schema, Semantic, Full];

    /// <summary>The level a call runs at when the caller does not name one.</summary>
    public const ValidationLevel Default = ValidationLevel.Full;

    /// <summary>
    /// Parses one of <see cref="All"/> into its <see cref="ValidationLevel"/>, case-sensitively.
    /// </summary>
    /// <remarks>
    /// <b>Case-sensitive, deliberately</b>, matching <c>get_schema</c>'s <c>section</c>/<c>format</c>
    /// arguments and the engine's own DSL vocabulary convention (see the composed schema's
    /// <c>dependency.type</c> <c>$comment</c>: exactly one canonical spelling per term, with the
    /// rejection naming the correct one). A <see langword="null"/> token is NOT accepted here —
    /// "the caller omitted it" is a different question from "the caller wrote something", and
    /// defaulting is the tool boundary's decision, not this parser's.
    /// </remarks>
    public static bool TryParse(string? token, out ValidationLevel level)
    {
        switch (token)
        {
            case Schema:
                level = ValidationLevel.Schema;
                return true;
            case Semantic:
                level = ValidationLevel.Semantic;
                return true;
            case Full:
                level = ValidationLevel.Full;
                return true;
            default:
                level = Default;
                return false;
        }
    }

    /// <summary>Renders <paramref name="level"/> back to its wire token.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a declared enum value.</exception>
    public static string ToToken(ValidationLevel level) => level switch
    {
        ValidationLevel.Schema => Schema,
        ValidationLevel.Semantic => Semantic,
        ValidationLevel.Full => Full,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown validation level."),
    };
}

/// <summary>
/// Serialises a <see cref="ValidationLevel"/> as its <see cref="ValidationLevels"/> wire token, and
/// reads one back through the same parser.
/// </summary>
/// <remarks>
/// <para>
/// Applied to the enum itself (<c>[JsonConverter]</c> on the type), so every shape carrying a level
/// — today <see cref="SuiteAnalysis.Level"/>, on both the worker wire and the <c>validate_suite</c>
/// result — spells it identically without each one remembering to opt in.
/// </para>
/// <para>
/// <b>Deliberately not <see cref="JsonStringEnumConverter"/> with a camelCase naming policy</b>,
/// which would produce the same three strings today: that would make the wire vocabulary a
/// consequence of the C# member NAMES plus a policy, so renaming a member would silently rename a
/// public wire token. Routing through <see cref="ValidationLevels.ToToken"/> and
/// <see cref="ValidationLevels.TryParse"/> keeps the one parser this server and its worker already
/// share (see <see cref="ValidationLevels"/>'s own remarks) as the single source of truth for the
/// vocabulary in every direction.
/// </para>
/// <para>
/// An unrecognised token throws <see cref="JsonException"/> rather than defaulting, for the reason
/// <c>Program.cs</c>'s worker-argument loop refuses one: a level this build does not know means the
/// two sides disagree about the contract, and answering with the default would answer a question
/// nobody asked. The token itself is not echoed into the exception message — it is worker output,
/// and <c>ValidationWorkerClient</c> reports the whole condition as "output could not be parsed as
/// a result" regardless.
/// </para>
/// </remarks>
public sealed class ValidationLevelJsonConverter : JsonConverter<ValidationLevel>
{
    /// <inheritdoc/>
    public override ValidationLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var token = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        return ValidationLevels.TryParse(token, out var level)
            ? level
            : throw new JsonException("The value is not a recognised validate_suite level token.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ValidationLevel value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(ValidationLevels.ToToken(value));
    }
}
