namespace Vouchfx.Mcp.Tests;

/// <summary>
/// xUnit v2 cannot skip dynamically. Discover the container-backed evidence probes as skipped until
/// explicitly requested; once requested, every unmet prerequisite must fail instead of returning.
/// </summary>
public sealed class RequiredRealProbeFactAttribute : FactAttribute
{
    internal const string EnvironmentVariable = "VOUCHFX_MCP_REQUIRED_REAL_PROBES";

    public RequiredRealProbeFactAttribute()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable))
    {
    }

    internal RequiredRealProbeFactAttribute(string? mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            Skip = $"Real pinned-CLI/Docker evidence was not requested. Set {EnvironmentVariable}=1 to require execution.";
        }
    }
}
