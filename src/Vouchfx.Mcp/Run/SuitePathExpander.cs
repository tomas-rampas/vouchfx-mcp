using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// Turns <c>run_suite</c>'s caller-supplied <c>paths</c> list (US-S3-02) into the ordered, bounded,
/// de-duplicated list of suite files a run actually covers — expanding any entry that carries glob
/// syntax against the workspace root, and leaving every other entry exactly as
/// <see cref="PathSafetyGuard.ResolveCallerPath"/> resolves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type decides WHICH files a run covers; it decides nothing about whether they are safe or
/// valid.</b> Every path it returns still goes through the identical per-path chain a single
/// <c>path</c> has always gone through in <see cref="RunSuiteOrchestrator"/> — leading-dash refusal
/// (applied here, on the RAW token, before any rebase can bury a dash mid-path), workspace rebase,
/// UNC/containment refusal and existence/schema validation in the EDGE-003 pre-flight. Expansion is
/// deliberately not the security boundary: the guarantee that an escaping PATH is refused comes from
/// the guard chain, which cannot be bypassed by arriving through a glob. What expansion DOES owe is
/// not to invent an escape of its own, which is why a pattern containing a <c>..</c> segment is
/// refused outright (measured behaviour — see <see cref="Expand"/>) rather than trusted to match
/// nothing.
/// </para>
/// <para>
/// <b>Globs are expanded for <c>paths</c> only — never for the legacy scalar <c>path</c></b>, and
/// that asymmetry is a compatibility requirement rather than an oversight. Today a
/// <c>path</c> containing <c>*</c> is a file name that does not exist, and the call fails with
/// <c>VFX-E-1002</c>; glob-expanding it would silently turn one of those failing calls into a run of
/// several suites. The v2 input (<c>paths</c>) is where glob syntax is part of the contract, so the
/// old input's meaning never changes under a caller who did not ask for the new one.
/// </para>
/// <para>
/// <b>A glob's matches are filtered to <c>*.e2e.yaml</c>; an explicit literal path is not.</b> The
/// story's own example is <c>e2e/checkout/**</c>, which matches every file under that directory —
/// READMEs and fixtures included — and handing those to the pre-flight would refuse the whole run
/// over files the caller plainly did not mean. The filter is the engine's own discovery rule
/// (<c>vouchfx run &lt;directory&gt;</c> discovers <c>*.e2e.yaml</c> recursively), so a glob here
/// selects what the CLI would have selected. A literal path is left alone for the opposite reason:
/// naming one file is an unambiguous instruction, and refusing it for its extension would be a new
/// rejection today's callers have never seen.
/// </para>
/// <para>
/// <b>Ordering is deterministic and states the caller's intent where there is one.</b> Entries are
/// processed in the order the caller wrote them; each GLOB entry's own matches are sorted ordinally
/// by full path (the filesystem's enumeration order is not stable across platforms or even across
/// two calls on one platform, and a run whose spec order changes between calls is not reproducible);
/// duplicates — including one file reached through two different entries — are dropped, keeping the
/// first occurrence. The comparison is <see cref="PathSafetyGuard.PathComparison"/>, so
/// <c>E2E\A.e2e.yaml</c> and <c>e2e\a.e2e.yaml</c> are one file on Windows and two on Linux, exactly
/// as the platform itself treats them.
/// </para>
/// <para>
/// <b>Three caps, on three different axes</b> (the shape <see cref="FileRunRegistry"/> already uses
/// for its own directory walk). <see cref="MaxRequestedPaths"/> bounds how many ENTRIES a call may
/// carry — a cheap, caller-visible limit checked before any filesystem work;
/// <see cref="MaxExpandedPaths"/> bounds the total number of SUITES one run may cover, which is what
/// a single <c>**</c> over a large tree would otherwise blow past; and
/// <see cref="MaxExpandedPathCharacters"/> bounds their total LENGTH, which is what keeps the run's
/// registry entry inside the size its reader will still accept. Exceeding any is refused
/// outright rather than silently truncated: a run that quietly covered the first hundred matches of
/// a caller's pattern would report a verdict about a set the caller never chose.
/// </para>
/// <para>
/// <b>The cost of a leading <c>**</c> is the caller's to bear, and is bounded only BETWEEN
/// patterns.</b> <see cref="Matcher"/> prunes its walk by the pattern's literal segments, so
/// <c>e2e/checkout/**</c> touches only that subtree — but <c>**/*.e2e.yaml</c> genuinely enumerates
/// the whole workspace, synchronously, before <see cref="MatchGlob"/> returns.
/// <see cref="Matcher.Execute"/> exposes no cancellation, so the <c>run_suite</c> call's own
/// <c>timeoutSeconds</c> budget (threaded in as <c>cancellationToken</c> since the Sprint-3 review)
/// can be observed BETWEEN patterns and between the suites one pattern produced, but not DURING a
/// single walk. That is the honest bound: one pathological pattern can still overrun the budget by
/// however long its own walk takes, and a caller with a very large workspace should anchor the
/// pattern with a literal prefix.
/// </para>
/// </remarks>
public static class SuitePathExpander
{
    /// <summary>The largest number of entries a single <c>paths</c> argument may carry.</summary>
    public const int MaxRequestedPaths = 50;

    /// <summary>The largest number of suite files one run may cover, after expansion.</summary>
    public const int MaxExpandedPaths = 100;

    /// <summary>
    /// The largest TOTAL number of characters the expanded paths may come to — a second axis on the
    /// same set, bounding length where <see cref="MaxExpandedPaths"/> bounds count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists to keep a run's registry entry readable.</b> Every spec path is persisted into
    /// that entry, which <see cref="FileRunRegistry"/> SKIPS on read once it exceeds
    /// <see cref="FileRunRegistry.MaxEntryFileBytes"/> (64 KB) — so without a length bound here, a
    /// hundred deeply-nested paths could produce a run whose record is written and then permanently
    /// invisible, losing the run from <c>explain_run</c>'s default and from any later listing while
    /// the run itself proceeded normally.
    /// </para>
    /// <para>
    /// <b>This is a FIRST-LINE bound, not the enforcement — and the earlier arithmetic here was
    /// wrong</b> (a gatekeeper review's finding). It claimed the stored form is "up to twice the raw
    /// length" because a Windows separator doubles. That is the ASCII case only:
    /// <c>JavaScriptEncoder.Default</c>, which <see cref="FileRunRegistry"/>'s
    /// <c>JsonSerializerDefaults.Web</c> options use, escapes every non-ASCII character to a
    /// <c>\uXXXX</c> sequence — SIX bytes per UTF-16 unit. 24,000 characters of, say, Cyrillic or CJK
    /// path text therefore serialises to ~144 KB, comfortably past the 64 KB cap, so no character
    /// count chosen here can imply the byte bound without being punitively small for ordinary ASCII
    /// paths (where 24,000 characters is ~24 KB and leaves ample room for bounded labels in the same
    /// document). The bound is therefore enforced where the bytes actually exist:
    /// <c>FileRunRegistry.Persist</c> measures the serialised document and refuses to write one over
    /// the cap, so an oversized entry fails the call with a catalogued <c>VFX-E-1502</c> instead of
    /// producing an invisible run. What THIS cap buys is that the overwhelmingly common case is
    /// refused early, cheaply, and with a message about the caller's own argument.
    /// </para>
    /// </remarks>
    public const int MaxExpandedPathCharacters = 24_000;

    /// <summary>The file-name suffix a glob's matches are filtered to — the engine's own discovery rule.</summary>
    public const string SuiteFileSuffix = ".e2e.yaml";

    /// <summary>
    /// The characters whose presence makes an entry a PATTERN rather than a literal path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately just <c>*</c> and <c>?</c>: those are the two wildcards <see cref="Matcher"/>
    /// honours. Character classes (<c>[a-z]</c>) are NOT glob syntax here — <c>Matcher</c> does not
    /// implement them, and <c>[</c> is a perfectly ordinary filename character.
    /// </para>
    /// <para>
    /// <b>There is no escaping mechanism, and that asymmetry is platform-dependent.</b> Both
    /// characters are ILLEGAL in a path component on Windows, so there a file whose name contains one
    /// cannot exist and treating them as syntax costs nothing. On Linux they are legal, merely rare —
    /// so a suite genuinely named <c>what?.e2e.yaml</c> is unreachable through <c>paths</c>, which
    /// will read it as a pattern and (almost certainly) match nothing, answering <c>VFX-E-1002</c>.
    /// The remedy is the scalar <c>path</c> input, which is never glob-expanded and therefore names
    /// such a file exactly; <c>run_suite</c>'s tool description says so. Inventing an escape syntax
    /// (<c>\?</c>) was rejected because <c>Matcher</c> does not implement one either, so this type
    /// would have to unescape before handing the pattern over and the two would disagree about what
    /// a backslash means on the platform where it is also a separator.
    /// </para>
    /// </remarks>
    private static readonly char[] GlobCharacters = ['*', '?'];

    /// <summary>
    /// The set/ordering comparer form of <see cref="PathSafetyGuard.PathComparison"/> — derived from
    /// it rather than chosen again here, so de-duplication cannot disagree with containment about
    /// whether two spellings are the same file.
    /// </summary>
    private static readonly StringComparer PathComparer =
        StringComparer.FromComparison(PathSafetyGuard.PathComparison);

    /// <summary>
    /// Expands and orders <paramref name="requestedPaths"/>.
    /// </summary>
    /// <param name="requestedPaths">The caller's entries, raw and unvalidated.</param>
    /// <param name="workspace">
    /// The workspace resolved at server start, or <see langword="null"/>. Decides both what a
    /// relative path rebases onto and what a glob is rooted at — with no workspace, both are this
    /// process's current directory, which is exactly what a relative path has always meant here.
    /// </param>
    /// <param name="allowGlobs">
    /// <see langword="false"/> for the legacy scalar <c>path</c> input, whose meaning must not change
    /// (see this type's remarks). An entry carrying glob syntax is then simply a literal path that
    /// will fail its own existence check.
    /// </param>
    /// <param name="cancellationToken">
    /// The <c>run_suite</c> call's whole budget (<c>RunSuiteOrchestrator.RunAsync</c> links the
    /// caller's own token with <c>timeoutSeconds</c> and threads the result through here) — observed
    /// between patterns and between the suites one pattern produced, never during a single
    /// <see cref="Matcher.Execute"/> walk, which exposes no cancellation. Throws
    /// <see cref="OperationCanceledException"/>, which the orchestrator normalises into its
    /// cancelled/timed-out answer rather than letting escape.
    /// </param>
    public static SuitePathExpansion Expand(
        IReadOnlyList<string> requestedPaths,
        Workspace? workspace,
        bool allowGlobs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedPaths);

        if (requestedPaths.Count == 0)
        {
            return new SuitePathExpansion.Invalid(
                "No suite paths were supplied. Give at least one path (or, in 'paths', a glob that "
                + "matches at least one suite).");
        }

        if (requestedPaths.Count > MaxRequestedPaths)
        {
            return new SuitePathExpansion.Invalid(
                $"Too many paths: {requestedPaths.Count} supplied, at most {MaxRequestedPaths} are accepted.");
        }

        var expanded = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var requested in requestedPaths)
        {
            // The call's whole budget, observed at the one granularity available here: BETWEEN
            // patterns. Matcher.Execute below is a synchronous walk with no cancellation of its own,
            // so a single leading-`**` pattern over a huge tree can still overrun — see this type's
            // remarks, which state that bound rather than implying one this cannot deliver.
            cancellationToken.ThrowIfCancellationRequested();

            // A malformed MCP payload can legally put a JSON null inside a string array regardless of
            // this server's own nullable-reference-type annotations — the same reasoning
            // RunSuiteOrchestrator.ValidateTags records for `tags`.
            if (string.IsNullOrWhiteSpace(requested))
            {
                return new SuitePathExpansion.Invalid(
                    "Suite paths must not be null, empty, or whitespace-only.");
            }

            // Checked on the RAW token, before any rebase: rebasing first would bury a leading '-'
            // mid-path where it no longer looks like one, and this is the guard that keeps a path
            // from being read as an option by the engine CLI's own argument parser (and by the
            // validation worker's in-band `--yaml-stdin` discriminator — see
            // RunSuiteOrchestrator.RunAsync's own note on the two command lines this covers).
            if (requested.StartsWith('-'))
            {
                return new SuitePathExpansion.Invalid(
                    $"Path must not begin with '-': '{PathSafetyGuard.CapAndSanitisePathForDisplay(requested)}'. A "
                    + "leading '-' would be interpreted as a command-line option, not a file path.");
            }

            var isGlob = allowGlobs && requested.IndexOfAny(GlobCharacters) >= 0;
            if (!isGlob)
            {
                if (!AddIfNew(PathSafetyGuard.ResolveCallerPath(requested, workspace)))
                {
                    break;
                }

                continue;
            }

            // MEASURED (2026-09-05, Microsoft.Extensions.FileSystemGlobbing 10.0.10), and the reason
            // this check exists at all rather than being left to containment: a LEADING `..` is not
            // a literal segment to Matcher — it walks up out of the base directory and happily
            // returns `../outside.e2e.yaml`. A `..` anywhere else throws ArgumentException (".." can
            // be only added at the beginning of the pattern"). Both are refused here, uniformly, for
            // two reasons: with no workspace configured there is NO containment behind this to catch
            // the first case, and a pattern whose meaning depends on which of two undocumented
            // matcher behaviours it happens to hit is not something to hand a caller. A literal path
            // containing `..` is untouched — that is an ordinary relative path, rebased and then
            // containment-checked exactly as it always has been.
            //
            // WHAT THIS DOES NOT CLOSE, stated plainly (a security review's MINOR finding). Refusing
            // `..` does not make expansion escape-proof, because the matcher FOLLOWS reparse points:
            // measured on the same library, a junction or directory symlink inside the base directory
            // is traversed and its targets come back as paths that are LEXICALLY inside the workspace
            // (`<root>/link/suite.e2e.yaml`), so nothing in this method can see that they are not.
            // With a workspace configured that is closed by the per-path guard chain every expanded
            // path still goes through — PathSafetyGuard resolves links segment by segment to a fixed
            // point and refuses the escape (asserted by
            // RunSuiteOrchestratorTests.RunAsync_GlobMatchReachedThroughALinkOutsideTheWorkspace_IsRefusedByTheGuardChain).
            // With NO workspace there is no containment at all, so a linked-to file is reachable —
            // which is pre-existing-equivalent to passing that file's absolute path, something the
            // no-workspace mode has always allowed. What IS new there is DISCOVERY: a pattern can
            // enumerate files outside the current directory that the caller did not name. That is
            // accepted for the no-workspace compatibility mode and is one more reason a host that
            // cares about this boundary should launch with `--workspace`.
            if (HasParentSegment(requested))
            {
                return new SuitePathExpansion.Invalid(
                    $"A glob must not contain a '..' segment: "
                    + $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(requested)}'. Patterns are matched "
                    + "relative to the workspace root and may not walk out of it. Name the directory "
                    + "directly, or pass the file's own path.");
            }

            if (Path.IsPathFullyQualified(requested))
            {
                // Spec §5.7 defines the glob form as workspace-relative, and rooting an absolute
                // pattern would mean inventing a base directory for it (its own drive? its literal
                // prefix?) — a decision with no stated answer, made silently, on a security-relevant
                // path. Refused instead, with the remedy named.
                return new SuitePathExpansion.Invalid(
                    $"A glob must be workspace-relative: "
                    + $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(requested)}' is an absolute path "
                    + "containing '*' or '?'. Supply the pattern relative to the workspace root, or name "
                    + "the file exactly.");
            }

            var matches = MatchGlob(requested, workspace);
            if (matches.Count == 0)
            {
                return new SuitePathExpansion.NoMatches(
                    $"The pattern '{PathSafetyGuard.CapAndSanitisePathForDisplay(requested)}' matched no "
                    + $"'*{SuiteFileSuffix}' file under "
                    + $"'{PathSafetyGuard.CapAndSanitisePathForDisplay(BaseDirectoryFor(workspace))}'. Nothing was run.");
            }

            var capReached = false;
            foreach (var match in matches)
            {
                // Between SUITES, as well as between patterns: one pattern can contribute up to
                // MaxExpandedPaths + 1 entries, and a caller's budget must be able to end the walk
                // inside that.
                cancellationToken.ThrowIfCancellationRequested();

                if (!AddIfNew(match))
                {
                    capReached = true;
                    break;
                }
            }

            if (capReached)
            {
                break;
            }
        }

        if (expanded.Count > MaxExpandedPaths)
        {
            // "More than", not an exact count: accumulation stops at MaxExpandedPaths + 1 (see
            // AddIfNew), so the exact total is deliberately not computed — counting it would mean
            // materialising the very set this cap exists to refuse. The semantics are unchanged:
            // refused outright, never silently truncated to the first hundred.
            return new SuitePathExpansion.Invalid(
                $"Too many suites: the supplied paths expand to more than {MaxExpandedPaths} suite files, "
                + $"and at most {MaxExpandedPaths} may run in one call. Narrow the pattern, or split the call.");
        }

        var totalCharacters = expanded.Sum(path => path.Length);
        if (totalCharacters > MaxExpandedPathCharacters)
        {
            return new SuitePathExpansion.Invalid(
                $"The supplied paths are too long in total ({totalCharacters:N0} characters; at most "
                + $"{MaxExpandedPathCharacters:N0} are accepted). Narrow the pattern, or split the call.");
        }

        return new SuitePathExpansion.Expanded(expanded);

        // Returns false the moment the accumulated set has passed MaxExpandedPaths, which is the
        // signal for both loops above to stop walking (a gatekeeper review's finding: the count cap
        // used to be checked only AFTER every entry had been expanded, so fifty patterns each
        // matching a large tree were all materialised before the refusal). One entry past the cap is
        // kept rather than none, because the check after the loop is stated as a strict `>` and the
        // refusal must not depend on where the walk happened to stop.
        bool AddIfNew(string path)
        {
            if (seen.Add(path))
            {
                expanded.Add(path);
            }

            return expanded.Count <= MaxExpandedPaths;
        }
    }

    /// <summary>
    /// Whether any <c>/</c>- or <c>\</c>-delimited segment of <paramref name="pattern"/> is exactly
    /// <c>..</c> — see the call site for what the matcher does with one, measured rather than assumed.
    /// </summary>
    private static bool HasParentSegment(string pattern)
    {
        foreach (var segment in pattern.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The absolute, ordinally-sorted <c>*.e2e.yaml</c> files <paramref name="pattern"/> selects
    /// under the workspace root (or, with no workspace, this process's current directory).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every failure is answered as "no matches" rather than thrown: a base directory that does not
    /// exist, is unreadable, or disappears mid-walk means the pattern selected nothing, and the
    /// caller's remedy ("that pattern matches no suite") is identical either way. A pattern
    /// containing <c>..</c> never reaches here — it is refused by the caller, for the measured
    /// reason recorded there.
    /// </para>
    /// <para>
    /// <b>Materialisation is capped at <see cref="MaxExpandedPaths"/> + 1</b> (a gatekeeper review's
    /// finding), applied AFTER de-duplication and BEFORE the sort so a pattern matching a hundred
    /// thousand files does not build a hundred-thousand-element list and sort it purely to be
    /// refused. Taking an arbitrary subset cannot change the ANSWER, and the argument is worth
    /// writing down: the caller's set is refused whenever the accumulated count exceeds
    /// <see cref="MaxExpandedPaths"/>. If this pattern yields <c>MaxExpandedPaths + 1</c> distinct
    /// files, the entries already accumulated number at most <see cref="MaxExpandedPaths"/> (the walk
    /// stops the moment it passes that), so at most that many of the sampled files can be duplicates
    /// and the total after adding them is at least <c>MaxExpandedPaths + 1</c> — a refusal, exactly
    /// as the untruncated set would have produced. If the pattern yields fewer, nothing is truncated
    /// at all and the sort is over the complete set.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> MatchGlob(string pattern, Workspace? workspace)
    {
        var baseDirectory = BaseDirectoryFor(workspace);

        try
        {
            var matcher = new Matcher(PathSafetyGuard.PathComparison);
            matcher.AddInclude(pattern);

            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(baseDirectory)));
            if (!result.HasMatches)
            {
                return [];
            }

            return
            [
                .. result.Files
                    .Select(file => Path.GetFullPath(file.Path, baseDirectory))
                    .Where(path => path.EndsWith(SuiteFileSuffix, PathSafetyGuard.PathComparison))
                    .Distinct(PathComparer)
                    .Take(MaxExpandedPaths + 1)
                    .OrderBy(path => path, StringComparer.Ordinal)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or ArgumentException
                                   or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// What a glob is rooted at: the workspace root when one is configured, and otherwise this
    /// process's current directory — the same base a relative path has always resolved against
    /// (see <see cref="PathSafetyGuard.ResolveCallerPath"/>), so the two inputs cannot disagree
    /// about what "relative" means.
    /// </summary>
    private static string BaseDirectoryFor(Workspace? workspace) =>
        workspace?.Root ?? Directory.GetCurrentDirectory();
}

/// <summary>
/// What <see cref="SuitePathExpander.Expand"/> produced — a closed union (a private constructor
/// confines derivation to the cases nested here), mirroring <see cref="RunSuiteOutcome"/>'s shape so
/// each case maps onto exactly one <c>VFX-E-####</c> at the tool boundary.
/// </summary>
public abstract record SuitePathExpansion
{
    private SuitePathExpansion()
    {
    }

    /// <summary>The suites this run covers, in run order, de-duplicated and bounded.</summary>
    public sealed record Expanded(IReadOnlyList<string> Paths) : SuitePathExpansion;

    /// <summary>
    /// The <c>paths</c> argument itself is unusable — an injection-shaped entry, a blank one, an
    /// absolute glob, or a list that breaches one of the two caps.
    /// </summary>
    public sealed record Invalid(string Message) : SuitePathExpansion;

    /// <summary>
    /// A well-formed pattern that selected no suite at all. Distinct from
    /// <see cref="Invalid"/> because the remedy differs (fix the pattern or create the suite, rather
    /// than fix the argument's shape) and because it maps to a different code — the same
    /// <c>VFX-E-1002</c> a single missing <c>path</c> already returns, so one file and one pattern
    /// that name nothing are answered alike.
    /// </summary>
    public sealed record NoMatches(string Message) : SuitePathExpansion;
}
