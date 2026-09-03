using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tests.Contracts;

/// <summary>
/// Covers <see cref="Diagnostic"/>'s construction-time <c>VFX-D-####</c> code and severity
/// validation, and its source-generated <see cref="DiagnosticJsonContext"/> (Sprint 1 / US-S1-03).
/// This story wires up the record and its JSON context only — no tool constructs a
/// <see cref="Diagnostic"/> yet (that is US-S1-04, starting with the existing <c>suite-invalid</c>
/// precedent — see this type's file-header remarks for the "diagnostics are data, not errors" rule
/// this record exists to enforce).
/// </summary>
public class DiagnosticTests
{
    // ── Round-trip through the source-generated context ────────────────────────────────────────

    [Fact]
    public void RoundTrip_ViaSourceGeneratedContext_ReturnsEqualInstance()
    {
        var original = new Diagnostic(
            "VFX-D-1201",
            "warning",
            "Unknown step type 'mq-expect.nonexistent-provider'",
            location: null,
            path: null,
            fix: null,
            docsUrl: null);

        var json = JsonSerializer.Serialize(original, DiagnosticJsonContext.Default.Diagnostic);
        var roundTripped = JsonSerializer.Deserialize(json, DiagnosticJsonContext.Default.Diagnostic);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void RoundTrip_WithLocationAndFixPopulated_ReturnsEqualInstance()
    {
        var original = new Diagnostic(
            "VFX-D-1201",
            "error",
            "Unknown step type 'mq-expect.nonexistent-provider' — did you mean 'mq-expect.kafka'?",
            new DiagnosticLocation("suites/checkout.e2e.yaml", 12, 5, EndLine: 12, EndColumn: 40),
            "$.steps[2].type",
            new DiagnosticFix("Use the closest-matching known step type", "mq-expect.kafka"),
            "https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1201.html");

        var json = JsonSerializer.Serialize(original, DiagnosticJsonContext.Default.Diagnostic);
        var roundTripped = JsonSerializer.Deserialize(json, DiagnosticJsonContext.Default.Diagnostic);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Serialize_OptionalFieldsNull_AreOmittedFromJson_NotEmittedAsNull()
    {
        var diagnostic = new Diagnostic(
            "VFX-D-1201",
            "warning",
            "Unknown step type",
            location: null,
            path: null,
            fix: null,
            docsUrl: null);

        var json = JsonSerializer.Serialize(diagnostic, DiagnosticJsonContext.Default.Diagnostic);

        Assert.DoesNotContain("location", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fix\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("docsUrl", json, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"VFX-D-1201\"", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"warning\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_FixWithoutReplacement_OmitsReplacementButKeepsFix()
    {
        var diagnostic = new Diagnostic(
            "VFX-D-1201",
            "info",
            "Advisory only",
            location: null,
            path: null,
            new DiagnosticFix("Consider renaming the step id", Replacement: null),
            docsUrl: null);

        var json = JsonSerializer.Serialize(diagnostic, DiagnosticJsonContext.Default.Diagnostic);

        Assert.Contains("\"fix\":{\"description\":\"Consider renaming the step id\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", json, StringComparison.Ordinal);
    }

    // ── Severity validation ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("error")]
    [InlineData("warning")]
    [InlineData("info")]
    public void Constructor_ValidSeverity_Succeeds(string severity)
    {
        var diagnostic = new Diagnostic("VFX-D-1201", severity, "msg", null, null, null, null);

        Assert.Equal(severity, diagnostic.Severity);
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("Error")] // wrong case — the three literals are case-sensitive per spec §5
    [InlineData("")]
    [InlineData("fatal")]
    public void Constructor_InvalidSeverity_Throws(string severity)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Diagnostic("VFX-D-1201", severity, "msg", null, null, null, null));

        Assert.Contains("error", ex.Message, StringComparison.Ordinal);
        Assert.Contains("warning", ex.Message, StringComparison.Ordinal);
        Assert.Contains("info", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullSeverity_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Diagnostic("VFX-D-1201", null!, "msg", null, null, null, null));
    }

    // ── Construction-time code validation (shared range table with VfxError) ───────────────────

    [Fact]
    public void Constructor_CodeAboveEveryReservedRange_ThrowsNamingValidRanges()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Diagnostic("VFX-D-9999", "error", "msg", null, null, null, null));

        Assert.Contains("1000-1099", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1900-1999", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1800)] // the gap's own lower fencepost — a mistyped (1700, 1800) tuple would wrongly accept this
    [InlineData(1850)] // midpoint — the original regression case
    [InlineData(1899)] // the gap's own upper fencepost — a mistyped (1899, 1999) tuple would wrongly accept this
    public void Constructor_CodeInUnreservedGap_Throws(int number)
    {
        // Mirrors VfxErrorTests.Constructor_CodeInUnreservedGap_Throws: 1800-1899 is deliberately
        // NOT reserved despite sitting between two reserved neighbours, and the midpoint alone
        // cannot catch an off-by-one in either boundary tuple — only the fenceposts can.
        var ex = Assert.Throws<ArgumentException>(() => new Diagnostic($"VFX-D-{number}", "error", "msg", null, null, null, null));

        Assert.Contains("1700-1799", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1900-1999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ErrorPrefixCode_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Diagnostic("VFX-E-1201", "error", "msg", null, null, null, null));
    }

    [Theory]
    [InlineData("VFX-D-120")] // three digits
    [InlineData("VFX-D-12011")] // five digits
    [InlineData("VFX-D-12A1")] // non-digit
    [InlineData("vfx-d-1201")] // wrong case
    [InlineData("VFX-D-")] // no number at all
    [InlineData("1201")] // no prefix at all
    [InlineData("")] // empty
    public void Constructor_MalformedCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => new Diagnostic(code, "error", "msg", null, null, null, null));
    }

    [Fact]
    public void Constructor_NullCode_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new Diagnostic(null!, "error", "msg", null, null, null, null));
    }

    [Fact]
    public void Constructor_BlankMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new Diagnostic("VFX-D-1201", "error", "   ", null, null, null, null));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1099)]
    [InlineData(1199)]
    [InlineData(1299)]
    [InlineData(1399)]
    [InlineData(1499)]
    [InlineData(1599)]
    [InlineData(1699)]
    [InlineData(1799)]
    [InlineData(1900)]
    [InlineData(1999)]
    public void Constructor_CodeAtRangeBoundary_Succeeds(int number)
    {
        // Mirrors VfxErrorTests.Constructor_CodeAtRangeBoundary_Succeeds, with VFX-D- codes: every
        // reserved range's own lower and upper fencepost must be ACCEPTED, symmetric with
        // Constructor_CodeInUnreservedGap_Throws asserting the 1800-1899 gap's fenceposts are
        // REJECTED — together the two theories pin down every boundary in the range table.
        var diagnostic = new Diagnostic($"VFX-D-{number}", "error", "boundary", null, null, null, null);

        Assert.Equal($"VFX-D-{number}", diagnostic.Code);
    }
}
