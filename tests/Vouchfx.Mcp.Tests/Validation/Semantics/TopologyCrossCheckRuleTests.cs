using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1210 — the topology cross-check: <b>implemented, catalogued, and shipped
/// DISABLED</b> until upstream ask U1 (<c>vouchfx topology --json</c>) lands
/// (<c>sprint-00-overview.md</c> §3).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "present but disabled" means concretely here</b>, and what each test below pins:
/// </para>
/// <list type="number">
/// <item><description>the code is catalogued and has a <c>docs/errors/</c> page, so the sprint's
/// bidirectional completeness gate recognises it — Gherkin scenario 5's second clause;</description></item>
/// <item><description>the rule is NOT in <see cref="SemanticAnalyser.Rules"/>, so
/// <c>validate_suite</c> at ANY level never emits it — Gherkin scenario 5's first clause;</description></item>
/// <item><description>there is no configuration flag, environment variable, or tool argument that
/// puts it back: the ONLY way to run it is to construct it with a topology, and nothing in
/// <c>src/</c> constructs one because no topology source exists pre-U1;</description></item>
/// <item><description>the BODY is real, not a stub returning <c>[]</c> — a test supplying a
/// hand-built topology gets the finding the rule will produce on the day U1 lands.</description></item>
/// </list>
/// </remarks>
public class TopologyCrossCheckRuleTests
{
    [Fact]
    public void TheCodeIsCatalogued_EvenThoughNothingEmitsItYet()
    {
        // Gherkin scenario 5: "the sprint's completeness gate still recognises VFX-D-1210 as a
        // catalogued, reserved code".
        Assert.Equal("VFX-D-1210", VfxCodeCatalogue.TopologyCrossCheck);

        var entry = VfxCodeCatalogue.Get(VfxCodeCatalogue.TopologyCrossCheck);
        Assert.Equal(VfxCodeKind.Diagnostic, entry.Kind);
        Assert.False(entry.Retryable);
    }

    [Fact]
    public void TheRuleIsNotRegistered_SoNoLevelCanEmitIt()
    {
        Assert.DoesNotContain(
            SemanticAnalyser.Rules,
            rule => string.Equals(rule.Code, VfxCodeCatalogue.TopologyCrossCheck, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ValidationLevel.Schema)]
    [InlineData(ValidationLevel.Semantic)]
    [InlineData(ValidationLevel.Full)]
    public void NoLevelEmitsIt_ForASuiteThatWouldOtherwiseTripIt(ValidationLevel level)
    {
        // The suite Gherkin scenario 5 describes: "a suite whose step targets a topic absent from
        // any extracted topology". Every level, because "no configuration in this sprint turns it
        // on" is a claim about all of them.
        var analysis = SuiteValidator.AnalyseYaml(
            """
            environment:
              dependencies:
                broker:
                  type: kafka
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: a-topic-no-producer-in-this-workspace-publishes
                match:
                  key: "1"
            """,
            level);

        Assert.DoesNotContain(analysis.SemanticDiagnostics, d => d.Code == VfxCodeCatalogue.TopologyCrossCheck);
    }

    [Fact]
    public void WithATopologySupplied_TheBodyReportsAnUnknownTopic()
    {
        // The rule is IMPLEMENTED, not stubbed: this is the finding it will produce once a real
        // topology source exists. Constructing one by hand is the only way to reach the body — which
        // is exactly the property the test above asserts nothing in production does.
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders.unknown
                match:
                  key: "1"
            """);

        var rule = new TopologyCrossCheckRule(new SuiteTopology(["orders.created", "orders.shipped"]));
        var finding = Assert.Single(fixture.Run(rule));

        Assert.Equal("VFX-D-1210", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("orders.unknown", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoTopology_TheBodyIsUnreachableAndReportsNothing()
    {
        // The gate itself, at the one place it is expressible in code: no topology, no findings —
        // never a fabricated verdict about contracts this server cannot see (sprint-00 §3's
        // gated-feature stance (b): never a fabricated value for the missing portion).
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders.unknown
                match:
                  key: "1"
            """);

        Assert.Empty(fixture.Run(new TopologyCrossCheckRule(topology: null)));
    }
}
