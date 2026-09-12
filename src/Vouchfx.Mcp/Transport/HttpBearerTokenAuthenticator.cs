using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Vouchfx.Mcp.Transport;

/// <summary>
/// Compares a presented <c>Authorization: Bearer …</c> credential against the configured token's
/// digest (US-S6-06).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the constant-time property actually is, stated precisely.</b>
/// <see cref="CryptographicOperations.FixedTimeEquals"/> is only constant-time for buffers of EQUAL
/// length — handed different lengths it returns immediately, which would leak the configured token's
/// length to anyone who can time the endpoint. Both sides are therefore reduced to a fixed 32-byte
/// SHA-256 digest before comparison, and the comparison itself is over those.
/// </para>
/// <para>
/// The honest limit of that claim: hashing the PRESENTED credential costs time proportional to its
/// length, so an attacker can of course tell that a longer string took longer to hash. What is
/// constant is the CONFIGURED token's contribution — nothing an attacker can measure varies with the
/// secret, which is the property that matters. <see cref="ServerTransportSelection.MaximumCredentialLength"/>
/// bounds the presented side before any hashing happens, so that cost is bounded too.
/// </para>
/// <para>
/// The configured side is hashed ONCE, at startup, and reaches this type as a digest — see
/// <see cref="ServerTransportSelection.BearerTokenDigest"/>. This type never sees the token in
/// plaintext, never logs, never throws with, and never returns either value. The answer is a bool: a
/// caller that wanted to explain WHY a credential failed would be building the oracle the plain 401
/// exists to avoid.
/// </para>
/// </remarks>
internal static class HttpBearerTokenAuthenticator
{
    /// <summary>The scheme this server accepts, matched case-insensitively per RFC 7235.</summary>
    private const string BearerScheme = "Bearer";

    /// <summary>
    /// Whether <paramref name="authorizationHeader"/> presents the token behind
    /// <paramref name="configuredTokenDigest"/>.
    /// </summary>
    /// <param name="authorizationHeader">
    /// The raw <c>Authorization</c> header value, or <see langword="null"/> when absent.
    /// </param>
    /// <param name="configuredTokenDigest">
    /// SHA-256 of the configured token, computed once at startup. Never empty by construction.
    /// </param>
    public static bool IsAuthorised(string? authorizationHeader, ReadOnlySpan<byte> configuredTokenDigest)
    {
        if (configuredTokenDigest.IsEmpty)
        {
            // Reaching here with no configured digest would mean the fail-closed startup check was
            // bypassed. Throwing is the only safe answer: the alternative is a server that believes
            // it is authenticated while accepting anything.
            throw new ArgumentException(
                "No configured bearer-token digest was supplied. The HTTP transport must not start " +
                "without one.",
                nameof(configuredTokenDigest));
        }

        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return false;
        }

        // Scheme parsing is NOT constant-time and does not need to be: the scheme name is public,
        // fixed, and carries no secret. Only the credential comparison below is timing-sensitive.
        var separator = authorizationHeader.IndexOf(' ', StringComparison.Ordinal);

        if (separator <= 0)
        {
            return false;
        }

        if (!authorizationHeader.AsSpan(0, separator).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = authorizationHeader.AsSpan(separator + 1).Trim();

        // Bounded BEFORE any hashing. Kestrel caps a request header at 32 KB, but relying on a web
        // server's default for a security property is how that property vanishes when the default
        // changes — and a legitimate token is nowhere near this bound.
        if (presented.Length > ServerTransportSelection.MaximumCredentialLength)
        {
            return false;
        }

        Span<byte> presentedDigest = stackalloc byte[32];

        try
        {
            HashUtf8Into(presented, presentedDigest);
            return CryptographicOperations.FixedTimeEquals(presentedDigest, configuredTokenDigest);
        }
        finally
        {
            // Derived from attacker-controlled input rather than from the secret, but cleared anyway
            // rather than left on the stack for whatever runs next in this frame.
            CryptographicOperations.ZeroMemory(presentedDigest);
        }
    }

    private static void HashUtf8Into(ReadOnlySpan<char> value, Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);

        // Rented rather than stack-allocated: the presented credential is attacker-controlled, and
        // stackalloc on a caller-influenced length is a stack-overflow lever even under a cap.
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            SHA256.HashData(buffer.AsSpan(0, written), destination);
        }
        finally
        {
            // clearArray: true does the zeroing, so there is no separate manual wipe to forget or to
            // get wrong — the pool clears the whole rented array, not just the written prefix.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
