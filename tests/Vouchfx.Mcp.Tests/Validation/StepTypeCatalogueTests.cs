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

        // Bar-B surface also populated on schema-derived entries (validate path / fixtures).
        Assert.Equal(HttpRestExpectedRequiredFields, info.RequiredFields.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(info.CaptureSupported);
        Assert.False(string.IsNullOrWhiteSpace(info.FamilyIntent));
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
    public void Find_EveryType_SurfacesTheTypeLevelDescriptionFromTheSchema()
    {
        // Every one of the 25 types carries a top-level "description" on its own 'then' block as
        // of engine v1.0.0-rc.4. Measured at the rc.3→rc.4 repin: 9 of 25 carried one under rc.3
        // (http.rest, mq-publish.kafka and 14 others did not); rc.4 filled in all 25. Asserted as
        // a census rather than by naming one type, so the next pin that drops a description is
        // caught here instead of silently degrading describe_step_type's output.
        var missing = StepTypeCatalogue.All
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.Type)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Types with no schema type-level description: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void Find_TypeWithATypeLevelDescriptionInSchema_ReturnsItVerbatim()
    {
        // Spot-check that the value is the schema's own text, not merely non-empty: the census
        // above proves presence, this proves provenance.
        var expected = LoadTypeDescriptionDirectlyFromVendoredSchema("storage-assert.s3");
        Assert.False(string.IsNullOrWhiteSpace(expected));

        var info = StepTypeCatalogue.Find("storage-assert.s3");

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Description);
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
    /// Reads one type's <c>then.description</c> straight out of the vendored schema file, so
    /// <see cref="Find_TypeWithATypeLevelDescriptionInSchema_ReturnsItVerbatim"/> compares the
    /// catalogue against the schema rather than against itself.
    /// </summary>
    private static string? LoadTypeDescriptionDirectlyFromVendoredSchema(string type)
    {
        var schemaPath = Path.Combine(RepoRoot.FullName, "vendored", "composed-schema.v1.json");
        using var stream = File.OpenRead(schemaPath);
        using var document = JsonDocument.Parse(stream);

        var allOf = document.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("allOf");

        foreach (var clause in allOf.EnumerateArray())
        {
            var constValue = clause.GetProperty("if").GetProperty("properties").GetProperty("type")
                .GetProperty("const").GetString();

            if (constValue == type)
            {
                return clause.GetProperty("then").TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null;
            }
        }

        return null;
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
