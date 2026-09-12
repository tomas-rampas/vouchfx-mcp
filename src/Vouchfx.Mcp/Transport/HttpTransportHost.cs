using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Mcp.Observability;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Transport;

/// <summary>
/// Builds and runs the opt-in Streamable HTTP transport (US-S6-06): the SDK's own HTTP endpoint,
/// behind a bearer-token gate that runs before it.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE DI configuration, two transports — scoped to what that actually guarantees.</b> This host
/// calls the same <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/> the stdio path
/// does, so the SERVED PRIMITIVES cannot drift: the same eighteen tools, the same prompts, the same
/// resources, the same run registry selection, the same workspace threading and the same pin gate.
/// It does NOT claim the two transports behave identically in every respect, because they do not —
/// see the transport-capability note below.
/// </para>
/// <para>
/// <b>Configuration is rooted in the INSTALL directory, never the working directory, and that is a
/// security fix rather than tidiness.</b> <c>WebApplication.CreateBuilder(args)</c> defaults its
/// content root to <see cref="Environment.CurrentDirectory"/> — which, for this server, is the
/// untrusted workspace a host happened to launch it from. A planted <c>appsettings.json</c> there
/// would be loaded as configuration and could rebind the server (Kestrel endpoints override the
/// URLs a host is asked for), and an ambient <c>ASPNETCORE_ENVIRONMENT=Development</c> would change
/// the middleware posture. Both are closed here: the content root is
/// <see cref="AppContext.BaseDirectory"/>, the environment is pinned to Production, and — belt and
/// braces — the bind endpoint is configured EXPLICITLY on Kestrel from the value the operator
/// supplied, so no configuration source can move it.
/// </para>
/// <para>
/// <b>The SDK's transport, not a hand-rolled one.</b> <c>ModelContextProtocol.AspNetCore</c> supplies
/// the Streamable HTTP server transport and its endpoint routing; hand-rolling session handling, SSE
/// framing and resumability on a security-exposed surface is exactly what this repository refuses
/// elsewhere.
/// </para>
/// <para>
/// <b>Auth is middleware ORDERING, which is what makes "rejected before the tool handler runs"
/// structural.</b> The gate is registered before <c>MapMcp</c>, so an unauthenticated request is
/// short-circuited in the pipeline and never reaches MCP dispatch — no session, no tool resolution,
/// no handler, and therefore no tool result of any kind. Enforcing this inside a tool handler
/// instead would mean every one of the eighteen had to remember to, and a nineteenth would forget.
/// </para>
/// <para>
/// <b>Transport capability differences, recorded rather than implied.</b> Two are worth knowing and
/// neither is a defect: this host does not enable the transport's stateless mode, so sessions are
/// retained and server→client requests remain available in principle — but sampling, elicitation and
/// roots depend on the CLIENT's declared capabilities, and this server uses none of them today.
/// Progress notifications over HTTP are INFERRED from the SDK's session handling rather than
/// measured by a test here; <c>run_suite</c>'s best-effort progress is the only producer, and
/// nothing in this story exercised it over HTTP.
/// </para>
/// </remarks>
internal static class HttpTransportHost
{
    /// <summary>The endpoint MCP is served on.</summary>
    private const string McpEndpointPath = "/mcp";

    /// <summary>
    /// Runs the server over HTTP until the process is stopped. Returns the process exit code.
    /// </summary>
    /// <param name="selection">
    /// The already-validated transport selection. Its digest and bind endpoint are non-null by
    /// construction — <see cref="ServerTransportSelection.TryParseCommandLine"/> refuses HTTP
    /// without them, which is what makes "no listener is opened without a token" a property of the
    /// ORDER of operations rather than of a check inside this method.
    /// </param>
    /// <param name="enginePin">The loaded pin, threaded to the shared registration unchanged.</param>
    /// <param name="workspace">The resolved workspace, or null — threaded unchanged.</param>
    public static async Task<int> RunAsync(
        ServerTransportSelection selection, EnginePin enginePin, Workspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var digest = selection.BearerTokenDigest;
        var endpoint = selection.BindEndpoint
            ?? throw new InvalidOperationException(
                "An HTTP transport selection without a bind endpoint reached the host. " +
                "ServerTransportSelection.TryParseCommandLine must refuse that combination.");

        // args are deliberately NOT forwarded to CreateBuilder: this server's flags are its own, and
        // letting ASP.NET Core's configuration binder reinterpret them is another late-binding path
        // to a different bind address. Everything the host needs has already been parsed.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(HttpTransportHost).Assembly.GetName().Name,
        });

        // Logging matches the stdio path exactly: structured records, stderr only. stdout stays free
        // of anything this process writes — the HTTP transport does not change that invariant, it
        // simply stops being the protocol channel.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            consoleLogOptions.FormatterName = StructuredConsoleFormatter.FormatterName;
        });
        builder.Logging.AddConsoleFormatter<StructuredConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // THE authoritative bind. An explicit Kestrel Listen from the already-parsed endpoint, so the
        // operator's value wins over every configuration source there is.
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
            kestrel => kestrel.Listen(endpoint));

        WebApplication app;

        try
        {
            // THE shared registration — see this type's remarks.
            builder.Services
                .AddVouchfxMcpServer(enginePin, workspace: workspace)
                .WithHttpTransport();

            app = builder.Build();
        }
        catch (RunArtefactStorageException ex)
        {
            // The same fail-closed boundary the stdio path has, for the same reason and in the same
            // shape: AddVouchfxMcpServer constructs FileRunRegistry and WorkspaceRunLock, whose
            // containment check is what this catch exists for. One sanitised line, exit 1, never a
            // stack trace — and, per US-S6-05, that line is a JSON record like every other.
            StructuredLog.Write(
                LogLevel.Error,
                "vouchfx-mcp could not configure its run-artefact storage: " +
                TextSanitiser.SanitiseForDisplay(ex.Message));
            return 1;
        }

        // Registered BEFORE MapMcp below. That ordering is the security property, not a style choice.
        app.Use(async (context, next) =>
        {
            if (!HttpBearerTokenAuthenticator.IsAuthorised(context.Request.Headers.Authorization, digest.Span))
            {
                // A plain 401 with the challenge header and NO body. Deliberately uninformative:
                // distinguishing "no token configured" from "wrong token" from "malformed header"
                // would tell an unauthenticated caller how this server is set up, and the difference
                // is of no use to a legitimate client, which either has the token or does not.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next(context);
        });

        app.MapMcp(McpEndpointPath);

        try
        {
            await app.RunAsync();
        }
        catch (IOException ex)
        {
            // The bind-failure family. A port already in use surfaces from Kestrel as an IOException
            // wrapping a SocketException, and an unhandled one would print a stack trace to a stderr
            // stream this sprint just made a structured-record contract. Same one-line, exit-1 shape
            // as every other startup fault.
            StructuredLog.Write(
                LogLevel.Error,
                $"vouchfx-mcp could not start the HTTP listener on {endpoint}: " +
                TextSanitiser.SanitiseForDisplay(ex.GetType().Name) +
                ". The port may already be in use.");
            return 1;
        }

        return 0;
    }
}
