namespace Vouchfx.Mcp.Run;

/// <summary>
/// Session-scoped record of the most recent <c>run_suite</c> call's outcome — REQ-007's
/// default-to-last-run behaviour: <c>explain_run</c> reads this when its caller omits
/// <c>eventsPath</c>.
/// </summary>
/// <param name="EventsFilePath">The completed (or aborted) run's events-file path.</param>
/// <param name="Verdict">The run's own verdict token (<c>Pass</c>/<c>Fail</c>/<c>EnvironmentError</c>/<c>Inconclusive</c>), for display only — <c>explain_run</c> still re-derives its own diagnosis from the file itself.</param>
/// <param name="RecordedAtUtc">When this run was recorded.</param>
public sealed record LastRunInfo(string EventsFilePath, string Verdict, DateTimeOffset RecordedAtUtc);

/// <summary>
/// Abstracts "what was the last run this session" so <see cref="RunSuiteOrchestrator"/> (the writer)
/// and <c>Vouchfx.Mcp.Diagnosis.ExplainRunOrchestrator</c> (the reader) can be unit tested
/// independently of one another, and so a test can inject a pre-populated or empty tracker without
/// needing to drive a real <c>run_suite</c> call first.
/// </summary>
public interface ILastRunTracker
{
    /// <summary>The most recent run recorded this session, or <see langword="null"/> if none has completed yet.</summary>
    LastRunInfo? LastRun { get; }

    /// <summary>Records a run's outcome, replacing whatever was previously recorded.</summary>
    void RecordRun(string eventsFilePath, string verdict);
}

/// <summary>
/// The production <see cref="ILastRunTracker"/>: a single, process-lifetime instance shared between
/// <see cref="RunSuiteOrchestrator"/> and <c>ExplainRunOrchestrator</c> (constructed once in
/// <see cref="VouchfxMcpServerRegistration"/> and passed to both), one per server session — this
/// server serves exactly one MCP session per process (see <c>Program.cs</c>'s
/// <c>SingleSessionMcpServerHostedService</c>), so "session-scoped" and "instance-scoped" coincide
/// here.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> <see cref="LastRunInfo"/> is an immutable record, and the field holding it
/// is read/written exclusively via <see cref="Volatile.Read{T}"/>/<see cref="Volatile.Write{T}"/> —
/// the exact same pattern <see cref="Cli.CliPinVerifier"/>'s own cached-result field already uses and
/// documents: a reference assignment/read is atomic on .NET either way, but the explicit barrier
/// makes "safe under concurrent calls" intentional, not incidental, and rules out a torn or stale
/// read if <c>run_suite</c> and <c>explain_run</c> are ever in flight on different threads at once.
/// </remarks>
public sealed class LastRunTracker : ILastRunTracker
{
    private LastRunInfo? _lastRun;

    public LastRunInfo? LastRun => Volatile.Read(ref _lastRun);

    public void RecordRun(string eventsFilePath, string verdict)
    {
        ArgumentNullException.ThrowIfNull(eventsFilePath);
        ArgumentNullException.ThrowIfNull(verdict);

        Volatile.Write(ref _lastRun, new LastRunInfo(eventsFilePath, verdict, DateTimeOffset.UtcNow));
    }
}
