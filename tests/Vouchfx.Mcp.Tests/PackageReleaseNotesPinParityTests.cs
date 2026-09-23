using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The package's release notes name the engine version <c>ENGINE_PIN</c> pins, and no other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> nuget.org renders <c>&lt;PackageReleaseNotes&gt;</c> on the same page as
/// the package readme, and the notes state the engine pin in prose. Nothing tied that sentence to
/// <c>ENGINE_PIN</c>, so the rc.6 repin shipped a readme describing rc.6 behaviour beside notes
/// still reading "Pinned to … v1.0.0-rc.5" (a Copilot review finding on vouchfx-mcp#124). The next
/// repin that forgets them fails here instead.
/// </para>
/// <para>
/// <b>Read through <see cref="XDocument"/>, not as file text</b>, for the reason
/// <c>PublishedTerminologyGateTests</c> gives: the project file's XML comments record dated
/// measurements that name older engine versions on purpose, and only the element value ships.
/// Every <c>v&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;</c> token in the notes counts as an engine
/// version, which holds while the notes name no other versioned thing; narrow the pattern if they
/// ever do.
/// </para>
/// </remarks>
public class PackageReleaseNotesPinParityTests
{
    private const string PackagingProject = "src/Vouchfx.Mcp/Vouchfx.Mcp.csproj";

    /// <summary>A <c>v</c>-prefixed SemVer, with its pre-release label but never a trailing full stop.</summary>
    private static readonly Regex VersionToken = new(
        @"\bv\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void PackageReleaseNotes_NameTheEnginePinVersion_AndNoOther()
    {
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var project = XDocument.Load(Path.Combine(RepoLayout.ResolveRepoRootPath(), PackagingProject));

        var notes = Assert.Single(
            project.Descendants(),
            element => string.Equals(element.Name.LocalName, "PackageReleaseNotes", StringComparison.Ordinal)).Value;

        var versions = VersionToken.Matches(notes)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            versions.Length > 0,
            $"The <PackageReleaseNotes> in {PackagingProject} name no engine version at all. They are "
            + "expected to state the pin; if that sentence was removed on purpose, remove this test with it.");
        Assert.True(
            versions.Length == 1 && string.Equals(versions[0], pin.Version, StringComparison.Ordinal),
            $"The <PackageReleaseNotes> in {PackagingProject} name {string.Join(", ", versions)}, but "
            + $"ENGINE_PIN pins {pin.Version}. nuget.org shows these notes beside the package readme, "
            + "so update them in the same change as the pin.");
    }
}
