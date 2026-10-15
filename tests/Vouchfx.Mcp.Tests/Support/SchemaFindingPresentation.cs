using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Adapts only the pinned CLI's display surface. Production MCP diagnostics keep their keyword
/// prefixes; the CLI's human and --json reports omit those prefixes.
/// </summary>
internal static class SchemaFindingPresentation
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "additionalProperties", "unevaluatedProperties", "properties", "required", "enum", "const",
        "type", "pattern", "format", "minLength", "maxLength", "minItems", "maxItems",
        "minProperties", "maxProperties", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "multipleOf", "uniqueItems", "contains", "propertyNames",
        "not", "oneOf", "anyOf", "allOf",
    };

    internal sealed record Identity(string Kind, string Subject, long? Line);

    internal static List<(string Message, long? Line)> ExactPairs(
        IEnumerable<(string Message, long? Line)> findings) =>
        findings.Select(f => (Message: SplitPrefix(f.Message).Text, f.Line))
            .OrderBy(f => f.Message, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

    internal static List<Identity> SemanticPairs(
        IEnumerable<(string Message, long? Line)> findings, string yaml) =>
        findings.Select(f => Describe(f.Message, f.Line, yaml))
            .OrderBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Subject, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ToList();

    private static (string? Keyword, string Text) SplitPrefix(string message)
    {
        var close = message.IndexOf("] ", StringComparison.Ordinal);
        if (message.StartsWith('[') && close > 1 && Keywords.Contains(message[1..close]))
        {
            return (message[1..close], message[(close + 2)..]);
        }

        return (null, message);
    }

    private static Identity Describe(string message, long? line, string yaml)
    {
        var (keyword, text) = SplitPrefix(message);
        var unknown = Match(text, "^Unknown property '([^']+)'(?: on | in |$)");
        if (unknown.Success && keyword is null or "additionalProperties" or "unevaluatedProperties")
        {
            // The CLI does not expose which closed-object keyword produced this finding.
            return new Identity("unknown-property", unknown.Groups[1].Value, line);
        }

        var required = Match(text, @"^Required properties (\[.*?\]) are not present(?: |$)");
        if (required.Success && keyword is null or "required")
        {
            var names = JsonSerializer.Deserialize<string[]>(required.Groups[1].Value)
                ?? throw new InvalidOperationException("Required-property list was null.");
            return new Identity("required", JsonSerializer.Serialize(names.Order(StringComparer.Ordinal)), line);
        }

        var forbidden = Match(text, "^Property '([^']+)' (?:is not valid |cannot be combined )");
        if (forbidden.Success && keyword is null or "properties")
        {
            return new Identity("forbidden-property", forbidden.Groups[1].Value, line);
        }

        var container = Match(text, "^Dependency '[^']+'(?: \\(type '[^']+'\\))? declares '([^']+)', but ");
        if (container.Success && keyword is null or "properties")
        {
            return new Identity("forbidden-property", container.Groups[1].Value, line);
        }

        var enumeration = Match(text, "^Value '[^']*' is not one of the accepted values for '([^']+)': ");
        if (enumeration.Success && keyword is null or "enum")
        {
            return new Identity("enum", enumeration.Groups[1].Value, line);
        }

        if (keyword == "enum" && text == "Value should match one of the values specified by the enum")
        {
            // MCP's generic enum text omits the property; recover it from the same immutable YAML
            // line both validators received. The CLI independently names that property.
            return new Identity("enum", SourceProperty(yaml, line), line);
        }

        var propertyName = Match(text, "^Property name '([^']+)' begins with an engine-reserved bookkeeping prefix - ");
        if (propertyName.Success && keyword is null or "pattern")
        {
            return new Identity("pattern", propertyName.Groups[1].Value, line);
        }

        if (keyword == "pattern" && text == "The string value is not a match for the indicated regular expression")
        {
            return new Identity("pattern", SourceProperty(yaml, line), line);
        }

        var schemaVersion = Match(text, "^'[^']*' is not a language schema version this engine recognises - write '([^']+)', or omit the field\\.$");
        if (schemaVersion.Success && keyword is null or "const")
        {
            return new Identity("const", "schemaVersion:" + schemaVersion.Groups[1].Value, line);
        }

        var healthCheck = Match(text, "^Service '[^']+' declares 'healthCheck: \\{ type: [^ }]+ \\}', but .* or write 'healthCheck: \\{ type: ([^ }]+) \\}'\\.$");
        if (healthCheck.Success && keyword is null or "const")
        {
            return new Identity("const", "type:" + healthCheck.Groups[1].Value, line);
        }

        if (keyword == "const" && text.StartsWith("Expected ", StringComparison.Ordinal))
        {
            // JsonSchema.Net JSON-encodes the expected string, including its JSON quotes.
            var encoded = JsonSerializer.Deserialize<string>(text["Expected ".Length..])
                ?? throw new InvalidOperationException("Const expectation was null.");
            var expected = JsonSerializer.Deserialize<string>(encoded)
                ?? throw new InvalidOperationException("Const string expectation was null.");
            return new Identity("const", SourceProperty(yaml, line) + ":" + expected, line);
        }

        // No broad keyword-only or line-only fallback: an unrecognised wording change must fail.
        return new Identity("message", text, line);
    }

    private static string SourceProperty(string yaml, long? line)
    {
        var lines = yaml.ReplaceLineEndings("\n").Split('\n');
        if (line is not > 0 || line > lines.Length)
        {
            throw new InvalidOperationException("Finding has no usable source line.");
        }

        var property = Match(lines[(int)line.Value - 1], @"^\s*(?:-\s*)?([A-Za-z][A-Za-z0-9]*):");
        return property.Success
            ? property.Groups[1].Value
            : throw new InvalidOperationException("Finding source line has no unambiguous property.");
    }

    private static Match Match(string value, string pattern) =>
        Regex.Match(value, pattern, RegexOptions.CultureInvariant);
}
