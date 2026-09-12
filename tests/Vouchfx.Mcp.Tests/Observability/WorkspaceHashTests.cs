using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Tests.Observability;

/// <summary>Mirror-namespace unit tests for US-S6-04's <c>workspace.hash</c> derivation.</summary>
public class WorkspaceHashTests
{
    [Fact]
    public void NoWorkspace_YieldsNull_SoTheAttributeIsOmittedRatherThanInvented()
    {
        // A server launched without --workspace has no resolved root. Hashing the process directory
        // instead would put a path-derived value on every span that corresponds to nothing the host
        // chose, which is worse than saying nothing.
        Assert.Null(WorkspaceHash.Of(null));
    }

    [Fact]
    public void TheSameRoot_AlwaysHashesIdentically()
    {
        // The whole value of the attribute is correlation across calls and across restarts, so this
        // is the property being paid for — and the reason a per-process salt is deliberately absent
        // (see WorkspaceHash's remarks).
        const string root = @"C:\src\github\example-project";

        Assert.Equal(WorkspaceHash.OfRoot(root), WorkspaceHash.OfRoot(root));
    }

    [Fact]
    public void DifferentRoots_HashDifferently()
    {
        Assert.NotEqual(
            WorkspaceHash.OfRoot(@"C:\src\project-a"),
            WorkspaceHash.OfRoot(@"C:\src\project-b"));
    }

    [Fact]
    public void TheHash_IsSixteenLowercaseHexCharacters()
    {
        var hash = WorkspaceHash.OfRoot("/home/dev/work/orders-service");

        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9a-f]{16}$", hash);
    }

    [Fact]
    public void TheHash_ContainsNoFragmentOfThePathItWasDerivedFrom()
    {
        // The point of the attribute: a person reading a trace cannot recover the directory layout.
        // (Not a secret-strength claim — see WorkspaceHash's remarks on low-entropy inputs.)
        const string root = @"C:\clients\acme-corp\secret-project";

        var hash = WorkspaceHash.OfRoot(root);

        foreach (var segment in new[] { "clients", "acme", "acme-corp", "secret", "secret-project", "C:" })
        {
            Assert.DoesNotContain(segment, hash, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(Path.DirectorySeparatorChar, hash);
        Assert.DoesNotContain('/', hash);
    }

    [Fact]
    public void AResolvedWorkspace_HashesItsRootRatherThanAnyOtherOfItsDirectories()
    {
        using var temp = new TempWorkspace();

        // Root, never SpecsDir or OutputDir: those are derived, and two servers differing only in
        // output layout must still correlate as the same workspace.
        Assert.Equal(WorkspaceHash.OfRoot(temp.Workspace.Root), WorkspaceHash.Of(temp.Workspace));
    }

    [Fact]
    public void CaseIsPreserved_NotNormalised()
    {
        // Documented trade-off (see WorkspaceHash's remarks): lower-casing would silently merge two
        // genuinely distinct roots on a case-sensitive filesystem. The cost of preserving case is a
        // split series on Windows if a host is launched with differently-cased paths; the cost of
        // normalising is a wrong answer on Linux.
        Assert.NotEqual(WorkspaceHash.OfRoot("/src/Project"), WorkspaceHash.OfRoot("/src/project"));
    }
}
