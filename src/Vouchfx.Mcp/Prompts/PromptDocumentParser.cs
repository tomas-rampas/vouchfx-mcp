using System.Text.Json;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Prompts;

/// <summary>
/// Splits a <c>Prompts/*.md</c> file into its YAML front matter and its template body, and turns the
/// former into a <see cref="PromptDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why YamlDotNet is used HERE when US-S5-01 built a whole child process to avoid it.</b> The
/// hazard that forced <c>vouchfx://workspace/specs</c> out of process is real and unchanged — the
/// Scanner can be driven into an uninterruptible spin by a twelve-byte input — but it is a hazard
/// about UNTRUSTED YAML. These files are not untrusted: they are this repository's own, embedded into
/// the assembly at build time, fixed for the lifetime of a release, and parsed by
/// <c>PromptCatalogueTests</c> on every build. A host cannot supply one, cannot edit one, and cannot
/// reach this parser at all — <see cref="PromptRepository"/> loads every prompt eagerly at type
/// initialisation, before any request is served. The distinction that matters is not "is this YAML"
/// but "can a caller choose the bytes", and here they cannot.
/// </para>
/// <para>
/// Routed through <see cref="YamlToJsonConverter"/> rather than a second YamlDotNet configuration, so
/// this assembly keeps ONE YAML reader with one set of settings. A malformed front matter therefore
/// fails at server start with the same class of error a malformed vendored artefact does — loudly,
/// once, as the packaging fault it is.
/// </para>
/// <para>
/// <b>A hand-rolled front-matter parser was considered and rejected.</b> The declaration is nested
/// (a list of argument objects), and hand-rolling nested key/value parsing is how a parser acquires
/// its own quoting and indentation bugs — in a file whose contents reach a host's context window. One
/// well-tested reader beats two.
/// </para>
/// </remarks>
public static class PromptDocumentParser
{
    /// <summary>The fence that opens and closes front matter, on its own line.</summary>
    private const string Fence = "---";

    /// <summary>
    /// Parses one prompt document.
    /// </summary>
    /// <param name="sourceName">The file's name, for error messages only.</param>
    /// <param name="text">The whole file, front matter included.</param>
    /// <exception cref="InvalidOperationException">
    /// The document has no front matter, its front matter is not a YAML mapping, or a required field
    /// is missing or of the wrong shape. Always a packaging fault; see this type's remarks.
    /// </exception>
    public static PromptDefinition Parse(string sourceName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(text);

        var (frontMatter, body) = Split(sourceName, text);

        JsonDocument document;
        try
        {
            document = YamlToJsonConverter.Convert(frontMatter);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' has front matter that is not valid YAML.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Prompt '{sourceName}' front matter must be a mapping of fields.");
            }

            var arguments = ReadArguments(sourceName, root);
            AssertDeclarationMatchesBody(sourceName, arguments, body);

            return new PromptDefinition(
                Name: RequiredString(sourceName, root, "name"),
                Title: RequiredString(sourceName, root, "title"),
                Description: RequiredString(sourceName, root, "description"),
                Arguments: arguments,
                Body: body);
        }
    }

    /// <summary>
    /// Every argument name the body REFERENCES — as a <c>{{name}}</c> substitution or a
    /// <c>{{#name}}</c>/<c>{{^name}}</c> section.
    /// </summary>
    public static IReadOnlyCollection<string> ReferencedArgumentNames(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return [.. PlaceholderPattern.Matches(body).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A <c>{{name}}</c>, <c>{{#name}}</c> or <c>{{^name}}</c> reference — deliberately NOT matching
    /// the <c>{{/name}}</c> close, which carries no information the open does not.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\{\{[#^]?(?<name>[A-Za-z][A-Za-z0-9]*)\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Asserts the declared arguments and the body's placeholders are the SAME SET.
    /// </summary>
    /// <remarks>
    /// <b>The leg the "drift is impossible" claim did not cover</b> (a peer review's MAJOR). Building
    /// the wire declaration from the front matter makes signature-vs-declaration drift structurally
    /// impossible — but says nothing about the BODY. A prompt declaring <c>specPath</c> whose body
    /// writes <c>{{specpath}}</c> renders a silently empty paragraph: the argument is advertised, the
    /// host supplies it, and the procedure it produces has a hole in it with nothing anywhere failing.
    /// <para>
    /// Checked at PARSE time, so it is a startup failure naming the file — the same treatment every
    /// other packaging fault in this repository gets, and strictly stronger than a test, because a
    /// prompt added without one could not ship broken even briefly.
    /// </para>
    /// <para>
    /// Set EQUALITY, both directions: an undeclared placeholder always renders empty (it can never be
    /// supplied), and a declared-but-unused argument advertises something the procedure ignores. Both
    /// are bugs; neither is worth a special case.
    /// </para>
    /// </remarks>
    private static void AssertDeclarationMatchesBody(
        string sourceName, IReadOnlyList<PromptArgumentDefinition> arguments, string body)
    {
        var declared = arguments.Select(argument => argument.Name).ToHashSet(StringComparer.Ordinal);
        var referenced = ReferencedArgumentNames(body).ToHashSet(StringComparer.Ordinal);

        var undeclared = referenced.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' body references undeclared argument(s): "
                + $"{string.Join(", ", undeclared)}. They would always render empty.");
        }

        var unused = declared.Except(referenced, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (unused.Length > 0)
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' declares argument(s) its body never uses: "
                + $"{string.Join(", ", unused)}. They would be advertised and then ignored.");
        }
    }

    /// <summary>
    /// Separates the front matter from the body. The document MUST open with a fence — a prompt with
    /// no declaration has no arguments and no name, so there is nothing to fall back to.
    /// </summary>
    private static (string FrontMatter, string Body) Split(string sourceName, string text)
    {
        // Normalised so a CRLF checkout and an LF one parse identically — these files are edited by
        // hand on both, and a line-ending difference must never change what a host receives.
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (!normalised.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' must begin with a '{Fence}' front-matter fence on its own line.");
        }

        var closing = normalised.IndexOf("\n" + Fence + "\n", Fence.Length, StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' has an unterminated front-matter block.");
        }

        var frontMatter = normalised[(Fence.Length + 1)..(closing + 1)];
        var body = normalised[(closing + Fence.Length + 2)..].TrimStart('\n');

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException($"Prompt '{sourceName}' has no body after its front matter.");
        }

        return (frontMatter, body);
    }

    private static List<PromptArgumentDefinition> ReadArguments(string sourceName, JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out var arguments))
        {
            // A prompt with no arguments is legitimate (spec §7.3's `review_spec` has one, but a
            // future one may have none), so an absent list is empty rather than an error.
            return [];
        }

        if (arguments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' front matter's 'arguments' must be a list.");
        }

        var result = new List<PromptArgumentDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Prompt '{sourceName}' front matter has an 'arguments' entry that is not a mapping.");
            }

            var name = RequiredString(sourceName, argument, "name");
            if (!seen.Add(name))
            {
                // A duplicate would make the wire declaration ambiguous and the render silently
                // depend on ordering.
                throw new InvalidOperationException(
                    $"Prompt '{sourceName}' declares the argument '{name}' more than once.");
            }

            result.Add(new PromptArgumentDefinition(
                name,
                RequiredString(sourceName, argument, "description"),
                ReadRequiredFlag(sourceName, argument, name)));
        }

        return result;
    }

    /// <summary>
    /// Reads an argument's <c>required</c> flag, defaulting to <see langword="false"/> when absent.
    /// </summary>
    /// <remarks>
    /// <b>Optional is the default, deliberately.</b> An argument silently becoming REQUIRED because
    /// someone forgot a line would break every host already invoking the prompt without it; the
    /// reverse mistake only widens what is accepted. The safe default is the one whose failure mode is
    /// recoverable.
    /// </remarks>
    private static bool ReadRequiredFlag(string sourceName, JsonElement argument, string argumentName)
    {
        if (!argument.TryGetProperty("required", out var required))
        {
            return false;
        }

        return required.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"Prompt '{sourceName}' argument '{argumentName}' has a 'required' value that is not a boolean."),
        };
    }

    private static string RequiredString(string sourceName, JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text
            || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Prompt '{sourceName}' front matter is missing a non-empty '{field}' string.");
        }

        return text;
    }
}
