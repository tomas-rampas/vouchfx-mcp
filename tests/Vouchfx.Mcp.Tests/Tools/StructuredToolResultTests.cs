using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Tools;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Tools;

/// <summary>
/// Covers <see cref="StructuredToolResult"/> — the single pathway all nine tools use to reach the
/// wire — for US-S1-02: the <c>meta</c> stamp it attaches, and the
/// <c>TypeInfoResolverChain</c> that makes the <c>Contracts/</c> types' source-generated contexts
/// actually apply on that pathway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests exist at all</b> (the US-S1-03 review finding this story resolves): before
/// the chain, <see cref="StructuredToolResult"/>'s options carried no <c>TypeInfoResolver</c>, so
/// every payload was resolved REFLECTIVELY and <see cref="VfxErrorJsonContext"/>/
/// <see cref="DiagnosticJsonContext"/> were bypassed on the only path that reaches a host. Their
/// own unit tests serialise through <c>Context.Default.&lt;Type&gt;</c> and so could never have
/// caught that. Everything here therefore goes through <see cref="StructuredToolResult.Success"/>
/// — the real path — never through a context directly.
/// </para>
/// <para>
/// <b>Measured, not assumed</b>: options-level settings and a context's own
/// <c>JsonSourceGenerationOptions</c> do NOT compose the way they look like they should. A
/// context's <c>DefaultIgnoreCondition</c> does not travel with its metadata into a different
/// <see cref="JsonSerializerOptions"/> — that was measured during this story and is why every
/// optional field on the <c>Contracts/</c> shapes carries its own
/// <c>[JsonIgnore(Condition = WhenWritingNull)]</c>. <see cref="RealWirePath_NestedVfxError_OmitsNullOptionalFields"/>
/// is the regression that keeps it true.
/// </para>
/// </remarks>
public class StructuredToolResultTests
{
    /// <summary>
    /// A stand-in for the US-S1-04 payload shape that will nest a <see cref="VfxError"/> inside a
    /// tool result. Declared here rather than waiting for that story because the resolver hole this
    /// class guards is only observable through a nested contract type: a payload type resolved by
    /// the reflection fallback whose CHILD must still be resolved by the source-generated context.
    /// </summary>
    private sealed record NestedErrorProbePayload(bool Ok, VfxError Error);

    // ── The resolver chain resolves what it claims to resolve ──────────────────────────────────

    [Theory]
    [InlineData(typeof(ToolMeta), typeof(ToolMetaJsonContext))]
    [InlineData(typeof(VfxError), typeof(VfxErrorJsonContext))]
    [InlineData(typeof(Diagnostic), typeof(DiagnosticJsonContext))]
    [InlineData(typeof(DiagnosticLocation), typeof(DiagnosticJsonContext))]
    [InlineData(typeof(DiagnosticFix), typeof(DiagnosticJsonContext))]
    public void Options_ResolveContractTypes_ThroughTheirSourceGeneratedContext(Type contractType, Type expectedResolver)
    {
        // JsonTypeInfo.OriginatingResolver names which resolver in the chain actually produced the
        // metadata — the direct evidence that the source-generated context won, rather than an
        // inference from output that the reflection fallback might happen to match.
        var typeInfo = StructuredToolResult.Options.GetTypeInfo(contractType);

        Assert.IsType(expectedResolver, typeInfo.OriginatingResolver);
    }

    [Fact]
    public void Options_ResolveOrdinaryToolPayloads_ThroughTheReflectionFallback()
    {
        // The other half of the chain's contract: the nine existing payload types must keep being
        // resolved exactly as they were before the chain existed (an unset TypeInfoResolver IS a
        // DefaultJsonTypeInfoResolver), which is what makes the golden below byte-identical.
        var typeInfo = StructuredToolResult.Options.GetTypeInfo(typeof(ValidateSuiteResult));

        Assert.IsType<DefaultJsonTypeInfoResolver>(typeInfo.OriginatingResolver);
    }

    // ── The REAL wire path honours the contexts' null-omission and camelCase ───────────────────

    [Fact]
    public void RealWirePath_NestedVfxError_OmitsNullOptionalFields()
    {
        // The regression this story's directive names: a VfxError whose optional fields are null
        // must reach the wire WITHOUT them. Serialised through StructuredToolResult, not through
        // VfxErrorJsonContext.Default.VfxError — the whole point is that the two agree.
        var payload = new NestedErrorProbePayload(
            Ok: false,
            Error: new VfxError("VFX-E-1001", "Path outside workspace", retryable: false));

        var text = TextOf(StructuredToolResult.Success(payload));

        // Scoped to the PAYLOAD portion, not the whole result: `meta.workspaceRoot` is a machine
        // path, and a "no nulls anywhere" scan over the full string would be checking that path's
        // characters too — which is both unrelated to the property under test and a latent
        // false-failure on any machine whose install path happens to contain the substring.
        var payloadPortion = PayloadPortionOf(text);

        Assert.DoesNotContain("details", payloadPortion, StringComparison.Ordinal);
        Assert.DoesNotContain("docsUrl", payloadPortion, StringComparison.Ordinal);
        Assert.DoesNotContain("null", payloadPortion, StringComparison.Ordinal);
        Assert.Contains(
            """"error":{"code":"VFX-E-1001","message":"Path outside workspace","retryable":false}"""",
            payloadPortion,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RealWirePath_NestedDiagnostic_OmitsNullOptionalFieldsAndKeepsCamelCase()
    {
        var diagnostic = new Diagnostic(
            "VFX-D-1001",
            "warning",
            "Step has no assertion",
            location: new DiagnosticLocation("suite.e2e.yaml", 4, 2, EndLine: null, EndColumn: null),
            path: null,
            fix: new DiagnosticFix("Add an assertion", Replacement: null),
            docsUrl: null);

        var text = TextOf(StructuredToolResult.Success(diagnostic));

        // Present-but-nested optional shapes keep their camelCase names; absent ones vanish
        // entirely, at BOTH nesting levels (Diagnostic's own and DiagnosticLocation's).
        Assert.Contains(""""location":{"file":"suite.e2e.yaml","line":4,"column":2}"""", text, StringComparison.Ordinal);
        Assert.Contains(""""fix":{"description":"Add an assertion"}"""", text, StringComparison.Ordinal);
        Assert.DoesNotContain("endLine", text, StringComparison.Ordinal);
        Assert.DoesNotContain("endColumn", text, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement", text, StringComparison.Ordinal);
        Assert.DoesNotContain("docsUrl", text, StringComparison.Ordinal);
        Assert.DoesNotContain(""""path":"""", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RealWirePath_MetaStamp_IsCamelCaseAndCarriesTheProvenanceFields()
    {
        var structured = StructuredContentOf(StructuredToolResult.Success(new ValidateSuiteResult(true, [])));

        var meta = structured.GetProperty("meta");
        Assert.Equal(ToolMetaProvider.Current.SchemaVersion, meta.GetProperty("schemaVersion").GetString());
        Assert.Equal(ToolMetaProvider.Current.ServerVersion, meta.GetProperty("serverVersion").GetString());
        Assert.Equal(ToolMetaProvider.Current.WorkspaceRoot, meta.GetProperty("workspaceRoot").GetString());
        Assert.Equal(3, meta.EnumerateObject().Count());
    }

    // ── Golden: the resolver change must not have reshaped a single existing payload byte ──────

    /// <summary>
    /// The byte-for-byte regression guard the resolver change is accountable to: a representative
    /// existing tool payload (<c>validate_suite</c>'s) must serialise EXACTLY as it did before
    /// <see cref="StructuredToolResult"/> gained a <c>TypeInfoResolverChain</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected payload text below was MEASURED against the pre-change code (a throwaway probe
    /// run before the chain was introduced), not derived from the post-change code it now guards —
    /// which is what makes it a before/after comparison rather than a tautology.
    /// </para>
    /// <para>
    /// <c>ValidateSuiteResult</c> is the representative payload deliberately: it is the only shape
    /// combining a nested collection with optional fields that are genuinely null in normal use, so
    /// it pins BOTH halves of the chain's contract — reflection-resolved payloads keep the Web
    /// defaults' camelCase naming (<c>instancePath</c>), and, critically, they keep EMITTING their
    /// nulls. That second half is the trap: making the contexts' null-omission work by setting
    /// <c>DefaultIgnoreCondition</c> on the shared options would silently delete
    /// <c>"instancePath":null,"line":null,"column":null</c> from every <c>validate_suite</c>
    /// result, and this literal is what refuses that shortcut.
    /// </para>
    /// </remarks>
    [Fact]
    public void Golden_ExistingPayloadShape_IsUnchangedByTheResolverChain()
    {
        const string ExpectedPayloadJson =
            """{"valid":false,"errors":[{"kind":"schema","instancePath":"/steps/1","message":"Required properties are not present","line":12,"column":null},{"kind":"unknown-step-type","instancePath":null,"message":"Unknown step type","line":null,"column":null}]}""";

        var payload = new ValidateSuiteResult(
            Valid: false,
            Errors:
            [
                new SuiteValidationError("schema", "/steps/1", "Required properties are not present", 12, null),
                new SuiteValidationError("unknown-step-type", null, "Unknown step type", null, null),
            ]);

        var text = TextOf(StructuredToolResult.Success(payload));

        // The payload portion is the whole result minus the appended meta stamp: asserting on the
        // prefix (rather than on a re-serialisation of the payload alone) is what proves the bytes
        // a HOST receives are unchanged, not merely that some other serialisation of the same
        // object would be.
        var metaSuffix = ",\"meta\":" + JsonSerializer.Serialize(ToolMetaProvider.Current, ToolMetaJsonContext.Default.ToolMeta) + "}";
        Assert.EndsWith(metaSuffix, text, StringComparison.Ordinal);
        Assert.Equal(ExpectedPayloadJson, text[..^metaSuffix.Length] + "}");

        // 247 bytes: the measured pre-change size of this exact payload (see ToolMetaTests'
        // wire-cost remarks for the stamp's own measured cost on top of it).
        Assert.Equal(247, Encoding.UTF8.GetByteCount(ExpectedPayloadJson));
    }

    [Fact]
    public void Success_MetaStamp_CostsTheMeasuredEnvelopeBytes()
    {
        // Scope: this asserts the stamp's cost in ONE serialised rendering of the result -- the
        // text block's own length -- which is exactly `,"meta":` plus the meta object. It is NOT
        // the full-envelope cost: the result is carried twice, and the text copy is JSON-escaped
        // inside the response, so the envelope grows by MORE than double this (measured; see
        // ToolMetaTests.WireCost_MeasuredSprint1Baseline, which pins that inequality). Conflating
        // the two is the error this comment exists to prevent recurring.
        var payload = new ValidateSuiteResult(true, []);
        var metaBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(ToolMetaProvider.Current, ToolMetaJsonContext.Default.ToolMeta));

        var result = StructuredToolResult.Success(payload);
        var text = TextOf(result);

        const string BarePayloadJson = """{"valid":true,"errors":[]}""";
        const int MetaPropertyOverheadBytes = 8; // `,"meta":`

        Assert.Equal(
            Encoding.UTF8.GetByteCount(BarePayloadJson) + MetaPropertyOverheadBytes + metaBytes,
            Encoding.UTF8.GetByteCount(text));

        // The two copies are byte-identical — the premise
        // ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes is built on, restated here as a test
        // so a future "optimisation" that dropped one copy would have to face it. (That the two
        // copies are identical as JSON does NOT mean they cost the same on the wire: the text one
        // is escaped into a string when the CallToolResult is serialised. See the note above.)
        Assert.Equal(text, StructuredContentOf(result).GetRawText());
    }

    // ── Structural faults in a caller fail loudly rather than silently losing data ─────────────

    private static readonly int[] NonObjectPayload = [1, 2, 3];

    [Fact]
    public void Success_PayloadThatIsNotAJsonObject_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructuredToolResult.Success(NonObjectPayload));

        Assert.Contains("meta", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Success_PayloadCarryingItsOwnMetaProperty_ThrowsRatherThanEmittingADuplicateKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StructuredToolResult.Success(new CollidingProbePayload("x")));

        Assert.Contains("meta", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CollidingProbePayload), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_CarriesNoMetaStamp_AndStaysAPlainMessage()
    {
        // US-S1-02 scopes the stamp to SUCCESS results; the error path has no structured content to
        // attach it to until US-S1-04 gives it a VfxError shape of its own.
        var result = StructuredToolResult.Error("nope.nope is not a known step type");

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Equal("nope.nope is not a known step type", TextOf(result));
    }

    [Fact]
    public void Success_EmptyObjectPayload_StillGetsTheStamp()
    {
        // The degenerate end of the merge loop: a payload with no properties at all must produce a
        // well-formed object carrying only `meta`, not `{,"meta":…}` or a stray comma.
        var text = TextOf(StructuredToolResult.Success(new EmptyProbePayload()));

        Assert.Equal(
            "{\"meta\":" + JsonSerializer.Serialize(ToolMetaProvider.Current, ToolMetaJsonContext.Default.ToolMeta) + "}",
            text);
    }

    // ── Adversarial relay: engine evidence reaches the wire byte-identical ─────────────────────

    /// <summary>
    /// The rewrite in <c>SerialiseWithMeta</c> re-emits every payload property through a fresh
    /// <c>Utf8JsonWriter</c>, so it is the one place a hostile or merely awkward string could be
    /// re-escaped differently, mangled, or silently dropped. This pins that it is a faithful relay.
    /// </summary>
    /// <remarks>
    /// Routed through an <c>explain_run</c>-shaped payload deliberately: <c>observation</c> and
    /// <c>summary</c> carry ENGINE-AUTHORED evidence copied out of a run's event stream, which is
    /// the only content on this path an attacker (via a malicious service under test) has real
    /// influence over. <c>TextSanitiser</c> handles control characters at parse time by policy; this
    /// test covers what the SERIALISER must do regardless — including for a character the default
    /// <c>JavaScriptEncoder</c> escapes (<c>&lt;</c>, emitted as <c><</c>), which is also what
    /// makes it the encoder-fidelity guard for the writer options.
    /// </remarks>
    [Fact]
    public void RealWirePath_AdversarialEvidenceStrings_AreRelayedByteIdentically()
    {
        const string Adversarial =
            "quote:\" backslash:\\ slash:/ lt:< gt:> amp:& tab:\t nl:\n cr:\r nul:\0 " +
            "surrogate:\U0001F600 combining:é rtl:‮ bom:﻿";

        var payload = new EvidenceProbePayload("Fail", Adversarial);

        var result = StructuredToolResult.Success(payload);
        var text = TextOf(result);
        var structured = StructuredContentOf(result);

        // 1. Both wire copies agree exactly.
        Assert.Equal(text, structured.GetRawText());

        // 2. The value survives a round trip through the merge unchanged, character for character.
        using var reparsed = JsonDocument.Parse(text);
        Assert.Equal(Adversarial, reparsed.RootElement.GetProperty("observation").GetString());

        // 3. The rewrite used the SAME encoder the payload was serialised under (US-S1-02 review
        //    fix — the writer is now configured from Options): '<' must appear escaped, exactly as
        //    the default JavaScriptEncoder emits it, rather than raw from a default-constructed
        //    writer.
        Assert.Contains(@"lt:\u003C", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lt:<", text, StringComparison.Ordinal);

        // 4. Raw control characters never appear unescaped on the wire.
        Assert.DoesNotContain("\t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", text, StringComparison.Ordinal);

        // 5. The stamp still landed alongside all of that.
        Assert.Equal(
            ToolMetaProvider.Current.SchemaVersion,
            reparsed.RootElement.GetProperty("meta").GetProperty("schemaVersion").GetString());
    }

    private sealed record CollidingProbePayload([property: JsonPropertyName("meta")] string Meta);

    private sealed record EmptyProbePayload;

    /// <summary>An <c>explain_run</c>-shaped payload: a verdict plus engine-authored evidence text.</summary>
    private sealed record EvidenceProbePayload(string Verdict, string Observation);

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>
    /// The result text minus its appended <c>meta</c> stamp, closed back into a valid object — so an
    /// assertion about the PAYLOAD cannot accidentally be satisfied (or broken) by the machine-
    /// specific contents of <c>meta.workspaceRoot</c>.
    /// </summary>
    private static string PayloadPortionOf(string resultText)
    {
        var metaSuffix = ",\"meta\":"
            + JsonSerializer.Serialize(ToolMetaProvider.Current, ToolMetaJsonContext.Default.ToolMeta) + "}";

        Assert.EndsWith(metaSuffix, resultText, StringComparison.Ordinal);
        return resultText[..^metaSuffix.Length] + "}";
    }

    private static JsonElement StructuredContentOf(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");
}
