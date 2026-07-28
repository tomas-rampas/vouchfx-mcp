namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Documents the minimum engine export capability this MCP server requires for shape-level
/// catalogue tools (REQ-010 / EDGE-004 of engine-schema-and-catalogue-export / Spec A).
/// </summary>
/// <remarks>
/// <para>
/// Catalogue tools (<c>list_step_types</c>, <c>describe_step_type</c>) obtain their data from the
/// pinned engine's live <c>vouchfx list --json</c> export — never from a thin type-key-only list,
/// and never by silently degrading to field-less summaries. Engines that predate Spec A emit only
/// <c>type</c>/<c>family</c>/<c>provider</c>; those are rejected with a clear fail-fast message
/// rather than returned as if they were complete field metadata.
/// </para>
/// <para>
/// A specific NuGet version is intentionally <strong>not</strong> hard-coded here until Spec A is
/// published and <c>ENGINE_PIN</c> is advanced to it. Capability is described by the export surface
/// (rich <c>list --json</c> + <c>vouchfx schema</c>), not by a guessed pre-release number.
/// </para>
/// </remarks>
public static class EngineExportCapability
{
    /// <summary>
    /// Human-readable description of the minimum engine capability for shape-level catalogue
    /// tools — used in fail-fast tool errors and public docs.
    /// </summary>
    public const string MinimumRequirementDescription =
        "a vouchfx engine that includes Spec A (engine-schema-and-catalogue-export): "
        + "`vouchfx schema` and a rich `vouchfx list --json` catalogue with requiredFields, "
        + "optionalFields, captureSupported, and familyIntent on every step-type entry";

    /// <summary>
    /// Fail-fast message when the CLI is missing or cannot produce catalogue JSON at all.
    /// </summary>
    public static string CatalogueUnavailableMessage(string pinVersion) =>
        "Could not load the step-type catalogue from the pinned vouchfx CLI "
        + $"(ENGINE_PIN {TextSanitiser.SanitiseForDisplay(pinVersion)}). "
        + $"list_step_types / describe_step_type require {MinimumRequirementDescription}. "
        + "Install or upgrade the vouchfx global tool to the version this server is pinned to "
        + "(see ENGINE_PIN), ensure it is on PATH, and that `vouchfx list --json` succeeds.";

    /// <summary>
    /// Fail-fast message when the CLI returned JSON that is not a bar-B shape-level catalogue
    /// (EDGE-004: never silently return only type keys without field metadata).
    /// </summary>
    public static string ThinOrInvalidCatalogueMessage(string detail) =>
        "The installed vouchfx CLI returned a step catalogue without usable shape-level field "
        + "metadata. " + TextSanitiser.SanitiseForDisplay(detail) + " "
        + $"list_step_types / describe_step_type require {MinimumRequirementDescription}. "
        + "Upgrade vouchfx to a build that includes that export (and advance ENGINE_PIN once that "
        + "build is published); this server will not invent or silently omit field metadata.";
}
