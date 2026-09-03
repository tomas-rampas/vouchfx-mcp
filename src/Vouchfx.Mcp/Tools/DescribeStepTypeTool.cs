using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>describe_step_type</c> tool: describes one step type's full field-level contract from
/// the live engine catalogue export (REQ-010).
/// </summary>
internal static class DescribeStepTypeTool
{
    public const string Name = "describe_step_type";

    private const string Description =
        "Describes one vouchfx step type's full contract from the pinned engine's live catalogue " +
        "export: requiredFields, optionalFields, captureSupported, familyIntent, and a fields " +
        "array derived from those lists. Give it the dotted '<family>.<provider>' type name " +
        "(e.g. 'mq-publish.kafka') exactly as list_step_types reports it. Requires the pinned " +
        "vouchfx CLI on PATH with Spec A rich `list --json`. An unknown type returns a tool " +
        "error listing every valid type rather than crashing.";

    public static McpServerTool Create(LiveStepCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        async Task<CallToolResult> Handle(
            [Description("The dotted '<family>.<provider>' step type to describe, e.g. 'db-assert.postgres'.")]
            string type,
            CancellationToken cancellationToken) =>
            await HandleAsync(catalogue, type, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        LiveStepCatalogue catalogue,
        string type,
        CancellationToken cancellationToken)
    {
        var (load, info) = await catalogue.FindAsync(type, cancellationToken);
        if (load is StepCatalogueLoadResult.Failed failed)
        {
            // Same code, same reasoning as list_step_types' identical branch.
            return StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.EngineCliUnavailable, failed.Message));
        }

        var ok = (StepCatalogueLoadResult.Ok)load;
        if (info is null)
        {
            var knownTypes = string.Join(", ", ok.StepTypes.Select(t => t.Type));
            // Deliberately StepTypeNotInCatalogue (VFX-E-1250) rather than UnknownStepType
            // (VFX-D-1201), despite both describing an unrecognised step type. VFX-D-1201 is a
            // FINDING about a suite that mentions a bad type; this is a CALL that cannot be
            // performed, because there is no such type to describe. Reusing the D code here would
            // put a diagnostic on an isError result and break the rule the catalogue rests on.
            //
            // The type argument is caller-supplied (M1): sanitised before it is spliced into the
            // error message.
            return StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.StepTypeNotInCatalogue,
                $"Unknown step type '{TextSanitiser.SanitiseForDisplay(type)}'. Known types: {knownTypes}."));
        }

        return StructuredToolResult.Success(info);
    }
}
