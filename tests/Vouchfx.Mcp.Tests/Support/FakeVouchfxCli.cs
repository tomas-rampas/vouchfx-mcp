using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A canned <see cref="IVouchfxCli"/> for tests: returns a fixed result instantly, with no process
/// spawned — so no test ever depends on the real <c>vouchfx</c> CLI being installed on the machine
/// running it (REQ-008's whole point in making <see cref="IVouchfxCli"/> an injectable seam).
/// </summary>
internal sealed class FakeVouchfxCli : IVouchfxCli
{
    private readonly string? _rawVersionOutput;

    private FakeVouchfxCli(string? rawVersionOutput) => _rawVersionOutput = rawVersionOutput;

    /// <summary>A fake reporting the CLI is not installed (mirrors a launch failure / not on PATH).</summary>
    public static FakeVouchfxCli NotFound() => new(null);

    /// <summary>A fake reporting <paramref name="rawVersionOutput"/> verbatim, as if it were the CLI's own stdout text.</summary>
    public static FakeVouchfxCli ReportingVersion(string rawVersionOutput) => new(rawVersionOutput);

    public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_rawVersionOutput);
}
