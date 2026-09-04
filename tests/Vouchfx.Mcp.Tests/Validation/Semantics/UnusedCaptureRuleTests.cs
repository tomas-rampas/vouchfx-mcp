using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>US-S2-03, VFX-D-1204 — a <c>capture</c> declares a name nothing ever interpolates.</summary>
public class UnusedCaptureRuleTests
{
    [Fact]
    public void ACaptureNoPlaceholderEverUses_IsAWarning()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
            """);

        var finding = Assert.Single(fixture.Run(new UnusedCaptureRule()));

        Assert.Equal("VFX-D-1204", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("orderId", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.steps[0].capture.orderId", finding.Path);
    }

    [Fact]
    public void ACaptureSomeLaterStepInterpolates_IsNotReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
              - id: fetch-order
                type: http.rest
                target: orders-api
                method: GET
                path: /orders/{orderId}
            """);

        Assert.Empty(fixture.Run(new UnusedCaptureRule()));
    }

    [Fact]
    public void ASecretNamedCapture_IsReportedWithoutEchoingTheReference()
    {
        // THE case the seam's remarks single out by name: "capture '{name}' is never used",
        // interpolating a fact-set entry, is the natural first draft and it fails the whole call.
        // The finding must still be reported — the capture really is unused — but by a bounded,
        // reference-free identifier, and located by path.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  "${secret:vault/prod-db-password}": "$.id"
            """);

        var finding = Assert.Single(fixture.Run(new UnusedCaptureRule()));

        Assert.Equal("VFX-D-1204", finding.Code);
        Assert.DoesNotContain("${", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("vault", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prod-db-password", finding.Message, StringComparison.Ordinal);

        // The path stops at the capture MAP rather than naming the entry: a pointer segment carrying
        // the name would smuggle the same reference out through Diagnostic.Path, which the choke
        // point checks for exactly that reason.
        Assert.Equal("$.steps[0].capture", finding.Path);
    }

    [Fact]
    public void ACaptureAScriptReadsFromVars_IsNotReported()
    {
        // A script.csharp step consumes a capture WITHOUT a placeholder — it reads the shared
        // context directly, as Vars["orderId"] — so the placeholder set truthfully says nothing
        // interpolates it and the naive reading calls a working suite's capture unused.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
              - id: check
                type: script.csharp
                code: |
                  var id = Vars["orderId"];
                  Assert.NotNull(id);
            """);

        Assert.Empty(fixture.Run(new UnusedCaptureRule()));
    }

    [Fact]
    public void AScriptWhoseSourceIsAFile_SuppressesTheRuleEntirely()
    {
        // The bluntest of the two mitigations, and the one whose rationale is worth stating: this
        // server never reads a suite's neighbouring files, so the evidence that would answer "is
        // this capture used?" is unreachable BY CONSTRUCTION. Reporting anyway is guessing.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
                  somethingElse: "$.total"
              - id: check
                type: script.csharp
                file: ./scripts/check-order.csx
            """);

        Assert.Empty(fixture.Run(new UnusedCaptureRule()));
    }
}
