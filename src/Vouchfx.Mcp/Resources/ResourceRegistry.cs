using ModelContextProtocol.Server;
using Vouchfx.Mcp.Diagnosis;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// Aggregates every MCP resource this server advertises — the resource analogue of
/// <see cref="Vouchfx.Mcp.Tools.ToolRegistry"/>, and the single list
/// <c>VouchfxMcpServerRegistration.AddVouchfxMcpServer</c> assigns to its
/// <c>ResourceCollection</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type appeared in Sprint 5 and not before.</b> Until US-S5-01 there were three
/// resources and the registration site listed them inline. This story takes that to ten across SIX
/// registries — <see cref="DocResourceRegistry"/>, <see cref="DiagnosticResourceRegistry"/> (which
/// contributes two, its original template and its Sprint 5 alias),
/// <see cref="SchemaResourceRegistry"/>, <see cref="ExampleResourceRegistry"/>,
/// <see cref="WorkspaceResourceRegistry"/> and <see cref="RunResourceRegistry"/> — at which point the
/// registration method, whose job is wiring orchestrators, would otherwise have grown a second,
/// unrelated composition concern. The tool side settled this question in Sprint 1 with
/// <c>ToolRegistry</c>; this is the same answer for the same reason. (An earlier revision said
/// "five", miscounting the errors alias as free because it shares a registry with its original — a
/// peer review's nit, and the kind of number worth writing out rather than estimating.)
/// </para>
/// <para>
/// <b>Append-only by convention here, but the WIRE order is the SDK's, not this list's</b> — and that
/// is a measured fact rather than an assumption. On ModelContextProtocol 1.4.1, registering these in
/// the declared order produces a <c>resources/templates/list</c> in an UNRELATED order (measured:
/// <c>vouchfx://runs/{runId}/events</c> came back first), consistent with the SDK holding its
/// resource collection in a hash-keyed structure. So <c>RealResourceTemplatesMcpTests</c> asserts the
/// advertised sets as sorted SETS, not sequences: membership is this repository's to guarantee and
/// ordering is not, and a test that pinned an order it does not control would break on an SDK bump
/// for no product reason. The grouping below is still written append-only, because it is what a
/// reader of this file uses to see what each sprint added.
/// </para>
/// <para>
/// <b>The static/templated split is the SDK's to make, not this list's.</b> Every entry below is an
/// <see cref="McpServerResource"/>; the SDK routes one to <c>resources/list</c> or
/// <c>resources/templates/list</c> according to whether its <c>UriTemplate</c> carries a <c>{…}</c>
/// expansion. That is why the two vendored documents and <c>vouchfx://workspace/specs</c> end up in
/// the former and the six templates in the latter, without this file classifying anything —
/// classifying it here would be a second opinion able to disagree with the protocol's.
/// </para>
/// </remarks>
public static class ResourceRegistry
{
    /// <summary>
    /// Creates every resource this server serves, grouped for readability: the two vendored
    /// <c>vouchfx-docs:///</c> documents, then the diagnostic-catalogue template, then the
    /// <c>vouchfx://</c> set.
    /// </summary>
    /// <remarks>
    /// <b>This grouping is a READING order, not a wire guarantee</b> (a review of an earlier version
    /// of this comment, which claimed the first two "stay at the head of <c>resources/list</c>").
    /// The order a host sees is the SDK's to choose, and every test in this repository asserts the
    /// advertised SET rather than a sequence — deliberately, because a promise nothing enforces and
    /// nothing owns is one a reader can act on and be wrong. Nothing about correctness depends on the
    /// order below.
    /// </remarks>
    /// <param name="runRegistry">The one run registry — see <see cref="RunResourceRegistry.CreateAll"/>.</param>
    /// <param name="explainRun">The instance <c>explain_run</c> uses.</param>
    /// <param name="getRunEvents">The instance <c>get_run_events</c> uses.</param>
    /// <param name="getRunArtifacts">The instance <c>get_run_artifacts</c> uses.</param>
    /// <param name="workspace">
    /// The startup workspace, or <see langword="null"/> when none was configured — threaded to
    /// <c>vouchfx://workspace/specs</c> and to nothing else here.
    /// </param>
    public static IReadOnlyList<McpServerResource> CreateAll(
        IRunRegistry runRegistry,
        ExplainRunOrchestrator explainRun,
        GetRunEventsOrchestrator getRunEvents,
        GetRunArtifactsOrchestrator getRunArtifacts,
        Workspace? workspace) =>
    [
        // ── Sprint 1: the two vendored engine documents (static) ─────────────────────────────
        .. DocResourceRegistry.CreateAll(),

        // ── Sprint 1: the diagnostic catalogue (templated), under its original URI ────────────
        DiagnosticResourceRegistry.Create(),

        // ── Sprint 5 / US-S5-01 ───────────────────────────────────────────────────────────────
        DiagnosticResourceRegistry.CreateAlias(),
        SchemaResourceRegistry.Create(),
        ExampleResourceRegistry.Create(),
        WorkspaceResourceRegistry.Create(workspace),
        DslGuideResourceRegistry.Create(),
        .. RunResourceRegistry.CreateAll(runRegistry, explainRun, getRunEvents, getRunArtifacts),
    ];
}
