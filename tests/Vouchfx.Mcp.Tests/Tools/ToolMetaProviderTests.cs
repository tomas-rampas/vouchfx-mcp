using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Tools;

/// <summary>
/// US-S3-08: <c>ToolMetaProvider</c>'s workspace sourcing for <c>meta.workspaceRoot</c>.
/// </summary>
/// <remarks>
/// Exercises <c>Compose</c> — the pure function — rather than the process-wide publish. That is the
/// point of extracting it: the publish can only happen once per process and belongs to
/// <c>Program.cs</c>, so testing THROUGH it would either mutate static state the rest of this
/// (parallel) test assembly reads, or force every workspace assertion into a spawned process. The
/// composition rule is the thing with logic in it, and it is fully testable in isolation;
/// <c>RealWorkspaceProcessTests</c> covers the startup wiring end to end in a real process.
/// </remarks>
public class ToolMetaProviderTests
{
    [Fact]
    public void Compose_NoWorkspaceConfigured_ReportsTheProcessBaseDirectory()
    {
        var meta = ToolMetaProvider.Compose(null);

        // Exactly US-S1-02's shipped value, unchanged: plan §2.1's compatibility rule reaches the
        // provenance stamp too, not just the path guards.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            meta.WorkspaceRoot);
    }

    [Fact]
    public void Compose_WorkspaceConfigured_ReportsItsRoot()
    {
        var workspace = Workspace.Resolve(Path.GetTempPath());

        Assert.Equal(workspace.Root, ToolMetaProvider.Compose(workspace).WorkspaceRoot);
    }

    [Fact]
    public void Compose_WorkspaceConfigured_LeavesTheOtherTwoFieldsAlone()
    {
        var workspace = Workspace.Resolve(Path.GetTempPath());
        var withWorkspace = ToolMetaProvider.Compose(workspace);
        var without = ToolMetaProvider.Compose(null);

        // US-S3-08 changes the SOURCE of one field. The wire shape, and the other two fields, are
        // untouched — as US-S1-02 said Sprint 3 would leave them.
        Assert.Equal(without.SchemaVersion, withWorkspace.SchemaVersion);
        Assert.Equal(without.ServerVersion, withWorkspace.ServerVersion);
        Assert.Equal(VendoredSchemaVersion.Value, withWorkspace.SchemaVersion);
        Assert.Equal(ServerIdentity.Version, withWorkspace.ServerVersion);
    }

    [Fact]
    public void Compose_WorkspaceRoot_CarriesNoTrailingSeparator()
    {
        // The two sources must produce the same SHAPE, or a host correlating results across a
        // configuration change would see a spurious difference.
        var workspace = Workspace.Resolve(Path.GetTempPath());

        Assert.False(Path.EndsInDirectorySeparator(ToolMetaProvider.Compose(workspace).WorkspaceRoot));
        Assert.False(Path.EndsInDirectorySeparator(ToolMetaProvider.Compose(null).WorkspaceRoot));
    }

    [Fact]
    public void Current_IsReferenceStable()
    {
        // StructuredToolResult pre-serialises this exactly once and caches the bytes; a Current that
        // could return a different instance would make that cache silently stale.
        Assert.Same(ToolMetaProvider.Current, ToolMetaProvider.Current);
    }

    [Fact]
    public void PublishStartupWorkspace_Null_IsANoOp()
    {
        // The no-flag startup path calls this unconditionally. It must not consume the one-shot
        // publish, and must not throw — including here, in a test process where Current has already
        // been materialised by other tests.
        ToolMetaProvider.PublishStartupWorkspace(null);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            ToolMetaProvider.Current.WorkspaceRoot);
    }
}
