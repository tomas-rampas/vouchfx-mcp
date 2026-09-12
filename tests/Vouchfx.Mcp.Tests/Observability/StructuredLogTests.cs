using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Tests.Observability;

/// <summary>
/// Mirror-namespace unit tests for US-S6-05's structured log record shape and its DI-less writer.
/// </summary>
/// <remarks>
/// Joins <see cref="SpanAssertionGroup"/> because <see cref="ConsoleErrorCapture"/> redirects a
/// PROCESS-global stream — see that type's remarks.
/// </remarks>
[Collection(SpanAssertionGroup.Name)]
public class StructuredLogTests
{
    [Fact]
    public void AStartupRecord_CarriesTheThreeAlwaysPresentFieldsAndNoOptionalOnes()
    {
        using var stderr = new ConsoleErrorCapture();

        StructuredLog.Write(LogLevel.Information, "vouchfx-mcp: pinned to vouchfx engine v1.0.0-rc.5");

        var record = Assert.Single(stderr.JsonLines());

        Assert.Equal("information", record.GetProperty("level").GetString());
        Assert.Equal("vouchfx-mcp: pinned to vouchfx engine v1.0.0-rc.5", record.GetProperty("message").GetString());
        Assert.True(record.TryGetProperty("timestamp", out _));

        // Absent, not null: a startup line is genuinely outside any run, and a null runId would read
        // in a log aggregator as "a run whose id is unknown", which is a different and false claim.
        Assert.False(record.TryGetProperty("runId", out _));
        Assert.False(record.TryGetProperty("seq", out _));
    }

    [Fact]
    public void TheTimestamp_IsUtcIso8601AndRoundTrips()
    {
        using var stderr = new ConsoleErrorCapture();

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        StructuredLog.Write(LogLevel.Information, "probe");
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        var record = Assert.Single(stderr.JsonLines());
        var raw = record.GetProperty("timestamp").GetString();

        Assert.NotNull(raw);
        Assert.EndsWith("Z", raw, StringComparison.Ordinal);

        var parsed = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void ARunScopedRecord_CarriesTheRunIdAndAMonotonicSeq()
    {
        using var stderr = new ConsoleErrorCapture();

        const string runId = "run-0123456789abcdef0123456789abcdef";
        var scope = StructuredLog.ForRun(runId);

        scope.Write(LogLevel.Information, "run started");
        scope.Write(LogLevel.Information, "run completed");

        var records = stderr.JsonLines();
        Assert.Equal(2, records.Count);

        Assert.All(records, record => Assert.Equal(runId, record.GetProperty("runId").GetString()));

        // Per-run ordinal starting at 1 — a tie-breaker when two records share a millisecond
        // timestamp. NOT an engine event ordinal; see StructuredLog's remarks.
        Assert.Equal(1, records[0].GetProperty("seq").GetInt32());
        Assert.Equal(2, records[1].GetProperty("seq").GetInt32());
    }

    [Fact]
    public void TwoRunScopes_NumberIndependentlyRatherThanSharingAGlobalCounter()
    {
        using var stderr = new ConsoleErrorCapture();

        var first = StructuredLog.ForRun("run-11111111111111111111111111111111");
        var second = StructuredLog.ForRun("run-22222222222222222222222222222222");

        first.Write(LogLevel.Information, "a");
        second.Write(LogLevel.Information, "b");
        first.Write(LogLevel.Information, "c");

        var records = stderr.JsonLines();

        // seq is per-RUN, so the second run's first record is 1, not 2 — otherwise the field would
        // be a global counter wearing a per-run name.
        Assert.Equal(1, records[0].GetProperty("seq").GetInt32());
        Assert.Equal(1, records[1].GetProperty("seq").GetInt32());
        Assert.Equal(2, records[2].GetProperty("seq").GetInt32());
    }

    [Fact]
    public void ARecordIsExactlyOneLine_EvenWhenTheMessageContainsNewlinesAndControlCharacters()
    {
        using var stderr = new ConsoleErrorCapture();

        StructuredLog.Write(LogLevel.Warning, "line one\nline two\r\tand a taband a NUL");

        // One physical line: a message that broke the record across lines would destroy the
        // one-object-per-line contract every log shipper depends on.
        var lines = stderr.Lines();
        Assert.Single(lines);

        var record = Assert.Single(stderr.JsonLines());
        var message = record.GetProperty("message").GetString();

        Assert.NotNull(message);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
    }

    [Fact]
    public void TheLevel_IsRenderedAsAStableLowercaseToken()
    {
        using var stderr = new ConsoleErrorCapture();

        StructuredLog.Write(LogLevel.Information, "i");
        StructuredLog.Write(LogLevel.Warning, "w");
        StructuredLog.Write(LogLevel.Error, "e");

        var levels = stderr.JsonLines().Select(record => record.GetProperty("level").GetString() ?? string.Empty).ToArray();

        // Lower-case and stable: these are grouped on in a log backend, so they are pinned rather
        // than left to follow whatever ToString() the enum happens to have.
        Assert.Equal(["information", "warning", "error"], levels);
    }

    [Fact]
    public void TheFieldSet_IsExactlyTheFiveTheAcNames()
    {
        using var stderr = new ConsoleErrorCapture();

        StructuredLog.ForRun("run-33333333333333333333333333333333")
            .Write(LogLevel.Information, "run started");

        var record = Assert.Single(stderr.JsonLines());

        // EXACT equality — a sixth field would be an unreviewed addition to a surface whose whole
        // point is that its contents are enumerable.
        Assert.Equal(
            ["level", "message", "runId", "seq", "timestamp"],
            record.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
