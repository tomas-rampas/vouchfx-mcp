using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tests.Contracts;

/// <summary>
/// Covers <see cref="VfxError"/>'s construction-time <c>VFX-E-####</c> code validation and its
/// source-generated <see cref="VfxErrorJsonContext"/> (Sprint 1 / US-S1-03). This story wires up the
/// record and its JSON context only — no tool constructs a <see cref="VfxError"/> yet (that is
/// US-S1-04), so every test here goes directly through the type, not through the MCP wire.
/// </summary>
public class VfxErrorTests
{
    // ── Round-trip through the source-generated context ────────────────────────────────────────

    [Fact]
    public void RoundTrip_ViaSourceGeneratedContext_ReturnsEqualInstance()
    {
        var original = new VfxError("VFX-E-1001", "Path outside workspace", retryable: false);

        var json = JsonSerializer.Serialize(original, VfxErrorJsonContext.Default.VfxError);
        var roundTripped = JsonSerializer.Deserialize(json, VfxErrorJsonContext.Default.VfxError);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void RoundTrip_WithDetailsDocsUrlPopulated_PreservesEveryField()
    {
        using var details = JsonDocument.Parse("""{"runId":"run-abc123"}""");
        var original = new VfxError(
            "VFX-E-1501",
            "A run is already in progress for this workspace",
            details.RootElement.Clone(),
            "https://vouchfx.io/docs/errors/VFX-E-1501",
            retryable: true);

        var json = JsonSerializer.Serialize(original, VfxErrorJsonContext.Default.VfxError);
        var roundTripped = JsonSerializer.Deserialize(json, VfxErrorJsonContext.Default.VfxError);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Code, roundTripped.Code);
        Assert.Equal(original.Message, roundTripped.Message);
        Assert.Equal(original.DocsUrl, roundTripped.DocsUrl);
        Assert.Equal(original.Retryable, roundTripped.Retryable);
        Assert.Equal("run-abc123", roundTripped.Details!.Value.GetProperty("runId").GetString());
        // VfxError overrides Equals(VfxError?) to compare Details by raw JSON text (see its
        // remarks) rather than JsonElement's own no-op value equality — exercise that override
        // directly here so a regression that breaks it (e.g. deleting the override) is caught by
        // this test, not just inferred from the field-by-field asserts above.
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Serialize_OptionalFieldsNull_AreOmittedFromJson_NotEmittedAsNull()
    {
        var error = new VfxError("VFX-E-1001", "Path outside workspace", retryable: false);

        var json = JsonSerializer.Serialize(error, VfxErrorJsonContext.Default.VfxError);

        Assert.DoesNotContain("details", json, StringComparison.Ordinal);
        Assert.DoesNotContain("docsUrl", json, StringComparison.Ordinal);
        // Required fields are still present.
        Assert.Contains("\"code\":\"VFX-E-1001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_OptionalFieldsPopulated_AppearOnTheWire()
    {
        using var details = JsonDocument.Parse("""{"path":"/tmp/x"}""");
        var error = new VfxError(
            "VFX-E-1001",
            "Path outside workspace",
            details.RootElement.Clone(),
            "https://vouchfx.io/docs/errors/VFX-E-1001",
            retryable: false);

        var json = JsonSerializer.Serialize(error, VfxErrorJsonContext.Default.VfxError);

        Assert.Contains("\"details\":{\"path\":\"/tmp/x\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"docsUrl\":\"https://vouchfx.io/docs/errors/VFX-E-1001\"", json, StringComparison.Ordinal);
    }

    // ── Construction-time code validation ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CodeAboveEveryReservedRange_ThrowsNamingValidRanges()
    {
        var ex = Assert.Throws<ArgumentException>(() => new VfxError("VFX-E-9999", "boom", retryable: false));

        Assert.Contains("1000-1099", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1900-1999", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1800)] // the gap's own lower fencepost — a mistyped (1700, 1800) tuple would wrongly accept this
    [InlineData(1850)] // midpoint — the original regression case
    [InlineData(1899)] // the gap's own upper fencepost — a mistyped (1899, 1999) tuple would wrongly accept this
    public void Constructor_CodeInUnreservedGap_Throws(int number)
    {
        // 1800-1899 sits between two reserved neighbours (1700-1799 and 1900-1999) but is itself
        // deliberately NOT reserved — this is the specific regression this test guards against: a
        // naive "is the number between the lowest and highest reserved bound" check would wrongly
        // accept it. The midpoint (1850) alone cannot catch an off-by-one in either boundary tuple
        // (e.g. (1700, 1800) or (1899, 1999)) — only the fenceposts themselves can.
        var ex = Assert.Throws<ArgumentException>(() => new VfxError($"VFX-E-{number}", "boom", retryable: false));

        Assert.Contains("1700-1799", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1900-1999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DiagnosticPrefixCode_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VfxError("VFX-D-1001", "boom", retryable: false));
    }

    [Theory]
    [InlineData("VFX-E-101")] // three digits
    [InlineData("VFX-E-10011")] // five digits
    [InlineData("VFX-E-10A1")] // non-digit
    [InlineData("vfx-e-1001")] // wrong case
    [InlineData("VFX-E-")] // no number at all
    [InlineData("1001")] // no prefix at all
    [InlineData("")] // empty
    public void Constructor_MalformedCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => new VfxError(code, "boom", retryable: false));
    }

    [Fact]
    public void Constructor_NullCode_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new VfxError(null!, "boom", retryable: false));
    }

    [Fact]
    public void Constructor_BlankMessage_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new VfxError("VFX-E-1001", "   ", retryable: false));
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
        var error = new VfxError($"VFX-E-{number}", "boundary", retryable: false);

        Assert.Equal($"VFX-E-{number}", error.Code);
    }
}
