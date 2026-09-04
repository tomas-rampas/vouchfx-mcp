using System.Reflection;
using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers the vendored engine artefacts embedded into the <c>Vouchfx.Mcp</c> assembly (see
/// <c>vendored/README.md</c> and the <c>EmbeddedResource</c> items in
/// <c>src/Vouchfx.Mcp/Vouchfx.Mcp.csproj</c>): that all three resources exist, are non-empty,
/// parse/contain what they are expected to, and — guarding specifically against csproj wiring
/// drift — that each embedded resource's bytes are identical to the corresponding file currently
/// committed in <c>vendored/</c>.
/// </summary>
public class VendoredArtefactsTests
{
    // The suffix after this prefix is exactly the vendored/ file name (see
    // Vouchfx.Mcp.csproj's EmbeddedResource LogicalName metadata) — kept as a single source of
    // truth here so EmbeddedResource_Bytes_EqualVendoredFileOnDisk can derive one from the other
    // instead of maintaining a second, independently-drifting resource-name-to-file-name map.
    private const string ResourceNamePrefix = "Vouchfx.Mcp.Vendored.";

    private const string SchemaResourceName = ResourceNamePrefix + "composed-schema.v1.json";
    private const string LanguageReferenceResourceName = ResourceNamePrefix + "language-reference.md";
    private const string RecipesResourceName = ResourceNamePrefix + "recipes.md";

    [Theory]
    [InlineData(SchemaResourceName)]
    [InlineData(LanguageReferenceResourceName)]
    [InlineData(RecipesResourceName)]
    public void EmbeddedResource_Exists_AndIsNonEmpty(string resourceName)
    {
        using var stream = OpenResource(resourceName);

        Assert.True(stream.Length > 0, $"Embedded resource '{resourceName}' is present but empty.");
    }

    [Fact]
    public void ComposedSchema_Parses_AndNamesDraft202012()
    {
        using var stream = OpenResource(SchemaResourceName);
        using var document = JsonDocument.Parse(stream);

        var schemaValue = document.RootElement.GetProperty("$schema").GetString();

        Assert.NotNull(schemaValue);
        Assert.Contains("2020-12", schemaValue!, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageReference_ContainsExpectedSentinelHeadings()
    {
        var text = ReadResourceText(LanguageReferenceResourceName);

        // Stable, top-of-file headings from the engine's generated docs/language-reference.md —
        // chosen because a regeneration that changed the step-family catalogue would still keep
        // both of these (the title and the "## Step types" section header itself).
        Assert.Contains("# vouchfx Language Reference", text, StringComparison.Ordinal);
        Assert.Contains("## Step types", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipes_ContainsExpectedSentinelHeadings()
    {
        var text = ReadResourceText(RecipesResourceName);

        // Stable headings from the engine's docs/recipes.md: the document title, and the CI
        // integration section that sits near the end of the file — together they guard against
        // both a truncated download and a wholesale wrong-file substitution.
        Assert.Contains("# vouchfx Recipes: Common Patterns and Examples", text, StringComparison.Ordinal);
        Assert.Contains("## CI integration with GitHub Actions", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SchemaResourceName)]
    [InlineData(LanguageReferenceResourceName)]
    [InlineData(RecipesResourceName)]
    public void EmbeddedResource_Bytes_EqualVendoredFileOnDisk(string resourceName)
    {
        // Guards csproj wiring drift specifically: if Vouchfx.Mcp.csproj's EmbeddedResource
        // Include/LogicalName for ANY of the three artefacts ever pointed at a stale or wrong
        // copy, the content-specific tests above could still pass against whatever got embedded
        // while the committed vendored/ file itself had moved on (or vice versa) — and a stale
        // embedded markdown copy in particular would not be caught by the JSON-only schema check
        // this test replaces. Parameterised across all three resources, not just the schema, so
        // that gap is closed for every vendored artefact.
        var embeddedBytes = ReadResourceBytes(resourceName);
        var vendoredFileName = resourceName[ResourceNamePrefix.Length..];
        var vendoredFilePath = Path.Combine(RepoRoot.FullName, "vendored", vendoredFileName);
        var vendoredBytes = File.ReadAllBytes(vendoredFilePath);

        Assert.Equal(vendoredBytes, embeddedBytes);
    }

    private static Stream OpenResource(string resourceName)
    {
        var assembly = typeof(ServerIdentity).Assembly;

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' was not found in '{assembly.FullName}'. " +
                "Known resources: " + string.Join(", ", assembly.GetManifestResourceNames()));
    }

    private static byte[] ReadResourceBytes(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public void VendoredSchema_StepSurface_IsStillClosedByUnevaluatedProperties()
    {
        // The vendored schema is EXECUTABLE INPUT to this repo's logic, not inert data.
        // SuiteValidator branches on schema-derived keyword shapes — it rewrites the blank-keyword
        // unevaluatedProperties, additionalProperties and per-field-false rejections, and suppresses
        // the annotation-withholding cascade that the first of those brings with it. So a future pin
        // can change what validate_suite reports with no code change in this repo, which is exactly
        // what rc.3 -> rc.4 did: the step surface went from "additionalProperties": true to
        // "unevaluatedProperties": false, and seven new composites appeared. Pinned here so the next
        // such change fails a test that names it rather than silently degrading behaviour.
        var schemaPath = Path.Combine(RepoRoot.FullName, "vendored", "composed-schema.v1.json");
        using var stream = File.OpenRead(schemaPath);
        using var document = JsonDocument.Parse(stream);

        var step = document.RootElement.GetProperty("$defs").GetProperty("step");

        Assert.True(
            step.TryGetProperty("unevaluatedProperties", out var closure),
            "$defs/step no longer declares 'unevaluatedProperties'. SuiteValidator's cascade " +
            "suppression and closure-message rewriting are built on that closure — re-check both " +
            "against the pinned CLI (see RealValidateAgainstPinnedCliTests) before accepting the " +
            "new schema.");
        Assert.Equal(JsonValueKind.False, closure.ValueKind);
    }

    [Fact]
    public void VendoredSchema_StillDeclaresItsOwnLanguageVersionMarker()
    {
        // US-S1-02: meta.schemaVersion on every tool result is READ from this marker rather than
        // hardcoded anywhere in src/ (see Validation/VendoredSchemaVersion), precisely so the value
        // a host is told cannot disagree with the schema actually being validated against. Pinned
        // here, against the REPO file rather than the embedded copy (the byte-equality test above
        // ties the two together), so a pin bump that moves or removes the marker fails a test that
        // names it instead of surfacing as an InvalidOperationException at server startup.
        //
        // The marker read is the schema's own top-level x-vouchfx-schema-version keyword — the
        // purpose-built self-declaration — NOT $defs/metadata/properties/schemaVersion's "const",
        // which is a validation rule about what an author may write in a suite and merely happens
        // to carry the same string today. See VendoredSchemaVersion's remarks for why that
        // coincidence is a trap (widening that const to an enum during a v1->v2 transition would
        // delete it, and it is stamped onto every tool result).
        var schemaPath = Path.Combine(RepoRoot.FullName, "vendored", "composed-schema.v1.json");
        using var stream = File.OpenRead(schemaPath);
        using var document = JsonDocument.Parse(stream);

        Assert.True(
            document.RootElement.TryGetProperty(VendoredSchemaVersion.MarkerKeyword, out var marker),
            $"The composed schema no longer declares a top-level '{VendoredSchemaVersion.MarkerKeyword}' "
            + "keyword. That keyword IS the schema's own version marker; update "
            + "Validation/VendoredSchemaVersion to read the marker's new location before accepting "
            + "the new schema.");
        Assert.Equal(JsonValueKind.String, marker.ValueKind);
        Assert.False(string.IsNullOrEmpty(marker.GetString()));

        // The production reader and the schema on disk must agree — this is what makes
        // ToolMetaTests' "schemaVersion comes from the schema" assertion non-circular.
        Assert.Equal(marker.GetString(), VendoredSchemaVersion.Value);
    }

    private static string ReadResourceText(string resourceName)
    {
        using var stream = OpenResource(resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Walks up from the test assembly's own output directory to the repo root. Mirrors
    /// <see cref="RealServerProcessTests"/>'s <c>ResolveServerDllPath</c>: <see cref="AppContext.BaseDirectory"/>
    /// for this test run looks like ".../tests/Vouchfx.Mcp.Tests/bin/&lt;Configuration&gt;/&lt;TFM&gt;/".
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
