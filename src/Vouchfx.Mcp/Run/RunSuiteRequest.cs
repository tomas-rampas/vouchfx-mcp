namespace Vouchfx.Mcp.Run;

/// <summary>
/// One <c>run_suite</c> call's arguments, exactly as the caller sent them (US-S3-02) — the shape
/// <see cref="RunSuiteOrchestrator.RunAsync(RunSuiteRequest, Action{string}, CancellationToken)"/>
/// takes instead of a positional parameter list that had already reached five and would have reached
/// nine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is validated, defaulted, or normalised.</b> Every property is nullable and means
/// "the caller did not send this" when it is <see langword="null"/> — never "the caller sent the
/// default". The distinction is load-bearing in two places: <see cref="Path"/> versus
/// <see cref="Paths"/> is an exactly-one-of rule that can only be checked if "absent" and "empty"
/// are distinguishable, and <see cref="Wait"/>/<see cref="KeepEnvironment"/> are refused only for
/// the value that selects the unimplemented behaviour, so an explicit <c>wait: true</c> must be
/// distinguishable from an omitted one for the refusal to be about what the caller actually asked
/// for.
/// </para>
/// <para>
/// <b>Deliberately a property-initialiser record, not a positional one.</b> A caller writes
/// <c>new RunSuiteRequest { Path = path }</c>, so adding an argument later cannot silently reorder
/// anyone's existing call — which is precisely the failure a nine-parameter positional record
/// invites when several of its parameters share a type.
/// </para>
/// </remarks>
public sealed record RunSuiteRequest
{
    /// <summary>
    /// The legacy single-suite input: an absolute or workspace-relative path to one
    /// <c>.e2e.yaml</c> file. Mutually exclusive with <see cref="Paths"/> (<c>VFX-E-1503</c>).
    /// <b>Glob syntax is NOT expanded here</b> — see <see cref="SuitePathExpander"/> for why the old
    /// input's meaning is held fixed.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// US-S3-02's input: one or more absolute/workspace-relative paths, each of which may instead be
    /// a workspace-relative glob (<c>e2e/checkout/**</c>) that expands to the <c>*.e2e.yaml</c> files
    /// it selects. Mutually exclusive with <see cref="Path"/> (<c>VFX-E-1503</c>).
    /// </summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>Zero or more tag filters, applied to every suite in the run; omitted runs them whole.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// The WHOLE call's wall-clock budget in seconds, not a per-suite one — see
    /// <c>ExecuteRegisteredRunAsync</c>'s remarks. Omitted means
    /// <see cref="RunSuiteOrchestrator.DefaultTimeoutSeconds"/>.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Free-form host metadata recorded into the run's registry entry (spec §5.7), bounded by
    /// <see cref="RunSuiteOrchestrator.MaxLabelCount"/> and its sibling limits.
    /// <b>Registry-side only in this build</b>: spec §5.7 also describes labels appearing in the JSON
    /// Lines run envelope, and that half is not implementable here — every event in that stream is
    /// AUTHORED by the engine (this server only ever appends engine-produced bytes to it, when a
    /// multi-suite run merges its per-suite parts), and the pinned CLI has no labels flag through
    /// which to ask the engine to write them. The half that is implementable ships; the half that is
    /// not is not faked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// Whether to leave the environment up after the run. <see langword="false"/> (or omitted) is the
    /// implemented behaviour; <see langword="true"/> is refused with <c>VFX-E-1504</c> —
    /// <c>sprint-00-overview.md</c> §3's stance (a) — because the pinned CLI exposes no such flag and
    /// this server will not implement a competing teardown policy of its own.
    /// </summary>
    public bool? KeepEnvironment { get; init; }

    /// <summary>
    /// Whether to block until the run finishes. <see langword="true"/> (or omitted) is the
    /// implemented behaviour; <see langword="false"/> is refused with <c>VFX-E-1504</c> naming
    /// upstream ask U4, rather than silently blocking or being rejected as an unknown field.
    /// </summary>
    public bool? Wait { get; init; }
}
