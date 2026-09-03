using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Builds a <see cref="CallToolResult"/> for a real (non-stub) tool handler: either a successful
/// structured result, or a tool-level error.
/// </summary>
/// <remarks>
/// <para>
/// Built explicitly rather than relying on <c>McpServerToolCreateOptions.UseStructuredContent</c>'s
/// return-type-based inference, so every real tool's JSON shape (both the <c>Content</c> text
/// block and <c>StructuredContent</c>) is produced the same explicit way regardless of what its
/// <c>Handle</c> method's C# return type happens to be — several handlers return either a result
/// payload or an error, and inference from a shared <c>object</c>-typed return doesn't carry a
/// useful output schema anyway.
/// </para>
/// <para>
/// <b>This is the ONE pathway all nine tools use to reach the wire</b> — which is why US-S1-02's
/// <c>meta</c> stamp is attached here (see <see cref="Success(object)"/>) instead of as a field on
/// each of the nine payload records: a per-payload field is nine places that can drift and a tenth
/// tool that can forget, whereas a choke point cannot be bypassed without deleting the only
/// mechanism a tool has for returning success at all.
/// </para>
/// </remarks>
internal static class StructuredToolResult
{
    /// <summary>
    /// The property name <c>meta</c> is written under. A payload that already carries a property of
    /// this name is rejected rather than silently duplicated or overwritten — see
    /// <see cref="SerialiseWithMeta"/>.
    /// </summary>
    private const string MetaPropertyName = "meta";

    /// <summary>
    /// The serializer options every tool payload — and <c>meta</c> itself — reaches the wire
    /// through. <see langword="internal"/> purely so tests can assert the resolver chain's SHAPE
    /// directly (which resolver produces which type's metadata), rather than only inferring it from
    /// serialised output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the resolver chain exists (a review fix from US-S1-03):</b> this type serialises via
    /// the <c>JsonSerializer.Serialize(object, Type, JsonSerializerOptions)</c> overload, which
    /// resolves metadata through <c>options</c>. Before this chain existed, <c>options</c> had NO
    /// <c>TypeInfoResolver</c> at all, so every payload — including, once US-S1-04 starts nesting
    /// them, <see cref="VfxError"/> and <see cref="Diagnostic"/> — was resolved REFLECTIVELY, and
    /// the source-generated contexts those types ship with were silently bypassed on the only path
    /// that actually reaches a host. Their tested behaviour would have held in their own unit tests
    /// and nowhere else. Chaining the contexts here is what makes those tests describe the real
    /// wire.
    /// </para>
    /// <para>
    /// <b>Order matters, and the fallback is deliberate:</b> the three source-generated contexts
    /// come first so they own their types' metadata; <see cref="DefaultJsonTypeInfoResolver"/> is
    /// appended last so all nine existing payload types keep resolving exactly as they did when no
    /// resolver was set at all (an unset <c>TypeInfoResolver</c> IS a
    /// <see cref="DefaultJsonTypeInfoResolver"/>). That equivalence is not assumed — it is pinned by
    /// a byte-for-byte golden in <c>StructuredToolResultTests</c>.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT set here, and why it would be a bug:</b> a
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> on these options. It looks like the obvious
    /// way to make the contexts' own <c>JsonSourceGenerationOptions</c> null-omission hold on this
    /// path — MEASURED, a context's <c>JsonSourceGenerationOptions</c> does NOT travel with its
    /// metadata into a different <c>JsonSerializerOptions</c>; only per-property attributes do,
    /// which is why every optional field on <see cref="VfxError"/>/<see cref="Diagnostic"/> carries
    /// its own <c>[JsonIgnore(Condition = WhenWritingNull)]</c>. But setting it at the OPTIONS level
    /// would apply to all nine existing payloads too, several of which emit nulls today and are
    /// contractually expected to (e.g. <c>validate_suite</c>'s
    /// <c>"instancePath":null,"line":null,"column":null</c>) — reshaping every tool's wire format as
    /// a side effect of a contracts change. Null omission belongs to the TYPE here, never to the
    /// shared options.
    /// </para>
    /// </remarks>
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// <see cref="ToolMetaProvider.Current"/> pre-serialised once, through <see cref="Options"/> —
    /// so <c>meta</c> genuinely travels the same resolver chain every payload does (this is the
    /// path <c>StructuredToolResultTests</c> asserts against), and so stamping it onto a result
    /// costs a buffer copy rather than a fresh serialisation per call.
    /// </summary>
    private static readonly JsonElement MetaElement =
        JsonSerializer.SerializeToElement(ToolMetaProvider.Current, typeof(ToolMeta), Options);

    /// <summary>
    /// Builds a successful result: <paramref name="payload"/>, stamped with US-S1-02's
    /// <c>meta</c> object, serialised as both the structured content and, as text, the first
    /// content block (for clients that only read <c>Content</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same JSON is deliberately carried twice</b> (a text <c>Content</c> block AND
    /// <c>StructuredContent</c>), so a payload's wire cost is double its serialised size — the
    /// premise behind <c>ExplainRunOrchestrator.EffectiveDiagnosisBudgetBytes</c> budgeting each
    /// candidate diagnosis against HALF of its public 64&#160;KB cap. That behaviour is unchanged
    /// here; the two copies are now produced from ONE serialisation rather than two identical ones,
    /// which is a pure cost saving, not a shape change (the text block is the structured element's
    /// own raw JSON, so the two are byte-identical by construction instead of by coincidence).
    /// </para>
    /// <para>
    /// <b>Effect on that budget, measured:</b> <c>meta</c> adds a fixed
    /// <c>,"meta":{…}</c> of 65&#160;bytes plus the UTF-8 length of the JSON-escaped
    /// <c>workspaceRoot</c> (measured — see <c>ToolMetaTests</c>), paid twice like everything else
    /// here. The 64&#160;KB constant continues to bound the doubled PAYLOAD exactly as before; the
    /// meta stamp sits outside that arithmetic, which is why US-S1-02 records the measurement as
    /// the documented baseline for Sprint 4's budget re-measurement rather than silently
    /// re-tuning the tiers now.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="payload"/> does not serialise to a JSON object, or already carries a
    /// top-level <c>meta</c> property.
    /// </exception>
    public static CallToolResult Success(object payload)
    {
        var structured = SerialiseWithMeta(payload);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

    /// <summary>
    /// Builds a tool-level error result: not a crash, just <c>IsError: true</c> carrying a single
    /// <see cref="VfxError"/> JSON object (US-S1-04).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the ONE pathway every <c>isError: true</c> result travels</b>, the mirror of
    /// <see cref="Success(object)"/>'s role for successful ones — and it takes a
    /// <see cref="VfxError"/>, never a bare string, DELIBERATELY. US-S1-04's migration was carried
    /// out by deleting the old <c>Error(string message)</c> overload rather than adding this one
    /// beside it: with both present, every one of the twenty-five existing call sites would have
    /// kept compiling against the code-less overload and the migration would have been a matter of
    /// remembering. With only this one, the compiler enumerated the call sites and the migration
    /// could not be partially done. Do not reintroduce a string overload for convenience.
    /// </para>
    /// <para>
    /// <b>No <c>meta</c> stamp</b>, unchanged from before the migration: US-S1-02 scopes the stamp
    /// to SUCCESS results. That scoping is now load-bearing rather than incidental — an error's
    /// body must be the <see cref="VfxError"/> shape "exactly" (spec §4.4), and a host that
    /// deserialises this content into its own error type would choke on an extra property. It is
    /// asserted by <c>RealVfxCodeContractMcpTests</c>.
    /// </para>
    /// <para>
    /// <b>The same JSON is carried twice</b> — as the text <c>Content</c> block and as
    /// <c>StructuredContent</c> — matching <see cref="Success(object)"/>'s existing convention, and
    /// from ONE serialisation so the two are byte-identical by construction. A client that only
    /// reads <c>Content</c> (the pre-migration behaviour, when this was a plain message) still gets
    /// the whole error; one that reads <c>StructuredContent</c> gets it without re-parsing.
    /// </para>
    /// </remarks>
    public static CallToolResult Error(VfxError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var structured = JsonSerializer.SerializeToElement(error, typeof(VfxError), Options);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            StructuredContent = structured,
        };
    }

    /// <summary>
    /// Serialises <paramref name="payload"/> and appends the <c>meta</c> object as its last
    /// property, returning the merged JSON object.
    /// </summary>
    /// <remarks>
    /// Appended LAST, and by rewriting rather than by wrapping: a host reading any existing
    /// property of any of the nine payloads sees it at exactly the path it was at before, since
    /// every original property is copied through in its original order with its original raw
    /// bytes. Both failure modes below are structural faults in a CALLER (a payload type that is
    /// not a JSON object, or one that has grown its own <c>meta</c> property and so would produce a
    /// duplicate key), never a condition an agent's input can provoke — so they fail fast and
    /// loudly, where the nine-tool coverage in <c>RealToolMetaMcpTests</c> catches them, rather
    /// than silently dropping either the payload's data or the stamp.
    /// </remarks>
    private static JsonElement SerialiseWithMeta(object payload)
    {
        var payloadElement = JsonSerializer.SerializeToElement(payload, payload.GetType(), Options);

        if (payloadElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Tool payload '{payload.GetType().Name}' serialised to {payloadElement.ValueKind}, "
                + "but a tool result must be a JSON object so the 'meta' stamp can be attached to it.");
        }

        var buffer = new ArrayBufferWriter<byte>();

        // The writer is configured FROM Options rather than left at Utf8JsonWriter's own defaults
        // (a review fix): this rewrite must be a faithful re-emission of what Options produced, and
        // the two settings that can change the bytes are the encoder and indentation. They agree
        // today — Options leaves both at the framework default — so this is a no-op right now and
        // deliberately so: it is what keeps the rewrite faithful if Options ever gains a custom
        // Encoder (e.g. a relaxed one), instead of silently re-escaping every already-written
        // property to a different convention than the one the payload was serialised under.
        var writerOptions = new JsonWriterOptions
        {
            Encoder = Options.Encoder,
            Indented = Options.WriteIndented,
        };

        using (var writer = new Utf8JsonWriter(buffer, writerOptions))
        {
            writer.WriteStartObject();

            foreach (var property in payloadElement.EnumerateObject())
            {
                if (property.NameEquals(MetaPropertyName))
                {
                    throw new InvalidOperationException(
                        $"Tool payload '{payload.GetType().Name}' already carries a top-level "
                        + $"'{MetaPropertyName}' property, which would collide with the provenance "
                        + "stamp every successful result carries. Rename the payload's own property.");
                }

                property.WriteTo(writer);
            }

            writer.WritePropertyName(MetaPropertyName);
            MetaElement.WriteTo(writer);

            writer.WriteEndObject();
        }

        using var merged = JsonDocument.Parse(buffer.WrittenMemory);
        return merged.RootElement.Clone();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        options.TypeInfoResolverChain.Add(ToolMetaJsonContext.Default);
        options.TypeInfoResolverChain.Add(VfxErrorJsonContext.Default);
        options.TypeInfoResolverChain.Add(DiagnosticJsonContext.Default);
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());

        // Frozen before first use: these options are shared by every tool call on every thread, and
        // a JsonSerializerOptions silently becomes read-only on first serialisation anyway. Doing it
        // here makes that a stated property of this type rather than an emergent one.
        options.MakeReadOnly();
        return options;
    }
}
