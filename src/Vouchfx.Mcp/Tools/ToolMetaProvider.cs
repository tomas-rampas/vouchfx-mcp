using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Composes the one <see cref="ToolMeta"/> instance this process stamps onto every successful tool
/// result (US-S1-02), from the three sources that own its fields.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="ToolMeta"/> itself so the record stays a pure shape, exactly like
/// its <c>Contracts/</c> siblings <see cref="VfxError"/> and <see cref="Diagnostic"/> — the ambient
/// reads (an embedded resource, an assembly attribute, the startup workspace) live at the boundary
/// that consumes them rather than inside the contract every host also deserialises.
/// </para>
/// <para>
/// Computed ONCE, and reference-stable: all three inputs are fixed for the lifetime of the process
/// (the embedded schema and the assembly's own version are immutable; the workspace is resolved at
/// startup and never moves), so re-deriving them per tool call would be pure waste on a value
/// stamped onto every single result. <see cref="Current"/> being reference-stable is also what lets
/// <see cref="StructuredToolResult"/> pre-serialise it exactly once — see that type's
/// <c>MetaRawJsonBytes</c>.
/// </para>
/// <para>
/// <b>Why the workspace arrives through a static publish rather than the DI graph</b> (US-S3-08).
/// Everything else US-S3-08 wires — <see cref="PathSafetyGuard"/>'s containment, and every
/// orchestrator that feeds it — takes the <see cref="Workspace"/> as an ordinary constructor or
/// method argument through
/// <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/>, because those are instances.
/// This value cannot follow that route without threading a meta instance through all eighteen tools
/// and every one of <see cref="StructuredToolResult"/>'s call sites, purely to carry a string that
/// is a PROCESS-wide startup fact in production. So the workspace root is published here once, by
/// <c>Program.cs</c>, before anything reads <see cref="Current"/>.
/// </para>
/// <para>
/// <b>What that costs, stated honestly.</b> A second in-process server with a DIFFERENT workspace
/// cannot have its own stamp — which is why <see cref="PublishStartupWorkspace"/> throws rather than
/// silently re-stamping, and why the in-memory <c>McpTestHarness</c> never calls it: harness-hosted
/// tests all run at the unconfigured default (today's value, unchanged), and the
/// workspace-configured stamp is covered end to end by a REAL spawned <c>vouchfx-mcp</c> process
/// instead (<c>RealWorkspaceProcessTests</c>). That keeps the static write out of the test process
/// entirely, so there is no cross-test state to bleed. The composition itself is a pure function
/// (<see cref="Compose"/>) and is unit-tested directly.
/// </para>
/// </remarks>
internal static class ToolMetaProvider
{
    private static Workspace? _startupWorkspace;

    /// <summary>
    /// Materialised lazily rather than by a static field initialiser so that
    /// <see cref="PublishStartupWorkspace"/> is guaranteed to run FIRST. A plain
    /// <c>static ToolMeta Current { get; } = …</c> would be a static field initialiser on a
    /// <c>beforefieldinit</c> type, which the runtime may execute at any point before the first
    /// field access — including on the call to <see cref="PublishStartupWorkspace"/> itself, which
    /// touches <see cref="_startupWorkspace"/>. <see cref="Lazy{T}"/> makes the ordering explicit
    /// instead of dependent on that guarantee, and its
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> mode is what keeps
    /// <see cref="Current"/> a single, reference-stable instance under concurrent first access.
    /// </summary>
    private static readonly Lazy<ToolMeta> LazyCurrent =
        new(() => Compose(_startupWorkspace), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The provenance stamp for this process — see <see cref="ToolMeta"/> for each field.</summary>
    public static ToolMeta Current => LazyCurrent.Value;

    /// <summary>
    /// Records the workspace resolved at server start, so <see cref="Current"/> can report its root.
    /// Call exactly once, from <c>Program.cs</c>, before anything reads <see cref="Current"/>.
    /// </summary>
    /// <param name="workspace">
    /// The startup workspace, or <see langword="null"/> when no <c>--workspace</c> flag was given —
    /// in which case this call is a no-op and the stamp keeps reporting the process's base
    /// directory, exactly as it did before US-S3-08.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Current"/> has already been materialised (the stamp is pre-serialised downstream
    /// and cannot change afterwards), or a workspace was already published. Both are programming
    /// errors in startup ordering, and both fail loudly rather than leaving the stamp quietly
    /// disagreeing with the workspace the guards actually enforce.
    /// </exception>
    internal static void PublishStartupWorkspace(Workspace? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        if (LazyCurrent.IsValueCreated)
        {
            throw new InvalidOperationException(
                "The tool-result provenance stamp has already been composed; a workspace must be published before it is first read.");
        }

        if (_startupWorkspace is not null)
        {
            throw new InvalidOperationException("A startup workspace has already been published for this process.");
        }

        _startupWorkspace = workspace;
    }

    /// <summary>
    /// Builds the stamp for <paramref name="workspace"/> — a pure function of its argument plus two
    /// immutable ambient reads, so it is directly unit-testable without touching process state.
    /// </summary>
    internal static ToolMeta Compose(Workspace? workspace) => new(
        VendoredSchemaVersion.Value,
        ServerIdentity.Version,
        workspace?.Root ?? ResolveProcessBaseDirectory());

    /// <summary>
    /// The fallback <c>workspaceRoot</c> when no workspace is configured: this process's resolved
    /// base directory — the value US-S1-02 shipped and US-S3-08 keeps for exactly the callers that
    /// never opted into a workspace.
    /// </summary>
    /// <remarks>
    /// <see cref="AppContext.BaseDirectory"/> rather than
    /// <see cref="Environment.CurrentDirectory"/>: the current directory is mutable at runtime by
    /// any code in the process, so a value derived from it could not be computed once and would not
    /// be stable across the calls of one session — the opposite of what a provenance stamp is for.
    /// Canonicalised through <see cref="Path.GetFullPath(string)"/> and
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/> so the wire form is one consistent
    /// shape rather than sometimes carrying a trailing separator
    /// (<see cref="AppContext.BaseDirectory"/> does), while leaving a genuine filesystem root (e.g.
    /// <c>C:\</c>) alone — <see cref="Path.TrimEndingDirectorySeparator(string)"/> deliberately does
    /// not trim a root. <see cref="Workspace.Resolve"/> canonicalises its own root identically, so
    /// the field's shape does not change with its source.
    /// <para>
    /// No secret-hygiene concern on either branch: both are directory paths this server already
    /// resolves in order to run at all, never any part of this process's ENVIRONMENT (which
    /// CLAUDE.md's secret-hygiene invariant forbids echoing into any tool result) and never a
    /// caller-supplied path. The workspace root is additionally a path the HOST itself chose and
    /// already knows — see <see cref="ToolMeta.WorkspaceRoot"/> for why that makes the configured
    /// branch strictly better for privacy than the fallback.
    /// </para>
    /// </remarks>
    private static string ResolveProcessBaseDirectory() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
}
