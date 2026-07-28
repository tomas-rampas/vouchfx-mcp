using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="StepCatalogueParser"/> — pure parse of Spec A bar-B <c>list --json</c>
/// documents, including EDGE-004 fail-fast when field metadata is missing.
/// </summary>
public class StepCatalogueParserTests
{
    [Fact]
    public void Parse_SingleHttpRestBarB_ReturnsRequiredOptionalCaptureAndFamilyIntent()
    {
        var types = StepCatalogueParser.Parse(RichListJsonFixture.SingleHttpRestJson);

        var httpRest = Assert.Single(types);
        Assert.Equal("http.rest", httpRest.Type);
        Assert.Equal("http", httpRest.Family);
        Assert.Equal("rest", httpRest.Provider);
        Assert.Equal(["method", "path", "target"], httpRest.RequiredFields);
        Assert.Equal(["body", "expect", "headers"], httpRest.OptionalFields);
        Assert.True(httpRest.CaptureSupported);
        Assert.Contains("HTTP", httpRest.FamilyIntent, StringComparison.Ordinal);

        Assert.Contains(httpRest.Fields, f => f.Name == "method" && f.Required);
        Assert.Contains(httpRest.Fields, f => f.Name == "headers" && !f.Required);
    }

    [Fact]
    public void Parse_FullVendoredDerivedFixture_ContainsCoreTypesWithBarBFields()
    {
        var types = StepCatalogueParser.Parse(RichListJsonFixture.Json);

        Assert.Equal(25, types.Count);
        Assert.Contains(types, t => t.Type == "http.rest");
        Assert.Contains(types, t => t.Type == "db-assert.postgres");

        foreach (var type in types)
        {
            Assert.False(string.IsNullOrWhiteSpace(type.FamilyIntent));
            Assert.NotNull(type.RequiredFields);
            Assert.NotNull(type.OptionalFields);
        }
    }

    [Fact]
    public void Parse_ThinPreSpecACatalogue_ThrowsNamingMissingRequiredFields()
    {
        var ex = Assert.Throws<StepCatalogueParseException>(
            () => StepCatalogueParser.Parse(RichListJsonFixture.ThinJson));

        Assert.Contains("requiredFields", ex.Message, StringComparison.Ordinal);
        Assert.Contains("http.rest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingCaptureSupported_Throws()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "stepTypes": [
                {
                  "type": "http.rest",
                  "family": "http",
                  "provider": "rest",
                  "requiredFields": ["method"],
                  "optionalFields": [],
                  "familyIntent": "Call HTTP endpoints."
                }
              ]
            }
            """;

        var ex = Assert.Throws<StepCatalogueParseException>(() => StepCatalogueParser.Parse(json));
        Assert.Contains("captureSupported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyFamilyIntent_Throws()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "stepTypes": [
                {
                  "type": "http.rest",
                  "family": "http",
                  "provider": "rest",
                  "requiredFields": [],
                  "optionalFields": [],
                  "captureSupported": true,
                  "familyIntent": "   "
                }
              ]
            }
            """;

        var ex = Assert.Throws<StepCatalogueParseException>(() => StepCatalogueParser.Parse(json));
        Assert.Contains("familyIntent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyStepTypes_Throws()
    {
        const string json = """{ "schemaVersion": 1, "stepTypes": [] }""";

        var ex = Assert.Throws<StepCatalogueParseException>(() => StepCatalogueParser.Parse(json));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<StepCatalogueParseException>(() => StepCatalogueParser.Parse("{ not json"));
    }
}
