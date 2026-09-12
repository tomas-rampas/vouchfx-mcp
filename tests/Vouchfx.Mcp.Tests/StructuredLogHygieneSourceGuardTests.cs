using System.Text.RegularExpressions;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// The secret-hygiene guard for US-S6-05's new logging surface — a sibling of
/// <see cref="SecretHygieneSourceGuardTests"/>, extending the same rule to the structured logger's
/// call sites and to the DI-less writer itself (AC3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source guard and not only a content assertion.</b> The wire tests can prove that the
/// records emitted on one particular run carried no secret. They cannot prove that a record added
/// next month will not, because the dangerous thing is not a value — it is a CALL SITE that
/// interpolates something unbounded into a message. This pins the set of files that may write a
/// structured record at all, so a new writer in a component holding suite text, an environment
/// dictionary, or an engine stderr excerpt fails by name.
/// </para>
/// <para>
/// The companion rule is the one the writer itself enforces: every message reaching stderr is a
/// literal composed in the call site, with only server-minted identifiers and bounded tokens
/// interpolated — never caller text, never a resolved secret, never an environment value.
/// </para>
/// <para>
/// <b>WHAT THIS GUARD DOES NOT REACH, stated here rather than only in the published docs.</b> Its
/// reach is the files in <see cref="StructuredLogCallSiteRelativePaths"/> — the permitted emitters,
/// plus whatever is deliberately added to that list. It does NOT cover a future component that takes
/// an injected <see cref="Microsoft.Extensions.Logging.ILogger"/> and logs through it: such a
/// component reaches stderr through <c>StructuredConsoleFormatter</c> without ever naming
/// <c>StructuredLog</c>, so the call-site pin does not see it, and its own file is not scanned, so
/// the content scan does not see what it interpolates into a message either.
/// </para>
/// <para>
/// That is a real boundary, not a defect to fix here by widening the scan to all of <c>src/</c> —
/// which would make every <c>.Message</c> in the codebase this guard's business and drown the
/// allow-list. <b>What actually mitigates it changed in US-S6-06, and the earlier version of this
/// paragraph is now wrong:</b> it named <c>Log.cs</c>'s three <c>[LoggerMessage]</c> templates as
/// the chokepoint a reviewer would notice a fourth being added to. That file no longer exists — the
/// startup banners moved to direct <see cref="Vouchfx.Mcp.Observability.StructuredLog"/> calls so the
/// HTTP transport would get them too, and nothing was left for it to hold.
/// </para>
/// <para>
/// The mitigation that is true today is narrower and worth stating plainly: <b>no component in
/// <c>src/</c> takes an injected logger at all.</b> Every line this server writes on its own behalf
/// goes through the direct calls this guard pins; what reaches
/// <c>StructuredConsoleFormatter</c> is Hosting, SDK and Kestrel output, which this repository does
/// not author. So the gap is not "a file we forgot to list" but "the first component that ever asks
/// DI for an <c>ILogger</c>" — a conspicuous act in a codebase with no precedent for it. When one
/// appears, add its file to the allow-list above and the content scan follows it automatically.
/// </para>
/// </remarks>
public class StructuredLogHygieneSourceGuardTests
{
    /// <summary>
    /// The only files in <c>src/</c> permitted to emit a structured log record.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>Observability/StructuredConsoleFormatter.cs</c> — the <c>ILogger</c>
    /// entry point. Since US-S6-06 deleted <c>Log.cs</c> it renders Hosting/SDK/Kestrel output rather
    /// than anything this server authors, and it forwards only the already-composed message text plus
    /// an exception's TYPE name — never its own material.</description></item>
    /// <item><description><c>Run/RunSuiteOrchestrator.cs</c> — the DI-less entry point, emitting the
    /// four run-lifecycle records (started, completed, threw, completion-not-recorded), carrying a
    /// server-minted run id, a suite COUNT, the taxonomy verdict enum, a duration, and an exception's
    /// TYPE name. No suite path, no engine output, no exception message.</description></item>
    /// <item><description><c>Program.cs</c> — the eight startup-FAILURE writes, each a single already
    /// sanitised line composed by <c>PathSafetyGuard</c>/<c>PinFailureReporting</c> or by
    /// <c>Workspace.TryParseCommandLine</c>. These are the server path's last words before a non-zero
    /// exit, and they were plain text until US-S6-05 routed them here so the one-object-per-line
    /// claim would hold for every server-path line rather than most of them.</description></item>
    /// </list>
    /// <para>
    /// <c>StructuredLog.cs</c> is deliberately ABSENT: it is the DEFINITION rather than a call site —
    /// the same convention <see cref="SpecIndexParserSourceGuardTests"/> and
    /// <see cref="ToolTelemetrySourceGuardTests"/> follow, where the declaring file does not
    /// allow-list itself; it is separately checked by
    /// <see cref="TheWriter_NeverReadsTheProcessEnvironment"/>. Program.cs's presence here is itself
    /// a correction: an earlier draft named it on the assumption that it called the writer, was
    /// corrected by RUNNING the scan when it turned out to log only through <c>ILogger</c>, and is
    /// back now that US-S6-05's routing made the assumption true. Derive this list from the scan, not
    /// from a mental model — it has now been wrong in both directions.
    /// </para>
    /// A fourth file here means a component that holds richer material has started logging; establish
    /// what it interpolates before adding it.
    /// </remarks>
    private static readonly string[] StructuredLogCallSiteRelativePaths =
    [
        "src/Vouchfx.Mcp/Observability/StructuredConsoleFormatter.cs",
        "src/Vouchfx.Mcp/Program.cs",
        "src/Vouchfx.Mcp/Run/RunSuiteOrchestrator.cs",
        "src/Vouchfx.Mcp/Transport/HttpTransportHost.cs",
    ];

    /// <summary>Emission of a structured record, through any of its spellings.</summary>
    /// <remarks>
    /// <para>
    /// Three alternations, because two of them were not enough:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>StructuredLog.Write(…)</c> / <c>StructuredLog.ForRun(…)</c> — the
    /// type-qualified form, which is how every current call site spells it.</description></item>
    /// <item><description><c>.Write(LogLevel.…)</c> — a record written through a
    /// <c>RunLogScope</c> held in a local, where the type name does not appear on the
    /// line.</description></item>
    /// <item><description><c>Write(LogLevel.…)</c> with NO receiver — the <c>using static</c>
    /// evasion. A file carrying <c>using static Vouchfx.Mcp.Observability.StructuredLog;</c> can emit
    /// records without the string "StructuredLog" appearing anywhere in it, which would have walked
    /// straight past the first two alternations. The lookbehind <c>(?&lt;![.\w])</c> is what keeps
    /// this from also matching the two receiver forms above and any method merely ENDING in
    /// "Write".</description></item>
    /// </list>
    /// </remarks>
    private static readonly Regex StructuredLogEmission =
        new(
            @"StructuredLog\s*\.\s*(Write|ForRun)\s*\(" +
            @"|\.\s*Write\s*\(\s*LogLevel\s*\." +
            @"|(?<![.\w])Write\s*\(\s*LogLevel\s*\.",
            RegexOptions.Compiled);

    /// <summary>
    /// Shapes that would put unbounded or secret-bearing material into a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rename-proof by construction.</b> An earlier revision matched <c>ex.Message</c> and
    /// <c>failure.Message</c> by VARIABLE NAME, which a rename to <c>error</c> or <c>cause</c> walks
    /// straight past — the guard would have kept reporting success while the shape it forbids sat in
    /// the file. These match the MEMBER on any identifier instead, which is the part that cannot be
    /// renamed away because it is the BCL's.
    /// </para>
    /// <para>
    /// Matching that broadly necessarily catches benign uses too — <c>RunVerdict.ToString()</c> and
    /// this server's own catalogued <c>VfxError.Message</c> are both legitimate and both match. Those
    /// are handled by <see cref="KnownBenignMaterialMatches"/>, a fail-closed EXACT-equality
    /// allow-list, rather than by narrowing the pattern until it stops seeing them: a narrower pattern
    /// silently re-opens the hole, whereas an allow-list makes every new match a test failure that
    /// names the offending source line.
    /// </para>
    /// </remarks>
    private static readonly (string Description, Regex Pattern)[] ForbiddenMaterialShapes =
    [
        // No trailing `\(`, deliberately: requiring the open paren missed the METHOD-GROUP form by
        // exactly one character. `Environment.GetEnvironmentVariable` passed as a delegate — which is
        // how Program.cs supplies the environment reader to the transport parse — reads the
        // environment just as effectively as calling it, and the previous pattern could not see it.
        // The `\b` matches both spellings.
        ("Environment.GetEnvironmentVariable — a raw environment value, called OR passed as a delegate",
            new Regex(@"Environment\s*\.\s*GetEnvironmentVariable\b", RegexOptions.Compiled)),
        ("Environment.GetEnvironmentVariables — the whole environment, called or passed",
            new Regex(@"Environment\s*\.\s*GetEnvironmentVariables\b", RegexOptions.Compiled)),
        ("a .Message / .StackTrace / .InnerException member — an exception's own text routinely " +
         "embeds a full filesystem path, and a stack trace embeds the build machine's",
            new Regex(@"\.\s*(Message|StackTrace|InnerException)\b", RegexOptions.Compiled)),
        (".ToString() — on an exception this yields message, type and stack trace at once",
            new Regex(@"\.\s*ToString\s*\(\s*\)", RegexOptions.Compiled)),

        // US-S6-06: the HTTP bearer token. A log record naming it would hand an operator's transport
        // credential to whatever ships their stderr — and unlike a suite secret, this one grants
        // access to every tool on this server. Matching the VARIABLE NAME rather than a value is the
        // only shape a static scan can see, and it is the shape a call site would actually use
        // (interpolating the configured token, or echoing the variable it came from).
        ("the HTTP bearer token or its environment variable",
            new Regex(@"VOUCHFX_MCP_HTTP_TOKEN|BearerTokenVariable|BearerToken\b", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Every line in a guarded file that matches a <see cref="ForbiddenMaterialShapes"/> pattern and
    /// has been REVIEWED as benign, recorded as its trimmed source text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two families, both measured 2026-09-12 and both safe for the same reason — neither touches an
    /// EXCEPTION:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>.Message</c> on this server's OWN catalogued result types
    /// (<c>InvalidArgument</c>, <c>NoSuitesMatched</c>, the three <c>CliPinResult</c> cases). These
    /// are <c>VfxError</c>-family strings this server composed itself — bounded, catalogued, and
    /// already destined for a tool result. They never reach a log record.</description></item>
    /// <item><description><c>.ToString()</c> on a <see cref="Vouchfx.Mcp.Run.RunVerdict"/> enum —
    /// the taxonomy token, a closed set of four words — and on a <c>StringBuilder</c>.</description></item>
    /// <item><description><c>ex.Message</c> on a <c>RunArtefactStorageException</c> in Program.cs's
    /// startup-failure record. This is NOT a BCL exception's text: that type is constructed by
    /// <c>FileRunRegistry</c>/<c>WorkspaceRunLock</c> from a <c>PathSafetyGuard</c> containment
    /// error — this server's own composed, catalogued message — and it goes through
    /// <c>TextSanitiser</c> on the way out. It is also the operator's only explanation of why the
    /// server refused to start.</description></item>
    /// <item><description><c>ex.Message</c> in the <b>validation worker</b> crash line. Worth naming
    /// rather than waving through, because it IS an arbitrary exception's message reaching a stderr
    /// stream: it belongs to the <c>--validate-worker</c> CHILD process, whose stderr the parent
    /// captures and relays as bounded data, and it is a plain <c>Console.Error.WriteLine</c> rather
    /// than a structured record. It is out of US-S6-05's scope for exactly that reason — but it is
    /// in a guarded FILE, so the guard sees it, and this entry is the record that it was looked at
    /// rather than missed.</description></item>
    /// </list>
    /// <para>
    /// Recorded as TRIMMED LINE TEXT rather than line numbers, so an unrelated edit above does not
    /// churn this list while a change to the matched code itself does — which is exactly when a
    /// re-review is wanted.
    /// </para>
    /// </remarks>
    private static readonly string[] KnownBenignMaterialMatches =
    [
        "Program.cs: $\"vouchfx-mcp could not configure its run-artefact storage: {TextSanitiser.SanitiseForDisplay(ex.Message)}\");",
        "Program.cs: Console.Error.WriteLine($\"vouchfx-mcp validation worker crashed: {ex.Message}\");",
        "Program.cs: return builder.ToString();",
        "Program.cs: return builder.ToString();",
        "Program.cs: args, Environment.GetEnvironmentVariable, out var transport, out var transportError))",
        "HttpTransportHost.cs: TextSanitiser.SanitiseForDisplay(ex.Message));",
        "RunSuiteOrchestrator.cs: CliPinResult.NotFound notFound => notFound.Message,",
        "RunSuiteOrchestrator.cs: CliPinResult.Unparseable unparseable => unparseable.Message,",
        "RunSuiteOrchestrator.cs: CliPinResult.VersionMismatch mismatch => mismatch.Message,",
        "RunSuiteOrchestrator.cs: Verdict: (aggregate ?? RunVerdict.Inconclusive).ToString(),",
        "RunSuiteOrchestrator.cs: Verdict: RunVerdict.Inconclusive.ToString(),",
        "RunSuiteOrchestrator.cs: Verdict: elevated.ToString(),",
        "RunSuiteOrchestrator.cs: return new RunSuiteOutcome.InvalidArgument(invalidPaths.Message);",
        "RunSuiteOrchestrator.cs: return new RunSuiteOutcome.NoSuitesMatched(noMatches.Message);",
        "RunSuiteOrchestrator.cs: specs.Add(new SpecRunOutcome(suitePath, suiteSummary.Verdict.ToString(), suiteSummary.Steps));",
        "RunSuiteOrchestrator.cs: specs.Add(new SpecRunOutcome(suitePaths[abortedIndex], RunVerdict.Inconclusive.ToString(), []));",
    ];

    [Fact]
    public void StructuredLogEmissionInSrc_HappensOnlyInTheAllowlistedFiles()
    {
        var actualSites = SourceGuardScan.SourceFilesInSrc()
            .Where(path => StructuredLogEmission.IsMatch(SourceGuardScan.ExecutableSourceOf(path)))
            .Select(SourceGuardScan.ToRepoRelativeForwardSlashPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            StructuredLogCallSiteRelativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            actualSites);
    }

    /// <summary>
    /// The content guard: every match of a forbidden material shape, in every file permitted to emit
    /// a record, must be one of the reviewed-benign lines.
    /// </summary>
    /// <remarks>
    /// <b>Scanned with string literals INTACT</b> (<see cref="SourceGuardScan.SourceWithCommentsStrippedOnly"/>),
    /// which is the whole point and was a security review's MAJOR finding. The ordinary scan blanks a
    /// literal's entire body, and these files' house style is interpolated messages — so
    /// <c>$"…{ex.Message}"</c>, the exact shape this guard exists to forbid, had its
    /// <c>ex.Message</c> blanked along with the prose and passed silently. Comments are still
    /// stripped, so the rule can be documented beside the code without tripping itself.
    /// </remarks>
    [Fact]
    public void NoGuardedFile_ReachesForSecretBearingMaterial_BeyondTheReviewedBenignLines()
    {
        var actualMatches = new List<string>();

        foreach (var relativePath in StructuredLogCallSiteRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(fullPath), $"Expected a tracked file at '{fullPath}'.");

            // Interpolation holes are visible here; see this method's remarks.
            var source = SourceGuardScan.SourceWithCommentsStrippedOnly(fullPath);

            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();

                if (trimmed.Length > 0 && ForbiddenMaterialShapes.Any(shape => shape.Pattern.IsMatch(trimmed)))
                {
                    actualMatches.Add($"{Path.GetFileName(relativePath)}: {trimmed}");
                }
            }
        }

        // EXACT equality, fail-closed in both directions: a new match fails naming the source line,
        // and a stale entry fails once the line it excused is gone — so the allow-list cannot rot
        // into a licence for code that no longer exists.
        Assert.Equal(
            KnownBenignMaterialMatches.OrderBy(line => line, StringComparer.Ordinal).ToArray(),
            actualMatches.OrderBy(line => line, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Anti-vacuity for the scan itself: the forbidden shapes must be visible INSIDE an interpolation
    /// hole, which is the defect that made the previous revision of this guard useless.
    /// </summary>
    [Theory]
    [InlineData("var message = $\"run failed: {ex.Message}\";")]
    [InlineData("runLog.Write(LogLevel.Warning, $\"run '{runId}' failed ({failure.Message})\");")]
    [InlineData("Console.Error.WriteLine($\"{cause.StackTrace}\");")]
    [InlineData("var text = $\"{problem.InnerException}\";")]
    [InlineData("var text = $\"{exception.ToString()}\";")]
    // Renamed variables must still be caught — the member is what is matched, not the identifier.
    [InlineData("var message = $\"run failed: {cause.Message}\";")]
    [InlineData("var message = $\"run failed: {whateverTheyCallItNext.Message}\";")]
    public void TheForbiddenShapes_AreVisibleInsideAnInterpolationHole(string line)
    {
        // The scan a content guard must use sees it...
        var kept = SourceGuardScan.StripCommentsOnly(line);
        Assert.Contains(
            ForbiddenMaterialShapes,
            shape => shape.Pattern.IsMatch(kept));

        // ...and the ordinary call-site scan does NOT, which is precisely why this guard needed its
        // own. Pinned so nobody "simplifies" the two scans back into one.
        var blanked = SourceGuardScan.StripCommentsAndStringLiterals(line);
        // If this ever fails, ExecutableSourceOf has started keeping interpolation holes — at which
        // point this guard could go back to using it, deliberately rather than by accident.
        Assert.DoesNotContain(
            ForbiddenMaterialShapes,
            shape => shape.Pattern.IsMatch(blanked));
    }

    [Fact]
    public void TheCommentsOnlyScan_StillStripsComments_SoTheRuleCanBeDocumentedBesideTheCode()
    {
        // Without this, the paragraph in StructuredLog.cs that says "never an exception's Message"
        // would itself trip the content guard, making the rule undocumentable where it matters.
        var documented = SourceGuardScan.StripCommentsOnly("// never log ex.Message here\nvar x = 1;");

        Assert.DoesNotContain(ForbiddenMaterialShapes, shape => shape.Pattern.IsMatch(documented));
        Assert.Contains("var x = 1;", documented, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriter_NeverReadsTheProcessEnvironment()
    {
        var writer = Path.Combine(
            SourceGuardScan.RepoRoot.FullName,
            Path.Combine("src", "Vouchfx.Mcp", "Observability", "StructuredLog.cs"));

        Assert.True(File.Exists(writer), $"Expected the writer at '{writer}'.");

        var source = SourceGuardScan.ExecutableSourceOf(writer);

        Assert.DoesNotMatch(new Regex(@"Environment\s*\.\s*GetEnvironmentVariable", RegexOptions.Compiled), source);

        // And it writes to stderr, never stdout — the invariant that makes this whole surface safe.
        Assert.DoesNotMatch(new Regex(@"Console\s*\.\s*Out\b|Console\s*\.\s*Write", RegexOptions.Compiled), source);
        Assert.Matches(new Regex(@"Console\s*\.\s*Error", RegexOptions.Compiled), source);
    }

    /// <summary>
    /// Anti-vacuity for the call-site pin: every spelling of an emission must be seen, including the
    /// receiver-less <c>using static</c> form that a fourth file could otherwise use to emit records
    /// without the string "StructuredLog" appearing in it at all.
    /// </summary>
    [Theory]
    // The type-qualified forms every current call site uses.
    [InlineData("StructuredLog.Write(LogLevel.Error, workspaceError!);")]
    [InlineData("var runLog = StructuredLog.ForRun(registryEntry.RunId);")]
    [InlineData("StructuredLog . Write ( LogLevel.Information, \"spaced out\");")]
    // Through a scope held in a local, where the type name is not on the line.
    [InlineData("runLog.Write(LogLevel.Warning, \"run threw\", failure.GetType().Name);")]
    // THE using-static evasion: no receiver at all.
    [InlineData("Write(LogLevel.Information, \"emitted via using static\");")]
    [InlineData("        Write( LogLevel.Warning , \"spaced, still an emission\");")]
    public void TheCallSitePattern_SeesEverySpellingOfAnEmission(string line)
    {
        Assert.Matches(StructuredLogEmission, line);
    }

    [Theory]
    // A method merely ENDING in Write is not this one — the lookbehind is what excludes it.
    [InlineData("Console.Error.WriteLine(LogLevel.Information.ToString());")]
    [InlineData("buffer.OverWrite(LogLevel.Information);")]
    // A declaration is not a call site.
    [InlineData("public static void Write(LogLevel level, string message, string? errorType = null) =>")]
    // And an unrelated Write with no LogLevel argument is somebody else's API.
    [InlineData("writer.Write(buffer.WrittenSpan);")]
    public void TheCallSitePattern_DoesNotFireOnNeighbouringShapes(string line)
    {
        Assert.DoesNotMatch(StructuredLogEmission, line);
    }

    [Fact]
    public void TheAllowlistedFiles_StillEmit()
    {
        // Anti-vacuity: a renamed file or a gutted logger would make the set check pass over nothing.
        foreach (var relativePath in StructuredLogCallSiteRelativePaths)
        {
            var fullPath = Path.Combine(
                SourceGuardScan.RepoRoot.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(fullPath), $"Expected a tracked file at '{fullPath}'.");
            Assert.Matches(StructuredLogEmission, SourceGuardScan.ExecutableSourceOf(fullPath));
        }
    }
}
