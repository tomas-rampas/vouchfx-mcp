using System.Text.Json;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Tests.Schema;

/// <summary>
/// US-S2-01: <c>get_schema</c>'s section addressing — the mapping from a caller's <c>section</c>
/// token to a subtree of the embedded composed schema, and the fail-closed answer for a token that
/// addresses nothing.
/// </summary>
/// <remarks>
/// Driven against the REAL embedded <c>composed-schema.v1.json</c> (never a hand-written stand-in)
/// for the same reason <c>StepTypeCatalogueTests</c> is: the section table is a claim about that
/// document's actual structure, and a synthetic fixture would keep passing after an engine pin bump
/// moved something. The one synthetic-input test below is deliberately about resolver BEHAVIOUR
/// (a malformed token), not about the schema's shape.
/// </remarks>
public class SchemaSectionResolverTests
{
    [Fact]
    public void Resolve_FullSection_ReturnsTheWholeSchemaDocument()
    {
        using var document = VendoredComposedSchema.Parse();

        var resolved = SchemaSectionResolver.Resolve(document.RootElement, SchemaSectionResolver.FullSection);

        var ok = Assert.IsType<SchemaSectionResolution.Ok>(resolved);
        Assert.True(ok.Subtree.TryGetProperty("$defs", out _));
        Assert.True(ok.Subtree.TryGetProperty("properties", out _));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("environment")]
    [InlineData("variables")]
    [InlineData("steps")]
    public void Resolve_NamedSection_ReturnsADescribedObjectSubtreeThatIsNotTheWholeDocument(string section)
    {
        using var document = VendoredComposedSchema.Parse();

        var resolved = SchemaSectionResolver.Resolve(document.RootElement, section);

        var ok = Assert.IsType<SchemaSectionResolution.Ok>(resolved);
        // A named section addresses a genuine subtree: it never carries the document's own $defs
        // table (which only the full document does), and it always carries a description the
        // summary renderer can read.
        Assert.False(ok.Subtree.TryGetProperty("$defs", out _));
        Assert.True(ok.Subtree.TryGetProperty("description", out var description));
        Assert.Equal(JsonValueKind.String, description.ValueKind);
    }

    [Fact]
    public void Resolve_StepSection_ReturnsOnlyThatStepTypesOwnClause()
    {
        using var document = VendoredComposedSchema.Parse();

        var resolved = SchemaSectionResolver.Resolve(document.RootElement, "step:mq-expect.kafka");

        var ok = Assert.IsType<SchemaSectionResolution.Ok>(resolved);
        var raw = ok.Subtree.GetRawText();

        Assert.Contains("mq-expect.kafka", raw, StringComparison.Ordinal);
        // Only THAT step type's subtree: no other registered type's discriminator survives.
        Assert.DoesNotContain("http.rest", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("db-assert.postgres", raw, StringComparison.Ordinal);
        // The clause's own `then` block is what carries the type's fields.
        Assert.True(ok.Subtree.GetProperty("then").TryGetProperty("properties", out _));
    }

    [Fact]
    public void Resolve_EveryStepTypeTheSchemaDefines_IsAddressableAsAStepSection()
    {
        using var document = VendoredComposedSchema.Parse();

        // Anti-vacuity: the vendored schema really does define a non-trivial vocabulary.
        Assert.NotEmpty(Vouchfx.Mcp.Validation.StepTypeCatalogue.All);

        foreach (var stepType in Vouchfx.Mcp.Validation.StepTypeCatalogue.All)
        {
            var resolved = SchemaSectionResolver.Resolve(document.RootElement, "step:" + stepType.Type);

            var ok = Assert.IsType<SchemaSectionResolution.Ok>(resolved);
            Assert.Contains(stepType.Type, ok.Subtree.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Resolve_UnknownStepProvider_ReportsNotFoundRatherThanAnEmptySuccess()
    {
        using var document = VendoredComposedSchema.Parse();

        var resolved = SchemaSectionResolver.Resolve(document.RootElement, "step:mq-expect.nonexistent-provider");

        var notFound = Assert.IsType<SchemaSectionResolution.NotFound>(resolved);
        Assert.Contains("mq-expect.nonexistent-provider", notFound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnknownNamedSection_ReportsNotFound()
    {
        using var document = VendoredComposedSchema.Parse();

        var resolved = SchemaSectionResolver.Resolve(document.RootElement, "not-a-section");

        Assert.IsType<SchemaSectionResolution.NotFound>(resolved);
    }

    [Fact]
    public void Resolve_SectionMatchingIsOrdinalAndCaseSensitive()
    {
        using var document = VendoredComposedSchema.Parse();

        // The tool's `section` parameter description names lower-case tokens; accepting "Metadata"
        // would make that advertisement a lie and let two spellings drift apart.
        Assert.IsType<SchemaSectionResolution.NotFound>(
            SchemaSectionResolver.Resolve(document.RootElement, "Metadata"));
        Assert.IsType<SchemaSectionResolution.NotFound>(
            SchemaSectionResolver.Resolve(document.RootElement, "STEP:http.rest"));
    }

    [Fact]
    public void Resolve_StepSectionWithNoTypeAfterThePrefix_ReportsNotFoundWithoutThrowing()
    {
        using var document = VendoredComposedSchema.Parse();

        // Malformed caller input, not a schema-shape claim — the one deliberately synthetic case.
        Assert.IsType<SchemaSectionResolution.NotFound>(
            SchemaSectionResolver.Resolve(document.RootElement, "step:"));
        Assert.IsType<SchemaSectionResolution.NotFound>(
            SchemaSectionResolver.Resolve(document.RootElement, "step:no-dot-here"));
    }

    [Fact]
    public void Resolve_NotFoundMessage_CapsAnOverlongSectionToken()
    {
        using var document = VendoredComposedSchema.Parse();

        var abusive = "step:" + new string('x', 400) + ".evil";

        var notFound = Assert.IsType<SchemaSectionResolution.NotFound>(
            SchemaSectionResolver.Resolve(document.RootElement, abusive));

        // Untrusted input is never echoed raw into an agent-facing message (M1): the echo goes
        // through VfxCode.SanitiseForEcho, which caps it (and escapes non-printable characters —
        // covered by that helper's own tests, not re-asserted here).
        Assert.DoesNotContain(new string('x', 400), notFound.Message, StringComparison.Ordinal);
        Assert.True(
            notFound.Message.Length < 400,
            $"Expected a capped echo; message was {notFound.Message.Length} characters.");
    }

    [Fact]
    public void KnownSections_AreExactlyTheAdvertisedFiveNamedTokens()
    {
        // The resolver is the only place that decides what these tokens mean. The input schema has
        // no enum to derive from it (MCP cannot express "one of these five, or step: plus any dotted
        // type name"), so the tool's `section` parameter DESCRIPTION restates them in prose —
        // RealGetSchemaMcpTests asserts that prose covers every entry of this same list.
        Assert.Equal(
            ["full", "metadata", "environment", "variables", "steps"],
            SchemaSectionResolver.NamedSections);
    }
}
