namespace Vouchfx.Mcp.Resources;

// Vouchfx.Mcp.Resources — the vouchfx:// URI set (Sprint 5 / US-S5-01; spec §6, plan §2.4).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// TWO SCHEMES, SIDE BY SIDE, ON PURPOSE — plan D4
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// This server already served content under `vouchfx-docs:///` (Sprint 1: the two vendored engine
// documents, and the templated errors catalogue). Sprint 5 adds `vouchfx://` and does NOT rename,
// migrate, alias-away or deprecate the older scheme: D4 says the codebase's existing URIs win, and
// a host that cached `vouchfx-docs:///recipes` keeps resolving it byte for byte. The one place the
// two meet is the errors catalogue, which is deliberately served under BOTH — see
// DiagnosticResourceRegistry.AliasUriTemplate for why that is an alias rather than a move.
//
// So: `vouchfx-docs:///` is the vendored-document scheme (frozen, additive-only), and `vouchfx://`
// is this sprint's addressable-content scheme. Neither is "the new one" for content the other
// already serves.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY EVERY URI LIVES IN THIS ONE FILE
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// The same reason Contracts/VfxCodeCatalogue is the single VFX-* registry: a URI is a PUBLISHED
// CONTRACT a host caches against, so it must have exactly one spelling in this codebase. Each
// registry below reads its template from here; nothing composes a `vouchfx://` string inline. The
// golden tests assert `resources/templates/list` against these constants rather than against
// re-typed literals, so a typo cannot be introduced on both sides at once and pass.
//
// The `{…}` placeholders are RFC 6570 level-1 expansions, which is all the MCP SDK's own template
// matcher supports and all this server needs — no query expansion, no path-segment lists.

/// <summary>
/// Every <c>vouchfx://</c> resource URI and URI template this server advertises — the single
/// spelling of each, shared by the registries that serve them and the tests that pin them.
/// </summary>
public static class VouchfxResourceUris
{
    /// <summary>The scheme and authority-less prefix every URI below starts with.</summary>
    /// <remarks>
    /// <c>vouchfx://</c> with a non-empty first segment (<c>schema</c>, <c>docs</c>, …), so the
    /// segment after the scheme is the URI's AUTHORITY component rather than a path segment. That is
    /// deliberate and matches spec §6's own spelling; the SDK's template matcher operates on the
    /// whole string, so the distinction never has to be reasoned about at resolution time. Contrast
    /// <c>vouchfx-docs:///</c>, whose empty authority is Sprint 1's equally deliberate choice and is
    /// not being retro-fitted here.
    /// </remarks>
    public const string Scheme = "vouchfx";

    /// <summary>
    /// <c>vouchfx://schema/{version}</c> — the vendored composed schema, addressed by language
    /// schema version. <see cref="LatestSchemaVersionAlias"/> resolves to whatever version the
    /// vendored document declares.
    /// </summary>
    public const string SchemaTemplate = "vouchfx://schema/{version}";

    /// <summary>
    /// The <c>{version}</c> value that means "whatever the embedded schema says it is" — so a host
    /// never has to know the literal version string in advance (US-S5-01 AC-002).
    /// </summary>
    /// <remarks>
    /// Reading <c>vouchfx://schema/latest</c> and <c>vouchfx://schema/&lt;that version&gt;</c>
    /// returns BYTE-IDENTICAL content, because both serve the same
    /// <see cref="Vouchfx.Mcp.Schema.VendoredComposedSchema.RawJson"/> string — there is no second
    /// document and therefore no way for the alias to drift from its target. Pinned by
    /// <c>SchemaResourceRegistryTests</c> and again over the wire by the golden tests.
    /// </remarks>
    public const string LatestSchemaVersionAlias = "latest";

    /// <summary>
    /// <c>vouchfx://docs/errors/{code}</c> — the scheme ALIAS for Sprint 1's
    /// <c>vouchfx-docs:///errors/{code}</c>. Same bytes, both URIs live.
    /// </summary>
    public const string ErrorPageTemplate = "vouchfx://docs/errors/{code}";

    /// <summary>
    /// <c>vouchfx://examples/{name}</c> — one complete, comment-annotated sample suite per
    /// <see cref="Vouchfx.Mcp.Examples.ExampleSuites.All"/> entry.
    /// </summary>
    public const string ExampleTemplate = "vouchfx://examples/{name}";

    /// <summary>
    /// <c>vouchfx://workspace/specs</c> — an index of the <c>.e2e.yaml</c> suites under the
    /// configured workspace's <c>specsDir</c>.
    /// </summary>
    /// <remarks>
    /// <b>A FIXED URI, not a template, and therefore advertised through <c>resources/list</c> rather
    /// than <c>resources/templates/list</c>.</b> US-S5-01's Gherkin lists it among the templates,
    /// which cannot be honoured literally: it carries no <c>{…}</c> expansion, the MCP protocol
    /// separates concrete resources from URI templates precisely on that property, and the SDK
    /// classifies by it (a variable-free <c>UriTemplate</c> produces a
    /// <c>ProtocolResource</c>). Advertising a variable-free "template" would tell a host to expand
    /// something with no variables and would leave the directly-readable URI absent from the list
    /// hosts actually read from. So it is served — and golden-tested — as a concrete resource, which
    /// is the shape that makes the Gherkin's INTENT ("the host can discover and read it") true.
    /// </remarks>
    public const string WorkspaceSpecsUri = "vouchfx://workspace/specs";

    /// <summary>
    /// <c>vouchfx://docs/dsl-guide</c> — US-S5-05's authoring guide, written for a model reader.
    /// </summary>
    /// <remarks>
    /// A FIXED URI like <see cref="WorkspaceSpecsUri"/>, and for the same protocol reason: it carries
    /// no expansion, so it is a concrete resource rather than a template. There is exactly one guide.
    /// </remarks>
    public const string DslGuideUri = "vouchfx://docs/dsl-guide";

    /// <summary>
    /// <c>vouchfx://runs/{runId}/verdict</c> — the same diagnosis <c>explain_run</c> returns for
    /// that run, as a cacheable document.
    /// </summary>
    public const string RunVerdictTemplate = "vouchfx://runs/{runId}/verdict";

    /// <summary>
    /// <c>vouchfx://runs/{runId}/events</c> — the same first page <c>get_run_events</c> returns for
    /// that run.
    /// </summary>
    public const string RunEventsTemplate = "vouchfx://runs/{runId}/events";

    /// <summary>
    /// <c>vouchfx://runs/{runId}/logs/{container}</c> — the same log inventory
    /// <c>get_run_artifacts</c> returns for that run and container, <b>which is empty in this
    /// build</b> and says so rather than fabricating lines. See
    /// <see cref="RunResourceRegistry"/>.
    /// </summary>
    public const string RunLogsTemplate = "vouchfx://runs/{runId}/logs/{container}";

    /// <summary>
    /// Every <c>vouchfx://</c> URI TEMPLATE (one with at least one <c>{…}</c> expansion) this
    /// server advertises, in the order <c>resources/templates/list</c> reports them.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <see cref="WorkspaceSpecsUri"/> — see that constant's remarks — and
    /// excludes <c>vouchfx-docs:///errors/{code}</c>, which is Sprint 1's own template and is
    /// registered by its own registry. This list exists so a test can assert the advertised set
    /// EXACTLY rather than "at least these", which is what makes an unreviewed sixth template fail
    /// the build.
    /// </remarks>
    public static IReadOnlyList<string> AllTemplates { get; } =
    [
        SchemaTemplate,
        ErrorPageTemplate,
        ExampleTemplate,
        RunVerdictTemplate,
        RunEventsTemplate,
        RunLogsTemplate,
    ];
}
