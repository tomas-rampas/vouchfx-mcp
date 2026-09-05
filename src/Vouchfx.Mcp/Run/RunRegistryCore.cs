namespace Vouchfx.Mcp.Run;

/// <summary>
/// The rules every <see cref="IRunRegistry"/> implementation must apply identically — id minting,
/// argument validation, and the entry-shape transitions — factored out so the in-memory and
/// file-backed registries share one definition rather than two that could drift.
/// </summary>
/// <remarks>
/// What is deliberately NOT here: anything about WHERE an entry is stored or how it is made
/// crash-safe. That is exactly what differs between the two implementations, and each documents its
/// own answer.
/// </remarks>
internal static class RunRegistryCore
{
    /// <summary>The prefix every minted run id carries — see <see cref="RunRegistryEntry.RunId"/>.</summary>
    public const string RunIdPrefix = "run-";

    /// <summary>
    /// The exact character length of a minted run id: <see cref="RunIdPrefix"/> plus a
    /// <see cref="Guid"/>'s 32-character <c>N</c> form.
    /// </summary>
    public const int RunIdLength = 4 + 32;

    /// <summary>
    /// Mints a fresh, server-side run id (<c>run-</c> + 32 lowercase hex). See
    /// <see cref="RunRegistryEntry.RunId"/> for why this is server-minted today and what changes
    /// when upstream work item U4 gives the engine a stable id of its own.
    /// </summary>
    public static string MintRunId() => RunIdPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether <paramref name="runId"/> has the exact shape <see cref="MintRunId"/> produces.
    /// </summary>
    /// <remarks>
    /// <b>A path-safety check, not a formatting nicety.</b> The file-backed registry names a
    /// DIRECTORY after a run id, so an id containing a separator, a <c>..</c> segment, or a drive
    /// qualifier would let a lookup escape the output directory entirely. Validating the shape at
    /// every entry point — rather than sanitising at the point of use — means there is exactly one
    /// rule and no call site can forget to apply it. Hex-only also makes an id case-insensitively
    /// unambiguous, which matters because Windows path comparison is case-insensitive while the
    /// registry's own dictionary lookups are ordinal.
    /// </remarks>
    public static bool IsWellFormedRunId(string? runId)
    {
        if (runId is null || runId.Length != RunIdLength || !runId.StartsWith(RunIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = RunIdPrefix.Length; i < runId.Length; i++)
        {
            var c = runId[i];
            if (c is (< '0' or > '9') and (< 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="outcome"/> is one of <see cref="RunVerdict"/>'s four PascalCase names,
    /// or <see langword="null"/> (a run still in flight) — the ONE definition of the outcome
    /// vocabulary, shared by the write side (<see cref="ApplyStatusTransition"/>) and the file-backed
    /// registry's read side, so a value rejected on the way in cannot be accepted on the way back out.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> is case-SENSITIVE by default, which is
    /// what rejects the engine's own <c>PASS</c> wire token; the explicit name check on top of it is
    /// what rejects a numeric string (<c>"0"</c>), which <c>TryParse</c> would otherwise happily
    /// accept as a valid enum value.
    /// </remarks>
    public static bool IsKnownOutcome(string? outcome) =>
        outcome is null
        || (Enum.TryParse<RunVerdict>(outcome, out _)
            && Enum.GetNames<RunVerdict>().Contains(outcome, StringComparer.Ordinal));

    /// <summary>Builds the <see cref="RunRegistryStatus.Running"/> entry <see cref="IRunRegistry.StartRun"/> records.</summary>
    /// <remarks>
    /// <para>
    /// <paramref name="specPaths"/> and <paramref name="labels"/> are COPIED, never aliased: the
    /// caller's collection is mutable and an entry that changed under a reader after the fact would
    /// defeat <see cref="RunRegistryEntry"/>'s whole immutability guarantee.
    /// </para>
    /// <para>
    /// <b><paramref name="labels"/> is VALIDATED here as well as at the tool boundary</b> (a security
    /// review's MINOR finding). <c>RunSuiteOrchestrator.ValidateLabels</c> already refuses a bad map
    /// before this is reached, so in production this check never fires — but "the caller checked"
    /// is an assumption <see cref="IRunRegistry"/> never states and nothing enforces for a second
    /// caller, and the same doctrine that gives <see cref="PathSafetyGuard.CheckLocalPath"/> no
    /// default workspace parameter applies here: the layer that persists is the layer that refuses.
    /// Both call the one definition in <see cref="RunLabelRules"/>, so the two enforcers cannot drift
    /// apart on what a valid label is.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="specPaths"/> is empty or holds a blank entry, or <paramref name="labels"/>
    /// violates <see cref="RunLabelRules"/>. An exception rather than a returned message because by
    /// this point the call has already been accepted: a map arriving here in violation is a bug in
    /// this server, not a bad request.
    /// </exception>
    public static RunRegistryEntry CreateStartedEntry(
        string runId,
        string eventsFilePath,
        DateTimeOffset startedAtUtc,
        IReadOnlyList<string> specPaths,
        IReadOnlyDictionary<string, string>? labels)
    {
        ArgumentNullException.ThrowIfNull(specPaths);

        if (specPaths.Count == 0)
        {
            throw new ArgumentException("A run must cover at least one suite path.", nameof(specPaths));
        }

        foreach (var specPath in specPaths)
        {
            if (string.IsNullOrWhiteSpace(specPath))
            {
                throw new ArgumentException("A suite path must not be null, empty, or whitespace-only.", nameof(specPaths));
            }
        }

        if (labels is not null && RunLabelRules.Validate(labels) is { } labelViolation)
        {
            throw new ArgumentException(labelViolation, nameof(labels));
        }

        return new RunRegistryEntry(
            RunId: runId,
            Status: RunRegistryStatus.Running,
            Outcome: null,
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: null,
            SpecPaths: [.. specPaths],
            EventsFilePath: eventsFilePath,
            Labels: labels is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(labels, StringComparer.Ordinal));
    }

    /// <summary>
    /// Validates a transition's arguments and returns the resulting entry — the single definition of
    /// what a status change does to an entry, including when
    /// <see cref="RunRegistryEntry.FinishedAtUtc"/> gets stamped.
    /// </summary>
    /// <exception cref="ArgumentException">See <see cref="IRunRegistry.RecordStatusTransition"/>.</exception>
    public static RunRegistryEntry ApplyStatusTransition(RunRegistryEntry entry, string status, string? outcome)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        if (!RunRegistryStatus.IsKnown(status))
        {
            throw new ArgumentException(
                $"'{status}' is not a known run status. Expected one of: {string.Join(", ", RunRegistryStatus.All)}.",
                nameof(status));
        }

        // A run that has already ended cannot un-end. Refused rather than applied, because every way
        // this could happen is a bug worth surfacing: an orchestrator recording a completion twice
        // with the second call passing the wrong status, or a future story reusing a finished run's
        // id. Silently allowing it would resurrect a finished run as the registry's in-flight one and
        // — since FinishedAtUtc is never re-stamped — leave an entry claiming to be running while
        // carrying a finish time. Terminal → terminal stays legal (a defensive double-complete, whose
        // FinishedAtUtc must not move; see below) — but only while it repeats the SAME outcome, which
        // the outcome-rewrite check further down enforces.
        if (RunRegistryStatus.IsTerminal(entry.Status) && !RunRegistryStatus.IsTerminal(status))
        {
            throw new ArgumentException(
                $"Run '{entry.RunId}' already reached the terminal status '{entry.Status}' and cannot "
                + $"transition back to '{status}'. A finished run stays finished.",
                nameof(status));
        }

        // The MCP response vocabulary is enforced HERE, at the boundary, rather than trusted from
        // the caller: the registry is what a future get_run_status/list_runs response is projected
        // from, so an engine wire token ("PASS") stored here would become this server's contract.
        // See IsKnownOutcome for how the two halves of that check earn their keep.
        if (!IsKnownOutcome(outcome))
        {
            throw new ArgumentException(
                $"'{outcome}' is not a run outcome. Expected one of: {string.Join(", ", Enum.GetNames<RunVerdict>())}, "
                + "or null while the run is still in flight.",
                nameof(outcome));
        }

        // Null MEANS "keep what is recorded" (that is what the `?? entry.Outcome` below does), so the
        // two rules that follow are both stated against the EFFECTIVE outcome rather than against the
        // argument — otherwise a legitimate defensive double-complete passing null would trip them.
        var effectiveOutcome = outcome ?? entry.Outcome;

        // A terminal status MUST carry an outcome (a peer review's finding). "This run finished" and
        // "we never learned what it decided" is not a state the four-verdict taxonomy has a name for:
        // a run that reached no verdict is Inconclusive, and saying so is the caller's job — which is
        // exactly what RunSuiteOrchestrator's catch arm does. Left permitted, such an entry would be
        // the newest FINISHED run explain_run defaults to, and the run a future list_runs projects as
        // having ended saying nothing. Refused on the way in; FileRunRegistry.ReadEntry refuses the
        // same shape on the way back out, so a hand-written run.json cannot introduce one either.
        if (RunRegistryStatus.IsTerminal(status) && effectiveOutcome is null)
        {
            throw new ArgumentException(
                $"Run '{entry.RunId}' cannot transition to the terminal status '{status}' without an "
                + $"outcome. Expected one of: {string.Join(", ", Enum.GetNames<RunVerdict>())}.",
                nameof(outcome));
        }

        // Terminal → terminal stays legal (the defensive double-complete above), but only while it
        // records the SAME outcome — or null, meaning keep. Rewriting a recorded verdict is refused
        // HERE, at the storage layer, rather than trusted from a caller: `completed/Pass` followed by
        // `cancelled/Inconclusive` would silently overwrite a verdict the engine genuinely produced
        // with one derived from bookkeeping, and the entry is the record a later explain_run and a
        // future list_runs answer from. A caller that believes the verdict changed has a bug, and this
        // is where it surfaces instead of being written down as fact.
        if (RunRegistryStatus.IsTerminal(entry.Status)
            && !string.Equals(effectiveOutcome, entry.Outcome, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Run '{entry.RunId}' already finished with the outcome '{entry.Outcome}' and cannot be "
                + $"re-recorded as '{effectiveOutcome}'. A recorded verdict is not rewritten.",
                nameof(outcome));
        }

        return entry with
        {
            Status = status,
            Outcome = effectiveOutcome,

            // Stamped only on the transition that ENDS the run, and never re-stamped: a terminal
            // status recorded twice (a defensive double-complete) must not move the finish time.
            FinishedAtUtc = RunRegistryStatus.IsTerminal(status)
                ? entry.FinishedAtUtc ?? DateTimeOffset.UtcNow
                : entry.FinishedAtUtc,
        };
    }

    /// <summary>
    /// Orders entries most-recent-first, per <see cref="IRunRegistry.ListRuns"/>'s documented
    /// contract. One comparator, used by both implementations.
    /// </summary>
    public static IReadOnlyList<RunRegistryEntry> OrderMostRecentFirst(IEnumerable<RunRegistryEntry> entries) =>
        [.. entries
            .OrderByDescending(entry => entry.StartedAtUtc)
            .ThenByDescending(entry => entry.RunId, StringComparer.Ordinal)];
}
