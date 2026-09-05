using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// VFX-D-1208 — two or more steps declare the same <c>id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not expressible as a schema keyword, which is why it is a rule.</b> The composed schema
/// constrains an id's SHAPE (<c>^[A-Za-z_][A-Za-z0-9_-]*$</c>) but JSON Schema has no
/// "unique by property" keyword for an array of objects — <c>uniqueItems</c> compares whole
/// elements, so two steps sharing an id while differing anywhere else satisfy it.
/// </para>
/// <para>
/// <b>Reported at the DUPLICATE, never at the first declaration.</b> The first occurrence is
/// legitimate; the second is what the author has to rename. Pointing at both would double every
/// finding and make the fix ambiguous.
/// </para>
/// </remarks>
internal sealed class DuplicateStepIdRule : ISemanticRule
{
    /// <inheritdoc/>
    public string Code => VfxCodeCatalogue.DuplicateStepId;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<Diagnostic>();

        // Ordinal, matching how the engine identifies a step in its own reporting: two ids
        // differing only in case are two steps, and saying otherwise would be a wrong finding.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (index, step) in SuiteDocument.Steps(context.Document))
        {
            if (SuiteDocument.StringProperty(step, "id") is not { } id || seen.Add(id))
            {
                continue;
            }

            findings.Add(SemanticFinding.Create(
                context,
                Code,
                SemanticFinding.Warning,
                $"Step id {SemanticFinding.Identifier(id)} is already used by an earlier step. Ids "
                + "identify steps in reporting and failure messages, so give this one a distinct name.",
                SuitePath.Step(index).Property("id")));
        }

        return findings;
    }
}
