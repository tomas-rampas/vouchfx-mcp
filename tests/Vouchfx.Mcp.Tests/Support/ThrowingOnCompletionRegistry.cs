using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// An <see cref="IRunRegistry"/> that records a run normally and then throws when its COMPLETION is
/// written back — the one condition that produces US-S6-05's completion-not-recorded warning record.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a "throws at every call" stub: the record under test only exists on the path
/// where a run reached a verdict AND the bookkeeping behind it failed, so <see cref="StartRun"/> has
/// to succeed for the scenario to be reachable at all. A stub that failed earlier would test the
/// RunNotRecorded outcome instead, which is a different branch with a different observable.
/// </para>
/// <para>
/// Backed by <see cref="InMemoryRunRegistry"/> rather than reimplementing entry construction, so the
/// entry this hands back — and therefore the runId the log records carry — is the real shape.
/// </para>
/// </remarks>
internal sealed class ThrowingOnCompletionRegistry : IRunRegistry
{
    private readonly InMemoryRunRegistry _inner = new();

    /// <summary>The exception thrown from <see cref="RecordStatusTransition"/>.</summary>
    /// <remarks>
    /// Its TYPE is what the warning record must carry as <c>errorType</c>; its MESSAGE is deliberately
    /// distinctive so a test can assert the message never reaches a record.
    /// </remarks>
    public static InvalidOperationException Failure { get; } =
        new("REGISTRY-WRITE-FAILED-MESSAGE-MUST-NOT-LEAK");

    public RunRegistryEntry StartRun(
        IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
        _inner.StartRun(specPaths, labels);

    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
        throw Failure;

    public RunRegistryEntry? TryGetRun(string runId) => _inner.TryGetRun(runId);

    public RunListing ListRuns() => _inner.ListRuns();
}
