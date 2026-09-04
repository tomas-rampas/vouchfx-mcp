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
/// reads (an embedded resource, an assembly attribute, the process's base directory) live at the
/// boundary that consumes them rather than inside the contract every host also deserialises.
/// </para>
/// <para>
/// Computed ONCE into a static: all three inputs are fixed for the lifetime of the process (the
/// embedded schema and the assembly's own version are immutable; the base directory does not move),
/// so re-deriving them per tool call would be pure waste on a value stamped onto every single
/// result. <see cref="Current"/> is consequently reference-stable, which is also what lets
/// <see cref="StructuredToolResult"/> pre-serialise it exactly once.
/// </para>
/// </remarks>
internal static class ToolMetaProvider
{
    /// <summary>The provenance stamp for this process — see <see cref="ToolMeta"/> for each field.</summary>
    public static ToolMeta Current { get; } = new(
        VendoredSchemaVersion.Value,
        ServerIdentity.Version,
        ResolveWorkspaceRoot());

    /// <summary>
    /// <b>PROVISIONAL</b> (see <see cref="ToolMeta.WorkspaceRoot"/>): the process's resolved base
    /// directory, until Sprint 3's workspace model replaces it.
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
    /// not trim a root.
    /// <para>
    /// No secret-hygiene concern: this is a directory path this server already resolves in order to
    /// run at all, never any part of this process's ENVIRONMENT (which CLAUDE.md's secret-hygiene
    /// invariant forbids echoing into any tool result) and never a caller-supplied path.
    /// </para>
    /// </remarks>
    private static string ResolveWorkspaceRoot() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
}
