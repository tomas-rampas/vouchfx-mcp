using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation.Semantics;

/// <summary>
/// US-S2-03, VFX-D-1205 — a step type needs a dependency KIND that <c>environment.dependencies</c>
/// never declares (spec §5.5's own example: <c>mq-expect.kafka</c> without a <c>kafka</c>
/// dependency).
/// </summary>
public class UndeclaredDependencyRuleTests
{
    [Fact]
    public void AKafkaStepWithNoKafkaDependency_IsReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            environment:
              dependencies:
                orders-db:
                  type: postgres
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
            """);

        var finding = Assert.Single(fixture.Run(new UndeclaredDependencyRule()));

        Assert.Equal("VFX-D-1205", finding.Code);
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("kafka", finding.Message, StringComparison.Ordinal);
        Assert.Equal("$.steps[0].type", finding.Path);
    }

    [Fact]
    public void TheSameStepWithItsDependencyDeclared_IsNotReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            environment:
              dependencies:
                broker:
                  type: kafka
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
            """);

        Assert.Empty(fixture.Run(new UndeclaredDependencyRule()));
    }

    [Fact]
    public void AServiceFormBrokerSuppressesTheFinding()
    {
        // The composed schema's own `target` description for mq-expect.kafka says a declared SERVICE
        // is a legitimate broker ("a customer-supplied broker under its own entrypoint/config"), so
        // demanding a dependency of the matching kind there would be a wrong finding on a valid
        // suite.
        using var fixture = SemanticRuleFixture.For("""
            environment:
              services:
                broker:
                  image: redpanda:latest
            steps:
              - id: consume
                type: mq-expect.kafka
                target: broker
                topic: orders
            """);

        Assert.Empty(fixture.Run(new UndeclaredDependencyRule()));
    }

    [Fact]
    public void AStepTypeNeedingNoDependency_IsNeverReported()
    {
        using var fixture = SemanticRuleFixture.For("""
            steps:
              - id: call
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        Assert.Empty(fixture.Run(new UndeclaredDependencyRule()));
    }

    [Fact]
    public void TheStepTypeToDependencyKindTableAgreesWithTheVendoredSchema()
    {
        // The anti-rot guard for a hand-written table: every step type it names must still exist in
        // the pinned engine's catalogue, and every dependency kind it demands must still be a value
        // the composed schema's `dependency.type` enum accepts. An ENGINE_PIN bump that renames
        // either fails here, by name, instead of silently making the rule unreachable (a step type
        // nothing matches) or permanently wrong (a kind no dependency can ever declare).
        var knownStepTypes = StepTypeCatalogue.All.Select(t => t.Type).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(UndeclaredDependencyRule.RequiredDependencyKinds);

        foreach (var (stepType, kind) in UndeclaredDependencyRule.RequiredDependencyKinds)
        {
            Assert.Contains(stepType, knownStepTypes);
            Assert.True(
                DependencyKinds.All.Contains(kind),
                $"Dependency kind '{kind}' is not in the vendored schema's dependency.type enum.");
        }
    }

    /// <summary>
    /// The six step types the rule's remarks name as needing no dependency of any kind.
    /// </summary>
    /// <remarks>
    /// A named constant rather than a count, so a failure below says WHICH type is unaccounted for.
    /// Every entry is a deliberate omission with a stated reason (see
    /// <see cref="UndeclaredDependencyRule.RequiredDependencyKinds"/>'s remarks): the two
    /// <c>http.*</c> types and <c>webhook-listen.http</c> target services, <c>script.csharp</c>
    /// touches no infrastructure, and <c>metrics-assert.prometheus</c> / <c>trace-expect.otlp</c>
    /// have backends the schema's <c>dependency.type</c> enum does not name.
    /// </remarks>
    private static readonly string[] AbsentByDesign =
    [
        "http.rest",
        "http.soap",
        "metrics-assert.prometheus",
        "script.csharp",
        "trace-expect.otlp",
        "webhook-listen.http",
    ];

    [Fact]
    public void EveryCatalogueStepTypeIsEitherInTheTableOrExplicitlyAbsentByDesign()
    {
        // The REVERSE direction of the gate above, and the one that actually catches an ENGINE_PIN
        // bump. Checking only that every table entry still exists proves nothing about a step type
        // ADDED upstream: a new `mq-publish.pulsar` would land in neither set and this rule would
        // silently never fire for it. Partitioning the whole catalogue is what makes "absent by
        // design" a decision someone made rather than a gap nobody noticed.
        var covered = UndeclaredDependencyRule.RequiredDependencyKinds.Keys
            .Concat(AbsentByDesign)
            .ToHashSet(StringComparer.Ordinal);

        var catalogue = StepTypeCatalogue.All.Select(t => t.Type).ToHashSet(StringComparer.Ordinal);

        // Set equality both ways, so neither an unclassified new type nor a stale entry survives.
        Assert.Equal(
            catalogue.OrderBy(t => t, StringComparer.Ordinal),
            covered.OrderBy(t => t, StringComparer.Ordinal));

        // The two halves are disjoint — a type cannot be both required-a-dependency and absent by
        // design, and the set union above would hide that collision.
        Assert.Empty(UndeclaredDependencyRule.RequiredDependencyKinds.Keys.Intersect(
            AbsentByDesign, StringComparer.Ordinal));
    }
}
