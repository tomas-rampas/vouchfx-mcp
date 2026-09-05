using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// The <see cref="IRunRegistry"/> used when the host launched this server with no
/// <c>--workspace</c> flag: process-lifetime, in-memory, and — by construction — incapable of
/// writing anything anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the compatibility mode, and its whole point is that it is invisible.</b> US-S3-08
/// established the rule that a host which did not opt into a workspace sees behaviour byte for byte
/// unchanged; US-S3-01 extends it to persistence. Without a workspace this server has no directory
/// it has been GIVEN permission to write into — an OS temp file for the events stream is a
/// pre-existing, documented behaviour, but a registry inventing its own base directory under the
/// user profile or the current working directory would be this server creating state on a host that
/// never asked for it. So it does not: no directory is created, no file is written, and the
/// registry dies with the process, exactly as the <c>ILastRunTracker</c> it replaces did.
/// </para>
/// <para>
/// <b>Behavioural parity with the retired tracker.</b> One instance per server session (this server
/// serves exactly one MCP session per process — see <c>Program.cs</c>'s
/// <c>SingleSessionMcpServerHostedService</c>), so "session-scoped" and "instance-scoped" coincide;
/// <c>explain_run</c>'s default resolves to the most recent FINISHED run
/// (<see cref="RunRegistryExtensions.MostRecentFinishedRun"/>), which is precisely what the tracker
/// — a record written only at completion — used to return.
/// </para>
/// <para>
/// <b>Thread safety:</b> every mutation happens under <see cref="_gate"/>, and every read returns an
/// immutable snapshot list built under that same lock. This is a strictly stronger guarantee than
/// the <see cref="Volatile"/> single-reference swap the retired <c>LastRunTracker</c> documented,
/// which was sufficient only because it held exactly ONE entry: a dictionary cannot be swapped
/// atomically the same way, and a reader enumerating it while <c>run_suite</c> inserted would throw.
/// Snapshotting under the lock also means a caller can enumerate the result at leisure without
/// holding anything.
/// </para>
/// </remarks>
public sealed class InMemoryRunRegistry : IRunRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunRegistryEntry> _runs = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastStartedAtUtc;

    /// <inheritdoc />
    public RunRegistryEntry StartRun(IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(specPaths);

        lock (_gate)
        {
            var runId = RunRegistryCore.MintRunId();
            var entry = RunRegistryCore.CreateStartedEntry(
                runId,
                MintTempEventsFilePath(runId),
                RunRegistryTimestamps.NextStartedAt(ref _lastStartedAtUtc),
                specPaths,
                labels);

            _runs[runId] = entry;
            return entry;
        }
    }

    /// <inheritdoc />
    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var existing))
            {
                return null;
            }

            var updated = RunRegistryCore.ApplyStatusTransition(existing, status, outcome);
            _runs[runId] = updated;
            return updated;
        }
    }

    /// <inheritdoc />
    public RunRegistryEntry? TryGetRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (_gate)
        {
            return _runs.GetValueOrDefault(runId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRegistryEntry> ListRuns()
    {
        lock (_gate)
        {
            return RunRegistryCore.OrderMostRecentFirst(_runs.Values);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// O(1), and semantically identical to the file-backed registry's answer — see
    /// <see cref="IRunRegistry.IsRecordedEventsFilePath"/>. The derivation is simply the inverse of
    /// <see cref="MintTempEventsFilePath"/>: this registry's paths encode the run id in the FILE NAME
    /// rather than in a parent directory, so that is the segment read back. A path that does not carry
    /// this registry's own name shape is answered <see langword="false"/> immediately, without taking
    /// the lock.
    /// </remarks>
    public bool IsRecordedEventsFilePath(string eventsPath)
    {
        ArgumentNullException.ThrowIfNull(eventsPath);

        var runId = TryDeriveRunIdFromTempEventsFileName(eventsPath);
        if (runId is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _runs.TryGetValue(runId, out var entry)
                && string.Equals(eventsPath, entry.EventsFilePath, PathSafetyGuard.PathComparison);
        }
    }

    /// <summary>The literal prefix every path <see cref="MintTempEventsFilePath"/> mints carries.</summary>
    private const string TempEventsFileNamePrefix = "vouchfx-mcp-events-";

    /// <summary>The literal suffix every path <see cref="MintTempEventsFilePath"/> mints carries.</summary>
    private const string TempEventsFileNameSuffix = ".jsonl";

    /// <summary>
    /// The events-file path for a run in no-workspace mode: the OS temp directory, under the SAME
    /// <c>vouchfx-mcp-events-*.jsonl</c> name shape <c>RunSuiteOrchestrator</c> has always used.
    /// </summary>
    /// <remarks>
    /// The name shape is load-bearing rather than cosmetic: <c>RunSuiteOrchestrator</c>'s
    /// best-effort retention sweep matches exactly that glob, so a file minted here is still swept
    /// after its retention window — and so are files left behind by servers that predate this
    /// registry. The run id's hex body is reused as the file's discriminator so a temp file can be
    /// traced back to its run by eye — and, since
    /// <see cref="IsRecordedEventsFilePath"/>, read back out of it.
    /// </remarks>
    private static string MintTempEventsFilePath(string runId) =>
        Path.Combine(
            Path.GetTempPath(),
            TempEventsFileNamePrefix + runId[RunRegistryCore.RunIdPrefix.Length..] + TempEventsFileNameSuffix);

    /// <summary>
    /// The run id <paramref name="eventsPath"/>'s FILE NAME encodes, or <see langword="null"/> when it
    /// does not carry <see cref="MintTempEventsFilePath"/>'s shape. Pure string arithmetic — never a
    /// filesystem call, since a caller-supplied path reaches here uncapped.
    /// </summary>
    private static string? TryDeriveRunIdFromTempEventsFileName(string eventsPath)
    {
        var fileName = Path.GetFileName(eventsPath);

        if (fileName.Length <= TempEventsFileNamePrefix.Length + TempEventsFileNameSuffix.Length
            || !fileName.StartsWith(TempEventsFileNamePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(TempEventsFileNameSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var body = fileName[TempEventsFileNamePrefix.Length..^TempEventsFileNameSuffix.Length];

        return RunRegistryCore.TryDeriveRunIdFromPathSegment(RunRegistryCore.RunIdPrefix + body);
    }
}
