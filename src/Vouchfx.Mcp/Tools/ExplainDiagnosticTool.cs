using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.ErrorCatalogue;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>explain_diagnostic</c> tool (US-S1-05): looks up one catalogued <c>VFX-*-####</c> code
/// (<see cref="VfxCodeCatalogue"/>) and returns its <c>docs/errors/&lt;CODE&gt;.md</c> catalogue page —
/// title, explanation, common causes, and fixes — as a structured result, without spawning anything.
/// </summary>
/// <remarks>
/// CLI-free by design, like <see cref="ValidateSuiteTool"/>/<see cref="SearchDocsTool"/>: the page
/// content comes from the embedded <see cref="DiagnosticPageRepository"/>, never a subprocess and
/// never this process's environment. The SAME embedded bytes back
/// <c>vouchfx-docs:///errors/{code}</c> (<see cref="Vouchfx.Mcp.Resources.DiagnosticResourceRegistry"/>)
/// — one file, two access paths, so the two can never disagree about what a code means.
/// </remarks>
internal static class ExplainDiagnosticTool
{
    public const string Name = "explain_diagnostic";

    private const string Description =
        "Explains one vouchfx-mcp diagnostic/error code (VFX-D-#### or VFX-E-####) — the codes " +
        "carried on every VfxError and Diagnostic this server returns. Give it a code exactly as " +
        "seen in a result's 'code' field (e.g. 'VFX-E-1002') and it returns that code's title, a " +
        "plain-language explanation, common causes, and concrete fixes — the same content served " +
        "as the vouchfx-docs:///errors/{code} resource. Never spawns the engine CLI; works fully " +
        "offline. An unknown/uncatalogued code returns a tool error rather than crashing.";

    public static McpServerTool Create() => McpServerTool.Create(Handle, new McpServerToolCreateOptions
    {
        Name = Name,
        Description = Description,
        ReadOnly = true,
    });

    private static CallToolResult Handle(
        [Description("The VFX-D-#### / VFX-E-#### code to explain, exactly as returned on a result, e.g. 'VFX-E-1002'.")]
        string code,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticPageRepository.TryGetPage(code, out var page))
        {
            // The caller-supplied code is untrusted input (M1): sanitised before it is echoed into
            // the error message, mirroring DescribeStepTypeTool's identical treatment of an unknown
            // step type.
            return StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.UnknownDiagnosticCode,
                $"Unknown diagnostic code '{VfxCode.SanitiseForEcho(code)}'. Codes must match a VFX-D-#### "
                + "or VFX-E-#### value this server has catalogued."));
        }

        return StructuredToolResult.Success(new ExplainDiagnosticResult(
            page.Code,
            page.Title,
            page.Explanation,
            page.CommonCauses,
            page.Fixes,
            VfxCodeCatalogue.DocsUrlFor(page.Code)));
    }
}
