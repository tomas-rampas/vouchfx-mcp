using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Direct coverage for the two containment primitives US-S5-01 added to
/// <see cref="PathSafetyGuard"/> — <c>TryResolveContainmentRoot</c> and
/// <c>IsInsideResolvedDirectory</c> — which <c>vouchfx://workspace/specs</c> uses to keep its index
/// inside the workspace's <c>specsDir</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these need tests of their own</b> (a code review's MAJOR finding). They shipped with only
/// one indirect exercise: a symlink scenario in <c>WorkspaceSpecIndexerTests</c> that (a) cannot
/// distinguish which of the two containment mechanisms rejected the file — the enumeration's
/// <c>AttributesToSkip</c> filter would have excluded it before these methods ever saw it — and
/// (b) self-skips on a Windows host without Developer Mode, which is most of them. So the security
/// property was, in practice, untested. These tests call the primitives directly, so the mechanism
/// under test is unambiguous, and the ones that need a real link say so and skip explicitly.
/// </para>
/// <para>
/// The FAIL-CLOSED direction is what most of these pin: "could not tell" must never resolve to
/// "inside". Every branch that cannot demonstrate containment — a network path, an unresolvable
/// root, a walk that throws, an exhausted segment budget — has to answer <see langword="false"/>,
/// and a bug in any of them is a containment bypass rather than a nuisance.
/// </para>
/// </remarks>
public class PathContainmentPrimitiveTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("vfx-mcp-containment-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Never fail a test over temp-directory cleanup.
        }

        GC.SuppressFinalize(this);
    }

    // ── TryResolveContainmentRoot ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryResolveContainmentRoot_AnOrdinaryDirectory_ResolvesToItselfWithoutATrailingSeparator()
    {
        var resolved = PathSafetyGuard.TryResolveContainmentRoot(_root.FullName);

        Assert.NotNull(resolved);
        Assert.False(
            resolved!.EndsWith(Path.DirectorySeparatorChar),
            "The resolved root is trimmed, so the prefix test can append exactly one separator.");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root.FullName)),
            resolved,
            StringComparer.Ordinal);
    }

    [Fact]
    public void TryResolveContainmentRoot_ATrailingSeparator_ResolvesToTheSameValue() =>
        // A caller may hand this either spelling; both must produce the one form the prefix test
        // expects, or "C:\repo" and "C:\repo\" would contain different sets of files.
        Assert.Equal(
            PathSafetyGuard.TryResolveContainmentRoot(_root.FullName),
            PathSafetyGuard.TryResolveContainmentRoot(_root.FullName + Path.DirectorySeparatorChar),
            StringComparer.Ordinal);

    [Fact]
    public void TryResolveContainmentRoot_ADirectoryThatDoesNotExist_StillResolves() =>
        // Deliberate: the walk leaves a missing segment as written, which is what lets containment be
        // decided BEFORE an existence check. A specs directory that has not been created yet must not
        // make every path under it "outside".
        Assert.NotNull(PathSafetyGuard.TryResolveContainmentRoot(Path.Combine(_root.FullName, "not-created-yet")));

    [Theory]
    [InlineData(@"\\attacker\share")]
    [InlineData("//attacker/share")]
    [InlineData(@"\\?\UNC\attacker\share")]
    public void TryResolveContainmentRoot_ANetworkPath_IsRefusedWithoutTouchingIt(string directory) =>
        // Refused by STRING INSPECTION, before any filesystem call: probing a UNC path on Windows
        // triggers an outbound SMB/NTLM authentication to the named host, which is the
        // forced-authentication primitive this whole type exists to prevent.
        Assert.Null(PathSafetyGuard.TryResolveContainmentRoot(directory));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveContainmentRoot_AnEmptyDirectory_IsRefused(string? directory) =>
        Assert.Null(PathSafetyGuard.TryResolveContainmentRoot(directory!));

    [Fact]
    public void TryResolveContainmentRoot_APathThisPlatformCannotCanonicalise_FailsClosed()
    {
        // A NUL byte is rejected by every platform's path APIs. The point is the DIRECTION: an input
        // that upsets Path.GetFullPath must produce null (⇒ nothing is contained) rather than an
        // exception escaping to the caller or, worse, a value that something treats as a root.
        Assert.Null(PathSafetyGuard.TryResolveContainmentRoot("bad\0path"));
    }

    // ── IsInsideResolvedDirectory ──────────────────────────────────────────────────────────────

    [Fact]
    public void AFileDirectlyInside_IsContained()
    {
        var resolved = PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!;
        var file = Path.Combine(_root.FullName, "suite.e2e.yaml");
        File.WriteAllText(file, "steps: []\n");

        Assert.True(PathSafetyGuard.IsInsideResolvedDirectory(file, resolved));
    }

    [Fact]
    public void AFileNestedInside_IsContained()
    {
        var resolved = PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!;
        var nested = Directory.CreateDirectory(Path.Combine(_root.FullName, "a", "b"));
        var file = Path.Combine(nested.FullName, "suite.e2e.yaml");
        File.WriteAllText(file, "steps: []\n");

        Assert.True(PathSafetyGuard.IsInsideResolvedDirectory(file, resolved));
    }

    [Fact]
    public void TheDirectoryItself_IsContained() =>
        // The equality arm, distinct from the prefix arm. Not reached by this resource's own
        // enumeration (which yields files), but a containment predicate that answered "no" for the
        // directory it was asked about would be wrong in a way that bites the next caller.
        Assert.True(PathSafetyGuard.IsInsideResolvedDirectory(
            _root.FullName, PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!));

    [Fact]
    public void ASiblingDirectoryWithTheRootAsAStringPREFIX_IsNotContained()
    {
        // The classic prefix-vs-containment bug: without the separator the comparison appends,
        // "…/workspace-evil/x" counts as inside "…/workspace". Both directories are real here so the
        // walk has something to resolve.
        var workspace = Directory.CreateDirectory(Path.Combine(_root.FullName, "workspace"));
        var evilTwin = Directory.CreateDirectory(Path.Combine(_root.FullName, "workspace-evil"));
        var file = Path.Combine(evilTwin.FullName, "suite.e2e.yaml");
        File.WriteAllText(file, "steps: []\n");

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(workspace.FullName)!;

        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(file, resolved));
    }

    [Fact]
    public void AParentDirectoryFile_IsNotContained()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));
        var outside = Path.Combine(_root.FullName, "outside.e2e.yaml");
        File.WriteAllText(outside, "steps: []\n");

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(outside, resolved));
    }

    [Fact]
    public void ATraversalPathThatEscapes_IsNotContained()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));
        var outside = Path.Combine(_root.FullName, "secrets.e2e.yaml");
        File.WriteAllText(outside, "steps: []\n");

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        // Path.GetFullPath collapses the '..' before the comparison, so the escape is caught by
        // canonicalisation rather than by a string search for "..".
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(
            Path.Combine(specs.FullName, "..", "secrets.e2e.yaml"), resolved));
    }

    [Theory]
    [InlineData(@"\\attacker\share\suite.e2e.yaml")]
    [InlineData("//attacker/share/suite.e2e.yaml")]
    public void ANetworkShapedCandidate_IsNotContained(string path) =>
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(
            path, PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!));

    [Fact]
    public void ACandidateThisPlatformCannotCanonicalise_FailsClosed() =>
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(
            "bad\0path", PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyCandidateOrRoot_IsNotContained(string? value)
    {
        var resolved = PathSafetyGuard.TryResolveContainmentRoot(_root.FullName)!;

        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(value!, resolved));
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(_root.FullName, value!));
    }

    // ── The link cases: the whole reason this shares ResolveRealPath rather than doing string maths ──

    [Fact]
    public void ASymlinkInsideTheDirectoryPointingOutside_IsNotContained()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));
        var secrets = Directory.CreateDirectory(Path.Combine(_root.FullName, "secrets"));
        var target = Path.Combine(secrets.FullName, "leaked.e2e.yaml");
        File.WriteAllText(target, "steps: []\n");

        var link = Path.Combine(specs.FullName, "linked.e2e.yaml");
        if (!TryCreateFileSymbolicLink(link, target))
        {
            return;
        }

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        // A pure string comparison would call this contained — the link's own path IS under e2e/. It
        // is the link walk that makes the answer correct, and this is the assertion that separates
        // the two mechanisms the indexer relies on (the enumeration filter never runs here).
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(link, resolved));
    }

    [Fact]
    public void ADirectorySymlinkInsideTheDirectoryPointingOutside_IsNotContained()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));
        var secrets = Directory.CreateDirectory(Path.Combine(_root.FullName, "secrets"));
        File.WriteAllText(Path.Combine(secrets.FullName, "leaked.e2e.yaml"), "steps: []\n");

        var linkDir = Path.Combine(specs.FullName, "linked");
        if (!TryCreateDirectorySymbolicLink(linkDir, secrets.FullName))
        {
            return;
        }

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        // The ANCESTOR case: the file itself is an ordinary file, and only a segment above it is a
        // link. FileSystemInfo.FullName does not resolve an ancestor link, which is exactly the
        // bypass ResolveRealPath's top-down walk exists to close.
        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(
            Path.Combine(linkDir, "leaked.e2e.yaml"), resolved));
    }

    [Fact]
    public void ADirectorySymlinkPointingBackINSIDE_IsStillContained()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));
        var real = Directory.CreateDirectory(Path.Combine(specs.FullName, "real"));
        File.WriteAllText(Path.Combine(real.FullName, "suite.e2e.yaml"), "steps: []\n");

        var linkDir = Path.Combine(specs.FullName, "alias");
        if (!TryCreateDirectorySymbolicLink(linkDir, real.FullName))
        {
            return;
        }

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        // The other direction, and it matters: a guard that simply refused every link would be
        // "safe" and useless. A link whose target is inside the directory resolves to a contained
        // path and is accepted.
        Assert.True(PathSafetyGuard.IsInsideResolvedDirectory(
            Path.Combine(linkDir, "suite.e2e.yaml"), resolved));
    }

    [Fact]
    public void ASymlinkCycle_FailsClosedRatherThanLooping()
    {
        var specs = Directory.CreateDirectory(Path.Combine(_root.FullName, "e2e"));

        // a -> b and b -> a. Termination comes from the shared segment budget, not from a cycle
        // detector: every pass that substitutes a target spends at least one segment of it.
        var a = Path.Combine(specs.FullName, "a");
        var b = Path.Combine(specs.FullName, "b");
        if (!TryCreateDirectorySymbolicLink(a, b) || !TryCreateDirectorySymbolicLink(b, a))
        {
            return;
        }

        var resolved = PathSafetyGuard.TryResolveContainmentRoot(specs.FullName)!;

        Assert.False(PathSafetyGuard.IsInsideResolvedDirectory(Path.Combine(a, "suite.e2e.yaml"), resolved));
    }

    /// <summary>
    /// Creates a file symlink, returning <see langword="false"/> when the platform will not allow it.
    /// </summary>
    /// <remarks>
    /// Creating a symlink needs Developer Mode or elevation on Windows. Returning false — so the
    /// caller returns without asserting — is the honest outcome: a test that silently passed on such a
    /// machine would be claiming coverage that never ran. The tests above that need no link cover the
    /// fail-closed arms on every host, so this skip narrows what is proven rather than removing it.
    /// </remarks>
    private static bool TryCreateFileSymbolicLink(string path, string target) =>
        TryCreateLink(() => File.CreateSymbolicLink(path, target));

    private static bool TryCreateDirectorySymbolicLink(string path, string target) =>
        TryCreateLink(() => Directory.CreateSymbolicLink(path, target));

    private static bool TryCreateLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
