using Vouchfx.Mcp.Validation;
using Vouchfx.Mcp.Validation.Semantics;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="RequiredResourceCatalogue"/> — US-S2-05's derivation of spec §5.2's
/// <c>requiredResources</c> from data this repo already holds, with no engine change.
/// </summary>
/// <remarks>
/// The three-way outcome is the whole contract, and each arm has a different meaning that must not
/// be collapsed into another: a NON-EMPTY list is a derived requirement, an EMPTY list is the
/// derived fact "this type needs no dependency kind" (an answer, not a blank), and
/// <see langword="null"/> is "this server cannot say" — which the tools omit from the wire rather
/// than emitting as <c>[]</c>, because <c>[]</c> would be a fabricated claim of independence.
/// </remarks>
public class RequiredResourceCatalogueTests
{
    [Theory]
    [InlineData("mq-expect.kafka", "kafka")]
    [InlineData("mq-publish.rabbitmq", "rabbitmq")]
    [InlineData("db-assert.postgres", "postgres")]
    [InlineData("cache-assert.redis", "redis")]
    public void KindMatchingTheProviderName_IsDerived(string stepType, string expectedKind)
    {
        Assert.Equal([expectedKind], RequiredResourceCatalogue.For(stepType));
    }

    [Theory]
    // The two types where the DSL's provider vocabulary and its dependency vocabulary diverge —
    // the reason this reuses UndeclaredDependencyRule's explicit table instead of matching the
    // provider segment against DependencyKinds.All (see that table's own remarks).
    [InlineData("mail-expect.smtp", "mailpit")]
    [InlineData("storage-assert.s3", "minio")]
    public void KindDivergingFromTheProviderName_IsDerivedFromTheTableNotTheName(string stepType, string expectedKind)
    {
        Assert.Equal([expectedKind], RequiredResourceCatalogue.For(stepType));
    }

    [Theory]
    // Absent by design — see UndeclaredDependencyRule.RequiredDependencyKinds' remarks for why each
    // of these six needs no dependency kind.
    [InlineData("http.rest")]
    [InlineData("http.soap")]
    [InlineData("webhook-listen.http")]
    [InlineData("script.csharp")]
    [InlineData("metrics-assert.prometheus")]
    [InlineData("trace-expect.otlp")]
    public void AbsentByDesignType_IsAnEmptyList_NotNull(string stepType)
    {
        var resources = RequiredResourceCatalogue.For(stepType);

        Assert.NotNull(resources);
        Assert.Empty(resources);
    }

    [Theory]
    [InlineData("mq-expect.pulsar")]
    [InlineData("")]
    [InlineData("not-a-dotted-type")]
    public void TypeOutsideTheVendoredCatalogue_IsNull_SoTheToolsOmitTheField(string stepType)
    {
        // A step type this repo's pinned schema does not know is one this repo cannot answer for —
        // most plausibly a type added by an ENGINE_PIN bump ahead of a vendored resync. Answering
        // "[]" would tell a host the new type needs no infrastructure, which is a guess.
        Assert.Null(RequiredResourceCatalogue.For(stepType));
    }

    [Fact]
    public void EveryVendoredCatalogueType_HasAnAnswer()
    {
        foreach (var stepType in StepTypeCatalogue.All)
        {
            Assert.NotNull(RequiredResourceCatalogue.For(stepType.Type));
        }
    }

    [Fact]
    public void EveryDerivedKind_IsARealDependencyKindTheSchemaDeclares()
    {
        // Anti-fabrication: nothing this returns may be a kind the engine would reject in an
        // environment.dependencies block.
        foreach (var stepType in StepTypeCatalogue.All)
        {
            foreach (var kind in RequiredResourceCatalogue.For(stepType.Type)!)
            {
                // Assert.True over the set's own Contains: a FrozenSet satisfies both
                // Assert.Contains&lt;T&gt;(T, ISet&lt;T&gt;) and its IReadOnlySet overload, so the
                // direct call is ambiguous.
                Assert.True(
                    DependencyKinds.All.Contains(kind),
                    $"'{stepType.Type}' derives dependency kind '{kind}', which the composed "
                    + "schema's own dependency enum does not accept.");
            }
        }
    }

    [Fact]
    public void EveryAbsentByDesignType_HasNoProviderMatchInTheSchemaDependencyEnum_ElseReDecide()
    {
        // M1 (second-reviewer follow-up): an EMPTY requiredResources is derived purely from
        // table-ABSENCE — UndeclaredDependencyRule has no row for the type. For four of the six
        // absent-by-design types that is a settled fact (http.rest/http.soap/webhook-listen.http
        // target services, script.csharp touches no infrastructure). For the other two —
        // metrics-assert.prometheus and trace-expect.otlp — it is true ONLY because the schema's
        // dependency.type enum does not (yet) name prometheus/otlp: their backends are real, just
        // not modelled as dependency kinds today. An ENGINE_PIN bump that adds such a kind to the
        // enum would leave every other test green while this catalogue kept asserting the type
        // "needs none" — a fabricated claim of independence. This guard fires the instant the schema
        // learns a kind whose name matches an absent-by-design type's provider segment, forcing a
        // conscious re-decision rather than a silent lie.
        foreach (var stepType in StepTypeCatalogue.All)
        {
            if (RequiredResourceCatalogue.For(stepType.Type) is not { Count: 0 })
            {
                continue;
            }

            Assert.False(
                DependencyKinds.All.Contains(stepType.Provider),
                $"'{stepType.Type}' is catalogued as needing no dependency kind, but the composed "
                + $"schema's dependency.type enum now accepts '{stepType.Provider}' — re-decide: the "
                + "catalogue asserts it needs none.");
        }
    }

    [Fact]
    public void TheDerivationIsTheRulesTable_NotASecondCopyOfIt()
    {
        // The single-source check: every entry of the rule's table must show up verbatim here, so a
        // future edit to the table cannot leave the catalogue tools reporting the old answer.
        foreach (var (stepType, kind) in UndeclaredDependencyRule.RequiredDependencyKinds)
        {
            Assert.Equal([kind], RequiredResourceCatalogue.For(stepType));
        }

        var withRequirements = StepTypeCatalogue.All
            .Count(t => RequiredResourceCatalogue.For(t.Type) is { Count: > 0 });

        Assert.Equal(UndeclaredDependencyRule.RequiredDependencyKinds.Count, withRequirements);
    }
}
