using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1203 — a <c>{placeholder}</c> used before any <c>capture</c> or root
/// <c>variables</c> entry provides it. The one rule that genuinely needs DOCUMENT ORDER.
/// </summary>
public class PlaceholderDefinitionOrderRuleTests
{
    [Fact]
    public void APlaceholderUsedBeforeItsCapture_IsReported()
    {
        // Gherkin scenario 2's first half.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: fetch-order
                type: http.rest
                target: orders-api
                method: GET
                path: /orders/{orderId}
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders
                capture:
                  orderId: "$.id"
            """);

        var finding = Assert.Single(fixture.Run(new PlaceholderDefinitionOrderRule()));

        Assert.Equal("VFX-D-1203", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("orderId", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.steps[0]", finding.Path);
    }

    [Fact]
    public void ThatSamePlaceholderUsedAfterItsCapture_IsNotReported()
    {
        // Gherkin scenario 2's second half — "no entry with that code appears if the same
        // placeholder is used after its capture". The identical two steps, swapped.
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

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void ARootVariableCountsAsDefinedBeforeEveryStep()
    {
        // SuiteFacts.Variables exists precisely so this case is not a false positive: root
        // `variables` are loaded into the shared context before the first step runs, so a token
        // naming one resolves in step 0.
        using var fixture = SemanticRuleFixture.For("""
            variables:
              baseUrl: "https://api.example.test"
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: "{baseUrl}/health"
            """);

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void AReservedPrefixTokenIsNeverReported()
    {
        // `{svc::…}` and `{conn::…}` resolve from the environment, not from captures — reporting one
        // would be a wrong finding on a valid suite, which is the failure mode this seam's design
        // exists to avoid. (See SuiteSummaryBuilder.IsPlaceholderNameChar's remarks for why `:` is in
        // the placeholder charset at all.)
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: "{svc::orders-api.baseUrl}/health"
            """);

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void AStepsOwnCaptureDoesNotDefineATokenThatSameStepUses()
    {
        // Order-awareness is per STEP, not per document: a capture is produced by running the step,
        // so a token the same step interpolates cannot already hold that capture's value.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: create-order
                type: http.rest
                target: orders-api
                method: POST
                path: /orders/{orderId}
                capture:
                  orderId: "$.id"
            """);

        var finding = Assert.Single(fixture.Run(new PlaceholderDefinitionOrderRule()));
        Assert.Equal("VFX-D-1203", finding.Code);
    }

    [Fact]
    public void AListenerNameStagedByALaterWebhookStep_IsDefinedForEveryEarlierStep()
    {
        // The canonical webhook suite, which a naive document-order reading reported. The engine
        // stands the listener up BEFORE the run and stages its URL at the plain `callbacks` Vars key
        // precisely so an earlier step can hand it to the SUT — the language reference says so in as
        // many words (vendored/language-reference.md:514: "so an earlier step can interpolate
        // {<listener>}"). Reporting this is a wrong finding on the documented, intended shape.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: register-callback
                type: http.rest
                target: orders-api
                method: POST
                path: /subscriptions
                body: '{"url":"{callbacks}"}'
              - id: await-callback
                type: webhook-listen.http
                listener: callbacks
                verifyMode: RETRY
                match:
                  path: /hooks/orders
            """);

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void AReceiverNameStagedByALaterTraceStep_IsDefinedForEveryEarlierStep()
    {
        // The same staging, for trace-expect.* (vendored/language-reference.md:502).
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: configure-sut
                type: http.rest
                target: orders-api
                method: POST
                path: /config
                body: '{"OTEL_EXPORTER_OTLP_ENDPOINT":"{collector}"}'
              - id: await-span
                type: trace-expect.otlp
                receiver: collector
                verifyMode: RETRY
                match:
                  traceId: "abc"
            """);

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void TheStagedVarsKeyTableAgreesWithTheVendoredSchema()
    {
        // The anti-rot guard for a hand-written table, exactly as UndeclaredDependencyRuleTests
        // gates its own: every prefix must still match at least one real step type, and the field
        // it names must still be one that type REQUIRES. An ENGINE_PIN bump renaming `listener` or
        // dropping the family fails here, by name, instead of silently reintroducing the false
        // positive this table exists to prevent.
        Assert.NotEmpty(PlaceholderDefinitionOrderRule.StagedVarsKeyFields);

        foreach (var (prefix, field) in PlaceholderDefinitionOrderRule.StagedVarsKeyFields)
        {
            var matching = StepTypeCatalogue.All
                .Where(t => t.Type.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

            Assert.True(matching.Length > 0, $"No step type in the catalogue starts with '{prefix}'.");

            Assert.All(matching, type => Assert.True(
                type.RequiredFields.Contains(field, StringComparer.Ordinal),
                $"Step type '{type.Type}' does not require a '{field}' field."));
        }
    }

    [Fact]
    public void ACSharpInterpolationHoleInAScriptBody_IsNotASuitePlaceholder()
    {
        // C# spells its own string interpolation with the same braces, so `$"...{id}..."` inside a
        // script body looks exactly like a suite placeholder to a brace scan. It is not one: the
        // engine never interpolates a script body against the Vars context — the script reads
        // Vars["id"] itself — so reporting "nothing provides {id}" is a wrong finding on a valid
        // suite. The `file` property is excluded for the same reason plus one more: it is a path.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: seed
                type: script.csharp
                code: |
                  var id = 42;
                  Console.WriteLine($"order {id} created for {customer}");
            """);

        Assert.Empty(fixture.Run(new PlaceholderDefinitionOrderRule()));
    }

    [Fact]
    public void TheSummaryDigestAgreesWithTheRuleAboutScriptSource()
    {
        // The digest and this rule share one scanner precisely so they cannot disagree about what a
        // placeholder IS. A `summary.placeholders` listing `{id}` mined out of C# would be the same
        // falsehood as the finding, merely rendered as data — so the exclusion is asserted on both
        // sides of the seam.
        var analysis = SuiteValidator.AnalyseYaml(
            """
            steps:
              - id: seed
                type: script.csharp
                code: |
                  Console.WriteLine($"order {id} created");
            """,
            ValidationLevel.Full);

        Assert.NotNull(analysis.Summary);
        Assert.Empty(analysis.Summary!.Placeholders);
        Assert.DoesNotContain(analysis.SemanticDiagnostics, d => d.Code == "VFX-D-1203");
    }
}
