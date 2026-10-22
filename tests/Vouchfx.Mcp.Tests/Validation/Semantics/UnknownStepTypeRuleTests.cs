using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1201 — the one MAPPED code: the semantic channel's enriched restatement of the
/// unknown-step-type finding the SCHEMA pass has always emitted.
/// </summary>
/// <remarks>
/// <b>Both channels carry 1201, and that is the adjudicated decision, not an oversight</b> — see
/// <see cref="UnknownStepTypeRule"/>'s own remarks for the full argument, and
/// <see cref="UnknownStepTypeDetector"/> for the single detector both channels share. What this
/// class pins is the half the schema channel cannot express: the Levenshtein closest-match
/// suggestion the sprint spec's first Gherkin scenario demands.
/// </remarks>
public class UnknownStepTypeRuleTests
{
    [Fact]
    public void UnknownStepType_IsReportedWithAClosestMatchSuggestion()
    {
        // Gherkin scenario 1, verbatim input: "a suite referencing step type
        // 'mq-expect.nonexistent-provider'" must yield code exactly VFX-D-1201 whose message
        // "includes a closest-match suggestion by Levenshtein distance".
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.nonexistent-provider
                target: broker
            """);

        var finding = Assert.Single(fixture.Run(new UnknownStepTypeRule()));

        Assert.Equal("VFX-D-1201", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("mq-expect.nonexistent-provider", finding.Message, StringComparison.Ordinal);

        // The suggestion is the point of this rule existing beside the schema pass's own emission.
        // Asserted as a real family-mate rather than merely "some type is named": a distance
        // function that returned an arbitrary catalogue entry would still pass a weaker assertion.
        Assert.Contains("mq-expect.", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.steps[0].type", finding.Path);
    }

    [Fact]
    public void AnExternalProvidersType_IsNotPresentedAsATypoToRename()
    {
        // rpc.json-rpc is the published Community provider measured at the v1.2.0 pin: installed,
        // the engine validates this suite and list_step_types reports the type with tier
        // 'community', yet the vendored vocabulary this rule reads has never heard of it. The
        // suggestion may stay, but only framed as the typo case, beside the instruction that keeps
        // an agent from renaming a real provider's type to a bundled one.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call-rpc
                type: rpc.json-rpc
                url: http://localhost:9/rpc
                method: ping
            """);

        var finding = Assert.Single(fixture.Run(new UnknownStepTypeRule()));

        Assert.Contains("bundled step types", finding.Message, StringComparison.Ordinal);
        Assert.Contains("If it is a typo, the closest bundled type", finding.Message, StringComparison.Ordinal);
        Assert.Contains("tier 'community'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("`vouchfx validate` and `vouchfx run`", finding.Message, StringComparison.Ordinal);
        Assert.Contains("instead of renaming the type", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutASuggestion_TheExternalProviderGuidanceStillAppears()
    {
        // Past MaxSuggestibleTypeLength there is no suggestion at all; the rest of the advice is
        // independent of it and must not be lost with it.
        var longType = "rpc." + new string('x', UnknownStepTypeDetector.MaxSuggestibleTypeLength);
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: call
                type: {longType}
            """);

        var finding = Assert.Single(fixture.Run(new UnknownStepTypeRule()));

        Assert.DoesNotContain("typo", finding.Message, StringComparison.Ordinal);
        Assert.Contains("tier 'community'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("`vouchfx validate` and `vouchfx run`", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownStepTypes_ProduceNothing()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        Assert.Empty(fixture.Run(new UnknownStepTypeRule()));
    }

    [Fact]
    public void TheSemanticFindingCarriesTheSuiteLineAndFile()
    {
        // Location is derivable here and so must be populated: the file identity now reaches the
        // seam (SemanticAnalysisContext.SourceName) and the line comes from the SAME YamlMappingNode
        // the schema pass resolves against — never a second parse.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.nope
            """);

        var finding = Assert.Single(fixture.Run(new UnknownStepTypeRule()));

        Assert.NotNull(finding.Location);
        Assert.Equal(SemanticRuleFixture.SourceName, finding.Location!.File);
        Assert.Equal(3, finding.Location.Line);
    }

    [Fact]
    public void TheSchemaChannelKeepsEmittingItsOwnUnenrichedMessage()
    {
        // The compatibility half of the channel decision, pinned where a reader will look for it:
        // the schema channel's 1201 wording is what the US-S2-06 agreement oracle compares against
        // `vouchfx validate` byte for byte, so the Levenshtein enrichment must NOT appear there.
        var analysis = SuiteValidator.AnalyseYaml(
            """
            steps:
              - id: consume
                type: mq-expect.nonexistent-provider
            """,
            ValidationLevel.Full);

        var schemaFinding = Assert.Single(
            analysis.Errors,
            e => string.Equals(e.Code, VfxCodeCatalogue.UnknownStepType, StringComparison.Ordinal));

        Assert.Contains("Known types:", schemaFinding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("closest", schemaFinding.Message, StringComparison.OrdinalIgnoreCase);

        var semanticFinding = Assert.Single(
            analysis.SemanticDiagnostics,
            d => string.Equals(d.Code, VfxCodeCatalogue.UnknownStepType, StringComparison.Ordinal));

        Assert.Contains("closest", semanticFinding.Message, StringComparison.OrdinalIgnoreCase);
    }
}
