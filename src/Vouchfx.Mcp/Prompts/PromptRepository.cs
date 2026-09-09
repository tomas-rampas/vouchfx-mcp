using System.Text;

namespace Vouchfx.Mcp.Prompts;

/// <summary>
/// The single place that reads and parses every catalogued prompt — always from the embedded assembly
/// resource, NEVER from disk — mirroring <see cref="Vouchfx.Mcp.Docs.VendoredDocRepository"/>,
/// <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository"/> and
/// <see cref="Vouchfx.Mcp.Examples.ExampleSuiteRepository"/> exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loaded and PARSED eagerly at first touch</b>, like all three of those. A prompt that is missing,
/// misnamed, has malformed front matter, or declares a name that disagrees with its catalogue entry
/// fails HERE — at server startup and in every test that touches this type — rather than lazily, the
/// first time a host happens to invoke that one prompt. A packaging fault should behave like one.
/// </para>
/// <para>
/// Eager parsing is also what makes <see cref="PromptDocumentParser"/>'s use of YamlDotNet safe to
/// reason about: every prompt is parsed before the server accepts a single request, so no parse ever
/// happens on a request thread and no caller can influence what is parsed. See that type's remarks.
/// </para>
/// </remarks>
public static class PromptRepository
{
    private static readonly Dictionary<string, PromptDefinition> DefinitionsByName = LoadAll();

    /// <summary>Every parsed prompt, in <see cref="PromptCatalogue.All"/>'s order.</summary>
    public static IReadOnlyList<PromptDefinition> All { get; } =
        [.. PromptCatalogue.All.Select(descriptor => DefinitionsByName[descriptor.Name])];

    /// <summary>The parsed prompt named <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> is not a catalogued prompt.</exception>
    public static PromptDefinition Get(string name) =>
        TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Unknown prompt '{name}'.");

    /// <summary>
    /// Looks up <paramref name="name"/> without throwing — the lookup the MCP surface uses, since an
    /// unrecognised name arriving there is untrusted caller input, never a programming error inside
    /// this assembly.
    /// </summary>
    public static bool TryGet(string? name, out PromptDefinition definition)
    {
        if (name is null)
        {
            definition = null!;
            return false;
        }

        return DefinitionsByName.TryGetValue(name, out definition!);
    }

    private static Dictionary<string, PromptDefinition> LoadAll()
    {
        var assembly = typeof(PromptRepository).Assembly;
        var result = new Dictionary<string, PromptDefinition>(StringComparer.Ordinal);

        foreach (var descriptor in PromptCatalogue.All)
        {
            using var stream = assembly.GetManifestResourceStream(descriptor.EmbeddedResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{descriptor.EmbeddedResourceName}' was not found in "
                    + $"'{assembly.FullName}' — every PromptCatalogue.All entry needs a "
                    + $"src/Vouchfx.Mcp/Prompts/{descriptor.FileName} file embedded under this exact "
                    + "logical name.");

            // detectEncodingFromByteOrderMarks: a BOM added by an editor would otherwise sit in front
            // of the opening '---' fence and make the front-matter split fail on a file that looks
            // perfectly correct in every editor — the same defensive decode
            // VendoredComposedSchema and ExampleSuiteRepository apply, for the same reason.
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var definition = PromptDocumentParser.Parse(descriptor.FileName, reader.ReadToEnd());

            // The catalogue's name and the file's own front-matter name must agree. Without this the
            // catalogue could advertise `heal_run` while serving `author_scenario`'s text — a
            // copy-paste mistake that every other check in this repository would pass, exactly as
            // DiagnosticPageRepository guards its pages' H1 against the code they are filed under.
            if (!string.Equals(definition.Name, descriptor.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Prompt file '{descriptor.FileName}' declares name '{definition.Name}', but the "
                    + $"catalogue files it under '{descriptor.Name}'.");
            }

            result[descriptor.Name] = definition;
        }

        return result;
    }
}
