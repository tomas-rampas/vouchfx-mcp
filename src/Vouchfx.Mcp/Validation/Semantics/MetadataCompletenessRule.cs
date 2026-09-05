using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1211 — the suite declares no <c>metadata.owner</c> and/or no <c>metadata.tags</c>
/// (spec §5.5: info).
/// </summary>
/// <remarks>
/// <para>
/// <b>The set's only <c>info</c>, and the severity is the finding's whole character:</b> nothing
/// about how the suite runs changes. What changes is what happens AFTER it fails — the composed
/// schema's own field descriptions say <c>owner</c> is "the team or individual responsible for this
/// test; used by the runner's selection language" and <c>tags</c> are "labels used to select subsets
/// of tests during a CI run". An unowned, untagged suite cannot be routed or selected.
/// </para>
/// <para>
/// <b>One finding per suite, not one per missing field.</b> The two are the same omission with the
/// same one-line fix (fill in the metadata block), and two <c>info</c> entries on every unowned
/// suite would be noise in the channel a host is most likely to display wholesale. The MESSAGE names
/// whichever of the two is actually missing, so the fix stays specific.
/// </para>
/// <para>
/// <b>An empty <c>tags: []</c> counts as missing.</b> It declares the field without declaring a
/// single selector, so the runner's selection language still cannot address the suite — which is the
/// condition the code is about, not the presence of a key.
/// </para>
/// <para>
/// <b>No path when there is no metadata block.</b> A path naming an absent node resolves to no line
/// and would mislead a host that trusts it; the finding is about the document as a whole in that
/// case, and says so by carrying no location at all.
/// </para>
/// </remarks>
internal sealed class MetadataCompletenessRule : ISemanticRule
{
    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.MetadataIncomplete;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Document.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var hasMetadata = context.Document.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object;

        var ownerMissing = !hasMetadata || SuiteDocument.StringProperty(metadata, "owner") is null;
        var tagsMissing = !hasMetadata || !HasAtLeastOneTag(metadata);

        if (!ownerMissing && !tagsMissing)
        {
            return [];
        }

        var missing = (ownerMissing, tagsMissing) switch
        {
            (true, true) => "no metadata.owner and no metadata.tags",
            (true, false) => "no metadata.owner",
            _ => "no metadata.tags",
        };

        return
        [
            SemanticFinding.Create(
                context,
                Code,
                SemanticFinding.Info,
                $"This suite declares {missing}. Both are used to route a failure to its team and to "
                + "select subsets of suites in CI; a suite without them still runs, but cannot be "
                + "addressed by either.",
                hasMetadata ? SuitePath.Root.Property("metadata") : null),
        ];
    }

    private static bool HasAtLeastOneTag(JsonElement metadata) =>
        metadata.TryGetProperty("tags", out var tags)
        && tags.ValueKind == JsonValueKind.Array
        && tags.EnumerateArray().Any();
}
