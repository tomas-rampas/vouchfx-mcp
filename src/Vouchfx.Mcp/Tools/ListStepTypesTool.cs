using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// The <c>list_step_types</c> tool: lists every step type the pinned vouchfx engine supports,
/// grouped by family, from the live engine catalogue export (REQ-010).
/// </summary>
internal static class ListStepTypesTool
{
    public const string Name = "list_step_types";

    /// <remarks>
    /// The U5 sentence is APPENDED from <see cref="ProviderInfoContract.U5PendingNotice"/> rather
    /// than written out here (US-S2-05), so this description and <c>describe_step_type</c>' cannot
    /// disagree about which fields are pending, and neither can outlive the gate.
    /// </remarks>
    private static readonly string Description =
        "Lists every step type the pinned vouchfx engine supports, in dotted " +
        "'<family>.<provider>' form (e.g. 'http.rest', 'db-assert.postgres', 'mq-publish.kafka') " +
        "grouped by family, with each type's captureSupported flag, familyIntent one-liner " +
        "from the engine's live `vouchfx list --json` export, and requiredResources — the " +
        "dependency kinds a step of that type needs declared in environment.dependencies (an " +
        "empty list means none; the field is omitted entirely for a type this server cannot " +
        "derive it for). Takes no arguments. Requires the pinned vouchfx CLI on PATH (Spec A rich " +
        "catalogue). Call describe_step_type for the full required/optional field contract of any " +
        "one type this returns. " + ProviderInfoContract.U5PendingNotice;

    public static McpServerTool Create(LiveStepCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        async Task<CallToolResult> Handle(CancellationToken cancellationToken) =>
            await HandleAsync(catalogue, cancellationToken);

        return McpServerTool.Create(Handle, new McpServerToolCreateOptions
        {
            Name = Name,
            Description = Description,
            ReadOnly = true,
        });
    }

    private static async Task<CallToolResult> HandleAsync(
        LiveStepCatalogue catalogue,
        CancellationToken cancellationToken)
    {
        var load = await catalogue.GetOrLoadAsync(cancellationToken);
        if (load is StepCatalogueLoadResult.Failed failed)
        {
            // StepCatalogueLoadResult.Failed collapses several conditions into one case, and the
            // DOMINANT ones are all "the pinned engine CLI was unusable" — absent,
            // version-mismatched, unparseable, unlaunchable, or too thin to carry a Spec A rich
            // catalogue — which is why EngineCliUnavailable is the right label for it. It is not a
            // perfect fit for every one of them: LiveStepCatalogue's outermost catch-all also
            // produces Failed for genuinely internal/unexpected conditions, which in isolation
            // would belong in the 1900-1999 range. The union carries no discriminator to tell them
            // apart, so one code must answer for all of it, and the CLI-unavailable remediation
            // ("install the pinned CLI") is the one that helps in the overwhelming majority of
            // cases. Splitting this properly means splitting the outcome type first.
            return StructuredToolResult.Error(VfxCodeCatalogue.CreateError(
                VfxCodeCatalogue.EngineCliUnavailable, failed.Message));
        }

        var ok = (StepCatalogueLoadResult.Ok)load;
        var families = ok.StepTypes
            .GroupBy(t => t.Family, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StepFamilyGroup(
                g.Key,
                g.First().FamilyIntent,
                g.OrderBy(t => t.Type, StringComparer.Ordinal)
                    .Select(t => new StepTypeSummary(
                        t.Type,
                        t.Provider,
                        t.Description,
                        t.CaptureSupported,
                        t.FamilyIntent,
                        // US-S2-05: the same derivation describe_step_type attaches, so the cheap
                        // list and the expensive per-type lookup can never disagree about what a
                        // step type needs. 25 short arrays — a rounding error against the 32 KB
                        // effective payload budget (sprint-00-overview.md §4 risk 4).
                        RequiredResourceCatalogue.For(t.Type)))
                    .ToArray()))
            .ToArray();

        return StructuredToolResult.Success(new ListStepTypesResult(families));
    }
}

/// <summary>The <c>list_step_types</c> result contract: every step type, grouped by family.</summary>
internal sealed record ListStepTypesResult(IReadOnlyList<StepFamilyGroup> Families);

/// <summary>One step family and every type registered under it.</summary>
/// <param name="Family">Family id (e.g. <c>http</c>).</param>
/// <param name="FamilyIntent">Short intent one-liner shared by types in this family.</param>
/// <param name="Types">Types registered under this family.</param>
internal sealed record StepFamilyGroup(
    string Family,
    string FamilyIntent,
    IReadOnlyList<StepTypeSummary> Types);

/// <summary>A one-line summary of a single step type, as returned by <c>list_step_types</c>.</summary>
/// <param name="Type">The full dotted <c>family.provider</c> type name.</param>
/// <param name="Provider">The part after the dot.</param>
/// <param name="Description">The engine's one-line description of the type.</param>
/// <param name="CaptureSupported">Whether a <c>capture</c> block is allowed on steps of this type.</param>
/// <param name="FamilyIntent">The family's short intent one-liner.</param>
/// <param name="RequiredResources">
/// Spec §5.2's <c>requiredResources</c> (US-S2-05): dependency KINDS a step of this type needs
/// declared in <c>environment.dependencies</c>. Empty means "none, derived"; <see langword="null"/>
/// means "not derivable here" and is omitted from the wire — see
/// <see cref="RequiredResourceCatalogue"/>. Appended LAST so every property a host read before this
/// story kept its position as well as its name.
/// </param>
internal sealed record StepTypeSummary(
    string Type,
    string Provider,
    string? Description,
    bool CaptureSupported,
    string FamilyIntent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? RequiredResources);
