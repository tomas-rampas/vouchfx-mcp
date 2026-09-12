using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Tests.Observability;

/// <summary>
/// Mirror-namespace unit tests for the <c>ILogger</c> entry point into US-S6-05's record shape.
/// </summary>
/// <remarks>
/// Joins <see cref="SpanAssertionGroup"/> because <see cref="ConsoleErrorCapture"/> redirects a
/// PROCESS-global stream — see that type's remarks.
/// </remarks>
[Collection(SpanAssertionGroup.Name)]
public class StructuredConsoleFormatterTests
{
    [Fact]
    public void ItForwardsTheLevelAndTheFormattedMessage()
    {
        using var stderr = new ConsoleErrorCapture();

        WriteEntry(LogLevel.Warning, "vouchfx-mcp: something operational happened");

        var record = Assert.Single(stderr.JsonLines());

        Assert.Equal("warning", record.GetProperty("level").GetString());
        Assert.Equal("vouchfx-mcp: something operational happened", record.GetProperty("message").GetString());
    }

    /// <summary>
    /// A security review's MAJOR finding: an earlier revision dropped <c>logEntry.Exception</c> for the
    /// whole process, so a faulted Hosting background service or a throwing SDK handler lost its cause
    /// entirely — the default formatter this replaced printed the exception.
    /// </summary>
    [Fact]
    public void AnExceptionContributesItsTypeName_AndNothingElseAboutIt()
    {
        using var stderr = new ConsoleErrorCapture();

        var failure = new InvalidOperationException(
            "SECRET-BEARING-MESSAGE with C:\\some\\path",
            new UnauthorizedAccessException("INNER-SECRET"));

        WriteEntry(LogLevel.Error, "vouchfx-mcp: a hosted service faulted", failure);

        var record = Assert.Single(stderr.JsonLines());

        Assert.Equal("InvalidOperationException", record.GetProperty("errorType").GetString());

        // The TYPE only. Message routinely embeds a full filesystem path, StackTrace embeds the build
        // machine's, and the inner chain carries both again — none may reach a record.
        foreach (var field in record.EnumerateObject())
        {
            var value = field.Value.ToString();

            Assert.DoesNotContain("SECRET-BEARING-MESSAGE", value, StringComparison.Ordinal);
            Assert.DoesNotContain("INNER-SECRET", value, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\some\\path", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithNoException_TheErrorTypeFieldIsAbsentRatherThanNull()
    {
        using var stderr = new ConsoleErrorCapture();

        WriteEntry(LogLevel.Information, "vouchfx-mcp: an ordinary banner");

        var record = Assert.Single(stderr.JsonLines());

        Assert.False(record.TryGetProperty("errorType", out _));
    }

    /// <summary>
    /// The empty-message early return, and the BCL dependency it rests on.
    /// </summary>
    /// <remarks>
    /// This formatter writes nothing to the <see cref="TextWriter"/> it is handed and calls
    /// <c>StructuredLog</c> instead, which only avoids a DOUBLE write because <c>ConsoleLogger</c>
    /// skips its own write when the formatter left the buffer empty. That is a dependency on another
    /// assembly's behaviour, so the path is covered here deliberately: if a future
    /// <c>Microsoft.Extensions.Logging.Console</c> stopped short-circuiting, every record would be
    /// followed by a blank line on stderr — breaking one-object-per-line without breaking any
    /// assertion about the records themselves.
    /// </remarks>
    [Fact]
    public void AnEmptyFormattedMessageWithNoException_ProducesNoRecordAtAll()
    {
        using var stderr = new ConsoleErrorCapture();

        WriteEntry(LogLevel.Information, string.Empty);

        Assert.Empty(stderr.Lines());
    }

    /// <summary>
    /// The other side of that early return: an empty message WITH an exception is still a record.
    /// </summary>
    /// <remarks>
    /// Dropping it reintroduced the very defect
    /// <see cref="AnExceptionContributesItsTypeName_AndNothingElseAboutIt"/> exists to prevent — a
    /// fault whose cause never reaches an operator's only diagnostic channel. Hosting's
    /// background-service failure path is a real producer of entries whose state formats to nothing,
    /// so the empty-message test above could not stand alone.
    /// </remarks>
    [Fact]
    public void AnEmptyFormattedMessageWithAnException_StillRecordsTheCause()
    {
        using var stderr = new ConsoleErrorCapture();

        WriteEntry(
            LogLevel.Critical,
            string.Empty,
            new TimeoutException("SECRET-BEARING-MESSAGE with C:\\some\\path"));

        var record = Assert.Single(stderr.JsonLines());

        Assert.Equal("critical", record.GetProperty("level").GetString());
        Assert.Equal("TimeoutException", record.GetProperty("errorType").GetString());

        // A fixed placeholder rather than the type name repeated into the message: the type belongs
        // in its own queryable field.
        Assert.Equal("(no message)", record.GetProperty("message").GetString());

        // The content policy is unchanged on this path — the type only, never the message.
        foreach (var field in record.EnumerateObject())
        {
            Assert.DoesNotContain("SECRET-BEARING-MESSAGE", field.Value.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ItWritesNothingToTheTextWriterItIsHanded()
    {
        // The other half of the same dependency: the buffer must be left empty, or ConsoleLogger
        // writes it and the record is duplicated in a second, unstructured form.
        using var stderr = new ConsoleErrorCapture();
        var handed = new StringWriter();

        WriteEntry(LogLevel.Information, "vouchfx-mcp: a banner", textWriter: handed);

        Assert.Equal(string.Empty, handed.ToString());
        Assert.Single(stderr.JsonLines());
    }

    [Fact]
    public void TheFormatterName_IsTheOneProgramSelectsBy()
    {
        Assert.Equal("vouchfx-structured", StructuredConsoleFormatter.FormatterName);
        Assert.Equal(StructuredConsoleFormatter.FormatterName, new StructuredConsoleFormatter().Name);
    }

    private static void WriteEntry(
        LogLevel level, string message, Exception? exception = null, TextWriter? textWriter = null)
    {
        var entry = new LogEntry<string>(
            level,
            category: "Vouchfx.Mcp.Tests",
            eventId: default,
            state: message,
            exception: exception,
            formatter: (state, _) => state);

        new StructuredConsoleFormatter().Write(in entry, scopeProvider: null, textWriter ?? new StringWriter());
    }
}
