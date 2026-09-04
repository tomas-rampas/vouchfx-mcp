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

    // ── DescribeToolMetaFailure (US-S1-02's startup provenance-stamp guard) ─────────────────────

    [Fact]
    public void DescribeToolMetaFailure_WrappedInTypeInitializationException_ReportsTheInnerCause()
    {
        // The shape this helper actually sees in production: ToolMetaProvider.Current is a static
        // property, so a failure inside VendoredSchemaVersion's initialiser reaches Program.cs
        // WRAPPED. Reporting the wrapper would print "could not derive the result provenance stamp:
        // the vendored schema could not be read (TypeInitializationException)" -- true but useless.
        // The inner InvalidOperationException is the one that names the missing marker.
        var inner = new InvalidOperationException(
            "Embedded resource 'Vouchfx.Mcp.Vendored.composed-schema.v1.json' no longer declares its "
            + "language schema version at the top-level 'x-vouchfx-schema-version' keyword.");
        var wrapped = new TypeInitializationException("Vouchfx.Mcp.Validation.VendoredSchemaVersion", inner);

        var message = PinFailureReporting.DescribeToolMetaFailure(wrapped);

        Assert.Equal($"vouchfx-mcp: could not derive the result provenance stamp: {inner.Message}", message);
        Assert.DoesNotContain(nameof(TypeInitializationException), message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeToolMetaFailure_UnexpectedExceptionType_IsReducedToItsTypeNameOnly()
    {
        // Same message-forwarding policy as DescribeLoadFailure: only this repo's own
        // InvalidOperationException text is forwarded. A BCL-authored message could carry a full
        // path, so anything else collapses to a type name.
        var exception = new UnauthorizedAccessException(@"Access to the path 'C:\Users\someone\secret' is denied.");

        var message = PinFailureReporting.DescribeToolMetaFailure(exception);

        Assert.Equal(
            "vouchfx-mcp: could not derive the result provenance stamp: the vendored schema could not "
            + "be read (UnauthorizedAccessException).",
            message);
        Assert.DoesNotContain("someone", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeToolMetaFailure_MessageWithDisallowedByte_ProducesMessageWithoutRawControlBytes()
    {
        var disallowedByte = ((char)27).ToString();
        var exception = new InvalidOperationException($"Embedded resource bad{disallowedByte}marker.");

        var message = PinFailureReporting.DescribeToolMetaFailure(exception);

        Assert.DoesNotContain(disallowedByte, message, StringComparison.Ordinal);
        foreach (var c in message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    // ── DescribeDiagnosticCatalogueFailure (US-S1-05's startup diagnostic-catalogue guard) ──────

    [Fact]
    public void DescribeDiagnosticCatalogueFailure_WrappedInTypeInitializationException_ReportsTheInnerCause()
    {
        // The shape this helper actually sees in production: DiagnosticPageRepository.AllByCode is
        // a static property, so a bad embedded page reaches Program.cs WRAPPED. Reporting the
        // wrapper would print a useless "(TypeInitializationException)" instead of naming the page.
        var inner = new InvalidOperationException(
            "Embedded resource 'Vouchfx.Mcp.Errors.VFX-E-1002.md' was not found in 'Vouchfx.Mcp'.");
        var wrapped = new TypeInitializationException("Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository", inner);

        var message = PinFailureReporting.DescribeDiagnosticCatalogueFailure(wrapped);

        Assert.Equal($"vouchfx-mcp: could not load the diagnostic catalogue: {inner.Message}", message);
        Assert.DoesNotContain(nameof(TypeInitializationException), message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeDiagnosticCatalogueFailure_FormatExceptionFromAMalformedPage_ForwardsTheSanitisedMessage()
    {
        // DiagnosticPageParser throws FormatException for a page that violates the fixed heading
        // structure — this repo's own code, path-free, so (unlike DescribeToolMetaFailure, which
        // forwards only InvalidOperationException) this helper forwards it too.
        var exception = new FormatException("'VFX-E-1002' is missing required heading(s): ## Fixes.");

        var message = PinFailureReporting.DescribeDiagnosticCatalogueFailure(exception);

        Assert.Equal($"vouchfx-mcp: could not load the diagnostic catalogue: {exception.Message}", message);
    }

    [Fact]
    public void DescribeDiagnosticCatalogueFailure_UnexpectedExceptionType_IsReducedToItsTypeNameOnly()
    {
        var exception = new UnauthorizedAccessException(@"Access to the path 'C:\Users\someone\secret' is denied.");

        var message = PinFailureReporting.DescribeDiagnosticCatalogueFailure(exception);

        Assert.Equal(
            "vouchfx-mcp: could not load the diagnostic catalogue: a docs/errors/*.md catalogue page "
            + "could not be read (UnauthorizedAccessException).",
            message);
        Assert.DoesNotContain("someone", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeDiagnosticCatalogueFailure_MessageWithDisallowedByte_ProducesMessageWithoutRawControlBytes()
    {
        var disallowedByte = ((char)27).ToString();
        var exception = new InvalidOperationException($"Embedded resource bad{disallowedByte}marker.");

        var message = PinFailureReporting.DescribeDiagnosticCatalogueFailure(exception);

        Assert.DoesNotContain(disallowedByte, message, StringComparison.Ordinal);
        foreach (var c in message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }
}
