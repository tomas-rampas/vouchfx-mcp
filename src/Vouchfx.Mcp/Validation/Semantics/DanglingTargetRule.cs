using System.Text.Json;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1202 — a step's <c>target</c> names no service and no dependency the suite's
/// <c>environment</c> declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership is tested against <see cref="SemanticAnalysisContext.Facts"/>, never against
/// <see cref="SemanticAnalysisContext.Summary"/>.</b> The digest caps every list at 1 000 entries
/// and drops any name containing <c>${</c>, so deciding "this target is undeclared" from it would
/// report a valid suite as broken twice over — once for a service named after a secret reference,
/// once for every name past the thousandth. That is the exact false negative the seam's own remarks
/// single out.
/// </para>
/// <para>
/// <b>Services AND dependencies, because the schema says both are legitimate.</b> The composed
/// schema's own <c>target</c> description for <c>mq-expect.kafka</c> spells it out — a declared
/// dependency of the right kind, or a declared service that advertises a reachable address ("a
/// customer-supplied broker under its own entrypoint/config"). Testing only <c>services</c>, the
/// shape a first draft reaches for, would fire on every dependency-targeted step in the corpus.
/// </para>
/// </remarks>
internal sealed class DanglingTargetRule : ISemanticRule
{
    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.DanglingTargetReference;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            if (!step.TryGetProperty("target", out var target) ||
                target.ValueKind != JsonValueKind.String ||
                target.GetString() is not { Length: > 0 } name)
            {
                // No target, or a target the SCHEMA pass will reject for its shape. Either way this
                // rule has nothing to say — "treat a shape you did not expect as nothing to say".
                continue;
            }

            if (context.Facts.Services.Contains(name) || context.Facts.Dependencies.Contains(name))
            {
                continue;
            }

            findings.Add(SemanticFinding.Create(
                context,
                Code,
                SemanticFinding.Warning,
                $"Step target {SemanticFinding.Identifier(name)} names no service under "
                + "environment.services and no dependency under environment.dependencies. Declare "
                + "it, or point the step at a name the environment already declares.",
                SuitePath.Step(index).Property("target")));
        }

        return findings;
    }
}
