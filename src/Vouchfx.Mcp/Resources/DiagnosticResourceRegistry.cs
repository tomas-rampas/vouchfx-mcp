using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// The diagnostic-catalogue resource family — one catalogue page per <see cref="VfxCodeCatalogue"/>
/// code, served from the SAME embedded bytes <see cref="Vouchfx.Mcp.Tools.ExplainDiagnosticTool"/>
/// parses (see <see cref="DiagnosticPageRepository"/>'s own remarks: "one file, two access paths"),
/// under TWO URI templates: US-S1-05's original <c>vouchfx-docs:///errors/{code}</c> and Sprint 5's
/// <c>vouchfx://docs/errors/{code}</c> alias.
/// </summary>
/// <remarks>
/// <para>
/// A TEMPLATED resource (a <c>{code}</c> placeholder in its <c>UriTemplate</c>), unlike
/// <see cref="DocResourceRegistry"/>'s static ones — appropriate here because there is one
/// catalogue page per code rather than a small, fixed set of documents, and a template lets a host
/// resolve any of them from the code alone without this server having to enumerate every one of them
/// as a separate static resource. Templated resources are advertised via
/// <c>resources/templates/list</c>, not <c>resources/list</c> — <c>DocResourceRegistry</c>'s
/// resources are unaffected by these existing alongside them.
/// </para>
/// <para>
/// <b>Why the Sprint 5 alias is an ALIAS and not a migration</b> (US-S5-01 AC-003; plan D4). Every
/// other resource this sprint adds lives under <c>vouchfx://</c>, and a host discovering the
/// template list would otherwise find the errors family alone wearing a different scheme — a
/// gratuitous inconsistency in a surface whose whole purpose is discoverability. But the Sprint 1
/// URI is PUBLISHED: hosts cache by URI, <c>docs/tools-and-resources.md</c> documents it, and D4
/// states the codebase's existing URIs win over the fleet spec's. So both are advertised, both
/// resolve, and — because <see cref="Create"/> and <see cref="CreateAlias"/> differ in nothing but
/// their <c>UriTemplate</c>, calling the same <see cref="GetPageText"/> — neither can serve content
/// the other does not. There is no primary and no deprecated form.
/// </para>
/// </remarks>
public static class DiagnosticResourceRegistry
{
    /// <summary>
    /// The RFC 6570 URI template every catalogue page resolves through — the shape US-S1-05's
    /// acceptance criteria name explicitly. <b>Never renamed</b>; see this type's remarks.
    /// </summary>
    public const string UriTemplate = "vouchfx-docs:///errors/{code}";

    /// <summary>
    /// Sprint 5's <c>vouchfx://</c>-scheme alias for <see cref="UriTemplate"/> — same pages, same
    /// bytes, additive.
    /// </summary>
    public const string AliasUriTemplate = VouchfxResourceUris.ErrorPageTemplate;

    private const string ResourceName = "vouchfx diagnostic catalogue page";

    private const string ResourceDescription =
        "One page per vouchfx-mcp diagnostic/error code (VFX-D-#### / VFX-E-####): title, " +
        "explanation, common causes, and fixes, in Markdown. The same content " +
        "explain_diagnostic returns, addressable by code.";

    /// <summary>Creates the templated diagnostic-catalogue resource under <see cref="UriTemplate"/>.</summary>
    public static McpServerResource Create() => CreateUnder(UriTemplate);

    /// <summary>
    /// Creates the SAME resource under <see cref="AliasUriTemplate"/>. Identical content by
    /// construction — the two factories share every field but the template.
    /// </summary>
    public static McpServerResource CreateAlias() => CreateUnder(AliasUriTemplate);

    private static McpServerResource CreateUnder(string uriTemplate) =>
        McpServerResource.Create(
            (string code) => GetPageText(code),
            new McpServerResourceCreateOptions
            {
                UriTemplate = uriTemplate,
                Name = ResourceName,
                Description = ResourceDescription,
                MimeType = "text/markdown",
            });

    /// <summary>
    /// Resolves <paramref name="code"/>'s catalogue page text, or throws <see cref="McpException"/>
    /// for an unrecognised code — the SDK's own documented pattern for a template resource whose
    /// parameter does not resolve (see the C# SDK's <c>docs://articles/{id}</c> worked example),
    /// which the client sees as a clean <c>resources/read</c> protocol error rather than a crash.
    /// </summary>
    private static string GetPageText(string code)
    {
        // Routed through the SHARED template-argument guard (a security review's finding). This
        // resource was the one family that bypassed it, which made ResourceArgumentGuard's own claim
        // — "the one gate every vouchfx:// template argument passes through" — false, and left the two
        // errors URIs applying different admission rules to the same {code}. Nothing about the
        // resolution below changed: a code that is not in the catalogue was already refused. What the
        // guard adds is that a blank, absurdly long, path-shaped or UNC-shaped {code} is refused for
        // WHAT IT IS rather than incidentally, by the same sentence every other template uses, and
        // before it reaches a dictionary keyed on caller-supplied text.
        var requested = ResourceArgumentGuard.Require(code, "code");

        try
        {
            return DiagnosticPageRepository.GetRawText(requested);
        }
        catch (InvalidOperationException)
        {
            // The caller-supplied code is untrusted input (M1): sanitised before it reaches an
            // exception message a client may surface to a user.
            throw new McpException($"Unknown diagnostic code '{VfxCode.SanitiseForEcho(requested)}'.");
        }
    }
}
