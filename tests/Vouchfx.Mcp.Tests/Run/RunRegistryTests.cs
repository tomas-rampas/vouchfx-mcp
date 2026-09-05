using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// US-S3-01's run registry: the contract both implementations must satisfy identically, plus the
/// storage-specific behaviour each one owns alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shared contract is asserted against BOTH implementations, from one body.</b> Every
/// <c>[Theory]</c> below takes a <see cref="RegistryKind"/> and runs against an
/// <see cref="InMemoryRunRegistry"/> and a <see cref="FileRunRegistry"/> in turn. That is the point:
/// <c>VouchfxMcpServerRegistration</c> picks between them by workspace-configured-ness, so a caller
/// (<c>RunSuiteOrchestrator</c>, <c>ExplainRunOrchestrator</c>) must be unable to tell which it
/// got. A behaviour proven against only one of them would be a behaviour that silently differs
/// between a workspace-configured server and a legacy one.
/// </para>
/// <para>
/// The implementation-specific facts — atomic publish, per-entry fault isolation, "the in-memory one
/// writes nothing anywhere" — are asserted in the two <c>[Fact]</c> sections at the end, because
/// they are precisely where the two are ALLOWED to differ.
/// </para>
/// </remarks>
public class RunRegistryTests : IDisposable
{
    /// <summary>Which implementation a shared-contract theory case runs against.</summary>
    public enum RegistryKind
    {
        /// <summary>No workspace configured — <see cref="InMemoryRunRegistry"/>.</summary>
        InMemory,

        /// <summary>Workspace configured — <see cref="FileRunRegistry"/> under a temp output directory.</summary>
        FileBacked,
    }

    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "vouchfx-mcp-registry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_outputDirectory))
            {
                Directory.Delete(_outputDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    // ── The contract both implementations must satisfy identically ─────────────────────────────

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void StartRun_RecordsARunningRunWithAMintedIdAndEventsPath(RegistryKind kind)
    {
        var registry = Create(kind);

        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);

        // The documented id shape: `run-` plus 32 lowercase hex characters. Asserted structurally
        // (not merely "not empty") because FileRunRegistry names a DIRECTORY after it — see
        // RunRegistryCore.IsWellFormedRunId's remarks on why that makes the shape a safety property.
        Assert.StartsWith("run-", entry.RunId, StringComparison.Ordinal);
        Assert.Equal(36, entry.RunId.Length);
        Assert.All(entry.RunId[4..], c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));

        Assert.Equal(RunRegistryStatus.Running, entry.Status);
        Assert.Null(entry.Outcome);
        Assert.Null(entry.FinishedAtUtc);
        Assert.Equal(["/suites/orders.e2e.yaml"], entry.SpecPaths);
        Assert.Empty(entry.Labels);
        Assert.False(string.IsNullOrWhiteSpace(entry.EventsFilePath));

        // The entry is readable back immediately — the write at run START is a real write, not a
        // deferred one flushed at completion.
        AssertSameEntry(entry, registry.TryGetRun(entry.RunId));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void StartRun_CopiesItsInputs_SoALaterMutationCannotChangeARecordedEntry(RegistryKind kind)
    {
        var registry = Create(kind);
        var specPaths = new List<string> { "/suites/a.e2e.yaml" };
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["trigger"] = "agent" };

        var entry = registry.StartRun(specPaths, labels);

        specPaths.Add("/suites/injected.e2e.yaml");
        labels["trigger"] = "tampered";

        Assert.Equal(["/suites/a.e2e.yaml"], entry.SpecPaths);
        Assert.Equal("agent", entry.Labels["trigger"]);
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void StartRun_RejectsAnEmptySpecPathList(RegistryKind kind)
    {
        var registry = Create(kind);

        Assert.Throws<ArgumentException>(() => registry.StartRun([]));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ToCompleted_StampsTheOutcomeAndFinishTime(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var completed = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Fail));

        Assert.NotNull(completed);
        Assert.Equal(RunRegistryStatus.Completed, completed.Status);
        Assert.Equal("Fail", completed.Outcome);
        Assert.NotNull(completed.FinishedAtUtc);
        Assert.True(completed.FinishedAtUtc >= completed.StartedAtUtc);

        // Written through, not just returned: the persisted record is what a later reader (or a
        // later PROCESS, for the file-backed registry) sees.
        AssertSameEntry(completed, registry.TryGetRun(started.RunId));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_RejectsAnEngineWireTokenAsAnOutcome(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        // "PASS" is the ENGINE's own JSON Lines token. The registry is what a future get_run_status
        // response is projected from, so letting one in here would leak the engine's serialisation
        // into this server's contract — see RunRegistryEntry.Outcome.
        Assert.Throws<ArgumentException>(
            () => registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, "PASS"));

        // A numeric string is the other way Enum.TryParse would have said yes.
        Assert.Throws<ArgumentException>(
            () => registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, "0"));

        Assert.Null(registry.TryGetRun(started.RunId)?.Outcome);
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_RejectsAStatusOutsideTheSpecVocabulary(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        Assert.Throws<ArgumentException>(() => registry.RecordStatusTransition(started.RunId, "finished"));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ToANonTerminalStatus_LeavesFinishedAtNullAndPersists(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var still = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Running);

        // FinishedAtUtc is stamped by the transition that ENDS the run and by no other, so a
        // non-terminal transition must leave it null — and must still be written through, since a
        // later reader (or a later PROCESS) sees only what was persisted.
        Assert.NotNull(still);
        Assert.Equal(RunRegistryStatus.Running, still.Status);
        Assert.Null(still.Outcome);
        Assert.Null(still.FinishedAtUtc);
        AssertSameEntry(still, registry.TryGetRun(started.RunId));

        // And such a run is still not eligible to be explain_run's default.
        Assert.Null(registry.MostRecentFinishedRun());
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_RepeatedTerminalTransition_DoesNotMoveTheFinishTime(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var first = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        Assert.NotNull(first);
        Assert.NotNull(first.FinishedAtUtc);

        var second = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Fail));

        // A defensive double-complete must not re-stamp the finish time: the run ended when it ended,
        // and moving it would make a duration computed from the entry lie.
        Assert.NotNull(second);
        Assert.Equal(first.FinishedAtUtc, second.FinishedAtUtc);
        Assert.Equal(nameof(RunVerdict.Fail), second.Outcome);
        AssertSameEntry(second, registry.TryGetRun(started.RunId));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_FromATerminalStatusBackToRunning_IsRefused(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        // A finished run stays finished. Allowing this would resurrect a completed run as the
        // registry's in-flight one — and, since FinishedAtUtc is never re-stamped, leave an entry
        // claiming to be `running` while carrying a finish time.
        Assert.Throws<ArgumentException>(
            () => registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Running));

        var unchanged = registry.TryGetRun(started.RunId);
        Assert.NotNull(unchanged);
        Assert.Equal(RunRegistryStatus.Completed, unchanged.Status);
        Assert.Equal(nameof(RunVerdict.Pass), unchanged.Outcome);
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ForAnUnknownRun_ReturnsNullRatherThanThrowing(RegistryKind kind)
    {
        var registry = Create(kind);

        // Deliberately not an exception: a host that deleted the run's record between the start and
        // the completion must lose the record, not the whole run_suite call.
        Assert.Null(registry.RecordStatusTransition("run-" + new string('a', 32), RunRegistryStatus.Completed, "Pass"));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void ListRuns_OrdersMostRecentFirst_EvenWhenStartedInsideOneTickOfTheSystemClock(RegistryKind kind)
    {
        var registry = Create(kind);

        // Started back to back, with no delay at all: on Windows the system timer's ~15 ms
        // resolution means both of these routinely read the same UtcNow. Without the monotonic floor
        // in RunRegistryTimestamps, "which is the most recent run" would fall to a random-GUID
        // tie-break — and explain_run's whole default rests on that ordering.
        var first = registry.StartRun(["/suites/first.e2e.yaml"]);
        var second = registry.StartRun(["/suites/second.e2e.yaml"]);

        Assert.True(second.StartedAtUtc > first.StartedAtUtc);
        Assert.Equal([second.RunId, first.RunId], registry.ListRuns().Select(entry => entry.RunId));
        Assert.Equal(second.RunId, registry.MostRecentRun()?.RunId);
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void MostRecentFinishedRun_SkipsARunStillInFlight(RegistryKind kind)
    {
        var registry = Create(kind);
        var finished = registry.StartRun(["/suites/finished.e2e.yaml"]);
        registry.RecordStatusTransition(finished.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        var inFlight = registry.StartRun(["/suites/in-flight.e2e.yaml"]);

        // The compatibility rule US-S3-01 must not break: the retired ILastRunTracker recorded a run
        // only at COMPLETION, so explain_run called during a run defaulted to the previous FINISHED
        // one. The registry records at run start too, so without this filter the default would
        // silently become "diagnose the run happening right now" — against a file the engine has not
        // finished writing.
        Assert.Equal(inFlight.RunId, registry.MostRecentRun()?.RunId);
        Assert.Equal(finished.RunId, registry.MostRecentFinishedRun()?.RunId);
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void IsRecordedEventsFilePath_MatchesAMintedPathAndNothingElse(RegistryKind kind)
    {
        var registry = Create(kind);
        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);

        Assert.True(registry.IsRecordedEventsFilePath(entry.EventsFilePath));
        Assert.False(registry.IsRecordedEventsFilePath(Path.Combine(Path.GetTempPath(), "someone-elses.jsonl")));

        // Whole-string equality, never a prefix or directory-containment test: the exemption it
        // backs in ExplainRunOrchestrator must widen the exempt set by exactly the server-minted
        // paths and by nothing else.
        Assert.False(registry.IsRecordedEventsFilePath(Path.GetDirectoryName(entry.EventsFilePath)!));
    }

    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public async Task ConcurrentStartRunCalls_EachProduceADistinctRecordedRun(RegistryKind kind)
    {
        const int RunCount = 32;
        var registry = Create(kind);

        // run_suite writes while explain_run/diagnose_run read, potentially on different threads of
        // the same session, so thread safety is part of IRunRegistry's contract rather than an
        // implementation detail. A lost update or a torn dictionary would show up here as a missing
        // or duplicated entry.
        var entries = await Task.WhenAll(Enumerable.Range(0, RunCount)
            .Select(i => Task.Run(() => registry.StartRun([$"/suites/{i}.e2e.yaml"]))));

        Assert.Equal(RunCount, entries.Select(entry => entry.RunId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(RunCount, registry.ListRuns().Count);
        Assert.Equal(RunCount, registry.ListRuns().Select(entry => entry.StartedAtUtc).Distinct().Count());
    }

    // ── InMemoryRunRegistry only: it must be incapable of leaving anything behind ───────────────

    [Fact]
    public void InMemoryRegistry_WritesNothingToDisk()
    {
        var registry = new InMemoryRunRegistry();

        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        // US-S3-08's compatibility rule, extended to persistence by US-S3-01: a host that never
        // passed --workspace must see no new files anywhere. The events file itself is the SUITE
        // RUNNER's to create, not the registry's — the registry only mints the path.
        Assert.False(File.Exists(entry.EventsFilePath));
        Assert.False(Directory.Exists(_outputDirectory));
    }

    [Fact]
    public void InMemoryRegistry_MintsTheEventsPathShapeTheStaleTempSweepStillMatches()
    {
        var entry = new InMemoryRunRegistry().StartRun(["/suites/orders.e2e.yaml"]);

        // RunSuiteOrchestrator's retention sweep globs `vouchfx-mcp-events-*.jsonl` in the OS temp
        // directory. If this name shape drifted, no-workspace runs would accumulate there forever.
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(entry.EventsFilePath));
        Assert.StartsWith("vouchfx-mcp-events-", Path.GetFileName(entry.EventsFilePath), StringComparison.Ordinal);
        Assert.EndsWith(".jsonl", entry.EventsFilePath, StringComparison.Ordinal);
    }

    // ── FileRunRegistry only: layout, restart survival, and crash safety ───────────────────────

    [Fact]
    public void FileRegistry_PutsAMetadataDocumentAndTheEventsStreamInTheRunsOwnDirectory()
    {
        var registry = new FileRunRegistry(_outputDirectory);

        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var runDirectory = Path.Combine(_outputDirectory, entry.RunId);
        Assert.True(Directory.Exists(runDirectory));
        Assert.True(File.Exists(Path.Combine(runDirectory, FileRunRegistry.EntryFileName)));
        Assert.Equal(Path.Combine(runDirectory, FileRunRegistry.EventsFileName), entry.EventsFilePath);

        // The registry mints the path but never creates the file — the suite runner writes it.
        Assert.False(File.Exists(entry.EventsFilePath));
    }

    [Fact]
    public void FileRegistry_CreatesNothingUntilARunActuallyStarts()
    {
        var registry = new FileRunRegistry(_outputDirectory);

        // Construction plus reads against a workspace whose output directory does not exist yet is
        // an empty registry, not a directory-creating side effect. get_run_status/list_runs will be
        // read-only tools, and a read must not provision storage.
        Assert.Empty(registry.ListRuns());
        Assert.Null(registry.TryGetRun("run-" + new string('b', 32)));
        Assert.False(Directory.Exists(_outputDirectory));
    }

    [Fact]
    public void FileRegistry_SurvivesARestart_ASecondInstanceReadsTheFirstsRuns()
    {
        var first = new FileRunRegistry(_outputDirectory);
        var entry = first.StartRun(["/suites/orders.e2e.yaml"]);
        first.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.EnvironmentError));

        // A fresh instance stands in for a fresh SERVER PROCESS: it shares nothing with `first` but
        // the directory. (RealRunRegistryMcpTests proves the same thing one layer up, through two
        // independent MCP servers.)
        var second = new FileRunRegistry(_outputDirectory);

        var recovered = second.TryGetRun(entry.RunId);
        Assert.NotNull(recovered);
        Assert.Equal(RunRegistryStatus.Completed, recovered.Status);
        Assert.Equal("EnvironmentError", recovered.Outcome);
        Assert.Equal(entry.EventsFilePath, recovered.EventsFilePath);
        Assert.Equal(entry.StartedAtUtc, recovered.StartedAtUtc);
        Assert.Equal(entry.RunId, second.MostRecentFinishedRun()?.RunId);
    }

    [Fact]
    public void FileRegistry_KeepsStartTimesIncreasingAcrossARestart()
    {
        var first = new FileRunRegistry(_outputDirectory);
        var earlier = first.StartRun(["/suites/earlier.e2e.yaml"]);
        first.RecordStatusTransition(earlier.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        var second = new FileRunRegistry(_outputDirectory);
        var later = second.StartRun(["/suites/later.e2e.yaml"]);

        // Seeded from disk on the first write (FileRunRegistry.SeedStartedAtFloorFromDisk): without
        // it, a restart inside the system timer's resolution could stamp an EQUAL timestamp and
        // leave "the most recent run" decided by a random GUID — in the exact scenario restart
        // survival is asserted against.
        Assert.True(later.StartedAtUtc > earlier.StartedAtUtc);
        Assert.Equal(later.RunId, second.MostRecentRun()?.RunId);
    }

    [Fact]
    public void FileRegistry_ATornOrCorruptEntry_DoesNotPoisonTheOtherEntries()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        registry.RecordStatusTransition(good.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        var damaged = registry.StartRun(["/suites/damaged.e2e.yaml"]);

        // A write interrupted at the byte level is impossible through Persist (it publishes by
        // atomic rename), but a filesystem-level truncation, a hand edit, or a half-synced file on a
        // network share can still leave one of these on disk. Each must cost exactly its own run's
        // record and nothing more.
        File.WriteAllText(EntryPathOf(damaged.RunId), """{"version":1,"run":{"runId":"run-""");

        var runs = registry.ListRuns();
        Assert.Equal([good.RunId], runs.Select(entry => entry.RunId));
        Assert.Null(registry.TryGetRun(damaged.RunId));
        Assert.Equal(good.RunId, registry.MostRecentFinishedRun()?.RunId);
    }

    [Fact]
    public void FileRegistry_SkipsAnEntryWrittenInAFormatVersionItDoesNotKnow()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        var future = registry.StartRun(["/suites/future.e2e.yaml"]);

        var futureDocument = File.ReadAllText(EntryPathOf(future.RunId))
            .Replace($"\"version\": {FileRunRegistry.CurrentFormatVersion}", "\"version\": 999", StringComparison.Ordinal);
        Assert.Contains("999", futureDocument, StringComparison.Ordinal);
        File.WriteAllText(EntryPathOf(future.RunId), futureDocument);

        // Skipped, never best-effort-misread: a version 2 that re-means a field must not have its
        // status or outcome reported as fact by a server that only knows version 1.
        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    /// <summary>
    /// The FORGED-entry case: a perfectly well-formed <c>run.json</c> whose <c>eventsFilePath</c>
    /// names a file outside the registry's own layout. Distinct from — and not covered by — the
    /// corrupt/truncated/oversized/unknown-version cases above, every one of which fails to PARSE.
    /// This one parses fine and is rejected on its CONTENT.
    /// </summary>
    /// <remarks>
    /// Why it matters more than tidiness: a registry entry is <c>explain_run</c>'s containment
    /// EXEMPTION anchor. Anything able to drop a file into the output directory (a shared build agent,
    /// a hostile dependency's postinstall, a synced folder) could otherwise name any file on the host
    /// as a run's "events file" and have <c>explain_run</c> read it back with containment turned off —
    /// under a workspace whose whole purpose is to stop exactly that. The registry mints exactly one
    /// events path per run id, so any other value is forged or foreign by definition.
    /// </remarks>
    [Fact]
    public void FileRegistry_IgnoresAWellFormedEntryWhoseEventsPathPointsOutsideTheRunsOwnDirectory()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        registry.RecordStatusTransition(good.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        // Hand-written, valid JSON, current format version, known status, real outcome, a run id that
        // matches its own directory — everything the earlier checks look at is correct.
        var forgedRunId = "run-" + new string('c', 32);
        var forgedEventsPath = Path.Combine(Path.GetTempPath(), "attacker-chosen.jsonl");
        Directory.CreateDirectory(Path.Combine(_outputDirectory, forgedRunId));
        File.WriteAllText(EntryPathOf(forgedRunId), $$"""
            {
              "version": {{FileRunRegistry.CurrentFormatVersion}},
              "run": {
                "runId": "{{forgedRunId}}",
                "status": "completed",
                "outcome": "Pass",
                "startedAt": "2099-01-01T00:00:00+00:00",
                "finishedAt": "2099-01-01T00:00:01+00:00",
                "specPaths": [ "/suites/forged.e2e.yaml" ],
                "eventsFilePath": {{JsonSerializer.Serialize(forgedEventsPath)}},
                "labels": {}
              }
            }
            """);

        // Skipped exactly like a corrupt entry — layer-3 fault isolation, so the neighbour still reads
        // normally. Note the forged entry's startedAt is far in the future: without the check it would
        // sort FIRST and become explain_run's default.
        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
        Assert.Null(registry.TryGetRun(forgedRunId));
        Assert.Equal(good.RunId, registry.MostRecentFinishedRun()?.RunId);

        // And the forged path never reaches explain_run's containment exemption.
        Assert.False(registry.IsRecordedEventsFilePath(forgedEventsPath));
        Assert.True(registry.IsRecordedEventsFilePath(good.EventsFilePath));
    }

    [Fact]
    public void FileRegistry_SkipsAnOversizedEntryRatherThanReadingIt()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        var oversized = registry.StartRun(["/suites/oversized.e2e.yaml"]);

        File.WriteAllText(EntryPathOf(oversized.RunId), new string('x', FileRunRegistry.MaxEntryFileBytes + 1));

        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    [Fact]
    public void FileRegistry_IgnoresCrashResidueAndForeignDirectories()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);

        // What a process killed mid-Persist leaves behind, and what an unrelated tool might drop in
        // the same output directory. Neither is a run.
        File.WriteAllText(EntryPathOf(good.RunId) + ".tmp-" + Guid.NewGuid().ToString("N"), "{ half written");
        Directory.CreateDirectory(Path.Combine(_outputDirectory, "not-a-run"));
        File.WriteAllText(Path.Combine(_outputDirectory, "not-a-run", FileRunRegistry.EntryFileName), "{}");

        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    [Fact]
    public void FileRegistry_RefusesToResolveAPathTraversingRunId()
    {
        var registry = new FileRunRegistry(_outputDirectory);

        // A run id is spliced into a directory path, so its shape is a containment property. A
        // lookup for a malformed id is "no such run", never an escape.
        Assert.Null(registry.TryGetRun(Path.Combine("..", "..", "etc")));
        Assert.Null(registry.TryGetRun("run-" + new string('z', 32)));
        Assert.Null(registry.TryGetRun("run-short"));
    }

    [Fact]
    public void FileRegistry_PersistedDocumentCarriesTheSpecWireFieldNames()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        using var document = JsonDocument.Parse(File.ReadAllBytes(EntryPathOf(entry.RunId)));
        var run = document.RootElement.GetProperty("run");

        // Spec §5.8's RunSummary field names, so US-S3-03 can project a response from this document
        // without a translation layer that could drift from it.
        Assert.Equal(FileRunRegistry.CurrentFormatVersion, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(entry.RunId, run.GetProperty("runId").GetString());
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal("Pass", run.GetProperty("outcome").GetString());
        Assert.True(run.TryGetProperty("startedAt", out _));
        Assert.True(run.TryGetProperty("finishedAt", out _));
        Assert.Equal(["/suites/orders.e2e.yaml"], run.GetProperty("specPaths").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(entry.EventsFilePath, run.GetProperty("eventsFilePath").GetString());

        // Present and empty until US-S3-02 gives run_suite a `labels` input — the field exists now
        // so the on-disk format does not change when it does.
        Assert.Empty(run.GetProperty("labels").EnumerateObject());
    }

    [Fact]
    public void FileRegistry_StoresRunMetadataAndNothingElse()
    {
        var registry = new FileRunRegistry(_outputDirectory);
        var entry = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        using var document = JsonDocument.Parse(File.ReadAllBytes(EntryPathOf(entry.RunId)));

        // Plan §2.7 invariant 4, held STRUCTURALLY for a persistent surface: the set of properties an
        // entry may carry is closed. A future field that carried log content, a resolved
        // ${secret:...} value, or an environment snapshot would fail here before any behavioural
        // test had to notice it.
        Assert.Equal(
            ["run", "version"],
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["eventsFilePath", "finishedAt", "labels", "outcome", "runId", "specPaths", "startedAt", "status"],
            document.RootElement.GetProperty("run").EnumerateObject()
                .Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Field-by-field entry comparison, used instead of <c>Assert.Equal(expected, actual)</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="RunRegistryEntry"/> is a record, so its generated equality compares its
    /// <see cref="RunRegistryEntry.SpecPaths"/> and <see cref="RunRegistryEntry.Labels"/> members by
    /// REFERENCE — which makes an entry round-tripped through <see cref="FileRunRegistry"/>'s JSON
    /// (a fresh <c>List</c>/<c>Dictionary</c> every read) unequal to the identical entry held in
    /// memory, no matter how identical its contents are. Comparing the fields is therefore not
    /// pedantry: <c>Assert.Equal</c> on the whole record would pass for the in-memory registry and
    /// fail for the file-backed one while proving nothing about either.
    /// </remarks>
    private static void AssertSameEntry(RunRegistryEntry expected, RunRegistryEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.StartedAtUtc, actual.StartedAtUtc);
        Assert.Equal(expected.FinishedAtUtc, actual.FinishedAtUtc);
        Assert.Equal(expected.EventsFilePath, actual.EventsFilePath);
        Assert.Equal(expected.SpecPaths, actual.SpecPaths);
        Assert.Equal(expected.Labels, actual.Labels);
    }

    private string EntryPathOf(string runId) =>
        Path.Combine(_outputDirectory, runId, FileRunRegistry.EntryFileName);

    private IRunRegistry Create(RegistryKind kind) => kind switch
    {
        RegistryKind.InMemory => new InMemoryRunRegistry(),
        RegistryKind.FileBacked => new FileRunRegistry(_outputDirectory),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
