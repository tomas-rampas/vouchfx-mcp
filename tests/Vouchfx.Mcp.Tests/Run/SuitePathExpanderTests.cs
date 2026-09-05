using Vouchfx.Mcp;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// Covers <see cref="SuitePathExpander"/> — US-S3-02's <c>paths</c> resolution: per-entry argument
/// safety, workspace-rooted glob expansion, the <c>*.e2e.yaml</c> filter, deterministic ordering and
/// de-duplication, and both caps.
/// </summary>
/// <remarks>
/// Unit-level and filesystem-real (a temp workspace tree, no mocks): a glob's whole job is to answer
/// a question about the filesystem, and a fake one would only prove that this test and the matcher
/// agree about a fiction. Nothing here spawns anything — expansion is deliberately the one stage of
/// <c>run_suite</c> that decides which files a run covers and nothing else.
/// </remarks>
public sealed class SuitePathExpanderTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root;
    private readonly Workspace _workspace;

    public SuitePathExpanderTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-expander-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "workspace");
        Directory.CreateDirectory(Path.Combine(_root, "e2e", "checkout"));
        Directory.CreateDirectory(Path.Combine(_root, "e2e", "billing"));

        WriteSuite("e2e/checkout/happy-path.e2e.yaml");
        WriteSuite("e2e/checkout/timeout-case.e2e.yaml");
        WriteSuite("e2e/billing/invoice.e2e.yaml");
        WriteFile("e2e/checkout/README.md", "not a suite");

        _workspace = Workspace.Resolve(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ── Per-entry argument safety ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-rf")]
    [InlineData("--danger")]
    [InlineData("--yaml-stdin")]
    public void Expand_EntryBeginningWithDash_IsInvalid(string entry)
    {
        var expansion = SuitePathExpander.Expand([entry], _workspace, allowGlobs: true);

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(expansion);
        Assert.Contains("begin with", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_NullOrBlankEntry_IsInvalid()
    {
        // A malformed MCP payload can legally place a JSON null inside a string array at runtime,
        // regardless of the compile-time annotations — the null-forgiving '!' models exactly that.
        IReadOnlyList<string> entries = ["e2e/checkout/happy-path.e2e.yaml", null!];

        Assert.IsType<SuitePathExpansion.Invalid>(SuitePathExpander.Expand(entries, _workspace, allowGlobs: true));
        Assert.IsType<SuitePathExpansion.Invalid>(SuitePathExpander.Expand(["   "], _workspace, allowGlobs: true));
    }

    [Fact]
    public void Expand_NoEntriesAtAll_IsInvalid()
    {
        Assert.IsType<SuitePathExpansion.Invalid>(SuitePathExpander.Expand([], _workspace, allowGlobs: true));
    }

    [Fact]
    public void Expand_TooManyEntries_IsInvalidBeforeAnyFilesystemWork()
    {
        var entries = Enumerable
            .Range(0, SuitePathExpander.MaxRequestedPaths + 1)
            .Select(index => $"e2e/checkout/suite-{index}.e2e.yaml")
            .ToArray();

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(
            SuitePathExpander.Expand(entries, _workspace, allowGlobs: true));
        Assert.Contains("Too many paths", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_GlobMatchingMoreThanTheExpandedCap_IsInvalidRatherThanTruncated()
    {
        var many = Path.Combine(_root, "e2e", "many");
        Directory.CreateDirectory(many);
        for (var index = 0; index <= SuitePathExpander.MaxExpandedPaths; index++)
        {
            WriteFile($"e2e/many/suite-{index:D3}.e2e.yaml", "metadata:\n  name: x\n");
        }

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(
            SuitePathExpander.Expand(["e2e/many/**"], _workspace, allowGlobs: true));

        // Refused, never silently truncated: a run that covered the first hundred matches would
        // report a verdict about a set the caller never chose.
        Assert.Contains("Too many suites", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count cap is reached by ACCUMULATION and stops the walk there — a set far past the cap,
    /// spread across several entries, is refused exactly as one entry past it is (a gatekeeper
    /// review's finding: the cap used to be checked only after every entry had been fully expanded,
    /// so fifty patterns over a large tree were all materialised on the way to being refused).
    /// </summary>
    /// <remarks>
    /// The ANSWER is what this pins, not the internal early exit: truncating a pattern's own matches
    /// at <c>MaxExpandedPaths + 1</c> cannot change it, because at most <c>MaxExpandedPaths</c>
    /// entries can already have been accumulated when it happens, so at least one of the sampled
    /// files is new and the total still exceeds the cap. See <c>SuitePathExpander.MatchGlob</c>'s
    /// remarks for that argument in full.
    /// </remarks>
    [Fact]
    public void Expand_FarMoreMatchesThanTheCapAcrossSeveralEntries_IsStillRefused()
    {
        for (var index = 0; index < 3; index++)
        {
            Directory.CreateDirectory(Path.Combine(_root, "e2e", $"bulk{index}"));
            for (var file = 0; file <= SuitePathExpander.MaxExpandedPaths; file++)
            {
                WriteFile($"e2e/bulk{index}/suite-{file:D3}.e2e.yaml", "metadata:\n  name: x\n");
            }
        }

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(SuitePathExpander.Expand(
            ["e2e/bulk0/**", "e2e/bulk1/**", "e2e/bulk2/**"], _workspace, allowGlobs: true));

        Assert.Contains("Too many suites", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The call's budget is observable BETWEEN entries: an already-cancelled token ends expansion
    /// with an <see cref="OperationCanceledException"/>, which
    /// <c>RunSuiteOrchestrator.RunAsync</c> normalises into its cancelled/timed-out result.
    /// </summary>
    /// <remarks>
    /// Deterministic by construction — a pre-cancelled token, not a race against a real walk. What it
    /// pins is that a token is CONSULTED at all: before the Sprint-3 review the whole-call budget was
    /// created after expansion had already run, so no amount of cancellation could reach here.
    /// </remarks>
    [Fact]
    public void Expand_WithAnAlreadyCancelledToken_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => SuitePathExpander.Expand(
            ["e2e/**"], _workspace, allowGlobs: true, cancelled.Token));
    }

    /// <summary>
    /// Every message this type echoes a caller's entry into is BOUNDED (a security review's MAJOR
    /// finding): all four echo sites used the uncapped sanitiser, so a multi-megabyte entry produced
    /// a multi-megabyte error message — and, because sanitising expands each non-printable character
    /// into a six-character escape, up to six times the input, built on the request thread.
    /// </summary>
    [Theory]
    [InlineData("leading-dash")]
    [InlineData("parent-segment")]
    [InlineData("absolute-glob")]
    [InlineData("no-matches")]
    public void Expand_AMultiMegabyteEntry_ProducesABoundedMessage(string shape)
    {
        // Control characters as well as length: the cap has to survive the sanitiser's own expansion,
        // which is why PathSafetyGuard.CapAndSanitisePathForDisplay caps twice.
        var hostile = string.Concat(Enumerable.Repeat("a" + (char)1 + "b", 1_000_000));

        IReadOnlyList<string> entry = shape switch
        {
            "leading-dash" => ["-" + hostile],
            "parent-segment" => ["../" + hostile + "/*.e2e.yaml"],
            "absolute-glob" => [Path.Combine(_root, hostile + "*")],
            _ => [hostile + "*.e2e.yaml"],
        };

        var expansion = SuitePathExpander.Expand(entry, _workspace, allowGlobs: true);

        var message = expansion switch
        {
            SuitePathExpansion.Invalid invalid => invalid.Message,
            SuitePathExpansion.NoMatches noMatches => noMatches.Message,
            _ => throw new InvalidOperationException($"Expected a refusal for the '{shape}' shape."),
        };

        // Two capped path renderings (1,000 characters each) plus the surrounding prose — nowhere
        // near the 3 MB the raw entry would have contributed, nor the 18 MB its sanitised form would.
        Assert.True(
            message.Length < 3_000,
            $"The '{shape}' message was {message.Length:N0} characters; it must stay bounded.");
    }

    /// <summary>
    /// The LENGTH cap, which exists so a run's registry entry stays readable — see
    /// <see cref="SuitePathExpander.MaxExpandedPathCharacters"/> for the arithmetic against
    /// <c>FileRunRegistry.MaxEntryFileBytes</c>. Without it a set of deeply-nested paths would be
    /// written into an entry the registry then skips as oversized, losing the run's record while the
    /// run itself proceeded.
    /// </summary>
    [Fact]
    public void Expand_PathsTotallingMoreThanTheCharacterCap_IsInvalid()
    {
        // Deep, long directory names: few enough paths to stay under the COUNT cap, long enough to
        // breach the character one — which is what makes them two independent axes.
        var deepDirectory = string.Join('/', Enumerable.Repeat(new string('d', 60), 12));
        var entries = Enumerable
            .Range(0, 40)
            .Select(index => $"{deepDirectory}/suite-{index:D3}.e2e.yaml")
            .ToArray();

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(
            SuitePathExpander.Expand(entries, _workspace, allowGlobs: true));
        Assert.Contains("too long in total", invalid.Message, StringComparison.Ordinal);
    }

    // ── Literal paths ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_RelativeLiteralPath_IsRebasedOntoTheWorkspaceRoot()
    {
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            ["e2e/checkout/happy-path.e2e.yaml"], _workspace, allowGlobs: true));

        Assert.Equal([Path.Combine(_root, "e2e", "checkout", "happy-path.e2e.yaml")], expanded);
    }

    [Fact]
    public void Expand_LiteralPathThatDoesNotExist_IsStillReturnedForTheGuardChainToRefuse()
    {
        // Expansion decides WHICH files a run covers, never whether they are valid — a missing file
        // is the pre-flight's answer (VFX-E-1002), reached identically whether the caller used
        // `path` or `paths`.
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            ["e2e/checkout/nope.e2e.yaml"], _workspace, allowGlobs: true));

        Assert.Equal([Path.Combine(_root, "e2e", "checkout", "nope.e2e.yaml")], expanded);
    }

    [Fact]
    public void Expand_LiteralPathOutsideTheRoot_IsReturnedUnchangedForContainmentToRefuse()
    {
        // The expander is NOT the containment boundary and must not pretend to be: it hands the path
        // on, and PathSafetyGuard (through the EDGE-003 pre-flight) is what refuses it. Asserted so a
        // future "helpful" filter here cannot silently become the only guard.
        var outside = Path.Combine(_sandbox, "outside.e2e.yaml");
        WriteFile("../outside.e2e.yaml", "metadata:\n  name: x\n");

        var expanded = AssertExpanded(SuitePathExpander.Expand([outside], _workspace, allowGlobs: true));

        Assert.Equal([outside], expanded);
    }

    // ── Globs ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_RecursiveGlob_SelectsEveryMatchingSuiteSortedAndFiltered()
    {
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            ["e2e/checkout/**"], _workspace, allowGlobs: true));

        // Sorted ordinally, and the README is not a suite — the filter is the engine's own discovery
        // rule (*.e2e.yaml), which is what makes `e2e/checkout/**` mean what the story says.
        Assert.Equal(
            [
                Path.Combine(_root, "e2e", "checkout", "happy-path.e2e.yaml"),
                Path.Combine(_root, "e2e", "checkout", "timeout-case.e2e.yaml"),
            ],
            expanded);
    }

    [Fact]
    public void Expand_GlobAcrossDirectories_SelectsEveryMatchingSuite()
    {
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            ["e2e/**/*.e2e.yaml"], _workspace, allowGlobs: true));

        Assert.Equal(
            [
                Path.Combine(_root, "e2e", "billing", "invoice.e2e.yaml"),
                Path.Combine(_root, "e2e", "checkout", "happy-path.e2e.yaml"),
                Path.Combine(_root, "e2e", "checkout", "timeout-case.e2e.yaml"),
            ],
            expanded);
    }

    [Fact]
    public void Expand_GlobMatchingNothing_ReportsNoMatchesRatherThanAnEmptySuccess()
    {
        var noMatches = Assert.IsType<SuitePathExpansion.NoMatches>(
            SuitePathExpander.Expand(["e2e/shipping/**"], _workspace, allowGlobs: true));

        Assert.Contains("matched no", noMatches.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A glob attempting to walk out of the workspace with <c>..</c> is REFUSED — and this test
    /// exists because the first implementation assumed it would simply match nothing, which is
    /// measurably false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against Microsoft.Extensions.FileSystemGlobbing 10.0.10: <c>../*.e2e.yaml</c> does
    /// NOT treat <c>..</c> as a literal segment — the matcher walks up out of its base directory and
    /// returns the file outside it. (A <c>..</c> anywhere other than the start throws
    /// <see cref="ArgumentException"/> instead: ".." can be only added at the beginning of the
    /// pattern".) Leaving that to containment would have been a hole in exactly the configuration
    /// where containment does not exist — no <c>--workspace</c>, where a pattern could then walk out
    /// of the current directory unchecked.
    /// </para>
    /// <para>
    /// This is expansion's own contribution, not the security guarantee. That one is the guard chain
    /// every expanded path still goes through — asserted end to end by
    /// <c>RunSuiteOrchestratorTests.RunAsync_PathsEntryOutsideTheWorkspace_IsRefusedBeforeAnythingRuns</c>,
    /// which drives an escaping ABSOLUTE entry through the pre-flight. Both halves are needed: this
    /// one proves a pattern cannot express the escape, that one proves the escape is refused even
    /// when a path does arrive.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("../*.e2e.yaml")]
    [InlineData("../**")]
    [InlineData("..\\*.e2e.yaml")]
    [InlineData("e2e/../../*.e2e.yaml")]
    public void Expand_GlobWithAParentSegment_IsInvalid(string pattern)
    {
        WriteFile("../outside.e2e.yaml", "metadata:\n  name: x\n");

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(
            SuitePathExpander.Expand([pattern], _workspace, allowGlobs: true));
        Assert.Contains("'..'", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_AbsoluteGlob_IsInvalid()
    {
        var absolutePattern = Path.Combine(_root, "e2e", "**");

        var invalid = Assert.IsType<SuitePathExpansion.Invalid>(
            SuitePathExpander.Expand([absolutePattern], _workspace, allowGlobs: true));
        Assert.Contains("workspace-relative", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_WithGlobsDisallowed_TreatsAPatternAsALiteralPath()
    {
        // The legacy scalar `path` input's meaning must not change: `e2e/**` has always been a file
        // name that does not exist, and it stays one.
        var expanded = AssertExpanded(SuitePathExpander.Expand(["e2e/**"], _workspace, allowGlobs: false));

        Assert.Equal([Path.Combine(_root, "e2e", "**")], expanded);
    }

    // ── Ordering and de-duplication ──────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_KeepsCallerOrderAcrossEntriesAndDropsDuplicates()
    {
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            [
                "e2e/billing/invoice.e2e.yaml",
                "e2e/checkout/**",

                // Already selected by the glob above, and again by name: one file, one run.
                "e2e/checkout/happy-path.e2e.yaml",
            ],
            _workspace,
            allowGlobs: true));

        Assert.Equal(
            [
                Path.Combine(_root, "e2e", "billing", "invoice.e2e.yaml"),
                Path.Combine(_root, "e2e", "checkout", "happy-path.e2e.yaml"),
                Path.Combine(_root, "e2e", "checkout", "timeout-case.e2e.yaml"),
            ],
            expanded);
    }

    [Fact]
    public void Expand_IsDeterministicAcrossRepeatedCalls()
    {
        var first = AssertExpanded(SuitePathExpander.Expand(["e2e/**"], _workspace, allowGlobs: true));
        var second = AssertExpanded(SuitePathExpander.Expand(["e2e/**"], _workspace, allowGlobs: true));

        Assert.Equal(first, second);
    }

    // ── No workspace configured ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_WithNoWorkspace_LeavesARelativePathToResolveAgainstTheCurrentDirectory()
    {
        // Byte-for-byte the pre-US-S3-08 behaviour: with no workspace nothing is rebased here, and
        // the path means whatever it has always meant relative to this process's directory.
        var expanded = AssertExpanded(SuitePathExpander.Expand(
            ["relative/suite.e2e.yaml"], workspace: null, allowGlobs: true));

        Assert.Equal(["relative/suite.e2e.yaml"], expanded);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> AssertExpanded(SuitePathExpansion expansion) =>
        Assert.IsType<SuitePathExpansion.Expanded>(expansion).Paths;

    private void WriteSuite(string relativePath) => WriteFile(
        relativePath,
        """
        metadata:
          name: "A suite"
          owner: "platform-team"

        steps:
          - id: check-health
            type: http.rest
            description: "Confirms the health endpoint responds successfully."
            target: orders-api
            method: GET
            path: /health
        """);

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.GetFullPath(relativePath, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
