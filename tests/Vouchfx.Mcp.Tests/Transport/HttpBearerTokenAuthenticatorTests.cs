using Vouchfx.Mcp.Transport;

namespace Vouchfx.Mcp.Tests.Transport;

/// <summary>
/// Mirror-namespace unit tests for the bearer-token comparison that gates every HTTP request
/// (US-S6-06).
/// </summary>
public class HttpBearerTokenAuthenticatorTests
{
    private const string Token = "a-high-entropy-token-value-0123456789";

    /// <summary>The configured side reaches the authenticator as a digest, hashed once at startup.</summary>
    private static ReadOnlySpan<byte> TokenDigest =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Token));

    [Fact]
    public void TheExactToken_WithTheBearerScheme_IsAuthorised()
    {
        Assert.True(HttpBearerTokenAuthenticator.IsAuthorised($"Bearer {Token}", TokenDigest));
    }

    [Theory]
    // RFC 7235 makes the scheme case-insensitive; a client that sends "bearer" is not an attacker.
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public void TheSchemeIsMatchedCaseInsensitively(string scheme)
    {
        Assert.True(HttpBearerTokenAuthenticator.IsAuthorised($"{scheme} {Token}", TokenDigest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic " + Token)]
    [InlineData("Token " + Token)]
    // The token without any scheme at all.
    [InlineData(Token)]
    public void AMissingOrMalformedCredential_IsRefused(string? header)
    {
        Assert.False(HttpBearerTokenAuthenticator.IsAuthorised(header, TokenDigest));
    }

    [Theory]
    // Wrong value, same length — the case a naive length check would wave through.
    [InlineData("a-high-entropy-token-value-9876543210")]
    // A prefix of the real token: the classic early-exit comparison probe.
    [InlineData("a-high-entropy-token-value-012345678")]
    // The real token plus a character.
    [InlineData("a-high-entropy-token-value-0123456789x")]
    // Case differs — the token itself is compared exactly, unlike the scheme.
    [InlineData("A-HIGH-ENTROPY-TOKEN-VALUE-0123456789")]
    public void AWrongToken_IsRefused(string presented)
    {
        Assert.False(HttpBearerTokenAuthenticator.IsAuthorised($"Bearer {presented}", TokenDigest));
    }

    [Fact]
    public void AVeryLongPresentedCredential_IsRefusedWithoutBlowingTheStack()
    {
        // A presented credential is attacker-controlled and unbounded. The hashing step deliberately
        // rents rather than stackallocs for exactly this input; a megabyte here must be an ordinary
        // refusal rather than a crash.
        var enormous = new string('x', 1_000_000);

        Assert.False(HttpBearerTokenAuthenticator.IsAuthorised($"Bearer {enormous}", TokenDigest));
    }

    [Fact]
    public void AnEmptyConfiguredToken_IsAProgrammingError_NotAnOpenDoor()
    {
        // Reaching this type with no configured token would mean the fail-closed startup check was
        // bypassed. It throws rather than comparing, because the alternative — an empty token that
        // some presented value might match — is an unauthenticated server that believes it is
        // authenticated.
        Assert.Throws<ArgumentException>(() => HttpBearerTokenAuthenticator.IsAuthorised("Bearer x", ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => HttpBearerTokenAuthenticator.IsAuthorised("Bearer x", default));
    }

    /// <summary>
    /// A padded credential is REFUSED, symmetrically with the configured side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An earlier revision trimmed the presented credential, on the reasoning that a proxy which
    /// pads is not an attacker. That made the two sides of one comparison disagree about what the
    /// secret is: <c>ServerTransportSelection</c> REFUSES a configured token with surrounding
    /// whitespace outright rather than trimming it, so trimming here silently widened the set of
    /// strings that count as the right credential — the wrong direction for the comparison that
    /// gates every tool on this transport.
    /// </para>
    /// <para>
    /// The single space after the scheme is not padding and is still consumed: it is RFC 7235
    /// framing, which the scheme split handles. Everything after it is the credential, verbatim.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Bearer  {0}")]     // one extra leading space
    [InlineData("Bearer {0} ")]     // one trailing space
    [InlineData("Bearer   {0}  ")]  // both
    [InlineData("Bearer \t{0}")]    // a tab rather than a space
    public void SurroundingWhitespaceOnTheCredential_IsRefusedRatherThanTrimmed(string template)
    {
        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Token);

        Assert.False(HttpBearerTokenAuthenticator.IsAuthorised(header, TokenDigest));
    }
}
