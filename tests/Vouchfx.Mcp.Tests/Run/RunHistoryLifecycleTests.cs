using System.Text.Json.Nodes;
using Vouchfx.Mcp.Run;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests.Run;

public sealed class RunHistoryLifecycleTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-history-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddMinutes(1);
    private readonly ITestOutputHelper _output;
    private string OutputDirectory => Path.Combine(_sandbox, "runs");
    private FileRunRegistry Registry => new(OutputDirectory, workspace: null);

    public RunHistoryLifecycleTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_sandbox))
        {
            Directory.Delete(_sandbox, recursive: true);
        }
    }

    [Fact]
    public void OrphanRecovery_IsBookkeepingOnly_Idempotent_AndPreservesTerminalEvidence()
    {
        var orphan = Registry.StartRun(["synthetic.e2e.yaml"]);
        File.WriteAllText(orphan.EventsFilePath, "uninterpreted engine evidence");
        var terminal = SeedTerminal(_now.AddDays(-1));
        var before = File.ReadAllBytes(MetadataPath(terminal));
        var result = Registry.MaintainHistory(new(), _now, CancellationToken.None);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(2, result.Mutations);
        Assert.False(result.Incomplete);
        var recovered = Assert.IsType<RunRegistryEntry>(Registry.TryGetRun(orphan.RunId));
        Assert.Equal(RunRegistryStatus.Completed, recovered.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), recovered.Outcome);
        Assert.Equal(_now, recovered.FinishedAtUtc);
        Assert.Equal(FileRunRegistry.InterruptedOwnershipHint, recovered.RemediationHint);
        Assert.Equal("uninterpreted engine evidence", File.ReadAllText(orphan.EventsFilePath));
        Assert.Equal(before, File.ReadAllBytes(MetadataPath(terminal)));
        var second = Registry.MaintainHistory(new(), _now.AddSeconds(1), CancellationToken.None);
        Assert.Equal(0, second.Reconciled);
        Assert.Equal(0, second.Mutations);
        Assert.Equal(_now, Registry.TryGetRun(orphan.RunId)!.FinishedAtUtc);
    }

    [Fact]
    public void AgeBoundary_IsInclusive_AndCountRetainsTheNewestEligibleTerminals()
    {
        var expired = SeedTerminal(_now.AddDays(-30).AddTicks(-1));
        var boundary = SeedTerminal(_now.AddDays(-30));
        var newest = SeedTerminal(_now.AddDays(-1));
        var byAge = Registry.MaintainHistory(new(3, 30), _now, CancellationToken.None);
        Assert.Equal(1, byAge.Pruned);
        Assert.Null(Registry.TryGetRun(expired.RunId));
        Assert.NotNull(Registry.TryGetRun(boundary.RunId));
        var byCount = Registry.MaintainHistory(new(1, 30), _now, CancellationToken.None);
        Assert.Equal(1, byCount.Pruned);
        Assert.Null(Registry.TryGetRun(boundary.RunId));
        Assert.NotNull(Registry.TryGetRun(newest.RunId));
    }

    [Fact]
    public void KeepHistory_DisablesPruningButRetainsReconciliation()
    {
        var old = SeedTerminal(_now.AddDays(-100));
        var orphan = Registry.StartRun(["synthetic.e2e.yaml"]);
        var result = Registry.MaintainHistory(new(1, 1, true), _now, CancellationToken.None);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(0, result.Pruned);
        Assert.NotNull(Registry.TryGetRun(old.RunId));
        Assert.Equal(RunRegistryStatus.Completed, Registry.TryGetRun(orphan.RunId)!.Status);
    }

    [Theory]
    [InlineData(nameof(RunVerdict.Pass))]
    [InlineData(nameof(RunVerdict.Fail))]
    [InlineData(nameof(RunVerdict.EnvironmentError))]
    [InlineData(nameof(RunVerdict.Inconclusive))]
    public void UnselectedTerminalReceipts_KeepTheirExactBytesAndVerdict(string outcome)
    {
        var entry = Registry.StartRun(["synthetic.e2e.yaml"]);
        Registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, outcome, "existing receipt");
        File.WriteAllText(entry.EventsFilePath, "existing terminal evidence");
        var bytes = File.ReadAllBytes(MetadataPath(entry));
        var result = Registry.MaintainHistory(new(), _now, CancellationToken.None);
        Assert.Equal(0, result.Mutations);
        Assert.Equal(bytes, File.ReadAllBytes(MetadataPath(entry)));
        Assert.Equal(outcome, Registry.TryGetRun(entry.RunId)!.Outcome);
        Assert.Equal("existing terminal evidence", File.ReadAllText(entry.EventsFilePath));
    }

    [Fact]
    public void VersionOneOrphan_IsReadableAndRecovered_WhileAnOverCapRecoveryIsPreserved()
    {
        var v1 = Registry.StartRun(["synthetic.e2e.yaml"]);
        var document = JsonNode.Parse(File.ReadAllText(MetadataPath(v1)))!;
        document["version"] = 1;
        File.WriteAllText(MetadataPath(v1), document.ToJsonString());
        Assert.Equal(1, Registry.MaintainHistory(new(), _now, CancellationToken.None).Reconciled);
        Assert.Equal(FileRunRegistry.InterruptedOwnershipHint, Registry.TryGetRun(v1.RunId)!.RemediationHint);

        var nearCap = Registry.StartRun(["synthetic.e2e.yaml"]);
        document = JsonNode.Parse(File.ReadAllText(MetadataPath(nearCap)))!;
        var remaining = FileRunRegistry.MaxEntryFileBytes - 40 - System.Text.Encoding.UTF8.GetByteCount(document.ToJsonString());
        document["run"]!["specPaths"]![0] = "synthetic.e2e.yaml" + new string('x', remaining);
        File.WriteAllText(MetadataPath(nearCap), document.ToJsonString());
        var original = File.ReadAllBytes(MetadataPath(nearCap));
        Assert.Equal(FileRunRegistry.MaxEntryFileBytes - 40, original.Length);
        Assert.NotNull(Registry.TryGetRun(nearCap.RunId));
        var result = Registry.MaintainHistory(new(), _now, CancellationToken.None);
        Assert.Equal(0, result.Reconciled);
        Assert.Equal(0, result.Mutations);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Incomplete);
        Assert.Equal(original, File.ReadAllBytes(MetadataPath(nearCap)));
    }

    [Fact]
    public void UnavailableStartupClaim_DoesNotMutateHistory()
    {
        var orphan = Registry.StartRun(["synthetic.e2e.yaml"]);
        var before = File.ReadAllBytes(MetadataPath(orphan));
        Assert.Null(RunHistoryMaintenance.TryAtStartup(Registry, new UnavailableLock(), new()));
        Assert.Equal(before, File.ReadAllBytes(MetadataPath(orphan)));
    }

    private sealed class UnavailableLock : IRunLock
    {
        public RunLockResult TryAcquire() => new RunLockResult.Unavailable(new UnauthorizedAccessException("synthetic-private-path"));
    }

    [Fact]
    public void MutationBudget_IsAtMost100_AndASecondPassConverges()
    {
        for (var i = 0; i < 51; i++)
        {
            SeedTerminal(_now.AddDays(-2).AddSeconds(i));
        }

        var current = SeedTerminal(_now);
        var first = Registry.MaintainHistory(new(1, 1), _now, CancellationToken.None);
        Assert.Equal(33, first.Pruned);
        Assert.Equal(99, first.Mutations);
        Assert.True(first.Incomplete);
        Assert.NotNull(Registry.TryGetRun(current.RunId));
        var second = Registry.MaintainHistory(new(1, 1), _now, CancellationToken.None);
        Assert.Equal(18, second.Pruned);
        Assert.Equal(54, second.Mutations);
        Assert.False(second.Incomplete);
        Assert.Single(Registry.ListRuns());
    }

    [Fact]
    public void PartialScan_OnlyPrunesWhereObservedNewerRecordsProveTheCountLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedTerminal(_now.AddMinutes(-i));
        }

        var limited = new FileRunRegistry(OutputDirectory, workspace: null, maxRunsScanned: 3, maxDirectoriesExamined: 10);
        var observed = limited.ListRuns().Select(entry => entry.RunId).ToHashSet(StringComparer.Ordinal);
        var newestObserved = limited.ListRuns().OrderByDescending(entry => entry.FinishedAtUtc).First().RunId;
        var all = Registry.ListRuns().Select(entry => entry.RunId).ToArray();
        var result = limited.MaintainHistory(new(1, 30), _now, CancellationToken.None);
        Assert.True(result.Incomplete);
        Assert.Equal(3, result.Examined);
        Assert.Equal(2, result.Pruned);
        Assert.NotNull(Registry.TryGetRun(newestObserved));
        foreach (var id in all.Where(id => !observed.Contains(id)))
        {
            Assert.NotNull(Registry.TryGetRun(id));
        }
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("corrupt")]
    [InlineData("unknown-property")]
    [InlineData("duplicate-property")]
    [InlineData("missing-finish")]
    [InlineData("future-finish")]
    [InlineData("reversed-finish")]
    [InlineData("non-utc-finish")]
    [InlineData("foreign-file")]
    [InlineData("foreign-directory")]
    [InlineData("stale-temp")]
    public void UntrustedOrForeignHistory_IsBytePreserved(string kind)
    {
        var entry = SeedTerminal(_now.AddDays(-100));
        var path = MetadataPath(entry);
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (kind)
        {
            case "future-version": document["version"] = 999; break;
            case "unknown-property": document["futureOwner"] = "foreign"; break;
            case "missing-finish": document["run"]!["finishedAt"] = null; break;
            case "future-finish": document["run"]!["finishedAt"] = _now.AddDays(1); break;
            case "reversed-finish": document["run"]!["finishedAt"] = _now.AddDays(-101); break;
            case "non-utc-finish": document["run"]!["finishedAt"] = _now.AddDays(-100).ToOffset(TimeSpan.FromHours(1)); break;
            case "foreign-file": File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "owned-by-user.txt"), "preserve"); break;
            case "foreign-directory": Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "foreign")); break;
            case "stale-temp": File.WriteAllText(path + ".tmp-" + new string('a', 32), "preserve"); break;
        }

        File.WriteAllText(path, kind switch
        {
            "corrupt" => "not JSON",
            "duplicate-property" => document.ToJsonString().Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal),
            _ => document.ToJsonString(),
        });
        var before = File.ReadAllBytes(path);
        var events = File.ReadAllBytes(entry.EventsFilePath);
        var result = Registry.MaintainHistory(new(1, 1), _now, CancellationToken.None);
        Assert.Equal(0, result.Mutations);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Incomplete);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(events, File.ReadAllBytes(entry.EventsFilePath));
    }

    [Theory]
    [InlineData(false, false, "{\"owner\":\"first\",\"owner\":\"second\"}")]
    [InlineData(false, false, "{\"owner\":\"first\",\"\\u006fwner\":\"second\"}")]
    [InlineData(true, false, "{\"owner\":\"first\",\"owner\":\"second\"}")]
    [InlineData(true, false, "{\"owner\":\"first\",\"\\u006fwner\":\"second\"}")]
    [InlineData(true, true, "{\"owner\":\"first\",\"owner\":\"second\"}")]
    [InlineData(true, true, "{\"owner\":\"first\",\"\\u006fwner\":\"second\"}")]
    public void DuplicateNestedPropertyNames_PreserveMetadataAndEventsBeforeAnyMaintenance(
        bool orphan, bool keepHistory, string labelsJson)
    {
        var entry = orphan ? Registry.StartRun(["synthetic.e2e.yaml"]) : SeedTerminal(_now.AddDays(-100));
        SetRawLabels(entry, labelsJson);
        File.WriteAllBytes(entry.EventsFilePath, [0, 255, 13, 10, 123, 125]);
        var metadata = File.ReadAllBytes(MetadataPath(entry));
        var events = File.ReadAllBytes(entry.EventsFilePath);

        var result = Registry.MaintainHistory(new(1, 1, keepHistory), _now, CancellationToken.None);

        Assert.Equal(0, result.Mutations);
        Assert.Equal(0, result.Reconciled);
        Assert.Equal(0, result.Pruned);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Incomplete);
        Assert.Equal(metadata, File.ReadAllBytes(MetadataPath(entry)));
        Assert.Equal(events, File.ReadAllBytes(entry.EventsFilePath));
    }

    [Theory]
    [InlineData(false, "{\"owner\":\"first\",\"component\":\"second\"}")]
    [InlineData(false, "{\"Owner\":\"first\",\"owner\":\"second\"}")]
    [InlineData(false, "{\"evidence\":\"{\\\"owner\\\":\\\"first\\\",\\\"owner\\\":\\\"second\\\"}\"}")]
    [InlineData(true, "{\"owner\":\"first\",\"component\":\"second\"}")]
    [InlineData(true, "{\"Owner\":\"first\",\"owner\":\"second\"}")]
    [InlineData(true, "{\"evidence\":\"{\\\"owner\\\":\\\"first\\\",\\\"owner\\\":\\\"second\\\"}\"}")]
    public void ValidLabels_CaseDistinctNamesAndJsonLookingStrings_DoNotPreventMaintenance(
        bool orphan, string labelsJson)
    {
        var entry = orphan ? Registry.StartRun(["synthetic.e2e.yaml"]) : SeedTerminal(_now.AddDays(-100));
        SetRawLabels(entry, labelsJson);
        var labels = Registry.TryGetRun(entry.RunId)!.Labels;
        File.WriteAllBytes(entry.EventsFilePath, [0, 255, 13, 10, 123, 125]);
        var events = File.ReadAllBytes(entry.EventsFilePath);

        var result = Registry.MaintainHistory(new(1, 1, orphan), _now, CancellationToken.None);

        Assert.False(result.Incomplete);
        Assert.Equal(0, result.Skipped);
        if (orphan)
        {
            Assert.Equal(1, result.Reconciled);
            Assert.Equal(2, result.Mutations);
            var recovered = Assert.IsType<RunRegistryEntry>(Registry.TryGetRun(entry.RunId));
            Assert.Equal(RunRegistryStatus.Completed, recovered.Status);
            Assert.Equal(nameof(RunVerdict.Inconclusive), recovered.Outcome);
            Assert.Equal(labels.Count, recovered.Labels.Count);
            foreach (var (name, value) in labels)
            {
                Assert.Equal(value, recovered.Labels[name]);
            }

            Assert.Equal(events, File.ReadAllBytes(entry.EventsFilePath));
        }
        else
        {
            Assert.Equal(1, result.Pruned);
            Assert.Equal(3, result.Mutations);
            Assert.False(File.Exists(MetadataPath(entry)));
            Assert.False(File.Exists(entry.EventsFilePath));
        }
    }

    [Fact]
    public void UndecodableNestedPropertyName_PreservesMetadataAndEvents()
    {
        var entry = Registry.StartRun(["synthetic.e2e.yaml"]);
        SetRawLabels(entry, "{\"\\uD800\":\"value\"}");
        File.WriteAllBytes(entry.EventsFilePath, [0, 255, 13, 10, 123, 125]);
        var metadata = File.ReadAllBytes(MetadataPath(entry));
        var events = File.ReadAllBytes(entry.EventsFilePath);

        var result = Registry.MaintainHistory(new(keepHistory: true), _now, CancellationToken.None);

        Assert.Equal(0, result.Mutations);
        Assert.Equal(1, result.Skipped);
        Assert.True(result.Incomplete);
        Assert.Equal(metadata, File.ReadAllBytes(MetadataPath(entry)));
        Assert.Equal(events, File.ReadAllBytes(entry.EventsFilePath));
    }

    [Fact]
    public void Cancellation_AndAbsentOutput_DoNotMutateOrCreateDirectories()
    {
        var registry = Registry;
        Assert.Null(RunHistoryMaintenance.TryAtStartup(registry,
            new WorkspaceRunLock(OutputDirectory, workspace: null), new()));
        Assert.False(Directory.Exists(OutputDirectory));
        var entry = SeedTerminal(_now.AddDays(-100));
        var before = File.ReadAllBytes(MetadataPath(entry));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = registry.MaintainHistory(new(), _now, cancelled.Token);
        Assert.True(result.Incomplete);
        Assert.Equal(0, result.Mutations);
        Assert.Equal(before, File.ReadAllBytes(MetadataPath(entry)));
    }

    [Fact]
    public void ReadersAcrossInstances_ObserveRecoveryAndPruningWithoutMaintenance()
    {
        var reader = Registry;
        var orphan = Registry.StartRun(["synthetic.e2e.yaml"]);
        Assert.Equal(RunRegistryStatus.Running, reader.TryGetRun(orphan.RunId)!.Status);
        Registry.MaintainHistory(new(), _now, CancellationToken.None);
        Assert.Equal(RunRegistryStatus.Completed, reader.TryGetRun(orphan.RunId)!.Status);
        var old = SeedTerminal(_now.AddDays(-100));
        Assert.Contains(reader.ListRuns(), entry => entry.RunId == old.RunId);
        Registry.MaintainHistory(new(), _now, CancellationToken.None);
        Assert.Null(reader.TryGetRun(old.RunId));
        Assert.DoesNotContain(reader.ListRuns(), entry => entry.RunId == old.RunId);
    }

    [Fact]
    public void ReadersOfAbsentHistory_DoNotCreateTheOutputDirectoryOrLock()
    {
        var reader = Registry;
        var id = "run-" + new string('a', 32);
        Assert.Null(reader.TryGetRun(id));
        Assert.Empty(reader.ListRuns());
        Assert.IsType<GetRunStatusOutcome.RunNotFound>(new GetRunStatusOrchestrator(reader).Get(new(id)));
        var listing = Assert.IsType<ListRunsOutcome.Paged>(new ListRunsOrchestrator(reader).List(new()));
        Assert.Empty(listing.Result.Runs);
        Assert.False(Directory.Exists(OutputDirectory));
        Assert.False(Directory.Exists(_sandbox));
    }

    [ContainmentLinkTheory]
    [Trait("containment", "link")]
    [InlineData("directory")]
    [InlineData("metadata")]
    [InlineData("events")]
    public void LinkedRunArtefacts_AreNeverFollowedOrMutated(string kind)
    {
        var entry = SeedTerminal(_now.AddDays(-100));
        var runDirectory = Path.GetDirectoryName(MetadataPath(entry))!;
        var outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(outside);
        if (kind == "directory")
        {
            Directory.Move(runDirectory, Path.Combine(outside, "run"));
            Directory.CreateSymbolicLink(runDirectory, Path.Combine(outside, "run"));
        }
        else
        {
            var linked = kind == "metadata" ? MetadataPath(entry) : entry.EventsFilePath;
            var destination = Path.Combine(outside, Path.GetFileName(linked));
            File.Move(linked, destination);
            File.CreateSymbolicLink(linked, destination);
        }

        var outsideFiles = Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        var result = Registry.MaintainHistory(new(1, 1), _now, CancellationToken.None);
        Assert.Equal(0, result.Mutations);
        Assert.True(result.Incomplete);
        foreach (var (path, bytes) in outsideFiles)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        _output.WriteLine("MEASURED_LINK_REFUSAL: " + kind);
    }

    [Theory]
    [InlineData("--run-history-max-terminal-runs", "0")]
    [InlineData("--run-history-max-terminal-runs", "100001")]
    [InlineData("--run-history-max-age-days", "-1")]
    [InlineData("--run-history-max-age-days", "36501")]
    [InlineData("--run-history-max-age-days", "private-value")]
    public void InvalidSettings_RefuseWithoutEchoingValues(string flag, string value)
    {
        Assert.False(RunHistoryOptions.TryParseCommandLine([flag, value], out var options, out var error));
        Assert.Null(options);
        Assert.DoesNotContain("private-value", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--keep-run-history=private-value")]
    [InlineData("--run-history-max-age-days")]
    public void InvalidSettingShapes_AreRefused(string argument) =>
        Assert.False(RunHistoryOptions.TryParseCommandLine([argument], out _, out _));

    [Fact]
    public void Settings_Defaults_EqualsSyntax_Duplicates_AndConflictsAreValidated()
    {
        Assert.True(RunHistoryOptions.TryParseCommandLine([], out var defaults, out _));
        Assert.Equal(1000, defaults!.MaxTerminalRuns);
        Assert.Equal(30, defaults.MaxAgeDays);
        Assert.False(defaults.KeepHistory);
        Assert.True(RunHistoryOptions.TryParseCommandLine(
            ["--run-history-max-terminal-runs=5", "--run-history-max-age-days", "7"], out var custom, out _));
        Assert.Equal(5, custom!.MaxTerminalRuns);
        Assert.Equal(7, custom.MaxAgeDays);
        Assert.False(RunHistoryOptions.TryParseCommandLine(
            ["--run-history-max-age-days", "7", "--run-history-max-age-days=8"], out _, out _));
        Assert.False(RunHistoryOptions.TryParseCommandLine(
            ["--keep-run-history", "--run-history-max-age-days", "7"], out _, out _));
        Assert.True(RunHistoryOptions.TryParseCommandLine(["--keep-run-history"], out var keep, out _));
        Assert.True(keep!.KeepHistory);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunHistoryOptions(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunHistoryOptions(maxAgeDays: 0));
    }

    private RunRegistryEntry SeedTerminal(DateTimeOffset finished)
    {
        var registry = Registry;
        var entry = registry.StartRun(["synthetic.e2e.yaml"]);
        registry.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        File.WriteAllText(entry.EventsFilePath, "fixed server evidence");
        var path = MetadataPath(entry);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["run"]!["startedAt"] = finished.AddMinutes(-1);
        document["run"]!["finishedAt"] = finished;
        File.WriteAllText(path, document.ToJsonString());
        return registry.TryGetRun(entry.RunId)!;
    }

    private void SetRawLabels(RunRegistryEntry entry, string labelsJson)
    {
        var document = JsonNode.Parse(File.ReadAllText(MetadataPath(entry)))!;
        document["run"]!["labels"] = "raw-label-fixture";
        var text = document.ToJsonString().Replace("\"labels\":\"raw-label-fixture\"", "\"labels\":" + labelsJson, StringComparison.Ordinal);
        Assert.Contains("\"labels\":" + labelsJson, text, StringComparison.Ordinal);
        File.WriteAllText(MetadataPath(entry), text);
    }

    private string MetadataPath(RunRegistryEntry entry) => Path.Combine(OutputDirectory, entry.RunId, FileRunRegistry.EntryFileName);
}
