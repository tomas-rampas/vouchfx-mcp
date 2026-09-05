using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Tests.Validation;
using Vouchfx.Mcp.Validation;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// The fail-closed check that <see cref="FileRunRegistry"/>'s output directory actually resolves
/// INSIDE the workspace root — asserted at both seams that run it: the registry's own constructor,
/// and <see cref="PathSafetyGuard.DescribeWorkspaceStartupFailure"/>, which is what turns the same
/// refusal into a readable startup line instead of an exception out of DI registration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs a real link.</b> The whole point of the check is a case no string comparison can
/// reach: <c>&lt;root&gt;/.vouchfx</c> replaced by a symlink or junction whose target is elsewhere.
/// <c>Path.GetFullPath</c> sees a path squarely under the root; only the guard's segment-by-segment
/// link resolution sees the escape. A test that fabricated the escape with <c>..</c> instead would
/// pass without ever exercising the resolution the check depends on.
/// </para>
/// <para>
/// <b>Self-gated, like every other link test in this repo.</b> On Windows, creating a directory
/// symlink needs Developer Mode or <c>SeCreateSymbolicLinkPrivilege</c>, and no test here may depend
/// on ambient machine capability. The gate is ANNOUNCED rather than silent — the same
/// <see cref="PathSafetyGuardTests.LinksUnavailableMarker"/> those tests print, so "did the link
/// tests actually run on this agent?" stays answerable from the run's own output.
/// </para>
/// </remarks>
public class RunRegistryContainmentTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _outside;
    private readonly ITestOutputHelper _output;

    public RunRegistryContainmentTests(ITestOutputHelper output)
    {
        _output = output;
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-registry-containment-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "repo");
        _outside = Path.Combine(_sandbox, "elsewhere");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
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
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    /// <summary>
    /// The constructor check itself: <see cref="FileRunRegistry"/> is one of the very few types in
    /// <c>src/</c> allowed to write, and its whole licence to do so is "everything written here is
    /// under the workspace's own output directory". With <c>.vouchfx</c> symlinked out of the tree
    /// that sentence is false, and every run directory, metadata document, and events file would land
    /// somewhere the operator never authorised — through a path the operator never typed.
    /// </summary>
    [Fact]
    public void Constructor_OutputDirectoryEscapingTheRootThroughALink_IsRefused()
    {
        if (!TryLinkVouchfxDirectoryOutsideTheRoot())
        {
            return;
        }

        var workspace = Workspace.Resolve(_root);

        // Anti-vacuity for the escape itself: as a STRING the output directory is inside the root.
        // Only link resolution can tell otherwise, which is what makes the guard load-bearing here.
        Assert.StartsWith(_root, Path.GetFullPath(workspace.OutputDir), StringComparison.Ordinal);

        var refusal = Assert.Throws<ArgumentException>(() => new FileRunRegistry(workspace.OutputDir, workspace));
        Assert.Contains("workspace root", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anti-vacuity twin: a healthy workspace must construct. A containment check that refused
    /// everything would pass the test above and break every workspace-configured server.
    /// </summary>
    [Fact]
    public void Constructor_OutputDirectoryInsideTheRoot_IsAccepted()
    {
        var workspace = Workspace.Resolve(_root);

        var registry = new FileRunRegistry(workspace.OutputDir, workspace);

        // Constructed, and still creating nothing until a run actually starts.
        Assert.Empty(registry.ListRuns());
        Assert.False(Directory.Exists(workspace.OutputDir));
    }

    /// <summary>
    /// The startup seam. <c>Program.cs</c> calls this BEFORE DI registration precisely so the
    /// constructor refusal above reaches the operator as one sanitised line and a non-zero exit
    /// rather than as a stack trace out of <c>AddVouchfxMcpServer</c>. Both answers are asserted in
    /// one test because the behaviour under test is a DIFFERENCE — a method that returned a message
    /// unconditionally would pass either half alone.
    /// </summary>
    [Fact]
    public void DescribeWorkspaceStartupFailure_NamesTheEscapingOutputDirectory_AndPassesAHealthyWorkspace()
    {
        // Healthy first, so the negative answer is established on the very same root before the link
        // is planted under it.
        Assert.Null(PathSafetyGuard.DescribeWorkspaceStartupFailure(Workspace.Resolve(_root)));

        if (!TryLinkVouchfxDirectoryOutsideTheRoot())
        {
            return;
        }

        var failure = PathSafetyGuard.DescribeWorkspaceStartupFailure(Workspace.Resolve(_root));

        Assert.NotNull(failure);
        Assert.Contains(Workspace.CommandLineFlag, failure, StringComparison.Ordinal);
        Assert.Contains("run-artefact", failure, StringComparison.Ordinal);

        // One line — this is printed to stderr at startup, where a multi-line dump would bury it.
        Assert.DoesNotContain('\n', failure);
    }

    /// <summary>
    /// Replaces <c>&lt;root&gt;/.vouchfx</c> with a directory symlink pointing outside the root.
    /// Returns <see langword="false"/> — having announced the gate — when the host refuses to create
    /// the link, in which case the calling test asserts nothing.
    /// </summary>
    private bool TryLinkVouchfxDirectoryOutsideTheRoot()
    {
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, ".vouchfx"), _outside);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _output.WriteLine($"{PathSafetyGuardTests.LinksUnavailableMarker} ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }
}
