using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="PathSafetyGuard"/> (M2): a UNC/network path must be rejected before any
/// filesystem call is made against it, while ordinary local paths — including relative traversal
/// — remain allowed.
/// </summary>
public class PathSafetyGuardTests
{
    [Theory]
    [InlineData(@"\\attacker-host\share\suite.e2e.yaml")]
    [InlineData("//attacker-host/share/suite.e2e.yaml")]
    [InlineData(@"\\?\UNC\attacker-host\share\suite.e2e.yaml")]
    public void CheckLocalPath_UncOrNetworkPath_ReturnsInvalidPath(string uncPath)
    {
        var error = PathSafetyGuard.CheckLocalPath(uncPath);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
    }

    [Theory]
    [InlineData("good-suite.e2e.yaml")]
    [InlineData("../fixtures/good-suite.e2e.yaml")]
    [InlineData("../../etc/whatever.e2e.yaml")]
    [InlineData(@"C:\suites\good.e2e.yaml")]
    [InlineData("/home/user/suites/good.e2e.yaml")]
    public void CheckLocalPath_LocalPathIncludingTraversal_ReturnsNull(string localPath)
    {
        // Local traversal is allowed by design — only network locations are blocked.
        Assert.Null(PathSafetyGuard.CheckLocalPath(localPath));
    }

    [Fact]
    public void CheckLocalPath_EmptyPath_ReturnsNull()
    {
        Assert.Null(PathSafetyGuard.CheckLocalPath(string.Empty));
    }
}
