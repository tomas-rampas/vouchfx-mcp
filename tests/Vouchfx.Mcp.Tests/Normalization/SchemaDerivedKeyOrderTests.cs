using System.Text.Json;
using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Tests.Normalization;

/// <summary>
/// Locks the two schema-derived inputs to <see cref="CanonicalKeyOrder"/> against the vendored
/// composed schema at the current <c>ENGINE_PIN</c>: which key names are FREE-FORM containers whose
/// contents are the author's to order, and the strong-majority threshold that covers the names this
/// derivation deliberately does not claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both directions, so the set cannot rot.</b> The expected list below is a lock, not the
/// derivation — it is written out so that advancing the engine pin has to be a deliberate edit here
/// rather than a silent change in which of an author's mappings get reordered. The reverse direction
/// enumerates every property name the schema declares and asserts that nothing outside the list is
/// treated as a container, which is what makes the list exhaustive rather than merely correct about
/// the names someone thought to check.
/// </para>
/// <para>
/// <b>The negative cases are the interesting ones.</b> A human writing this list from memory would
/// have included <c>query</c> and <c>params</c>; the schema declares <c>query</c> as a STRING and
/// declares no <c>params</c> at all, so no mapping can legitimately appear under either and neither
/// is derived. <c>metadata</c> and <c>expect</c> are excluded for the opposite reason — each is
/// free-form in one place and shaped in another, so the derivation refuses to claim the name and the
/// strong-majority test carries them instead.
/// </para>
/// </remarks>
public class SchemaDerivedKeyOrderTests
{
    /// <summary>
    /// Every key name the pinned composed schema declares as a free-form object — no
    /// <c>properties</c>, no <c>$ref</c>, and a <c>type</c> permitting an object — in EVERY place it
    /// declares that name.
    /// </summary>
    private static readonly string[] ExpectedAuthorDataContainers =
    [
        "attributes", "body", "capture", "dependencies", "document", "env", "expectProperties",
        "headers", "item", "json", "labels", "parameters", "properties", "record", "row",
        "schemaVersion", "seed", "services", "variables",
    ];

    [Fact]
    public void EveryDerivedAuthorDataContainer_IsRecognisedAsOne()
    {
        foreach (var name in ExpectedAuthorDataContainers)
        {
            Assert.True(
                CanonicalKeyOrder.IsAuthorDataContainer(name),
                $"'{name}' is declared free-form everywhere the composed schema mentions it, so a "
                + "mapping under it is the author's data and must not be reordered.");
        }
    }

    [Fact]
    public void NoOtherSchemaDeclaredKeyName_IsTreatedAsAnAuthorDataContainer()
    {
        var unexpected = SchemaDeclaredPropertyNames()
            .Where(CanonicalKeyOrder.IsAuthorDataContainer)
            .Except(ExpectedAuthorDataContainers, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            $"The vendored schema now derives these additional free-form containers: "
            + $"{string.Join(", ", unexpected)}. That is a real behaviour change — mappings under "
            + "them stop being reordered — so add them to this lock deliberately.");
    }

    [Theory]
    // Shaped in at least one place, so the derivation does not claim the name; the strong-majority
    // test in CanonicalKeyOrder is what protects a free-form mapping that happens to use it.
    [InlineData("metadata")]
    [InlineData("expect")]
    // Declared, but never as something a mapping can live under.
    [InlineData("query")]
    [InlineData("name")]
    [InlineData("steps")]
    [InlineData("environment")]
    // Not declared at all — a human's list would have guessed this one.
    [InlineData("params")]
    public void NamesTheDerivationDeliberatelyDoesNotClaim_AreNotContainers(string name) =>
        Assert.False(CanonicalKeyOrder.IsAuthorDataContainer(name));

    /// <summary>
    /// Every name appearing in any <c>properties</c> object of the composed schema. A different
    /// question from "is it free-form", deliberately — this test must not re-implement the predicate
    /// it is checking, only supply the universe to check it over.
    /// </summary>
    private static HashSet<string> SchemaDeclaredPropertyNames()
    {
        using var schema = VendoredComposedSchema.Parse();

        var names = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<JsonElement>();
        queue.Enqueue(schema.RootElement);

        while (queue.Count > 0)
        {
            var element = queue.Dequeue();
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("properties", out var properties)
                        && properties.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in properties.EnumerateObject())
                        {
                            names.Add(property.Name);
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        queue.Enqueue(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        queue.Enqueue(item);
                    }

                    break;

                default:
                    break;
            }
        }

        // Anti-vacuity: an empty universe would make the reverse-direction check pass over nothing.
        Assert.NotEmpty(names);

        return names;
    }
}
