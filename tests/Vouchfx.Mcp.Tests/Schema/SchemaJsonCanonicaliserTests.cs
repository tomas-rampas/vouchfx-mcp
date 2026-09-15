using System.Text.Json;
using Vouchfx.Mcp.Schema;

namespace Vouchfx.Mcp.Tests.Schema;

/// <summary>
/// Issue #89: the control-character pre-pass
/// (<see cref="SchemaJsonCanonicaliser.EscapeRawControlCharacters"/>) and the tolerant
/// canonicalisation built on it, which <c>get_schema</c>'s cross-verification runs on BOTH sides of
/// every comparison.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pre-pass is needed at all, measured rather than assumed.</b> A Windows console output
/// code page that cannot represent a schema character best-fit-maps it in the CHILD, before any byte
/// reaches this server. MEASURED 2026-09-15 under cp852: the schema's <c>…</c> (U+2026) is emitted as
/// a raw <c>0x07</c> INSIDE a JSON string, which makes <c>JsonDocument.Parse</c> throw — so without
/// the pre-pass the live export cannot even be compared, only reported as a divergence.
/// </para>
/// <para>
/// Every control character in this file is written as a C# <c>\uXXXX</c> escape rather than pasted
/// in raw, so no invisible byte can be lost or "helpfully" normalised by an editor, a diff or a
/// formatter — which would silently turn these tests into assertions about nothing.
/// </para>
/// </remarks>
public class SchemaJsonCanonicaliserTests
{
    [Fact]
    public void EscapeRawControlCharacters_EscapesTheCp852EllipsisByte()
    {
        // The exact shape measured off the pinned CLI under cp852: U+2026 best-fit-mapped to 0x07
        // inside a string value.
        var mangled = "{\"a\":\"x\u0007y\"}";

        var escaped = SchemaJsonCanonicaliser.EscapeRawControlCharacters(mangled);

        Assert.Equal("{\"a\":\"x\\u0007y\"}", escaped);

        // And the whole point: it now parses, whereas the raw text does not.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(mangled));
        using var parsed = JsonDocument.Parse(escaped);
        Assert.Equal("x\u0007y", parsed.RootElement.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData('\u0000', "\\u0000")]
    [InlineData('\u0008', "\\u0008")]
    [InlineData('\u000b', "\\u000b")]
    [InlineData('\u000c', "\\u000c")]
    [InlineData('\u000e', "\\u000e")]
    [InlineData('\u001f', "\\u001f")]
    public void EscapeRawControlCharacters_CoversEveryIllegalC0Character(char raw, string expectedEscape)
    {
        var mangled = $"{{\"a\":\"{raw}\"}}";

        // THE PREMISE, asserted per character rather than assumed. The whole "this pre-pass cannot
        // launder a broken document" argument rests on these characters being ILLEGAL RAW in JSON —
        // if any of them parsed, escaping it would be a content change rather than a repair, and the
        // repair would be applying to documents that never needed it.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(mangled));

        var escaped = SchemaJsonCanonicaliser.EscapeRawControlCharacters(mangled);

        Assert.Equal($"{{\"a\":\"{expectedEscape}\"}}", escaped);

        // And the repair lands: the escaped form parses, and to the original character.
        using var parsed = JsonDocument.Parse(escaped);
        Assert.Equal(raw.ToString(), parsed.RootElement.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void EscapeRawControlCharacters_LeavesTheThreeJsonWhitespaceControlsAlone(char whitespace)
    {
        // Tab/LF/CR are legal raw BETWEEN tokens (they are JSON whitespace), so escaping them would
        // change nothing about validity but would turn the pre-pass into a formatting change rather
        // than a repair. Placed between tokens here for exactly that reason.
        var json = $"{{{whitespace}\"a\":{whitespace}1{whitespace}}}";

        // The complement of the premise asserted above: these three genuinely DO parse raw, which is
        // why they are exempt — and, per SchemaJsonCanonicaliser's "SECOND residual" paragraph, why a
        // best-fit mapping landing on one of them inside a string is unrepairable.
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("a").GetInt32());

        Assert.Same(json, SchemaJsonCanonicaliser.EscapeRawControlCharacters(json));
    }

    [Fact]
    public void EscapeRawControlCharacters_IsIdempotentAndReturnsTheSameInstanceWhenThereIsNothingToDo()
    {
        var alreadyEscaped = "{\"a\":\"x\\u0007y\"}";

        var once = SchemaJsonCanonicaliser.EscapeRawControlCharacters(alreadyEscaped);
        var twice = SchemaJsonCanonicaliser.EscapeRawControlCharacters(once);

        Assert.Equal(alreadyEscaped, once);
        Assert.Equal(alreadyEscaped, twice);

        // Reference equality, not just value equality: the no-op path must not allocate a copy of a
        // 167,854-character document on a per-call code path.
        Assert.Same(alreadyEscaped, once);
    }

    [Fact]
    public void EscapeRawControlCharacters_LeavesAControlCharacterInAnEscapePositionRaw()
    {
        // The backslash-parity rule. `\` + 0x07 is an invalid escape sequence; escaping the 0x07 would
        // produce `\\u0007`, which parses as a literal backslash followed by the text "u0007" — the
        // one shape in which this pre-pass could turn an invalid document into a valid one with
        // DIFFERENT content. It must stay broken.
        var invalid = "{\"a\":\"\\\u0007\"}";

        var escaped = SchemaJsonCanonicaliser.EscapeRawControlCharacters(invalid);

        Assert.Same(invalid, escaped);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(escaped));
    }

    [Fact]
    public void EscapeRawControlCharacters_EscapesAfterAnEvenBackslashRun()
    {
        // The complement of the test above: `\\` is a complete escape (one literal backslash), so the
        // 0x07 that follows it is a genuine raw control character and IS escaped.
        var mangled = "{\"a\":\"\\\\\u0007\"}";

        var escaped = SchemaJsonCanonicaliser.EscapeRawControlCharacters(mangled);

        Assert.Equal("{\"a\":\"\\\\\\u0007\"}", escaped);
        using var parsed = JsonDocument.Parse(escaped);
        Assert.Equal("\\\u0007", parsed.RootElement.GetProperty("a").GetString());
    }

    [Fact]
    public void EscapeRawControlCharacters_DoesNotRescueAControlCharacterOutsideAString()
    {
        // Outside a string a C0 control is not whitespace, so the document is invalid before the
        // pre-pass; escaping it yields a bare `\`, which is invalid too. "Never makes an invalid
        // document valid except in the transcoding case" is what this pins.
        var invalid = "{\"a\":\u00071}";

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(invalid));
        Assert.ThrowsAny<JsonException>(
            () => JsonDocument.Parse(SchemaJsonCanonicaliser.EscapeRawControlCharacters(invalid)));
    }

    [Fact]
    public void CanonicaliseTolerant_AgreesWithStrictCanonicaliseOnTheVendoredDocument()
    {
        // The committed vendored file carries no raw control characters, so the pre-pass is a no-op on
        // it and GetSchemaOrchestrator's VendoredCanonicalJson is byte-identical whichever entry point
        // built it. That equality is what lets both sides of the cross-verification run the tolerant
        // pipeline without changing the meaning of the exact comparison.
        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.CanonicaliseTolerant(VendoredComposedSchema.RawJson));
    }

    [Fact]
    public void Canonicalise_StillThrowsOnARawControlCharacter()
    {
        // The strict entry point is deliberately unchanged: it is used where the text is this
        // server's own, and a raw control character there is a defect rather than a console artefact.
        Assert.ThrowsAny<JsonException>(() => SchemaJsonCanonicaliser.Canonicalise("{\"a\":\"x\u0007y\"}"));
    }
}
