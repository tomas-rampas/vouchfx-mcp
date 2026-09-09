using System.Text;

namespace Vouchfx.Mcp.Docs;

/// <summary>
/// Reads the embedded <c>docs/dsl-guide-for-agents.md</c> once and hands it out as raw Markdown —
/// what <c>vouchfx://docs/dsl-guide</c> serves (Sprint 5 / US-S5-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Embedded, never read from disk</b>, following the discipline every other served document in
/// this assembly uses (<see cref="VendoredDocRepository"/>,
/// <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository"/>,
/// <see cref="Vouchfx.Mcp.Examples.ExampleSuiteRepository"/>): a manifest resource resolves
/// identically from a source checkout and from the installed <c>Vouchfx.Mcp</c> dotnet tool, whereas
/// a loose-file read needs a base-directory guess that is wrong in one of those two cases.
/// </para>
/// <para>
/// <b>Distinct from <see cref="VendoredDocuments"/>, deliberately.</b> Those two documents are
/// byte-exact copies of the pinned engine commit and are drift-gated by
/// <c>scripts/sync-vendored.ps1</c>; this one is this repository's OWN writing and must never be
/// swept into that gate. Keeping it in its own type is what stops a future "load all the served
/// markdown from one place" refactor quietly putting a repo-authored file under a gate that would
/// then fail every engine pin bump.
/// </para>
/// <para>
/// Read EAGERLY at first touch, like its siblings: a missing or misnamed embed is a packaging fault
/// and should fail loudly at startup rather than on whichever resource read happens to be first.
/// </para>
/// </remarks>
public static class DslGuideDocument
{
    /// <summary>The manifest resource name the csproj pins via <c>LogicalName</c>.</summary>
    private const string ResourceName = "Vouchfx.Mcp.Docs.dsl-guide-for-agents.md";

    /// <summary>The guide's full, verbatim Markdown text.</summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing.</exception>
    public static string RawMarkdown { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(DslGuideDocument).Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' was not found in '{assembly.FullName}' — "
                + "docs/dsl-guide-for-agents.md must be embedded under this exact logical name.");

        // BOM detection on, for VendoredComposedSchema's reason: an editor-added byte-order mark would
        // otherwise sit at the head of the served text and reach a host as a stray U+FEFF.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
