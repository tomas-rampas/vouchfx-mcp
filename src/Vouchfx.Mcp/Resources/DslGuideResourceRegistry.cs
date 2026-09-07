using ModelContextProtocol.Server;
using Vouchfx.Mcp.Docs;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// <c>vouchfx://docs/dsl-guide</c> (Sprint 5 / US-S5-05) — the authoring guide written for a MODEL
/// reader: short, imperative, example-dense, and every example schema-valid.
/// </summary>
/// <remarks>
/// <para>
/// <b>A FIXED resource, not a template member.</b> The AC allows either ("addressable by URI, not
/// only reachable by reading the raw docs file"), and there is exactly one guide: a template over a
/// one-member set would tell a host to expand a variable with one legal value. It joins
/// <c>vouchfx://workspace/specs</c> and the two vendored documents in <c>resources/list</c>, taking
/// that listing from three entries to four.
/// </para>
/// <para>
/// <b>Under the <c>vouchfx://docs/</c> prefix, beside the errors catalogue</b> — spec §6's own
/// spelling, and it groups the two documentation surfaces a host discovers by URI. The two VENDORED
/// documents keep their Sprint 1 <c>vouchfx-docs:///</c> scheme (plan D4: published URIs are not
/// renamed), which is why this repository serves documentation under two schemes at once.
/// </para>
/// <para>
/// <b>Served from the repository's own <c>docs/</c> tree, embedded.</b> Unlike the two vendored
/// documents this is not byte-gated against the engine repo — it is this server's own writing about
/// this server's own tools, and <c>DslGuideForAgentsTests</c> is what keeps it honest: every fenced
/// example is validated against the vendored schema on every build. Landing it under <c>docs/</c>
/// (rather than beside the prompts) is deliberate: <c>scripts/build_site.py</c> publishes everything
/// tracked there, so the same file a host reads by URI is the page a human reads on the site.
/// </para>
/// </remarks>
public static class DslGuideResourceRegistry
{
    /// <summary>Creates the DSL-guide resource.</summary>
    public static McpServerResource Create() =>
        McpServerResource.Create(
            () => DslGuideDocument.RawMarkdown,
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.DslGuideUri,
                Name = "vouchfx DSL guide for agents",
                Description =
                    "How to author a vouchfx .e2e.yaml suite, written for a model reader: short, "
                    + "imperative and example-dense. Covers the file's four blocks, state threading "
                    + "with capture and {placeholder}, RETRY semantics with an explicit timeout, "
                    + "secrets as ${secret:...} references only, the four-outcome verdict taxonomy, "
                    + "and a do/don't list. Every YAML example in it is a complete document that is "
                    + "validated against the vendored composed schema in this repository's own tests, "
                    + "so it cannot drift from what validate_suite accepts. Read this first when "
                    + "authoring.",
                MimeType = ResourceJson.MarkdownMimeType,
            });
}
