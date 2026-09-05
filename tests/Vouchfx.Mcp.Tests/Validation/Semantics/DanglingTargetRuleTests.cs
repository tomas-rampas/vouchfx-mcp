using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>US-S2-03, VFX-D-1202 — a step's <c>target</c> names nothing <c>environment</c> declares.</summary>
public class DanglingTargetRuleTests
{
    [Fact]
    public void ATargetNamingNoDeclaredServiceOrDependency_IsReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            environment:
              services:
                orders-api:
                  image: orders:1.0
            steps:
              - id: call
                type: http.rest
                target: ordres-api
                method: GET
                path: /health
            """);

        var finding = Assert.Single(fixture.Run(new DanglingTargetRule()));

        Assert.Equal("VFX-D-1202", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("ordres-api", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.steps[0].target", finding.Path);
    }

    [Fact]
    public void ATargetNamingADeclaredDependency_IsNotReported()
    {
        // Both halves of the union are authoritative: the composed schema's own `target` description
        // for a broker step says a DEPENDENCY name or a SERVICE name is legitimate, so testing only
        // against services would report a valid suite.
        using var fixture = SemanticRuleFixture.For("""
            environment:
              dependencies:
                orders-db:
                  type: postgres
            steps:
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
            """);

        Assert.Empty(fixture.Run(new DanglingTargetRule()));
    }

    [Fact]
    public void ATargetThatIsItselfAReference_IsNotEchoed()
    {
        // The seam's own hazard: SuiteFacts retains `${secret:…}`-shaped identifiers so membership
        // stays answerable, which makes "interpolate the target into the message" the natural draft
        // that fails the whole call at the choke point. Running through SemanticRuleFixture.Run
        // (i.e. through Analyse) is what makes this a real assertion rather than a hopeful one.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: "${secret:vault/prod-db-host}"
                method: GET
                path: /health
            """);

        var finding = Assert.Single(fixture.Run(new DanglingTargetRule()));

        Assert.DoesNotContain("${", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("$.steps[0].target", finding.Path);
    }

    [Fact]
    public void AStepWithNoTarget_ProducesNothing()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: run-script
                type: script.csharp
                code: "return 1;"
            """);

        Assert.Empty(fixture.Run(new DanglingTargetRule()));
    }
}
