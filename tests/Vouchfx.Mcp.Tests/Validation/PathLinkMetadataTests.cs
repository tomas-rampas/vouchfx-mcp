using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

public class PathLinkMetadataTests
{
    [Fact]
    public void NativeLinkReader_UsesOnlyImmediateMetadata_NotTargetFollowingProbes()
    {
        var path = Path.Combine(SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Validation", "PathSafetyGuard.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        var reader = Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "TryResolveLinkTarget");
        var calls = reader.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        Assert.DoesNotContain(calls, call => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Exists");
        var resolution = Assert.Single(calls, call => call.Expression is MemberAccessExpressionSyntax member
            && member.Name.Identifier.ValueText == "ResolveLinkTarget");

        Assert.Equal(2, resolution.ArgumentList.Arguments.Count);
        Assert.True(resolution.ArgumentList.Arguments[1].Expression.RawKind == (int)SyntaxKind.FalseLiteralExpression,
            "Native metadata must never follow a link before its stored network target is checked.");
    }

    [Theory]
    [InlineData(@"\\never-contact-this-host\share\suite.e2e.yaml")]
    [InlineData("//never-contact-this-host/share/suite.e2e.yaml")]
    public void NetworkInput_DoesNotInvokeTheMetadataReader(string path)
    {
        var calls = 0;

        Assert.Null(PathSafetyGuard.ResolvePathWithMetadata(path, _ => { calls++; return null; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(@"\\never-contact-this-host\share\suite.e2e.yaml")]
    [InlineData("//never-contact-this-host/share/suite.e2e.yaml")]
    [InlineData(@"\\?\UNC\never-contact-this-host\share\suite.e2e.yaml")]
    public void StoredNetworkTarget_IsRefusedBeforeAnyDescendantIsInspected(string networkTarget)
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-model");
        var link = Path.Combine(root, "alias");
        var inspected = new List<string>();

        var resolved = PathSafetyGuard.ResolvePathWithMetadata(Path.Combine(link, "descendant.yaml"), path =>
        {
            inspected.Add(path);
            Assert.False(PathSafetyGuard.IsNetworkPath(path), "Network metadata must never be requested.");
            Assert.DoesNotContain("descendant.yaml", path, StringComparison.Ordinal);
            return path == link ? networkTarget : null;
        });

        Assert.Null(resolved);
        Assert.Equal(link, inspected[^1]);
        Assert.Equal(1, inspected.Count(path => path == link));
    }

    [Fact]
    public void RelativeLinkTargetWithNetworkAncestor_IsRefusedBeforeInspectingItsTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-model");
        var alias = Path.Combine(root, "alias");
        var bridge = Path.Combine(root, "bridge");
        var inspected = new List<string>();

        var resolved = PathSafetyGuard.ResolvePathWithMetadata(Path.Combine(alias, "private.yaml"), path =>
        {
            inspected.Add(path);
            Assert.False(PathSafetyGuard.IsNetworkPath(path));
            Assert.DoesNotContain("private.yaml", path, StringComparison.Ordinal);
            return path == alias ? "bridge" : path == bridge ? @"\\never-contact-this-host\share" : null;
        });

        Assert.Null(resolved);
        Assert.Equal(bridge, inspected[^1]);
        Assert.Contains(alias, inspected);
    }

    [Fact]
    public void RelativeLocalLink_ResolvesAgainstItsParent_AndPreservesTheTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "metadata-model");
        var alias = Path.Combine(root, "alias");
        var real = Path.Combine(root, "real");

        var resolved = PathSafetyGuard.ResolvePathWithMetadata(
            Path.Combine(alias, "suite.yaml"), path => path == alias ? "real" : null);

        Assert.Equal(Path.Combine(real, "suite.yaml"), resolved);
    }

    [Fact]
    public void Cycle_IsRefusedWithoutSpendingTheWholeSegmentBudget()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var a = Path.Combine(root, "metadata-cycle-a");
        var b = Path.Combine(root, "metadata-cycle-b");
        var calls = 0;

        var resolved = PathSafetyGuard.ResolvePathWithMetadata(a, path =>
        {
            calls++;
            return path == a ? b : a;
        });

        Assert.Null(resolved);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void BudgetExhaustion_IsRefusedWithoutInspectingAnAdditionalSegment()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", 300)));
        var calls = 0;

        Assert.Null(PathSafetyGuard.ResolvePathWithMetadata(path, _ => { calls++; return null; }));
        Assert.Equal(256, calls);
    }

    [Fact]
    public void MetadataFailure_PropagatesRatherThanBecomingAnOrdinaryLocalSegment()
    {
        var path = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "metadata-denied");
        Assert.Throws<UnauthorizedAccessException>(() => PathSafetyGuard.ResolvePathWithMetadata(
            path, _ => throw new UnauthorizedAccessException("untrusted target text")));
    }
}
