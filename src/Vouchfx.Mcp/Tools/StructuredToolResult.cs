using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Builds a <see cref="CallToolResult"/> for a real (non-stub) tool handler: either a successful
/// structured result, or a tool-level error.
/// </summary>
/// <remarks>
/// Built explicitly rather than relying on <c>McpServerToolCreateOptions.UseStructuredContent</c>'s
/// return-type-based inference, so every real tool's JSON shape (both the <c>Content</c> text
/// block and <c>StructuredContent</c>) is produced the same explicit way regardless of what its
/// <c>Handle</c> method's C# return type happens to be — several handlers return either a result
/// payload or an error, and inference from a shared <c>object</c>-typed return doesn't carry a
/// useful output schema anyway.
/// </remarks>
internal static class StructuredToolResult
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds a successful result: <paramref name="payload"/> serialised as both the structured
    /// content and, as text, the first content block (for clients that only read <c>Content</c>).
    /// </summary>
    public static CallToolResult Success(object payload)
    {
        var payloadType = payload.GetType();

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, payloadType, Options) }],
            StructuredContent = JsonSerializer.SerializeToElement(payload, payloadType, Options),
        };
    }

    /// <summary>Builds a tool-level error result: not a crash, just <c>IsError: true</c> with a clear message.</summary>
    public static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
