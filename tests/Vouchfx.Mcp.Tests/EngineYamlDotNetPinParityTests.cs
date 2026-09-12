using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Mechanically gates the csproj's match-the-engine YamlDotNet policy, CLONE-FREE — using the one
/// piece of engine-authored evidence this repository already ships: the vendored composed schema
/// names the engine's own YamlDotNet version in its prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test exists.</b> <c>Vouchfx.Mcp.csproj</c> states a correctness policy — YamlDotNet
/// tracks the version the vouchfx ENGINE repo pins, because <c>validate_suite</c> must resolve YAML
/// scalars exactly as a real <c>vouchfx</c> run does and <c>normalize_suite</c> may write its output
/// over an author's file. That policy was held by prose alone and <b>silently broke for 46 days</b>:
/// this project sat on 18.1.0 while the engine pinned 16.3.0, and the csproj comment had been
/// rewritten to explain the divergence away as unverifiable rather than to fix it. Prose cannot hold
/// an invariant that a routine dependency bump can violate; only a failing test can.
/// </para>
/// <para>
/// <b>Why the vendored schema is a legitimate oracle.</b> The earlier comment's premise — "this
/// repository cannot verify the engine's current pin from anything it contains" — is false.
/// <c>vendored/composed-schema.v1.json</c> is a byte-exact copy of the engine's own composed schema
/// at <c>ENGINE_PIN</c>'s commit, drift-gated in CI, and the engine's authors documented their YAML
/// reader's behaviour in it by version ("with the pinned YamlDotNet 16.3.0 representation model, a
/// YAML '~' scalar is read back as the LITERAL, ONE-CHARACTER TEXT '~'"). That is engine-authored
/// text about the engine's own dependency, refreshed whenever the pin advances. No clone, no
/// network, no engine checkout.
/// </para>
/// <para>
/// <b>Why "the loaded version is AMONG the named versions" and not a plain equality.</b> Measured
/// 2026-09-12: the schema names TWO distinct versions. Four mentions say "the pinned YamlDotNet
/// 16.3.0" (current, and describing <c>$defs/dependency.env</c>'s and <c>service.env</c>'s null
/// handling plus octal scalar resolution); one, inside <c>$defs/security.clientKeyPassword</c>'s
/// <c>$comment</c>, says "the pinned YamlDotNet 15.1.6" while describing a block-scalar
/// trailing-newline measurement. The engine's own prose has rotted there — it was measured when the
/// engine pinned 15.1.6 and not refreshed when the engine moved on. A strict "every named version
/// equals ours" assertion would therefore fail against a correct pin, so this test asserts the
/// strongest statement that is actually TRUE: our loaded version must be one of the versions the
/// engine's schema names. Combined with the exact-set pin below, a real drift still cannot pass —
/// on 18.1.0, which is the failure this exists to catch, NO named version matches.
/// </para>
/// <para>
/// <b>Fail-closed in three directions</b>, following <see cref="ReadOnlySourceGuardTests"/>'
/// exact-equality idiom rather than a presence check. A schema that names no version at all FAILS
/// rather than passing vacuously — the oracle having disappeared is exactly when this gate is most
/// needed, and a silent pass would hand back the prose-only regime that broke. The distinct set is
/// pinned, so the stale 15.1.6 mention being fixed upstream, or a third version appearing, fails
/// here and gets re-triaged deliberately instead of quietly widening what counts as a match.
/// </para>
/// <para>
/// <b>The version source is load-bearing and is NOT <c>AssemblyName.Version</c>.</b> Measured on the
/// pinned package: <c>FileVersionInfo.FileVersion</c> is <c>16.3.0.0</c> and
/// <c>AssemblyInformationalVersionAttribute</c> is <c>16.3.0</c>, but
/// <c>Assembly.GetName().Version</c> is <c>16.0.0.0</c> — major-only, because YamlDotNet does not
/// advance the assembly version within a major. Asserting against that would compare "16.0.0"
/// to "16.3.0" and miss every 16.x drift this test is meant to catch, which is the entire class of
/// drift most likely to happen by accident.
/// </para>
/// </remarks>
public class EngineYamlDotNetPinParityTests
{
    /// <summary>
    /// Every distinct <c>YamlDotNet &lt;x.y.z&gt;</c> version the vendored composed schema names, as
    /// measured 2026-09-12 at ENGINE_PIN v1.0.0-rc.5 (cc5e8efa). See this type's remarks for why the
    /// set has two members rather than one, and why that is the engine's staleness rather than ours.
    /// </summary>
    private static readonly string[] ExpectedVersionsNamedBySchema = ["15.1.6", "16.3.0"];

    /// <summary>
    /// A version this schema attributes to YamlDotNet. Deliberately matches the bare
    /// <c>YamlDotNet 1.2.3</c> shape rather than requiring the word "pinned" — measured, BOTH the
    /// current and the stale mention say "pinned", so that word discriminates nothing, and a future
    /// mention phrased differently should still be seen by this gate rather than slip past it.
    /// </summary>
    private static readonly Regex NamedYamlDotNetVersion =
        new(@"YamlDotNet\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.Compiled);

    [Fact]
    public void TheVendoredSchema_NamesAtLeastOneYamlDotNetVersion()
    {
        // Anti-vacuity, and the fail-closed direction that matters most: if the engine ever stops
        // naming a version in its schema prose, this oracle is gone and the policy silently reverts
        // to being held by a comment. That must surface as a red test demanding a new mechanism, not
        // as a green run over an empty match set.
        var named = VersionsNamedBySchema();

        Assert.True(
            named.Length > 0,
            "The vendored composed schema no longer names any 'YamlDotNet <x.y.z>' version, so the " +
            "clone-free oracle this test depends on has disappeared. Do NOT delete this test: find " +
            "another engine-authored artefact that carries the version, or reinstate the check " +
            "against the engine repo's Directory.Packages.props at ENGINE_PIN's commit.");
    }

    [Fact]
    public void TheLoadedYamlDotNet_IsOneOfTheVersionsTheEnginesSchemaNames()
    {
        var loaded = LoadedYamlDotNetVersion();
        var named = VersionsNamedBySchema();

        Assert.True(
            named.Contains(loaded, StringComparer.Ordinal),
            $"This project loads YamlDotNet {loaded}, which is NOT among the version(s) the engine's " +
            $"own vendored schema names ({string.Join(", ", named)}). That is the match-the-engine " +
            "policy in Vouchfx.Mcp.csproj being violated — the same break that went unnoticed for 46 " +
            "days while this project sat on 18.1.0. Fix the PackageReference to match the engine, or, " +
            "if the engine genuinely moved, advance ENGINE_PIN and re-sync vendored/ first.");
    }

    [Fact]
    public void TheSetOfVersionsNamedBySchema_IsExactlyTheRecordedSet()
    {
        // Exact equality, not a subset check: a third version appearing, or the stale 15.1.6 mention
        // being corrected upstream, both change what "is among the named versions" licenses above.
        // Either is a deliberate re-triage, not something to absorb silently.
        Assert.Equal(
            ExpectedVersionsNamedBySchema.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            VersionsNamedBySchema());
    }

    [Fact]
    public void TheVersionSource_IsTheFileVersion_NotTheMajorOnlyAssemblyVersion()
    {
        // Pins the caveat in this type's remarks, so a future "simplification" to GetName().Version
        // fails here with the reason rather than quietly making the parity check unable to see a
        // 16.3 -> 16.x move.
        var assembly = typeof(Scanner).Assembly;

        Assert.Equal("16.0.0.0", assembly.GetName().Version?.ToString());
        Assert.StartsWith("16.3.0", LoadedYamlDotNetVersion(), StringComparison.Ordinal);
        Assert.NotEqual(assembly.GetName().Version?.ToString(), LoadedYamlDotNetVersion());
    }

    [Fact]
    public void ThePattern_MatchesTheSchemasRealPhrasingsAndNotUnrelatedNumbers()
    {
        // Sanity check for the regex, because the whole gate is only as good as it is. Both strings
        // below are lifted from the vendored schema's actual prose.
        Assert.Equal("16.3.0", NamedYamlDotNetVersion
            .Match("with the pinned YamlDotNet 16.3.0 representation model, a YAML '~' scalar")
            .Groups["version"].Value);
        Assert.Equal("15.1.6", NamedYamlDotNetVersion
            .Match("against the pinned YamlDotNet 15.1.6, which a '$'-anchored pattern ACCEPTS")
            .Groups["version"].Value);

        // A YamlDotNet mention carrying no version must not be read as one.
        Assert.DoesNotMatch(NamedYamlDotNetVersion, "YamlDotNet's own DeserializerBuilder resolves an");

        // And an unrelated dotted number nearby is not a YamlDotNet version.
        Assert.DoesNotMatch(NamedYamlDotNetVersion, "JsonSchema.Net 9.4.0 is deliberately ahead");
    }

    /// <summary>
    /// The YamlDotNet the test host actually loaded, as <c>major.minor.patch</c>, taken from the
    /// file version — see this type's remarks for why not <c>AssemblyName.Version</c>.
    /// </summary>
    private static string LoadedYamlDotNetVersion()
    {
        var location = typeof(Scanner).Assembly.Location;

        Assert.False(
            string.IsNullOrEmpty(location),
            "YamlDotNet reported no assembly location, so its file version cannot be read. If this " +
            "assembly is ever loaded from a single-file bundle, switch to the informational version " +
            "attribute rather than weakening this gate.");

        var fileVersion = FileVersionInfo.GetVersionInfo(location);

        // FileVersion is "16.3.0.0"; the schema names "16.3.0". Compare on the three-part form both
        // sides actually express, rather than padding the schema's text to four parts.
        return $"{fileVersion.FileMajorPart}.{fileVersion.FileMinorPart}.{fileVersion.FileBuildPart}";
    }

    /// <summary>Distinct, ordinal-sorted versions named in the vendored composed schema's text.</summary>
    private static string[] VersionsNamedBySchema() =>
        NamedYamlDotNetVersion.Matches(ComposedSchemaText())
            .Select(match => match.Groups["version"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => version, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The embedded composed schema as text — the EMBEDDED copy rather than the file on disk, so
    /// this reads exactly what the built assembly ships (<c>VendoredArtefactsTests</c> separately
    /// pins those two as byte-identical).
    /// </summary>
    private static string ComposedSchemaText()
    {
        const string resourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";

        using var stream = typeof(Vouchfx.Mcp.Validation.SuiteValidator).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
