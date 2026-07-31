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

    [Fact]
    public void MaxListJsonOutputBytes_IsOneMegabyte()
    {
        Assert.Equal(1L * 1024 * 1024, VouchfxCliProcessRunner.MaxListJsonOutputBytes);
    }

    [Fact]
    public void MaxSchemaOutputBytes_IsTwoMegabytes()
    {
        Assert.Equal(2L * 1024 * 1024, VouchfxCliProcessRunner.MaxSchemaOutputBytes);
    }

    [Fact]
    public void MaxScaffoldOutputBytes_IsOneMegabyte()
    {
        Assert.Equal(1L * 1024 * 1024, VouchfxCliProcessRunner.MaxScaffoldOutputBytes);
    }

    [Fact]
    public void MaxPlanOutputBytes_IsFourMegabytes()
    {
        Assert.Equal(4L * 1024 * 1024, VouchfxCliProcessRunner.MaxPlanOutputBytes);
    }

    [Fact]
    public void DefaultTimeout_IsFifteenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), VouchfxCliProcessRunner.DefaultTimeout);
    }

    [Fact]
    public void PlanTimeout_IsSixtySeconds()
    {
        // 4x DefaultTimeout, mirroring MaxPlanOutputBytes's own 4x-larger-than-MaxScaffoldOutputBytes
        // proportion — see PlanTimeout's own remarks for the full rationale.
        Assert.Equal(TimeSpan.FromSeconds(60), VouchfxCliProcessRunner.PlanTimeout);
        Assert.Equal(VouchfxCliProcessRunner.DefaultTimeout * 4, VouchfxCliProcessRunner.PlanTimeout);
    }
}
