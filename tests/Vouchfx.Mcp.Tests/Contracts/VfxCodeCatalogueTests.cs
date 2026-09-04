using System.Text.RegularExpressions;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tests.Contracts;

/// <summary>
/// Exhaustiveness and consistency guards for <see cref="VfxCodeCatalogue"/> — US-S1-04's single
/// source of <c>kind</c> → code truth.
/// </summary>
/// <remarks>
/// <para>
/// <b>The load-bearing test in this class is <see cref="EveryVfxCodeLiteralInSrc_HasACatalogueEntry"/>.</b>
/// It scans the real <c>src/</c> tree for every <c>VFX-[ED]-####</c> literal and asserts each one is
/// catalogued — following <c>SecretHygieneSourceGuardTests</c>' pattern of deriving a set from SOURCE
/// rather than trusting a hand-maintained list, and for the same fail-closed reason: a future call
/// site that mints a code inline, without adding it to the table, fails here BY NAME rather than
/// slipping through to a host. It is also the precondition for US-S1-06's bidirectional catalogue
/// completeness gate, which needs "the set of codes this server emits" to be knowable at all.
/// </para>
/// <para>
/// The remaining tests pin the properties the emission helpers rely on: codes are unique and
/// well-formed, prefix agrees with kind, <c>docsUrl</c> follows spec §4.4's shape, and the
/// hand-checked mandated assignments (VFX-E-1001, VFX-D-1201, VFX-E-1501) are exactly what the
/// sprint plan and <see cref="VfxError"/>'s own documentation committed to.
/// </para>
/// </remarks>
public class VfxCodeCatalogueTests
{
    /// <summary>
    /// Matches any <c>VFX-E-####</c>/<c>VFX-D-####</c> literal. Deliberately NOT anchored: it must
    /// find codes wherever they appear in a source file — inside a string literal, an XML doc
    /// comment, or a plain code comment — because a code mentioned anywhere in <c>src/</c> is a code
    /// this server has laid claim to and therefore owes a catalogue entry (and, from US-S1-06, a
    /// docs page).
    /// </summary>
    private static readonly Regex VfxCodeLiteralPattern = new(@"VFX-[ED]-\d{4}", RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="code"/> could ever actually be emitted — i.e. whether its number
    /// falls inside one of spec §4.4's reserved ranges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the scans filter on this rather than taking every literal at face value:</b> source
    /// files legitimately MENTION codes that are deliberately invalid, as counter-examples. The
    /// live instance is <c>VfxCode.cs</c>'s own header, which cites <c>VFX-E-2000</c> while
    /// explaining what its range table exists to reject. Treating that as an emission claim would
    /// make the catalogue scan demand an entry for a code the type system refuses to construct.
    /// </para>
    /// <para>
    /// This filter costs nothing in coverage, because an out-of-range code CANNOT reach a host:
    /// <see cref="VfxError"/>'s and <see cref="Diagnostic"/>'s constructors both run
    /// <c>VfxCode.Validate</c> and throw on one. So the codes this scan skips are exactly the codes
    /// another guard already makes unreachable — and the check is delegated to <c>VfxCode</c>
    /// itself rather than re-listing the ranges here, so the two can never disagree (a range added
    /// to the spec is picked up by this test for free).
    /// </para>
    /// </remarks>
    private static bool IsConstructible(string code) =>
        Record.Exception(() => VfxCode.Validate(code, code[..6], nameof(code))) is null;

    [Fact]
    public void EveryVfxCodeLiteralInSrc_HasACatalogueEntry()
    {
        var catalogued = VfxCodeCatalogue.All.Select(entry => entry.Code).ToHashSet(StringComparer.Ordinal);

        var codesInSource = SourceFilesUnderSrc()
            .SelectMany(path => VfxCodeLiteralPattern.Matches(File.ReadAllText(path)).Select(match => match.Value))
            .Where(IsConstructible)
            .ToHashSet(StringComparer.Ordinal);

        // Not merely "the catalogue is non-empty": the scan must actually be finding things, or a
        // broken RepoRoot walk would make this test vacuously pass while guarding nothing.
        Assert.NotEmpty(codesInSource);

        var uncatalogued = codesInSource.Except(catalogued, StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            uncatalogued.Length == 0,
            $"These VFX codes appear in src/ but have no VfxCodeCatalogue.All entry: {string.Join(", ", uncatalogued)}. "
            + "Add an entry (with its range rationale and retryable decision) rather than emitting an uncatalogued code.");
    }

    [Fact]
    public void EveryCatalogueEntry_IsReferencedFromSrc()
    {
        // The reverse direction, and the reason it is safe to assert today: every entry's code is
        // written exactly once in src/ — as its own `const string` in VfxCodeCatalogue — and every
        // call site refers to that constant by name rather than repeating the literal. So this test
        // is really asserting "no entry has lost its constant", which is the shape an accidental
        // half-deletion of a code would take. US-S1-06 extends this direction all the way to the
        // docs/errors/ pages.
        var codesInSource = SourceFilesUnderSrc()
            .SelectMany(path => VfxCodeLiteralPattern.Matches(File.ReadAllText(path)).Select(match => match.Value))
            .Where(IsConstructible)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = VfxCodeCatalogue.All
            .Select(entry => entry.Code)
            .Where(code => !codesInSource.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            orphans.Length == 0,
            $"These catalogued codes appear nowhere in src/: {string.Join(", ", orphans)}.");
    }

    [Fact]
    public void EveryLegacyKind_MapsToExactlyOneCode()
    {
        // The migration's headline acceptance criterion. Listed literally, not derived from the
        // catalogue, so this test can actually fail: deriving the expected set from the same table
        // it checks would assert nothing. Every string here is one this server emitted before
        // US-S1-04, recovered by the story's audit of src/.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Confirmed by the sprint plan.
            ["invalid-path"] = "VFX-E-1001",
            ["file-not-found"] = "VFX-E-1002",
            ["file-access-error"] = "VFX-E-1003",
            ["suite-invalid"] = "VFX-D-1100",
            ["unknown-step-type"] = "VFX-D-1201",
            ["validation-timeout"] = "VFX-E-1150",
            ["validation-worker-failed"] = "VFX-E-1901",

            // Found by the story's own audit, beyond the confirmed list — every one of them a
            // SuiteValidationError kind the plan's list did not name.
            ["schema"] = "VFX-D-1101",
            ["yaml-parse"] = "VFX-D-1102",
            ["too-large"] = "VFX-D-1103",
            ["too-deep"] = "VFX-D-1104",
            ["alias-limit"] = "VFX-D-1105",
        };

        var actual = VfxCodeCatalogue.All
            .Where(entry => entry.LegacyKind is not null)
            .ToDictionary(entry => entry.LegacyKind!, entry => entry.Code, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void MandatedCodeAssignments_AreExactlyWhatWasCommittedTo()
    {
        // Three assignments that were fixed BEFORE this story and must not be re-derived:
        //   * VFX-E-1001 — the sprint plan names it exactly, as the PathOutsideWorkspace family.
        //   * VFX-D-1201 — spec §5.5 names it exactly; Sprint 2's semantic rules build on it.
        //   * VFX-E-1501 — VfxError.cs's own documentation cites it by name, WITH retryable: true,
        //     as its worked example.
        Assert.Equal("VFX-E-1001", VfxCodeCatalogue.PathOutsideWorkspace);
        Assert.Equal("VFX-D-1201", VfxCodeCatalogue.UnknownStepType);
        Assert.Equal("VFX-E-1501", VfxCodeCatalogue.RunInProgress);
        Assert.True(VfxCodeCatalogue.Get(VfxCodeCatalogue.RunInProgress).Retryable);
    }

    [Fact]
    public void EveryCode_IsUniqueWellFormedAndAgreesWithItsKind()
    {
        Assert.Distinct(VfxCodeCatalogue.All.Select(entry => entry.Code), StringComparer.Ordinal);
        Assert.Distinct(VfxCodeCatalogue.All.Select(entry => entry.Name), StringComparer.Ordinal);

        foreach (var entry in VfxCodeCatalogue.All)
        {
            var expectedPrefix = entry.Kind == VfxCodeKind.Error ? "VFX-E-" : "VFX-D-";
            Assert.StartsWith(expectedPrefix, entry.Code, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary), $"{entry.Code} has no summary.");

            // The real published shape US-S1-05 makes resolvable — this repo's own site, not the
            // engine's (see VfxCodeCatalogue.DocsUrlPrefix's remarks).
            Assert.Equal($"https://vouchfx-mcp.vouchfx.io/docs/errors/{entry.Code}.html", entry.DocsUrl);

            // A diagnostic is data on a successful call, so "retry it" is not a question that can
            // be asked about one — the field must stay false rather than carrying a meaningless
            // value a host might act on.
            if (entry.Kind == VfxCodeKind.Diagnostic)
            {
                Assert.False(entry.Retryable, $"{entry.Code} is a diagnostic and must not be marked retryable.");
            }
        }
    }

    [Fact]
    public void EveryCodeNumber_FallsInsideAReservedRange()
    {
        // VfxError/Diagnostic validate this at construction, but only for codes that actually get
        // constructed. This asserts it for the whole table up front, so an entry added in a
        // reserved-range GAP (notably 1800-1899, which spec §4.4 deliberately leaves unreserved) is
        // caught by this test rather than by a runtime throw on the first call that emits it.
        foreach (var entry in VfxCodeCatalogue.All)
        {
            var exception = Record.Exception(() => _ = entry.Kind == VfxCodeKind.Error
                ? new VfxError(entry.Code, entry.Summary, entry.Retryable)
                : (object)new Diagnostic(entry.Code, "error", entry.Summary, null, null, null, entry.DocsUrl));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void CreateError_RefusesADiagnosticCode_AndViceVersa()
    {
        // The guard that keeps the classification rule from being violated by a typo at a call
        // site: a D code can never reach the wire as isError, and an E code can never be dressed up
        // as data.
        Assert.Throws<ArgumentException>(
            () => VfxCodeCatalogue.CreateError(VfxCodeCatalogue.UnknownStepType, "wrong shape"));
        Assert.Throws<ArgumentException>(
            () => VfxCodeCatalogue.CreateDiagnostic(VfxCodeCatalogue.SuiteFileNotFound, "error", "wrong shape"));
    }

    [Fact]
    public void CreateError_TakesRetryableAndDocsUrlFromTheTable()
    {
        // Why the helpers exist at all: retryable and docsUrl are stated once, in the table, and a
        // call site cannot contradict them because it never gets to supply them.
        var error = VfxCodeCatalogue.CreateError(VfxCodeCatalogue.RunInProgress, "already running");

        Assert.True(error.Retryable);
        Assert.Equal("https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1501.html", error.DocsUrl);

        var notRetryable = VfxCodeCatalogue.CreateError(VfxCodeCatalogue.SuiteFileNotFound, "missing");
        Assert.False(notRetryable.Retryable);
    }

    [Fact]
    public void Get_RejectsAnUncataloguedCode()
    {
        // Fails loudly rather than degrading to an uncatalogued code that would slip past
        // US-S1-06's completeness gate. 1850 is inside spec §4.4's deliberate 1800-1899 gap, so it
        // is doubly invalid.
        Assert.Throws<ArgumentException>(() => VfxCodeCatalogue.Get("VFX-E-1850"));
    }

    private static IEnumerable<string> SourceFilesUnderSrc() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path));

    /// <summary>
    /// Excludes <c>bin/</c> and <c>obj/</c>, which contain generated sources (and, under
    /// <c>obj/</c>, copies of real ones) that are not part of the checked-in surface this scan is
    /// about. Checked on the REPO-RELATIVE path, not the raw absolute one: a checkout rooted
    /// somewhere that happens to contain a "bin" or "obj" path SEGMENT above the repo root (e.g. a
    /// machine-specific clone location like <c>C:\Users\x\bin\vouchfx-mcp</c>) must never cause a
    /// false exclusion — only a bin/obj segment INSIDE the repo tree counts. Mirrors
    /// <c>SecretHygieneSourceGuardTests</c>' and <c>ErrorCatalogueFilesystemParityTests</c>'
    /// identically-named helpers exactly (all three now share this same relative-path
    /// implementation, precisely so they cannot silently disagree on what counts as build output).
    /// The leading-segment checks below are unreachable via today's src/-rooted scans (every
    /// relative path here already carries a leading path component before any bin/obj segment) but
    /// keep this helper correct for a future caller rooted at the repo root (defence in depth, per
    /// the Copilot review on PR #69).
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

            return testsDir.Parent
                ?? throw new InvalidOperationException("Could not walk up to the repo root from the 'tests' directory.");
        }
    }
}
