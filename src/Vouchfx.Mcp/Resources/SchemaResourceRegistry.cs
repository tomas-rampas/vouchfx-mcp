using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// <c>vouchfx://schema/{version}</c> (Sprint 5 / US-S5-01) — the vendored composed schema, served
/// offline by language schema version, with <c>latest</c> as an alias for whatever version the
/// embedded document itself declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Served from the same bytes <c>get_schema</c> serves, and from nothing else.</b>
/// <see cref="VendoredComposedSchema.RawJson"/> is the single read of the embedded, drift-gated
/// <c>composed-schema.v1.json</c>; this resource hands out that string verbatim. That is what makes
/// "<c>get_schema</c> and this resource can never disagree" a structural fact rather than a claim —
/// there is one document, and neither access path transforms it.
/// </para>
/// <para>
/// <b>The alias cannot drift from its target</b>, for the same reason. <c>latest</c> and the literal
/// version resolve through the identical <see cref="Resolve"/> branch and return the identical
/// string instance, so the AC's "byte-identical content" is not something this type has to preserve
/// — there is no second value that could differ. The version comparison itself is ORDINAL against
/// <see cref="VendoredSchemaVersion.Value"/>, the schema's own <c>x-vouchfx-schema-version</c>
/// marker: the same value <c>ToolMeta.schemaVersion</c> stamps onto every successful tool result, so
/// a host that read the version off a tool result can address the schema with it directly.
/// </para>
/// <para>
/// <b>Deliberately CLI-free, unlike <c>get_schema</c>.</b> That tool cross-verifies the embedded
/// copy against a pinned engine's own <c>vouchfx schema</c> export when one is installed, and reports
/// any divergence as a VFX-D-1106 diagnostic on the still-successful result. A resource has no
/// diagnostic channel to carry such a finding — a <c>resources/read</c> reply is content or an error,
/// with nowhere to put "here it is, and by the way it differs" — so this resource does not probe at
/// all rather than either dropping the finding silently or failing a read over it. A host that wants
/// the cross-check calls <c>get_schema</c>; a host that wants the document reads this. Both get the
/// same bytes either way, which is the property that makes the split safe.
/// </para>
/// <para>
/// <b>An unknown version is a read ERROR, not an empty document.</b> <c>vouchfx://schema/v2</c>
/// against a v1 pin means the host is asking for something this build does not have, and answering
/// with the v1 schema under a v2 URI would be worse than refusing: the host would cache v1 bytes
/// under a v2 key. <see cref="McpException"/> is the SDK's own pattern for an unresolvable template
/// parameter — see <see cref="DiagnosticResourceRegistry"/>, which established it here in Sprint 1.
/// </para>
/// </remarks>
public static class SchemaResourceRegistry
{
    /// <summary>Creates the templated composed-schema resource.</summary>
    public static McpServerResource Create() =>
        McpServerResource.Create(
            (string version) => Resolve(version),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.SchemaTemplate,
                Name = "vouchfx composed JSON Schema",
                Description =
                    "The vouchfx .e2e.yaml language's composed JSON Schema (draft 2020-12) — the exact "
                    + "contract validate_suite checks a suite against, byte-exact from the pinned engine "
                    + "commit. Address it by language schema version (e.g. '"
                    + "v1'), or use '" + VouchfxResourceUris.LatestSchemaVersionAlias + "' to get "
                    + "whatever version this build embeds without knowing it in advance — both return "
                    + "identical bytes. Served entirely offline; no vouchfx CLI needed.",
                MimeType = ResourceJson.MimeType,
            });

    /// <summary>
    /// Resolves <paramref name="version"/> to the embedded schema's raw JSON, or throws for a
    /// version this build does not carry.
    /// </summary>
    internal static string Resolve(string? version)
    {
        var requested = ResourceArgumentGuard.Require(version, "version");

        if (string.Equals(requested, VouchfxResourceUris.LatestSchemaVersionAlias, StringComparison.Ordinal)
            || string.Equals(requested, VendoredSchemaVersion.Value, StringComparison.Ordinal))
        {
            return VendoredComposedSchema.RawJson;
        }

        throw new McpException(
            $"This build embeds the vouchfx language schema version '{VendoredSchemaVersion.Value}' only; "
            + $"'{VfxCode.SanitiseForEcho(requested)}' is not available. Read "
            + $"'vouchfx://schema/{VouchfxResourceUris.LatestSchemaVersionAlias}' for whichever version "
            + "this build carries.");
    }
}
