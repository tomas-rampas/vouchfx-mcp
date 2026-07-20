namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Covers <see cref="PinFailureReporting.DescribeLoadFailure"/>: the helper Program.cs uses to
/// build the one-line message it prints to stderr when ENGINE_PIN cannot be read or parsed.
/// </summary>
public class PinFailureReportingTests
{
    [Fact]
    public void DescribeLoadFailure_ExceptionMessageWithDisallowedByte_ProducesMessageWithoutRawControlBytes()
    {
        // Defence in depth: even if an exception's own Message somehow carried an unsanitised
        // raw control byte through to this point, DescribeLoadFailure must scrub it before the
        // message is fit to print to a terminal. Built numerically (27 equals ASCII ESC), never
        // as a literal.
        var disallowedByte = ((char)27).ToString();
        var exception = new FormatException($"Malformed ENGINE_PIN version 'v1.0.0{disallowedByte}': bad.");

        var message = PinFailureReporting.DescribeLoadFailure(exception);

        Assert.Contains("ENGINE_PIN", message, StringComparison.Ordinal);
        Assert.DoesNotContain(disallowedByte, message, StringComparison.Ordinal);
        foreach (var c in message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    [Fact]
    public void DescribeLoadFailure_MissingEnginePinFile_MessageDoesNotContainTheDirectoryPath()
    {
        // A real regression test, not a vacuous one: EnginePin.Load's own FileNotFoundException
        // for a genuinely missing file is what DescribeLoadFailure has to scrub — a FormatException
        // whose message never had a path in it proves nothing about that.
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var missingPinPath = Path.Combine(tempDirectory.FullName, "ENGINE_PIN");

            var exception = Assert.Throws<FileNotFoundException>(() => EnginePin.Load(missingPinPath));

            var message = PinFailureReporting.DescribeLoadFailure(exception);

            Assert.Contains("ENGINE_PIN", message, StringComparison.Ordinal);
            Assert.DoesNotContain(tempDirectory.FullName, message, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeLoadFailure_FileNotFoundExceptionWithControlByteInFileName_ProducesMessageWithoutRawControlBytes()
    {
        // ResolveFileName must sanitise the bare file name it resolves from FileName, not just
        // trust it: on platforms that allow control characters in file names, a crafted
        // FileNotFoundException.FileName could otherwise inject raw bytes into the printed
        // message. Built numerically (7 equals ASCII bell), never as a literal.
        var disallowedByte = ((char)7).ToString();
        var hostileFileName = $"ENGINE_PIN{disallowedByte}";
        var exception = new FileNotFoundException("irrelevant — FileNotFoundException never forwards .Message", hostileFileName);

        var message = PinFailureReporting.DescribeLoadFailure(exception);

        Assert.DoesNotContain(disallowedByte, message, StringComparison.Ordinal);
        foreach (var c in message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    [Fact]
    public void DescribeLoadFailure_UnauthorizedAccessException_MessageOmitsPathAndMessageButNamesTheExceptionType()
    {
        // The core of the fix: a BCL exception type's own Message is never forwarded at all,
        // because it cannot be trusted to be path-free (UnauthorizedAccessException,
        // DirectoryNotFoundException, and plain IOException routinely embed a full path in
        // printable ASCII, which a control-character-only sanitiser would pass straight
        // through). Instead the caller gets a path-free, type-named summary.
        const string fakeAbsolutePath = @"C:\Users\someone\secret-folder\ENGINE_PIN";
        var exception = new UnauthorizedAccessException($"Access to the path '{fakeAbsolutePath}' is denied.");

        var message = PinFailureReporting.DescribeLoadFailure(exception);

        Assert.DoesNotContain(fakeAbsolutePath, message, StringComparison.Ordinal);
        Assert.DoesNotContain(exception.Message, message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnauthorizedAccessException), message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeLoadFailure_FormatException_StillForwardsTheSanitisedMessageVerbatim()
    {
        // The flip side of the two tests above: FormatException is the one exception type whose
        // message IS forwarded, because EnginePin authors it, it never contains a path, and it
        // is what actually tells the caller which pin field was malformed and why — that detail
        // must not be lost to the new "never forward BCL messages" policy.
        var exception = new FormatException(
            "Malformed ENGINE_PIN version 'bad version': expected only ASCII letters, digits, " +
            "'.', '-', or '+' characters (no spaces, control characters, or other symbols).");

        var message = PinFailureReporting.DescribeLoadFailure(exception);

        Assert.Equal($"vouchfx-mcp: could not read ENGINE_PIN: {exception.Message}", message);
    }
}
