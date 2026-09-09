namespace Vouchfx.Mcp.Prompts;

/// <summary>
/// Everything <see cref="PromptRepository"/> needs to know about one shipped prompt.
/// </summary>
/// <param name="Name">
/// The prompt name a host calls — and the ONLY thing declared here rather than parsed from the file.
/// It is repeated so this catalogue can be a compile-time list; <see cref="PromptRepository"/> asserts
/// it against the file's own <c>name</c> front-matter field at load, so the two cannot disagree.
/// </param>
/// <param name="FileName">The file's name under <c>src/Vouchfx.Mcp/Prompts/</c>.</param>
/// <param name="EmbeddedResourceName">
/// The exact manifest resource name embedded into this assembly (see <c>Vouchfx.Mcp.csproj</c>'s
/// <c>EmbeddedResource</c> items) — the ONLY place a prompt's content is ever read from.
/// </param>
public sealed record PromptDescriptor(string Name, string FileName, string EmbeddedResourceName);

/// <summary>
/// The fixed catalogue of MCP prompts this server advertises (spec §7) — the prompt analogue of
/// <see cref="Vouchfx.Mcp.Tools.ToolRegistry"/>'s tool list and
/// <see cref="Vouchfx.Mcp.Examples.ExampleSuites"/>' example list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and US-S5-03/US-S5-04 each extend it.</b> Sprint 5 ships four prompts —
/// <c>author_scenario</c> (this story), then <c>heal_run</c>, <c>review_spec</c> and
/// <c>explain_failure</c>. Each addition is one entry here, one <c>.md</c> file, and one csproj
/// item; <c>PromptCatalogueTests</c> fails loudly if those three fall out of step, and
/// <c>RealAuthorScenarioPromptMcpTests</c>' fail-closed set equality means a prompt cannot reach a
/// host without being catalogued.
/// </para>
/// <para>
/// <b>Content lives in the <c>.md</c>, not here.</b> This type carries only what is needed to FIND a
/// prompt; its name, title, description and arguments are all parsed from the file's front matter.
/// See <see cref="PromptDefinition"/>'s header for why that split is the whole design.
/// </para>
/// </remarks>
public static class PromptCatalogue
{
    /// <summary>The prefix every prompt's manifest resource name carries.</summary>
    private const string ResourceNamePrefix = "Vouchfx.Mcp.Prompts.";

    /// <summary>
    /// US-S5-02's authoring procedure: the method a host follows to write a passing suite using only
    /// this server's tools.
    /// </summary>
    public static readonly PromptDescriptor AuthorScenario = Describe("author_scenario");

    /// <summary>
    /// US-S5-03's healing procedure: diagnose an EnvironmentError or Inconclusive run and apply the
    /// smallest scoped fix — never acting on a Fail except to explain it.
    /// </summary>
    public static readonly PromptDescriptor HealRun = Describe("heal_run");

    /// <summary>US-S5-04's pre-flight review: a checklist over an existing suite, before it runs.</summary>
    public static readonly PromptDescriptor ReviewSpec = Describe("review_spec");

    /// <summary>US-S5-04's plain-language explanation of one step's outcome, for a newcomer.</summary>
    public static readonly PromptDescriptor ExplainFailure = Describe("explain_failure");

    /// <summary>Every prompt this server advertises, in the order it advertises them.</summary>
    public static IReadOnlyList<PromptDescriptor> All { get; } = [AuthorScenario, HealRun, ReviewSpec, ExplainFailure];

    /// <summary>
    /// Builds a descriptor from the prompt's name alone — file name and resource name are DERIVED,
    /// so a new prompt cannot be added with a mismatched trio by typing one of the three wrong.
    /// </summary>
    private static PromptDescriptor Describe(string name) =>
        new(name, $"{name}.md", $"{ResourceNamePrefix}{name}.md");
}
