using System.Diagnostics;
using System.Globalization;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="VouchfxCliProcessRunner"/>'s constants and bounded pipe lifetime through a
/// real child-process fixture. The fixture holds inherited output handles after its parent exits;
/// no test here depends on an installed <c>vouchfx</c> CLI or Docker.
/// </summary>
public class VouchfxCliProcessRunnerTests
{
    [Fact]
    public async Task RunAgainstProcessAsync_ExitedParentWithInheritedOutputHandles_StillHonoursTimeout()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"vouchfx-held-pipes-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo
        {
            FileName = VouchfxCliPathResolver.ResolveAbsolutePath("dotnet")!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(RepoLayout.ResolveStdinEofChildFixtureDllPath());
        startInfo.ArgumentList.Add("hold-output");
        startInfo.ArgumentList.Add(marker);
        using var process = Process.Start(startInfo)!;
        Task<CliInvocationResult>? invocation = null;
        try
        {
            invocation = VouchfxCliProcessRunner.RunAgainstProcessAsync(process, 1024, TimeSpan.FromSeconds(2));
            using var exitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(exitBudget.Token);
            Assert.True(File.Exists(marker), "The descendant must start before the parent exits.");

            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CliLaunchFailureReason.TimedOut, result.FailureReason);
        }
        finally
        {
            if (File.Exists(marker))
            {
                var id = int.Parse(await File.ReadAllTextAsync(marker), CultureInfo.InvariantCulture);
                using var child = Process.GetProcessById(id);
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(cleanup.Token);
                File.Delete(marker);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            if (invocation is not null)
            {
                await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

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
