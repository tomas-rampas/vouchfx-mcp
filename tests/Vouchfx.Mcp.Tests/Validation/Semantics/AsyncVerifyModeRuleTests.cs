using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1209 — an asynchronous step type left on the default <c>IMMEDIATE</c> verify
/// mode. The only rule in the set that ships a MACHINE-APPLICABLE fix.
/// </summary>
public class AsyncVerifyModeRuleTests
{
    [Fact]
    public void AWebhookListenerWithNoVerifyMode_IsAWarningWithAnApplicableFix()
    {
        // Gherkin scenario 3, verbatim: a "webhook-listen.http" step "with no verifyMode set" must
        // come back as severity "warning" whose "fix.replacement sets verifyMode to RETRY".
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: await-callback
                type: webhook-listen.http
                listener: callbacks
                match:
                  path: /hooks/orders
            """);

        var finding = Assert.Single(fixture.Run(new AsyncVerifyModeRule()));

        Assert.Equal("VFX-D-1209", finding.Code);
        Assert.Equal("warning", finding.Severity);

        Assert.NotNull(finding.Fix);
        Assert.Equal("verifyMode: RETRY", finding.Fix!.Replacement);
        Assert.False(string.IsNullOrWhiteSpace(finding.Fix.Description));
        Assert.Equal("$.steps[0]", finding.Path);
    }

    [Fact]
    public void AnMqExpectStepAlreadyOnRetry_IsNotReported()
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

        Assert.Empty(fixture.Run(new AsyncVerifyModeRule()));
    }

    [Fact]
    public void AnMqExpectStepExplicitlyOnImmediate_IsStillReported()
    {
        // "Without verifyMode: RETRY" covers both the omitted case and the explicitly-IMMEDIATE one;
        // an async assertion polled zero times is the flake either way.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
                verifyMode: IMMEDIATE
            """);

        Assert.Single(fixture.Run(new AsyncVerifyModeRule()));
    }

    [Fact]
    public void ADbAssertBeforeAnyPublish_IsNotReported()
    {
        // The spec table's own qualifier — "db-assert.* AFTER a publish" — is what makes this rule
        // usable: an ordinary read-your-writes db-assert following an HTTP call is synchronous, and
        // flagging it would fire on most suites in the corpus.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
            """);

        Assert.Empty(fixture.Run(new AsyncVerifyModeRule()));
    }

    [Fact]
    public void ADbAssertAfterAPublish_IsReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: '{"id":1}'
              - id: assert-row
                type: db-assert.postgres
                target: orders-db
                query: "SELECT 1"
                expect:
                  rowCount: 1
            """);

        var finding = Assert.Single(fixture.Run(new AsyncVerifyModeRule()));
        Assert.Equal("$.steps[1]", finding.Path);
    }

    [Fact]
    public void EveryAlwaysAsyncPrefixMatchesAtLeastOneRealStepType()
    {
        // The anti-rot gate for a hand-written prefix list: a prefix that matches nothing is a rule
        // arm that can never fire, and an ENGINE_PIN bump renaming a family would produce exactly
        // that — silently, since a rule that stops firing looks the same as a suite with no problem.
        Assert.NotEmpty(AsyncVerifyModeRule.AlwaysAsyncPrefixes);

        foreach (var prefix in AsyncVerifyModeRule.AlwaysAsyncPrefixes)
        {
            Assert.True(
                StepTypeCatalogue.All.Any(t => t.Type.StartsWith(prefix, StringComparison.Ordinal)),
                $"No step type in the catalogue starts with '{prefix}'.");
        }
    }
}
