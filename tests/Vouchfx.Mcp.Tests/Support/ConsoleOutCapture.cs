namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Redirects <see cref="Console.Out"/> to a buffer for the lifetime of the instance and restores
/// the original writer on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// This guards against a tool handler body mistakenly calling <c>Console.WriteLine</c> (or
/// similar): <see cref="McpTestHarness"/>'s in-memory paired-stream transport carries nothing but
/// the JSON-RPC frames the SDK writes to its own pipe, so a stray write elsewhere would otherwise
/// go unnoticed here — it lands on a separate channel from the pipe, not on it. Capturing
/// <see cref="Console.Out"/> makes that failure mode observable instead.
/// <para>
/// It does <b>not</b> exercise the production logging configuration (<c>Program.cs</c> redirects
/// the console logging provider to stderr): <see cref="McpTestHarness"/> clears all logging
/// providers rather than replicating that setup, so it says nothing about whether that
/// redirection is wired correctly. <c>RealServerProcessTests</c> covers that, against the real
/// built process over real stdio.
/// </para>
/// </remarks>
internal sealed class ConsoleOutCapture : IDisposable
{
    private readonly TextWriter _original = Console.Out;

    public StringWriter Writer { get; } = new();

    public ConsoleOutCapture() => Console.SetOut(Writer);

    public void Dispose() => Console.SetOut(_original);
}
