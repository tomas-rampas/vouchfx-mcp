using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1206 — <c>verifyMode: RETRY</c> with no <c>timeout</c> (the engine's default
/// applies), or a <c>timeout</c> above this server's advisory maximum.
/// </summary>
public class RetryTimeoutRuleTests
{
    [Fact]
    public void RetryWithoutATimeout_IsAWarning()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
                verifyMode: RETRY
            """);

        var finding = Assert.Single(fixture.Run(new RetryTimeoutRule()));

        Assert.Equal("VFX-D-1206", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("$.steps[0]", finding.Path);
    }

    [Fact]
    public void RetryWithATimeoutInsideTheBound_IsNotReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
                verifyMode: RETRY
                timeout: 30s
            """);

        Assert.Empty(fixture.Run(new RetryTimeoutRule()));
    }

    [Theory]
    [InlineData("timeout: 900")]
    [InlineData("timeout: 20m")]
    [InlineData("timeout: 1h")]
    public void ATimeoutAboveTheAdvisoryMaximum_IsAWarning(string timeoutLine)
    {
        using var fixture = SemanticRuleFixture.For($"""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
                verifyMode: RETRY
                {timeoutLine}
            """);

        var finding = Assert.Single(fixture.Run(new RetryTimeoutRule()));

        Assert.Equal("VFX-D-1206", finding.Code);
        Assert.Equal("$.steps[0].timeout", finding.Path);
    }

    [Fact]
    public void AnImmediateStepWithNoTimeout_IsNotReported()
    {
        // The rule is scoped to RETRY. An IMMEDIATE step without a timeout is ordinary, and
        // reporting it would bury the one case the spec table actually names.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        Assert.Empty(fixture.Run(new RetryTimeoutRule()));
    }

    [Fact]
    public void AnUnparseableTimeout_ProducesNothing()
    {
        // "Treat a shape you did not expect as nothing to say" (ISemanticRule's contract). A
        // malformed duration is the SCHEMA pass's finding to make, not a second, differently-worded
        // complaint from here — and a rule must never throw on it.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
                verifyMode: RETRY
                timeout: "whenever"
            """);

        Assert.Empty(fixture.Run(new RetryTimeoutRule()));
    }
}
