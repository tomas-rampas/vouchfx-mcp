using ModelContextProtocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;

namespace Vouchfx.Mcp.Resources;

/// <summary>
/// The single templated MCP resource this server advertises: <c>vouchfx-docs:///errors/{code}</c>
/// (US-S1-05) — one catalogue page per <see cref="VfxCodeCatalogue"/> code, served from the SAME
/// embedded bytes <see cref="Vouchfx.Mcp.Tools.ExplainDiagnosticTool"/> parses (see
/// <see cref="DiagnosticPageRepository"/>'s own remarks: "one file, two access paths").
/// </summary>
/// <remarks>
/// A TEMPLATED resource (a <c>{code}</c> placeholder in its <c>UriTemplate</c>), unlike
/// <see cref="DocResourceRegistry"/>'s two static ones — appropriate here because there is one
/// catalogue page per code rather than a small, fixed set of documents, and a template lets a host
/// resolve any of them from the code alone without this server having to enumerate every one of them
/// as a separate static resource. Templated resources are advertised via
/// <c>resources/templates/list</c>, not <c>resources/list</c> — <c>DocResourceRegistry</c>'s two
/// resources are unaffected by this one existing alongside them.
/// </remarks>
public static class DiagnosticResourceRegistry
{
    /// <summary>
    /// The RFC 6570 URI template every catalogue page resolves through — the shape US-S1-05's
    /// acceptance criteria name explicitly.
    /// </summary>
    public const string UriTemplate = "vouchfx-docs:///errors/{code}";

    /// <summary>Creates the templated diagnostic-catalogue resource.</summary>
    public static McpServerResource Create() =>
        McpServerResource.Create(
            (string code) => GetPageText(code),
            new McpServerResourceCreateOptions
            {
                UriTemplate = UriTemplate,
                Name = "vouchfx diagnostic catalogue page",
                Description =
                    "One page per vouchfx-mcp diagnostic/error code (VFX-D-#### / VFX-E-####): title, " +
                    "explanation, common causes, and fixes, in Markdown. The same content " +
                    "explain_diagnostic returns, addressable by code.",
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
        try
        {
            return DiagnosticPageRepository.GetRawText(code);
        }
        catch (InvalidOperationException)
        {
            // The caller-supplied code is untrusted input (M1): sanitised before it reaches an
            // exception message a client may surface to a user.
            throw new McpException($"Unknown diagnostic code '{VfxCode.SanitiseForEcho(code)}'.");
        }
    }
}
