using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="PathSafetyGuard"/>: the unconditional UNC/network rejection (M2), and
/// US-S3-08's workspace-gated containment — including plan §7's "path-containment escape tests".
/// </summary>
/// <remarks>
/// <b>The compatibility half matters as much as the containment half.</b> Plan §2.1 is explicit that
/// containment is NEW POLICY rather than a bug fix: local <c>../</c> traversal was allowed here on
/// purpose, so the no-workspace tests below are not leftovers to be tightened later — they pin the
/// contract that a caller who never passed <c>--workspace</c> sees no new rejection.
/// </remarks>
public class PathSafetyGuardTests : IDisposable
{
    private readonly string _root;
    private readonly Workspace _workspace;

    public PathSafetyGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-guard-tests-" + Guid.NewGuid().ToString("N"), "workspace-a");
        Directory.CreateDirectory(_root);
        _workspace = Workspace.Resolve(_root);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    // ── The unconditional UNC rejection (M2) ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"\\attacker-host\share\suite.e2e.yaml")]
    [InlineData("//attacker-host/share/suite.e2e.yaml")]
    [InlineData(@"\\?\UNC\attacker-host\share\suite.e2e.yaml")]
    public void CheckLocalPath_UncOrNetworkPath_ReturnsInvalidPath(string uncPath)
    {
        var error = PathSafetyGuard.CheckLocalPath(uncPath, workspace: null);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
    }

    /// <summary>
    /// US-S3-08's fourth scenario: "UNC paths are still rejected regardless of workspace
    /// configuration". Its threat model is SMB/NTLM credential leakage, which has nothing to do with
    /// whether a root was configured, so this story neither gated it nor weakened it.
    /// </summary>
    [Theory]
    [InlineData(@"\\attacker-host\share\suite.e2e.yaml")]
    [InlineData("//attacker-host/share/suite.e2e.yaml")]
    [InlineData(@"\\?\UNC\attacker-host\share\suite.e2e.yaml")]
    public void CheckLocalPath_UncPath_IsRejectedInBothWorkspaceModes(string uncPath)
    {
        var withoutWorkspace = PathSafetyGuard.CheckLocalPath(uncPath, workspace: null);
        var withWorkspace = PathSafetyGuard.CheckLocalPath(uncPath, _workspace);

        Assert.NotNull(withoutWorkspace);
        Assert.NotNull(withWorkspace);
        Assert.Equal("VFX-E-1001", withWorkspace!.Code);

        // Byte-identical message: the UNC arm is untouched by this story, not merely still present.
        Assert.Equal(withoutWorkspace!.Message, withWorkspace.Message);
        Assert.Contains("network/UNC", withWorkspace.Message, StringComparison.Ordinal);
    }

    // ── No workspace configured: arbitrary local paths still remain allowed ─────────────────────

    [Theory]
    [InlineData("good-suite.e2e.yaml")]
    [InlineData("../fixtures/good-suite.e2e.yaml")]
    [InlineData("../../etc/whatever.e2e.yaml")]
    [InlineData(@"C:\suites\good.e2e.yaml")]
    [InlineData("/home/user/suites/good.e2e.yaml")]
    public void CheckLocalPath_LocalPathIncludingTraversal_ReturnsNull(string localPath)
    {
        // Local traversal is allowed by design — only network locations are blocked.
        Assert.Null(PathSafetyGuard.CheckLocalPath(localPath, workspace: null));
    }

    /// <summary>
    /// US-S3-08's third scenario: "No --workspace configured — existing absolute-path behaviour is
    /// unchanged". The very input the containment test below REJECTS is accepted here, which is the
    /// whole compatibility claim expressed as one pair of assertions.
    /// </summary>
    [Fact]
    public void CheckLocalPath_EscapingPath_IsAcceptedWhenNoWorkspaceIsConfigured()
    {
        var escaping = Path.Combine(_root, "..", "workspace-b", "secret.e2e.yaml");

        Assert.Null(PathSafetyGuard.CheckLocalPath(escaping, workspace: null));
        Assert.NotNull(PathSafetyGuard.CheckLocalPath(escaping, _workspace));
    }

    [Fact]
    public void CheckLocalPath_EmptyPath_ReturnsNull()
    {
        Assert.Null(PathSafetyGuard.CheckLocalPath(string.Empty, workspace: null));
    }

    [Fact]
    public void CheckLocalPath_EmptyPath_ReturnsNullEvenWithAWorkspaceConfigured()
    {
        // An empty path is not a containment question — it is left to fail on its own terms at the
        // read, exactly as it always has. Path.GetFullPath would throw on it.
        Assert.Null(PathSafetyGuard.CheckLocalPath(string.Empty, _workspace));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(64, true)]
    public void CheckLocalPath_UnusableSuffix_RespectsTheWholeRequestBudget(int descendants, bool expectRefusal)
    {
        var ancestor = _root;
        for (var i = 0; i < 24; i++)
        {
            ancestor = Directory.CreateDirectory(Path.Combine(ancestor, "local" + i)).FullName;
        }

        // Only local, existing ancestors are created. This oversized component cannot name an
        // entry; recovery must preserve the ordinary reader error when the ancestor checks fit,
        // but refuse if repeated recovery across many descendants exhausts the request budget.
        var path = Path.Combine(ancestor, new string('p', 300));
        for (var i = 0; i < descendants; i++)
        {
            path = Path.Combine(path, "tail" + i);
        }

        var error = PathSafetyGuard.CheckLocalPath(path, workspace: null);

        if (expectRefusal)
        {
            Assert.NotNull(error);
            Assert.Equal("VFX-E-1001", error.Code);
            Assert.DoesNotContain(_root, error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(error);
        }
    }

    [ContainmentLinkFact]
    [Trait("containment", "link")]
    public void CheckLocalPath_LocalLinkToNetworkTarget_IsRejectedWithoutAWorkspace()
    {
        const string target = @"\\vouchfx-containment-no-network\share\suite.e2e.yaml";
        var link = Path.Combine(_root, "network-target.e2e.yaml");
        File.CreateSymbolicLink(link, target);

        // Inspect only the local link's stored metadata. Never open or probe its remote target.
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        var error = PathSafetyGuard.CheckLocalPath(link, workspace: null);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error.Code);
        Assert.Contains("network/UNC", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [ContainmentLinkTheory]
    [Trait("containment", "link")]
    [InlineData(300)]
    [InlineData(200_000)]
    public void CheckLocalPath_UnusableDescendant_DoesNotHideANetworkAncestor(int nameLength)
    {
        const string target = @"\\vouchfx-containment-no-network\share";
        var link = Path.Combine(_root, "network-directory");
        Directory.CreateSymbolicLink(link, target);
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);

        // Both a component rejected by native metadata and a path rejected by canonicalisation
        // must inspect the preceding local link. Never open or probe the stored network target.
        var error = PathSafetyGuard.CheckLocalPath(Path.Combine(link, new string('p', nameLength)), workspace: null);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error.Code);
        Assert.DoesNotContain(link, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(target, error.Message, StringComparison.Ordinal);
    }

    // ── Workspace configured: containment (plan §7 escape tests) ────────────────────────────────

    /// <summary>
    /// US-S3-08's second scenario: "A path escaping the configured workspace root is rejected".
    /// </summary>
    [Fact]
    public void CheckLocalPath_TraversalEscapingTheRoot_ReturnsPathOutsideWorkspace()
    {
        var escaping = Path.Combine(_root, "..", "workspace-b", "secret.e2e.yaml");

        var error = PathSafetyGuard.CheckLocalPath(escaping, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
        Assert.Contains("outside the configured workspace root", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("suite.e2e.yaml")]
    [InlineData("nested/deeper/suite.e2e.yaml")]
    // Collapses back INSIDE the root: a `..` segment is not itself an escape.
    [InlineData("nested/../suite.e2e.yaml")]
    public void CheckLocalPath_PathInsideTheRoot_ReturnsNull(string relative)
    {
        var inside = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Null(PathSafetyGuard.CheckLocalPath(inside, _workspace));
    }

    [Fact]
    public void CheckLocalPath_TheRootItself_ReturnsNull()
    {
        Assert.Null(PathSafetyGuard.CheckLocalPath(_root, _workspace));
    }

    /// <summary>
    /// The root's link walk is memoised per <see cref="Workspace"/> instance (a peer review's MINOR
    /// finding: it was re-run on every containment check). This pins the two properties that
    /// memoisation must not disturb — repeated checks still agree, and a SECOND workspace resolved
    /// from the same root agrees with the first — plus the reason the cache is keyed on the instance
    /// rather than stored as a field on the record: a private field would join the record's
    /// synthesised structural equality and make two workspaces on one root compare unequal.
    /// </summary>
    [Fact]
    public void CheckLocalPath_RepeatedChecks_AgreeAndLeaveWorkspaceEqualityIntact()
    {
        var inside = Path.Combine(_root, "suite.e2e.yaml");
        var outside = Path.Combine(_root, "..", "workspace-b", "secret.e2e.yaml");

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(PathSafetyGuard.CheckLocalPath(inside, _workspace));
            Assert.NotNull(PathSafetyGuard.CheckLocalPath(outside, _workspace));
        }

        var second = Workspace.Resolve(_root);

        Assert.Null(PathSafetyGuard.CheckLocalPath(inside, second));
        Assert.NotNull(PathSafetyGuard.CheckLocalPath(outside, second));

        Assert.Equal(_workspace, second);
    }

    [Fact]
    public void CheckLocalPath_SiblingDirectoryWithTheRootAsANamePrefix_IsRejected()
    {
        // "…/workspace-a-evil" starts with "…/workspace-a" as a STRING but is not inside it. The
        // separator appended before the prefix comparison is what makes this a rejection.
        var sibling = _root + "-evil" + Path.DirectorySeparatorChar + "suite.e2e.yaml";

        var error = PathSafetyGuard.CheckLocalPath(sibling, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
    }

    [Fact]
    public void CheckLocalPath_PathThatDoesNotExistYetButIsInside_ReturnsNull()
    {
        // Containment runs BEFORE the existence check, so a not-yet-created file inside the root must
        // pass — otherwise the guard would turn every missing-file case into a containment error.
        var missing = Path.Combine(_root, "never", "created", "suite.e2e.yaml");

        Assert.Null(PathSafetyGuard.CheckLocalPath(missing, _workspace));
    }

    /// <summary>
    /// An ancestor link inside the root points outside it. Link prerequisites are checked at
    /// discovery; required CI mode executes this assertion and fails missing capabilities.
    /// </summary>
    [ContainmentLinkFact]
    [Trait("containment", "link")]
    public void CheckLocalPath_SymlinkInsideTheRootPointingOutside_IsRejected()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "workspace-b");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.e2e.yaml"), "steps: []");

        var link = Path.Combine(_root, "escape-hatch");
        Directory.CreateSymbolicLink(link, outside);

        var throughTheLink = Path.Combine(link, "secret.e2e.yaml");

        // Path.GetFullPath alone cannot see through this: the string has no `..` to collapse and
        // lands squarely under the root. Only the link resolution catches it.
        Assert.StartsWith(_root, Path.GetFullPath(throughTheLink), StringComparison.Ordinal);

        var error = PathSafetyGuard.CheckLocalPath(throughTheLink, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
        Assert.Contains("outside the configured workspace root", error.Message, StringComparison.Ordinal);
    }

    [ContainmentLinkFact]
    [Trait("containment", "link")]
    public void CheckLocalPath_SymlinkInsideTheRootPointingBackInside_ReturnsNull()
    {
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);

        var link = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(link, target);

        // The anti-vacuity twin of the escape test: link resolution must not turn a legitimately
        // contained path into a false rejection.
        Assert.Null(PathSafetyGuard.CheckLocalPath(Path.Combine(link, "suite.e2e.yaml"), _workspace));
    }

    // ── The display-path override ExplainRunOrchestrator relies on ─────────────────────────────

    [Fact]
    public void CheckLocalPath_DisplayPathSupplied_IsWhatTheMessageQuotes()
    {
        // ExplainRunOrchestrator caps the path before display so a huge path argument cannot inflate
        // the error response. Passing that capped rendering in is what lets it reuse this guard's
        // wording instead of maintaining a second copy of it.
        var escaping = Path.Combine(_root, "..", "workspace-b", "secret.e2e.yaml");

        var error = PathSafetyGuard.CheckLocalPath(escaping, _workspace, displayPath: "<capped>");

        Assert.NotNull(error);
        Assert.Contains("'<capped>'", error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace-b", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard's OWN message must be bounded too, not only the one
    /// <c>ExplainRunOrchestrator</c> pre-caps for it. A review found <c>Reject</c>'s
    /// no-<c>displayPath</c> branch sanitised the raw path with no length cap at all, so a 200 kB
    /// path argument produced a 200 kB error message.
    /// </summary>
    [Fact]
    public void CheckLocalPath_NoDisplayPathSupplied_CapsTheEchoedPath()
    {
        var enormous = @"\\attacker-host\share\" + new string('a', 200_000);

        var error = PathSafetyGuard.CheckLocalPath(enormous, workspace: null);

        Assert.NotNull(error);

        // The whole message, not just the path fragment: the fixed prose is a few dozen characters,
        // so bounding the message at cap + a small constant bounds the echo.
        Assert.True(
            error!.Message.Length < PathSafetyGuard.MaxDisplayedPathChars + 200,
            $"Expected a capped message; got {error.Message.Length} characters.");
    }

    // ── Fixed-point link resolution (the double-hop bypass) ─────────────────────────────────────

    /// <summary>
    /// The security review's MAJOR finding, as an executable case: root <c>&lt;root&gt;</c>, a link
    /// <c>inner → &lt;outside&gt;</c>, and a link <c>hop → &lt;root&gt;/inner/secret.e2e.yaml</c>.
    /// One pass of link resolution turns <c>hop</c> into <c>&lt;root&gt;/inner/secret.e2e.yaml</c> —
    /// textually inside the root, so the prefix test passes — while the read that follows lands
    /// outside it. Only re-walking the substituted result to a fixed point catches it.
    /// </summary>
    [ContainmentLinkFact]
    [Trait("containment", "link")]
    public void CheckLocalPath_TwoHopSymlinkChainLandingOutsideTheRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "workspace-b");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.e2e.yaml"), "steps: []");

        var innerLink = Path.Combine(_root, "inner");
        var hopLink = Path.Combine(_root, "hop");
        Directory.CreateSymbolicLink(innerLink, outside);
        File.CreateSymbolicLink(hopLink, Path.Combine(innerLink, "secret.e2e.yaml"));

        // Anti-vacuity for the whole test: `hop`'s own stored target is INSIDE the root, so a guard
        // that stopped after one substitution would have nothing left to object to.
        Assert.StartsWith(
            _root,
            File.ResolveLinkTarget(hopLink, returnFinalTarget: false)!.FullName,
            StringComparison.Ordinal);

        var error = PathSafetyGuard.CheckLocalPath(hopLink, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
        Assert.Contains("outside the configured workspace root", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dangling link (its target does not exist) still has its target resolved. On Unix
    /// <c>File.Exists</c>/<c>Directory.Exists</c> both FOLLOW the link and answer false for one, so
    /// the earlier existence-gated shape treated it as an ordinary directory name and left the
    /// escape unresolved — measured; on Windows the same link answers <c>File.Exists == true</c>,
    /// which is why the hole was Unix-only.
    /// </summary>
    [ContainmentLinkFact]
    [Trait("containment", "link")]
    public void CheckLocalPath_DanglingSymlinkPointingOutsideTheRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "workspace-b", "never-created.e2e.yaml");

        var link = Path.Combine(_root, "dangling");
        File.CreateSymbolicLink(link, outside);

        var error = PathSafetyGuard.CheckLocalPath(link, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
    }

    /// <summary>
    /// A path deeper than the resolution budget is NOT CONTAINED, never "contained because we gave
    /// up looking" — the fail-closed rule in <see cref="PathSafetyGuard"/>'s remarks. The path below
    /// is textually inside the root, so only the budget can be what rejects it.
    /// </summary>
    [Fact]
    public void CheckLocalPath_PathDeeperThanTheResolutionBudget_IsRejectedFailClosed()
    {
        // 300 > the 256-segment budget. Nothing is created on disk: the walk spends budget on every
        // segment whether or not it exists.
        var absurdlyDeep = Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", 300)));

        Assert.StartsWith(_root, absurdlyDeep, StringComparison.Ordinal);

        var error = PathSafetyGuard.CheckLocalPath(absurdlyDeep, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);

        // A peer review's MINOR finding: the refusal is right, but the SENTENCE must not claim the
        // path "resolves outside the root" — the walk never reached a verdict about where it lands.
        // Same code, same fail-closed refusal, a claim the guard has actually earned.
        Assert.Contains(
            "could not be verified as inside the configured workspace root",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("resolves outside", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart to the budget case above: a path that genuinely resolves outside keeps the
    /// original wording. Without this, "split the message" could be satisfied by making every
    /// containment refusal say "could not be verified" — which would be a strictly weaker claim than
    /// the guard can make, and would read as a malfunction on a real escape.
    /// </summary>
    [Fact]
    public void CheckLocalPath_GenuineEscape_KeepsTheDefiniteWording()
    {
        var escaping = Path.Combine(_root, "..", "workspace-b", "secret.e2e.yaml");

        var error = PathSafetyGuard.CheckLocalPath(escaping, _workspace);

        Assert.NotNull(error);
        Assert.Contains("resolves outside the configured workspace root", error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be verified", error.Message, StringComparison.Ordinal);
    }

    // ── A filesystem-root workspace (the prefix that used to match nothing) ─────────────────────

    /// <summary>
    /// <c>--workspace C:\</c> (or <c>/</c>) rejected EVERY path before this fix: the containment
    /// prefix was built by unconditional concatenation, yielding <c>C:\\</c>, which no canonicalised
    /// path starts with. Both reviewers caught it pre-merge.
    /// </summary>
    [Fact]
    public void CheckLocalPath_FilesystemRootWorkspace_ContainsPathsBeneathIt()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(_root))!;
        var workspace = Workspace.Resolve(filesystemRoot);

        // The root as Workspace.Resolve leaves it: TrimEndingDirectorySeparator deliberately does
        // NOT trim a genuine filesystem root, so it still carries its separator.
        Assert.True(Path.EndsInDirectorySeparator(workspace.Root));

        Assert.Null(PathSafetyGuard.CheckLocalPath(_root, workspace));
        Assert.Null(PathSafetyGuard.CheckLocalPath(Path.Combine(_root, "suite.e2e.yaml"), workspace));
        Assert.Null(PathSafetyGuard.CheckLocalPath(filesystemRoot, workspace));
    }

    [Fact]
    public void CheckLocalPath_FilesystemRootWorkspace_StillRejectsAUncPath()
    {
        // Anti-vacuity: "contains everything local" must not have become "contains everything".
        var workspace = Workspace.Resolve(Path.GetPathRoot(Path.GetFullPath(_root))!);

        Assert.NotNull(PathSafetyGuard.CheckLocalPath(@"\\attacker-host\share\suite.e2e.yaml", workspace));
    }

    // ── Workspace-relative resolution (US-S3-08 review fix) ─────────────────────────────────────

    [Fact]
    public void ResolveCallerPath_NoWorkspace_ReturnsThePathUntouched()
    {
        // The compatibility half: with no workspace, a relative path is handed on exactly as written
        // and keeps resolving against the process's current directory, as it always has.
        const string relative = "suites/good.e2e.yaml";

        Assert.Equal(relative, PathSafetyGuard.ResolveCallerPath(relative, workspace: null));
    }

    [Fact]
    public void ResolveCallerPath_WorkspaceConfigured_RebasesARelativePathOntoTheRoot()
    {
        var resolved = PathSafetyGuard.ResolveCallerPath("nested/suite.e2e.yaml", _workspace);

        Assert.Equal(Path.Combine(_root, "nested", "suite.e2e.yaml"), resolved);
        Assert.Null(PathSafetyGuard.CheckLocalPath(resolved, _workspace));
    }

    [Fact]
    public void ResolveCallerPath_WorkspaceConfigured_LeavesAnAbsolutePathAlone()
    {
        var absolute = Path.Combine(_root, "suite.e2e.yaml");

        Assert.Equal(absolute, PathSafetyGuard.ResolveCallerPath(absolute, _workspace));
    }

    [Fact]
    public void ResolveCallerPath_WorkspaceConfigured_IsIdempotent()
    {
        // RunSuiteOrchestrator rebases and then hands the result to ValidationWorkerClient, which
        // rebases again. Those two must agree, or the pre-flight validates a different file from the
        // one the engine runs.
        var once = PathSafetyGuard.ResolveCallerPath("nested/suite.e2e.yaml", _workspace);

        Assert.Equal(once, PathSafetyGuard.ResolveCallerPath(once, _workspace));
    }

    [Fact]
    public void ResolveCallerPath_RelativePathEscapingTheRoot_ResolvesOutsideAndIsThenRejected()
    {
        // Rebasing is NOT itself a containment mechanism — it only decides which absolute path is
        // meant. `../` still escapes, and the guard is still what refuses it.
        var resolved = PathSafetyGuard.ResolveCallerPath(
            Path.Combine("..", "workspace-b", "secret.e2e.yaml"), _workspace);

        Assert.False(resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        var error = PathSafetyGuard.CheckLocalPath(resolved, _workspace);

        Assert.NotNull(error);
        Assert.Equal("VFX-E-1001", error!.Code);
    }

    [Fact]
    public void ResolveCallerPath_NetworkPath_IsReturnedUntouchedSoTheUncCheckStillSeesIt()
    {
        const string unc = @"\\attacker-host\share\suite.e2e.yaml";

        Assert.Equal(unc, PathSafetyGuard.ResolveCallerPath(unc, _workspace));
        Assert.NotNull(PathSafetyGuard.CheckLocalPath(unc, _workspace));
    }

}
