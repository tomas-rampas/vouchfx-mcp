using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.ErrorCatalogue;

/// <summary>
/// The single place that reads every <c>docs/errors/&lt;CODE&gt;.md</c> catalogue page's raw Markdown
/// text — always from the embedded assembly resource, NEVER from disk — and parses it once, for both
/// <see cref="Vouchfx.Mcp.Tools.ExplainDiagnosticTool"/> (which returns the parsed
/// <see cref="DiagnosticPage"/>) and <see cref="Vouchfx.Mcp.Resources.DiagnosticResourceRegistry"/>
/// (which serves the raw text verbatim under <c>vouchfx-docs:///errors/{code}</c>) to share — the
/// same single-source-of-truth shape <see cref="Vouchfx.Mcp.Docs.VendoredDocRepository"/> already
/// established for the two vendored engine documents (US-S1-05 acceptance criterion 3: "one file, two
/// access paths").
/// </summary>
/// <remarks>
/// One page per <see cref="VfxCodeCatalogue.All"/> entry, loaded and parsed EAGERLY at first touch of
/// this type: a page that is missing, misnamed, or malformed fails here — at server startup, in every
/// test that touches this type, and in <c>DiagnosticPageRepositoryTests</c>' own completeness guard —
/// rather than lazily, the first time an agent happens to ask <c>explain_diagnostic</c> about that one
/// code. US-S1-06's coming bidirectional gate (every emitted code has a page, every page has an
/// emitter) builds on this same "every catalogue code resolves to a page" invariant already holding.
/// </remarks>
public static class DiagnosticPageRepository
{
    /// <summary>
    /// Builds the embedded manifest resource name for <paramref name="code"/>'s catalogue page,
    /// following the <c>Vouchfx.Mcp.Vendored.*</c> convention <c>Vouchfx.Mcp.csproj</c> already uses
    /// for the two vendored documents — see that file's own <c>EmbeddedResource</c> items.
    /// </summary>
    public static string EmbeddedResourceNameFor(string code) => $"Vouchfx.Mcp.Errors.{code}.md";

    private static readonly Dictionary<string, string> RawTextByCode = LoadRawText();

    /// <summary>Every catalogued code's parsed page, keyed by code.</summary>
    public static IReadOnlyDictionary<string, DiagnosticPage> AllByCode { get; } = ParseAll();

    /// <summary>
    /// The full, verbatim Markdown text of <paramref name="code"/>'s catalogue page — what
    /// <c>vouchfx-docs:///errors/{code}</c> serves.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="code"/> has no catalogue page.</exception>
    public static string GetRawText(string code) =>
        RawTextByCode.TryGetValue(code, out var text)
            ? text
            : throw new InvalidOperationException($"No diagnostic catalogue page for code '{code}'.");

    /// <summary>
    /// Looks up <paramref name="code"/>'s parsed page without throwing — the lookup
    /// <see cref="Vouchfx.Mcp.Tools.ExplainDiagnosticTool"/> uses, since an unrecognised code arriving
    /// here is untrusted caller input (EDGE case), never a programming error inside this assembly.
    /// </summary>
    public static bool TryGetPage(string? code, out DiagnosticPage page)
    {
        if (code is null)
        {
            page = null!;
            return false;
        }

        return AllByCode.TryGetValue(code, out page!);
    }

    private static Dictionary<string, string> LoadRawText()
    {
        var assembly = typeof(DiagnosticPageRepository).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in VfxCodeCatalogue.All)
        {
            var resourceName = EmbeddedResourceNameFor(entry.Code);
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' was not found in '{assembly.FullName}' — "
                    + $"every VfxCodeCatalogue entry needs a docs/errors/{entry.Code}.md page embedded "
                    + "under this exact logical name.");
            using var reader = new StreamReader(stream);
            result[entry.Code] = reader.ReadToEnd();
        }

        return result;
    }

    private static Dictionary<string, DiagnosticPage> ParseAll()
    {
        var pages = new Dictionary<string, DiagnosticPage>(StringComparer.Ordinal);

        foreach (var entry in VfxCodeCatalogue.All)
        {
            var page = DiagnosticPageParser.Parse(RawTextByCode[entry.Code]);

            // The page's own H1 must name the SAME code it is filed under — catches a page that was
            // copy-pasted from a neighbour and had only its filename updated, which would otherwise
            // silently serve the wrong title/explanation/causes/fixes for the code it claims to be.
            if (!string.Equals(page.Code, entry.Code, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"docs/errors/{entry.Code}.md's own '# ' heading reads '{page.Code}', not '{entry.Code}'.");
            }

            pages[entry.Code] = page;
        }

        return pages;
    }
}
