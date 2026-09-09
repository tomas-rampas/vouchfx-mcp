using System.Text;

namespace Vouchfx.Mcp.Examples;

/// <summary>
/// The single place that reads each <see cref="ExampleSuites.All"/> entry's raw YAML text — always
/// from the embedded assembly resource, NEVER from disk — mirroring
/// <see cref="Vouchfx.Mcp.Docs.VendoredDocRepository"/> and
/// <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository"/> exactly.
/// </summary>
/// <remarks>
/// <para>
/// Loaded EAGERLY at first touch of this type, like both of those: an example that is missing,
/// misnamed, or not embedded fails here — at server startup and in every test that touches this type
/// — rather than lazily, the first time a host happens to read that one URI. A packaging mistake is
/// a build fault, and it should behave like one.
/// </para>
/// <para>
/// <b>Decoded with BOM detection on</b>, for
/// <see cref="Vouchfx.Mcp.Schema.VendoredComposedSchema"/>'s reason: these files are this
/// repository's own and carry no BOM today, but an editor that added one would otherwise leave a
/// U+FEFF at the head of the served string, which a host feeding it straight to a YAML parser would
/// see as a parse error in a document this repository's own tests call valid.
/// </para>
/// </remarks>
public static class ExampleSuiteRepository
{
    private static readonly Dictionary<string, string> RawTextByName = LoadRawText();

    /// <summary>The full, verbatim YAML text of the example named <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> does not name a known example.</exception>
    public static string GetRawText(string name) =>
        RawTextByName.TryGetValue(name, out var text)
            ? text
            : throw new InvalidOperationException($"Unknown example suite name '{name}'.");

    private static Dictionary<string, string> LoadRawText()
    {
        var assembly = typeof(ExampleSuiteRepository).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var example in ExampleSuites.All)
        {
            using var stream = assembly.GetManifestResourceStream(example.EmbeddedResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{example.EmbeddedResourceName}' was not found in "
                    + $"'{assembly.FullName}' — every ExampleSuites.All entry needs an "
                    + $"examples/{example.FileName} file embedded under this exact logical name.");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            result[example.Name] = reader.ReadToEnd();
        }

        return result;
    }
}
