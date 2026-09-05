using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Schema;

/// <summary>
/// US-S2-01: <c>get_schema</c>'s <c>format: "summary"</c> renderer — a markdown digest built
/// ENTIRELY from the schema's own <c>description</c> annotations, bounded at
/// <see cref="SchemaSummaryRenderer.MaxSummaryBytes"/>.
/// </summary>
/// <remarks>
/// The two properties this class exists to pin are adversarial opposites, and both are acceptance
/// criteria: the summary must never INVENT prose for an unannotated field (no placeholder text, no
/// "no description available" filler — an unannotated field is simply absent), and it must never
/// EXCEED 8&#160;KB however large the addressed subtree is. The first is proved against a synthetic
/// schema (the real vendored document annotates almost everything, so it cannot exhibit the case);
/// the second against every real section, which is where the size risk actually lives.
/// </remarks>
public class SchemaSummaryRendererTests
{
    /// <summary>
    /// The largest digest ANY addressable section renders to at the currently pinned engine, and the
    /// section that owns it — measured by
    /// <see cref="Render_EveryAddressableSection_StaysWithinTheBoundAndTheLargestIsThePinnedOne"/>,
    /// which is also the only thing that may change them.
    /// </summary>
    /// <remarks>
    /// These two constants exist so that a pin bump moves the headroom CONSCIOUSLY. The bound itself
    /// (<see cref="SchemaSummaryRenderer.MaxSummaryBytes"/>) is a renderer postcondition and cannot
    /// be breached whatever the schema says — but "no real section comes anywhere near it" is a claim
    /// about the pinned DOCUMENT, and <c>docs/tools-and-resources.md</c> publishes this exact number
    /// to hosts. Pinning it here means an engine bump that grows a step type's prose fails this test
    /// with the new maximum in the message, rather than silently invalidating the published figure.
    /// </remarks>
    private const int LargestObservedDigestBytes = 2141;

    /// <inheritdoc cref="LargestObservedDigestBytes"/>
    private const string LargestDigestSection = "step:mq-publish.kafka";

    /// <summary>
    /// The <c>full</c> section's digest size at the currently pinned engine — the OTHER figure
    /// <c>docs/tools-and-resources.md</c> publishes, pinned here for the same reason as
    /// <see cref="LargestObservedDigestBytes"/>.
    /// </summary>
    private const int FullSectionDigestBytes = 773;

    /// <summary>
    /// Every section a host can actually address — the five named ones plus one
    /// <c>step:&lt;family&gt;.&lt;provider&gt;</c> per step type in the catalogue — renders within
    /// the 8&#160;KB bound, and the largest of them is exactly the pinned
    /// <see cref="LargestObservedDigestBytes"/>.
    /// </summary>
    /// <remarks>
    /// Exhaustive rather than a hand-picked sample: a sampled list cannot say anything about the
    /// section it does not name, and the size risk lives precisely in whichever step type happens to
    /// carry the most annotated fields at the current pin — which is not knowable in advance, and
    /// moves when the pin does. Deriving the step sections from
    /// <see cref="StepTypeCatalogue.All"/> (itself derived from the same embedded schema) means a step
    /// type added by a pin bump is measured the day it arrives.
    /// </remarks>
    [Fact]
    public void Render_EveryAddressableSection_StaysWithinTheBoundAndTheLargestIsThePinnedOne()
    {
        using var document = VendoredComposedSchema.Parse();

        var sections = SchemaSectionResolver.NamedSections
            .Concat(StepTypeCatalogue.All.Select(step => SchemaSectionResolver.StepSectionPrefix + step.Type))
            .ToArray();

        // Anti-vacuity: an empty (or near-empty) catalogue would let this whole gate pass by measuring
        // nothing — the same guard ErrorCatalogueFilesystemParityTests applies to its derived sets.
        Assert.NotEmpty(StepTypeCatalogue.All);
        Assert.Equal(SchemaSectionResolver.NamedSections.Count + StepTypeCatalogue.All.Count, sections.Length);

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var resolved = Assert.IsType<SchemaSectionResolution.Ok>(
                SchemaSectionResolver.Resolve(document.RootElement, section));

            var summary = SchemaSummaryRenderer.Render(section, "v1", resolved.Subtree, document.RootElement);
            Assert.NotEmpty(summary);

            // Bytes, not characters: the bound is a wire-size promise and the schema's prose contains
            // non-ASCII (en dashes, section signs) that cost more than one byte each in UTF-8.
            var byteCount = Encoding.UTF8.GetByteCount(summary);
            Assert.True(
                byteCount <= SchemaSummaryRenderer.MaxSummaryBytes,
                $"Section '{section}' rendered {byteCount} bytes, over the "
                + $"{SchemaSummaryRenderer.MaxSummaryBytes}-byte bound.");

            sizes[section] = byteCount;
        }

        var largest = sizes.OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .First();

        Assert.True(
            largest.Key == LargestDigestSection && largest.Value == LargestObservedDigestBytes,
            $"The largest digest at the current pin is '{largest.Key}' at {largest.Value} bytes, not "
            + $"'{LargestDigestSection}' at {LargestObservedDigestBytes} bytes. If an engine pin bump "
            + "moved it, update LargestDigestSection/LargestObservedDigestBytes here AND the measured "
            + "figure published in docs/tools-and-resources.md's get_schema summary-size note.");

        Assert.True(
            sizes[SchemaSectionResolver.FullSection] == FullSectionDigestBytes,
            $"The 'full' digest is {sizes[SchemaSectionResolver.FullSection]} bytes, not the pinned "
            + $"{FullSectionDigestBytes}. Update FullSectionDigestBytes here AND the figure published "
            + "in docs/tools-and-resources.md's get_schema summary-size note.");
    }

    [Fact]
    public void Render_QuotesTheSchemasOwnDescriptionVerbatimRatherThanParaphrasingIt()
    {
        using var document = VendoredComposedSchema.Parse();
        var resolved = Assert.IsType<SchemaSectionResolution.Ok>(
            SchemaSectionResolver.Resolve(document.RootElement, "metadata"));

        var summary = SchemaSummaryRenderer.Render("metadata", "v1", resolved.Subtree, document.RootElement);

        // The subtree's own description, and one field's, must appear verbatim — this is what
        // "generated from the schema's own description annotations" means concretely.
        var subtreeDescription = resolved.Subtree.GetProperty("description").GetString()!;
        var ownerDescription = resolved.Subtree
            .GetProperty("properties").GetProperty("owner").GetProperty("description").GetString()!;

        Assert.Contains(subtreeDescription, summary, StringComparison.Ordinal);
        Assert.Contains(ownerDescription, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OmitsAFieldWithNoDescriptionRatherThanFillingItWithPlaceholderText()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "description": "A synthetic subtree.",
              "type": "object",
              "required": ["annotated"],
              "properties": {
                "annotated": { "type": "string", "description": "This field is documented." },
                "unannotated": { "type": "integer" }
              }
            }
            """);

        var summary = SchemaSummaryRenderer.Render(
            "metadata", "v1", document.RootElement, document.RootElement);

        Assert.Contains("annotated", summary, StringComparison.Ordinal);
        Assert.Contains("This field is documented.", summary, StringComparison.Ordinal);

        // The unannotated field is ABSENT — not listed with invented prose, not listed with a
        // placeholder, not listed bare.
        Assert.DoesNotContain("unannotated", summary, StringComparison.Ordinal);
        foreach (var placeholder in new[] { "TODO", "N/A", "no description", "No description", "(none)" })
        {
            Assert.DoesNotContain(placeholder, summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_SubtreeWithNoDescriptionsAtAll_ProducesAHeadingOnlySummaryWithoutInventedProse()
    {
        using var document = JsonDocument.Parse(
            """
            { "type": "object", "properties": { "a": { "type": "string" } } }
            """);

        var summary = SchemaSummaryRenderer.Render(
            "variables", "v1", document.RootElement, document.RootElement);

        // Still a well-formed document (the caller asked for a summary and gets one), but every
        // line of it is either structural or sourced — nothing describing "a" is fabricated.
        Assert.Contains("variables", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("**a**", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FollowsALocalRefSoARefOnlyPropertyIsStillDescribedFromTheSchemaItself()
    {
        using var document = VendoredComposedSchema.Parse();
        var resolved = Assert.IsType<SchemaSectionResolution.Ok>(
            SchemaSectionResolver.Resolve(document.RootElement, "full"));

        var summary = SchemaSummaryRenderer.Render("full", "v1", resolved.Subtree, document.RootElement);

        // The root's `metadata` property is a bare {"$ref": "#/$defs/metadata"} — its description
        // lives only at the target. Following the ref is what lets the full summary describe it
        // without inventing anything; not following it would silently drop the field.
        var metadataDescription = document.RootElement
            .GetProperty("$defs").GetProperty("metadata").GetProperty("description").GetString()!;

        Assert.Contains("metadata", summary, StringComparison.Ordinal);
        Assert.Contains(metadataDescription, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnOversizedSubtree_IsTruncatedAtAnEntryBoundaryAndSaysSo()
    {
        // A synthetic subtree far larger than the bound: 200 fields, each with a long description.
        var builder = new StringBuilder("{\"description\":\"Big.\",\"type\":\"object\",\"properties\":{");
        for (var i = 0; i < 200; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("\"field").Append(i).Append("\":{\"type\":\"string\",\"description\":\"")
                .Append(new string('d', 300)).Append("\"}");
        }

        builder.Append("}}");

        using var document = JsonDocument.Parse(builder.ToString());

        var summary = SchemaSummaryRenderer.Render(
            "metadata", "v1", document.RootElement, document.RootElement);

        Assert.True(Encoding.UTF8.GetByteCount(summary) <= SchemaSummaryRenderer.MaxSummaryBytes);
        // Truncation is announced rather than silent — a host must be able to tell the digest is
        // partial and fall back to format "json-schema" for the rest.
        Assert.Contains("truncated", summary, StringComparison.OrdinalIgnoreCase);
        // Cut at a whole ENTRY, never mid-description: every field the digest does list carries its
        // complete 300-character description, so the count of rendered entries equals the count of
        // complete description runs. A mid-description cut would leave one more entry than run.
        var renderedEntries = CountOccurrences(summary, "- **field");
        var completeDescriptions = CountOccurrences(summary, new string('d', 300));
        Assert.True(renderedEntries > 0, "Expected the truncated digest to still list some fields.");
        Assert.Equal(renderedEntries, completeDescriptions);
    }

    [Fact]
    public void Render_ASectionDescriptionLargerThanTheWholeBudget_IsDroppedRatherThanBreachingIt()
    {
        // The bound must be a POSTCONDITION of Render, not a property of the document that happens
        // to be pinned today. Every real section's own description is short, so only a synthetic one
        // can exercise the clamp — and a future pin bump could bring the real thing.
        //
        // Built by repeating a distinctive sentinel rather than one filler character: the assertion
        // below has to mean "no fragment of this prose survived", and a single-character probe would
        // only pass by the accident that no structural output happens to contain that character
        // today. The sentinel repeats every few dozen characters, so any surviving run long enough to
        // be a quotation contains at least one whole copy of it.
        const string sentinel = "OVERSIZED-DESCRIPTION-MUST-NOT-BE-QUOTED";
        var repeats = ((SchemaSummaryRenderer.MaxSummaryBytes + 4096) / (sentinel.Length + 1)) + 1;
        var oversized = string.Join(' ', Enumerable.Repeat(sentinel, repeats));
        using var document = JsonDocument.Parse(
            "{\"description\":\"" + oversized + "\",\"type\":\"object\",\"properties\":{"
            + "\"kept\":{\"type\":\"string\",\"description\":\"A short field description.\"}}}");

        var summary = SchemaSummaryRenderer.Render(
            "metadata", "v1", document.RootElement, document.RootElement);

        Assert.True(
            Encoding.UTF8.GetByteCount(summary) <= SchemaSummaryRenderer.MaxSummaryBytes,
            $"The digest was {Encoding.UTF8.GetByteCount(summary)} bytes, over the "
            + $"{SchemaSummaryRenderer.MaxSummaryBytes}-byte bound.");

        // Dropped WHOLE, never clipped mid-sentence: not one word of the oversized description
        // survives, because a partial quotation would misrepresent the schema's own prose.
        Assert.DoesNotContain(sentinel, summary, StringComparison.Ordinal);

        // And the drop is announced through the same notice mechanism a truncated field list uses.
        Assert.Contains("truncated", summary, StringComparison.OrdinalIgnoreCase);

        // The header and the (short) field entries still render — dropping the description clamps
        // the digest, it does not empty it.
        Assert.Contains("Schema version: `v1`", summary, StringComparison.Ordinal);
        Assert.Contains("- **kept**", summary, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
