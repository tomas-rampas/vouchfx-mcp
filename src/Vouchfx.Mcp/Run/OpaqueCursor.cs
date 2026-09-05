using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — OpaqueCursor (Sprint 3 / US-S3-05; spec §4.5).
//
// THE cursor implementation for this server, singular by design. Spec §4.5 says "any list-returning
// tool accepts limit (default 200, max 2000) and cursor (opaque string) and returns nextCursor", and
// the sprint's exit checklist spells out the consequence: "one cursor implementation, verified by a
// shared unit-test fixture, not two". get_run_events (US-S3-05) is the first caller; list_runs
// (US-S3-03) is the second and reuses this type verbatim, supplying its own scope constant and its
// own filter binding. Nothing about the encoded payload is specific to either tool.
//
// ---------------------------------------------------------------------------------------------
// What a cursor is, and what it deliberately is NOT
// ---------------------------------------------------------------------------------------------
//
// It is an OPAQUE continuation token: base64url text whose internal structure is this server's own
// business and no part of any tool's contract. A host must treat it as a string to hand back
// unchanged. Nothing outside this file — no tool description, no docs page, no result field — may
// describe what a position means, so the representation can change without a contract change.
//
// It is NOT an authentication token, and NEITHER digest below is a MAC. There is deliberately no
// server secret: a forged cursor buys a caller nothing they could not get by calling the same tool
// with different arguments, because a position only ever indexes into data the caller already asked
// for and is already entitled to read. Introducing a keyed MAC would imply a security property this
// type does not have and does not need, and would make cursors non-portable across a server
// restart for no gain. If a future caller ever pages over data whose visibility differs per caller,
// that is the moment to revisit this paragraph — not before.
//
// ---------------------------------------------------------------------------------------------
// The two digests, and why there are two
// ---------------------------------------------------------------------------------------------
//
// A cursor's payload is `version|scope|bindingDigest|position|integrityDigest`.
//
//   * bindingDigest fingerprints the caller's FILTERS (see the next section). It is what
//     distinguishes "a cursor from a different page walk" from "a cursor from this one".
//   * integrityDigest covers everything before it, and exists because the first version of this
//     type did NOT have it — with only the binding digested, a single flipped base64 character in
//     the position field decoded cleanly to a DIFFERENT position, and truncating the base64 tail
//     silently dropped a payload byte and was still accepted. Both were caught by the shared cursor
//     contract's tampering case, and both would have served a caller a plausible page from the
//     wrong offset with nothing to detect it by. It is a checksum against corruption and casual
//     tampering, at exactly the strength the paragraph above argues for.
//
// ---------------------------------------------------------------------------------------------
// Why the filter binding exists
// ---------------------------------------------------------------------------------------------
//
// A position is only meaningful under the filters that produced it. Page 2 of "types=[step-attempt]"
// applied to a call with no filters at all would silently skip a prefix of an entirely different
// result set — the caller would get a plausible-looking page that is quietly wrong, which is the
// worst failure mode available. Binding the filters into the cursor turns that into a REFUSAL
// (VFX-E-1506) instead: a cursor minted under one set of arguments cannot be misapplied under
// another, and the caller is told to restart the page walk.
//
// The binding is a digest, not the arguments themselves, for two reasons: a cursor stays short and
// fixed-length regardless of how many types a caller filtered on, and the filters — which can
// include a caller-supplied stepId — never travel back through the caller's own state as
// recoverable text this server would then have to re-sanitise on the way in.

/// <summary>
/// The <c>scope</c> values <see cref="OpaqueCursor"/> discriminates on — one per paginated tool, so
/// a cursor is single-purpose: handing <c>list_runs</c> a <c>get_run_events</c> cursor is refused
/// rather than decoded into a position that means something else entirely.
/// </summary>
/// <remarks>
/// A scope is part of the SIGNED-OVER payload rather than a convention, precisely because both
/// tools' cursors are the same shape of string and a host that mixes them up would otherwise get a
/// silently wrong page. US-S3-03's <c>list_runs</c> adds its own constant here; it must not reuse
/// <see cref="RunEvents"/>.
/// </remarks>
public static class CursorScopes
{
    /// <summary><c>get_run_events</c>'s scope (US-S3-05).</summary>
    public const string RunEvents = "run-events";
}

/// <summary>
/// Why <see cref="OpaqueCursor.TryDecode"/> refused a cursor. Every value is a REFUSAL: a cursor
/// this server cannot verify is never silently treated as "start from the beginning", because that
/// would hand a caller a duplicate page and call it a continuation.
/// </summary>
public enum CursorRejection
{
    /// <summary>No rejection — the cursor decoded and verified.</summary>
    None = 0,

    /// <summary>Not base64url, not this type's payload shape, over-long, or carrying a non-numeric position.</summary>
    Malformed,

    /// <summary>A well-formed payload from a different <see cref="OpaqueCursor.FormatVersion"/>.</summary>
    UnsupportedVersion,

    /// <summary>A cursor minted for a different tool (see <see cref="CursorScopes"/>).</summary>
    ScopeMismatch,

    /// <summary>A cursor minted under different filter arguments than the call now presenting it.</summary>
    FilterMismatch,
}

/// <summary>
/// Encodes and verifies this server's opaque pagination cursors (spec §4.5). See this file's header
/// comment for what a cursor is, what it deliberately is not, and why the filter binding exists.
/// </summary>
public static class OpaqueCursor
{
    /// <summary>
    /// The payload format version. Bumped only when the payload's FIELD LAYOUT changes; a cursor
    /// carrying any other version is refused as <see cref="CursorRejection.UnsupportedVersion"/>
    /// rather than misread, which is what makes a cursor minted by an older server safe to present
    /// to a newer one.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Longest cursor string this type will even attempt to decode. A legitimate cursor is around
    /// 60 characters; this bound exists so a hostile multi-megabyte "cursor" is rejected on its
    /// length before any base64 decode allocates anything.
    /// </summary>
    internal const int MaxCursorChars = 512;

    /// <summary>Field separator inside the decoded payload. Absent from every field by construction (see <see cref="Encode"/>).</summary>
    private const char FieldSeparator = '|';

    /// <summary>
    /// Separates the parts of a composed binding string. ASCII US (0x1F) — a control character no
    /// legitimate argument value contains, chosen so an argument cannot forge a part boundary even
    /// before the length prefixes below make that impossible anyway.
    /// </summary>
    private const char BindingUnitSeparator = (char)0x1F;

    /// <summary>
    /// Hex characters kept from each of the two SHA-256 digests a cursor carries — 16 bytes each.
    /// Not a security parameter (see the header: neither is a MAC): it is a collision bound for
    /// distinguishing one caller's filter set from another's and for detecting a corrupted payload,
    /// where 2^-64 on an ACCIDENTAL collision is far beyond generous, and half a digest keeps the
    /// cursor short (~100 characters, well inside <see cref="MaxCursorChars"/>).
    /// </summary>
    private const int DigestChars = 32;

    /// <summary>
    /// Composes the canonical binding string a cursor is bound to, from the caller's filter
    /// arguments. Length-prefixed and separated so no two different argument lists can produce the
    /// same string — <c>["ab", "c"]</c> and <c>["a", "bc"]</c> must not bind identically.
    /// </summary>
    /// <param name="parts">
    /// The filter arguments in a FIXED order the caller decides once. A <see langword="null"/> part
    /// is distinct from an empty one: "the caller sent no stepId" and "the caller sent stepId: ''"
    /// select different result sets, so they must not share a cursor.
    /// </param>
    /// <remarks>
    /// <b>What belongs in a binding, and what must not.</b> Everything that changes WHICH items the
    /// page walk enumerates: the run/resource being paged, and every filter. Nothing that changes
    /// only HOW MANY come back at a time — in particular <c>limit</c>, which a caller may legitimately
    /// change between pages (a host that shrinks its page size mid-walk is not making an error, and
    /// refusing it would be a bug in this server, not a protection).
    /// </remarks>
    public static string ComposeBinding(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null)
            {
                builder.Append('~');
            }
            else
            {
                builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            }

            builder.Append(BindingUnitSeparator);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Mints the cursor for <paramref name="position"/> under <paramref name="scope"/> and
    /// <paramref name="binding"/>.
    /// </summary>
    /// <param name="scope">One of <see cref="CursorScopes"/>' constants.</param>
    /// <param name="binding">The caller's filter binding, from <see cref="ComposeBinding"/>.</param>
    /// <param name="position">
    /// The IMPLEMENTATION-OWNED position the next page resumes from. Its meaning belongs entirely to
    /// the calling tool (for <c>get_run_events</c> it is a line index into the events file — see
    /// <see cref="GetRunEventsOrchestrator"/>) and is never described in any tool contract, docs
    /// page, or result field. Must not be negative.
    /// </param>
    public static string Encode(string scope, string binding, long position)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        // A scope carrying the field separator would make the payload ambiguous. Every scope is one
        // of this assembly's own constants, so this is a programming-error guard, not input
        // validation — hence a throw rather than a graceful refusal.
        if (scope.Contains(FieldSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A cursor scope must not contain '{FieldSeparator}'.", nameof(scope));
        }

        var body = string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatVersion}{FieldSeparator}{scope}{FieldSeparator}{DigestOf(binding)}{FieldSeparator}{position}");

        return ToBase64Url(Encoding.UTF8.GetBytes(body + FieldSeparator + DigestOf(body)));
    }

    /// <summary>
    /// Verifies <paramref name="cursor"/> against <paramref name="scope"/> and
    /// <paramref name="binding"/> and recovers its position.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> with <paramref name="position"/> set, or <see langword="false"/> with
    /// <paramref name="rejection"/> naming why. <b>Never throws</b> — every input here is
    /// caller-supplied text, and a tool's "never crash on bad input" contract has to hold for a
    /// cursor exactly as it does for a path.
    /// </returns>
    /// <param name="cursor">
    /// The caller's cursor. <see langword="null"/> or blank is <see cref="CursorRejection.Malformed"/>,
    /// not "no cursor": ABSENCE is the calling tool's own question, answered before it gets here, so
    /// that a caller who sent a whitespace string is told it was rejected rather than silently served
    /// page one.
    /// </param>
    public static bool TryDecode(
        string? cursor,
        string scope,
        string binding,
        out long position,
        out CursorRejection rejection)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(binding);

        position = 0;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxCursorChars)
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        if (!TryFromBase64Url(cursor, out var bytes))
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        string payload;
        try
        {
            // Strict UTF-8: a byte sequence that is not valid UTF-8 is a tampered cursor, not text
            // to salvage with replacement characters (which would then simply fail the field split
            // below — this is the same answer, reached honestly).
            payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (ArgumentException)
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        var fields = payload.Split(FieldSeparator);
        if (fields.Length != 5)
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        // INTEGRITY FIRST, before any field is interpreted — the fix for a real defect this type's
        // first version had, caught by OpaqueCursorContract.AssertTamperingIsRefused: with the digest
        // covering only the BINDING, flipping a character of the position field decoded cleanly to a
        // DIFFERENT position (measured: 1234 became 234), and truncating the base64 tail silently
        // dropped a payload byte and was accepted. A caller would then have been served a
        // plausible-looking page from the wrong offset, with nothing to detect it by.
        //
        // This is a CHECKSUM, not a MAC — there is no server secret, so it detects corruption and
        // accidental tampering, not forgery. That is the right strength for what a cursor is: see
        // this file's header for why forging one buys a caller nothing they could not get by calling
        // the tool with different arguments. Do not upgrade it to an HMAC without first establishing
        // that a cursor has become security-relevant, which today it is not.
        var body = string.Join(FieldSeparator, fields[..4]);
        if (!string.Equals(fields[4], DigestOf(body), StringComparison.Ordinal))
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        // Version is checked FIRST among the fields: a payload from a future layout may legitimately
        // carry five fields that mean something else, and reporting "your filters changed" about it
        // would send a caller chasing the wrong problem.
        if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            rejection = CursorRejection.Malformed;
            return false;
        }

        if (version != FormatVersion)
        {
            rejection = CursorRejection.UnsupportedVersion;
            return false;
        }

        if (!string.Equals(fields[1], scope, StringComparison.Ordinal))
        {
            rejection = CursorRejection.ScopeMismatch;
            return false;
        }

        // Fixed-length, lowercase-hex on both sides, so a constant-time comparison would buy
        // nothing here (there is no secret to leak — see the header) and ordinal equality is the
        // honest expression of the check.
        if (!string.Equals(fields[2], DigestOf(binding), StringComparison.Ordinal))
        {
            rejection = CursorRejection.FilterMismatch;
            return false;
        }

        if (!long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var decoded))
        {
            // NumberStyles.None rejects a leading sign, so a negative position cannot parse at all.
            rejection = CursorRejection.Malformed;
            return false;
        }

        position = decoded;
        rejection = CursorRejection.None;
        return true;
    }

    /// <summary>
    /// The one-line, caller-facing explanation for <paramref name="rejection"/> — shared so
    /// get_run_events and list_runs cannot describe the same refusal differently.
    /// </summary>
    /// <param name="toolName">The tool the message should tell the caller to re-call.</param>
    public static string DescribeRejection(CursorRejection rejection, string toolName) => rejection switch
    {
        CursorRejection.UnsupportedVersion =>
            $"The 'cursor' was minted by a different version of this server and cannot be used. Call "
            + $"{toolName} again without 'cursor' to restart from the first page.",
        CursorRejection.ScopeMismatch =>
            $"The 'cursor' was minted by a different tool and does not address {toolName}'s results. "
            + $"Pass only a 'nextCursor' that {toolName} itself returned, or omit it to start from the "
            + "first page.",
        CursorRejection.FilterMismatch =>
            $"The 'cursor' was minted under different arguments than this call sends, so the page it "
            + $"points at is not a continuation of these results. Keep the filters identical while "
            + $"paging, or omit 'cursor' to restart from the first page of the new filters.",
        _ =>
            $"The 'cursor' is not a cursor {toolName} issued (it is malformed or was altered). Pass "
            + $"back a 'nextCursor' exactly as {toolName} returned it — it is an opaque token, not a "
            + "value to construct — or omit it to start from the first page.",
    };

    /// <summary>The first <see cref="DigestChars"/> hex characters of SHA-256 over <paramref name="value"/>.</summary>
    /// <remarks>
    /// <see langword="internal"/> rather than private for exactly ONE consumer:
    /// <c>OpaqueCursorTests</c> constructs a cursor carrying a HYPOTHETICAL FUTURE
    /// <see cref="FormatVersion"/>, which is the only well-formed cursor that cannot be produced
    /// through <see cref="Encode"/> — and, since the integrity digest covers the version field, it
    /// cannot be assembled without this. Nothing in <c>src/</c> calls it from outside this type.
    /// </remarks>
    internal static string DigestOf(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..DigestChars];
    }

    /// <summary>
    /// RFC 4648 §5 base64url, unpadded. Hand-rolled because <c>System.Buffers.Text.Base64Url</c> is
    /// .NET 9+ and this project targets net8.0 (see <c>global.json</c> / <c>Directory.Build.props</c>).
    /// </summary>
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Reverses <see cref="ToBase64Url"/>, returning <see langword="false"/> for anything that is not valid base64url.</summary>
    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];

        // Reject the standard-base64 alphabet explicitly rather than letting Convert accept it: a
        // string carrying '+' or '/' is not something this type ever minted, and quietly decoding it
        // would widen what counts as "a cursor this server issued".
        foreach (var c in value)
        {
            var isBase64UrlChar = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!isBase64UrlChar)
            {
                return false;
            }
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - (standard.Length % 4)) % 4;
        if (padding == 3)
        {
            // A base64 quantum is never one character long, so this length can never have been
            // produced by ToBase64Url.
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(standard + new string('=', padding));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
