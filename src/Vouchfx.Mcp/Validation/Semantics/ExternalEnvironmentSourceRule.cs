using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>Reports the external configuration that CLI-free analysis deliberately leaves unresolved.</summary>
internal sealed class ExternalEnvironmentSourceRule : ISemanticRule
{
    public string Code => VfxCodeCatalogue.ExternalEnvironmentSourceUnresolved;

    public IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Facts.HasExternalEnvironmentSource)
        {
            return [];
        }

        // Fixed, bounded text: never echo driver/config values or inspect their contents. This advice
        // does not assert schema acceptance, driver registration, valid bindings or runtime readiness.
        return
        [
            SemanticFinding.Create(
                context,
                Code,
                SemanticFinding.Info,
                "CLI-free validation does not read external driver configuration or resolve its logical "
                + "service/dependency bindings. Schema acceptance does not validate that configuration "
                + "or runtime readiness. Use normal engine validation on the original suite path with a "
                + "compatible engine. Inline YAML has no filesystem base; save it beside its configuration first.",
                SuitePath.Root.Property("environment").Property("source")),
        ];
    }
}
