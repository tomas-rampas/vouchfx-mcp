namespace Vouchfx.Mcp.Docs;

/// <summary>
/// The single place that reads the vendored documents' raw Markdown text — always from the
/// embedded assembly resource, NEVER from disk — and parses it into <see cref="DocSection"/>s once,
/// for both <see cref="Vouchfx.Mcp.Resources.DocResourceRegistry"/> (which serves the raw text
/// verbatim) and <see cref="DocSearchService"/> (which searches the parsed sections) to share.
/// </summary>
public static class VendoredDocRepository
{
    private static readonly Dictionary<string, string> RawTextByDocId = LoadRawText();

    /// <summary>Every section of every vendored document, in document-then-heading order.</summary>
    public static IReadOnlyList<DocSection> AllSections { get; } = ParseAllSections();

    /// <summary>The full, verbatim Markdown text of the vendored document identified by <paramref name="docId"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="docId"/> does not name a known document.</exception>
    public static string GetRawText(string docId) =>
        RawTextByDocId.TryGetValue(docId, out var text)
            ? text
            : throw new InvalidOperationException($"Unknown vendored document id '{docId}'.");

    private static Dictionary<string, string> LoadRawText()
    {
        var assembly = typeof(VendoredDocRepository).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var doc in VendoredDocuments.All)
        {
            using var stream = assembly.GetManifestResourceStream(doc.EmbeddedResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{doc.EmbeddedResourceName}' was not found in '{assembly.FullName}'.");
            using var reader = new StreamReader(stream);
            result[doc.Id] = reader.ReadToEnd();
        }

        return result;
    }

    private static List<DocSection> ParseAllSections()
    {
        var sections = new List<DocSection>();
        foreach (var doc in VendoredDocuments.All)
        {
            sections.AddRange(MarkdownDocumentParser.Parse(doc.Id, RawTextByDocId[doc.Id]));
        }

        return sections;
    }
}
