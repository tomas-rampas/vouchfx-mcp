using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="VouchfxCliProcessRunner"/>'s documented constants. The runner's actual
/// process-spawning behaviour is deliberately NOT exercised here against a real binary — this
/// server's tests never depend on the real <c>vouchfx</c> CLI being installed (see
/// <c>Vouchfx.Mcp.Tests.FakeVouchfxCli</c>) — and the underlying bounded-read mechanism it shares
/// with <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/> via
/// <see cref="Vouchfx.Mcp.BoundedStreamReader"/> is already covered against a real spawned process
/// by that class's own existing test suite.
/// </summary>
public class VouchfxCliProcessRunnerTests
{
    [Fact]
    public void MaxCliOutputBytes_Is64Kilobytes()
    {
        // Security MAJOR 2: documents the exact cap chosen — see the constant's own remarks for
        // why 64 KB (generous over any legitimate --version reply, small relative to
        // ValidationWorkerClient.MaxWorkerOutputBytes because this process's legitimate output is
        // orders of magnitude smaller).
        Assert.Equal(64L * 1024, VouchfxCliProcessRunner.MaxCliOutputBytes);
    }
}
