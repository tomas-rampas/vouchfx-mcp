using System.Text.Json;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// The wire contract shared between the <c>validate_suite</c> orchestrator
/// (<see cref="ValidationWorkerClient"/>) and the hidden <c>--validate-worker &lt;path&gt;</c>
/// child-process mode it spawns (see <c>Program.cs</c>'s worker-mode branch): a
/// <see cref="ValidateSuiteResult"/> serialised as JSON on the worker's stdout — nothing else,
/// ever.
/// </summary>
/// <remarks>
/// Kept as one small, shared type rather than letting each side build its own
/// <see cref="JsonSerializerOptions"/> independently: the worker (in <c>Program.cs</c>) and the
/// orchestrator (<see cref="ValidationWorkerClient"/>) must serialise/deserialise with byte-for-byte
/// compatible settings, and a future change to one side without the other would otherwise be a
/// silent wire-format mismatch rather than a compile error.
/// </remarks>
public static class ValidationWorkerProtocol
{
    /// <summary>
    /// The command-line argument that switches the vouchfx-mcp executable into its hidden,
    /// one-shot worker mode: <c>--validate-worker &lt;path&gt;</c>. Checked first in
    /// <c>Program.cs</c>, before the ENGINE_PIN load or any MCP host bootstrap, since worker mode
    /// needs neither.
    /// </summary>
    public const string WorkerModeArgument = "--validate-worker";

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> both sides of the worker boundary use — camelCase,
    /// matching every other JSON shape this server emits (see <c>StructuredToolResult</c>).
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
