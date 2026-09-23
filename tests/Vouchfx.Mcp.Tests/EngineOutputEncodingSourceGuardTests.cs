using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Source-level guard for issue #89's central structural claim: there is exactly ONE place in
/// <c>src/</c> that decides what encoding the <c>vouchfx</c> engine writes its redirected output in
/// — <c>src/Vouchfx.Mcp/Cli/EngineOutputEncoding.cs</c> — and nothing in this server ever CHANGES
/// the console's encoding. Extended by vouchfx-mcp#115 with a second, related claim: every place in
/// <c>src/</c> that reads a spawned child's redirected stdout/stderr does so through that ONE
/// resolution, never through the DEFAULT-decoding <see cref="StreamReader"/> a bare
/// <c>Process.StandardOutput</c>/<c>StandardError</c> access hands back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one resolution site is a structural invariant rather than a tidiness preference.</b> Two
/// consumers now depend on the same answer, and they depend on it in OPPOSITE directions:
/// <c>VouchfxCliProcessRunner</c> DECODES the engine's bytes with it, and
/// <c>GetSchemaOrchestrator</c> MODELS the engine's ENCODE with it (it projects the vendored schema
/// through that encoding so a lossy console page stops being reported as schema drift). If a second
/// copy of the resolution appeared and the two ever disagreed, the projection would model a
/// transcoding the decode did not perform — and the symptom would be the exact bug #89 fixed,
/// VFX-D-1106 on every single <c>get_schema</c> call, with every existing test still green. That is
/// invisible to the build, the analyzers, the formatter and every behavioural test in this repo,
/// which is what makes it a guard's job.
/// </para>
/// <para>
/// <b>vouchfx-mcp#115's addition, and why IT needed a guard too.</b> Before #115,
/// <c>VouchfxCliSuiteRunner</c> (the <c>run_suite</c> spawn site) read <c>process.StandardOutput</c>/
/// <c>StandardError</c> DIRECTLY — a working, green, fully-tested code path that simply decoded with
/// whatever the OS/runtime defaults to, silently disagreeing with <c>VouchfxCliProcessRunner</c>'s
/// decode on a non-UTF-8 Windows console. Nothing short of a source-level rule can catch that
/// specific regression coming back: the shape compiles, every behavioural test passes (they either
/// inject an encoding or run where the two happen to agree), and only a REAL non-UTF-8 console
/// surfaces the divergence. <see cref="TheCodePageResolutionShapes_LiveOnlyInEngineOutputEncoding"/>
/// and <see cref="TheConsoleMutatingShapes_AppearNowhereInSrc"/> above are unchanged and still guard
/// issue #89's claim; the tests below guard #115's — a THIRD spawn site, or a regression in one of
/// the two named engine ones, fails by name rather than by a field report.
/// </para>
/// <para>
/// <b>Mirrors <see cref="CursorCallSiteSourceGuardTests"/>'s shape exactly</b> — whitespace-tolerant
/// regexes over source with comments and string literals stripped
/// (<see cref="SourceGuardScan.ExecutableSourceOf"/>), a fail-closed EXACT-equality check against a
/// named file set, and an anti-vacuity test asserting the named file still actually contains each
/// shape, so a rename or a gutting cannot make this guard pass over nothing. Comment stripping is
/// what lets the rule be DOCUMENTED beside the code it governs: several files legitimately discuss
/// <c>GetConsoleOutputCP</c> and <c>SetConsoleOutputCP</c> in prose (this file's subject matter is
/// hard to explain otherwise), and a prose mention is not a call.
/// </para>
/// <para>
/// <b>What it cannot catch, stated honestly.</b> It bounds WHERE the P/Invoke and code-page APIs may
/// live, not the existence of some other route to the same information — a future file that shelled
/// out to <c>chcp</c> and parsed the output would never match these patterns. The most plausible
/// second route, <c>Console.OutputEncoding</c>'s GETTER (on Windows, <c>GetConsoleOutputCP</c>
/// wrapped in an <c>OSEncoding</c>), IS covered — it was added after a review pointed out that a
/// guard naming only the P/Invoke blesses the managed equivalent by omission — but that closes one
/// known route, not the category. What this does guarantee is the direction that matters: the named
/// APIs cannot acquire a second call site without failing by name, and the console-MUTATING APIs
/// cannot acquire a first one at all. Symmetrically, #115's addition bounds WHERE a child's redirected
/// stream may be read at all and requires <c>.BaseStream</c> immediately after
/// <c>.StandardOutput</c>/<c>.StandardError</c> — a future site that opened a named pipe some OTHER
/// way (bypassing <c>Process.StandardOutput</c>/<c>StandardError</c> entirely) would not match either.
/// </para>
/// </remarks>
public class EngineOutputEncodingSourceGuardTests
{
    /// <summary>
    /// The ONLY file in <c>src/</c> allowed to resolve the console output code page or touch the
    /// code-pages encoding provider, and the only one allowed to declare a P/Invoke at all.
    /// </summary>
    private const string EngineOutputEncodingRelativePath = "src/Vouchfx.Mcp/Cli/EngineOutputEncoding.cs";

    /// <summary>
    /// Shapes that may appear ONLY in <see cref="EngineOutputEncodingRelativePath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MustBePresent</c> distinguishes the four shapes that file actually uses today — asserted
    /// present by <see cref="TheOwningFile_StillContainsEveryShapeItIsSupposedTo"/>, so a rename or a
    /// refactor that moved them elsewhere fails loudly rather than silently narrowing this guard —
    /// from <c>LibraryImport</c>, which appears NOWHERE in <c>src/</c> today. That one is listed
    /// because it is the source-generated equivalent of <c>DllImport</c>: restricting one and not the
    /// other would let a future P/Invoke land anywhere simply by choosing the newer attribute. (It
    /// also cannot be used in this project at all without enabling <c>AllowUnsafeBlocks</c>, which
    /// the csproj deliberately does not — see the comment above the <c>DllImport</c> in the owning
    /// file.)
    /// </para>
    /// <para>
    /// The <c>DllImport</c> patterns are anchored with <c>\b</c> on BOTH sides of the token and
    /// require an opening paren, so they match the attribute USAGE (<c>[DllImport("kernel32.dll")]</c>,
    /// <c>[DllImportAttribute(…)]</c>) and deliberately not <c>DefaultDllImportSearchPaths</c> or
    /// <c>DllImportSearchPath</c> — the hardening attributes that legitimately sit beside a permitted
    /// P/Invoke and are not themselves declarations.
    /// </para>
    /// </remarks>
    private static readonly (string Description, Regex Pattern, bool MustBePresent)[] SingleSiteShapes =
    [
        ("GetConsoleOutputCP — reading the console output code page",
            new Regex(@"\bGetConsoleOutputCP\b", RegexOptions.Compiled), true),
        ("CodePagesEncodingProvider — the OEM/ANSI encoding provider",
            new Regex(@"\bCodePagesEncodingProvider\b", RegexOptions.Compiled), true),
        ("Encoding.RegisterProvider(...) — a process-global encoding registration",
            new Regex(@"Encoding\s*\.\s*RegisterProvider\s*\(", RegexOptions.Compiled), true),
        ("a [DllImport(...)] P/Invoke declaration",
            new Regex(@"\bDllImport(Attribute)?\b\s*\(", RegexOptions.Compiled), true),
        ("a [LibraryImport(...)] P/Invoke declaration",
            new Regex(@"\bLibraryImport(Attribute)?\b\s*\(", RegexOptions.Compiled), false),

        // Console.OutputEncoding's GETTER is the obvious second route to the same fact: on Windows it
        // is GetConsoleOutputCP wrapped in an OSEncoding. A file that read it would be resolving the
        // engine's output encoding independently — the exact duplication this guard exists to stop —
        // while matching none of the patterns above. Restricted rather than forbidden: if the
        // resolution ever prefers the managed getter to the P/Invoke, that belongs in the owning file
        // like everything else. MustBePresent is false because nothing in src/ reads it today
        // (verified by scan, not by memory), so asserting its presence would be asserting a fiction.
        ("a read of Console.OutputEncoding / Console.InputEncoding",
            new Regex(@"Console\s*\.\s*(Output|Input)Encoding\b", RegexOptions.Compiled), false),
    ];

    /// <summary>
    /// Shapes that may appear NOWHERE in <c>src/</c>: anything that MUTATES the console's encoding
    /// rather than reading it.
    /// </summary>
    /// <remarks>
    /// A stdio MCP server shares its console with whatever host spawned it, so repointing that
    /// console's code page would be this server reaching outside its own process boundary — it would
    /// change how the HOST's own output renders, for the life of that console, as a side effect of
    /// starting a test server. It would also be a tempting "fix" for #89 (force 65001 and the
    /// transcoding stops), which is precisely why it needs a guard rather than a comment: modelling
    /// the code page leaves the operator's environment alone, and there must be no second,
    /// environment-mutating path to the same outcome.
    /// </remarks>
    private static readonly (string Description, Regex Pattern)[] ForbiddenEverywhereShapes =
    [
        ("SetConsoleOutputCP — changing the console's output code page",
            new Regex(@"\bSetConsoleOutputCP\b", RegexOptions.Compiled)),
        ("SetConsoleCP — changing the console's input code page",
            new Regex(@"\bSetConsoleCP\b", RegexOptions.Compiled)),

        // `[^=]` rather than a lookahead so a bare `=` at end-of-file cannot match, and so `==`
        // (a comparison, which several comments and a future assertion might legitimately write)
        // is not mistaken for an assignment.
        ("an assignment to Console.OutputEncoding / Console.InputEncoding",
            new Regex(@"Console\s*\.\s*(Output|Input)Encoding\s*=[^=]", RegexOptions.Compiled)),
    ];

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // vouchfx-mcp#115: every spawn site that reads a child's redirected stdout/stderr must route
    // through EngineOutputEncoding, never a default-decoding reader. See this class's remarks.
    // ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The only files in <c>src/</c> allowed to read a spawned child process's redirected stdout or
    /// stderr AT ALL — the structural "a third spawn site cannot reintroduce a default decode" half
    /// of vouchfx-mcp#115. Two are the ENGINE's own spawn sites (<c>vouchfx</c> itself), whose console
    /// output is real, uncontrolled text and therefore additionally governed by
    /// <see cref="EngineChildSpawnRelativePaths"/> below; the other two spawn THIS SERVER's own
    /// <c>--validate-worker</c>/<c>--spec-index-worker</c> modes, whose stdout is pure ASCII BY
    /// CONSTRUCTION — each worker's JSON is serialised through a <c>JavaScriptEncoder</c> that escapes
    /// every non-ASCII character (see <c>Vouchfx.Mcp.BoundedStreamReader</c>'s remarks) — so a UTF-8
    /// (or any ASCII-compatible) decode is correct there regardless of console code page, and
    /// <c>EngineOutputEncoding</c> legitimately has nothing to do with them. Named here anyway, so a
    /// brand-new FIFTH reader cannot appear silently either.
    /// </summary>
    private static readonly string[] ChildProcessOutputReaderRelativePaths =
    [
        "src/Vouchfx.Mcp/Cli/VouchfxCliProcessRunner.cs",
        "src/Vouchfx.Mcp/Run/VouchfxCliSuiteRunner.cs",
        "src/Vouchfx.Mcp/Specs/SpecIndexWorkerClient.cs",
        "src/Vouchfx.Mcp/Validation/ValidationWorkerClient.cs",
    ];

    /// <summary>
    /// The subset of <see cref="ChildProcessOutputReaderRelativePaths"/> that spawn the ENGINE
    /// (<c>vouchfx</c>) itself and therefore MUST decode through <c>EngineOutputEncoding.Current</c> —
    /// the floor half of vouchfx-mcp#115, which closed <c>VouchfxCliSuiteRunner</c>'s divergence from
    /// <c>VouchfxCliProcessRunner</c>'s already-correct decode. The other two files in
    /// <see cref="ChildProcessOutputReaderRelativePaths"/> are deliberately NOT named here — see that
    /// field's own remarks for why they are exempt rather than merely unchecked.
    /// </summary>
    private static readonly string[] EngineChildSpawnRelativePaths =
    [
        "src/Vouchfx.Mcp/Cli/VouchfxCliProcessRunner.cs",
        "src/Vouchfx.Mcp/Run/VouchfxCliSuiteRunner.cs",
    ];

    /// <summary>
    /// Any reference to <c>Process.StandardOutput</c>/<c>Process.StandardError</c> — the only two
    /// members through which this codebase's <c>Process</c> objects expose a spawned child's
    /// redirected streams, so matching the member name (rather than a receiver expression this
    /// text-level guard cannot resolve) is exact for this codebase without needing a full C# parse.
    /// Deliberately does NOT match <c>ProcessStartInfo.RedirectStandardOutput</c>/
    /// <c>RedirectStandardError</c> (no <c>.</c> immediately precedes <c>Standard</c> inside that
    /// single identifier) — see <see cref="TheChildProcessOutputPatterns_MatchTheShapesTheyClaimToAndNotTheirNeighbours"/>.
    /// </summary>
    private static readonly Regex StandardOutputOrErrorAccess =
        new(@"\.\s*Standard(Output|Error)\b", RegexOptions.Compiled);

    /// <summary>
    /// The FORBIDDEN shape (vouchfx-mcp#115): <c>.StandardOutput</c>/<c>.StandardError</c> used for
    /// anything OTHER than immediately reaching <c>.BaseStream</c> — i.e. read as the
    /// DEFAULT-decoding <see cref="StreamReader"/> that <c>Process</c> itself constructs, which
    /// decodes with whatever the OS/runtime defaults to rather than
    /// <c>EngineOutputEncoding.Current</c>. This is precisely the shape <c>VouchfxCliSuiteRunner</c>
    /// used before #115 (<c>RelayAsync(process.StandardOutput, …)</c>) and precisely the shape a
    /// reverted or re-introduced default decode would have again.
    /// </summary>
    private static readonly Regex StandardOutputOrErrorNotFollowedByBaseStream =
        new(@"\.\s*Standard(Output|Error)\b(?!\s*\.\s*BaseStream\b)", RegexOptions.Compiled);

    /// <summary>
    /// A reference to <c>EngineOutputEncoding.Current</c> — the floor half of vouchfx-mcp#115's rule:
    /// each file in <see cref="EngineChildSpawnRelativePaths"/> must actually USE the single resolved
    /// encoding, not merely reach <c>.BaseStream</c> with some other (e.g. hardcoded) one.
    /// </summary>
    private static readonly Regex EngineOutputEncodingCurrentReference =
        new(@"EngineOutputEncoding\s*\.\s*Current\b", RegexOptions.Compiled);

    [Fact]
    public void TheCodePageResolutionShapes_LiveOnlyInEngineOutputEncoding()
    {
        var offenders = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var relative = SourceGuardScan.ToRepoRelativeForwardSlashPath(file);
            if (string.Equals(relative, EngineOutputEncodingRelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            var executable = SourceGuardScan.ExecutableSourceOf(file);
            foreach (var (description, pattern, _) in SingleSiteShapes)
            {
                if (pattern.IsMatch(executable))
                {
                    offenders.Add($"{relative}: {description}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheConsoleMutatingShapes_AppearNowhereInSrc()
    {
        var offenders = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var executable = SourceGuardScan.ExecutableSourceOf(file);
            foreach (var (description, pattern) in ForbiddenEverywhereShapes)
            {
                if (pattern.IsMatch(executable))
                {
                    offenders.Add($"{SourceGuardScan.ToRepoRelativeForwardSlashPath(file)}: {description}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheOwningFile_StillContainsEveryShapeItIsSupposedTo()
    {
        // Anti-vacuity, in the direction the set check above cannot see: a renamed, deleted or
        // gutted EngineOutputEncoding.cs would make both tests above pass over nothing at all.
        var fullPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            EngineOutputEncodingRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(
            File.Exists(fullPath),
            $"Expected a tracked file at '{fullPath}' — update this guard if the single resolution site moved.");

        var executable = SourceGuardScan.ExecutableSourceOf(fullPath);
        foreach (var (description, pattern, _) in SingleSiteShapes.Where(shape => shape.MustBePresent))
        {
            Assert.True(
                pattern.IsMatch(executable),
                $"{EngineOutputEncodingRelativePath} no longer contains {description}. Either the "
                + "resolution moved (update this guard's file name) or it was removed (update this "
                + "guard's shape list) — do not leave a guard asserting over nothing.");
        }
    }

    [Fact]
    public void ThePatterns_MatchTheShapesTheyClaimToAndNotTheirNeighbours()
    {
        // Sanity checks for the regexes above, because the whole guard is only as good as they are.
        var dllImport = SingleSiteShapes.Single(shape => shape.Description.Contains("[DllImport", StringComparison.Ordinal)).Pattern;

        Assert.Matches(dllImport, "[DllImport(\"kernel32.dll\")]");
        Assert.Matches(dllImport, "[DllImportAttribute(\"kernel32.dll\")]");

        // The two hardening attributes that legitimately accompany a permitted P/Invoke. Both CONTAIN
        // the text "DllImport" and neither is a declaration, so a naive substring check would have
        // made this guard unimplementable beside its own subject.
        Assert.DoesNotMatch(dllImport, "[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]");
        Assert.DoesNotMatch(dllImport, "DllImportSearchPath.System32");

        var consoleAssignment = ForbiddenEverywhereShapes
            .Single(shape => shape.Description.Contains("assignment to Console", StringComparison.Ordinal)).Pattern;

        Assert.Matches(consoleAssignment, "Console.OutputEncoding = Encoding.UTF8;");
        Assert.Matches(consoleAssignment, "Console . InputEncoding = new UTF8Encoding(false);");

        // A READ is not an assignment — it is handled by the SINGLE-SITE rule instead (restricted to
        // the owning file), not by the forbidden-everywhere rule, so the two patterns must not
        // overlap on it. A comparison is likewise not an assignment.
        Assert.DoesNotMatch(consoleAssignment, "var page = Console.OutputEncoding.CodePage;");
        Assert.DoesNotMatch(consoleAssignment, "if (Console.OutputEncoding == Encoding.UTF8)");

        var consoleRead = SingleSiteShapes
            .Single(shape => shape.Description.Contains("read of Console", StringComparison.Ordinal)).Pattern;

        Assert.Matches(consoleRead, "var page = Console.OutputEncoding.CodePage;");
        Assert.Matches(consoleRead, "Console . InputEncoding . CodePage");

        // The read pattern deliberately DOES match the setter too — a setter is a mention of the
        // member, and one rule forbidding it everywhere plus one restricting it to the owning file
        // compose to "forbidden everywhere", which is the stricter and correct reading.
        Assert.Matches(consoleRead, "Console.OutputEncoding = Encoding.UTF8;");

        // But an unrelated member that merely starts the same way is not a code-page read.
        Assert.DoesNotMatch(consoleRead, "Console.OutputEncodingChanged += OnChanged;");
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // vouchfx-mcp#115
    // ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChildProcessOutputIsReadOnlyByTheFourNamedSpawnSites()
    {
        // "A third spawn site cannot reintroduce a default decode" (vouchfx-mcp#115), structural and
        // exact-equality — the same shape CursorCallSiteSourceGuardTests uses for its call sites, and
        // double-direction the same way: a NEW file referencing .StandardOutput/.StandardError fails
        // by not being in the named set, and a named file that stops referencing it fails just as
        // loudly (nothing here can silently narrow what this guard covers).
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => StandardOutputOrErrorAccess.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ChildProcessOutputReaderRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    [Fact]
    public void EveryStandardOutputOrErrorAccess_ReachesOnlyBaseStream_NeverTheDefaultDecodingReader()
    {
        // The universal floor (vouchfx-mcp#115), checked across ALL of src/ rather than only the four
        // named files above: wherever `.StandardOutput`/`.StandardError` is referenced, it may ONLY be
        // used to reach `.BaseStream`. Reading it any other way reconstructs the DEFAULT-decoding
        // StreamReader Process itself builds, bypassing EngineOutputEncoding entirely — exactly the
        // shape vouchfx-mcp#115 closed in VouchfxCliSuiteRunner (`RelayAsync(process.StandardOutput,
        // …)`) and exactly the shape a regression, in ANY of the four files, would reintroduce.
        var offenders = new List<string>();

        foreach (var file in SourceGuardScan.SourceFilesInSrc())
        {
            var executable = SourceGuardScan.ExecutableSourceOf(file);
            if (StandardOutputOrErrorNotFollowedByBaseStream.IsMatch(executable))
            {
                offenders.Add(SourceGuardScan.ToRepoRelativeForwardSlashPath(file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheEngineChildSpawnSites_DecodeThroughEngineOutputEncodingCurrent()
    {
        // The floor half of vouchfx-mcp#115, per file: VouchfxCliProcessRunner and (since this issue)
        // VouchfxCliSuiteRunner must each actually reference EngineOutputEncoding.Current — reaching
        // `.BaseStream` alone is not enough; a file could do that and still decode with some other,
        // hardcoded encoding. Anti-vacuity in the same style as
        // TheOwningFile_StillContainsEveryShapeItIsSupposedTo: a renamed or gutted file fails loudly
        // rather than this guard silently passing over nothing.
        foreach (var relativePath in EngineChildSpawnRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(
                File.Exists(fullPath),
                $"Expected a tracked file at '{fullPath}' — update this guard if it moved.");

            Assert.Matches(EngineOutputEncodingCurrentReference, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }

    [Fact]
    public void TheChildProcessOutputPatterns_MatchTheShapesTheyClaimToAndNotTheirNeighbours()
    {
        // Sanity checks for the three regexes above, because the whole guard is only as good as they
        // are.
        Assert.Matches(StandardOutputOrErrorAccess, "process.StandardOutput.BaseStream");
        Assert.Matches(StandardOutputOrErrorAccess, "process.StandardError.BaseStream");
        Assert.Matches(StandardOutputOrErrorAccess, "process . StandardOutput");

        // ProcessStartInfo's OWN property names contain "Standard(Output|Error)" as a substring but
        // are never preceded by a `.` immediately before it — a single identifier, not a member
        // access on the result of reading .StandardOutput/.StandardError — so they must not match.
        Assert.DoesNotMatch(StandardOutputOrErrorAccess, "RedirectStandardOutput = true");
        Assert.DoesNotMatch(StandardOutputOrErrorAccess, "startInfo.RedirectStandardError = true;");

        // The forbidden shape: NOT immediately followed by .BaseStream.
        Assert.Matches(StandardOutputOrErrorNotFollowedByBaseStream, "RelayAsync(process.StandardOutput, onLine)");
        Assert.Matches(StandardOutputOrErrorNotFollowedByBaseStream, "process.StandardError.ReadToEndAsync()");

        // The permitted shape: immediately followed by .BaseStream, whitespace- and newline-tolerant.
        Assert.DoesNotMatch(StandardOutputOrErrorNotFollowedByBaseStream, "process.StandardOutput.BaseStream");
        Assert.DoesNotMatch(StandardOutputOrErrorNotFollowedByBaseStream, "process . StandardError . BaseStream");
        Assert.DoesNotMatch(
            StandardOutputOrErrorNotFollowedByBaseStream, "process.StandardOutput\n    .BaseStream");

        Assert.Matches(EngineOutputEncodingCurrentReference, "EngineOutputEncoding.Current");
        Assert.Matches(EngineOutputEncodingCurrentReference, "EngineOutputEncoding . Current");

        // A same-named member on an unrelated type is not a reference to THE resolution site.
        Assert.DoesNotMatch(EngineOutputEncodingCurrentReference, "SomeOtherEncoding.Current");
    }
}
