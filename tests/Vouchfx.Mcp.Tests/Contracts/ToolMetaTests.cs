using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Contracts;

/// <summary>
/// Covers <see cref="ToolMeta"/>, its source-generated <see cref="ToolMetaJsonContext"/>, and the
/// three sources <c>ToolMetaProvider</c> composes its fields from (Sprint 1 / US-S1-02). The
/// complementary proof that the stamp actually reaches a host on all twelve tools is
/// <c>RealToolMetaMcpTests</c>; that it survives the REAL serializer path is
/// <c>Tools/StructuredToolResultTests</c>.
/// </summary>
public class ToolMetaTests
{
    /// <summary>Matches the options the envelope-size probes elsewhere in this suite use.</summary>
    private static readonly JsonSerializerOptions EnvelopeProbeOptions = new(JsonSerializerDefaults.Web);

    // ── Round-trip and wire names through the source-generated context ─────────────────────────

    [Fact]
    public void RoundTrip_ViaSourceGeneratedContext_ReturnsEqualInstance()
    {
        var original = new ToolMeta("v1", "0.1.0", @"C:\vouchfx");

        var json = JsonSerializer.Serialize(original, ToolMetaJsonContext.Default.ToolMeta);
        var roundTripped = JsonSerializer.Deserialize(json, ToolMetaJsonContext.Default.ToolMeta);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Serialize_ViaSourceGeneratedContext_EmitsExactlyTheThreeCamelCaseFields()
    {
        var meta = new ToolMeta("v1", "0.1.0", "/workspace");

        var json = JsonSerializer.Serialize(meta, ToolMetaJsonContext.Default.ToolMeta);

        // Asserted as one exact string, not three Contains() probes: field ORDER and the absence of
        // any fourth field are both part of the contract a host reads, and only an exact comparison
        // pins either.
        Assert.Equal(
            """{"schemaVersion":"v1","serverVersion":"0.1.0","workspaceRoot":"/workspace"}""",
            json);
    }

    // ── Field sourcing: every value comes from its stated source, none is hardcoded here ───────

    [Fact]
    public void Current_SchemaVersion_IsReadFromTheVendoredSchemasOwnVersionMarker()
    {
        // Not compared against a literal "v1": the point of US-S1-02's acceptance criterion is that
        // this value FOLLOWS the vendored schema (which is drift-gated against the pinned engine
        // commit), so the assertion has to be against the schema's own marker. That the marker is
        // still where VendoredSchemaVersion looks for it is pinned separately, against the
        // repo-checked-in file rather than the embedded copy, by
        // VendoredArtefactsTests.VendoredSchema_StillDeclaresItsOwnLanguageVersionMarker.
        Assert.Equal(VendoredSchemaVersion.Value, ToolMetaProvider.Current.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(ToolMetaProvider.Current.SchemaVersion));
    }

    [Fact]
    public void Current_ServerVersion_IsTheSameValueReportedAsServerInfoVersion()
    {
        // ServerIdentity.Version is what VouchfxMcpServerRegistration copies into
        // serverInfo.version (McpServerSkeletonTests pins that end of the equality, including the
        // literal "0.1.0" from the csproj). RealToolMetaMcpTests closes the loop over the actual
        // wire, comparing meta.serverVersion against the handshake value a real client received.
        Assert.Equal(ServerIdentity.Version, ToolMetaProvider.Current.ServerVersion);
    }

    [Fact]
    public void Current_WorkspaceRoot_IsTheProcessResolvedBaseDirectory_WhenNoWorkspaceIsConfigured()
    {
        // The unconfigured fallback (US-S3-08 kept it for exactly the callers who never passed
        // --workspace; the CONFIGURED branch is asserted by RealWorkspaceProcessTests, which spawns
        // a real server with a real flag). Asserted as the CANONICALISED base directory (no trailing
        // separator), because that canonicalisation is the part a host can observe.
        //
        // This test process never publishes a startup workspace — see ToolMetaProvider's remarks on
        // why McpTestHarness deliberately does not — so ToolMetaProvider.Current is the fallback here
        // by construction, not by luck.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            ToolMetaProvider.Current.WorkspaceRoot);
        Assert.False(
            Path.EndsInDirectorySeparator(ToolMetaProvider.Current.WorkspaceRoot),
            "workspaceRoot must be canonicalised to one consistent form on the wire.");
    }

    // ── Wire cost: MEASURED, never estimated (US-S1-02's Sprint-4 baseline) ────────────────────

    /// <summary>
    /// The Sprint-1 baseline for <see cref="ToolMeta"/>'s wire cost, so Sprint 4's response-budget
    /// re-measurement starts from a recorded number rather than a fresh guess.
    /// </summary>
    /// <remarks>
    /// <b>MEASURED by serialising a populated instance, not estimated</b> — every constant below is
    /// what this test actually observed, which is why the test asserts them rather than merely
    /// documenting them (an estimate written in a comment rots silently; an asserted measurement
    /// fails the build when it stops being true).
    /// <list type="bullet">
    /// <item><description>
    /// <b>65 UTF-8 bytes</b> — the fixed structural cost: the three field names, the two real
    /// values this server emits today (<c>"v1"</c> and <c>"0.1.0"</c>), the braces, colons and
    /// commas, with an EMPTY <c>workspaceRoot</c>. Everything beyond this is workspaceRoot's own
    /// JSON-escaped length.
    /// </description></item>
    /// <item><description>
    /// <b>76 UTF-8 bytes</b> — a representative populated instance
    /// (<c>workspaceRoot</c> = <c>C:\vouchfx</c>, which escapes to 11 characters plus its two
    /// quotes): 65 - 2 + 13.
    /// </description></item>
    /// <item><description>
    /// <b>+84 UTF-8 bytes per serialised copy</b> — what the stamp adds to one JSON rendering of a
    /// result: 8 bytes for the <c>,"meta":</c> property name plus the 76 above.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>The full-envelope cost is NOT twice that, and an earlier version of this remark saying so
    /// was wrong</b> (a review fix — the reviewer rebuilt the maximal case and falsified it).
    /// <c>StructuredToolResult</c> does carry the result twice, but the text <c>Content</c> copy is
    /// a JSON-escaped STRING inside the response, so its quotes and backslashes are re-escaped and
    /// it costs more than the <c>StructuredContent</c> copy of the same bytes. MEASURED end-to-end
    /// through the real serializer, a 141-byte stamp (this machine's <c>workspaceRoot</c>, a
    /// 62-character path) added <b>384 bytes</b> to the full <c>CallToolResult</c>, against the
    /// <c>2 × (8 + 141) = 298</c> the naive doubling predicts — an understatement of <b>28.9%</b>.
    /// <see cref="WireCost_MeasuredSprint1Baseline"/>'s final assertion pins that inequality
    /// (measured &gt; naive) rather than a literal, since the absolute number moves with
    /// <c>workspaceRoot</c>'s length.
    /// </para>
    /// <para>
    /// A real deployment's stamp sits above the 76: a production <c>workspaceRoot</c> is a full
    /// installed-tool path, typically 40-90 characters, putting one stamp near 105-155 bytes and its
    /// real envelope contribution near 290-420. For where that sits against the response budget —
    /// and why the budget's own 64 KB claim is separately and pre-existingly broken — see
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>' remarks and
    /// <c>ExplainRunOrchestratorTests.MaximalTierZeroDiagnosis_FitsTheBudgetButItsEnvelopeExceedsTheCap</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void WireCost_MeasuredSprint1Baseline()
    {
        const int MetaPropertyOverheadBytes = 8; // `,"meta":`

        var fixedCostOnly = new ToolMeta("v1", "0.1.0", string.Empty);
        var representative = new ToolMeta("v1", "0.1.0", @"C:\vouchfx");

        Assert.Equal(65, MeasureUtf8Bytes(fixedCostOnly));
        Assert.Equal(76, MeasureUtf8Bytes(representative));
        Assert.Equal(84, MetaPropertyOverheadBytes + MeasureUtf8Bytes(representative));

        // The correction the review forced: the stamp's REAL envelope cost exceeds the naive
        // "same bytes, paid twice" formula, because the duplicated text block is escaped. Asserted
        // as an inequality against the live serializer -- the absolute delta depends on this
        // machine's workspaceRoot length, but that it EXCEEDS the naive doubling does not.
        var stampBytes = MeasureUtf8Bytes(ToolMetaProvider.Current);
        var naiveDoubling = 2 * (MetaPropertyOverheadBytes + stampBytes);
        var measuredEnvelopeDelta = MeasureEnvelopeStampDelta();

        Assert.True(
            measuredEnvelopeDelta > naiveDoubling,
            $"Expected the stamp's real envelope cost to exceed the naive doubling ({naiveDoubling} bytes) "
            + $"because the duplicated text block is JSON-escaped, but measured {measuredEnvelopeDelta}.");
    }

    /// <summary>
    /// The stamp's cost in a full, serialised <c>CallToolResult</c>: the same payload measured with
    /// the stamp (production behaviour) and without it (the pre-US-S1-02 shape, rebuilt by hand).
    /// </summary>
    private static int MeasureEnvelopeStampDelta()
    {
        var payload = new ValidateSuiteResult(true, []);

        var withStamp = JsonSerializer.SerializeToUtf8Bytes(
            StructuredToolResult.Success(payload), EnvelopeProbeOptions).Length;

        var payloadJson = JsonSerializer.Serialize(
            payload, typeof(ValidateSuiteResult), StructuredToolResult.Options);
        var withoutStamp = JsonSerializer.SerializeToUtf8Bytes(
            new CallToolResult
            {
                Content = [new TextContentBlock { Text = payloadJson }],
                StructuredContent = JsonSerializer.SerializeToElement(
                    payload, typeof(ValidateSuiteResult), StructuredToolResult.Options),
            },
            EnvelopeProbeOptions).Length;

        return withStamp - withoutStamp;
    }

    private static int MeasureUtf8Bytes(ToolMeta meta) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(meta, ToolMetaJsonContext.Default.ToolMeta));
}
