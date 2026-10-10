using System.Globalization;

namespace Vouchfx.Mcp.Run;

/// <summary>Validated startup policy for server-owned terminal evidence, not engine resources.</summary>
public sealed record RunHistoryOptions
{
    public const int DefaultMaxTerminalRuns = 1000;
    public const int DefaultMaxAgeDays = 30;
    public const int MaximumTerminalRuns = 100_000;
    public const int MaximumAgeDays = 36_500;

    public int MaxTerminalRuns { get; }
    public int MaxAgeDays { get; }
    public bool KeepHistory { get; }

    public RunHistoryOptions(
        int maxTerminalRuns = DefaultMaxTerminalRuns,
        int maxAgeDays = DefaultMaxAgeDays,
        bool keepHistory = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTerminalRuns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTerminalRuns, MaximumTerminalRuns);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAgeDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAgeDays, MaximumAgeDays);
        MaxTerminalRuns = maxTerminalRuns;
        MaxAgeDays = maxAgeDays;
        KeepHistory = keepHistory;
    }

    internal static bool TryParseCommandLine(
        string[] args, out RunHistoryOptions? options, out string? error)
    {
        options = null;
        error = null;
        var maxRuns = DefaultMaxTerminalRuns;
        var maxAge = DefaultMaxAgeDays;
        var keep = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            var separator = argument.IndexOf('=');
            var flag = separator < 0 ? argument : argument[..separator];
            if (flag is not ("--run-history-max-terminal-runs" or "--run-history-max-age-days" or "--keep-run-history"))
            {
                if (flag.StartsWith("--run-hist", StringComparison.OrdinalIgnoreCase)
                    || flag.StartsWith("--keep-run-hist", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Unknown run-history setting; use --run-history-max-terminal-runs, --run-history-max-age-days or --keep-run-history.";
                    return false;
                }

                continue;
            }

            if (!seen.Add(flag))
            {
                error = "A run-history setting was repeated; supply each setting once.";
                return false;
            }

            if (flag == "--keep-run-history")
            {
                if (separator >= 0)
                {
                    error = "--keep-run-history takes no value.";
                    return false;
                }

                keep = true;
                continue;
            }

            var value = separator >= 0 ? argument[(separator + 1)..] : ++i < args.Length ? args[i] : null;
            var maximum = flag == "--run-history-max-terminal-runs" ? MaximumTerminalRuns : MaximumAgeDays;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1 || parsed > maximum)
            {
                error = flag == "--run-history-max-terminal-runs"
                    ? "--run-history-max-terminal-runs requires an integer from 1 to 100000."
                    : "--run-history-max-age-days requires an integer from 1 to 36500.";
                return false;
            }

            if (flag == "--run-history-max-terminal-runs")
            {
                maxRuns = parsed;
            }
            else
            {
                maxAge = parsed;
            }
        }

        if (keep && seen.Count > 1)
        {
            error = "--keep-run-history cannot be combined with pruning limits.";
            return false;
        }

        options = new RunHistoryOptions(maxRuns, maxAge, keep);
        return true;
    }
}
