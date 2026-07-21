using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Calls an MCP tool while capturing its <c>notifications/progress</c> messages WITHOUT the
/// subscribe/unsubscribe race built into <see cref="McpClient"/>'s own convenience
/// <c>CallToolAsync(string, IReadOnlyDictionary&lt;string, object?&gt;?, IProgress&lt;ProgressNotificationValue&gt;?, RequestOptions?, CancellationToken)</c>
/// overload — see <see cref="CallAsync"/>'s remarks for the confirmed mechanism this works around
/// and why raising a wait's timeout can never fix it.
/// </summary>
internal static class ProgressCapture
{
    /// <summary>
    /// Invokes <paramref name="toolName"/> with <paramref name="arguments"/>, adding every
    /// <c>notifications/progress</c> message correlated to THIS call into <paramref name="progressUpdates"/>
    /// — which the caller may keep polling for as long as the returned registration stays undisposed,
    /// including well after this method itself has returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Root cause this works around</b> (confirmed by decompiling ModelContextProtocol.Core 1.4.1,
    /// the exact version this repo pins — see <c>McpClient.cs</c>, <c>McpSessionHandler.cs</c> and
    /// <c>NotificationHandlers.cs</c>): the SDK's own per-call progress overload registers a TEMPORARY
    /// "notifications/progress" handler via <c>McpSession.RegisterNotificationHandler</c>, then
    /// disposes (unregisters) it in a <c>finally</c> block the INSTANT its own <c>tools/call</c>
    /// response arrives — i.e. the moment the awaited inner <c>CallToolAsync</c> call resolves.
    /// But <c>McpSessionHandler.ProcessMessagesCoreAsync</c> (the shared read loop used for both the
    /// notification and the response) dispatches EVERY incoming message — a notification or a
    /// response alike — as an independent, unawaited ("fire-and-forget") task, specifically so a slow
    /// handler can never block the read loop from reading the next message. There is therefore NO
    /// ordering guarantee between "the progress notification's handler has run" and "the response has
    /// been delivered and the tool call has returned", even though the server sent the notification
    /// strictly before its response on the wire. <c>NotificationHandlers.InvokeHandlers</c> looks up
    /// the still-registered handler for a method AT THE MOMENT a notification's own dispatch task
    /// actually executes — if the response's dispatch task wins that race first (unblocking the inner
    /// <c>CallToolAsync</c>, which then runs its <c>finally</c> and disposes the handler), the
    /// notification's handler is already gone by the time its own dispatch runs, and
    /// <c>InvokeHandlers</c> silently returns without invoking anything. The notification is not
    /// merely delayed in that case — it is PERMANENTLY dropped, with no exception and nothing to wait
    /// longer for, which is exactly why raising a wait's timeout never fixed this flake.
    /// </para>
    /// <para>
    /// <b>The fix:</b> register our OWN handler directly via
    /// <see cref="ModelContextProtocol.McpSession.RegisterNotificationHandler"/>, correlated to a
    /// progress token WE mint (never the SDK's own per-call one), and hand the registration back to
    /// the caller to dispose only once it is done observing progress — never tied to this call's own
    /// request/response lifecycle. That closes the race entirely rather than merely shrinking its
    /// window: a notification that is merely SLOW (thread-pool contention, not lost) is still caught
    /// by a caller's own bounded wait, because the handler that would add it is still registered.
    /// </para>
    /// </remarks>
    public static async Task<(CallToolResult Result, IAsyncDisposable ProgressRegistration)> CallAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ConcurrentBag<ProgressNotificationValue> progressUpdates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(progressUpdates);

        var progressToken = new ProgressToken(Guid.NewGuid().ToString("N"));

        // Own registration, own lifetime — deliberately NOT the SDK's per-call convenience overload
        // (see this type's remarks). The SDK's message loop can invoke this handler from ANY
        // thread-pool thread, independently of whatever thread is polling progressUpdates
        // afterwards — ConcurrentBag.Add is the caller's responsibility to use for exactly that
        // reason (mirrored from the callers of this helper).
        var registration = client.RegisterNotificationHandler(
            "notifications/progress",
            (notification, _) =>
            {
                var parameters = notification.Params?.Deserialize<ProgressNotificationParams>();
                if (parameters is not null && parameters.ProgressToken == progressToken)
                {
                    progressUpdates.Add(parameters.Progress);
                }

                return default;
            });

        try
        {
            var argumentsElements = new Dictionary<string, JsonElement>(arguments.Count);
            foreach (var (key, value) in arguments)
            {
                argumentsElements[key] = value is JsonElement element
                    ? element
                    : JsonSerializer.SerializeToElement(value);
            }

            var result = await client.CallToolAsync(
                new CallToolRequestParams
                {
                    Name = toolName,
                    Arguments = argumentsElements,
                    Meta = new JsonObject { ["progressToken"] = progressToken.ToString() },
                },
                cancellationToken);

            return (result, registration);
        }
        catch
        {
            // The call itself never reached/completed a response — nothing further will ever
            // correlate to this token, so unregister immediately rather than leaking the
            // registration for the rest of the harness's lifetime.
            await registration.DisposeAsync();
            throw;
        }
    }
}
