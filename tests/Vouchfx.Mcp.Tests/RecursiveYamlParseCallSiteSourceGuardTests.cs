using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level guard pinning recursive YamlDotNet use in <c>src/</c> at BOTH levels: the two
/// wrappers that hand text to a recursive parse (<c>YamlToJsonConverter.Convert</c> and
/// <c>YamlLineResolver.TryParseYamlRoot</c>) are pinned to an exact set of call sites, and the raw
/// YamlDotNet engines those wrappers are built on are pinned to an exact set of construction sites.
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
/// <b>Why the wrapper scan alone is not enough, and what the second scan adds.</b> Pinning
/// <c>Convert</c> and <c>TryParseYamlRoot</c> pins the two WRAPPERS — it says nothing about code
/// that bypasses them. A future <c>new YamlStream().Load(reader)</c> or
/// <c>new DeserializerBuilder().Build().Deserialize(text)</c> dropped into an unguarded file reaches
/// exactly the same recursive descent, with exactly the same uncatchable crash, and would satisfy
/// every wrapper assertion in this class. That is the likelier mistake, not the less likely one:
/// someone who wants "just parse this YAML" reaches for the library directly rather than hunting for
/// this repository's wrapper. So the raw ENGINE constructors are pinned too, fail-closed in both
/// directions, and the allow-list below was derived by scanning the code rather than assumed.
/// </para>
/// <para>
/// <b><see cref="YamlDotNet.Core.Scanner"/> is deliberately excluded from that set and pinned
/// separately.</b> It is the flat TOKENISER, not the recursive-descent parser, and it is what
/// <see cref="Vouchfx.Mcp.Validation.YamlSafetyGuard"/> itself runs to measure depth before anything
/// else is allowed to touch the text — measured 2026-09-12, it tokenises 2000-deep versions of all
/// three proven attack shapes in 69 ms / 41 ms / 6 ms where the deserializer on the same input
/// crashes the process. Lumping it in with the dangerous constructors would force the guard to
/// allow-list itself for using the safe tool, which inverts the message. It gets its own
/// exact-equality assertion instead, so it also cannot spread unnoticed.
/// </para>
/// <para>
/// <b>Mirrors <see cref="SpecIndexParserSourceGuardTests"/>' mechanism exactly</b> — a
/// whitespace-tolerant regex over source with comments and string literals stripped
/// (<see cref="SourceGuardScan"/>), plus EXACT-equality against a named set, so a new site fails by
/// name and a stale entry cannot rot. Note the deliberate consequence of matching only
/// TYPE-QUALIFIED invocations in the wrapper scan: each declaring type's own file is not a call
/// site, so neither has to name itself there — the same choice that guard makes for
/// <c>SpecIndexParser.cs</c>.
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

    /// <summary>
    /// The only files in <c>src/</c> that may CONSTRUCT a recursive or alias-expanding YamlDotNet
    /// engine directly. Derived by scanning <c>src/</c> for these constructors, not assumed.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>YamlToJsonConverter.cs</c> — <c>new DeserializerBuilder()</c> (the
    /// recursive parse) and <c>new SerializerBuilder()</c> (the <c>JsonCompatible</c> re-emission
    /// that re-expands every alias, i.e. the billion-laughs half the anchor/alias caps
    /// defend).</description></item>
    /// <item><description><c>YamlLineResolver.cs</c> — <c>new YamlStream()</c>, the
    /// RepresentationModel load behind <c>TryParseYamlRoot</c>.</description></item>
    /// <item><description><c>SuiteNormalizer.cs</c> — <c>new YamlStream(new YamlDocument(root))</c>
    /// and <c>new Emitter(...)</c>, both on the EMIT side over a graph that has already been parsed
    /// and guarded, never over caller text.</description></item>
    /// </list>
    /// A fourth file here means a new YamlDotNet engine exists somewhere that has not been reasoned
    /// about. On this pin that is not a style question: an unguarded one kills the process.
    /// </remarks>
    private static readonly string[] RawEngineConstructionRelativePaths =
    [
        "src/Vouchfx.Mcp/Normalization/SuiteNormalizer.cs",
        "src/Vouchfx.Mcp/Validation/YamlLineResolver.cs",
        "src/Vouchfx.Mcp/Validation/YamlToJsonConverter.cs",
    ];

    /// <summary>
    /// The only file in <c>src/</c> that may construct the flat <c>Scanner</c> — the guard itself.
    /// Separate from the set above because the Scanner is the SAFE tool; see this type's remarks.
    /// </summary>
    private static readonly string[] ScannerConstructionRelativePaths =
    [
        "src/Vouchfx.Mcp/Validation/YamlSafetyGuard.cs",
    ];

    /// <summary>
    /// Construction of a YamlDotNet engine that recurses over, or re-expands, a document.
    /// <c>\b</c> after each name keeps <c>new SpecIndexParser(</c> and <c>new PromptDocumentParser(</c>
    /// from reading as <c>new Parser(</c>.
    /// </summary>
    private static readonly Regex RawEngineConstruction =
        new(@"new\s+(YamlStream|YamlDocument|DeserializerBuilder|SerializerBuilder|Parser|Emitter)\b\s*\(",
            RegexOptions.Compiled);

    private static readonly Regex ScannerConstruction =
        new(@"new\s+Scanner\b\s*\(", RegexOptions.Compiled);

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
    public void TheRawYamlDotNetEngines_AreConstructedOnlyInTheFilesThatOwnThem()
    {
        // The bypass the wrapper scan cannot see: constructing the library directly instead of going
        // through Convert/TryParseYamlRoot. Exact equality, so a new construction site fails BY NAME
        // and a stale entry cannot rot.
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => RawEngineConstruction.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            RawEngineConstructionRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void TheFlatScanner_IsConstructedOnlyByTheSafetyGuardItself()
    {
        // Pinned separately and deliberately: the Scanner is the safe flat tokeniser the guard uses
        // to MEASURE depth. It spreading would not be a crash risk the way the engines above are,
        // but it would mean someone tokenising YAML outside the guard, which is worth seeing.
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => ScannerConstruction.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ScannerConstructionRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void TheRawConstructionPatterns_MatchRealConstructionAndNotNeighbouringNames()
    {
        // Every positive below is lifted from the real source it is meant to catch.
        Assert.Matches(RawEngineConstruction, "var deserializer = new DeserializerBuilder()");
        Assert.Matches(RawEngineConstruction, "var jsonSerializer = new SerializerBuilder()");
        Assert.Matches(RawEngineConstruction, "var stream = new YamlStream();");
        Assert.Matches(RawEngineConstruction, "var stream = new YamlStream(new YamlDocument(root));");
        Assert.Matches(RawEngineConstruction, "stream.Save(new Emitter(writer, CanonicalEmitterSettings), false);");

        // The shape this guard exists for: a raw parse dropped into a file that has no business
        // doing one. It must match even though no wrapper is named anywhere on the line.
        Assert.Matches(RawEngineConstruction, "new YamlStream().Load(new StringReader(untrusted));");
        Assert.Matches(RawEngineConstruction, "var p = new Parser(reader);");

        // Neighbouring type names that merely END in Parser are not the YamlDotNet Parser — without
        // the word boundary these would both false-positive and force unrelated files onto the list.
        Assert.DoesNotMatch(RawEngineConstruction, "var e = new SpecIndexParser(paths);");
        Assert.DoesNotMatch(RawEngineConstruction, "var d = new PromptDocumentParser();");

        // The Scanner is matched by its OWN pattern and must not be caught by the engine one.
        Assert.DoesNotMatch(RawEngineConstruction, "var scanner = new Scanner(new StringReader(yamlText));");
        Assert.Matches(ScannerConstruction, "var scanner = new Scanner(new StringReader(yamlText));");
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

            // Both routes, not just the wrapper one — reaching for the library directly is the
            // easier bypass and lands in exactly these files first.
            Assert.DoesNotMatch(RawEngineConstruction, source);
            Assert.DoesNotMatch(ScannerConstruction, source);
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
