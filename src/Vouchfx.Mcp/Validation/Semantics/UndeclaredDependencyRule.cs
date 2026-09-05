using System.Collections.Frozen;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1205 — a step type needs a dependency KIND that <c>environment.dependencies</c> never
/// declares (spec §5.5's own example: <c>mq-expect.kafka</c> without a <c>kafka</c> dependency).
/// </summary>
/// <remarks>
/// <para>
/// <b>Kind, not name.</b> <c>environment.dependencies</c> is keyed by the author's logical name
/// (<c>broker</c>) and each entry's <c>type</c> is the kind (<c>kafka</c>).
/// <see cref="SuiteFacts.Dependencies"/> carries the NAMES — which is what VFX-D-1202 resolves a
/// <c>target</c> against — so this rule reads the kinds out of the already-parsed document via
/// <see cref="DependencyKinds.DeclaredIn"/>. That is the sanctioned use of
/// <see cref="SemanticAnalysisContext.Document"/>: the fact set does not carry this shape, and
/// walking one small object is not a second parse.
/// </para>
/// <para>
/// <b>A declared SERVICE target suppresses the finding, and that is not a loophole.</b> The composed
/// schema's own <c>target</c> description for a broker step says a service is a legitimate,
/// reachable form of the same thing ("a customer-supplied broker under its own entrypoint/config").
/// Reporting a missing <c>kafka</c> dependency for a step pointed at a declared service would be a
/// wrong finding on a valid suite. A step pointed at an UNDECLARED name is VFX-D-1202's finding and
/// gets this one too — two true statements about one mistake, in one channel, filterable by code.
/// </para>
/// </remarks>
internal sealed class UndeclaredDependencyRule : ISemanticRule
{
    /// <summary>
    /// Which dependency kind each step type needs, for the step types that need one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written out rather than derived from the provider name, because the derivation is not
    /// total.</b> For seventeen of the nineteen entries the provider IS the kind
    /// (<c>db-assert.postgres</c> → <c>postgres</c>, <c>mq-publish.kafka</c> → <c>kafka</c>), and a
    /// rule that just matched the provider against
    /// <see cref="DependencyKinds.All"/> would get those right. It would also silently drop the two
    /// where the DSL's provider vocabulary and its dependency vocabulary diverge:
    /// <c>mail-expect.smtp</c> needs a <c>mailpit</c> dependency (the provider names the PROTOCOL),
    /// and <c>storage-assert.s3</c> needs a <c>minio</c> one (the provider names the API). A silent
    /// gap in a table is worse than a table, so the table is explicit and
    /// <c>UndeclaredDependencyRuleTests</c> gates both halves of it against the vendored schema.
    /// </para>
    /// <para>
    /// <b>Absent by design</b> — six of the catalogue's twenty-five types: <c>http.rest</c>,
    /// <c>http.soap</c> and <c>webhook-listen.http</c> (they target services),
    /// <c>script.csharp</c> (no infrastructure at all), <c>metrics-assert.prometheus</c> and
    /// <c>trace-expect.otlp</c> (their backends are not dependency kinds the schema's enum knows).
    /// Adding a guess for any of them would be a fabricated requirement, which is the one thing
    /// sprint-00 §3's stances forbid outright. <c>UndeclaredDependencyRuleTests</c> gates that
    /// partition in BOTH directions, so a step type added by an <c>ENGINE_PIN</c> bump cannot land
    /// in neither set unnoticed.
    /// </para>
    /// <para>
    /// <b>Two readers now (US-S2-05).</b> Besides this rule,
    /// <see cref="Vouchfx.Mcp.Validation.RequiredResourceCatalogue"/> reads these same rows and
    /// publishes them as spec §5.2's <c>requiredResources</c> on BOTH catalogue tools
    /// (<c>list_step_types</c>, <c>describe_step_type</c>) — so an edit to this table changes what
    /// those tools advertise a step type needs, not merely what VFX-D-1205 fires on. See that type's
    /// remarks for the single-source coupling.
    /// </para>
    /// </remarks>
    public static FrozenDictionary<string, string> RequiredDependencyKinds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache-assert.elasticsearch"] = "elasticsearch",
            ["cache-assert.redis"] = "redis",
            ["db-assert.dynamodb"] = "dynamodb",
            ["db-assert.mongodb"] = "mongodb",
            ["db-assert.mysql"] = "mysql",
            ["db-assert.postgres"] = "postgres",
            ["db-assert.sqlserver"] = "sqlserver",
            ["mail-expect.smtp"] = "mailpit",
            ["mq-expect.azureservicebus"] = "azureservicebus",
            ["mq-expect.kafka"] = "kafka",
            ["mq-expect.nats"] = "nats",
            ["mq-expect.rabbitmq"] = "rabbitmq",
            ["mq-expect.redis"] = "redis",
            ["mq-publish.azureservicebus"] = "azureservicebus",
            ["mq-publish.kafka"] = "kafka",
            ["mq-publish.nats"] = "nats",
            ["mq-publish.rabbitmq"] = "rabbitmq",
            ["mq-publish.redis"] = "redis",
            ["storage-assert.s3"] = "minio",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.UndeclaredDependencyType;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();
        var declaredKinds = DependencyKinds.DeclaredIn(context.Document);

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            if (SuiteDocument.StringProperty(step, "type") is not { } type ||
                !RequiredDependencyKinds.TryGetValue(type, out var kind) ||
                declaredKinds.Contains(kind))
            {
                continue;
            }

            // A declared SERVICE is a legitimate provider of the same infrastructure — see the
            // class remarks. Read from the fact set, which is the set-membership authority.
            if (SuiteDocument.StringProperty(step, "target") is { } target &&
                context.Facts.Services.Contains(target))
            {
                continue;
            }

            findings.Add(SemanticFinding.Create(
                context,
                Code,
                SemanticFinding.Warning,
                $"Step type {SemanticFinding.Identifier(type)} needs a "
                + $"{SemanticFinding.Identifier(kind)} dependency, but environment.dependencies "
                + "declares none of that kind (and the step's target is not a declared service "
                + "either). Add the dependency, or point the step at a service that provides it.",
                SuitePath.Step(index).Property("type")));
        }

        return findings;
    }
}
