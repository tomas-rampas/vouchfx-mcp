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
    public async Task TryReadBoundedAsync_PlatformNullDevice_DoesNotThrowAndReturnsNoUsableContent()
    {
        // A review-found MAJOR: a Windows reserved device name (NUL, CON, COM1, ...) opens
        // successfully but produces a NON-SEEKABLE stream, and Stream.Length's own documented
        // contract permits NotSupportedException for exactly such a stream — which was not caught,
        // bypassing the graceful "could not be read" result an agent-supplied eventsPath is
        // otherwise always resolved to. The CanSeek guard added to fix that is exercised on BOTH
        // platforms here, but its OBSERVABLE OUTCOME genuinely differs between them — confirmed by a
        // real Linux CI failure, not assumed:
        //   - Windows: "NUL" opens with CanSeek = false (empirically confirmed on a real Windows
        //     machine) -> the CanSeek guard fires -> Content is null.
        //   - Linux: "/dev/null" is a READABLE EMPTY character device -> CanSeek is apparently true
        //     enough (or Length/the read loop resolves it) for the normal read path to run to
        //     completion, reading zero bytes -> Content is "" (empty), not null.
        // Neither platform throws or crashes — that is the fix's actual guarantee, and the one this
        // test asserts: the call never throws, and degrades to NO USABLE CONTENT (null OR empty)
        // either way. Asserting a specific one of the two would be asserting an implementation
        // detail of a platform's character-device handling, not the contract this type promises.
        var nullDevicePath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        var (content, truncated) = await EventsFileReader.TryReadBoundedAsync(nullDevicePath, CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(content), $"Expected null or empty content, got '{content}'.");
        Assert.False(truncated);
    }
}
