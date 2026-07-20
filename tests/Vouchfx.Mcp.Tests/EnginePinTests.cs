namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers <see cref="EnginePin"/>'s parsing of the repo-root ENGINE_PIN file
/// format: "<version> <full-40-char-lowercase-commit-sha>" on the first
/// non-comment, non-blank line.
/// </summary>
public class EnginePinTests
{
    private const string ValidVersion = "v1.0.0-alpha.9";
    private const string ValidSha = "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1";
    private const string AllZerosSha = "0000000000000000000000000000000000000000";

    [Fact]
    public void Parse_ValidPinLine_YieldsVersionAndCommitSha()
    {
        var pin = EnginePin.Parse(new[] { $"{ValidVersion} {ValidSha}" });

        Assert.Equal(ValidVersion, pin.Version);
        Assert.Equal(ValidSha, pin.CommitSha);
    }

    [Fact]
    public void Parse_AllZerosCommitSha_ThrowsDescriptiveError()
    {
        var ex = Assert.Throws<FormatException>(
            () => EnginePin.Parse(new[] { $"{ValidVersion} {AllZerosSha}" }));

        Assert.Contains("all-zeros", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("v1.0.0-alpha.9")] // missing SHA entirely
    [InlineData("v1.0.0-alpha.9 8c579ab4")] // short SHA (8 chars, not 40)
    [InlineData("v1.0.0-alpha.9 8C579AB4315CACBA4066BC3F33DC24A19CA6C3D1")] // wrong case, not lowercase
    [InlineData(" 8c579ab4315cacba4066bc3f33dc24a19ca6c3d1")] // missing version, SHA only
    [InlineData("v1.0.0-alpha.9 8c579ab4315cacba4066bc3f33dc24a19ca6c3d1 extra")] // trailing content after the two expected fields
    public void Parse_MalformedFirstLine_Throws(string malformedLine)
    {
        Assert.Throws<FormatException>(() => EnginePin.Parse(new[] { malformedLine }));
    }

    [Fact]
    public void Parse_VersionWithDisallowedByte_Throws()
    {
        // The disallowed byte is built from its numeric code point (7 equals
        // ASCII bell) rather than an inline literal, so the tracked source
        // text never contains a raw non-printable byte. Guards the
        // security-relevant restriction in EnginePin.ValidateVersion: a byte
        // outside the allowed printable-ASCII set must never survive parsing,
        // since the version flows straight into console output and, later,
        // into a trust decision (the CLI handshake check).
        var disallowedByte = ((char)7).ToString();
        var line = ValidVersion + disallowedByte + " " + ValidSha;

        Assert.Throws<FormatException>(() => EnginePin.Parse(new[] { line }));
    }

    [Fact]
    public void Parse_LineWithTrailingCarriageReturn_ParsesSuccessfully()
    {
        // Guards a CRLF-sourced line where only the LF was stripped by the
        // line reader (e.g. a naive split), leaving a trailing CR on the
        // line; Trim() inside FindFirstPinLine already strips it, so this
        // must parse cleanly.
        var lineWithTrailingCr = ValidVersion + " " + ValidSha + ((char)13).ToString();

        var pin = EnginePin.Parse(new[] { lineWithTrailingCr });

        Assert.Equal(ValidVersion, pin.Version);
        Assert.Equal(ValidSha, pin.CommitSha);
    }

    [Fact]
    public void Parse_SkipsLeadingCommentAndBlankLines()
    {
        var lines = new[]
        {
            "# a leading comment",
            "",
            "   ",
            "# another comment before the pin line",
            $"{ValidVersion} {ValidSha}",
        };

        var pin = EnginePin.Parse(lines);

        Assert.Equal(ValidVersion, pin.Version);
        Assert.Equal(ValidSha, pin.CommitSha);
    }

    [Fact]
    public void Parse_EmptyInput_ThrowsDescriptiveError()
    {
        var ex = Assert.Throws<FormatException>(() => EnginePin.Parse(Array.Empty<string>()));

        Assert.Contains("ENGINE_PIN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReadsPinFileFromDisk()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[]
            {
                "# header comment, as in the real ENGINE_PIN file",
                $"{ValidVersion} {ValidSha}",
                "#",
                "# trailing commentary follows the pin line in the real file",
            });

            var pin = EnginePin.Load(path);

            Assert.Equal(ValidVersion, pin.Version);
            Assert.Equal(ValidSha, pin.CommitSha);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.missing");

        Assert.Throws<FileNotFoundException>(() => EnginePin.Load(missingPath));
    }
}
