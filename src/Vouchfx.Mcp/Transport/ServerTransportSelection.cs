using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Transport;

/// <summary>Which transport this server serves MCP over (US-S6-06).</summary>
public enum ServerTransportKind
{
    /// <summary>The default and the shipped contract: JSON-RPC over stdin/stdout.</summary>
    Stdio,

    /// <summary>Streamable HTTP, opt-in, bearer-token authenticated.</summary>
    Http,
}

/// <summary>
/// The resolved transport configuration: which transport, where it binds, and — for HTTP only — a
/// DIGEST of the bearer token every request must present.
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is never held in this type as plaintext.</b> It is hashed at parse time and the
/// string discarded; only <see cref="BearerTokenDigest"/> survives. Two reasons, and the first was a
/// real defect: a positional record generates a <c>ToString</c> that prints every member, so a
/// selection carrying the token would have rendered it into any log line, exception message or
/// debugger view that stringified it — one careless interpolation away from publishing the
/// credential. The second is that the comparison needs a digest anyway, so hashing once at startup
/// removes a per-request hash of the configured side.
/// </para>
/// <para>
/// <see cref="PrintMembers"/> is overridden regardless, so even a future member holding secret
/// material cannot regress into the generated <c>ToString</c>.
/// </para>
/// </remarks>
public sealed record ServerTransportSelection
{
    /// <summary>The flag this server reads.</summary>
    public const string TransportFlag = "--transport";

    /// <summary>The bind-address flag.</summary>
    public const string UrlsFlag = "--urls";

    /// <summary>
    /// Where the HTTP transport binds when the operator names no address.
    /// </summary>
    /// <remarks>
    /// LOOPBACK, deliberately and by default. A server that bound every interface by default would
    /// expose every tool that spawns the engine CLI to the network the moment an operator tried the
    /// flag, and the failure would be silent. An operator who genuinely wants a non-loopback bind has
    /// to say so — and is told, in <c>docs/install.md</c>, to put TLS and a reverse proxy in front of
    /// it, because this server speaks cleartext HTTP and the bearer token is on the wire every
    /// request.
    /// </remarks>
    public const string DefaultBindUrl = "http://127.0.0.1:5090";

    /// <summary>
    /// The environment variable carrying the HTTP bearer token.
    /// </summary>
    /// <remarks>
    /// <b>An environment variable rather than a command-line argument, and that is a security
    /// decision rather than a style one.</b> A process's argv is world-readable: any user on the host
    /// can read <c>/proc/&lt;pid&gt;/cmdline</c> on Linux or see the full command line in
    /// <c>ps</c>/Task Manager, so a token passed as an argument is exposed to every account on the
    /// machine for the life of the process — and it lands in shell history and in whatever
    /// supervisor or container manifest launched it. <see cref="TryParseCommandLine"/> therefore
    /// REFUSES a token-shaped argument outright instead of honouring it.
    /// </remarks>
    public const string BearerTokenVariable = "VOUCHFX_MCP_HTTP_TOKEN";

    /// <summary>
    /// The shortest configured token this server will start with.
    /// </summary>
    /// <remarks>
    /// A length floor is a blunt instrument and is not a substitute for entropy — <c>0123456789abcdef</c>
    /// passes it. What it buys is that the most catastrophic configurations (a one-character token, a
    /// placeholder like <c>changeme</c>) cannot reach a listening socket at all, which is worth
    /// having for free. The documentation asks for <c>openssl rand -base64 48</c>; this is the floor
    /// under the advice, not the advice.
    /// </remarks>
    public const int MinimumTokenLength = 16;

    /// <summary>
    /// The longest <c>Authorization</c> credential this server will even hash.
    /// </summary>
    /// <remarks>
    /// Bounded before any work is done on attacker-controlled input. Kestrel already caps a request
    /// header at 32 KB, so this is not the only bound — but relying on a web-server default for a
    /// security property is how that property disappears when the default changes, and 512 characters
    /// is comfortably above any legitimate token while being orders of magnitude below what an
    /// attacker would need to make hashing interesting.
    /// </remarks>
    public const int MaximumCredentialLength = 512;

    /// <summary>The token-on-the-command-line spelling this server refuses rather than accepts.</summary>
    private const string RefusedTokenFlag = "--bearer-token";

    private ServerTransportSelection(
        ServerTransportKind kind, ReadOnlyMemory<byte> bearerTokenDigest, IPEndPoint? bindEndpoint)
    {
        Kind = kind;
        BearerTokenDigest = bearerTokenDigest;
        BindEndpoint = bindEndpoint;
    }

    /// <summary>The selected transport.</summary>
    public ServerTransportKind Kind { get; }

    /// <summary>
    /// SHA-256 of the configured bearer token, or empty for stdio. Never the token itself.
    /// </summary>
    public ReadOnlyMemory<byte> BearerTokenDigest { get; }

    /// <summary>
    /// Where the HTTP transport binds — resolved at parse time from <see cref="UrlsFlag"/> or
    /// <see cref="DefaultBindUrl"/>, and <see langword="null"/> for stdio.
    /// </summary>
    /// <remarks>
    /// Resolved HERE, into a concrete endpoint, rather than left as a string for the web host to
    /// interpret later. That is what makes the operator's value AUTHORITATIVE: the host binds this
    /// endpoint explicitly, so no configuration file and no ambient environment variable can
    /// silently rebind the server somewhere else. See <c>HttpTransportHost</c>.
    /// </remarks>
    public IPEndPoint? BindEndpoint { get; }

    /// <summary>
    /// Suppresses the record's generated member printing entirely — see this type's remarks.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> so <c>ToString</c> renders as
    /// <c>ServerTransportSelection { }</c>. Even though no plaintext secret is held any more, this
    /// stays: it is the structural half of the fix, and it is what stops a future member from
    /// reintroducing the defect silently.
    /// </remarks>
    // CA1822 suppressed: a record's PrintMembers MUST be an instance member (CS8877), so "could be
    // static" is advice the language forbids taking here.
#pragma warning disable CA1822
    private bool PrintMembers(StringBuilder builder)
#pragma warning restore CA1822
    {
        _ = builder;
        return false;
    }

    /// <summary>
    /// Resolves the transport from <paramref name="args"/>, reading the bearer token through
    /// <paramref name="readEnvironmentVariable"/>.
    /// </summary>
    /// <param name="args">The server's command line.</param>
    /// <param name="readEnvironmentVariable">
    /// How to read an environment variable. Injected rather than read directly so a test can drive
    /// every branch without mutating the test host's own environment — which, being process-global,
    /// would race every other test in a parallel assembly.
    /// </param>
    /// <param name="selection">The resolved selection, or <see langword="null"/> on refusal.</param>
    /// <param name="error">A sanitised, catalogued diagnosis, or <see langword="null"/> on success.</param>
    /// <remarks>
    /// <b>Fail closed, in every direction.</b> An unknown transport, a repeated flag, a missing
    /// value, a token that is too short or carries padding — all are refusals, never a silent
    /// fall back to stdio or a quiet normalisation. An operator who asked for HTTP and got stdio
    /// would believe a listener exists when none does; one whose token was silently trimmed would
    /// have a credential that differs from the one they configured.
    /// </remarks>
    public static bool TryParseCommandLine(
        IReadOnlyList<string> args,
        Func<string, string?> readEnvironmentVariable,
        out ServerTransportSelection? selection,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        selection = null;
        error = null;

        // A token on the command line is refused BEFORE anything else is decided, and deliberately
        // not merely ignored: ignoring it would leave an operator believing their token was in force.
        // The refusal names the supported mechanism and never echoes the value it was handed.
        if (args.Any(arg =>
                arg.Equals(RefusedTokenFlag, StringComparison.Ordinal) ||
                arg.StartsWith(RefusedTokenFlag + "=", StringComparison.Ordinal)))
        {
            error = Refuse(
                $"a bearer token must not be passed on the command line ({RefusedTokenFlag} is not " +
                "supported): a process's arguments are readable by every user on the host. Set the " +
                $"{BearerTokenVariable} environment variable instead.");
            return false;
        }

        if (!TryReadFlagValue(args, TransportFlag, out var transportValue, out var transportError))
        {
            error = transportError;
            return false;
        }

        if (transportValue is null || transportValue.Equals("stdio", StringComparison.OrdinalIgnoreCase))
        {
            // A bind address supplied without the HTTP transport is REFUSED, not ignored — the same
            // rule, and the same reasoning, as the --bearer-token refusal above. Ignoring it leaves
            // an operator believing they configured where the server listens when it is not
            // listening at all, and the two most likely causes (a forgotten --transport http, or a
            // supervisor template that always appends --urls) are exactly the ones a silent
            // acceptance hides. Refusing costs one restart; ignoring costs a debugging session.
            if (args.Any(arg =>
                    arg.Equals(UrlsFlag, StringComparison.Ordinal) ||
                    arg.StartsWith(UrlsFlag + "=", StringComparison.Ordinal)))
            {
                error = Refuse(
                    $"{UrlsFlag} was supplied without {TransportFlag} http. The stdio transport binds " +
                    $"no socket, so a bind address would have no effect. Add {TransportFlag} http, or " +
                    $"drop {UrlsFlag}.");
                return false;
            }

            // The no-flag path and the explicit-stdio path converge here, which is what makes
            // "omitted behaves as today" structural: there is no stdio-specific configuration.
            selection = new ServerTransportSelection(
                ServerTransportKind.Stdio, ReadOnlyMemory<byte>.Empty, bindEndpoint: null);
            return true;
        }

        if (!transportValue.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            error = Refuse(
                $"unknown {TransportFlag} value '{TextSanitiser.SanitiseForDisplay(transportValue)}'. " +
                "Supported values are 'stdio' (the default) and 'http'.");
            return false;
        }

        if (!TryResolveBindEndpoint(args, out var endpoint, out var bindError))
        {
            error = bindError;
            return false;
        }

        if (!TryResolveToken(readEnvironmentVariable, out var digest, out var tokenError))
        {
            error = tokenError;
            return false;
        }

        selection = new ServerTransportSelection(ServerTransportKind.Http, digest, endpoint);
        return true;
    }

    /// <summary>Reads and validates the configured token, returning only its digest.</summary>
    private static bool TryResolveToken(
        Func<string, string?> readEnvironmentVariable, out ReadOnlyMemory<byte> digest, out string? error)
    {
        digest = ReadOnlyMemory<byte>.Empty;
        error = null;

        var token = readEnvironmentVariable(BearerTokenVariable);

        if (string.IsNullOrWhiteSpace(token))
        {
            // THE fail-closed start. Returning false here is what stops Program.cs reaching any
            // listener construction, so "no HTTP listener is opened" is a property of the order of
            // operations rather than of remembering to tear one down. The diagnosis never quotes what
            // was found — whatever an operator put there is secret-shaped by definition.
            error = Refuse(
                $"the {TransportFlag} http mode requires a bearer token, and the " +
                $"{BearerTokenVariable} environment variable is unset or blank. Set it to a " +
                "high-entropy secret and restart. No HTTP listener was opened.");
            return false;
        }

        // Padding is REFUSED rather than trimmed. Trimming would mean the credential this server
        // accepts differs from the one the operator configured — so a token pasted with a trailing
        // newline would work here and fail against any other consumer of the same secret, which is
        // the kind of mismatch that costs an afternoon. Refusing says so immediately.
        if (token.Length != token.Trim().Length)
        {
            error = Refuse(
                $"the {BearerTokenVariable} value has leading or trailing whitespace. That is refused " +
                "rather than trimmed, so the token this server accepts is exactly the one you " +
                "configured. Remove the padding and restart.");
            return false;
        }

        if (token.Length < MinimumTokenLength)
        {
            error = Refuse(
                $"the {BearerTokenVariable} value is shorter than the {MinimumTokenLength}-character " +
                "minimum. Use a high-entropy secret — `openssl rand -base64 48` is one way. " +
                "No HTTP listener was opened.");
            return false;
        }

        // ASCII only, and refused rather than silently accepted. HTTP header values are not a
        // reliable channel for non-ASCII: what a client puts on the wire depends on its own encoding
        // choices, so a token with a non-ASCII character would authenticate from some clients and not
        // others. A permanent, unexplainable 401 is a far worse outcome than this refusal.
        if (token.Any(c => c is < ' ' or > '~'))
        {
            error = Refuse(
                $"the {BearerTokenVariable} value contains non-ASCII or control characters. Bearer " +
                "credentials must be printable ASCII, because how a client encodes anything else in " +
                "an HTTP header is not well defined and would authenticate inconsistently.");
            return false;
        }

        digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return true;
    }

    /// <summary>Resolves the bind endpoint from the URL flag, or the loopback default.</summary>
    private static bool TryResolveBindEndpoint(
        IReadOnlyList<string> args, out IPEndPoint? endpoint, out string? error)
    {
        endpoint = null;

        if (!TryReadFlagValue(args, UrlsFlag, out var value, out error))
        {
            return false;
        }

        var url = value ?? DefaultBindUrl;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            !parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = Refuse(
                $"{UrlsFlag} value '{TextSanitiser.SanitiseForDisplay(url)}' is not an absolute http:// " +
                $"URL. This server speaks cleartext HTTP only; put TLS in front of it. Example: " +
                $"{DefaultBindUrl}");
            return false;
        }

        if (!IPAddress.TryParse(parsed.Host, out var address))
        {
            // A host NAME would have to be resolved, and resolution is exactly the ambient,
            // late-binding behaviour this parse exists to remove. An explicit address is unambiguous.
            error = Refuse(
                $"{UrlsFlag} host '{TextSanitiser.SanitiseForDisplay(parsed.Host)}' must be a literal " +
                $"IP address (e.g. 127.0.0.1), not a host name, so the bind cannot depend on name " +
                "resolution at start time.");
            return false;
        }

        endpoint = new IPEndPoint(address, parsed.Port);
        return true;
    }

    /// <summary>
    /// Reads a flag's value in both the space-separated and <c>=</c> spellings, refusing a repeat.
    /// </summary>
    /// <remarks>
    /// A REPEATED flag is a refusal rather than first-wins or last-wins. Either silent rule means an
    /// operator whose supervisor appends a second value gets a configuration they did not write, and
    /// for a bind address that is the difference between loopback and the network.
    /// </remarks>
    private static bool TryReadFlagValue(
        IReadOnlyList<string> args, string flag, out string? value, out string? error)
    {
        value = null;
        error = null;
        var seen = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? candidate;

            if (arg.StartsWith(flag + "=", StringComparison.Ordinal))
            {
                candidate = arg[(flag.Length + 1)..];
            }
            else if (arg.Equals(flag, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    error = Refuse($"{flag} was given with no value.");
                    return false;
                }

                candidate = args[i + 1];
                i++;
            }
            else
            {
                continue;
            }

            if (seen)
            {
                error = Refuse(
                    $"{flag} was given more than once. Supply it exactly once — silently choosing " +
                    "one of the values would give you a configuration you did not write.");
                return false;
            }

            seen = true;
            value = candidate;
        }

        return true;
    }

    private static string Refuse(string detail) =>
        VfxCodeCatalogue.DescribeStartupFailure(VfxCodeCatalogue.HttpTransportConfigurationInvalid, detail);
}
