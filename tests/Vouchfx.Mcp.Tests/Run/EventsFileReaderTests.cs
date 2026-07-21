using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>Covers <see cref="EventsFileReader"/> directly — the bounded-read boundary shared by <c>run_suite</c> and <c>explain_run</c>.</summary>
public class EventsFileReaderTests
{
    [Fact]
    public async Task TryReadBoundedAsync_MissingFile_ReturnsNullContentWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"events-file-reader-test-{Guid.NewGuid():N}.jsonl");

        var (content, truncated) = await EventsFileReader.TryReadBoundedAsync(missingPath, CancellationToken.None);

        Assert.Null(content);
        Assert.False(truncated);
    }

    [Fact]
    public async Task TryReadBoundedAsync_PlatformNullDevice_ReturnsNullContentWithoutThrowing()
    {
        // A review-found MAJOR: a Windows reserved device name (NUL, CON, COM1, ...) opens
        // successfully but produces a NON-SEEKABLE stream, and Stream.Length's own documented
        // contract permits NotSupportedException for exactly such a stream — which was not caught,
        // bypassing the graceful "could not be read" result an agent-supplied eventsPath is
        // otherwise always resolved to. Empirically confirmed on this Windows machine:
        // new FileStream("NUL", ...).CanSeek is false. On Linux, .NET's Unix FileStream
        // implementation reports CanSeek = false for a character device (/dev/null included) the
        // same way, via the SAME CanSeek check this fix added — so this test exercises the identical
        // code path on both platforms, regardless of whether the underlying platform's Length call
        // itself throws, returns 0, or something else: the FIX's own observable contract (never
        // throw; resolve to a clean "could not be read" result) is what is verified here, not a
        // specific platform's exception behaviour.
        var nullDevicePath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        var (content, truncated) = await EventsFileReader.TryReadBoundedAsync(nullDevicePath, CancellationToken.None);

        Assert.Null(content);
        Assert.False(truncated);
    }
}
