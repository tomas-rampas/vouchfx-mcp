using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
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

    /// <summary>
    /// The STORAGE layer refuses a label map that breaches <see cref="RunLabelRules"/>, in both
    /// implementations — it does not assume its caller checked (a security review's MINOR finding).
    /// </summary>
    /// <remarks>
    /// <c>RunSuiteOrchestrator.ValidateLabels</c> already refuses these before a registry is reached,
    /// so in production this path is unreachable through <c>run_suite</c>. That is exactly why it is
    /// asserted here: the guarantee is that a SECOND caller of <see cref="IRunRegistry"/> — a future
    /// story, a test harness, anything — cannot record what the tool boundary would have rejected,
    /// and both implementations enforce it because both build their entry through the one shared
    /// <c>RunRegistryCore.CreateStartedEntry</c>.
    /// </remarks>
    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void StartRun_RejectsLabelsThatBreachTheSharedBounds(RegistryKind kind)
    {
        var registry = Create(kind);

        var tooMany = Enumerable
            .Range(0, RunLabelRules.MaxCount + 1)
            .ToDictionary(index => $"k{index}", _ => "v", StringComparer.Ordinal);
        Assert.Throws<ArgumentException>(() => registry.StartRun(["/suites/a.e2e.yaml"], tooMany));

        var keyTooLong = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [new string('k', RunLabelRules.MaxKeyLength + 1)] = "v",
        };
        Assert.Throws<ArgumentException>(() => registry.StartRun(["/suites/a.e2e.yaml"], keyTooLong));

        var valueTooLong = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trigger"] = new string('v', RunLabelRules.MaxValueLength + 1),
        };
        Assert.Throws<ArgumentException>(() => registry.StartRun(["/suites/a.e2e.yaml"], valueTooLong));

        // Refused rather than escaped: a label is STORED and matched verbatim by the host, so
        // silently rewriting one to its escaped form would break the correlation it exists for.
        var controlCharacter = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trigger"] = "agent" + (char)7 + "author",
        };
        Assert.Throws<ArgumentException>(() => registry.StartRun(["/suites/a.e2e.yaml"], controlCharacter));

        // Nothing was recorded by any of the four attempts.
        Assert.Empty(registry.ListRuns());
    }

    /// <summary>
    /// <see cref="FileRunRegistry"/> refuses to WRITE an entry larger than
    /// <see cref="FileRunRegistry.MaxEntryFileBytes"/> — the size its own reader skips — and does so
    /// with an <see cref="IOException"/>, which is what routes it to <c>VFX-E-1502</c> rather than
    /// out of the tool handler uncoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The write-side check exists because the caller-side bounds are CHARACTER counts and this
    /// one is a BYTE count</b> (a gatekeeper/security review's MINOR finding). The encoder escapes
    /// every non-ASCII character to a six-byte <c>\uXXXX</c> sequence, which is what the non-ASCII
    /// spec paths below exercise: they sit inside every documented character bound and still
    /// serialise past 64 KB. Without the check the entry was written and then permanently invisible —
    /// <c>ListRuns</c> skips an oversized file — so the run proceeded, produced a verdict, and had no
    /// record anywhere.
    /// </para>
    /// <para>
    /// <b>Why the exception TYPE is asserted rather than the resulting VFX code.</b> This test drives
    /// the registry directly; the mapping from <see cref="IOException"/> to the catalogued
    /// <c>VFX-E-1502 RunNotRecorded</c> outcome is
    /// <c>RunSuiteOrchestratorTests.RunAsync_RegistryStorageFailure_ReturnsRunNotRecordedWithoutInvokingRunner</c>'s
    /// subject (its <c>diskFull</c> case throws exactly this type). The two together are the claim:
    /// an oversized entry ends the call as VFX-E-1502 with nothing run. Asserting the type here is
    /// what pins the deliberate choice NOT to throw <c>RunArtefactStorageException</c>, which derives
    /// from <see cref="ArgumentException"/> and would bypass that orchestrator catch entirely.
    /// </para>
    /// </remarks>
    [Fact]
    public void StartRun_FileBacked_RefusesAnEntryLargerThanItsOwnReaderWouldAccept()
    {
        var registry = Create(RegistryKind.FileBacked);

        // Non-ASCII, so each character costs six bytes escaped: 40 paths x 400 characters is 16,000
        // characters (inside SuitePathExpander's own 24,000-character bound) but ~96 KB serialised.
        var oversizedSpecPaths = Enumerable
            .Range(0, 40)
            .Select(index => "/suites/" + new string('é', 400) + $"-{index}.e2e.yaml")
            .ToArray();

        Assert.Throws<IOException>(() => registry.StartRun(oversizedSpecPaths));

        // Refused, not half-written: no readable entry, and no temp residue left behind either.
        Assert.Empty(registry.ListRuns());
        Assert.Empty(Directory.Exists(_outputDirectory)
            ? Directory.GetFiles(_outputDirectory, "*.tmp-*", SearchOption.AllDirectories)
            : []);
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

    /// <summary>
    /// vouchfx-mcp#114: the same round-trip <see cref="RecordStatusTransition_ToCompleted_StampsTheOutcomeAndFinishTime"/>
    /// pins for status/outcome/finish-time, extended to the new <c>remediationHint</c> argument — both
    /// registry implementations must round-trip it identically, since a caller must be unable to tell
    /// which one it got.
    /// </summary>
    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ToCompleted_PersistsTheRemediationHint(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var completed = registry.RecordStatusTransition(
            started.RunId,
            RunRegistryStatus.Completed,
            nameof(RunVerdict.Inconclusive),
            remediationHint: "The engine reported an environment configuration error and a suite produced no scenario result: pull access denied.");

        Assert.NotNull(completed);
        Assert.Equal(
            "The engine reported an environment configuration error and a suite produced no scenario result: pull access denied.",
            completed.RemediationHint);
        AssertSameEntry(completed, registry.TryGetRun(started.RunId));
    }

    /// <summary>
    /// vouchfx-mcp#114, from its review: an entry that fitted at <c>StartRun</c> but would pass
    /// <see cref="FileRunRegistry.MaxEntryFileBytes"/> once the hint is added is still COMPLETED —
    /// without the hint — rather than left <c>running</c> by a refused write.
    /// </summary>
    /// <remarks>
    /// The spec paths are non-ASCII, so each character costs six bytes escaped: the started entry
    /// lands a little under the cap, with room for the terminal fields but not for a thousand
    /// escaped characters of hint. The precondition is measured on disk rather than assumed.
    /// </remarks>
    [Fact]
    public void RecordStatusTransition_FileBacked_CompletesWithoutAHintThatWouldPassTheCap()
    {
        var registry = Create(RegistryKind.FileBacked);
        var nearCapSpecPaths = Enumerable
            .Range(0, 26)
            .Select(index => "/suites/" + new string('é', 400) + $"-{index}.e2e.yaml")
            .ToArray();

        var started = registry.StartRun(nearCapSpecPaths);
        var startedBytes = new FileInfo(EntryPathOf(started.RunId)).Length;
        Assert.True(
            startedBytes > FileRunRegistry.MaxEntryFileBytes - 4_000 && startedBytes < FileRunRegistry.MaxEntryFileBytes - 500,
            "The started entry is not in the near-cap band this row needs; adjust the spec-path count.");

        var completed = registry.RecordStatusTransition(
            started.RunId,
            RunRegistryStatus.Completed,
            nameof(RunVerdict.Inconclusive),
            remediationHint: new string('é', 1_000));

        Assert.NotNull(completed);
        Assert.Equal(RunRegistryStatus.Completed, completed.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), completed.Outcome);
        Assert.Null(completed.RemediationHint);
        AssertSameEntry(completed, registry.TryGetRun(started.RunId));
        Assert.True(
            new FileInfo(EntryPathOf(started.RunId)).Length <= FileRunRegistry.MaxEntryFileBytes,
            "The completed entry was written past the cap its own reader accepts.");
    }

    /// <summary>
    /// A Copilot review on #122 (high): the row above only ever gated the fit check on THIS call's own
    /// incoming <c>remediationHint</c> argument. That is not the same thing as what the completed entry
    /// actually carries — <see cref="RunRegistryCore.ApplyStatusTransition"/>'s "null keeps what is
    /// recorded" convention means a <see langword="null"/> argument (the ordinary Pass/Fail case)
    /// still lets a hint through when <c>existing.RemediationHint</c> already held one. Nothing in
    /// <c>ReadEntry</c>'s consistency checks ties a hint's presence to a terminal status, so a version-2
    /// <c>running</c> document that already carries one is read back exactly like a genuine one. Before
    /// the fix, such a hint reached <c>Persist</c> unnoticed on a plain completion and threw, leaving the
    /// run <c>running</c> forever — the exact phantom this whole mechanism exists to prevent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no product path that starts a run with a hint already on it</b> — <c>StartRun</c>
    /// takes no hint parameter, and the only site that ever supplies a non-null one is a run's single
    /// completing write. So the poisoned precondition here is written straight to disk, exactly as
    /// <c>FileRegistry_SkipsAnEntryWhoseStartedAtIsOutOfRange_AndStartRunStillWorks</c> and its
    /// neighbours already do for other forged-on-disk states — the registry directory is not a
    /// filesystem this server exclusively owns, so a document it did not itself write is a real
    /// precondition, not a hypothetical one.
    /// </para>
    /// <para>
    /// <b>Sized by MEASUREMENT</b>, the same discipline
    /// <see cref="RecordStatusTransition_FileBacked_CompletesWithoutAHintThatWouldPassTheCap"/> applies
    /// to its own near-cap band, used here for a document this test builds itself rather than one
    /// <c>StartRun</c> produces. The hint length is chosen so the RUNNING document (hint included)
    /// serialises to AT MOST <see cref="FileRunRegistry.MaxEntryFileBytes"/> — the precondition
    /// <c>ReadEntry</c> needs to accept it as `existing` at all — while completing it (status, outcome,
    /// and <c>finishedAt</c> stamped; the hint UNCHANGED) crosses the cap purely on the ordinary
    /// handful of extra bytes a completion adds, never on a second helping of hint. Both documents are
    /// serialised with the same <see cref="JsonSerializerOptions"/> shape <see cref="FileRunRegistry"/>'s
    /// own (private) document options use — <see cref="JsonSerializerDefaults.Web"/> plus
    /// <c>WriteIndented: true</c> — so this measurement and the production write can never disagree
    /// about a byte count.
    /// </para>
    /// <para>
    /// <b>Proven to fail without the fix.</b> Reverting <see cref="FileRunRegistry.RecordStatusTransition"/>'s
    /// check back to gating on the incoming argument reproduces an unhandled <see cref="IOException"/>
    /// out of this exact row (<c>Persist</c> refusing the oversized carried-over hint) rather than a
    /// completed, hint-cleared entry.
    /// </para>
    /// </remarks>
    [Fact]
    public void RecordStatusTransition_FileBacked_ClearsACarriedOverHintThatWouldPassTheCap()
    {
        static byte[] Envelope(RunRegistryEntry entry, JsonSerializerOptions options) =>
            JsonSerializer.SerializeToUtf8Bytes(
                new { version = FileRunRegistry.CurrentFormatVersion, run = entry }, options);

        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        // Templates sharing an EMPTY hint, so their byte-length difference is purely the cost of
        // completing the run — status, outcome, and finishedAt — with no hint characters in either.
        var runningTemplate = started with { RemediationHint = string.Empty };
        var completedTemplate = runningTemplate with
        {
            Status = RunRegistryStatus.Completed,
            Outcome = nameof(RunVerdict.Inconclusive),
            FinishedAtUtc = DateTimeOffset.UtcNow,
        };

        var runningOverhead = Envelope(runningTemplate, jsonOptions).Length;
        var completedOverhead = Envelope(completedTemplate, jsonOptions).Length;
        Assert.True(
            completedOverhead > runningOverhead,
            "Expected completing the run to cost strictly more bytes than staying running.");

        // The largest hint that keeps the RUNNING document at or under the cap. Completing it then
        // costs exactly (completedOverhead - runningOverhead) bytes more than that — the ordinary
        // completion overhead — without adding a single character to the hint itself.
        var hintLength = FileRunRegistry.MaxEntryFileBytes - runningOverhead;
        Assert.True(
            hintLength is > 40_000 and < FileRunRegistry.MaxEntryFileBytes,
            "The measured non-hint overhead put the required hint length outside a sane band.");

        var poisoned = runningTemplate with { RemediationHint = new string('h', hintLength) };
        var poisonedBytes = Envelope(poisoned, jsonOptions);
        Assert.True(
            poisonedBytes.Length <= FileRunRegistry.MaxEntryFileBytes,
            "The poisoned running fixture must itself fit, or ReadEntry would refuse it as oversized " +
            "rather than exercising the completing transition this row targets.");

        File.WriteAllBytes(EntryPathOf(started.RunId), poisonedBytes);

        // Precondition: the poisoned entry reads back as a genuine `running` entry that already
        // carries the hint — the other half of the finding, that ReadEntry does not refuse it on the
        // way in.
        var existing = registry.TryGetRun(started.RunId);
        Assert.NotNull(existing);
        Assert.Equal(RunRegistryStatus.Running, existing.Status);
        Assert.Equal(hintLength, existing.RemediationHint?.Length);

        // The completing call: an ordinary Pass/Fail-shaped completion passing NO remediationHint —
        // the common case, and the one the old, argument-gated check missed entirely because it never
        // inspected what `existing` already carried.
        var completed = registry.RecordStatusTransition(
            started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Inconclusive));

        Assert.NotNull(completed);
        Assert.Equal(RunRegistryStatus.Completed, completed.Status);
        Assert.Equal(nameof(RunVerdict.Inconclusive), completed.Outcome);
        Assert.Null(completed.RemediationHint);
        AssertSameEntry(completed, registry.TryGetRun(started.RunId));
        Assert.True(
            new FileInfo(EntryPathOf(started.RunId)).Length <= FileRunRegistry.MaxEntryFileBytes,
            "The completed entry was written past the cap its own reader accepts.");
    }

    /// <summary>
    /// A Copilot review on #122 (high): a version-1 <c>running</c> entry accepted near the cap must
    /// still be COMPLETED. Version-2 documents used to write <c>"remediationHint": null</c>, so completing
    /// a version-1 entry, which has no such property, grew it by the property's bytes on top of what any
    /// completion adds, and <c>Persist</c> refused the result, leaving the run <c>running</c> for good.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version-1 document is written straight to disk, as the neighbouring rows do for other
    /// forged-on-disk states: this server writes version 2, so only something else can put a
    /// version-1 <c>running</c> entry where it will complete one. It is padded through a label, which
    /// <c>ReadEntry</c> accepts at any length.
    /// </para>
    /// <para>
    /// <b>Sized by measurement</b> so the completion fits exactly WITHOUT the property and does not fit
    /// WITH it. Both are measured with <see cref="JsonSerializerDefaults.Web"/> plus
    /// <c>WriteIndented: true</c>, the shape <see cref="FileRunRegistry"/>'s document options write,
    /// and the completion is the largest this call can record: <c>EnvironmentError</c> is the longest
    /// outcome, and seven fractional digits the longest finish time. The real finish time can only be
    /// shorter, so the real completion can only fit more easily; the last assertion checks the
    /// measurement against the bytes actually written.
    /// </para>
    /// <para>
    /// <b>Proven to fail without the fix.</b> With the null written back into the document, this row's
    /// completion throws <see cref="IOException"/> out of <c>Persist</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void RecordStatusTransition_FileBacked_CompletesANearCapVersion1Entry()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        // Serialised directly, never through a JsonNode: re-encoding a node escapes the '+' in each
        // timestamp's offset as \u002B, five bytes the registry's own writes never contain.
        var withHintProperty = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var withoutHintProperty = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    static typeInfo =>
                    {
                        foreach (var property in typeInfo.Properties.Where(property => property.Name == "remediationHint"))
                        {
                            property.ShouldSerialize = static (_, _) => false;
                        }
                    },
                },
            },
        };

        byte[] Document(RunRegistryEntry entry, int version, bool withHint) =>
            JsonSerializer.SerializeToUtf8Bytes(new { version, run = entry }, withHint ? withHintProperty : withoutHintProperty);

        RunRegistryEntry Padded(int length) =>
            started with { Labels = new Dictionary<string, string> { ["pad"] = new string('p', length) } };

        RunRegistryEntry Completed(RunRegistryEntry entry) => entry with
        {
            Status = RunRegistryStatus.Completed,
            Outcome = nameof(RunVerdict.EnvironmentError),
            FinishedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_111_111),
        };

        const int probeLength = 1_000;
        var padLength = probeLength + FileRunRegistry.MaxEntryFileBytes
            - Document(Completed(Padded(probeLength)), FileRunRegistry.CurrentFormatVersion, withHint: false).Length;
        var legacy = Document(Padded(padLength), version: 1, withHint: false);

        Assert.True(
            legacy.Length <= FileRunRegistry.MaxEntryFileBytes,
            "The version-1 running fixture must itself fit, or ReadEntry would refuse it as oversized.");
        Assert.True(
            Document(Completed(Padded(padLength)), FileRunRegistry.CurrentFormatVersion, withHint: true).Length
                > FileRunRegistry.MaxEntryFileBytes,
            "The fixture must be close enough to the cap that writing the null property would push it over.");

        File.WriteAllBytes(EntryPathOf(started.RunId), legacy);

        var existing = registry.TryGetRun(started.RunId);
        Assert.NotNull(existing);
        Assert.Equal(RunRegistryStatus.Running, existing.Status);
        Assert.Null(existing.RemediationHint);

        var completed = registry.RecordStatusTransition(
            started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.EnvironmentError));

        Assert.NotNull(completed);
        Assert.Equal(RunRegistryStatus.Completed, completed.Status);
        Assert.Equal(nameof(RunVerdict.EnvironmentError), completed.Outcome);
        AssertSameEntry(completed, registry.TryGetRun(started.RunId));

        var written = new FileInfo(EntryPathOf(started.RunId)).Length;
        Assert.True(
            written is <= FileRunRegistry.MaxEntryFileBytes and >= FileRunRegistry.MaxEntryFileBytes - 8,
            "The completed entry's size is not what the measurement predicted: at most the cap, and short "
            + "of it only by fractional-second digits the serialiser trimmed.");
    }

    /// <summary>
    /// The shape the row above depends on: <c>remediationHint</c> is written into <c>run.json</c> only
    /// when the run carries a hint, so a document without one keeps version 1's shape.
    /// </summary>
    [Fact]
    public void FileRegistry_WritesTheHintPropertyOnlyWhenThereIsAHint()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

        bool HasHintProperty(string runId) =>
            JsonNode.Parse(File.ReadAllText(EntryPathOf(runId)))!["run"]!.AsObject().ContainsKey("remediationHint");

        var plain = registry.StartRun(["/suites/orders.e2e.yaml"]);
        Assert.False(HasHintProperty(plain.RunId), "A running entry, which never carries a hint, was written with the property.");

        registry.RecordStatusTransition(plain.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        Assert.False(HasHintProperty(plain.RunId), "A completion without a hint was written with the property.");
        Assert.Null(registry.TryGetRun(plain.RunId)?.RemediationHint);

        var hinted = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(
            hinted.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Inconclusive), remediationHint: "Check that Docker is running.");
        Assert.True(HasHintProperty(hinted.RunId), "A completion with a hint was written without the property.");
        Assert.Equal("Check that Docker is running.", registry.TryGetRun(hinted.RunId)?.RemediationHint);
    }

    /// <summary>
    /// A Copilot review on #122 (high), at its root: every entry <c>StartRun</c> accepts can be
    /// completed. The completing write adds the terminal status, the outcome and the finish time, and
    /// an entry accepted within those bytes of the cap used to fail its completion and read as
    /// <c>running</c> for good. <c>StartRun</c> now refuses it instead, while nothing has been run.
    /// </summary>
    /// <remarks>
    /// A sweep rather than one sized fixture, because <c>StartRun</c> mints the run id and start time
    /// itself: the spec path grows one byte at a time across the band where the running entry fits and
    /// its completion might not. Each accepted entry is completed with the longest status and outcome
    /// there are. Without the start-time check, entries in that band were accepted and their
    /// completion threw <see cref="IOException"/> out of <c>Persist</c>.
    /// </remarks>
    [Fact]
    public void StartRun_FileBacked_AcceptsOnlyEntriesItCanComplete()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

        // The running entry grows one byte per ASCII character of its only spec path.
        const int probeLength = 1_000;
        var probe = registry.StartRun(["/suites/" + new string('a', probeLength) + ".e2e.yaml"]);
        var lengthAtCap = probeLength + (int)(FileRunRegistry.MaxEntryFileBytes - new FileInfo(EntryPathOf(probe.RunId)).Length);

        var accepted = 0;
        var refused = 0;
        for (var length = lengthAtCap - 120; length <= lengthAtCap; length++)
        {
            RunRegistryEntry started;
            try
            {
                started = registry.StartRun(["/suites/" + new string('a', length) + ".e2e.yaml"]);
            }
            catch (IOException)
            {
                refused++;
                continue;
            }

            accepted++;
            var completed = registry.RecordStatusTransition(
                started.RunId, RunRegistryStatus.Cancelled, nameof(RunVerdict.EnvironmentError));

            Assert.NotNull(completed);
            Assert.Equal(RunRegistryStatus.Cancelled, registry.TryGetRun(started.RunId)?.Status);
            Assert.True(
                new FileInfo(EntryPathOf(started.RunId)).Length <= FileRunRegistry.MaxEntryFileBytes,
                "A completed entry was written past the cap its own reader accepts.");
        }

        Assert.True(accepted > 0 && refused > 0, "The sweep did not cross the refusal boundary; recalibrate it.");
    }

    /// <summary>
    /// The complement: a <see langword="null"/> hint (the ordinary case — most outcomes carry none)
    /// round-trips as <see langword="null"/>, never as an empty string or a placeholder.
    /// </summary>
    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ToCompletedWithNoHint_PersistsNull(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        var completed = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        Assert.NotNull(completed);
        Assert.Null(completed.RemediationHint);
        Assert.Null(registry.TryGetRun(started.RunId)?.RemediationHint);
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

        // Both legal spellings of a defensive double-complete: repeating the SAME outcome, and
        // passing null (which means "keep what is recorded"). Neither may re-stamp the finish time —
        // the run ended when it ended, and moving it would make a duration computed from the entry
        // lie. Re-recording a DIFFERENT outcome is refused; that is the test below.
        var second = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        var third = registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed);

        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(first.FinishedAtUtc, second.FinishedAtUtc);
        Assert.Equal(first.FinishedAtUtc, third.FinishedAtUtc);
        Assert.Equal(nameof(RunVerdict.Pass), third.Outcome);
        AssertSameEntry(third, registry.TryGetRun(started.RunId));
    }

    /// <summary>
    /// m2: terminal → terminal may repeat a recorded outcome but must never REWRITE it. Refused at
    /// the STORAGE layer rather than trusted from a caller, because the entry is the record a later
    /// <c>explain_run</c> and a future <c>list_runs</c> answer from — silently overwriting a
    /// <c>Pass</c> the engine genuinely produced with an <c>Inconclusive</c> derived from bookkeeping
    /// would make the registry state something no run ever decided.
    /// </summary>
    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_TerminalToTerminal_RefusesToRewriteARecordedOutcome(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);
        registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        // The concrete case from the review: a completed/Pass run being re-recorded as
        // cancelled/Inconclusive.
        Assert.Throws<ArgumentException>(
            () => registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Cancelled, nameof(RunVerdict.Inconclusive)));

        // Same terminal status, different outcome — refused for the same reason.
        Assert.Throws<ArgumentException>(
            () => registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Fail)));

        var unchanged = registry.TryGetRun(started.RunId);
        Assert.NotNull(unchanged);
        Assert.Equal(RunRegistryStatus.Completed, unchanged.Status);
        Assert.Equal(nameof(RunVerdict.Pass), unchanged.Outcome);
    }

    /// <summary>
    /// m1, write side: a terminal status must carry a verdict. "This run finished" and "we never
    /// learned what it decided" is not a state the four-verdict taxonomy has a name for — a run that
    /// reached none is <c>Inconclusive</c>, and saying so is the caller's job.
    /// </summary>
    [Theory]
    [InlineData(RegistryKind.InMemory)]
    [InlineData(RegistryKind.FileBacked)]
    public void RecordStatusTransition_ToATerminalStatusWithNoOutcome_IsRefused(RegistryKind kind)
    {
        var registry = Create(kind);
        var started = registry.StartRun(["/suites/orders.e2e.yaml"]);

        // Every terminal status, so the rule cannot hold for `completed` alone.
        foreach (var terminal in RunRegistryStatus.All.Where(RunRegistryStatus.IsTerminal))
        {
            Assert.Throws<ArgumentException>(() => registry.RecordStatusTransition(started.RunId, terminal));
        }

        // Nothing was written: the run is still in flight, and still not explain_run's default.
        var unchanged = registry.TryGetRun(started.RunId);
        Assert.NotNull(unchanged);
        Assert.Equal(RunRegistryStatus.Running, unchanged.Status);
        Assert.Null(unchanged.Outcome);
        Assert.Null(unchanged.FinishedAtUtc);
        Assert.Null(registry.MostRecentFinishedRun());

        // The counterpart that must STILL work: null means "keep what is recorded", so a defensive
        // double-complete of an already-outcome-carrying run is unaffected by this rule.
        registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));
        Assert.Equal(nameof(RunVerdict.Pass), registry.RecordStatusTransition(started.RunId, RunRegistryStatus.Completed)?.Outcome);
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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

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
        var first = new FileRunRegistry(_outputDirectory, workspace: null);
        var entry = first.StartRun(["/suites/orders.e2e.yaml"]);
        first.RecordStatusTransition(entry.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.EnvironmentError));

        // A fresh instance stands in for a fresh SERVER PROCESS: it shares nothing with `first` but
        // the directory. (RealRunRegistryMcpTests proves the same thing one layer up, through two
        // independent MCP servers.)
        var second = new FileRunRegistry(_outputDirectory, workspace: null);

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
        var first = new FileRunRegistry(_outputDirectory, workspace: null);
        var earlier = first.StartRun(["/suites/earlier.e2e.yaml"]);
        first.RecordStatusTransition(earlier.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        var second = new FileRunRegistry(_outputDirectory, workspace: null);
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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        var future = registry.StartRun(["/suites/future.e2e.yaml"]);

        var futureDocument = File.ReadAllText(EntryPathOf(future.RunId))
            .Replace($"\"version\": {FileRunRegistry.CurrentFormatVersion}", "\"version\": 999", StringComparison.Ordinal);
        Assert.Contains("999", futureDocument, StringComparison.Ordinal);
        File.WriteAllText(EntryPathOf(future.RunId), futureDocument);

        // Skipped, never best-effort-misread: a genuinely FUTURE version that renames or re-means a
        // field (999 stands in for one) must not have its status or outcome reported as fact by a
        // server that only knows up to FileRunRegistry.CurrentFormatVersion. Contrast the test below,
        // where an OLDER version this server itself used to write is deliberately still readable.
        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    /// <summary>
    /// vouchfx-mcp#114: a version-1 document — every <c>run.json</c> this server wrote before
    /// <see cref="RunRegistryEntry.RemediationHint"/> existed — remains fully readable after the
    /// format bump to <see cref="FileRunRegistry.CurrentFormatVersion"/> 2. The missing
    /// <c>remediationHint</c> property reads back as <see langword="null"/>, exactly the fact a
    /// version-1 run's record should report: no hint was ever captured for it.
    /// </summary>
    /// <remarks>
    /// Hand-written at <c>"version": 1</c> WITHOUT a <c>remediationHint</c> property at all — not
    /// merely a null one — because that is the literal byte shape every pre-#114 <c>run.json</c> on a
    /// real host has, and <see cref="System.Text.Json"/>'s own "a missing property on a nullable
    /// parameter binds to its default" behaviour is exactly the mechanism
    /// <see cref="FileRunRegistry.MinReadableFormatVersion"/>'s remarks say makes this safe.
    /// </remarks>
    [Fact]
    public void FileRegistry_AVersionOneDocumentWithNoRemediationHintProperty_StillReadsBack()
    {
        var oldRunId = "run-" + new string('f', 32);
        Directory.CreateDirectory(Path.Combine(_outputDirectory, oldRunId));
        File.WriteAllText(EntryPathOf(oldRunId), $$"""
            {
              "version": 1,
              "run": {
                "runId": "{{oldRunId}}",
                "status": "completed",
                "outcome": "Pass",
                "startedAt": "2026-01-01T00:00:00+00:00",
                "finishedAt": "2026-01-01T00:00:01+00:00",
                "specPaths": [ "/suites/pre-114.e2e.yaml" ],
                "eventsFilePath": {{JsonSerializer.Serialize(Path.Combine(_outputDirectory, oldRunId, FileRunRegistry.EventsFileName))}},
                "labels": {}
              }
            }
            """);

        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

        var entry = registry.TryGetRun(oldRunId);
        Assert.NotNull(entry);
        Assert.Equal(RunRegistryStatus.Completed, entry.Status);
        Assert.Equal(nameof(RunVerdict.Pass), entry.Outcome);
        Assert.Null(entry.RemediationHint);

        // And it is not merely readable in isolation — it participates in ListRuns like any other
        // entry, which a version check applied too strictly (requiring exactly 2) would have broken.
        Assert.Equal([oldRunId], registry.ListRuns().Select(e => e.RunId));
    }

    /// <summary>
    /// The FORGED-entry case: a perfectly well-formed <c>run.json</c> whose <c>eventsFilePath</c>
    /// names a file outside the registry's own layout. Distinct from — and not covered by — the
    /// corrupt/truncated/oversized/unknown-version cases above, every one of which fails to PARSE.
    /// This one parses fine and is rejected on its CONTENT.
    /// </summary>
    /// <remarks>
    /// Why it still matters now that <c>explain_run</c>'s containment exemptions are RETIRED (so this
    /// is defence in depth rather than the load-bearing trust anchor it once was): an entry whose
    /// contents contradict its own location is not a usable record on any reading, and anything able
    /// to drop a file into the output directory (a shared build agent, a hostile dependency's
    /// postinstall, a synced folder) would otherwise be able to point <c>explain_run</c>'s DEFAULT at
    /// an arbitrary file — a path containment would now refuse on its own merits, but only if that
    /// path happens to be outside the root, and only when a workspace is configured at all. The
    /// registry mints exactly one events path per run id, so any other value is forged or foreign by
    /// definition, and cheaper to reject here than to reason about downstream.
    /// </remarks>
    [Fact]
    public void FileRegistry_IgnoresAWellFormedEntryWhoseEventsPathPointsOutsideTheRunsOwnDirectory()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
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
    }

    /// <summary>
    /// m1, read side. The write side refuses a terminal status with no outcome, and this is the other
    /// half of that symmetry: a file on disk is no more trusted than a caller. Without it, a
    /// hand-written or half-migrated <c>run.json</c> saying <c>{"status":"completed","outcome":null}</c>
    /// would read back as a FINISHED run carrying no verdict — which <c>explain_run</c> would then
    /// default to, and a future <c>list_runs</c> would project as a run that ended saying nothing.
    /// </summary>
    /// <remarks>
    /// Asserted against <see cref="FileRunRegistry"/> only because it is the only implementation with
    /// a read side to subvert: <see cref="InMemoryRunRegistry"/> has no ingress except
    /// <see cref="IRunRegistry.RecordStatusTransition"/>, which the write-side theory above already
    /// proves refuses this shape.
    /// </remarks>
    [Fact]
    public void FileRegistry_SkipsATerminalEntryCarryingNoOutcome()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        registry.RecordStatusTransition(good.RunId, RunRegistryStatus.Completed, nameof(RunVerdict.Pass));

        var verdictlessRunId = "run-" + new string('d', 32);
        Directory.CreateDirectory(Path.Combine(_outputDirectory, verdictlessRunId));
        File.WriteAllText(EntryPathOf(verdictlessRunId), $$"""
            {
              "version": {{FileRunRegistry.CurrentFormatVersion}},
              "run": {
                "runId": "{{verdictlessRunId}}",
                "status": "completed",
                "outcome": null,
                "startedAt": "2099-01-01T00:00:00+00:00",
                "finishedAt": "2099-01-01T00:00:01+00:00",
                "specPaths": [ "/suites/verdictless.e2e.yaml" ],
                "eventsFilePath": {{JsonSerializer.Serialize(Path.Combine(_outputDirectory, verdictlessRunId, FileRunRegistry.EventsFileName))}},
                "labels": {}
              }
            }
            """);

        // Everything else about this document is correct — including the minted events path — so it is
        // rejected on the terminal-outcome clause alone. Its startedAt is far in the future, so
        // without the clause it would sort FIRST and become explain_run's default.
        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
        Assert.Null(registry.TryGetRun(verdictlessRunId));
        Assert.Equal(good.RunId, registry.MostRecentFinishedRun()?.RunId);
    }

    /// <summary>
    /// An entry carrying <see cref="DateTimeOffset.MaxValue"/> as its <c>startedAt</c> is skipped —
    /// and, the load-bearing half, <see cref="IRunRegistry.StartRun"/> still works afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one defends against an uncoded CRASH, not a wrong answer</b> (a security review's MINOR
    /// finding, and the reason the timestamp joined <c>ReadEntry</c>'s consistency clause).
    /// <c>SeedStartedAtFloorFromDisk</c> seeds the monotonic floor from the NEWEST entry on disk, and
    /// <c>RunRegistryTimestamps.NextStartedAt</c> advances a floor it has caught up to with
    /// <c>floor.AddTicks(1)</c> — which throws <see cref="ArgumentOutOfRangeException"/> at
    /// <see cref="DateTimeOffset.MaxValue"/>. One planted or corrupted file therefore made EVERY
    /// subsequent <c>run_suite</c> call fail with a framework exception carrying no <c>VFX-</c> code,
    /// and — being on disk — did it again after every restart.
    /// </para>
    /// <para>
    /// The <c>StartRun</c> assertion is what makes this a regression test rather than a restatement of
    /// the skip rule: an implementation that skipped the entry on READ but still seeded the floor from
    /// it would satisfy the first assertion and fail the second.
    /// </para>
    /// </remarks>
    [Fact]
    public void FileRegistry_SkipsAnEntryWhoseStartedAtIsOutOfRange_AndStartRunStillWorks()
    {
        var poisonedRunId = "run-" + new string('e', 32);
        Directory.CreateDirectory(Path.Combine(_outputDirectory, poisonedRunId));
        File.WriteAllText(EntryPathOf(poisonedRunId), $$"""
            {
              "version": {{FileRunRegistry.CurrentFormatVersion}},
              "run": {
                "runId": "{{poisonedRunId}}",
                "status": "completed",
                "outcome": "Pass",
                "startedAt": {{JsonSerializer.Serialize(DateTimeOffset.MaxValue)}},
                "finishedAt": {{JsonSerializer.Serialize(DateTimeOffset.MaxValue)}},
                "specPaths": [ "/suites/poison.e2e.yaml" ],
                "eventsFilePath": {{JsonSerializer.Serialize(Path.Combine(_outputDirectory, poisonedRunId, FileRunRegistry.EventsFileName))}},
                "labels": {}
              }
            }
            """);

        // A FRESH instance, so the floor has not been seeded yet — the state a restarted server is in,
        // which is the state that made this permanent.
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

        Assert.Empty(registry.ListRuns());
        Assert.Null(registry.TryGetRun(poisonedRunId));

        // The whole point: the poison does not become this instance's monotonic floor, so a run can
        // still be started. Before the bound, this line threw ArgumentOutOfRangeException.
        var started = registry.StartRun(["/suites/good.e2e.yaml"]);
        Assert.Equal([started.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    [Fact]
    public void FileRegistry_SkipsAnOversizedEntryRatherThanReadingIt()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
        var good = registry.StartRun(["/suites/good.e2e.yaml"]);
        var oversized = registry.StartRun(["/suites/oversized.e2e.yaml"]);

        File.WriteAllText(EntryPathOf(oversized.RunId), new string('x', FileRunRegistry.MaxEntryFileBytes + 1));

        Assert.Equal([good.RunId], registry.ListRuns().Select(entry => entry.RunId));
    }

    [Fact]
    public void FileRegistry_IgnoresCrashResidueAndForeignDirectories()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);

        // A run id is spliced into a directory path, so its shape is a containment property. A
        // lookup for a malformed id is "no such run", never an escape.
        Assert.Null(registry.TryGetRun(Path.Combine("..", "..", "etc")));
        Assert.Null(registry.TryGetRun("run-" + new string('z', 32)));
        Assert.Null(registry.TryGetRun("run-short"));
    }

    [Fact]
    public void FileRegistry_PersistedDocumentCarriesTheSpecWireFieldNames()
    {
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
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
        var registry = new FileRunRegistry(_outputDirectory, workspace: null);
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

        // remediationHint is the one optional member: it is written only when the run carries a hint
        // (FileRegistry_WritesTheHintPropertyOnlyWhenThereIsAHint), and this run carries none.
        Assert.Equal(
            ["eventsFilePath", "finishedAt", "labels", "outcome", "runId", "specPaths", "startedAt", "status"],
            document.RootElement.GetProperty("run").EnumerateObject()
                .Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    // ── The scan cap, and the signal it now carries (US-S3-06's second rider) ────────────────────

    /// <summary>
    /// A <see cref="FileRunRegistry"/> walk that stops at its run-directory cap reports
    /// <see cref="RunListing.ScanCapped"/>, and one that exhausts the directory does not — including
    /// at the exact boundary, where a count-based check would get it wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driven through the internal small-cap constructor</b> (see <see cref="FileRunRegistry"/>'s
    /// own remarks on that seam): reaching the real 10,000 honestly costs ten thousand directory
    /// creations and establishes exactly the same boolean.
    /// </para>
    /// <para>
    /// <b>The boundary case is the point.</b> An implementation that compared the RETURNED COUNT
    /// against the cap would report a capped scan for a directory holding precisely <c>cap</c> runs —
    /// which was enumerated in full and is not capped at all. Reading one entry PAST the cap and
    /// checking whether it exists is what distinguishes them, and it is the same look-one-ahead device
    /// both pagers use for their cursors.
    /// </para>
    /// </remarks>
    [Fact]
    public void FileRunRegistry_ReportsWhetherItsScanStoppedAtTheRunCap()
    {
        // Seed with the production constructor so every entry is minted exactly as it would be in a
        // real workspace; only the READING instances below carry a small cap.
        var writer = new FileRunRegistry(_outputDirectory, workspace: null);
        for (var i = 0; i < 3; i++)
        {
            writer.StartRun([$"/suites/suite-{i}.e2e.yaml"]);
        }

        // Exactly at the cap: enumerated in full, so NOT capped — the case a count-based check fails.
        var atTheBoundary = new FileRunRegistry(_outputDirectory, workspace: null, maxRunsScanned: 3).ListRuns();
        Assert.Equal(3, atTheBoundary.Count);
        Assert.False(atTheBoundary.ScanCapped);

        // One below it: the walk stopped short, and says so.
        var capped = new FileRunRegistry(_outputDirectory, workspace: null, maxRunsScanned: 2).ListRuns();
        Assert.Equal(2, capped.Count);
        Assert.True(capped.ScanCapped);

        // And the production cap over the same three runs is comfortably uncapped.
        Assert.False(new FileRunRegistry(_outputDirectory, workspace: null).ListRuns().ScanCapped);
    }

    /// <summary>
    /// The OTHER cap — <see cref="FileRunRegistry.MaxDirectoriesExamined"/>, applied before the
    /// well-formed-run-id filter — reports a capped scan on its own, and is what stops a directory
    /// holding a mass of FOREIGN entries from being enumerated in full on every read.
    /// </summary>
    /// <remarks>
    /// <b>This branch had no test until a gatekeeper review found the claim of one</b>:
    /// <c>EnumerateRunIds</c>' documentation said "both caps are probed" while only
    /// <c>maxRunsScanned</c> had a seam, and reaching the real 100,000-entry cap honestly is not
    /// something a suite can afford. The second seam parameter closes that. The two caps are exercised
    /// SEPARATELY here: the run cap is set high enough that it cannot be what fires, so a capped result
    /// can only have come from the directory cap.
    /// </remarks>
    [Fact]
    public void FileRunRegistry_ReportsWhetherItsScanStoppedAtTheDirectoryCap()
    {
        var writer = new FileRunRegistry(_outputDirectory, workspace: null);
        writer.StartRun(["/suites/suite-0.e2e.yaml"]);

        // Foreign content: directories the registry must ignore as runs but must still COUNT as
        // examined, which is exactly what the directory cap exists to bound.
        for (var i = 0; i < 4; i++)
        {
            Directory.CreateDirectory(Path.Combine(_outputDirectory, $"not-a-run-{i}"));
        }

        // Five entries in total. Exactly at the cap: enumerated in full, so NOT capped — the same
        // boundary rule the run cap follows, and the same one a count-based check would get wrong.
        var atTheBoundary = new FileRunRegistry(
            _outputDirectory, workspace: null, maxRunsScanned: 1_000, maxDirectoriesExamined: 5).ListRuns();
        Assert.Single(atTheBoundary);
        Assert.False(atTheBoundary.ScanCapped);

        // One below it: the walk stopped short of the fifth entry, and says so — even though the run
        // cap (1,000) was nowhere near being reached, which is what makes this the directory cap's own
        // branch rather than the run cap's.
        var capped = new FileRunRegistry(
            _outputDirectory, workspace: null, maxRunsScanned: 1_000, maxDirectoriesExamined: 4).ListRuns();
        Assert.True(capped.ScanCapped);

        // And the production caps over the same directory are comfortably uncapped.
        Assert.False(new FileRunRegistry(_outputDirectory, workspace: null).ListRuns().ScanCapped);
    }

    /// <summary>
    /// <see cref="InMemoryRunRegistry"/> never reports a capped scan — it holds every run of the
    /// session in one dictionary and enumerates all of them, so <c>list_runs</c>' <c>truncated</c> is
    /// constantly false without a workspace. Asserted rather than assumed, because that constant is
    /// what a host without <c>--workspace</c> relies on.
    /// </summary>
    [Fact]
    public void InMemoryRunRegistry_NeverReportsACappedScan()
    {
        var registry = new InMemoryRunRegistry();
        Assert.False(registry.ListRuns().ScanCapped);

        for (var i = 0; i < 5; i++)
        {
            registry.StartRun([$"/suites/suite-{i}.e2e.yaml"]);
        }

        var listing = registry.ListRuns();
        Assert.Equal(5, listing.Count);
        Assert.False(listing.ScanCapped);
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
        RegistryKind.FileBacked => new FileRunRegistry(_outputDirectory, workspace: null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
