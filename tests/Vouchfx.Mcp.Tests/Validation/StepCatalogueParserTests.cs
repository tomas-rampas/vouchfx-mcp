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
    public void Parse_UnsortedFieldArrays_SortsRequiredAndOptionalOrdinal()
    {
        // Engine emission order is not guaranteed; tool output must be deterministic.
        const string json = """
            {
              "schemaVersion": 1,
              "stepTypes": [
                {
                  "type": "http.rest",
                  "family": "http",
                  "provider": "rest",
                  "requiredFields": ["target", "method", "path"],
                  "optionalFields": ["headers", "body", "expect"],
                  "captureSupported": true,
                  "familyIntent": "Call HTTP endpoints."
                }
              ]
            }
            """;

        var httpRest = Assert.Single(StepCatalogueParser.Parse(json));

        Assert.Equal(["method", "path", "target"], httpRest.RequiredFields);
        Assert.Equal(["body", "expect", "headers"], httpRest.OptionalFields);
        Assert.Equal(
            ["body", "expect", "headers", "method", "path", "target"],
            httpRest.Fields.Select(f => f.Name).ToArray());
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
            Assert.Equal("core", type.Tier);
            Assert.True(type.SupportsVerifyMode);
            Assert.NotNull(type.DocsUrl);
            Assert.NotNull(type.Example);
        }
    }

    [Fact]
    public void Parse_Rc6ProviderInfoMembers_AreRelayedAsWritten()
    {
        // The members engine v1.0.0-rc.6 appended to every entry (upstream ask U5), in the shape the
        // pinned engine emits for a Core type — after the two group arrays engine v1.0.0-rc.4 added,
        // which this parser does not read and which must not disturb it.
        var info = Assert.Single(StepCatalogueParser.Parse(SingleEntryWith("""
            "exactlyOneOfGroups": [],
            "atLeastOneOfGroups": [],
            "tier": "core",
            "supportedVerifyModes": ["IMMEDIATE", "RETRY"],
            "docsUrl": "https://vouchfx.io/language-reference/#httprest",
            "example": "steps:\n  - id: http-rest\n    type: http.rest\n"
            """)));

        Assert.Equal("core", info.Tier);
        Assert.True(info.SupportsVerifyMode);
        Assert.Equal("https://vouchfx.io/language-reference/#httprest", info.DocsUrl);
        Assert.Equal("steps:\n  - id: http-rest\n    type: http.rest\n", info.Example);
    }

    [Fact]
    public void Parse_AnEngineOlderThanRc6_LeavesEveryProviderInfoMemberNull()
    {
        // An engine that predates the U5 members omits them. They are optional, unlike the bar-B
        // fields, so the parse succeeds and each reads back null — omitted from the wire, never
        // defaulted.
        var info = Assert.Single(StepCatalogueParser.Parse(RichListJsonFixture.SingleHttpRestJson));

        Assert.Null(info.Tier);
        Assert.Null(info.SupportsVerifyMode);
        Assert.Null(info.DocsUrl);
        Assert.Null(info.Example);
    }

    [Fact]
    public void Parse_ProviderInfoMembersTheEngineReportsAsNull_ReadBackAsNull()
    {
        // The engine's own null cases: a non-Core entry has no docsUrl or example, and a library
        // caller that supplies no Core set gets a null tier. The engine never nulls
        // supportedVerifyModes; nulling it here covers the parser's defensive arm for it.
        var info = Assert.Single(StepCatalogueParser.Parse(SingleEntryWith("""
            "tier": null,
            "supportedVerifyModes": null,
            "docsUrl": null,
            "example": null
            """)));

        Assert.Null(info.Tier);
        Assert.Null(info.SupportsVerifyMode);
        Assert.Null(info.DocsUrl);
        Assert.Null(info.Example);
    }

    [Fact]
    public void Parse_BlankProviderInfoStrings_ReadBackAsNull()
    {
        // A blank string carries no more information than an absent one, so it is not relayed as a
        // value a host would then have to second-guess.
        var info = Assert.Single(StepCatalogueParser.Parse(SingleEntryWith("""
            "tier": "",
            "docsUrl": "   ",
            "example": ""
            """)));

        Assert.Null(info.Tier);
        Assert.Null(info.DocsUrl);
        Assert.Null(info.Example);
    }

    [Theory]
    [InlineData("""["IMMEDIATE"]""")]
    [InlineData("[]")]
    [InlineData("""["retry"]""")]
    public void Parse_SupportedVerifyModesWithoutRetry_ReportsNotRetryCapable(string modes)
    {
        // Spec §5.2's supportsVerifyMode is "RETRY-capable": true only when the list names the wire
        // token RETRY exactly. The tokens are case-sensitive on the wire, so "retry" is not it.
        var info = Assert.Single(StepCatalogueParser.Parse(SingleEntryWith($"\"supportedVerifyModes\": {modes}")));

        Assert.False(info.SupportsVerifyMode);
    }

    [Theory]
    [InlineData("tier", "1")]
    [InlineData("docsUrl", "true")]
    [InlineData("example", "{}")]
    [InlineData("supportedVerifyModes", "\"RETRY\"")]
    [InlineData("supportedVerifyModes", "[1]")]
    [InlineData("supportedVerifyModes", "[\"\"]")]
    public void Parse_ProviderInfoMemberOfTheWrongJsonType_FailsTheWholeParse(string member, string value)
    {
        // Optional, but not lenient: a PRESENT member of the wrong shape means the frozen v1
        // catalogue contract changed, and the parser fails closed exactly as it does for a malformed
        // bar-B field rather than relaying half a catalogue.
        var ex = Assert.Throws<StepCatalogueParseException>(
            () => StepCatalogueParser.Parse(SingleEntryWith($"\"{member}\": {value}")));

        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        Assert.Contains("http.rest", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// A one-entry bar-B <c>http.rest</c> document with <paramref name="extraMembers"/> (one or more
    /// comma-separated JSON members, never empty) appended to the entry.
    /// </summary>
    private static string SingleEntryWith(string extraMembers) => $$"""
        {
          "schemaVersion": 1,
          "stepTypes": [
            {
              "type": "http.rest",
              "family": "http",
              "provider": "rest",
              "requiredFields": ["method", "path", "target"],
              "optionalFields": ["body", "expect", "headers"],
              "captureSupported": true,
              "familyIntent": "Call HTTP endpoints.",
              {{extraMembers}}
            }
          ]
        }
        """;
}
