using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level guard pinning every entry point that hands text to a RECURSIVE YamlDotNet parse —
/// <c>YamlToJsonConverter.Convert</c> and <c>YamlLineResolver.TryParseYamlRoot</c> — to an exact,
/// named set of call sites in <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this became structural at the 16.3.0 pin.</b> Every call site on this list is required to
/// run <see cref="Vouchfx.Mcp.Validation.YamlSafetyGuard"/> first. While this project was pinned to
/// YamlDotNet 18.1.0 an unguarded call site was a bug with a recoverable failure mode: the
/// deserializer raised a catchable <c>MaximumRecursionLevelReachedException</c>, so a missed guard
/// surfaced as an ugly error rather than an outage. On the engine-matching 16.3.0 pin it does not.
/// Measured 2026-09-12, one depth per child process: <c>Deserializer.Deserialize</c> completes at
/// nesting depth 880 and, at 900, dies of an UNCATCHABLE native stack overflow that takes the whole
/// server process with it; the <c>YamlStream</c> paths behind <c>TryParseYamlRoot</c> do the same at
/// ~3900. No exception is raised at any depth, so there is no try/catch anywhere downstream that can
/// contain a new unguarded call site.
/// </para>
/// <para>
/// That turns "the safety guard runs before any recursive YamlDotNet parse" from a convention into a
/// must-not-spread rule, and this repository holds those the same way every time — structurally, and
/// fail-closed. A comment cannot: both methods are reachable, both are convenient, and a new call to
/// either would make every other test in this repository pass.
/// </para>
/// <para>
/// <b>Mirrors <see cref="SpecIndexParserSourceGuardTests"/>' mechanism exactly</b> — a
/// whitespace-tolerant regex over source with comments and string literals stripped
/// (<see cref="SourceGuardScan"/>), plus EXACT-equality against a named set, so a new call site
/// fails by name and a stale entry cannot rot. Note the deliberate consequence of matching only
/// TYPE-QUALIFIED invocations: each declaring type's own file is not a call site, so neither has to
/// name itself here — the same choice that guard makes for <c>SpecIndexParser.cs</c>.
/// </para>
/// </remarks>
public class RecursiveYamlParseCallSiteSourceGuardTests
{
    /// <summary>
    /// The only files in <c>src/</c> allowed to invoke <c>YamlToJsonConverter.Convert</c>, each with
    /// the reason it is safe.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>SuiteValidator.cs</c> — runs <c>YamlSafetyGuard.Check</c> before the
    /// convert, and the whole pipeline is additionally executed inside the spawned validation worker
    /// (<c>ValidationWorkerClient</c>), so even a guard miss costs a child process rather than the
    /// server.</description></item>
    /// <item><description><c>SpecIndexParser.cs</c> — runs only in the <c>--spec-index-worker</c>
    /// child, which is itself pinned by <see cref="SpecIndexParserSourceGuardTests"/>. Two process
    /// boundaries is precisely why a workspace's arbitrary files may be parsed at all.</description></item>
    /// <item><description><c>PromptDocumentParser.cs</c> — parses THIS ASSEMBLY'S OWN embedded prompt
    /// front matter, never caller-supplied text. The input is a build artefact of this repository, so
    /// there is no untrusted document and no hostile depth to guard against.</description></item>
    /// </list>
    /// A fourth entry means someone is parsing something new; establish which of those three
    /// categories it falls into, and if it is none of them, it needs the guard and probably a worker.
    /// </remarks>
    private static readonly string[] ConvertCallSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Prompts/PromptDocumentParser.cs",
        "src/Vouchfx.Mcp/Specs/SpecIndexParser.cs",
        "src/Vouchfx.Mcp/Validation/SuiteValidator.cs",
    ];

    /// <summary>
    /// The only files in <c>src/</c> allowed to invoke <c>YamlLineResolver.TryParseYamlRoot</c>.
    /// </summary>
    /// <remarks>
    /// <c>SuiteValidator.cs</c> parses for line resolution after its own guard has already admitted
    /// the text; <c>SuiteNormalizer.cs</c> is reached only through
    /// <c>SuiteValidator.NormaliseYaml</c>, which runs the same guard, and one of its three calls
    /// re-parses the EMITTER's own output rather than caller text. Notably NOT here:
    /// <c>YamlLineResolver.cs</c> itself, whose internal unqualified self-call is the declaration
    /// side of this API, not a new entry point.
    /// </remarks>
    private static readonly string[] TryParseYamlRootCallSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Normalization/SuiteNormalizer.cs",
        "src/Vouchfx.Mcp/Validation/SuiteValidator.cs",
    ];

    private static readonly Regex ConvertInvocation =
        new(@"YamlToJsonConverter\s*\.\s*Convert\w*\s*\(", RegexOptions.Compiled);

    private static readonly Regex TryParseYamlRootInvocation =
        new(@"YamlLineResolver\s*\.\s*TryParseYamlRoot\w*\s*\(", RegexOptions.Compiled);

    public static TheoryData<string, string[]> GuardedEntryPoints() => new()
    {
        { nameof(ConvertInvocation), ConvertCallSiteRelativePaths },
        { nameof(TryParseYamlRootInvocation), TryParseYamlRootCallSiteRelativePaths },
    };

    [Theory]
    [MemberData(nameof(GuardedEntryPoints))]
    public void EachRecursiveParseEntryPoint_HasExactlyItsGuardedCallSitesInSrc(
        string entryPoint, string[] expectedCallSites)
    {
        var pattern = PatternFor(entryPoint);

        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => pattern.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedCallSites.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Theory]
    [MemberData(nameof(GuardedEntryPoints))]
    public void EachGuardedCallSite_StillExistsAndStillCallsIt(string entryPoint, string[] expectedCallSites)
    {
        // Anti-vacuity in both directions, exactly as SpecIndexParserSourceGuardTests does it: a
        // renamed file would make the set check above pass over nothing, and a file that stopped
        // calling the entry point would mean this guard kept reporting success over a stale list.
        var pattern = PatternFor(entryPoint);

        foreach (var relativePath in expectedCallSites)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.Matches(pattern, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void TheGuardItself_IsNotBypassableByCallingTheConverterFromAResourceOrToolPath()
    {
        // The specific regression shape, named so a failure reads as a reason rather than a diff:
        // the resource and tool layers are where "just parse the file here" is most tempting, and on
        // this pin that would be a remote process-kill rather than a handled error.
        var tempting = new[]
        {
            Path.Combine("src", "Vouchfx.Mcp", "Specs", "WorkspaceSpecIndexer.cs"),
            Path.Combine("src", "Vouchfx.Mcp", "Resources", "ExampleResourceRegistry.cs"),
        };

        foreach (var relative in tempting)
        {
            var fullPath = Path.Combine(SourceGuardScan.RepoRoot.FullName, relative);
            Assert.True(File.Exists(fullPath), $"Expected the file at '{fullPath}'.");

            var source = SourceGuardScan.ExecutableSourceOf(fullPath);

            Assert.DoesNotMatch(ConvertInvocation, source);
            Assert.DoesNotMatch(TryParseYamlRootInvocation, source);
        }
    }

    [Fact]
    public void TheInvocationPatterns_MatchCallsAndNotDeclarationsOrMentions()
    {
        Assert.Matches(ConvertInvocation, "document = YamlToJsonConverter.Convert(yamlText);");
        Assert.Matches(ConvertInvocation, "var d = YamlToJsonConverter\n    .Convert(text);");
        Assert.Matches(TryParseYamlRootInvocation, "var yamlRoot = YamlLineResolver.TryParseYamlRoot(yamlText);");

        // The declarations themselves are not call sites.
        Assert.DoesNotMatch(ConvertInvocation, "public static JsonDocument Convert(string yamlText)");
        Assert.DoesNotMatch(
            TryParseYamlRootInvocation, "public static YamlMappingNode? TryParseYamlRoot(string yamlText)");

        // An unqualified call inside the declaring type is the declaration side, not a new entry
        // point — this is what keeps YamlLineResolver.cs off its own allow-list.
        Assert.DoesNotMatch(TryParseYamlRootInvocation, "ResolveLine(TryParseYamlRoot(yamlText), jsonPointer);");

        // Unrelated Convert members elsewhere in the assembly are not this converter.
        Assert.DoesNotMatch(ConvertInvocation, "System.Convert.ToHexString(bytes)");

        // A future ParseAsync/ConvertMany on either type is still a call site (the \w* suffix).
        Assert.Matches(ConvertInvocation, "YamlToJsonConverter.ConvertMany(texts)");
        Assert.Matches(TryParseYamlRootInvocation, "YamlLineResolver.TryParseYamlRootAsync(text)");
    }

    private static Regex PatternFor(string entryPoint) => entryPoint switch
    {
        nameof(ConvertInvocation) => ConvertInvocation,
        nameof(TryParseYamlRootInvocation) => TryParseYamlRootInvocation,
        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, "Unknown entry point."),
    };
}
