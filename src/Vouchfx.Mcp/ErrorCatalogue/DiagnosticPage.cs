namespace Vouchfx.Mcp.ErrorCatalogue;

// Vouchfx.Mcp.ErrorCatalogue — DiagnosticPage (Sprint 1 / US-S1-05).
//
// The parsed shape of one docs/errors/VFX-*.md catalogue page — one per Contracts/VfxCodeCatalogue.All
// entry. See DiagnosticPageParser for the fixed heading structure this is parsed from, and
// DiagnosticPageRepository for where the raw Markdown text actually comes from (the embedded
// manifest resource, never disk — same discipline as Docs/VendoredDocRepository).

/// <summary>
/// One parsed <c>docs/errors/&lt;CODE&gt;.md</c> catalogue page: the deterministic, structured answer
/// <c>explain_diagnostic</c> returns for a catalogued <c>VFX-*-####</c> code.
/// </summary>
/// <param name="Code">
/// The code this page documents (its own <c># </c> H1), e.g. <c>VFX-E-1002</c>. Always equal to the
/// <see cref="Contracts.VfxCodeEntry.Code"/> of the catalogue entry this page belongs to —
/// <see cref="DiagnosticPageRepository"/> asserts that at load time, so a page filed under the wrong
/// name fails fast rather than silently answering for a different code.
/// </param>
/// <param name="Title">The page's <c>## Title</c> section: a short, human-readable name for the code.</param>
/// <param name="Explanation">
/// The page's <c>## Explanation</c> section: what the code means and, where useful, the concrete
/// mechanism that produces it (a timeout value, a size cap, an emitting call site) — one or more
/// paragraphs, verbatim.
/// </param>
/// <param name="CommonCauses">
/// The page's <c>## Common causes</c> section, one entry per <c>- </c> bullet — concrete, specific
/// reasons this code fires, not boilerplate.
/// </param>
/// <param name="Fixes">
/// The page's <c>## Fixes</c> section, one entry per <c>- </c> bullet — concrete remediation steps.
/// </param>
public sealed record DiagnosticPage(
    string Code,
    string Title,
    string Explanation,
    IReadOnlyList<string> CommonCauses,
    IReadOnlyList<string> Fixes);
