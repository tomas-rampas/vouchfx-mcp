using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Schema;

/// <summary>
/// Maps <c>get_schema</c>'s <c>section</c> argument onto a subtree of the composed schema
/// (US-S2-01), or reports — fail-closed — that the token addresses nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The section table is a claim about the composed schema's real structure</b>, so it is decided
/// in exactly one place: here. <see cref="NamedSections"/> is that list, and it is what the
/// not-found message enumerates. The tool's parameter DESCRIPTION unavoidably restates the tokens in
/// prose (an MCP input schema cannot express "one of these five, or <c>step:</c> plus any dotted
/// type name"), so <c>RealGetSchemaMcpTests</c> asserts that prose mentions every entry of this
/// list — the copy is checked rather than merely trusted. The mapping is deliberately NOT
/// "follow the root property of the same name" for every token: <c>metadata</c> and
/// <c>environment</c> appear at the root as bare <c>{"$ref": "#/$defs/…"}</c> stubs that carry no
/// description and no fields, so returning them would answer an author's question with a pointer.
/// Each named section therefore resolves to the subtree that actually CARRIES the vocabulary:
/// </para>
/// <list type="bullet">
///   <item><c>full</c> — the whole document, exactly as embedded.</item>
///   <item><c>metadata</c> / <c>environment</c> — <c>$defs.metadata</c> / <c>$defs.environment</c>.</item>
///   <item><c>variables</c> — the root's own <c>properties.variables</c>, which is where that
///     section is defined in full (it has no <c>$defs</c> entry at all).</item>
///   <item><c>steps</c> — <c>$defs.step</c>: the single step OBJECT schema (id/type/capture/
///     verifyMode/timeout/…), not the root's array wrapper around a <c>$ref</c> to it. An author
///     asking "what goes in a step" wants the object, and the wrapper adds only <c>minItems</c>.</item>
///   <item><c>step:&lt;family&gt;.&lt;provider&gt;</c> — the matching clause of
///     <c>$defs.step.allOf</c>, i.e. the <c>if</c>/<c>then</c> pair whose
///     <c>if.properties.type.const</c> is that dotted name. The WHOLE clause, not just its
///     <c>then</c>: the <c>if</c> is what makes the returned fragment self-identifying (it names the
///     type it belongs to), and the clause IS the step type's definition in this schema — see
///     <see cref="Vouchfx.Mcp.Validation.StepTypeCatalogue"/>'s remarks for that structure.</item>
/// </list>
/// <para>
/// <b>Matching is ordinal and case-sensitive.</b> The tool advertises lower-case tokens; accepting
/// <c>"Metadata"</c> as well would make that advertisement a lie and create two spellings that can
/// drift. An unrecognised token is a <see cref="SchemaSectionResolution.NotFound"/>, never a
/// silently-empty success — that is an explicit acceptance criterion of this story.
/// </para>
/// </remarks>
public static class SchemaSectionResolver
{
    /// <summary>The default section: the whole composed schema document.</summary>
    public const string FullSection = "full";

    /// <summary>The prefix that addresses one step type's own definition, e.g. <c>step:http.rest</c>.</summary>
    public const string StepSectionPrefix = "step:";

    private const string MetadataSection = "metadata";
    private const string EnvironmentSection = "environment";
    private const string VariablesSection = "variables";
    private const string StepsSection = "steps";

    /// <summary>
    /// Every non-<c>step:</c> token this resolver understands, in the order the tool advertises
    /// them. There is no <c>enum</c> in the tool's input schema to build from it — an MCP input
    /// schema cannot express "one of these five, or <c>step:</c> plus any dotted type name" — so the
    /// tool's <c>section</c> parameter DESCRIPTION restates the tokens in prose instead, and
    /// <c>RealGetSchemaMcpTests</c> asserts that prose mentions every entry of THIS list. The copy is
    /// checked against the single source rather than merely trusted to match it.
    /// </summary>
    public static IReadOnlyList<string> NamedSections { get; } =
        [FullSection, MetadataSection, EnvironmentSection, VariablesSection, StepsSection];

    /// <summary>Resolves <paramref name="section"/> against <paramref name="schemaRoot"/>.</summary>
    /// <param name="schemaRoot">The composed schema's root object.</param>
    /// <param name="section">A caller-supplied section token — untrusted input.</param>
    public static SchemaSectionResolution Resolve(JsonElement schemaRoot, string section)
    {
        ArgumentNullException.ThrowIfNull(section);

        if (section.StartsWith(StepSectionPrefix, StringComparison.Ordinal))
        {
            return ResolveStepSection(schemaRoot, section[StepSectionPrefix.Length..], section);
        }

        return section switch
        {
            FullSection => new SchemaSectionResolution.Ok(schemaRoot),
            MetadataSection => FromDefs(schemaRoot, "metadata", section),
            EnvironmentSection => FromDefs(schemaRoot, "environment", section),
            VariablesSection => FromRootProperty(schemaRoot, "variables", section),
            StepsSection => FromDefs(schemaRoot, "step", section),
            _ => new SchemaSectionResolution.NotFound(UnknownSectionMessage(section)),
        };
    }

    private static SchemaSectionResolution ResolveStepSection(JsonElement schemaRoot, string stepType, string section)
    {
        // A dotted family.provider name is the only shape the schema's own `type` pattern accepts
        // ("^[a-z0-9-]+\\.[a-z0-9-]+$"), so a token without a dot cannot match any clause. Rejected
        // up front with the same message the no-such-type case gets: from the caller's side both are
        // "that step type does not exist", and splitting them would add a code path nobody acts on
        // differently.
        if (!stepType.Contains('.', StringComparison.Ordinal))
        {
            return new SchemaSectionResolution.NotFound(UnknownStepTypeMessage(stepType));
        }

        if (!TryGetStepClauses(schemaRoot, out var allOf))
        {
            // A structural fault in the embedded schema, not caller input — surfaced as NotFound
            // rather than thrown so get_schema keeps its "never crashes on a lookup" contract, with
            // a message that names the real cause rather than blaming the caller's token.
            return new SchemaSectionResolution.NotFound(
                "The embedded composed schema no longer exposes '$defs.step.allOf', so individual "
                + "step types cannot be addressed. This is a packaging fault in this server, not a "
                + "problem with the requested section.");
        }

        foreach (var clause in allOf.EnumerateArray())
        {
            if (TryReadClauseTypeConst(clause, out var clauseType)
                && string.Equals(clauseType, stepType, StringComparison.Ordinal))
            {
                return new SchemaSectionResolution.Ok(clause);
            }
        }

        return new SchemaSectionResolution.NotFound(UnknownStepTypeMessage(stepType));
    }

    private static bool TryGetStepClauses(JsonElement schemaRoot, out JsonElement allOf)
    {
        allOf = default;

        return schemaRoot.TryGetProperty("$defs", out var defs)
            && defs.ValueKind == JsonValueKind.Object
            && defs.TryGetProperty("step", out var step)
            && step.ValueKind == JsonValueKind.Object
            && step.TryGetProperty("allOf", out allOf)
            && allOf.ValueKind == JsonValueKind.Array;
    }

    private static bool TryReadClauseTypeConst(JsonElement clause, out string? typeConst)
    {
        typeConst = null;

        if (clause.ValueKind != JsonValueKind.Object
            || !clause.TryGetProperty("if", out var ifClause)
            || !ifClause.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty("type", out var type)
            || !type.TryGetProperty("const", out var constant)
            || constant.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        typeConst = constant.GetString();
        return typeConst is not null;
    }

    private static SchemaSectionResolution FromDefs(JsonElement schemaRoot, string defName, string section) =>
        schemaRoot.TryGetProperty("$defs", out var defs)
        && defs.ValueKind == JsonValueKind.Object
        && defs.TryGetProperty(defName, out var subtree)
            ? new SchemaSectionResolution.Ok(subtree)
            : new SchemaSectionResolution.NotFound(MissingSubtreeMessage(section, $"$defs.{defName}"));

    private static SchemaSectionResolution FromRootProperty(JsonElement schemaRoot, string propertyName, string section) =>
        schemaRoot.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(propertyName, out var subtree)
            ? new SchemaSectionResolution.Ok(subtree)
            : new SchemaSectionResolution.NotFound(MissingSubtreeMessage(section, $"properties.{propertyName}"));

    private static string UnknownSectionMessage(string section) =>
        // VfxCode.SanitiseForEcho, never the raw token: `section` is caller-supplied and unbounded,
        // and this message is agent-facing (M1) — the same treatment describe_step_type and
        // explain_diagnostic give their own unknown-identifier echoes.
        $"Unknown schema section '{VfxCode.SanitiseForEcho(section)}'. Valid sections are: "
        + $"{string.Join(", ", NamedSections)}, or '{StepSectionPrefix}<family>.<provider>' for one "
        + "step type (call list_step_types for the current set of dotted type names).";

    private static string UnknownStepTypeMessage(string stepType) =>
        $"Unknown step type '{VfxCode.SanitiseForEcho(stepType)}' in section "
        + $"'{StepSectionPrefix}{VfxCode.SanitiseForEcho(stepType)}'. The composed schema defines no "
        + "such family.provider. Call list_step_types for the exact, current set of dotted type names.";

    private static string MissingSubtreeMessage(string section, string jsonPath) =>
        $"The embedded composed schema no longer contains '{jsonPath}', so section "
        + $"'{VfxCode.SanitiseForEcho(section)}' cannot be resolved. This is a packaging fault in "
        + "this server (the vendored schema's structure changed with an engine pin bump), not a "
        + "problem with the requested section.";
}

/// <summary>Outcome of resolving a <c>get_schema</c> section token.</summary>
/// <remarks>
/// A closed union with a private constructor, matching this repo's other outcome types
/// (<c>LiveSchemaLoadResult</c>, <c>StepCatalogueLoadResult</c>, <c>PlanCoverageOutcome</c>).
/// Structural equality is never used on <see cref="Ok"/> — a <see cref="JsonElement"/> member does
/// not give a record a meaningful <c>Equals</c> — and no call site compares two resolutions.
/// </remarks>
public abstract record SchemaSectionResolution
{
    private SchemaSectionResolution()
    {
    }

    /// <summary>The addressed subtree of the composed schema.</summary>
    public sealed record Ok(JsonElement Subtree) : SchemaSectionResolution;

    /// <summary>The token addressed nothing; <paramref name="Message"/> is agent-safe and actionable.</summary>
    public sealed record NotFound(string Message) : SchemaSectionResolution;
}
