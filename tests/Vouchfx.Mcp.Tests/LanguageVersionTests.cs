using System.Text.Json;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

public class LanguageVersionTests
{
    private const string Steps = "steps:\n  - id: fine\n    type: http.rest\n    target: api\n    method: GET\n    path: /x\n";

    [Theory]
    [InlineData("", false, "v1")]
    [InlineData("  schemaVersion: v1\n", false, "v1")]
    [InlineData("  schemaVersion: v2\n", true, "v2")]
    public void TelemetryOptOut_RequiresExplicitV2(string version, bool valid, string expectedVersion)
    {
        var result = SuiteValidator.AnalyseYaml("metadata:\n" + version + "  telemetry: false\n" + Steps, ValidationLevel.Schema);
        Assert.Equal(valid, result.Valid);
        Assert.Equal(expectedVersion, result.SchemaVersion ?? "v1");
        var rendered = Vouchfx.Mcp.Tools.StructuredToolResult.Success(result);
        Assert.Equal(expectedVersion, rendered.StructuredContent!.Value.GetProperty("meta").GetProperty("schemaVersion").GetString());
    }

    [Theory]
    [InlineData("v999")]
    [InlineData("42")]
    [InlineData("null")]
    public void UnknownVersion_IsRefusedEvenForSemanticOnly(string version)
    {
        var result = SuiteValidator.AnalyseYaml("metadata:\n  schemaVersion: " + version + "\n" + Steps, ValidationLevel.Semantic);
        Assert.False(result.Valid);
        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, error => Assert.True(error.Message.Length <= 4096));
    }

    [Fact]
    public void V2Schema_HasOwnVersionMarker_AndDoesNotChangeV1()
    {
        using var v1 = JsonDocument.Parse(VendoredComposedSchema.ForVersion("v1"));
        using var v2 = JsonDocument.Parse(VendoredComposedSchema.ForVersion("v2"));
        Assert.Equal("v1", v1.RootElement.GetProperty("x-vouchfx-schema-version").GetString());
        Assert.Equal("v2", v2.RootElement.GetProperty("x-vouchfx-schema-version").GetString());
    }
}
