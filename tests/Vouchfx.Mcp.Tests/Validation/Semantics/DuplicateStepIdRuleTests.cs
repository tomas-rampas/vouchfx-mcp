using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1208 — two steps share an <c>id</c>. The schema constrains an id's SHAPE
/// (<c>^[A-Za-z_][A-Za-z0-9_-]*$</c>) but cannot express uniqueness across an array, which is why
/// this is a semantic rule rather than a schema keyword.
/// </summary>
public class DuplicateStepIdRuleTests
{
    [Fact]
    public void ARepeatedStepId_IsReportedOnceAtTheSecondOccurrence()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /ready
            """);

        var finding = Assert.Single(fixture.Run(new DuplicateStepIdRule()));

        Assert.Equal("VFX-D-1208", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("call", finding.Message, StringComparison.Ordinal);

        // Reported at the DUPLICATE, not at the first (legitimate) declaration: the second one is
        // what the author has to change.
        Assert.Equal("$.steps[1].id", finding.Path);
    }

    [Fact]
    public void ThreeOccurrences_ProduceTwoFindings()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /a
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /b
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /c
            """);

        var findings = fixture.Run(new DuplicateStepIdRule());

        Assert.Equal(2, findings.Count);
        Assert.Equal(["$.steps[1].id", "$.steps[2].id"], findings.Select(f => f.Path));
    }

    [Fact]
    public void DistinctStepIds_ProduceNothing()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: check-health
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: check-ready
                type: http.rest
                target: orders-api
                method: GET
                path: /ready
            """);

        Assert.Empty(fixture.Run(new DuplicateStepIdRule()));
    }
}
