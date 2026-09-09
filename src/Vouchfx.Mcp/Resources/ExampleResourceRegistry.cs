using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Examples;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// <c>vouchfx://examples/{name}</c> (Sprint 5 / US-S5-01 AC-004) — one complete,
/// comment-annotated sample <c>.e2e.yaml</c> per <see cref="ExampleSuites.All"/> entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>A TEMPLATE over a three-entry set, rather than three static resources</b>, unlike
/// <see cref="DocResourceRegistry"/>'s treatment of the two vendored documents. The reason is that
/// the set is expected to GROW — every future authoring pattern worth teaching is another example —
/// whereas the vendored document pair is fixed by what the engine repo publishes. A template means
/// adding a fourth example is a catalogue entry plus a csproj item, with nothing to remember here;
/// three static resources would make it a fourth registration a reviewer has to notice is missing.
/// The names are discoverable from this template's own description, which enumerates them.
/// </para>
/// <para>
/// <b>Comments are the payload, so the text is served VERBATIM.</b> The bytes a host receives are
/// the bytes in <c>examples/&lt;name&gt;.e2e.yaml</c>, comments and all — nothing is normalised,
/// re-emitted, or round-tripped through a YAML parser on the way out. That matters more here than
/// for any other resource this server serves: <c>normalize_suite</c>'s own measured finding is that
/// the pinned YamlDotNet DROPS comments on a round trip (see <c>SuiteNormalizer</c>'s remarks), so a
/// parse-and-re-emit here would silently discard the annotation that is the whole point of shipping
/// these files.
/// </para>
/// </remarks>
public static class ExampleResourceRegistry
{
    /// <summary>Creates the templated example-suite resource.</summary>
    public static McpServerResource Create() =>
        McpServerResource.Create(
            (string name) => Resolve(name),
            new McpServerResourceCreateOptions
            {
                UriTemplate = VouchfxResourceUris.ExampleTemplate,
                Name = "vouchfx example suite",
                Description = BuildDescription(),
                MimeType = ResourceJson.YamlMimeType,
            });

    /// <summary>
    /// Resolves <paramref name="name"/> to its example's verbatim YAML, or throws for a name this
    /// server does not carry.
    /// </summary>
    internal static string Resolve(string? name)
    {
        var requested = ResourceArgumentGuard.Require(name, "name");

        if (ExampleSuites.TryFind(requested) is not { } example)
        {
            // Names the available set rather than merely refusing: there are three of them, a host
            // that guessed wrong can recover from this sentence alone, and the alternative (call
            // resources/templates/list and read a description) is a round trip for a fact that fits
            // in the error. The same reasoning DiagnosticResourceRegistry does NOT apply to its own
            // unknown-code message, because there the set is ~50 codes and listing them would be
            // noise rather than help.
            throw new McpException(
                $"No example suite named '{VfxCode.SanitiseForEcho(requested)}'. Available: "
                + $"{string.Join(", ", ExampleSuites.All.Select(candidate => candidate.Name))}.");
        }

        return ExampleSuiteRepository.GetRawText(example.Name);
    }

    /// <summary>
    /// The template's description, with the available names enumerated FROM THE CATALOGUE rather
    /// than typed out — so adding a fourth example cannot leave the advertised list stale.
    /// </summary>
    private static string BuildDescription() =>
        "A complete, runnable-shaped, comment-annotated vouchfx .e2e.yaml suite — a whole document to "
        + "start from rather than fields to assemble. Every example is validated against the vendored "
        + "composed schema in this repository's own tests, so it cannot drift from what validate_suite "
        + "accepts, and none contains a literal secret. Available names: "
        + string.Join(
            "; ",
            ExampleSuites.All.Select(example => $"'{example.Name}' — {example.Title}"))
        + ". Note the container images are placeholders: substitute your own before running one.";
}
