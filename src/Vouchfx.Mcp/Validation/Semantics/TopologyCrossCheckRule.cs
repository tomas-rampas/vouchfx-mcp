using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// The workspace's extracted contract surface — the topics, HTTP paths and tables the code under
/// test actually publishes, serves and writes — as <see cref="TopologyCrossCheckRule"/> needs it.
/// </summary>
/// <remarks>
/// <b>Nothing in <c>src/</c> constructs one, and that is the U1 gate made structural.</b> The only
/// source of this data is <c>vouchfx topology --json</c>, upstream ask U1, which is outstanding
/// (<c>specs/sprints/sprint-00-overview.md</c> §3). Modelling it as a required constructor argument
/// rather than as an optional feature flag is what makes "shipped disabled" a property of the type
/// system instead of a configuration value someone could flip: there is no value to flip, and the
/// rule is not registered.
/// </remarks>
/// <param name="Names">
/// Every topic, path, or table the extracted topology knows about. Ordinal comparison, matching how
/// the engine matches a contract name.
/// </param>
internal sealed record SuiteTopology(IReadOnlySet<string> Names)
{
    /// <summary>Builds a topology from a name sequence.</summary>
    public SuiteTopology(IEnumerable<string> names)
        : this(new HashSet<string>(names, StringComparer.Ordinal))
    {
    }
}

/// <summary>
/// VFX-D-1210 — a step names a topic, path, or table that appears in no extracted contract.
/// <b>Implemented, catalogued, and NOT registered: it never fires in this sprint.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate, precisely.</b> This rule is absent from <see cref="SemanticAnalyser.Rules"/>, so
/// <c>validate_suite</c> at any level — <c>schema</c>, <c>semantic</c>, or <c>full</c> — cannot
/// emit VFX-D-1210. There is no configuration flag, environment variable, or tool argument that
/// changes that: the only way to run this rule is to construct it with a
/// <see cref="SuiteTopology"/>, and the only producer of one would be upstream ask U1
/// (<c>vouchfx topology [--sources …] [--json]</c>), which has not landed. Registering the rule and
/// wiring a topology source is a single change in a later sprint, and it is ADDITIVE: the code, its
/// catalogue entry, and its <c>docs/errors/VFX-D-1210.md</c> page all exist today, so nothing about
/// the wire shape or the code numbering moves when findings start arriving.
/// </para>
/// <para>
/// <b>Why the body is real rather than a stub returning <c>[]</c>.</b> A stub would make the
/// story's "implemented but disabled" acceptance criterion a matter of trust, and would leave the
/// actual cross-check to be designed under time pressure in the sprint that turns it on.
/// <c>TopologyCrossCheckRuleTests</c> drives this body with a hand-built topology and asserts the
/// finding it produces — so what ships disabled is a tested rule, not a placeholder.
/// </para>
/// <para>
/// <b>What it checks, and the deliberate narrowness of it.</b> Only the fields whose values ARE
/// contract names: a step's <c>topic</c> (broker steps) and its <c>table</c> (store assertions). An
/// HTTP <c>path</c> is excluded despite spec §5.5 naming paths, because a suite's path routinely
/// carries interpolation (<c>/orders/{orderId}</c>) and matching a templated path against an
/// extracted route needs the route-pattern matching U1's own output shape will define — guessing at
/// it now would bake in a mismatch. A value carrying a placeholder or a reference is skipped for the
/// same reason: this server does not resolve either, so it cannot know what the name will be.
/// </para>
/// </remarks>
internal sealed class TopologyCrossCheckRule : ISemanticRule
{
    private readonly SuiteTopology? _topology;

    /// <summary>
    /// Builds the rule against <paramref name="topology"/>, or — with <see langword="null"/> — in
    /// the state it can only be in today: unable to report anything.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is not a degraded mode to be filled in with a default. Per
    /// sprint-00 §3's gated-feature stances, a verdict derived from a topology this server cannot
    /// see would be a fabricated value for the missing portion; silence is the honest shape.
    /// </remarks>
    public TopologyCrossCheckRule(SuiteTopology? topology) => _topology = topology;

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.TopologyCrossCheck;

    /// <summary>The step fields whose values are contract names — see the class remarks.</summary>
    private static readonly string[] ContractNameFields = ["topic", "table"];

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // THE GATE. Pre-U1 there is no topology, so there is nothing this rule can honestly say.
        if (_topology is null)
        {
            return [];
        }

        var findings = new List<Diagnostic>();

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            foreach (var field in ContractNameFields)
            {
                if (SuiteDocument.StringProperty(step, field) is not { } name ||
                    _topology.Names.Contains(name))
                {
                    continue;
                }

                // An interpolated or referenced value names something this server cannot resolve,
                // so "it is not in the topology" would be a statement about a string rather than
                // about a contract.
                if (name.Contains('{', StringComparison.Ordinal))
                {
                    continue;
                }

                findings.Add(SemanticFinding.Create(
                    context,
                    Code,
                    SemanticFinding.Warning,
                    $"No extracted contract in this workspace names {SemanticFinding.Identifier(name)}. "
                    + "Either the producer is outside the analysed sources, or the name is a typo.",
                    SuitePath.Step(index).Property(field)));
            }
        }

        return findings;
    }
}
