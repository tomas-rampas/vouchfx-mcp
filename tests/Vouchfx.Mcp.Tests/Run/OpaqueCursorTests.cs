using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="OpaqueCursor"/>'s own unit tests (US-S3-05) — the shared
/// <see cref="OpaqueCursorContract"/> driven for <c>get_run_events</c>' scope, plus the cases that
/// belong to the encoder itself rather than to any one tool.
/// </summary>
/// <remarks>
/// US-S3-03's <c>list_runs</c> tests drive the SAME <see cref="OpaqueCursorContract"/> methods with
/// their own scope and bindings. That is the whole point of the split: this class is thin because
/// the contract lives where both tools can reach it.
/// </remarks>
public class OpaqueCursorTests
{
    /// <summary>
    /// The SECOND paginated tool's real scope — US-S3-03's <c>list_runs</c> landed, so the
    /// placeholder literal this constant once was ("some-other-paginated-tool", kept while the real
    /// constant would have been dead code) is now the genuine cross-tool value, exactly as its
    /// original comment promised. <c>ListRunsOrchestratorTests.Cursor_ScopeIsSinglePurpose</c>
    /// asserts the same rejection from the other tool's side.
    /// </summary>
    private const string OtherToolScope = CursorScopes.ListRuns;

    private static readonly string Binding = OpaqueCursor.ComposeBinding("run-abc", "step-attempt", "verify-order");
    private static readonly string DifferentBinding = OpaqueCursor.ComposeBinding("run-abc", "step-completed", "verify-order");

    [Fact]
    public void ACursor_RoundTripsItsPosition() =>
        OpaqueCursorContract.AssertRoundTrips(CursorScopes.RunEvents, Binding);

    [Fact]
    public void ACursor_IsOpaqueBase64UrlThatLeaksNeitherScopeNorPositionNorFilters() =>
        OpaqueCursorContract.AssertOpaque(
            CursorScopes.RunEvents, Binding, position: 987_654, mustNotAppear: ["run-abc", "step-attempt", "verify-order"]);

    [Fact]
    public void ACursorMintedUnderDifferentFilters_IsRefusedRatherThanMisapplied() =>
        OpaqueCursorContract.AssertFilterBindingIsEnforced(CursorScopes.RunEvents, Binding, DifferentBinding);

    [Fact]
    public void ACursorMintedByAnotherTool_IsRefused() =>
        OpaqueCursorContract.AssertScopeIsSinglePurpose(CursorScopes.RunEvents, OtherToolScope, Binding);

    [Fact]
    public void EveryMalformedCursor_IsRefusedWithoutThrowing() =>
        OpaqueCursorContract.AssertMalformedInputIsRefusedWithoutThrowing(CursorScopes.RunEvents, Binding);

    [Fact]
    public void TamperingWithAnyCharacter_NeverYieldsADifferentAcceptedPosition() =>
        OpaqueCursorContract.AssertTamperingIsRefused(CursorScopes.RunEvents, Binding);

    [Fact]
    public void ANullCursor_IsRefusedAsMalformedRatherThanTreatedAsAbsent()
    {
        // Absence is the CALLING TOOL's question, answered before TryDecode is reached — see its
        // remarks. A caller who sent whitespace must be told it was rejected, never quietly served
        // page one.
        Assert.False(OpaqueCursor.TryDecode(null, CursorScopes.RunEvents, Binding, out _, out var rejection));
        Assert.Equal(CursorRejection.Malformed, rejection);
    }

    [Fact]
    public void ComposeBinding_DistinguishesArgumentListsThatConcatenateIdentically()
    {
        // The length prefixes exist for exactly this: without them, ["ab","c"] and ["a","bc"] would
        // bind to the same digest and one caller's cursor would be accepted for the other's filters.
        Assert.NotEqual(OpaqueCursor.ComposeBinding("ab", "c"), OpaqueCursor.ComposeBinding("a", "bc"));
    }

    [Fact]
    public void ComposeBinding_DistinguishesAnAbsentArgumentFromAnEmptyOne()
    {
        // "no stepId" and "stepId: ''" select different result sets, so they must not share a cursor.
        Assert.NotEqual(OpaqueCursor.ComposeBinding("run-abc", null), OpaqueCursor.ComposeBinding("run-abc", string.Empty));
    }

    [Fact]
    public void ComposeBinding_IsStableAcrossCallsAndProcesses()
    {
        // A cursor has to survive being stored by a host and handed back later. Nothing in the
        // binding may depend on run-to-run state (a hash seed, an object identity, a clock).
        var first = OpaqueCursor.Encode(CursorScopes.RunEvents, OpaqueCursor.ComposeBinding("run-abc", "x"), 11);
        var second = OpaqueCursor.Encode(CursorScopes.RunEvents, OpaqueCursor.ComposeBinding("run-abc", "x"), 11);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ACursorFromADifferentFormatVersion_IsRefusedAsUnsupportedRatherThanMisread()
    {
        // Hand-built at the payload level — the ONLY place a test may know the layout, and the reason
        // this case lives here rather than in the shared contract. A future server bumping
        // FormatVersion must refuse today's cursors by VERSION, not by decoding them under a layout
        // that has since changed meaning; and because the integrity digest covers the version field,
        // a future-version cursor cannot simply be a character edit of a real one (it would be
        // rejected as Malformed first, which would make this test pass for the wrong reason).
        var todaysCursor = OpaqueCursor.Encode(CursorScopes.RunEvents, Binding, position: 9);
        var payload = System.Text.Encoding.UTF8.GetString(FromBase64Url(todaysCursor));
        var fields = payload.Split('|');
        Assert.Equal(5, fields.Length);
        Assert.Equal(OpaqueCursor.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[0]);

        var futureBody = $"{OpaqueCursor.FormatVersion + 1}|{fields[1]}|{fields[2]}|{fields[3]}";
        var futureCursor = ToBase64Url(System.Text.Encoding.UTF8.GetBytes(
            futureBody + "|" + OpaqueCursor.DigestOf(futureBody)));

        Assert.False(OpaqueCursor.TryDecode(futureCursor, CursorScopes.RunEvents, Binding, out _, out var rejection));
        Assert.Equal(CursorRejection.UnsupportedVersion, rejection);
    }

    [Fact]
    public void Encode_RefusesANegativePosition() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OpaqueCursor.Encode(CursorScopes.RunEvents, Binding, -1));

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard + new string('=', (4 - (standard.Length % 4)) % 4));
    }
}
