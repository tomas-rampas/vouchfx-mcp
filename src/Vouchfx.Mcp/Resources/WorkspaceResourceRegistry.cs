using ModelContextProtocol.Server;
using Vouchfx.Mcp.Specs;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// <c>vouchfx://workspace/specs</c> (Sprint 5 / US-S5-01 AC-005) — an index of the suites already
/// living under the configured workspace's <c>specsDir</c>, so a host can avoid authoring a second
/// suite covering a flow one of them already covers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A FIXED resource, therefore advertised through <c>resources/list</c></b> rather than
/// <c>resources/templates/list</c> — see <see cref="VouchfxResourceUris.WorkspaceSpecsUri"/>'s own
/// remarks for why the story's Gherkin cannot be honoured literally on that point and what is served
/// instead. It is the FIRST addition to <c>resources/list</c> since Sprint 1, which took that listing
/// from two entries to three; the two vendored documents are byte-unchanged and still first, which
/// <c>VendoredResourceRegressionTests</c> pins.
/// </para>
/// <para>
/// <b>Not cached, deliberately.</b> Every read re-enumerates the directory. The alternative — a
/// memoised index invalidated by a watcher — would make this server hold a file-system watch for the
/// life of the session to serve a resource read that costs a directory walk, and would return stale
/// content on the one workflow this resource exists for (the host writes a suite, then asks what
/// suites exist). MCP resources are cached by the HOST, keyed on URI, and this server's job is to
/// answer truthfully when asked.
/// </para>
/// <para>
/// <b>The handler is ASYNC and takes a <see cref="CancellationToken"/>, and both matter.</b> The
/// index build enumerates a directory tree and then crosses a process boundary to parse what it
/// found; a synchronous handler with no token would block an SDK request thread for that whole
/// duration with no way for an abandoning host to stop it (a security review's finding — the first
/// version of this resource was exactly that). The token is threaded to the enumeration loop and into
/// <see cref="SpecIndexWorkerClient"/>, which kills its worker's process tree on cancellation.
/// </para>
/// <para>
/// <b>All of the work — and every safety decision — is <see cref="WorkspaceSpecIndexer"/>'s.</b> This
/// type is the MCP surface and nothing else, mirroring how every tool in this server is a thin
/// wrapper over an orchestrator.
/// </para>
/// </remarks>
public static class WorkspaceResourceRegistry
{
    /// <summary>Creates the workspace spec-index resource.</summary>
    /// <param name="workspace">
    /// The startup workspace, or <see langword="null"/> when the host supplied no <c>--workspace</c>
    /// flag. Captured once, because it is a startup fact fixed for the process's lifetime — the same
    /// treatment every orchestrator gives it. <b>Null is a supported mode</b>, not a missing
    /// dependency: the resource then reports <c>workspaceConfigured: false</c> with a reason rather
    /// than failing (sprint-00-overview.md §3 stance (b)).
    /// </param>
    public static McpServerResource Create(Workspace? workspace) =>
        McpServerResource.Create(
            async (CancellationToken cancellationToken) =>
                ResourceJson.Serialise(
                    // budget: null — the production clocks. The parameter exists so TESTS can be
                    // deterministic (see BuildAsync's own remarks); nothing in the server overrides it.
                    await WorkspaceSpecIndexer.BuildAsync(workspace, budget: null, cancellationToken)
                        .ConfigureAwait(false)),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.WorkspaceSpecsUri,
                Name = "vouchfx workspace suite index",
                Description =
                    "An index of every .e2e.yaml suite under this workspace's specs directory "
                    + "(<workspace root>/e2e): each suite's path, metadata.name, tags, step types and "
                    + "step count. Read it BEFORE authoring a new suite, so you extend or reuse an "
                    + "existing scenario instead of duplicating it. Read-only enumeration plus a light "
                    + "parse — it never validates, never runs and never writes. When this server was "
                    + "launched without --workspace there is no specs directory to index, and the "
                    + "result says so via workspaceConfigured: false rather than pretending the "
                    + "directory is empty.",
                MimeType = ResourceJson.MimeType,
            });
}
