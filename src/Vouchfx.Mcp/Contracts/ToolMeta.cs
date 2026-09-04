using System.Text.Json.Serialization;

namespace Vouchfx.Mcp.Contracts;

// Vouchfx.Mcp.Contracts — ToolMeta (Sprint 1 / US-S1-02).
//
// The provenance stamp every successful tool result carries, so a host can tell which DSL schema
// version and which server version produced a result WITHOUT a second handshake round trip. It is
// attached at exactly one place — Tools/StructuredToolResult.Success, the single pathway all eleven
// tools use to reach the wire — rather than being a field on each of the eleven payload records; see
// that file's remarks for why that choke point is the right (and drift-proof) home.
//
// Deliberately a POSITIONAL record, unlike its Contracts/ siblings VfxError and Diagnostic: those
// two are written as explicit-constructor records purely because they need construction-time
// validation (a VFX-x-#### code, a severity from a closed set), and a positional record's primary
// constructor has no syntax for a validating body. ToolMeta has no such closed-set field — all
// three values are produced by this server itself from sources it controls (see
// Tools/ToolMetaProvider), never accepted from a caller — so it follows the other positional
// precedent in this namespace (DiagnosticLocation/DiagnosticFix) and gets free structural equality.

/// <summary>
/// Provenance attached to every successful tool result (spec §4 / US-S1-02): which DSL schema
/// version and server version produced it, and which root it was produced against.
/// </summary>
/// <param name="SchemaVersion">
/// The vouchfx language schema version, read from the vendored <c>composed-schema.v1.json</c>'s own
/// top-level <c>x-vouchfx-schema-version</c> self-declaration — see
/// <see cref="Vouchfx.Mcp.Validation.VendoredSchemaVersion"/>. Never hardcoded here: the vendored
/// schema is drift-gated against the pinned engine commit, so reading the marker from the schema
/// itself is the only way this value cannot disagree with the schema actually being validated
/// against.
/// </param>
/// <param name="ServerVersion">
/// This server's own version — the same value reported as <c>serverInfo.version</c> in the MCP
/// initialize handshake (<see cref="ServerIdentity.Version"/>, ultimately the
/// <c>&lt;Version&gt;</c> MSBuild property). Carrying it here is what removes the host's need for a
/// separate handshake call just to correlate a result with the server that produced it.
/// </param>
/// <param name="WorkspaceRoot">
/// <b>PROVISIONAL.</b> The root a result was produced against. The real workspace model does not
/// land until Sprint 3; until it does this is the process's resolved base directory
/// (<see cref="AppContext.BaseDirectory"/>, canonicalised — see
/// <c>Tools/ToolMetaProvider</c>), NOT a workspace in the Sprint-3 sense, and no host behaviour
/// should be built on its current value beyond "the server told me which root it thinks it is
/// running against". Sprint 3 replaces the source of this field without changing its name, shape,
/// or position on the wire.
/// <para>
/// <b>PRIVACY — this field is a local filesystem path, and it leaves the machine.</b> A
/// <c>dotnet tool</c> install resolves its base directory under the invoking user's profile (e.g.
/// <c>C:\Users\&lt;username&gt;\.dotnet\tools\...</c> or <c>~/.dotnet/tools/...</c>), so this value
/// commonly embeds the OS USERNAME and the local install layout — and it is stamped onto EVERY
/// successful result, which a host will routinely forward to a third-party model backend along
/// with the rest of the tool output. That is disclosure of local environment shape to a remote
/// party, and it is not covered by the engine's secret redaction (a path is not a
/// <c>${secret:...}</c> reference; see <c>Tools/ToolMetaProvider</c> for why this is nonetheless
/// not an environment read). It is accepted here only because this field is provisional and
/// short-lived.
/// </para>
/// <para>
/// <b>Sprint-3 design input:</b> the real workspace root should avoid embedding the user-profile
/// segment wherever it can — e.g. by reporting the workspace's own root (which the host chose and
/// already knows) rather than the tool's install location, or a root made relative to it — so the
/// provenance value stays useful without exporting the username by default.
/// </para>
/// </param>
public sealed record ToolMeta(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("workspaceRoot")] string WorkspaceRoot);

/// <summary>
/// The source-generated System.Text.Json serialization context for <see cref="ToolMeta"/> — no
/// reflection-based (<c>JsonSerializer.Serialize(object)</c> without an explicit
/// <c>System.Text.Json.Serialization.Metadata.JsonTypeInfo&lt;T&gt;</c>) path is used anywhere this
/// type is serialised, including on the real wire: it is the first resolver in
/// <c>Tools/StructuredToolResult</c>'s <c>TypeInfoResolverChain</c>, which
/// <c>StructuredToolResultTests</c> asserts by checking the resolved
/// <c>JsonTypeInfo.OriginatingResolver</c> is this context.
/// </summary>
// Both PropertyNamingPolicy (CamelCase) and every property's own explicit [JsonPropertyName] are
// set deliberately, even though either alone would already produce the correct camelCase wire
// names — belt-and-braces so neither a removed [JsonPropertyName] attribute nor a future change to
// this policy silently reshapes the wire casing without a test catching it. That redundancy is not
// merely stylistic here: MEASURED (see StructuredToolResultTests' resolver-chain tests), a
// JsonSourceGenerationOptions setting does NOT follow this context's metadata when the context is
// used as a resolver inside a DIFFERENT JsonSerializerOptions — only per-property attributes do.
// DefaultIgnoreCondition below is therefore inert for THIS type (it has no nullable property);
// it is kept only so all three Contracts/ contexts read identically. Any nullable property added
// to ToolMeta later MUST carry its own [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
// — the way VfxError/Diagnostic's optional fields do — or it will be emitted as null on the wire.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolMeta))]
internal sealed partial class ToolMetaJsonContext : JsonSerializerContext
{
}
