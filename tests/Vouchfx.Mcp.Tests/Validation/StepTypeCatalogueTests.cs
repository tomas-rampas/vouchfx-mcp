using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="StepTypeCatalogue"/>: the service that derives the vouchfx step-type
/// catalogue from the embedded <c>composed-schema.v1.json</c> at run time — never from a
/// hand-maintained duplicate list, which would rot as the schema is re-pinned.
/// </summary>
public class StepTypeCatalogueTests
{
    private static readonly string[] HttpRestExpectedRequiredFields = ["method", "path", "target"];
    private static readonly string[] ScriptCsharpExpectedRequiredOneOfGroups = ["code", "file"];

    [Fact]
    public void All_MatchesTheExactSetDerivedDirectlyFromTheVendoredSchemaFile()
    {
        // Independently re-derives the expected type set by parsing vendored/composed-schema.v1.json
        // directly here, rather than reusing StepTypeCatalogue itself — otherwise this would just
        // be asserting the catalogue equals itself and could never catch a derivation bug.
        var expectedTypes = LoadTypeConstsDirectlyFromVendoredSchema();
        var actualTypes = StepTypeCatalogue.All.Select(t => t.Type);

        Assert.Equal(
            expectedTypes.OrderBy(t => t, StringComparer.Ordinal),
            actualTypes.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void All_ContainsHttpRestAndDbAssertPostgres()
    {
        Assert.Contains(StepTypeCatalogue.All, t => t.Type == "http.rest");
        Assert.Contains(StepTypeCatalogue.All, t => t.Type == "db-assert.postgres");
    }

    [Fact]
    public void Find_HttpRest_ReturnsFamilyProviderAndRequiredFieldsMatchingSchema()
    {
        var info = StepTypeCatalogue.Find("http.rest");

        Assert.NotNull(info);
        Assert.Equal("http", info!.Family);
        Assert.Equal("rest", info.Provider);
        Assert.Null(info.RequiredOneOf);

        var requiredNames = info.Fields.Where(f => f.Required).Select(f => f.Name);
        Assert.Equal(
            HttpRestExpectedRequiredFields,
            requiredNames.OrderBy(n => n, StringComparer.Ordinal));

        var optionalNames = info.Fields.Where(f => !f.Required).Select(f => f.Name);
        Assert.Contains("headers", optionalNames);
        Assert.Contains("body", optionalNames);
        Assert.Contains("expect", optionalNames);
    }

    [Fact]
    public void Find_ScriptCsharp_HasNoIndividuallyRequiredField_ButExposesRequiredOneOfCodeOrFile()
    {
        // Schema-structure surprise (see STEP 0): script.csharp's 'then' block has no flat
        // 'required' array at all — it uses a 'oneOf' of single-field 'required' groups (code
        // XOR file) instead, unlike every other one of the 25 step types.
        var info = StepTypeCatalogue.Find("script.csharp");

        Assert.NotNull(info);
        Assert.DoesNotContain(info!.Fields, f => f.Required);
        Assert.NotNull(info.RequiredOneOf);

        var flattenedGroups = info.RequiredOneOf!
            .Select(group => string.Join(",", group))
            .OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(ScriptCsharpExpectedRequiredOneOfGroups, flattenedGroups);
    }

    [Fact]
    public void Find_TypeWithNoTypeLevelDescriptionInSchema_ReturnsNullDescription()
    {
        // http.rest's own 'then' block carries no top-level "description" key in the schema
        // (only some of the 25 types do) — the catalogue must not invent one.
        var info = StepTypeCatalogue.Find("http.rest");

        Assert.NotNull(info);
        Assert.Null(info!.Description);
    }

    [Fact]
    public void Find_TypeWithATypeLevelDescriptionInSchema_ReturnsIt()
    {
        var info = StepTypeCatalogue.Find("mq-publish.kafka");

        Assert.NotNull(info);
        Assert.Null(info!.Description); // mq-publish.kafka has no then.description either.

        var withDescription = StepTypeCatalogue.Find("storage-assert.s3");
        Assert.NotNull(withDescription);
        Assert.False(string.IsNullOrWhiteSpace(withDescription!.Description));
    }

    [Fact]
    public void Find_UnknownType_ReturnsNull()
    {
        Assert.Null(StepTypeCatalogue.Find("nope.nope"));
    }

    private static List<string> LoadTypeConstsDirectlyFromVendoredSchema()
    {
        var schemaPath = Path.Combine(RepoRoot.FullName, "vendored", "composed-schema.v1.json");
        using var stream = File.OpenRead(schemaPath);
        using var document = JsonDocument.Parse(stream);

        var allOf = document.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("allOf");

        var types = new List<string>();
        foreach (var clause in allOf.EnumerateArray())
        {
            var constValue = clause.GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("const").GetString();
            Assert.NotNull(constValue);
            types.Add(constValue!);
        }

        return types;
    }

    /// <summary>
    /// Walks up from the test assembly's own output directory to the repo root. Mirrors
    /// <see cref="VendoredArtefactsTests"/>'s identically-named helper.
    /// </summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
            var repoRoot = testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

            return repoRoot;
        }
    }
}
