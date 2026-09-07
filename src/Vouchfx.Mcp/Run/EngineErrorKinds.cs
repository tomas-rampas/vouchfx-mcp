using System.Collections.Frozen;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// The engine's own <c>OrchestrationErrorKind</c> names, as they appear on an
/// <c>environment-error</c> event's <c>errorKind</c> field — declared ONCE, for every consumer in
/// this server that has to recognise one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Two independent taxonomies over the same engine strings had grown
/// up — <c>Diagnosis.VerdictReasonClassifier</c>'s pull/unhealthy/seed sets, which map a kind to a
/// machine-branchable <c>reason.kind</c>, and
/// <c>RunSuiteOrchestrator.BuildRemediationHintFromEnvironmentErrors</c>, which maps the same
/// strings to remediation PROSE. They overlapped without agreeing, each carried a comment pointing
/// at the other, and a new engine kind had to be added in two places or one surface silently
/// degraded to its default. Both now spell their kinds from here.
/// </para>
/// <para>
/// <b>The consumers' SUBSETS stay deliberately different, and that is not a defect.</b> They answer
/// different questions, so they recognise different things: <see cref="Discovery"/> matters only to
/// the remediation prose (there is no <c>reason.kind</c> for it), while
/// <see cref="Unhealthy"/>/<see cref="WaitFor"/>/<see cref="Seed"/> matter only to the classifier
/// (the prose folds them into its default). What this type removes is the DUPLICATION of the
/// strings, not the divergence of the judgements — each consumer still states which subset it
/// recognises, in its own terms, at its own call site.
/// </para>
/// <para>
/// <b>Adding a kind is now a one-place edit, enforced two ways.</b> The names are <c>const</c>, so a
/// consumer naming one that does not exist here fails to COMPILE; and
/// <c>Run.EngineErrorKindsTests</c> asserts no consumer spells a kind as a bare string literal,
/// which is the only way the compiler could be bypassed. <see cref="All"/> is likewise asserted to
/// contain every constant declared below.
/// </para>
/// </remarks>
internal static class EngineErrorKinds
{
    /// <summary>A required container image could not be pulled — wrong tag, or missing registry credentials.</summary>
    public const string ImagePull = "ImagePull";

    /// <summary>A resource did not pass its health gate within the configured window.</summary>
    public const string HealthGate = "HealthGate";

    /// <summary>A resource came up but reported itself unhealthy.</summary>
    public const string Unhealthy = "Unhealthy";

    /// <summary>A declared <c>waitFor</c> dependency never became ready.</summary>
    public const string WaitFor = "WaitFor";

    /// <summary>Seeding a dependency failed before the suite could exercise anything.</summary>
    public const string Seed = "Seed";

    /// <summary>An endpoint could not be resolved — recognised by <c>run_suite</c>'s remediation prose only.</summary>
    public const string Discovery = "Discovery";

    /// <summary>
    /// A resource could not be provisioned at all. Recognised by NEITHER consumer's specific branch —
    /// it is each one's DEFAULT — and named here because it is a real engine kind that appears in
    /// this repo's own fixtures, so a reader looking for it finds it declared rather than absent.
    /// </summary>
    public const string Provision = "Provision";

    /// <summary>Every kind above.</summary>
    /// <remarks>
    /// <para>
    /// A <see cref="FrozenSet{T}"/> per this repo's convention for closed vocabularies (see
    /// <c>Validation.DependencyKinds.All</c> and <c>Diagnosis.SpecEditScopes.All</c>): built once,
    /// read many, and not downcastable to a mutable set. Ordinal-INSENSITIVE, matching the
    /// classifier's own long-standing comparison — the engine writes PascalCase and tolerating a
    /// casing change costs nothing and fabricates nothing.
    /// </para>
    /// <para>
    /// <b>That case-insensitivity is the CLASSIFIER's, and the two consumers differ on it.</b>
    /// <c>RunSuiteOrchestrator</c>'s remediation mapping is a C# <c>switch</c> on the raw string, so
    /// it matches case-SENSITIVELY — a lower-cased <c>"imagepull"</c> would classify but would fall
    /// to the remediation default. That asymmetry predates this file and is unchanged by hoisting the
    /// vocabulary: this set is not used by the switch, which spells the constants as case labels.
    /// Recorded rather than silently harmonised, because changing either side is a behaviour change
    /// that deserves its own decision.
    /// </para>
    /// </remarks>
    public static FrozenSet<string> All { get; } = new[]
    {
        ImagePull, HealthGate, Unhealthy, WaitFor, Seed, Discovery, Provision,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
