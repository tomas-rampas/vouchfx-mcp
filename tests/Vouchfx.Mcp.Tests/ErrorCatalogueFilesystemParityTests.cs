using System.Text.RegularExpressions;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S1-06's bidirectional, CI-enforced completeness gate between the codes this server can emit
/// and the <c>docs/errors/&lt;CODE&gt;.md</c> pages published for them — checked directly against the
/// FILESYSTEM, not the embedded resources <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository"/>
/// bakes at build time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a distinct gate from <c>VfxCodeCatalogueTests</c> and <c>DiagnosticPageRepositoryTests</c>:</b>
/// those two classes already prove, respectively, "every emitted code has a
/// <see cref="VfxCodeCatalogue"/> entry" (US-S1-04) and "every catalogue entry has a well-formed
/// EMBEDDED page" (US-S1-05 — <c>DiagnosticPageRepositoryTests.EveryCatalogueCode_HasAWellFormedEmbeddedPage</c>,
/// which compares <see cref="VfxCodeCatalogue.All"/> against
/// <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository.AllByCode"/>). Composed, those two
/// already imply "every emitted code has an embedded page" — but "embedded" is a build-time snapshot:
/// a page deleted from <c>docs/errors/</c> after the last build stays embedded in
/// <c>obj/</c>/<c>bin/</c> output until the next rebuild, so a check that only ever asks the
/// EMBEDDED-resource repository cannot catch that drift the moment it happens. This class re-derives
/// both directions straight from <c>src/**/*.cs</c> and <c>docs/errors/*.md</c> ON DISK — mirroring
/// <see cref="SecretHygieneSourceGuardTests"/>'s and <c>VfxCodeCatalogueTests</c>'s "derive from
/// source, never trust a snapshot" pattern — so a page removed from disk (stale embed or not) fails
/// <see cref="EveryEmittedVfxCodeInSrc_HasADocsErrorsPageOnDisk"/> immediately, and an orphan page
/// added to disk without ever being referenced from <c>src/</c> fails
/// <see cref="EveryDocsErrorsPageOnDisk_HasAReferencingSiteInSrc"/> immediately.
/// </para>
/// <para>
/// <b>The VfxCode.cs out-of-range doc-example edge case (VFX-E-2000):</b> <c>VfxCode.cs</c>'s own
/// header comment cites <c>VFX-E-2000</c> as an example of a code its range table must REJECT — the
/// literal appears in <c>src/</c> but is not, and can never become, an emittable code
/// (<see cref="VfxError"/>'s and <see cref="Diagnostic"/>'s constructors both run
/// <c>VfxCode.Validate</c> and throw on it). <see cref="EveryEmittedVfxCodeInSrc_HasADocsErrorsPageOnDisk"/>
/// therefore filters the "referenced in src/" scan through <see cref="IsConstructible"/> — the exact
/// same filter <c>VfxCodeCatalogueTests</c> uses for its identical reason — so this out-of-range
/// example does not demand a <c>docs/errors/VFX-E-2000.md</c> page that could never be reached.
/// </para>
/// <para>
/// <b>Direction 2 deliberately does NOT apply that filter:</b> every real, constructible code already
/// appears in <c>Contracts/VfxCodeCatalogue.cs</c> as a <c>const string</c> (proven by
/// <c>VfxCodeCatalogueTests.EveryCatalogueEntry_IsReferencedFromSrc</c>), so filtering for
/// constructibility here would change nothing about which pages are found orphaned — it would only
/// add unnecessary coupling to <see cref="VfxCode"/>'s validation for a scan that is really asking a
/// simpler question: "does ANY source file, code or comment, still say this page's code by name?" A
/// docs page is legitimately kept for a code that appears only in a doc comment (there is no such
/// case today, but nothing here should presume there never will be), so this direction counts EVERY
/// occurrence in <c>src/</c> — including inside comments — deliberately unfiltered.
/// </para>
/// </remarks>
public class ErrorCatalogueFilesystemParityTests
{
    /// <summary>Mirrors <c>VfxCodeCatalogueTests.VfxCodeLiteralPattern</c> exactly — same reasoning applies here.</summary>
    private static readonly Regex VfxCodeLiteralPattern = new(@"VFX-[ED]-\d{4}", RegexOptions.Compiled);

    [Fact]
    public void EveryEmittedVfxCodeInSrc_HasADocsErrorsPageOnDisk()
    {
        var emittedCodes = VfxCodeLiteralsInSrc().Where(IsConstructible).ToHashSet(StringComparer.Ordinal);

        // Anti-vacuity: a broken repo-root walk (RepoRoot resolving to the wrong directory, or the
        // src/ scan silently matching zero files) must fail loudly here rather than let both
        // directions of this gate pass by finding nothing to check — exactly the guard
        // SecretHygieneSourceGuardTests' remarks describe for its own derived sets.
        Assert.NotEmpty(emittedCodes);

        var docsErrorsDir = Path.Combine(RepoRoot.FullName, "docs", "errors");
        var missingPages = emittedCodes
            .Where(code => !File.Exists(Path.Combine(docsErrorsDir, $"{code}.md")))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingPages.Length == 0,
            $"These VFX codes are referenced in src/ (and are constructible per VfxCode's reserved "
            + $"ranges) but have no docs/errors/<CODE>.md page on disk: {string.Join(", ", missingPages)}. "
            + "Add the missing page(s) under docs/errors/ (and embed them in Vouchfx.Mcp.csproj per "
            + "US-S1-05's convention) rather than emitting a code with no catalogue page.");
    }

    [Fact]
    public void EveryDocsErrorsPageOnDisk_HasAReferencingSiteInSrc()
    {
        var docsErrorsDir = Path.Combine(RepoRoot.FullName, "docs", "errors");
        var pagesOnDisk = Directory.EnumerateFiles(docsErrorsDir, "VFX-*.md", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToHashSet(StringComparer.Ordinal);

        // Anti-vacuity: a broken docs/errors/ path (renamed directory, wrong RepoRoot walk) must not
        // let this direction pass by finding zero pages to check.
        Assert.NotEmpty(pagesOnDisk);

        // Deliberately unfiltered by IsConstructible — see this class's remarks on why direction 2
        // counts every occurrence, comments included, rather than reapplying the emission filter.
        var referencedCodes = VfxCodeLiteralsInSrc().ToHashSet(StringComparer.Ordinal);

        var orphanPages = pagesOnDisk
            .Where(code => !referencedCodes.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            orphanPages.Length == 0,
            $"These docs/errors/<CODE>.md pages exist on disk but their code is referenced nowhere in "
            + $"src/: {string.Join(", ", orphanPages)}. Remove the orphan page(s) (and their "
            + "Vouchfx.Mcp.csproj embed) or add the real emitting call site.");
    }

    /// <summary>
    /// Every <c>VFX-[ED]-####</c> literal occurring anywhere in <c>src/**/*.cs</c> (string literal,
    /// XML doc comment, or plain comment alike — see <see cref="VfxCodeLiteralPattern"/>'s own note in
    /// <c>VfxCodeCatalogueTests</c> for why an unanchored, un-filtered-by-context scan is correct
    /// here), unfiltered by constructibility. Callers filter through <see cref="IsConstructible"/>
    /// themselves when they need only the emittable subset.
    /// </summary>
    private static IEnumerable<string> VfxCodeLiteralsInSrc() =>
        SourceFilesUnderSrc().SelectMany(path => VfxCodeLiteralPattern.Matches(File.ReadAllText(path)).Select(match => match.Value));

    /// <summary>
    /// Whether <paramref name="code"/> could ever actually be emitted — mirrors
    /// <c>VfxCodeCatalogueTests.IsConstructible</c> exactly (same delegation to <see cref="VfxCode.Validate"/>,
    /// same rationale: a code out of every reserved range can never reach a host, so demanding a page
    /// for it would guard against something the type system already forbids).
    /// </summary>
    private static bool IsConstructible(string code) =>
        Record.Exception(() => VfxCode.Validate(code, code[..6], nameof(code))) is null;

    private static IEnumerable<string> SourceFilesUnderSrc() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path));

    /// <summary>
    /// Mirrors <c>SecretHygieneSourceGuardTests.IsBuildOutputPath</c> (and
    /// <c>VfxCodeCatalogueTests.IsBuildOutputPath</c>) exactly — see that method's remarks on why the
    /// check is REPO-RELATIVE rather than a raw absolute-path segment match: the latter misfires on a
    /// checkout path that happens to contain a "bin" or "obj" segment above the repo root. The
    /// leading-segment checks below are unreachable via today's src/-rooted scans (every relative
    /// path here already carries a leading path component before any bin/obj segment) but keep this
    /// helper correct for a future caller rooted at the repo root (defence in depth, per the Copilot
    /// review on PR #69).
    /// </summary>
    private static bool IsBuildOutputPath(string fullPath)
    {
        var relative = Path.GetRelativePath(RepoRoot.FullName, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative.Contains("/bin/", StringComparison.Ordinal)
            || relative.Contains("/obj/", StringComparison.Ordinal)
            || relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    /// <summary>Mirrors <c>SecretHygieneSourceGuardTests.RepoRoot</c> exactly — see that property's remarks.</summary>
    private static DirectoryInfo RepoRoot
    {
        get
        {
            var testOutputDir = new DirectoryInfo(AppContext.BaseDirectory);
            var testProjectDir = testOutputDir.Parent?.Parent?.Parent
                ?? throw new InvalidOperationException("Could not walk up to the test project directory from the test output path.");
            var testsDir = testProjectDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the 'tests' directory from the test project directory.");
            var repoRoot = testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");

            return repoRoot;
        }
    }
}
