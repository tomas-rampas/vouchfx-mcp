using Vouchfx.Mcp.Run;

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
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// AND SINCE ISSUE #87, THE SINGLE URI *CONSTRUCTION* AUTHORITY TOO — NOT ONLY THE DECLARATION
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Declaring each template once stops two REGISTRIES from spelling a URI differently. It does not,
// on its own, stop a TOOL RESULT from spelling one differently: the moment a payload carries a
// `resourceUri`, a second party is composing the same published string. Issue #87 populated
// get_run_artifacts' `reports.events.resourceUri` and get_run_events' `resourceUri`, so the
// expansion moved here as well — see RunEventsUri. Nothing outside this file may concatenate,
// interpolate or String.Format a `vouchfx://` URI, and VouchfxResourceUriSourceGuardTests holds
// that structurally by scanning src/ for the literal with string literals INTACT.
//
// WHICH SURFACES CARRY A resourceUri, AND WHY THE OTHER THREE DO NOT (issue #87's adjudication,
// covering all five run readers so none has to be re-derived later):
//
//   * get_run_artifacts → reports.events.resourceUri = RunEventsUri(runId). The artefact IS that
//     run's event stream and RunEventsTemplate is the advertised way to read it. Populated on
//     EVERY successful call, including one whose events file has been swept: the URI names the
//     resource, and `available: false` — a separate field, already carrying exactly this fact —
//     says whether the bytes are still there. Withholding it would conflate "no such resource"
//     with "the file is gone".
//   * get_run_events → resourceUri = RunEventsUri(runId), for the same run and the same family.
//     See GetRunEventsResult.ResourceUri for the one caveat it carries (a URI has no slot for a
//     filter or a cursor, so the resource always serves the UNFILTERED FIRST page).
//   * explain_run → NOTHING, deliberately. vouchfx://runs/{runId}/verdict exists and would look
//     like the obvious third case, but explain_run is keyed by an events FILE PATH, not by a
//     runId: a caller may hand it a path belonging to no registered run at all, and its payload
//     (Diagnosis) carries no runId to expand a template from. That reason is decisive on its own;
//     the tempting workaround (stamp it resource-side, where a runId IS known) would additionally
//     break the resource/tool byte-equality the run resources are held to. Recorded at the model:
//     see Diagnosis' own remarks.
//   * The remaining two run readers, settled here so nobody has to re-derive them. diagnose_run →
//     NOTHING, for exactly explain_run's reason: it is path-keyed and its payload carries no runId.
//     get_step_timeline → NOTHING for a different reason — it IS runId-keyed, but there is no
//     vouchfx://runs/{runId}/timeline resource to name. The rule across all five is one sentence: a
//     surface carries a resourceUri exactly when it has a runId in hand AND an advertised resource
//     exists for that data. Adding a timeline resource would make that tool eligible; nothing else
//     would need to change here but the template and its expander.
//
// Note the word `resourceUri` is also used, in ExplainRunOrchestrator.MaxDiagnosisResponseBytes'
// remarks, for a DIFFERENT mechanism: Sprint 4's sanctioned answer to an over-budget response is a
// resourceUri hand-off meaning payload OFFLOADING (serve oversized evidence as a resource so the
// inline response shrinks). What this file produces is resource IDENTITY — naming the resource that
// serves the same data — which moves no bytes and changes no budget. The two are unrelated and
// neither blocks the other.

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
    /// server advertises, in this repository's own declared order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared order, NOT wire order</b> (a review of an earlier version of this comment, which
    /// said "in the order <c>resources/templates/list</c> reports them"). What a host receives is the
    /// SDK's to order; this list is a SET with a stable listing for readability and for diffs. Every
    /// assertion over it compares set membership, and none may start depending on the sequence.
    /// </para>
    /// <para>
    /// Deliberately excludes <see cref="WorkspaceSpecsUri"/> — see that constant's remarks — and
    /// excludes <c>vouchfx-docs:///errors/{code}</c>, which is the diagnostic catalogue's original
    /// template and is registered by its own registry. This list exists so a test can assert the
    /// advertised set EXACTLY rather than "at least these", which is what makes an unreviewed
    /// additional template fail the build.
    /// </para>
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

    /// <summary>
    /// The <c>{runId}</c> expansion, as it is spelled inside the three run templates above.
    /// </summary>
    private const string RunIdExpansion = "{runId}";

    /// <summary>The <c>{version}</c> expansion, as it is spelled inside <see cref="SchemaTemplate"/>.</summary>
    private const string SchemaVersionExpansion = "{version}";

    /// <summary>
    /// <c>vouchfx://schema/latest</c> — <see cref="SchemaTemplate"/> expanded with
    /// <see cref="LatestSchemaVersionAlias"/>.
    /// </summary>
    /// <remarks>
    /// Exists so <see cref="SchemaResourceRegistry"/> can TELL a host which URI to read when it asks
    /// for a version this build does not carry, without typing the scheme out in a message. That
    /// message is an instruction a host will act on, so it has to be the advertised URI and not merely
    /// something that looks like it; a hand-typed copy in an error string is precisely the second
    /// spelling this type exists to prevent, and was one until issue #87's source guard found it.
    /// <para>
    /// A property rather than a <c>SchemaUri(version)</c> method, deliberately: the alias is the only
    /// version this codebase ever names for itself, and a general expander would have to answer what
    /// to do about percent-encoding caller-supplied text — a question worth answering when something
    /// actually asks it.
    /// </para>
    /// </remarks>
    public static string LatestSchemaUri { get; } =
        Expand(SchemaTemplate, SchemaVersionExpansion, LatestSchemaVersionAlias);

    /// <summary>
    /// <see cref="RunEventsTemplate"/> expanded for one run — the ONLY way a tool result composes a
    /// <c>vouchfx://</c> URI (issue #87; see this file's header).
    /// </summary>
    /// <param name="runId">
    /// A run id this server MINTED, taken from the registry entry itself rather than from the
    /// caller's argument. That is a contract, not a preference: the two are equal whenever a lookup
    /// succeeded (every registry matches ordinally), and reading it from the entry keeps the
    /// published URI sourced from the same place <c>get_run_artifacts</c> already takes its
    /// <c>runId</c> field from, so the two can never disagree within one payload.
    /// </param>
    /// <returns>
    /// <c>vouchfx://runs/&lt;runId&gt;/events</c> — byte-for-byte the URI
    /// <see cref="RunResourceRegistry"/> advertises and resolves, with no percent-encoding step
    /// needed or wanted: <see cref="RunRegistryCore.IsWellFormedRunId"/> admits only <c>run-</c> plus
    /// 32 lowercase hex, every character of which is RFC 3986 unreserved.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="runId"/> is not of the minted shape. <b>A throw rather than a null</b>, because
    /// there is no honest URI to return: a <c>vouchfx://runs/…</c> string built around an id this
    /// server could not have minted would be advertised-looking text that no resource resolves, and
    /// emitting one is the fabrication every gap in this codebase is written to avoid. It is
    /// unreachable from either call site — both pass a registry entry's own id — which is what makes
    /// it a programming-error guard rather than a caller-facing condition.
    /// </exception>
    public static string RunEventsUri(string runId)
    {
        if (!RunRegistryCore.IsWellFormedRunId(runId))
        {
            throw new ArgumentException(
                "A run resource URI may only be built from a run id this server minted "
                + $"('{RunRegistryCore.RunIdPrefix}' plus 32 lowercase hex characters).",
                nameof(runId));
        }

        return Expand(RunEventsTemplate, RunIdExpansion, runId);
    }

    /// <summary>
    /// Substitutes one RFC 6570 level-1 expansion, refusing to return a template that did not expand.
    /// </summary>
    /// <remarks>
    /// The check is DEFENSIVE and earns its two lines: <see cref="string.Replace(string, string)"/> on
    /// a needle that is not present returns the haystack unchanged, so renaming a template's
    /// placeholder would silently publish a URI with literal braces in it — the one failure mode this
    /// file exists to make impossible, arriving with no compiler error anywhere.
    /// <c>VouchfxResourceUriTests.TheRunEventsExpansion_LeavesNoPlaceholderBehind</c> pins the same
    /// property from the test side, which is what keeps this branch unreachable.
    /// </remarks>
    private static string Expand(string template, string expansion, string value)
    {
        var expanded = template.Replace(expansion, value, StringComparison.Ordinal);

        return expanded.Contains(expansion, StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"'{template}' did not expand: its placeholder is not '{expansion}'.")
            : expanded;
    }
}
