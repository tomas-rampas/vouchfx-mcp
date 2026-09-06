using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// The reusable contract every paginated tool's cursor must satisfy, written ONCE and driven from
/// each tool's own test class — the sprint's exit checklist requires "one cursor implementation,
/// verified by a shared unit-test fixture, not two".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shared fixture rather than one test class per tool.</b> <c>get_run_events</c>
/// (US-S3-05) and <c>list_runs</c> (US-S3-03) page over completely different data with completely
/// different filters, but they share <see cref="OpaqueCursor"/> verbatim. If each wrote its own
/// cursor tests, the two suites would drift — one would grow a case the other never checks — and
/// the first divergence in behaviour would show up as a bug report rather than a red test. Driving
/// both from these methods means adding a case here strengthens BOTH tools at once.
/// </para>
/// <para>
/// <b>How to use it from <c>list_runs</c>' tests (US-S3-03):</b> call each method with
/// <c>CursorScopes.ListRuns</c> and two bindings composed from that tool's OWN filter arguments —
/// one "these filters" and one "different filters" — plus, for
/// <see cref="AssertScopeIsSinglePurpose"/>, the other tool's scope. Nothing here knows or cares
/// what a binding is made of; that is each tool's decision (see
/// <c>GetRunEventsOrchestrator.ComposeBinding</c> for the first one).
/// </para>
/// </remarks>
internal static class OpaqueCursorContract
{
    /// <summary>A cursor round-trips its position under the scope and binding it was minted with.</summary>
    public static void AssertRoundTrips(string scope, string binding)
    {
        foreach (var position in new long[] { 0, 1, 42, int.MaxValue, long.MaxValue })
        {
            var cursor = OpaqueCursor.Encode(scope, binding, position);

            Assert.True(
                OpaqueCursor.TryDecode(cursor, scope, binding, out var decoded, out var rejection),
                $"A cursor this server just minted for position {position} was refused ({rejection}).");
            Assert.Equal(CursorRejection.None, rejection);
            Assert.Equal(position, decoded);
        }
    }

    /// <summary>
    /// A cursor is opaque: it never leaks its scope, its position, or the binding's inputs as
    /// readable text a host could come to depend on.
    /// </summary>
    public static void AssertOpaque(string scope, string binding, long position, params string[] mustNotAppear)
    {
        var cursor = OpaqueCursor.Encode(scope, binding, position);

        // base64url alphabet only: no padding, no '+', no '/', nothing a URL or a JSON string has to
        // escape. A host that round-trips a cursor through its own storage must get it back intact.
        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);

        Assert.DoesNotContain(scope, cursor, StringComparison.Ordinal);
        Assert.DoesNotContain(position.ToString(System.Globalization.CultureInfo.InvariantCulture), cursor, StringComparison.Ordinal);
        foreach (var secret in mustNotAppear)
        {
            Assert.DoesNotContain(secret, cursor, StringComparison.Ordinal);
        }
    }

    /// <summary>A cursor minted under one set of filters is REFUSED under another — never silently misapplied.</summary>
    public static void AssertFilterBindingIsEnforced(string scope, string binding, string differentBinding)
    {
        Assert.NotEqual(binding, differentBinding);

        var cursor = OpaqueCursor.Encode(scope, binding, position: 7);

        Assert.False(
            OpaqueCursor.TryDecode(cursor, scope, differentBinding, out var position, out var rejection),
            "A cursor minted under different filters was accepted — the page it points at is not a "
            + "continuation of these results.");
        Assert.Equal(CursorRejection.FilterMismatch, rejection);

        // The position is not leaked on the refusal path either: a rejected cursor yields nothing a
        // caller could act on.
        Assert.Equal(0, position);
    }

    /// <summary>A cursor minted by one tool is refused by another — scopes make a cursor single-purpose.</summary>
    public static void AssertScopeIsSinglePurpose(string scope, string otherScope, string binding)
    {
        Assert.NotEqual(scope, otherScope);

        var foreign = OpaqueCursor.Encode(otherScope, binding, position: 3);

        Assert.False(OpaqueCursor.TryDecode(foreign, scope, binding, out _, out var rejection));
        Assert.Equal(CursorRejection.ScopeMismatch, rejection);
    }

    /// <summary>
    /// Every way a cursor can be malformed is REFUSED — never thrown for, and never silently treated
    /// as "start again from the first page", which would hand a caller a duplicate page dressed as a
    /// continuation.
    /// </summary>
    public static void AssertMalformedInputIsRefusedWithoutThrowing(string scope, string binding)
    {
        var valid = OpaqueCursor.Encode(scope, binding, position: 5);

        var malformed = new List<string>
        {
            string.Empty,
            "   ",
            "not-a-cursor",
            "!!!!",       // outside the base64url alphabet
            valid + "=",  // padding this encoder never emits
            valid[..^1],  // truncated: one base64 character short
            "A" + valid,  // prefixed
            new string('A', OpaqueCursor.MaxCursorChars + 1),
        };

        // The standard-base64 alphabet is refused outright, so a cursor cannot arrive in a form this
        // encoder never mints. Tested with literals rather than by substituting '+'/'/' into a real
        // cursor, because MEASURED, that substitution is always a no-op: a cursor's payload is
        // lowercase hex, digits, '|' and a scope, and that byte range never produces base64 index 62
        // or 63. Mutating a real cursor therefore yields the cursor itself, and the case silently
        // asserted that the encoder accepts its own output. (Found exactly that way — first by the
        // case passing the valid cursor in, then by a search for a mutable position finding none.)
        malformed.Add("YWJj+ZGVm");
        malformed.Add("YWJj/ZGVm");

        foreach (var candidate in malformed)
        {
            var accepted = OpaqueCursor.TryDecode(candidate, scope, binding, out var position, out var rejection);

            // A tampered cursor may land on any refusal reason — flipping a base64 character can
            // corrupt the version, the scope, or the digest — so this asserts REFUSAL, not which
            // reason. The point is that nothing is ever accepted and nothing ever throws.
            Assert.False(accepted, $"'{candidate}' was accepted as a cursor.");
            Assert.NotEqual(CursorRejection.None, rejection);
            Assert.Equal(0, position);
        }
    }

    /// <summary>Flipping any single character of a valid cursor refuses it, rather than decoding a different position.</summary>
    public static void AssertTamperingIsRefused(string scope, string binding)
    {
        var valid = OpaqueCursor.Encode(scope, binding, position: 1234);

        for (var i = 0; i < valid.Length; i++)
        {
            var chars = valid.ToCharArray();
            chars[i] = chars[i] == 'A' ? 'B' : 'A';
            var tampered = new string(chars);
            if (string.Equals(tampered, valid, StringComparison.Ordinal))
            {
                continue;
            }

            var accepted = OpaqueCursor.TryDecode(tampered, scope, binding, out var position, out _);

            // THE case that found a real defect: before the payload carried an integrity digest, a
            // single flipped character in the position field decoded cleanly to a DIFFERENT position
            // (1234 became 234), and a caller would have been served a plausible page from the wrong
            // offset with nothing to detect it by.
            //
            // Accepting a flip is tolerable in exactly one situation: the flipped character encoded
            // only unused trailing bits of the base64 quantum, so the decoded BYTES — and therefore
            // the position — are unchanged. That is not tampering succeeding; it is two strings
            // denoting the same payload. Anything else must be refused.
            if (accepted)
            {
                Assert.Equal(1234, position);
            }
        }
    }
}
