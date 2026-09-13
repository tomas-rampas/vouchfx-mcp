using System.Text.Json;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Redirects <see cref="Console.Error"/> to a buffer for the lifetime of the instance and restores
/// the original writer on <see cref="Dispose"/> — the stderr counterpart of
/// <see cref="ConsoleOutCapture"/>.
/// </summary>
/// <remarks>
/// <para>
/// Introduced for US-S6-05. The run-lifecycle structured log records are written straight to
/// <see cref="Console.Error"/> rather than through an <c>ILogger</c>, because
/// <c>RunSuiteOrchestrator</c> is constructed eagerly outside the DI graph and no logger reaches it
/// (see <c>StructuredLog</c>'s remarks). Capturing the real stream is therefore the only way to
/// assert on those records at the harness seam.
/// </para>
/// <para>
/// <b>Console.SetError is process-global</b>, exactly as <see cref="ConsoleOutCapture"/>'s
/// redirection is, so a class asserting on captured stderr must not run concurrently with another
/// server that writes there. Log-asserting classes join <see cref="SpanAssertionGroup"/> for that
/// reason — the same serialisation the span tests needed, for the same underlying cause, and
/// measured to run that collection outside the parallel pool entirely rather than merely against its
/// own members (see that type's remarks). Tests here additionally filter by the run's own
/// <c>runId</c>, which is belt and braces on top of that isolation rather than a substitute for it.
/// </para>
/// </remarks>
internal sealed class ConsoleErrorCapture : IDisposable
{
    private readonly TextWriter _original;

    public ConsoleErrorCapture()
    {
        _original = Console.Error;
        Writer = new StringWriter();
        Console.SetError(Writer);
    }

    public StringWriter Writer { get; }

    /// <summary>Every non-blank line captured so far.</summary>
    public IReadOnlyList<string> Lines() =>
        Writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    /// <summary>
    /// Every captured line parsed as a JSON object. Fails the calling test if any line is not one —
    /// which is the "one JSON object per line" assertion in its most direct form.
    /// </summary>
    public IReadOnlyList<JsonElement> JsonLines()
    {
        var parsed = new List<JsonElement>();

        foreach (var line in Lines())
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"stderr line is not a JSON object — '{line}'. Parse error: {ex.Message}");
                return parsed;
            }

            using (document)
            {
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

                // Cloned so the element outlives the document this using disposes — matching
                // RealWorkspaceProcessTests. parsed entries stay valid; the document does not leak.
                parsed.Add(document.RootElement.Clone());
            }
        }

        return parsed;
    }

    public void Dispose() => Console.SetError(_original);
}
