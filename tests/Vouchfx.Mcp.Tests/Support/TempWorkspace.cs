namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A throwaway workspace root on disk, resolved into a real <see cref="Workspace"/> and deleted with
/// the test.
/// </summary>
/// <remarks>
/// <para>
/// Introduced for US-S6-04's span tests, which need each server to have a DISTINCT workspace root:
/// the <c>workspace.hash</c> attribute derived from it is what scopes a process-wide
/// <see cref="System.Diagnostics.ActivityListener"/>'s captures to the one server a test started.
/// A shared or absent workspace would make those assertions race every other class in a parallel
/// assembly — see <see cref="SpanRecorder"/>.
/// </para>
/// <para>
/// Deliberately a separate type from the private <c>TempWorkspace</c> nested inside
/// <c>Planning/PlanCoverageOrchestratorTests</c> rather than a promotion of it. That one is scoped to
/// its own class's guard fixtures and its sandbox prefix names them; widening it would couple two
/// unrelated test areas to one directory-naming choice, and the whole type is nine lines. If a third
/// consumer appears, promote then.
/// </para>
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    private readonly string _sandbox;

    public TempWorkspace()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-span-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_sandbox, "workspace");
        Directory.CreateDirectory(Root);
        Workspace = Workspace.Resolve(Root);
    }

    public string Root { get; }

    public Workspace Workspace { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only — a leftover sandbox must never fail a test.
        }
    }
}
