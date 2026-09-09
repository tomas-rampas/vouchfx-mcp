using ModelContextProtocol;
using Vouchfx.Mcp.Resources;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Resources;

/// <summary>
/// US-S5-01 AC-002: <c>vouchfx://schema/{version}</c> serves the vendored composed schema by version,
/// with <c>latest</c> as an alias for the vendored document's own version marker.
/// </summary>
public class SchemaResourceRegistryTests
{
    [Fact]
    public void TheLatestAlias_ReturnsTheEmbeddedSchemaVerbatim() =>
        Assert.Same(
            VendoredComposedSchema.RawJson,
            SchemaResourceRegistry.Resolve(VouchfxResourceUris.LatestSchemaVersionAlias));

    [Fact]
    public void TheLiteralVersion_ReturnsTheEmbeddedSchemaVerbatim() =>
        Assert.Same(VendoredComposedSchema.RawJson, SchemaResourceRegistry.Resolve(VendoredSchemaVersion.Value));

    [Fact]
    public void TheAliasAndTheLiteralVersion_AreByteIdentical()
    {
        // AC-002's own wording. Asserted as reference equality FIRST, because that is the stronger
        // statement and the one that says WHY it holds — both branches hand out the same string
        // instance, so there is no second document that could drift — and then as value equality,
        // which is what the AC literally asks for and what a host observes.
        var viaAlias = SchemaResourceRegistry.Resolve(VouchfxResourceUris.LatestSchemaVersionAlias);
        var viaVersion = SchemaResourceRegistry.Resolve(VendoredSchemaVersion.Value);

        Assert.Same(viaAlias, viaVersion);
        Assert.Equal(viaAlias, viaVersion, StringComparer.Ordinal);
    }

    [Fact]
    public void TheAliasedVersion_IsTheSameMarkerToolMetaStamps() =>
        // The point of the alias (AC-002: "so a host never has to know the literal version string in
        // advance") is that the literal it aliases is the one a host already saw on a tool result's
        // meta.schemaVersion. Asserted against the provider's own composed value rather than against
        // VendoredSchemaVersion twice, so this fails if the stamp is ever sourced from elsewhere.
        Assert.Equal(VendoredSchemaVersion.Value, Vouchfx.Mcp.Tools.ToolMetaProvider.Current.SchemaVersion);

    [Theory]
    [InlineData("v2")]
    [InlineData("V1")]     // Ordinal comparison: a case variant is not the pinned version.
    [InlineData("LATEST")] // Same, for the alias.
    [InlineData("1")]
    public void AnUnknownVersion_IsARefusalRatherThanTheV1DocumentUnderAnotherName(string version)
    {
        var ex = Assert.Throws<McpException>(() => SchemaResourceRegistry.Resolve(version));

        // Names the version this build DOES carry, so the host's next read succeeds.
        Assert.Contains(VendoredSchemaVersion.Value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANetworkShapedVersion_IsRefusedByTheSharedArgumentGuard()
    {
        var ex = Assert.Throws<McpException>(() => SchemaResourceRegistry.Resolve(@"\\attacker\share"));

        Assert.Contains("network/UNC", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServedDocument_IsTheSameOneGetSchemaServes() =>
        // "get_schema and this resource can never disagree" is structural, not a claim: there is one
        // embedded document and neither access path transforms it. Pinned so a future change that
        // introduced a transform on either side fails here.
        Assert.Same(VendoredComposedSchema.RawJson, SchemaResourceRegistry.Resolve("latest"));
}
