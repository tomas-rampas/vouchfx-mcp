using ModelContextProtocol;
using Vouchfx.Mcp.Resources;

namespace Vouchfx.Mcp.Tests.Resources;

/// <summary>
/// US-S5-01 AC-008: every <c>vouchfx://</c> URI-template argument goes through the same rejection
/// <c>PathSafetyGuard</c> already applies to a tool's path argument — reused, not reinvented.
/// </summary>
/// <remarks>
/// The threat these cases defend against is stated fully in <see cref="ResourceArgumentGuard"/>'s own
/// header: a <c>{runId}</c> expanded to <c>\\attacker\share</c> reaches a file-backed run registry
/// that composes a directory path from a run id, and merely PROBING a UNC path on Windows triggers an
/// outbound SMB/NTLM authentication to the named host. Refusing the shape before any lookup is the
/// same fail-closed ordering <c>Workspace.Resolve</c> applies to <c>--workspace</c>.
/// </remarks>
public class ResourceArgumentGuardTests
{
    [Theory]
    [InlineData("run-42")]
    [InlineData("v1")]
    [InlineData("latest")]
    [InlineData("http-smoke")]
    [InlineData("orders-api")]
    [InlineData("a")]
    [InlineData("run..2026")] // NOT a traversal — see ContainsTraversalSegment's remarks.
    public void AnOrdinaryFlatIdentifier_IsAccepted(string value) =>
        Assert.Equal(value, ResourceArgumentGuard.Require(value, "runId"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankArgument_IsRefused(string? value)
    {
        var ex = Assert.Throws<McpException>(() => ResourceArgumentGuard.Require(value, "runId"));
        Assert.Contains("{runId}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Both slash directions, because both are accepted by Windows' path APIs and both trigger the
    // outbound connection — the same pair PathSafetyGuard.IsNetworkPath tests for.
    [InlineData(@"\\attacker\share")]
    [InlineData("//attacker/share")]
    [InlineData(@"\\?\UNC\attacker\share")]
    [InlineData(@"\\.\pipe\anything")]
    public void ANetworkShapedArgument_IsRefused(string value)
    {
        var ex = Assert.Throws<McpException>(() => ResourceArgumentGuard.Require(value, "runId"));

        // Refused for a REASON the caller can act on, and refused before anything resolves it.
        Assert.Contains("network/UNC", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData(@"..\secrets")]
    [InlineData("nested/run-42")]
    [InlineData(@"nested\run-42")]
    [InlineData("..")]
    [InlineData(".")]
    public void APathShapedArgument_IsRefused(string value)
    {
        var ex = Assert.Throws<McpException>(() => ResourceArgumentGuard.Require(value, "container"));

        Assert.Contains("{container}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("flat identifier", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsurdlyLongArgument_IsRefusedBeforeAnythingWalksIt()
    {
        var ex = Assert.Throws<McpException>(
            () => ResourceArgumentGuard.Require(new string('a', ResourceArgumentGuard.MaxArgumentChars + 1), "name"));

        Assert.Contains(
            ResourceArgumentGuard.MaxArgumentChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnArgumentExactlyAtTheBound_IsAccepted() =>
        Assert.Equal(
            ResourceArgumentGuard.MaxArgumentChars,
            ResourceArgumentGuard.Require(new string('a', ResourceArgumentGuard.MaxArgumentChars), "name").Length);

    [Fact]
    public void ARefusalNeverEchoesAControlCharacterBack()
    {
        // The message is composed from caller-supplied text on its way to a host that may render it
        // in a terminal — the same reason every VFX-* message goes through VfxCode.SanitiseForEcho.
        const char escape = (char)27;
        var hostile = $"run{escape}[31m-42/x";

        var ex = Assert.Throws<McpException>(() => ResourceArgumentGuard.Require(hostile, "runId"));

        Assert.DoesNotContain(escape.ToString(), ex.Message, StringComparison.Ordinal);
    }
}
