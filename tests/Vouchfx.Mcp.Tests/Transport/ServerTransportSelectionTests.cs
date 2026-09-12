using System.Net;
using Vouchfx.Mcp.Transport;

namespace Vouchfx.Mcp.Tests.Transport;

/// <summary>
/// Mirror-namespace unit tests for US-S6-06's <c>--transport</c> flag and its fail-closed bearer-token
/// requirement.
/// </summary>
public class ServerTransportSelectionTests
{
    [Fact]
    public void NoFlag_SelectsStdio_WithNoTokenRequired()
    {
        // AC 1 and AC 3's mechanism: an existing host integration passes no transport flag, and must
        // see byte-identical behaviour — including needing no configuration at all.
        Assert.True(ServerTransportSelection.TryParseCommandLine([], _ => null, out var selection, out var error));

        Assert.Null(error);
        Assert.NotNull(selection);
        Assert.Equal(ServerTransportKind.Stdio, selection!.Kind);
        Assert.True(selection.BearerTokenDigest.IsEmpty);
        Assert.Null(selection.BindEndpoint);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("STDIO")]
    public void ExplicitStdio_IsAcceptedAndStillNeedsNoToken(string value)
    {
        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", value], _ => null, out var selection, out _));

        Assert.Equal(ServerTransportKind.Stdio, selection!.Kind);
        Assert.True(selection.BearerTokenDigest.IsEmpty);
        Assert.Null(selection.BindEndpoint);
    }

    [Fact]
    public void Http_WithAConfiguredToken_IsAccepted()
    {
        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"],
                name => name == ServerTransportSelection.BearerTokenVariable ? "s3cr3t-token-value" : null,
                out var selection,
                out var error));

        Assert.Null(error);
        Assert.Equal(ServerTransportKind.Http, selection!.Kind);
        // The DIGEST, never the token: the selection discards the plaintext at parse time.
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("s3cr3t-token-value")),
            selection.BearerTokenDigest.ToArray());
    }

    /// <summary>
    /// The Gherkin's refuse-to-start scenario, at the unit seam: no token ⇒ no selection, so the
    /// caller cannot proceed to open a listener.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Http_WithoutAToken_IsRefusedWithACataloguedError(string? configured)
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => configured, out var selection, out var error));

        Assert.Null(selection);
        Assert.NotNull(error);

        // Catalogued, never an ad-hoc string — and it names the variable to set.
        Assert.Contains("VFX-E-1007", error, StringComparison.Ordinal);
        Assert.Contains(ServerTransportSelection.BearerTokenVariable, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whatever was configured cannot influence the refusal text at all.
    /// </summary>
    /// <remarks>
    /// Asserted as INVARIANCE rather than as "the message does not contain the value", because the
    /// values that reach this branch are blank by definition and a substring check against an empty
    /// string is vacuously true — a trap an earlier revision of this test fell into. Invariance is
    /// the property that actually matters: an operator who put a secret-shaped string in the
    /// variable must not have any of it reflected back, and the only way to be sure is that the
    /// diagnosis is a fixed literal.
    /// </remarks>
    [Fact]
    public void TheRefusalMessage_IsInvariantToWhateverWasConfigured()
    {
        var messages = new[] { null, string.Empty, "   ", "\t  \r\n" }
            .Select(configured =>
            {
                ServerTransportSelection.TryParseCommandLine(
                    ["--transport", "http"], _ => configured, out _, out var error);
                return error;
            })
            .ToArray();

        Assert.All(messages, message => Assert.NotNull(message));
        Assert.Single(messages.Distinct(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("HTTPS")]
    [InlineData("")]
    public void AnUnknownTransport_IsRefusedRatherThanFallingBackToStdio(string value)
    {
        // Fail closed, exactly as a near-miss --workspace is: silently serving stdio when the
        // operator asked for something else is how a containment or exposure assumption breaks.
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", value], _ => "a-sufficiently-long-token", out var selection, out var error));

        Assert.Null(selection);
        Assert.NotNull(error);
        Assert.Contains("--transport", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlagWithNoValue_IsRefused()
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport"], _ => "a-sufficiently-long-token", out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void TheEqualsSpelling_IsAcceptedLikeEveryOtherFlagInThisServer()
    {
        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport=http"], _ => "a-sufficiently-long-token", out var selection, out _));

        Assert.Equal(ServerTransportKind.Http, selection!.Kind);
    }

    /// <summary>
    /// The token is read from the ENVIRONMENT and never from argv — asserted structurally, because
    /// the whole point is that a token on the command line is visible to every user on the box.
    /// </summary>
    [Fact]
    public void ATokenPassedOnTheCommandLine_IsRefusedOutrightRatherThanHonoured()
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http", "--bearer-token", "s3cr3t"], _ => null, out var selection, out var error));

        Assert.Null(selection);
        Assert.NotNull(error);

        // Refused, and the refusal explains the alternative rather than just rejecting the argument.
        Assert.Contains(ServerTransportSelection.BearerTokenVariable, error!, StringComparison.Ordinal);

        // And never echoes the token it was handed.
        Assert.DoesNotContain("s3cr3t", error!, StringComparison.Ordinal);
    }

    [Theory]
    // Too short — a length floor under the documentation's "use openssl rand" advice.
    [InlineData("Zq7")]
    [InlineData("123456789012345")]
    public void AConfiguredTokenBelowTheMinimumLength_IsRefused(string token)
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => token, out var selection, out var error));

        Assert.Null(selection);
        Assert.Contains("VFX-E-1007", error!, StringComparison.Ordinal);

        // And never echoes the token it refused.
        Assert.DoesNotContain(token, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredTokenAtExactlyTheMinimum_IsAccepted()
    {
        // Anti-vacuity for the bound: 16 is allowed, 15 is not (above).
        var atBound = new string('a', ServerTransportSelection.MinimumTokenLength);

        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => atBound, out var selection, out _));

        Assert.Equal(ServerTransportKind.Http, selection!.Kind);
    }

    [Theory]
    [InlineData(" a-sufficiently-long-token")]
    [InlineData("a-sufficiently-long-token ")]
    [InlineData("a-sufficiently-long-token\n")]
    public void AConfiguredTokenWithPadding_IsRefusedRatherThanTrimmed(string token)
    {
        // Trimming would mean the credential this server accepts differs from the one the operator
        // configured — a mismatch that only shows up against some other consumer of the same secret.
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => token, out _, out var error));

        Assert.Contains("whitespace", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("token-with-é-inside-it-long")]
    [InlineData("token-with-\u0001-control-char")]
    public void AConfiguredTokenThatIsNotPrintableAscii_IsRefused(string token)
    {
        // A KNOWN outcome rather than a mystery: how a client encodes a non-ASCII header value is not
        // well defined, so such a token would authenticate from some clients and not others. Refusing
        // at startup beats a permanent, unexplainable 401.
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => token, out _, out var error));

        Assert.Contains("ASCII", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--transport", "http", "--transport", "stdio")]
    [InlineData("--transport=http", "--transport=stdio")]
    public void ARepeatedTransportFlag_IsRefusedRatherThanResolvedSilently(params string[] args)
    {
        // Neither first-wins nor last-wins: either silent rule hands an operator a configuration they
        // did not write, and for a transport that is the difference between local and remote.
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                args, _ => "a-sufficiently-long-token", out _, out var error));

        Assert.Contains("more than once", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedUrlsFlag_IsRefusedToo()
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http", "--urls", "http://127.0.0.1:1", "--urls", "http://0.0.0.0:2"],
                _ => "a-sufficiently-long-token",
                out _,
                out var error));

        Assert.Contains("more than once", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoUrlsFlag_TheBindDefaultsToLoopback()
    {
        // The documented default, asserted rather than described: loopback, so trying the flag cannot
        // silently expose the tools that spawn the engine CLI to the network.
        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http"], _ => "a-sufficiently-long-token", out var selection, out _));

        Assert.Equal(IPAddress.Loopback, selection!.BindEndpoint!.Address);
        Assert.Equal(5090, selection.BindEndpoint.Port);
        Assert.Equal(ServerTransportSelection.DefaultBindUrl, $"http://{selection.BindEndpoint.Address}:{selection.BindEndpoint.Port}");
    }

    [Fact]
    public void AnExplicitUrl_IsResolvedToThatEndpoint()
    {
        Assert.True(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http", "--urls", "http://0.0.0.0:7171"],
                _ => "a-sufficiently-long-token",
                out var selection,
                out _));

        Assert.Equal(IPAddress.Any, selection!.BindEndpoint!.Address);
        Assert.Equal(7171, selection.BindEndpoint.Port);
    }

    [Theory]
    // A host NAME would need resolution — exactly the late-binding this parse removes.
    [InlineData("http://localhost:5090")]
    // https is not served; TLS belongs in front of this server.
    [InlineData("https://127.0.0.1:5090")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void AnUnusableUrl_IsRefused(string url)
    {
        Assert.False(
            ServerTransportSelection.TryParseCommandLine(
                ["--transport", "http", "--urls", url], _ => "a-sufficiently-long-token", out _, out var error));

        Assert.Contains("VFX-E-1007", error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record must never render its members — the defect that made a positional record print the
    /// bearer token through its generated <c>ToString</c>.
    /// </summary>
    [Fact]
    public void ToString_RendersNoMembers_SoNoSecretCanEverLeakThroughIt()
    {
        const string token = "a-very-distinctive-token-value-xyz";

        ServerTransportSelection.TryParseCommandLine(
            ["--transport", "http"], _ => token, out var selection, out _);

        var rendered = selection!.ToString();

        Assert.DoesNotContain(token, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Digest", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("BindEndpoint", rendered, StringComparison.Ordinal);
        Assert.Equal("ServerTransportSelection { }", rendered);
    }
}
