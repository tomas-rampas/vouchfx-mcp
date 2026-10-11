namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Native containment assertions are explicit skips when local link creation is unavailable.
/// CI requires execution instead; discovery never suppresses a required probe.
/// </summary>
public sealed class ContainmentLinkFactAttribute : FactAttribute
{
    internal const string EnvironmentVariable = "VOUCHFX_MCP_REQUIRED_LINK_PROBES";
    private static readonly Lazy<string?> CapabilityFailure = new(CheckLocalLinkCapability);

    public ContainmentLinkFactAttribute()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable), CapabilityFailure.Value)
    {
    }

    internal ContainmentLinkFactAttribute(string? requiredMode, string? capabilityFailure)
    {
        if (string.IsNullOrEmpty(requiredMode) && capabilityFailure is not null)
        {
            Skip = $"Native containment link probes unavailable ({capabilityFailure}). "
                + $"Set {EnvironmentVariable}=1 to require execution and fail missing prerequisites.";
        }
    }

    private static string? CheckLocalLinkCapability()
    {
        DirectoryInfo? sandbox = null;
        try
        {
            sandbox = Directory.CreateTempSubdirectory("vfx-mcp-link-capability-");
            var targetDirectory = Directory.CreateDirectory(Path.Combine(sandbox.FullName, "target-directory"));
            var targetFile = Path.Combine(sandbox.FullName, "target-file");
            File.WriteAllText(targetFile, string.Empty);
            var directoryLink = Path.Combine(sandbox.FullName, "directory-link");
            var fileLink = Path.Combine(sandbox.FullName, "file-link");
            Directory.CreateSymbolicLink(directoryLink, targetDirectory.FullName);
            File.CreateSymbolicLink(fileLink, targetFile);

            return new DirectoryInfo(directoryLink).LinkTarget == targetDirectory.FullName
                && new FileInfo(fileLink).LinkTarget == targetFile
                ? null
                : "link metadata could not be verified";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return ex.GetType().Name;
        }
        finally
        {
            if (sandbox is not null)
            {
                try
                {
                    // Only this probe's local temporary tree; Directory.Delete removes links,
                    // rather than traversing their targets. No product assertion happens here.
                    sandbox.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Capability probing must not fail solely over temporary cleanup.
                }
            }
        }
    }
}

/// <summary>The same native link prerequisite policy for parameterised probes.</summary>
public sealed class ContainmentLinkTheoryAttribute : TheoryAttribute
{
    public ContainmentLinkTheoryAttribute()
    {
        Skip = new ContainmentLinkFactAttribute().Skip;
    }
}
