using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Vouchfx.Mcp.Run;

// Vouchfx.Mcp.Run — RawEventRelay (Sprint 3 / US-S3-05).
//
// get_run_events is a NEW relay surface onto a file this server already reads, and its acceptance
// criteria are explicit that it "must not become a second, unsanitised path to the same underlying
// file". SuiteEventParser is the FIRST relay: it extracts a handful of named fields and runs each
// through TextSanitiser.SanitiseForDisplay at the point of extraction. This type is the second, and
// it has a harder job — it must relay the WHOLE event object, including fields no version of this
// server has ever heard of (the v1 event contract is additive-frozen; EDGE-004), so it cannot work
// from a list of known field names the way SuiteEventParser does.
//
// ---------------------------------------------------------------------------------------------
// What "sanitised" means here, precisely — and what it must NOT mean
// ---------------------------------------------------------------------------------------------
//
//   * DOES: replace every character outside printable ASCII with a literal \uXXXX escape, in every
//     string VALUE and every property NAME, at every depth, via the SAME TextSanitiser every other
//     relay in this codebase uses. A control character injected into a captured value therefore comes
//     back exactly as explain_run already renders it.
//   * DOES: bound what one event can cost — per line, per string, per collection, per depth, and per
//     whole event — so a single pathological line cannot dominate a response, and so the WORK of
//     relaying it is bounded too, not just its output (see MaxEventBytes' remarks).
//   * DOES: MARK every bound it actually applied. A relayed event whose strings were capped carries
//     `_vfxStringsCapped: true`; anything that could not be relayed faithfully at all becomes the
//     truncation MARKER. Nothing is dropped or shortened silently — a raw-event tool whose output
//     quietly differs from the engine's is worse than one that refuses to answer, because a host has
//     no way to tell the two apart.
//   * DOES NOT: redact. The engine is the sole redaction authority (CLAUDE.md); these bytes have
//     already passed through it. Re-redacting would mean this server inventing a second, weaker
//     redactor whose disagreements with the engine's would be invisible.
//   * DOES NOT: resolve, interpret, rename, reorder, or drop fields. An unknown event type and an
//     unknown field pass through untouched (modulo the sanitisation above), because that is the whole
//     point of a raw-event tool: a host builds its own timeline from the exact wire tokens the engine
//     emitted, including tokens this server does not yet know.
//   * DOES NOT: translate the verdict vocabulary. "ENV_ERROR" stays "ENV_ERROR" — this is the wire
//     boundary, not the response boundary (sprint-00-overview.md §5).
//
// ---------------------------------------------------------------------------------------------
// Accepted residual: cap-then-sanitise can collide two distinct property names
// ---------------------------------------------------------------------------------------------
//
// Property names go through the SAME cap-then-sanitise pipeline as values (MaxStringChars, then
// TextSanitiser). Two consequences, both accepted and recorded here per this codebase's convention
// of writing down what a decision costs rather than only what it buys:
//
//   * Two distinct names longer than MaxStringChars that agree on their first MaxStringChars
//     characters collapse to one name, and the relayed object then carries that name twice.
//   * A name containing a non-ASCII character collides with a name that spells the same escape
//     literally: the engine writing `aé` and the engine writing the six characters `aé` both
//     relay as `aé`.
//
// Neither is corrected, because every correction is worse: emitting the raw name would defeat the
// sanitisation this type exists for, and inventing a disambiguating suffix would put a name in the
// output that the engine never wrote — the one thing a raw-event tool must not do. Both require the
// engine to emit a property name that is either 2000+ characters long or non-ASCII, which the v1
// event contract does not do. The duplicate key IS visible to a host (JSON permits it, and every
// parser it will meet keeps the last), so the collision is detectable rather than silent.

/// <summary>One event as it will appear in a <c>get_run_events</c> result, plus what it costs.</summary>
/// <param name="Element">
/// The sanitised, bounded copy — a detached <see cref="JsonElement"/> (cloned out of its parsing
/// <see cref="JsonDocument"/>), so it stays valid after this method's own document is disposed.
/// </param>
/// <param name="SerialisedBytes">
/// Its exact serialised UTF-8 byte count. MEASURED, never estimated: it is what
/// <see cref="GetRunEventsOrchestrator"/>'s response budget is spent against, and the whole point of
/// that budget is that it is not a guess.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when <see cref="Element"/> is the truncation MARKER rather than the event
/// — see <see cref="RawEventRelay.Relay"/>.
/// </param>
public readonly record struct RelayedEvent(JsonElement Element, int SerialisedBytes, bool Truncated);

/// <summary>
/// Sanitises and bounds ONE raw event object for relay through <c>get_run_events</c>. See this
/// file's header comment for exactly what sanitisation does and does not mean here.
/// </summary>
public static class RawEventRelay
{
    /// <summary>
    /// Maximum serialised bytes one relayed event may occupy. An event over this is replaced by the
    /// truncation marker (see <see cref="Relay"/>) rather than trimmed field by field: trimming would
    /// silently produce an object that LOOKS like the engine's event but is missing content, which is
    /// exactly the misreading a raw-event tool must never invite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chosen against the response budget, not in isolation.</b>
    /// <see cref="GetRunEventsOrchestrator.EffectiveEventsBudgetBytes"/> is 32&#160;KB, so this cap
    /// guarantees at least eight events per page even in the worst case — which is what makes
    /// "the page is never empty while matching events remain" true by arithmetic rather than by
    /// hope. Realistic events measure well under 1&#160;KB (see
    /// <c>GetRunEventsOrchestratorTests</c>'s measured figures), so this bound is a backstop, not a
    /// limit a legitimate stream is expected to brush.
    /// </para>
    /// <para>
    /// <b>Enforced DURING the copy, not only after it</b> (a security review's MAJOR finding, and it
    /// was measured rather than argued). This cap used to be checked only once the whole event had
    /// already been written into the buffer, so the CHECK was bounded while the WORK was not: a
    /// 20-property-wide, 4-deep line — well inside <see cref="MaxCollectionItems"/>, so nothing else
    /// stopped it — measured <b>7,823,168&#160;B of line</b> producing <b>39,890,392&#160;B
    /// allocated in 47.7&#160;ms</b> before the 4&#160;KB check rejected it. With
    /// <see cref="TryWriteSanitised"/>'s leading byte guard the same line measures <b>16,400&#160;B
    /// in 0.2&#160;ms</b>, and the result is byte-identical (the marker) because an event that
    /// overruns this cap was always going to become the marker. The guard cannot change the outcome
    /// for a legitimate event either: it only fires once the writer is ALREADY past this cap, at
    /// which point the finished copy would have failed the same check.
    /// </para>
    /// </remarks>
    public const int MaxEventBytes = 4 * 1024;

    /// <summary>
    /// Maximum characters accepted in one raw events-file LINE before it is refused as an event and
    /// reported as the truncation marker (<see cref="OverLongLineMarker"/>) instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parse is work too.</b> <see cref="MaxEventBytes"/>' guard bounds the COPY; this one
    /// bounds the <see cref="JsonDocument"/> parse that precedes it, which is otherwise O(line) in
    /// both time and metadata for a line that <see cref="EventsFileReader"/>'s 50&#160;MB cap is the
    /// only thing bounding. A caller asking for one page of a run must not pay for parsing a 50&#160;MB
    /// line in order to be told the result is a 62-byte marker.
    /// </para>
    /// <para>
    /// <b>Set 256x above <see cref="MaxEventBytes"/> deliberately, so it changes nothing for a
    /// realistic event.</b> Any line this large relays to the marker under the existing byte cap
    /// anyway unless nearly all of its size sits in string values that <see cref="MaxStringChars"/>
    /// then discards — and an event whose payload is a 1&#160;MB blob shortened to 2000 characters is
    /// precisely the case that deserves to be MARKED rather than handed over looking complete.
    /// </para>
    /// </remarks>
    public const int MaxEventLineChars = 1024 * 1024;

    /// <summary>
    /// Maximum characters kept from any single string value or property name, applied BEFORE
    /// sanitisation (which can only lengthen text, by expanding one character into six). Matches
    /// <c>SuiteEventParser</c>'s own label cap and its cap-then-sanitise ordering, so the two relays
    /// over the same file cannot disagree about how long a field may be.
    /// </summary>
    /// <remarks>
    /// Capping is never silent: an event that had any string or property name shortened here carries
    /// <see cref="StringsCappedMarkerProperty"/>. See this file's header for the key-collision
    /// residual the cap-then-sanitise ordering accepts.
    /// </remarks>
    public const int MaxStringChars = 2_000;

    /// <summary>
    /// Maximum nesting depth <see cref="Relay"/> will reproduce. A deeper event is not relayed as an
    /// event at all — it becomes the truncation marker, so a host sees THAT something was refused
    /// rather than finding a gap in the stream it cannot account for.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="MaxParseDepth"/>, and that separation is the point: parsing is
    /// allowed deeper than relaying so that a too-deep event is a REPORTED refusal here rather than a
    /// <see cref="JsonException"/> indistinguishable from a corrupt line.
    /// </remarks>
    public const int MaxDepth = 24;

    /// <summary>
    /// Maximum nesting depth accepted from the events file at PARSE time — the
    /// <see cref="JsonDocument"/> default, kept explicit so the two depth bounds are visibly
    /// different numbers rather than accidentally the same one.
    /// </summary>
    /// <remarks>
    /// Deeper than <see cref="MaxDepth"/> on purpose. When both were 24, an event nested 25 deep
    /// failed parsing and was skipped exactly like a corrupt line — a SILENT hole in a raw-event
    /// stream, which contradicts this file's own "never invite misreading" rule. Parsing to 64 and
    /// refusing at 24 turns that hole into a visible marker, while keeping the parse bounded by the
    /// framework's own default rather than by an unbounded allowance.
    /// </remarks>
    public const int MaxParseDepth = 64;

    /// <summary>
    /// Maximum properties in one object, or elements in one array, before an event is refused as a
    /// faithful copy and replaced by the truncation marker.
    /// </summary>
    /// <remarks>
    /// <b>An independent bound on SHAPE, not a performance optimisation over <see cref="MaxEventBytes"/></b>
    /// (this remark previously claimed the latter, which the byte guard on
    /// <see cref="TryWriteSanitised"/> has made false — that guard is now what stops the write early).
    /// It catches events the byte cap does not: 300 single-character properties serialise to well
    /// under 4&#160;KB, yet an event of that shape is not something the v1 contract emits, and
    /// relaying it would mean this server had no bound at all on how many keys a host must be
    /// prepared to see in one event.
    /// </remarks>
    public const int MaxCollectionItems = 256;

    /// <summary>The marker property naming a truncated relay. Underscore-prefixed so it can never be mistaken for an engine field.</summary>
    public const string TruncatedMarkerProperty = "_vfxTruncated";

    /// <summary>The marker property carrying the original event's size in bytes.</summary>
    public const string OriginalBytesMarkerProperty = "_vfxOriginalBytes";

    /// <summary>
    /// The marker property set on an OTHERWISE COMPLETE relayed event whose strings or property names
    /// were shortened to <see cref="MaxStringChars"/>.
    /// </summary>
    /// <remarks>
    /// Added because the cap was silent: a host reading a 2000-character observation had no way to
    /// know whether the engine wrote exactly that or whether this server had cut it, which is the
    /// same class of quiet misreading <see cref="TruncatedMarkerProperty"/> exists to prevent one
    /// size up. Underscore-prefixed for the same reason, and written LAST so every field the engine
    /// wrote keeps its original position.
    /// </remarks>
    public const string StringsCappedMarkerProperty = "_vfxStringsCapped";

    /// <summary>Longest <c>type</c>/<c>stepId</c> value carried through onto a truncation marker, so the marker itself is always small.</summary>
    private const int MaxMarkerLabelChars = 200;

    /// <summary>
    /// Validation is skipped deliberately. The writer is fed from an ALREADY-PARSED
    /// <see cref="JsonElement"/>, so it cannot be asked to emit structurally invalid JSON — and with
    /// validation on, <see cref="TryWriteSanitised"/>'s early abort (which leaves the document
    /// deliberately unfinished before the buffer is discarded) would throw on flush instead of simply
    /// being thrown away.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    /// <summary>Parser options pinning <see cref="MaxParseDepth"/> — see that constant and <see cref="MaxDepth"/>.</summary>
    public static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = MaxParseDepth };

    /// <summary>
    /// Produces the relayable form of <paramref name="source"/>: a sanitised, bounded copy, or — when
    /// it cannot be reproduced faithfully within <see cref="MaxEventBytes"/>,
    /// <see cref="MaxCollectionItems"/> and <see cref="MaxDepth"/> — a small TRUNCATION MARKER that
    /// names what was dropped instead of pretending to be the event.
    /// </summary>
    /// <param name="source">
    /// One event OBJECT, straight from the events file. A non-object root (which the v1 event
    /// contract never produces, and which <see cref="GetRunEventsOrchestrator"/> filters out before
    /// calling here) is itself reported as the marker rather than relayed, so that every value this
    /// method returns is an object a host can read the marker properties off.
    /// </param>
    /// <param name="sourceBytes">
    /// The original line's UTF-8 byte count, which the caller already knows. Passed in rather than
    /// recomputed from <paramref name="source"/>'s raw text, which would allocate a second copy of a
    /// line that is potentially enormous — precisely the case this method exists to bound.
    /// </param>
    /// <remarks>
    /// <para>
    /// The marker carries the event's own <c>type</c> and <c>stepId</c> when it has them, because a
    /// host paging a timeline still needs to know THAT something of that type happened here even when
    /// its payload was too large to relay. Both are sanitised and capped like everything else.
    /// </para>
    /// <para>
    /// <b>Every "cannot relay this faithfully" path converges here</b> — over the byte cap, over the
    /// collection cap, deeper than <see cref="MaxDepth"/>, a value or property name carrying a lone
    /// surrogate escape, or a non-object root. One shape for all of them means a host has exactly one
    /// thing to check (<see cref="TruncatedMarkerProperty"/>) rather than a list of ways an event can
    /// quietly not be there.
    /// </para>
    /// </remarks>
    public static RelayedEvent Relay(JsonElement source, int sourceBytes)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return BuildTruncationMarker(source, sourceBytes);
        }

        var buffer = new ArrayBufferWriter<byte>();
        var stringsCapped = false;
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            if (TryWriteEventObject(writer, source, ref stringsCapped))
            {
                writer.Flush();
                if (buffer.WrittenCount <= MaxEventBytes)
                {
                    return new RelayedEvent(Detach(buffer.WrittenSpan), buffer.WrittenCount, Truncated: false);
                }
            }
        }

        return BuildTruncationMarker(source, sourceBytes);
    }

    /// <summary>
    /// The marker for a line refused BEFORE it was parsed — one longer than
    /// <see cref="MaxEventLineChars"/>.
    /// </summary>
    /// <remarks>
    /// It carries no <c>type</c>/<c>stepId</c> label, and cannot: those live inside the JSON this
    /// method exists precisely to avoid parsing. That absence is exactly why
    /// <see cref="GetRunEventsOrchestrator"/> only ever admits this marker to an UNFILTERED page —
    /// see its own remarks on why a line whose type is unknown must not be asserted to match a type
    /// filter.
    /// </remarks>
    public static RelayedEvent OverLongLineMarker(int sourceBytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(TruncatedMarkerProperty, true);
            writer.WriteNumber(OriginalBytesMarkerProperty, sourceBytes);
            writer.WriteEndObject();
        }

        return new RelayedEvent(Detach(buffer.WrittenSpan), buffer.WrittenCount, Truncated: true);
    }

    /// <summary>
    /// The event's <c>type</c> as the engine wrote it — RAW, unsanitised, for FILTER matching only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Filters compare raw against raw, deliberately.</b> A caller's <c>types</c> argument is the
    /// literal token they expect the engine to have written, so matching it against the sanitised
    /// rendering would make a filter fail on exactly the events whose type contained something odd —
    /// silently returning nothing rather than the event. The value returned here NEVER reaches a
    /// result; only <see cref="Relay"/>'s sanitised copy does.
    /// </para>
    /// <para>
    /// <b><see langword="null"/> also means "this string cannot be read at all".</b> See
    /// <see cref="TryReadString"/>: a JSON string carrying an unpaired surrogate escape parses
    /// happily and then throws on decode, so every read of one goes through that helper and a failure
    /// is reported as absence. For a filter that is the correct reading — an event whose <c>type</c>
    /// cannot be decoded is not a match for any type a caller could have named.
    /// </para>
    /// <para>
    /// <b>And the LOOKUP can throw too, not only the read — which is why the catch below wraps
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> itself</b> (a security
    /// review's reopened BLOCKER, MEASURED). A property NAME may carry the same unpaired surrogate
    /// escape, and <c>TryGetProperty</c> has to UNESCAPE each candidate name before it can compare it,
    /// so <c>{"type":"step-attempt","\ud800":1}</c> throws
    /// <see cref="InvalidOperationException"/> while looking up an entirely different property.
    /// <b>The throw is LENGTH-DEPENDENT</b>, which is why the value-side catch alone looked
    /// sufficient: escaping can only lengthen a name, so a candidate whose escaped byte length is
    /// SHORTER than the sought name is ruled out without ever being decoded. Against the six-byte
    /// <c>\ud800</c>, looking up <c>type</c> (four bytes) decodes and throws; looking up
    /// <c>schemaVersion</c> (thirteen) is skipped and quietly finds nothing. All three consumers ran
    /// through it — the filter path (<c>Matches</c>), the version probe, and, worst,
    /// <see cref="BuildTruncationMarker"/> via <see cref="WriteMarkerLabel"/>, so the marker that the
    /// value-side fix routes every refusal through was itself a crash site.
    /// </para>
    /// </remarks>
    public static string? RawStringProperty(JsonElement source, string propertyName)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return source.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                && TryReadString(value, out var text)
                    ? text
                    : null;
        }
        catch (InvalidOperationException)
        {
            // A SIBLING property name could not be unescaped for comparison (see the remarks above).
            // Reported as absence, which is already this method's documented meaning for "cannot be
            // read": a property whose lookup cannot even be performed is not a match for any filter,
            // and a marker label that cannot be looked up is simply not written.
            return null;
        }
    }

    /// <summary>
    /// Writes the ROOT event object: every property the engine wrote, in its original order, then
    /// <see cref="StringsCappedMarkerProperty"/> if any string was shortened along the way.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryWriteSanitised"/>'s object case only because the capped marker
    /// belongs on the event, not on every nested object inside it — a host needs one flag per event,
    /// not a scattering of them at whatever depth a cap happened to fire.
    /// </remarks>
    private static bool TryWriteEventObject(Utf8JsonWriter writer, JsonElement source, ref bool stringsCapped)
    {
        writer.WriteStartObject();

        var propertyCount = 0;
        foreach (var property in source.EnumerateObject())
        {
            if (++propertyCount > MaxCollectionItems)
            {
                return false;
            }

            if (!TryWriteProperty(writer, property, depth: 1, ref stringsCapped))
            {
                return false;
            }
        }

        if (stringsCapped)
        {
            writer.WriteBoolean(StringsCappedMarkerProperty, true);
        }

        writer.WriteEndObject();
        return true;
    }

    /// <summary>
    /// Writes <paramref name="element"/> into <paramref name="writer"/>, sanitising every string and
    /// property name and enforcing <see cref="MaxEventBytes"/>, <see cref="MaxCollectionItems"/> and
    /// <see cref="MaxDepth"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a bound was hit, in which case whatever has been written so far
    /// is abandoned by the caller (see <see cref="WriterOptions"/> for why an unfinished document is
    /// safe here).
    /// </returns>
    private static bool TryWriteSanitised(Utf8JsonWriter writer, JsonElement element, int depth, ref bool stringsCapped)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        // The byte cap, enforced on the way IN rather than only on the finished copy — see
        // MaxEventBytes' remarks for the measured cost of the version that did not. Checked here, at
        // the single entry point every value in the event passes through, so no shape of event can
        // route around it.
        if (writer.BytesPending + writer.BytesCommitted > MaxEventBytes)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (++propertyCount > MaxCollectionItems)
                    {
                        return false;
                    }

                    if (!TryWriteProperty(writer, property, depth + 1, ref stringsCapped))
                    {
                        return false;
                    }
                }

                writer.WriteEndObject();
                return true;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var elementCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (++elementCount > MaxCollectionItems)
                    {
                        return false;
                    }

                    if (!TryWriteSanitised(writer, item, depth + 1, ref stringsCapped))
                    {
                        return false;
                    }
                }

                writer.WriteEndArray();
                return true;

            case JsonValueKind.String:
                if (!TryReadString(element, out var text))
                {
                    return false;
                }

                writer.WriteStringValue(CapAndSanitise(text, ref stringsCapped));
                return true;

            case JsonValueKind.Number:
                // Written from the number's own raw text rather than through a CLR numeric type: an
                // engine-emitted number that does not fit long/double (or that carries a precision
                // this server would round away) must reach the host as the engine wrote it. This is
                // a raw-event tool; re-encoding a number is exactly the kind of quiet reinterpretation
                // it exists to avoid.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                return true;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.ValueKind == JsonValueKind.True);
                return true;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                return true;

            default:
                // JsonValueKind.Undefined cannot occur inside a parsed document; treated as a bound
                // failure rather than silently omitted, so it can never produce a copy that looks
                // complete but is not.
                return false;
        }
    }

    /// <summary>Writes one property name/value pair, refusing the whole event if the NAME cannot be decoded.</summary>
    private static bool TryWriteProperty(Utf8JsonWriter writer, JsonProperty property, int depth, ref bool stringsCapped)
    {
        if (!TryReadPropertyName(property, out var name))
        {
            return false;
        }

        writer.WritePropertyName(CapAndSanitise(name, ref stringsCapped));
        return TryWriteSanitised(writer, property.Value, depth, ref stringsCapped);
    }

    /// <summary>
    /// Reads a JSON string value, reporting <see langword="false"/> rather than throwing when it
    /// cannot be decoded.
    /// </summary>
    /// <remarks>
    /// <b>MEASURED, and the reason this helper exists</b> (a security review's BLOCKER). A line such
    /// as <c>{"type":"x","v":"\ud800"}</c> — an unpaired high surrogate ESCAPE, six perfectly valid
    /// ASCII characters in the file — parses without complaint, and then
    /// <see cref="JsonElement.GetString"/> throws
    /// <see cref="InvalidOperationException"/> ("Cannot read incomplete UTF-16 JSON text as string
    /// with missing low surrogate"). Uncaught, that escaped this whole tool: the walk over an events
    /// file is deterministic, so a single such line killed every page of that run forever, and it
    /// fired even on lines the caller had filtered OUT, because the filter reads <c>type</c> the same
    /// way. Caught here, the line degrades to the truncation marker (or, for a filter read, to "no
    /// match") — the per-line tolerance the rest of this pipeline already promises.
    /// <para>
    /// Relaying such a string from <see cref="JsonElement.GetRawText"/> instead — which returns the
    /// escape intact and never throws — was considered and rejected: raw text is the ESCAPED form, so
    /// it cannot be capped or run through <see cref="TextSanitiser"/>, and a relay that emitted one
    /// string unsanitised to avoid an exception would be trading this file's whole reason for
    /// existing against an edge case the marker already covers honestly.
    /// </para>
    /// </remarks>
    private static bool TryReadString(JsonElement element, out string value)
    {
        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            value = string.Empty;
            return false;
        }
    }

    /// <summary><see cref="TryReadString"/> for a property NAME, which decodes the same way and fails the same way.</summary>
    private static bool TryReadPropertyName(JsonProperty property, out string name)
    {
        try
        {
            name = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            return false;
        }
    }

    /// <summary>Caps to <see cref="MaxStringChars"/> and THEN sanitises — the ordering <c>SuiteEventParser</c> uses, for the same reason.</summary>
    private static string CapAndSanitise(string value, ref bool stringsCapped)
    {
        if (value.Length <= MaxStringChars)
        {
            return TextSanitiser.SanitiseForDisplay(value);
        }

        stringsCapped = true;
        return TextSanitiser.SanitiseForDisplay(value[..MaxStringChars]);
    }

    private static RelayedEvent BuildTruncationMarker(JsonElement source, int sourceBytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            WriteMarkerLabel(writer, source, "type");
            WriteMarkerLabel(writer, source, "stepId");
            writer.WriteBoolean(TruncatedMarkerProperty, true);
            writer.WriteNumber(OriginalBytesMarkerProperty, sourceBytes);
            writer.WriteEndObject();
        }

        return new RelayedEvent(Detach(buffer.WrittenSpan), buffer.WrittenCount, Truncated: true);
    }

    /// <summary>
    /// Writes one identifying label onto the marker, or nothing when the event has no such property
    /// — including when it HAS one that cannot be decoded (see <see cref="TryReadString"/>): a marker
    /// that threw while explaining a line that already could not be relayed would defeat its own
    /// purpose.
    /// </summary>
    private static void WriteMarkerLabel(Utf8JsonWriter writer, JsonElement source, string propertyName)
    {
        if (RawStringProperty(source, propertyName) is not { } raw)
        {
            return;
        }

        var capped = raw.Length > MaxMarkerLabelChars ? raw[..MaxMarkerLabelChars] : raw;
        writer.WriteString(propertyName, TextSanitiser.SanitiseForDisplay(capped));
    }

    /// <summary>
    /// Re-parses freshly written bytes and <see cref="JsonElement.Clone"/>s the root, so the result
    /// outlives the <see cref="JsonDocument"/> that produced it. The bytes are this method's OWN
    /// output, so the parse cannot fail and needs no depth allowance beyond what was written.
    /// </summary>
    private static JsonElement Detach(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    /// <summary>The UTF-8 byte count of <paramref name="line"/> — what <see cref="Relay"/>'s <c>sourceBytes</c> expects.</summary>
    public static int ByteCountOf(string line) => Encoding.UTF8.GetByteCount(line);
}
