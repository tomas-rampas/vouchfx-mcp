using Microsoft.Extensions.Logging;
using Vouchfx.Mcp.Observability;

namespace Vouchfx.Mcp.Run;

/// <summary>Bookkeeping only. The exclusive workspace claim never proves engine cleanup.</summary>
internal static class RunHistoryMaintenance
{
    internal static RunHistoryMaintenanceResult? TryAtStartup(
        IRunRegistry registry, IRunLock? runLock, RunHistoryOptions options)
    {
        // Do not acquire a claim merely to create an otherwise absent output directory.
        if (registry is not FileRunRegistry files || !files.HasExistingOutput || runLock is null)
        {
            return null;
        }

        var acquisition = runLock.TryAcquire();
        if (acquisition is not RunLockResult.Acquired claim)
        {
            Report(new RunHistoryMaintenanceResult(0, 0, 0, 0, 0, true));
            return null;
        }

        using (claim.Release)
        {
            return UnderClaim(files, options, CancellationToken.None);
        }
    }

    internal static RunHistoryMaintenanceResult? UnderClaim(
        IRunRegistry registry, RunHistoryOptions options, CancellationToken cancellationToken)
    {
        if (registry is not FileRunRegistry files)
        {
            return null;
        }

        var result = files.MaintainHistory(options, DateTimeOffset.UtcNow, cancellationToken);
        Report(result);
        return result;
    }

    private static void Report(RunHistoryMaintenanceResult result) =>
        StructuredLog.Write(
            result.Incomplete ? LogLevel.Warning : LogLevel.Information,
            $"Run-history maintenance: examined {result.Examined}, reconciled {result.Reconciled}, "
            + $"pruned {result.Pruned}, skipped {result.Skipped}, mutations {result.Mutations}, "
            + $"incomplete {(result.Incomplete ? 1 : 0)}. Engine cleanup is unconfirmed.");
}

internal sealed record RunHistoryMaintenanceResult(
    int Examined, int Reconciled, int Pruned, int Skipped, int Mutations, bool Incomplete);
