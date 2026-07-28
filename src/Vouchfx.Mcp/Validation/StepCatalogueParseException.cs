namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Thrown when <c>vouchfx list --json</c> output cannot be accepted as a complete bar-B
/// shape-level catalogue (REQ-010 / EDGE-004).
/// </summary>
public sealed class StepCatalogueParseException : InvalidOperationException
{
    public StepCatalogueParseException(string message)
        : base(message)
    {
    }

    public StepCatalogueParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
