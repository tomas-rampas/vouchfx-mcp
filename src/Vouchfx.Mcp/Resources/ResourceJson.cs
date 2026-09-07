using System.Text.Json;
using Vouchfx.Mcp.Tools;

namespace Vouchfx.Mcp.Resources;

// Vouchfx.Mcp.Resources — the one serialisation seam for JSON-bodied vouchfx:// resources
// (Sprint 5 / US-S5-01).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// SAME OPTIONS AS THE TOOL PATH, DELIBERATELY — AND NO `meta` STAMP, ALSO DELIBERATELY
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// US-S5-01's run resources exist to serve "the SAME data" explain_run / get_run_events /
// get_run_artifacts return (plan §2.4's resourceUri hand-off). "The same" has to mean something
// checkable, so it means this: the resource body is the tool's OWN payload record, serialised
// through the tool path's OWN JsonSerializerOptions — the identical resolver chain, the identical
// naming policy, the identical null-omission behaviour. A field named one way in the tool result
// and another way in the resource would be exactly the divergence the AC forbids, and reusing
// StructuredToolResult.Options rather than constructing a second options instance is what makes
// that structural instead of a convention somebody has to remember.
//
// What is NOT carried across is the `meta` stamp, and the reason is that `meta` is a TOOL-RESULT
// field rather than a property of the data:
//
//   * Contracts/ToolMeta is spec §4.4's per-tool-result provenance stamp. StructuredToolResult
//     attaches it at the single choke point every tool's SUCCESS travels, and its own remarks
//     record why that choke point exists — "a per-payload field is eighteen places that can drift
//     and a future nineteenth tool that can forget". Stamping it from HERE too would create the
//     second stamping site that design exists to prevent.
//   * The hand-off makes it redundant. A host reaches a run resource because a tool result pointed
//     it there; it already holds that result's `meta` for the same server, the same engine pin and
//     the same schema version. Re-serving it would be a copy whose only possible behaviour is to
//     agree.
//
// So the checkable equivalence the golden tests assert is: resource body == the tool's
// structuredContent with its `meta` property removed. That is a stronger statement than "looks
// similar", and it fails loudly if either side's shape moves.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE BUDGET, STATED RATHER THAN QUIETLY INHERITED
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Plan §2.4 frames a resourceUri as the answer when a tool response "cannot fit the 32 KB
// effective budget". A reader might therefore expect these resources to serve an UNBUDGETED,
// fuller rendering. They do not, and that is a decision rather than an omission: the orchestrators
// apply their trimming INSIDE themselves (ExplainRunOrchestrator's tiered shrink,
// GetRunEventsOrchestrator's paging), so an unbudgeted resource would require a second code path
// producing a second, larger shape — precisely the "competing second implementation" US-S5-01 and
// this sprint's own scoping rule out. What the resource genuinely adds today is ADDRESSABILITY and
// CACHEABILITY: a stable URI a host can re-read and cache without spending a tool call. Widening
// the budget for the resource form is a real, separate piece of work and belongs to whoever also
// widens the orchestrators.

/// <summary>
/// Serialises a tool payload record into the exact JSON a <c>vouchfx://</c> resource body carries.
/// </summary>
public static class ResourceJson
{
    /// <summary>The MIME type every JSON-bodied resource in this server declares.</summary>
    public const string MimeType = "application/json";

    /// <summary>
    /// The MIME type the Markdown-bodied resources declare — the two vendored documents, the
    /// diagnostic catalogue pages, and (US-S5-05) the DSL guide.
    /// </summary>
    /// <remarks>
    /// Removed as unused in US-S5-01 and restored here when <c>vouchfx://docs/dsl-guide</c> gave it a
    /// caller — which is the right order: a constant with no call site is dead weight that reads as
    /// API. The older Markdown resources still spell the literal inline in their own registries; they
    /// are not churned to use this purely for tidiness, since doing so would touch three shipped
    /// surfaces to change nothing observable.
    /// </remarks>
    public const string MarkdownMimeType = "text/markdown";

    /// <summary>
    /// The MIME type <c>vouchfx://examples/{name}</c> declares for a <c>.e2e.yaml</c> document.
    /// </summary>
    /// <remarks>
    /// <c>application/yaml</c> is the IANA-registered type since RFC 9512 (2024); the older
    /// <c>text/yaml</c> and <c>application/x-yaml</c> were never registered. Registered wins.
    /// </remarks>
    public const string YamlMimeType = "application/yaml";

    /// <summary>
    /// Serialises <paramref name="payload"/> through the SAME options every tool result uses — see
    /// this file's header for why the <c>meta</c> stamp is deliberately absent.
    /// </summary>
    public static string Serialise(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Serialize(payload, payload.GetType(), StructuredToolResult.Options);
    }
}
