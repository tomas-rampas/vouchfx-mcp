namespace Vouchfx.Mcp.Run;

/// <summary>
/// This server's own copy of the vouchfx engine's four-outcome verdict taxonomy (blueprint §12.1),
/// used to interpret the <c>verdict</c> field of parsed JSON Lines events and to compute the
/// aggregate suite verdict.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a SEPARATE type from the engine's own <c>Vouchfx.Engine.Abstractions.Verdict</c>,
/// not a shared reference to it: this server never references any engine assembly (see
/// <see cref="VouchfxCliSuiteRunner"/>'s remarks) — it consumes the engine only as an external
/// process producing a documented wire format, exactly like <see cref="Vouchfx.Mcp.Cli.IVouchfxCli"/>
/// consumes <c>vouchfx --version</c>'s stdout. The member NAMES are kept identical to the engine's
/// enum (<c>Pass</c>, <c>Fail</c>, <c>EnvironmentError</c>, <c>Inconclusive</c>) purely so this
/// type's own <see cref="object.ToString"/> reads naturally as the agent-facing verdict string in
/// <c>RunSuiteResult.Verdict</c> — that is a naming coincidence for readability, not a coupling.
/// </para>
/// <para>
/// <b>Elevation precedence (§12.1, mirrors the engine's <c>ScenarioRunner.VerdictPrecedence</c>
/// exactly):</b> <see cref="EnvironmentError"/> &gt; <see cref="Fail"/> &gt; <see cref="Inconclusive"/>
/// &gt; <see cref="Pass"/>. <see cref="Elevate"/> folds a sequence of per-scenario verdicts into one
/// aggregate suite verdict using this exact ordering, so a suite with even one <c>EnvironmentError</c>
/// scenario is never reported as anything else, and a suite with a <c>Fail</c> scenario is never
/// masked by an otherwise-passing majority.
/// </para>
/// </remarks>
public enum RunVerdict
{
    /// <summary>All assertions were met.</summary>
    Pass,

    /// <summary>One or more assertions failed. The only outcome that should break CI by default.</summary>
    Fail,

    /// <summary>
    /// Infrastructure failure (container unhealthy, image pull, seed, tunnel, Docker unavailable).
    /// The system under test was not exercised. Never conflated with <see cref="Fail"/>.
    /// </summary>
    EnvironmentError,

    /// <summary>
    /// The engine could not determine correctness (timeout, partition grace period exceeded,
    /// upstream capture unmet). Also used for this server's own cancellation/timeout outcomes
    /// (EDGE-002) — a run this server itself gave up waiting on is inconclusive, never a failure.
    /// </summary>
    Inconclusive,
}

/// <summary>Wire-token parsing and precedence-based elevation for <see cref="RunVerdict"/>.</summary>
public static class RunVerdictExtensions
{
    /// <summary>
    /// Parses a verdict wire token exactly as <c>VerdictJsonConverter</c> emits it in the engine's
    /// event stream: <c>"PASS"</c>, <c>"FAIL"</c>, <c>"ENV_ERROR"</c>, <c>"INCONCLUSIVE"</c>.
    /// </summary>
    /// <returns>
    /// The parsed verdict, or <see langword="null"/> for anything else — an absent field, an empty
    /// string, or a token this server does not recognise. Never throws: the event stream's
    /// forward-compatibility contract (§14) means an unrecognised value must be tolerated, not
    /// treated as a parse failure.
    /// </returns>
    public static RunVerdict? ParseWireToken(string? token) => token switch
    {
        "PASS" => RunVerdict.Pass,
        "FAIL" => RunVerdict.Fail,
        "ENV_ERROR" => RunVerdict.EnvironmentError,
        "INCONCLUSIVE" => RunVerdict.Inconclusive,
        _ => null,
    };

    /// <summary>
    /// Folds <paramref name="next"/> into <paramref name="current"/> using the §12.1 elevation
    /// precedence (<see cref="RunVerdict"/>'s remarks), returning whichever ranks higher.
    /// </summary>
    public static RunVerdict Elevate(RunVerdict current, RunVerdict next) =>
        Rank(next) > Rank(current) ? next : current;

    private static int Rank(RunVerdict verdict) => verdict switch
    {
        RunVerdict.Pass => 0,
        RunVerdict.Inconclusive => 1,
        RunVerdict.Fail => 2,
        RunVerdict.EnvironmentError => 3,
        _ => 0,
    };
}
