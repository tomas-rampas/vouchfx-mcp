namespace Vouchfx.Mcp.Scaffold;

/// <summary>One step in a <c>scaffold_suite</c> intent (structured only — never free text).</summary>
/// <param name="Id">Unique step id within the suite.</param>
/// <param name="Type">Dotted <c>family.provider</c> key that must exist in the live engine registry.</param>
/// <param name="Label">Optional short human label (engine emits as a YAML comment only).</param>
public sealed record ScaffoldStepRequest(string Id, string Type, string? Label = null);

/// <summary>A service outline entry for scaffolded <c>environment.services</c>.</summary>
/// <param name="Name">Logical service name (map key).</param>
/// <param name="Image">OCI image reference; engine defaults when null/empty.</param>
public sealed record ScaffoldServiceRequest(string Name, string? Image = null);

/// <summary>A dependency outline entry for scaffolded <c>environment.dependencies</c>.</summary>
/// <param name="Name">Logical dependency name (map key).</param>
/// <param name="Type">Dependency kind (e.g. <c>postgres</c>, <c>kafka</c>).</param>
public sealed record ScaffoldDependencyRequest(string Name, string Type);

/// <summary>Successful <c>scaffold_suite</c> payload: the machine-drafted YAML skeleton.</summary>
/// <param name="Yaml">Full <c>.e2e.yaml</c> document text from the pinned engine CLI.</param>
public sealed record ScaffoldSuiteResult(string Yaml);

/// <summary>Neutral outcome of <see cref="ScaffoldSuiteOrchestrator.ScaffoldAsync"/>.</summary>
public abstract record ScaffoldSuiteOutcome
{
    private ScaffoldSuiteOutcome()
    {
    }

    /// <summary>CLI produced a YAML skeleton.</summary>
    public sealed record Completed(ScaffoldSuiteResult Result) : ScaffoldSuiteOutcome;

    /// <summary>Caller args failed local validation before any CLI spawn.</summary>
    public sealed record InvalidArgument(string Message) : ScaffoldSuiteOutcome;

    /// <summary>Pinned CLI missing, mismatched, unparseable, or not launchable.</summary>
    public sealed record CliUnavailable(string Message) : ScaffoldSuiteOutcome;

    /// <summary>
    /// CLI ran and exited non-zero (unknown type, empty steps, unknown dependency kind, …).
    /// Message is built from the engine's stderr (sanitised).
    /// </summary>
    public sealed record ScaffoldFailed(string Message) : ScaffoldSuiteOutcome;
}
