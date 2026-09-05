using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// An <see cref="IRunRegistry"/> that fails loudly if anything looks a run up — the fixture that turns
/// "the argument bound runs before the lookup" from an assumption into an assertion.
/// </summary>
/// <remarks>
/// Shared rather than nested, because two tools' tests now need it: <c>get_run_status</c>'s and
/// <c>cancel_run</c>'s over-long-<c>runId</c> cases both assert that
/// <c>RunIdArgument.Validate</c> refuses BEFORE the registry is touched, and a hostile argument
/// reaching a lookup — or, for <c>cancel_run</c>, a run-lock probe — is the thing the bound exists to
/// prevent. <see cref="WasQueried"/> is what distinguishes "refused" from "refused first".
/// </remarks>
internal sealed class ThrowingOnLookupRegistry : IRunRegistry
{
    /// <summary>Whether any lookup was attempted. Set BEFORE the throw, so it is observable either way.</summary>
    public bool WasQueried { get; private set; }

    public RunRegistryEntry StartRun(
        IReadOnlyList<string> specPaths, IReadOnlyDictionary<string, string>? labels = null) =>
        throw new NotSupportedException();

    public RunRegistryEntry? RecordStatusTransition(string runId, string status, string? outcome = null) =>
        throw new NotSupportedException();

    public RunRegistryEntry? TryGetRun(string runId)
    {
        WasQueried = true;
        throw new InvalidOperationException("An argument bound should have refused this call first.");
    }

    public RunListing ListRuns()
    {
        WasQueried = true;
        throw new InvalidOperationException("An argument bound should have refused this call first.");
    }
}
