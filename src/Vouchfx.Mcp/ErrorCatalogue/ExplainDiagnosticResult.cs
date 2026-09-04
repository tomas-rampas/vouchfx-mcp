namespace Vouchfx.Mcp.ErrorCatalogue;

/// <summary>
/// <c>explain_diagnostic</c>'s success payload (US-S1-05) — a catalogued code's page, plus the same
/// <c>docsUrl</c> shape every <see cref="Contracts.VfxError"/>/<see cref="Contracts.Diagnostic"/>
/// already carries, so a host that just received one of those can hand its <c>code</c> straight to
/// this tool and land on the matching field here.
/// </summary>
/// <param name="Code">The catalogued code this result explains, e.g. <c>VFX-E-1002</c>.</param>
/// <param name="Title">A short, human-readable name for the code.</param>
/// <param name="Explanation">What the code means and, where useful, the mechanism that produces it.</param>
/// <param name="CommonCauses">Concrete, specific reasons this code fires.</param>
/// <param name="Fixes">Concrete remediation steps.</param>
/// <param name="DocsUrl">
/// This code's catalogue URL (<see cref="Contracts.VfxCodeCatalogue.DocsUrlFor"/>) — identical in
/// shape to <see cref="Contracts.VfxError.DocsUrl"/>/<see cref="Contracts.Diagnostic.DocsUrl"/>.
/// </param>
public sealed record ExplainDiagnosticResult(
    string Code,
    string Title,
    string Explanation,
    IReadOnlyList<string> CommonCauses,
    IReadOnlyList<string> Fixes,
    string DocsUrl);
