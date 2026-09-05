using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1211 — <c>metadata.owner</c>/<c>tags</c> missing. The only <c>info</c>-severity
/// code in the set: a suite without an owner still runs, it is just harder to route when it fails.
/// </summary>
public class MetadataCompletenessRuleTests
{
    [Fact]
    public void ASuiteWithNoMetadataBlock_IsReportedAsInfo()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        var finding = Assert.Single(fixture.Run(new MetadataCompletenessRule()));

        Assert.Equal("VFX-D-1211", finding.Code);
        Assert.Equal("info", finding.Severity);
        Assert.Contains("owner", finding.Message, StringComparison.Ordinal);
        Assert.Contains("tags", finding.Message, StringComparison.Ordinal);

        // No metadata block exists, so there is nothing to point at: a path naming an absent node
        // would resolve to no line and mislead a host that trusts it.
        Assert.Null(finding.Path);
    }

    [Fact]
    public void OwnerPresentButTagsMissing_NamesOnlyTheMissingOne()
    {
        using var fixture = SemanticRuleFixture.For("""
            metadata:
              name: "Orders smoke"
              owner: "platform-team"
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        var finding = Assert.Single(fixture.Run(new MetadataCompletenessRule()));

        Assert.Contains("tags", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("owner", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.metadata", finding.Path);
    }

    [Fact]
    public void AnEmptyTagsList_CountsAsMissing()
    {
        // "tags: []" declares the field without declaring a single selector, so the runner's
        // selection language still cannot address this suite — the condition the code is about.
        using var fixture = SemanticRuleFixture.For("""
            metadata:
              owner: "platform-team"
              tags: []
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        Assert.Single(fixture.Run(new MetadataCompletenessRule()));
    }

    [Fact]
    public void BothPresent_ProducesNothing()
    {
        using var fixture = SemanticRuleFixture.For("""
            metadata:
              owner: "platform-team"
              tags:
                - smoke
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        Assert.Empty(fixture.Run(new MetadataCompletenessRule()));
    }
}
