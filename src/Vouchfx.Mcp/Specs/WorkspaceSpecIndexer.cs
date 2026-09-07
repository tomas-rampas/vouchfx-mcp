using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Specs;

/// <summary>
/// Builds the <c>vouchfx://workspace/specs</c> index (Sprint 5 / US-S5-01 AC-005): a read-only
/// enumeration of the <c>.e2e.yaml</c> files under the configured workspace's <c>specsDir</c>, plus
/// a light parse of each — <b>performed in a child process</b> — for its name, tags and step types.
/// </summary>
/// <remarks>
/// <para>
/// <b>The parse is NOT done here, and that is the most important fact about this type.</b> Handing a
/// workspace suite's bytes to YamlDotNet on this server's own request thread is forbidden: the
/// Scanner can be driven into an uninterruptible ~100%-CPU spin by a twelve-byte well-formed input,
/// and <see cref="YamlSafetyGuard"/> is not a defence because its nesting check IS the Scanner. One
/// mis-indented suite in a developer's own <c>e2e/</c> directory would wedge this resource for the
/// life of the server. <see cref="SpecIndexWorkerClient"/> is the boundary that fixes it; see
/// <see cref="SpecIndexWorkerProtocol"/>'s header for the full reasoning and the streaming design
/// that lets one hostile file cost one degraded entry rather than the whole index.
/// </para>
/// <para>
/// <b>What stays here, and why:</b> enumeration and containment. Neither
/// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> nor
/// <see cref="PathSafetyGuard"/> hands a byte to YamlDotNet, so neither can spin; and both depend on
/// the SERVER's workspace configuration, which the disposable child is deliberately never told. The
/// child receives absolute paths this process has already decided are legitimate, and answers with
/// no path of its own.
/// </para>
/// <para>
/// <b>The <c>specsDir</c> is CONSUMED, never re-derived.</b> It comes from Sprint 3's
/// <see cref="Workspace"/> — resolved once at startup from <c>--workspace</c> — and this type does no
/// path arithmetic of its own beyond joining a discovered file onto it. That is the dependency
/// US-S5-01 names explicitly, and the same rule <c>FileRunRegistry</c> and <c>WorkspaceRunLock</c>
/// follow for <c>outputDir</c>.
/// </para>
/// <para>
/// <b>Two independent mechanisms keep the index inside <c>specsDir</c>, and each covers something the
/// other does not.</b>
/// <list type="number">
/// <item><description>
/// The enumeration skips reparse points (<see cref="EnumerationOptions.AttributesToSkip"/>), so a
/// symlink or NTFS junction planted inside <c>e2e/</c> and pointing at <c>../secrets/</c> is never
/// descended into or returned in the first place. Structural, and free.
/// </description></item>
/// <item><description>
/// Every returned path is then put through <see cref="PathSafetyGuard.IsInsideResolvedDirectory"/> —
/// the SAME fixed-point symlink walk the workspace containment check uses — and dropped if it does
/// not resolve under <c>specsDir</c>. This is what catches a link the attribute filter did not model,
/// and it is the mechanism that would still hold if that enumeration flag were ever changed.
/// </description></item>
/// </list>
/// <b>What NEITHER covers, stated plainly rather than implied away: a HARD LINK.</b> A hard link is a
/// second directory entry for the same inode, not a reference to another path — it carries no reparse
/// point for the filter to skip and no target for the link walk to resolve, so a hard link inside
/// <c>e2e/</c> to a file outside it is, at the path level, genuinely inside and is indexed. That is
/// the identical residual <see cref="PathSafetyGuard"/>'s own remarks record for workspace
/// containment, accepted there for the same reason: this is a local, single-user developer tool whose
/// caller could read that file directly anyway. (An earlier version of this comment called the
/// containment check a "hard-link backstop", which was simply false — a security review's finding.)
/// </para>
/// <para>
/// <b>Read-only, and structurally so:</b> the only filesystem APIs here are
/// <see cref="Directory.Exists(string)"/> and
/// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> — both reads. The file
/// READ itself happens in the child. <c>ReadOnlySourceGuardTests</c> holds this: neither this file nor
/// <see cref="SpecIndexParser"/> is on its permitted-mutation list, and neither may be added to it.
/// </para>
/// </remarks>
public static class WorkspaceSpecIndexer
{
    /// <summary>The file suffix that makes a file a vouchfx suite, matched case-insensitively by the OS.</summary>
    public const string SpecFileSuffix = ".e2e.yaml";

    /// <summary>The enumeration glob. Matched by the filesystem, so its case sensitivity is the platform's.</summary>
    private const string SpecFileGlob = "*" + SpecFileSuffix;

    /// <summary>
    /// The most suites one index will carry. The enumeration STOPS at one past this — see
    /// <see cref="EnumerateSpecFilesAsync"/> — so the cap bounds the WORK, not merely the output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized for the question this resource answers rather than for a byte budget: a repository with
    /// more than five hundred suites is one where "read the whole index and scan it" has already
    /// stopped being how a host avoids duplication, and the honest answer is a flag saying the list is
    /// partial.
    /// </para>
    /// <para>
    /// <b>The resulting body size, measured against the per-field caps rather than guessed</b> (a
    /// security review's finding — an earlier version of this comment said "~100 KB", which was about
    /// 36x optimistic). Worst case per entry: a 1,000-character path
    /// (<see cref="PathSafetyGuard.MaxDisplayedPathChars"/>), a 200-character name, 20 tags at 100
    /// characters, 40 step types at 64, plus JSON overhead — about 7.3 KB, so 500 entries is roughly
    /// <b>3.6 MB</b>. That is a legitimate size for a RESOURCE (it is precisely the case plan §2.4
    /// hands off from a tool's 32 KB effective budget for) and it is bounded rather than open-ended,
    /// which is the property that actually matters. A typical real workspace is a few tens of KB.
    /// </para>
    /// </remarks>
    public const int MaxSpecsIndexed = 500;

    /// <summary>
    /// How deep below <c>specsDir</c> the walk descends. Suites live in a shallow tree
    /// (<c>e2e/checkout/place-order.e2e.yaml</c>); a bound stops a pathological one costing an
    /// unbounded walk on the server's request thread.
    /// </summary>
    private const int MaxRecursionDepth = 16;

    /// <summary>Builds the index for <paramref name="workspace"/>, or the "no workspace" answer when it is null.</summary>
    /// <param name="workspace">The startup workspace, or <see langword="null"/> when none was configured.</param>
    /// <param name="cancellationToken">
    /// Observed between enumeration entries and across the worker boundary, so a host that abandons a
    /// <c>resources/read</c> stops this build rather than leaving it to finish into nothing. Threaded
    /// end to end (a security review's finding: the first version of this resource was synchronous and
    /// took no token at all, so the bounded work was only bounded by its own caps).
    /// </param>
    /// <param name="budget">
    /// Overrides <see cref="SpecIndexWorkerClient.DefaultBudget"/>. <b>Exists so tests can be
    /// deterministic</b>: a review found the equivalent seam on the worker client UNREACHABLE, because
    /// this method never forwarded one — which left every healthy-path test racing the production
    /// clock against whatever else the machine was running, and produced exactly the intermittent
    /// failures that finding came from. Production passes <see langword="null"/>.
    /// </param>
    public static async Task<WorkspaceSpecIndex> BuildAsync(
        Workspace? workspace,
        SpecIndexWorkerBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        if (workspace is null)
        {
            return new WorkspaceSpecIndex(
                WorkspaceConfigured: false,
                SpecsDir: null,
                Specs: [],
                Truncated: false,
                Reason: WorkspaceSpecIndexReasons.NoWorkspaceConfigured,
                Detail:
                    "This server was launched without --workspace, so it has no specs directory to "
                    + "index. Relaunch it as 'vouchfx-mcp --workspace <path>' to enable this resource "
                    + "(and workspace path containment with it).");
        }

        var specsDir = workspace.SpecsDir;
        var displayDir = PathSafetyGuard.CapAndSanitisePathForDisplay(specsDir);

        if (!Directory.Exists(specsDir))
        {
            return new WorkspaceSpecIndex(
                WorkspaceConfigured: true,
                SpecsDir: displayDir,
                Specs: [],
                Truncated: false,
                Reason: WorkspaceSpecIndexReasons.SpecsDirMissing,
                Detail:
                    $"The workspace's specs directory ('{displayDir}') does not exist. vouchfx suites "
                    + $"live under <workspace root>/{Workspace.SpecsDirectoryName}; create it and put "
                    + $"your {SpecFileSuffix} files there.");
        }

        List<string> files;
        bool enumerationCapped;
        try
        {
            (files, enumerationCapped) = EnumerateSpecFiles(specsDir, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return new WorkspaceSpecIndex(
                WorkspaceConfigured: true,
                SpecsDir: displayDir,
                Specs: [],
                Truncated: false,
                Reason: WorkspaceSpecIndexReasons.SpecsDirUnreadable,
                Detail:
                    $"The workspace's specs directory ('{displayDir}') could not be enumerated "
                    + $"({ex.GetType().Name}). Check its permissions.");
        }

        // Sorted on the WIRE key — the relative, forward-slashed path each entry will carry — not on
        // the absolute filesystem path (a gatekeeper review's finding). Sorting the absolute form and
        // publishing the relative one made the published order not the sorted order on Windows, where
        // '\' (0x5C) and '/' (0x2F) sit on opposite sides of every path character in an ordinal
        // comparison: "a/b.e2e.yaml" and "ab.e2e.yaml" order differently before and after the
        // conversion. Deriving the key first makes the published order match the documented one on
        // every platform, which is what makes this resource's body byte-stable across reads.
        var ordered = files
            .Select(path => (Wire: ToRelativeWirePath(path, specsDir), Absolute: path))
            .OrderBy(candidate => candidate.Wire, StringComparer.Ordinal)
            .ToArray();

        // The parse crosses the process boundary HERE, in one batch — see this type's remarks.
        var parsed = await SpecIndexWorkerClient
            .ParseAsync([.. ordered.Select(candidate => candidate.Absolute)], budget, cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<WorkspaceSpecEntry>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            // The worker client returns exactly one entry per input path, in order, with no gaps —
            // its own contract, and one it upholds even when the worker itself produced nothing.
            // Indexed rather than zipped so a contract break is an immediate, loud
            // IndexOutOfRangeException in a test rather than a silently short index in production.
            var entry = parsed.Entries[i];
            entries.Add(new WorkspaceSpecEntry(
                Path: ordered[i].Wire,
                Name: entry.Name,
                Tags: entry.Tags ?? [],
                StepTypes: entry.StepTypes ?? [],
                Steps: entry.Steps,
                Readable: entry.Readable,
                ParseError: entry.ParseError));
        }

        // REASON PRECEDENCE, and it is a judgement rather than an ordering accident: an unavailable
        // worker outranks a hit spec limit. Both can be true at once (a 600-suite directory whose
        // worker will not start), and of the two the operator can only act on the first — "your
        // machine could not run the parser" is the fact that explains why every entry is thin, whereas
        // "there are more than 500 suites" would leave them wondering why the 500 they got are empty.
        // `truncated` still reports the cap independently, so nothing is lost by not naming it here.
        var (reason, detail) = parsed.WorkerUnavailable
            ? (WorkspaceSpecIndexReasons.SpecWorkerUnavailable,
                $"{parsed.UnavailableDetail} The suites below were found on disk, but none could be "
                + "examined — their empty fields say nothing about the files themselves.")
            : enumerationCapped
                ? (WorkspaceSpecIndexReasons.SpecLimitReached,
                    $"This workspace holds more than {MaxSpecsIndexed} suites; the index stops there. "
                    + "Narrow your search with the validate_suite or plan_coverage tools rather than "
                    + "treating this list as complete.")
                : ((string?)null, (string?)null);

        return new WorkspaceSpecIndex(
            WorkspaceConfigured: true,
            SpecsDir: displayDir,
            Specs: entries,
            Truncated: enumerationCapped,
            Reason: reason,
            Detail: detail);
    }

    /// <summary>
    /// Every <c>.e2e.yaml</c> under <paramref name="specsDir"/> that survives BOTH containment
    /// mechanisms, stopping as soon as <see cref="MaxSpecsIndexed"/> have been kept.
    /// </summary>
    /// <returns>The kept paths, and whether the walk stopped at the cap rather than at the end.</returns>
    /// <remarks>
    /// <b>The cap bounds the WORK, not just the output</b> (a security review's MAJOR finding). The
    /// first version collected every match into an unbounded list and ran a containment probe — which
    /// makes up to two filesystem calls per path segment — on every one of them, THEN took the first
    /// five hundred. A directory holding a million suite files therefore cost a million probes to
    /// produce five hundred rows. The enumeration is lazy, so breaking out of the loop is all that is
    /// needed; what makes the break correct rather than arbitrary is that the ORDER is imposed
    /// afterwards, so "the first 500 the filesystem offered" is not a claim about which 500 those
    /// are — which is exactly what <see cref="WorkspaceSpecIndex.Truncated"/> exists to say.
    /// </remarks>
    private static (List<string> Files, bool Capped) EnumerateSpecFiles(
        string specsDir, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxRecursionDepth,

            // A file this process cannot stat is skipped rather than aborting the whole walk: one
            // unreadable subdirectory must not make the entire index unavailable.
            IgnoreInaccessible = true,

            // The structural half of the containment rule. A reparse point is a symlink, a junction
            // or a mount point; none of them is a suite, and descending one is how an index escapes
            // the directory it was asked about. Hidden/system files are skipped for a different and
            // duller reason: an editor's swap file is not a suite either.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,

            // The OS decides case sensitivity, so 'X.E2E.YAML' matches on Windows and does not on
            // Linux — which is exactly how the engine's own file resolution behaves, and therefore
            // what a host should be shown.
            MatchCasing = MatchCasing.PlatformDefault,
        };

        // Resolved ONCE for the whole walk, not per file: the link walk costs up to two filesystem
        // probes per path segment, and re-resolving the same directory for every candidate would
        // multiply this enumeration's I/O by the directory's depth for an answer that cannot change
        // within one call. A null here means the specs directory itself could not be resolved, in
        // which case NOTHING is contained by it — fail closed, and the caller reports an empty index.
        var resolvedSpecsDir = PathSafetyGuard.TryResolveContainmentRoot(specsDir);
        if (resolvedSpecsDir is null)
        {
            return ([], false);
        }

        var kept = new List<string>();
        var capped = false;

        // ONE walk, continued — never a second one. The first version re-enumerated the whole
        // directory from scratch to answer "is there a 501st?", which re-probed containment on up to
        // 500 already-accepted files, walked an unbounded number of non-contained entries a second
        // time, and — because the two walks happen at different instants — could return a `truncated`
        // that disagreed with the `kept` list it accompanied (a review's TOCTOU finding). Continuing
        // the SAME lazy enumerator answers the same question from the state that produced the list.
        var lookaheadBudget = MaxLookaheadCandidates;

        foreach (var file in Directory.EnumerateFiles(specsDir, SpecFileGlob, options))
        {
            // Checked per ENTRY rather than only per batch: enumeration itself is the unbounded part
            // of this method (a directory tree is as large as the disk allows), so an abandoned
            // request must be able to stop it mid-walk.
            cancellationToken.ThrowIfCancellationRequested();

            // The definitive half of containment. Cheap in the ordinary case (no links on the path ⇒
            // one pass that substitutes nothing) and the only thing that would catch an escape the
            // attribute filter did not model.
            if (!PathSafetyGuard.IsInsideResolvedDirectory(file, resolvedSpecsDir))
            {
                // Past the cap this is pure lookahead, and the budget is what stops a directory of a
                // million non-contained entries turning "is there one more?" into a full walk.
                if (kept.Count >= MaxSpecsIndexed && --lookaheadBudget <= 0)
                {
                    break;
                }

                continue;
            }

            if (kept.Count >= MaxSpecsIndexed)
            {
                // The 501st contained match. It is never added — the cap bounds the published list —
                // and finding it is the whole difference between "exactly 500 suites exist" and "the
                // walk stopped at 500", which is what Truncated exists to say.
                capped = true;
                break;
            }

            kept.Add(file);
        }

        return (kept, capped);
    }

    /// <summary>
    /// How many NON-CONTAINED entries the walk will step past, after the cap is reached, while looking
    /// for one more contained match.
    /// </summary>
    /// <remarks>
    /// Only reached in a directory that both exceeds <see cref="MaxSpecsIndexed"/> and contains
    /// entries the containment check rejects — a workspace with hundreds of suites and a wall of
    /// symlinks. Exhausting it reports <c>truncated: false</c> for a directory that might hold more,
    /// which is the right way to be wrong here: the alternative is an unbounded walk on a request
    /// thread to refine a flag that already says "this list may be incomplete".
    /// </remarks>
    private const int MaxLookaheadCandidates = 64;

    /// <summary>
    /// <paramref name="fullPath"/> expressed relative to <paramref name="specsDir"/> with forward
    /// slashes — see <see cref="WorkspaceSpecEntry.Path"/> for why relative and why <c>/</c>.
    /// </summary>
    private static string ToRelativeWirePath(string fullPath, string specsDir)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(specsDir, fullPath);
        }
        catch (ArgumentException)
        {
            // Cannot be expressed relative to the directory it was enumerated from — which should be
            // unreachable, since the containment check already resolved it under specsDir. Falling
            // back to the file NAME rather than the full path keeps the "no absolute paths in the
            // list" property true even on a branch that should not exist.
            relative = Path.GetFileName(fullPath);
        }

        return SpecIndexParser.CapAndSanitise(
            relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'),
            PathSafetyGuard.MaxDisplayedPathChars);
    }
}
