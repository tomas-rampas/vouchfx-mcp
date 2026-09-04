using System.Globalization;

namespace Vouchfx.Mcp.Contracts;

// Vouchfx.Mcp.Contracts — VfxCode (Sprint 1 / US-S1-03, spec §4.4).
//
// Shared VFX-<kind>-#### code validation for VfxError.cs (VFX-E-####) and Diagnostic.cs
// (VFX-D-####). The reserved 100-wide numeric ranges below are copied verbatim from spec §4.4 and
// from sprint-01-contract-foundations.md's own scope section (which adopts them "as-is" — see that
// file for the per-range rationale, e.g. why validation-timeout lands in 1100-1199 and not
// 1400-1499/1500-1599). 1800-1899 is DELIBERATELY not in this list: the spec table itself has a gap
// there, and this sprint's acceptance criteria call it out by name as a range that must be rejected,
// not silently accepted because it sits between two reserved neighbours.
//
// This file exists purely so VfxError and Diagnostic enforce the identical range table one way,
// rather than each hand-rolling its own copy that could drift apart over time — both constructors
// are build-time/test-time guards against a future sprint accidentally minting e.g. VFX-E-2000.

/// <summary>
/// Validates a <c>VFX-&lt;kind&gt;-####</c> code against the reserved range table from spec §4.4.
/// Internal: this is a construction-time guard for <see cref="VfxError"/> and <see cref="Diagnostic"/>,
/// not part of this server's public contract surface.
/// </summary>
internal static class VfxCode
{
    /// <summary>The reserved 4-digit code number ranges (inclusive on both ends), in spec §4.4 order.</summary>
    private static readonly (int Start, int End)[] ReservedRanges =
    [
        (1000, 1099), // Workspace / path / config
        (1100, 1199), // Schema validation
        (1200, 1299), // Semantic validation
        (1300, 1399), // Compilation
        (1400, 1499), // Orchestration / environment
        (1500, 1599), // Execution / run lifecycle
        (1600, 1699), // Analysis (topology / impact)
        (1700, 1799), // Agent layer
        // 1800-1899 intentionally absent — not a reserved area (spec §4.4's own gap).
        (1900, 1999), // Internal / unexpected
    ];

    /// <summary>The reserved ranges rendered for an exception message, e.g. "1000-1099, 1100-1199, ...".</summary>
    private static readonly string ReservedRangesDescription =
        string.Join(", ", ReservedRanges.Select(r => $"{r.Start}-{r.End}"));

    /// <summary>How much of an echoed caller value <see cref="SanitiseForEcho"/> keeps — see that method's remarks.</summary>
    private const int MaxEchoLength = 64;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="code"/> is not exactly
    /// <paramref name="prefix"/> followed by 4 digits, or when its 4-digit number falls outside
    /// every reserved range (including the deliberately-unreserved 1800-1899 gap).
    /// </summary>
    /// <param name="code">The full code under validation, e.g. <c>"VFX-E-1001"</c>.</param>
    /// <param name="prefix">Either <c>"VFX-E-"</c> or <c>"VFX-D-"</c>, matching the caller's own kind.</param>
    /// <param name="paramName">The caller's constructor parameter name, echoed on the thrown exception.</param>
    public static void Validate(string code, string prefix, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(code, paramName);

        // Exactly `prefix` + 4 ASCII digits, no more, no fewer — int.TryParse alone would accept a
        // 2-digit or 6-digit tail as long as it parses, so the length check carries the "exactly
        // four digits" half of the pattern and TryParse (with NumberStyles.None: no sign, no
        // separators, no leading/trailing whitespace) carries the "digits only" half. The `if`
        // throws unconditionally on any failure, so `codeNumber` is definitely assigned below —
        // control only reaches past this guard when TryParse itself returned true.
        if (!code.StartsWith(prefix, StringComparison.Ordinal)
            || code.Length != prefix.Length + 4
            || !int.TryParse(code.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var codeNumber))
        {
            // `code` is caller-supplied and, at this point, has not been shape-checked at all — it
            // could be arbitrarily long or carry control/escape bytes. SanitiseForEcho caps and
            // sanitises it before it goes anywhere near an exception message a host might display.
            throw new ArgumentException(
                $"Code must match the pattern '{prefix}####' (exactly four digits). Got: '{SanitiseForEcho(code)}'.",
                paramName);
        }

        if (!ReservedRanges.Any(r => codeNumber >= r.Start && codeNumber <= r.End))
        {
            // Unlike the malformed-code throw above, `code` here is already known to be exactly
            // `prefix` + 4 digits (the `if` above guarantees it) — inherently short and printable
            // ASCII — so no SanitiseForEcho call is needed on this path.
            throw new ArgumentException(
                $"Code '{code}' (number {codeNumber}) falls outside every reserved range. " +
                $"Valid ranges: {ReservedRangesDescription}.",
                paramName);
        }
    }

    /// <summary>
    /// Caps <paramref name="value"/> at <see cref="MaxEchoLength"/> characters and renders it
    /// through <see cref="TextSanitiser.SanitiseForDisplay"/> — every place in this codebase that
    /// echoes a caller-supplied value into a message meant for display renders it through that
    /// helper first (see its remarks: "a security boundary, not cosmetics"), and this adds the
    /// length cap <see cref="TextSanitiser.SanitiseForDisplay"/> alone does not apply, mirroring
    /// <c>DocSearchService.SearchAsync</c>'s truncate-then-sanitise pattern for an oversized query.
    /// Shared here so <see cref="VfxError"/>'s and <see cref="Diagnostic"/>'s constructors echo an
    /// invalid caller value the same, safe way. Tolerates <see langword="null"/> (a caller can pass
    /// <c>null!</c> for a non-nullable parameter) by echoing an empty string rather than throwing —
    /// this helper's whole job is to make an already-invalid value safe to display, not to add a
    /// second opinion on whether it is valid.
    /// </summary>
    internal static string SanitiseForEcho(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Truncate, THEN sanitise, THEN append the ellipsis — never append it before sanitising.
        // SanitiseForDisplay escapes every character outside printable ASCII (0x20-0x7E) to a
        // literal backslash-u-XXXX sequence, and the ellipsis glyph is U+2026, outside that range:
        // appending it before sanitising would have SanitiseForDisplay rewrite the one glyph into
        // that six-character escape sequence on the wire. Appending it after sanitising keeps it
        // the single real glyph, since it was never part of the text that got sanitised.
        var truncated = value.Length > MaxEchoLength;
        var capped = truncated ? value[..MaxEchoLength] : value;
        var sanitised = TextSanitiser.SanitiseForDisplay(capped);
        return truncated ? sanitised + "…" : sanitised;
    }
}
