using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="CliVersionNormaliser"/> — the exact normalisation rule verified against the
/// real, installed <c>vouchfx</c> CLI (see that type's remarks): strip a leading 'v'/'V', drop a
/// '+buildmetadata' suffix, trim whitespace.
/// </summary>
public class CliVersionNormaliserTests
{
    [Theory]
    [InlineData("v1.0.0-alpha.9", "1.0.0-alpha.9")]
    [InlineData("V1.0.0-alpha.9", "1.0.0-alpha.9")]
    [InlineData("1.0.0-alpha.9", "1.0.0-alpha.9")]
    public void Normalise_LeadingVOrNone_StripsOnlyALeadingV(string input, string expected)
    {
        Assert.Equal(expected, CliVersionNormaliser.Normalise(input));
    }

    [Theory]
    [InlineData("1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1", "1.0.0-alpha.9")]
    [InlineData("v1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1", "1.0.0-alpha.9")]
    [InlineData("1.0.0+build.1", "1.0.0")]
    public void Normalise_BuildMetadataSuffix_IsDropped(string input, string expected)
    {
        // The exact shape the REAL, currently-installed vouchfx 1.0.0-alpha.9 CLI reports (see
        // CliVersionNormaliser's remarks): "1.0.0-alpha.9+<commit-sha>".
        Assert.Equal(expected, CliVersionNormaliser.Normalise(input));
    }

    [Theory]
    [InlineData("  v1.0.0-alpha.9  ", "1.0.0-alpha.9")]
    [InlineData("\t1.0.0-alpha.9\r\n", "1.0.0-alpha.9")]
    public void Normalise_SurroundingWhitespace_IsTrimmed(string input, string expected)
    {
        // The real CLI's stdout ends in a trailing CRLF (verified directly) — Normalise must not
        // let that leak into the comparable core version.
        Assert.Equal(expected, CliVersionNormaliser.Normalise(input));
    }

    [Fact]
    public void Normalise_VPrefixAndBuildMetadataTogether_BothAreHandled()
    {
        Assert.Equal(
            "1.0.0-alpha.9",
            CliVersionNormaliser.Normalise(" V1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1\r\n"));
    }

    [Fact]
    public void Normalise_IsNotItselfCaseInsensitive_OnlyTheLeadingVIsSpecialCased()
    {
        // Case-insensitivity is CliPinVerifier's job (via StringComparison.OrdinalIgnoreCase at
        // the comparison site), not Normalise's — Normalise only strips a leading v/V, it does not
        // lower-case the rest of the string.
        Assert.Equal("Alpha.9", CliVersionNormaliser.Normalise("vAlpha.9"));
    }

    [Theory]
    [InlineData("1.0.0-alpha.9", true)]
    [InlineData("2.0.0", true)]
    [InlineData("", false)]
    [InlineData("alpha.9", false)]
    [InlineData("not-a-version", false)]
    public void LooksLikeAVersion_StartsWithADigitOrNot(string normalisedValue, bool expected)
    {
        Assert.Equal(expected, CliVersionNormaliser.LooksLikeAVersion(normalisedValue));
    }
}
