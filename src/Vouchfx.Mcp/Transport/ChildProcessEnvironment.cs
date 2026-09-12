using System.Diagnostics;

namespace Vouchfx.Mcp.Transport;

/// <summary>
/// Removes THIS SERVER's own secrets from a child process's inherited environment, immediately
/// before it is spawned (US-S6-06).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one sanctioned exception to "never touch a child's environment", and the
/// distinction it rests on is worth stating precisely.</b> Every child this server spawns inherits
/// its environment untouched, and that inheritance is CORRECT rather than incidental: the engine
/// resolves a suite's own <c>${secret:env/NAME}</c> references out of it, so a server that built or
/// filtered that environment would be deciding which of the operator's secrets the engine may see.
/// <c>SecretHygieneSourceGuardTests</c> forbids environment mutation at spawn sites for exactly that
/// reason.
/// </para>
/// <para>
/// What this does is the opposite operation on a different kind of value. <see cref="RemovedVariables"/>
/// holds secrets THIS SERVER owns — configuration for its own transport, which no suite has any
/// business reading and the engine has no use for. Removing them NARROWS what the child can see; it
/// never injects, never reads a value into this process, and never touches a variable the operator
/// set for the engine. An unauthenticated party who could get the engine to echo its environment
/// would otherwise be handed this server's HTTP bearer token, which is precisely the escalation the
/// token exists to prevent.
/// </para>
/// <para>
/// <b>The deliberate consequence:</b> a suite that wrote <c>${secret:env/VOUCHFX_MCP_HTTP_TOKEN}</c>
/// would fail to resolve. That is intended. A suite reaching for the server's own auth credential is
/// either a mistake or an attack, and failing to resolve is the correct answer to both.
/// </para>
/// <para>
/// Applied unconditionally rather than only when the HTTP transport is active. Whether the variable
/// happens to be set is not this code's business — a host that exports it globally, or an operator
/// who set it before switching back to stdio, must not silently start leaking it to child processes
/// because of a flag elsewhere.
/// </para>
/// </remarks>
internal static class ChildProcessEnvironment
{
    /// <summary>
    /// The variables stripped from every child's environment: this server's own secrets, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// Kept as an explicit list rather than a prefix match. A prefix rule (<c>VOUCHFX_MCP_*</c>)
    /// would quietly start removing future non-secret configuration a child might legitimately need,
    /// and the failure would look like a bug in the child.
    /// </remarks>
    public static readonly string[] RemovedVariables = [ServerTransportSelection.BearerTokenVariable];

    /// <summary>
    /// Strips <see cref="RemovedVariables"/> from <paramref name="startInfo"/>'s inherited
    /// environment. Call immediately before <c>Process.Start</c>, at every spawn site.
    /// </summary>
    /// <remarks>
    /// Reading <c>startInfo.Environment</c> materialises the inherited environment into a dictionary
    /// the child then receives — so removing a key is the only mutation performed, and every other
    /// variable still travels exactly as it would have.
    /// </remarks>
    public static void StripServerSecrets(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        foreach (var variable in RemovedVariables)
        {
            startInfo.Environment.Remove(variable);
        }
    }
}
