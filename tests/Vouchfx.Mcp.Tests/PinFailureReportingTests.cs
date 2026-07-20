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
}
