using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Resolves a JSON Pointer (RFC 6901) — as reported by a JSON Schema evaluation's
/// <c>InstanceLocation</c> — back to the 1-based YAML source line it addresses.
/// </summary>
/// <remarks>
/// Mirrors the vouchfx engine's own <c>Vouchfx.Engine.Compilation.Schema.DocumentValidator</c>
/// pointer-to-line resolver: the pointer is walked segment by segment against a
/// <see cref="YamlStream"/> parse of the same document, addressing each mapping key or sequence
/// index in turn. Resolution is <b>best-effort</b> — an unparseable document, a root-level
/// pointer, an absent mapping key, or an out-of-range sequence index all resolve to
/// <see langword="null"/> rather than throwing.
/// </remarks>
internal static class YamlLineResolver
{
    /// <summary>
    /// Resolves <paramref name="jsonPointer"/> against <paramref name="yamlText"/> to a 1-based
    /// source line number, or <see langword="null"/> when it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Parses <paramref name="yamlText"/> on every call. Use <see cref="TryParseYamlRoot"/> once and
    /// the <see cref="ResolveLine(YamlMappingNode?, string)"/> overload when resolving more than one
    /// pointer against the same document — see that overload for why it is not a micro-optimisation.
    /// </remarks>
    public static long? ResolveLine(string yamlText, string jsonPointer) =>
        ResolveLine(TryParseYamlRoot(yamlText), jsonPointer);

    /// <summary>
    /// Resolves <paramref name="jsonPointer"/> against an already-parsed document root.
    /// </summary>
    /// <remarks>
    /// The reason this overload exists is a measured cliff, not tidiness. Engine <c>v1.0.0-rc.4</c>
    /// closed the step surface with <c>unevaluatedProperties: false</c>, so the number of schema
    /// errors a suite can produce now tracks the number of keys in it rather than being bounded by
    /// the schema's own shape. With a full <see cref="YamlStream"/> re-parse per error, a 33 KB
    /// suite carrying 2 000 unknown keys took ~14 s to validate — against
    /// <c>ValidationWorkerClient</c>'s 10 s budget, and on an input 150× below
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/>. Parsing once takes the same document to well
    /// under a second.
    /// </remarks>
    public static long? ResolveLine(YamlMappingNode? root, string jsonPointer) =>
        root is null ? null : ResolveLineFromPointer(root, jsonPointer);

    /// <summary>
    /// Parses <paramref name="yamlText"/> into a document root suitable for repeated pointer
    /// resolution, or <see langword="null"/> when it cannot be parsed as a mapping.
    /// </summary>
    public static YamlMappingNode? TryParseYamlRoot(string yamlText)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yamlText);
            stream.Load(reader);

            return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch
        {
            // Any YamlDotNet parse exception means line resolution is unavailable here; the
            // caller already has (or will separately report) the parse failure itself.
            return null;
        }
    }

    private static long? ResolveLineFromPointer(YamlMappingNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return null;
        }

        var segments = pointer.Split('/');
        YamlNode current = root;

        // segments[0] is always "" (from the leading slash); walk from index 1.
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = DecodePointerSegment(segments[i]);

            switch (current)
            {
                case YamlMappingNode mapping:
                    var key = new YamlScalarNode(segment);
                    if (!mapping.Children.TryGetValue(key, out var child))
                    {
                        return null;
                    }

                    current = child;
                    break;

                case YamlSequenceNode sequence:
                    if (!int.TryParse(segment, out var index) || index < 0 || index >= sequence.Children.Count)
                    {
                        return null;
                    }

                    current = sequence.Children[index];
                    break;

                default:
                    // Hit a scalar or alias before exhausting all pointer segments.
                    return null;
            }
        }

        return current.Start.Line;
    }

    /// <summary>Decodes one JSON Pointer segment per RFC 6901: <c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>.</summary>
    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
               .Replace("~0", "~", StringComparison.Ordinal);
}
