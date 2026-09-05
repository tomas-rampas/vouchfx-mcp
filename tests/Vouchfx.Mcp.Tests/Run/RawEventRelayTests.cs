using System.Text.Json;
using Vouchfx.Mcp;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

/// <summary>
/// <see cref="RawEventRelay"/>'s unit tests (US-S3-05): the NEW relay surface onto the events file
/// must sanitise exactly as the existing one does, bound what one event can cost, and leave
/// everything else — unknown types, unknown fields, wire tokens, numeric precision — untouched.
/// </summary>
/// <remarks>
/// Every control character below is composed from a numeric constant rather than written as a
/// literal in the source. A raw ESC or NUL byte sitting in a <c>.cs</c> file is invisible in a diff,
/// survives a careless copy-paste into a terminal, and turns the file binary to <c>grep</c> — and a
/// test about sanitising control characters is the last place that should be tolerated.
/// </remarks>
public class RawEventRelayTests
{
    private const char Escape = (char)0x1B;
    private const char Bell = (char)0x07;
    private const char Nul = (char)0x00;
    private const char Soh = (char)0x01;

    [Fact]
    public void AControlCharacterInACapturedValue_IsSanitisedExactlyAsExplainRunSanitisesIt()
    {
        // An ANSI title-set sequence, the threat TextSanitiser's own remarks name. The SAME assertion
        // explain_run's relay satisfies, stated against the SAME helper — this is what "not a second,
        // unsanitised path to the same file" means concretely.
        var hostile = "order-" + Escape + "]0;pwned" + Bell + "id";
        // Concatenated rather than interpolated into a raw literal: the JSON's own closing braces sit
        // immediately after the hole, where `}}` would be read as the interpolation delimiter.
        var line = """{"type":"step-completed","observation":{"captured":"""
            + JsonSerializer.Serialize(hostile) + "}}";

        var captured = Relay(line).Element
            .GetProperty("observation")
            .GetProperty("captured")
            .GetString();

        Assert.Equal(TextSanitiser.SanitiseForDisplay(hostile), captured);
        Assert.DoesNotContain(Escape, captured!);
        Assert.DoesNotContain(Bell, captured!);
    }

    [Fact]
    public void SanitisationReachesEveryDepth_IncludingPropertyNamesAndArrayElements()
    {
        // A relay that sanitised only the fields it knows by name would be exactly the "second,
        // unsanitised path" the acceptance criteria forbid — this tool relays fields nobody has
        // named, at depths nobody has enumerated.
        var innerObject = new Dictionary<string, string> { ["na" + Nul + "me"] = "v" + Soh + "alue" };
        var line = """{"type":"x","a":{"b":[""" + JsonSerializer.Serialize(innerObject) + "]}}";

        var inner = Relay(line).Element.GetProperty("a").GetProperty("b")[0];
        var property = Assert.Single(inner.EnumerateObject());

        Assert.Equal("na\\u0000me", property.Name);
        Assert.Equal("v\\u0001alue", property.Value.GetString());
    }

    [Fact]
    public void AWireVerdictToken_IsRelayedVerbatim()
    {
        // sprint-00-overview.md §5: this is the WIRE boundary. ENV_ERROR stays ENV_ERROR; the
        // EnvironmentError response string must not appear anywhere.
        var relayed = Relay("""{"type":"step-completed","stepId":"s","verdict":"ENV_ERROR"}""");

        Assert.Equal("ENV_ERROR", relayed.Element.GetProperty("verdict").GetString());
        Assert.DoesNotContain("EnvironmentError", relayed.Element.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEventTypeAndUnknownFields_PassThroughUntouched()
    {
        // EDGE-004 / the additive-frozen v1 event contract: a raw-event tool that dropped what it did
        // not recognise would be strictly less useful than reading the file.
        var relayed = Relay("""
            {"type":"some-future-event","futureField":{"nested":[1,true,null]},"n":7}
            """);

        Assert.Equal("some-future-event", relayed.Element.GetProperty("type").GetString());
        Assert.Equal(7, relayed.Element.GetProperty("n").GetInt32());
        var nested = relayed.Element.GetProperty("futureField").GetProperty("nested");
        Assert.Equal(3, nested.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, nested[2].ValueKind);
    }

    [Fact]
    public void ANumberIsRelayedFromItsOwnRawText_NeverReEncoded()
    {
        // An engine-emitted number a CLR round trip would reshape (precision, exponent form) must
        // reach the host as the engine wrote it — re-encoding is the quiet reinterpretation a
        // raw-event tool exists to avoid.
        var relayed = Relay("""{"type":"x","big":123456789012345678901234567890,"exp":1.0E+2}""");

        Assert.Equal("123456789012345678901234567890", relayed.Element.GetProperty("big").GetRawText());
        Assert.Equal("1.0E+2", relayed.Element.GetProperty("exp").GetRawText());
    }

    [Fact]
    public void AnOverlongStringValue_IsCappedBeforeSanitisation_AndTheEventSaysSo()
    {
        var oversized = JsonSerializer.Serialize(new string('a', RawEventRelay.MaxStringChars + 500));
        var relayed = Relay($$"""{"type":"x","s":{{oversized}}}""");

        Assert.Equal(RawEventRelay.MaxStringChars, relayed.Element.GetProperty("s").GetString()!.Length);
        Assert.False(relayed.Truncated);

        // The cap used to be SILENT, which is the same misreading _vfxTruncated exists to prevent one
        // size up: a host reading exactly 2000 characters could not tell whether the engine wrote them
        // or this server cut them.
        Assert.True(relayed.Element.GetProperty(RawEventRelay.StringsCappedMarkerProperty).GetBoolean());
    }

    [Fact]
    public void AnEventWhoseStringsAllFit_CarriesNoCappedMarkerAtAll()
    {
        // The flag has to be absent, not false: a marker on every event would train a host to ignore
        // it, and it would cost bytes on the budget of every page.
        var relayed = Relay("""{"type":"step-completed","stepId":"a","verdict":"PASS"}""");

        Assert.False(relayed.Element.TryGetProperty(RawEventRelay.StringsCappedMarkerProperty, out _));
    }

    [Fact]
    public void AnOverlongPropertyNameAlsoTripsTheCappedMarker()
    {
        // Names go through the same cap as values, so they have to report through the same flag.
        var longName = new string('n', RawEventRelay.MaxStringChars + 10);
        var relayed = Relay($$"""{"type":"x",{{JsonSerializer.Serialize(longName)}}:1}""");

        Assert.True(relayed.Element.GetProperty(RawEventRelay.StringsCappedMarkerProperty).GetBoolean());
    }

    [Fact]
    public void AnEventNestedDeeperThanTheRelayBound_BecomesTheTruncationMarker_NotASilentSkip()
    {
        // Parsing is allowed to MaxParseDepth (64) precisely so that a 25-deep event is refused
        // VISIBLY here rather than failing to parse and being dropped like a corrupt line — a hole in
        // a raw-event stream a host cannot account for is the failure this split exists to avoid.
        var deep = Nested(RawEventRelay.MaxDepth + 5);
        var line = $$"""{"type":"deep-event","stepId":"s","d":{{deep}}}""";

        var relayed = Relay(line);

        Assert.True(relayed.Truncated);
        Assert.Equal("deep-event", relayed.Element.GetProperty("type").GetString());
    }

    [Fact]
    public void AnEventWithinTheRelayDepthBound_IsRelayedNormally()
    {
        // The other side of the bound, so the test above is not passing for the wrong reason.
        var relayed = Relay($$"""{"type":"x","d":{{Nested(RawEventRelay.MaxDepth - 4)}}}""");

        Assert.False(relayed.Truncated);
    }

    [Fact]
    public void ANonObjectRoot_BecomesTheTruncationMarker()
    {
        // The v1 contract never emits one and the orchestrator filters them out first, so this is
        // defence in depth — but the alternative (relaying it) would return a value with nowhere to
        // put the marker properties a host is told to check.
        using var document = JsonDocument.Parse("""[1,2,3]""", RawEventRelay.ParseOptions);

        Assert.True(RawEventRelay.Relay(document.RootElement, 7).Truncated);
    }

    // ── Lone surrogate escapes (a security review's BLOCKER — see TryReadString's remarks) ───────

    [Fact]
    public void AnUndecodableStringValue_BecomesTheTruncationMarker_RatherThanThrowing()
    {
        // MEASURED: `"\ud800"` — an unpaired high surrogate ESCAPE, six ordinary ASCII characters in
        // the file — parses without complaint and then throws InvalidOperationException on GetString.
        // Uncaught, that killed every page of the run forever, because the walk is deterministic.
        var relayed = Relay("""{"type":"step-completed","stepId":"s","v":"\ud800"}""");

        Assert.True(relayed.Truncated);
        Assert.Equal("step-completed", relayed.Element.GetProperty("type").GetString());
        Assert.Equal("s", relayed.Element.GetProperty("stepId").GetString());
    }

    [Fact]
    public void AnUndecodablePropertyName_BecomesTheTruncationMarker_RatherThanThrowing()
    {
        var relayed = Relay("""{"type":"x","na\ud800me":1}""");

        Assert.True(relayed.Truncated);
    }

    [Fact]
    public void AnUndecodableStringNestedDeepInsideAnEvent_IsCaughtToo()
    {
        // The throw happens wherever the string is, not only at the top level — a catch that only
        // wrapped the root would have left the same crash one field down.
        var relayed = Relay("""{"type":"x","a":{"b":[{"c":"\udfff"}]}}""");

        Assert.True(relayed.Truncated);
    }

    [Fact]
    public void AnUndecodableTypeLabel_LeavesTheMarkerUnlabelled_RatherThanThrowingWhileBuildingIt()
    {
        // The marker-label path reads `type`/`stepId` off an event that has ALREADY been refused. A
        // throw here would defeat the marker's own purpose, which is to explain what could not be
        // relayed.
        var relayed = Relay("""{"type":"x\ud800y","stepId":"kept","v":"\ud800"}""");

        Assert.True(relayed.Truncated);
        Assert.False(relayed.Element.TryGetProperty("type", out _));
        Assert.Equal("kept", relayed.Element.GetProperty("stepId").GetString());
    }

    [Fact]
    public void RawStringProperty_ReportsAnUndecodableStringAsAbsent_WhichIsWhatTheFilterPathNeeds()
    {
        // The filter path (`types`, `stepId`) reads raw strings on EVERY line, including lines the
        // caller filtered out — so before this, one poisoned line failed calls that were not even
        // asking for it. "Absent" is also the correct reading: a type that cannot be decoded is not a
        // match for any type a caller could have named.
        using var document = JsonDocument.Parse("""{"type":"\ud800","stepId":"s"}""", RawEventRelay.ParseOptions);

        Assert.Null(RawEventRelay.RawStringProperty(document.RootElement, "type"));
        Assert.Equal("s", RawEventRelay.RawStringProperty(document.RootElement, "stepId"));
    }

    [Fact]
    public void APoisonedPropertyNameMakesTheLOOKUPItselfThrow_AndTheMarkerIsBuiltAnyway()
    {
        // MEASURED, and the residual the value-side catch above did NOT cover: the throw is not only
        // on the READ, it is on the LOOKUP. JsonElement.TryGetProperty must UNESCAPE a candidate
        // property name before it can compare it, so an object carrying `"\ud800"` as a NAME throws
        // from TryGetProperty — before any value is touched.
        //
        // It is LENGTH-DEPENDENT, which is why every test above missed it: a candidate whose escaped
        // byte length is shorter than the sought name cannot possibly unescape to it and is skipped
        // without decoding. `"\ud800"` is six bytes on the wire, so looking up "type" (four) DECODES
        // and throws, while looking up "schemaVersion" (thirteen) is skipped and simply finds nothing.
        //
        // Both sides of that are pinned here: the event still becomes the marker (the poisoned NAME
        // fails TryReadPropertyName, as before), and BuildTruncationMarker -> WriteMarkerLabel then
        // performs exactly the four-byte "type" lookup that throws — so the fallback the value-side
        // fix routes every refusal through was itself the crash site.
        var relayed = Relay("""{"type":"step-attempt","\ud800":1}""");

        Assert.True(relayed.Truncated);

        // Unlabelled: the label could not be LOOKED UP, which is the same "cannot be read" absence
        // an undecodable label value produces. A marker with no label still explains the line.
        Assert.False(relayed.Element.TryGetProperty("type", out _));
        Assert.False(relayed.Element.TryGetProperty("stepId", out _));
    }

    [Fact]
    public void RawStringProperty_ReportsAnUndecodableSiblingNameAsAbsent_BecauseTheLookupIsWhatThrows()
    {
        // The same defect at the helper every consumer goes through — the filter path (Matches), the
        // version probe, and the marker labeller. `null` already means "cannot be read" per this
        // method's own remarks, so a lookup that cannot even be PERFORMED reports the same absence.
        using var document = JsonDocument.Parse(
            """{"type":"step-attempt","\ud800":1}""", RawEventRelay.ParseOptions);

        // Four bytes sought against a six-byte escaped name: decoded, and it is this that threw.
        Assert.Null(RawEventRelay.RawStringProperty(document.RootElement, "type"));

        // Thirteen bytes sought against the same six-byte name: ruled out on length, never decoded.
        // Pinned so the length-dependence is a stated fact rather than a coincidence of the fixture.
        Assert.Null(RawEventRelay.RawStringProperty(document.RootElement, "schemaVersion"));
    }

    [Fact]
    public void AValidSurrogatePair_IsUnaffected_AndSanitisedLikeAnyOtherNonAsciiText()
    {
        // Anti-overreach: the guard above must reject only what genuinely cannot be decoded. A well
        // formed astral character is two UTF-16 units and relays as two \uXXXX escapes. Written as
        // the JSON escape pair rather than as a literal character, for the same reason this file
        // composes its control characters numerically: the source stays ASCII.
        var relayed = Relay("""{"type":"x","v":"\ud83d\ude00"}""");

        Assert.False(relayed.Truncated);
        Assert.Equal("\\ud83d\\ude00", relayed.Element.GetProperty("v").GetString());
    }

    // ── Bounding the WORK, not just the output (a security review's MAJOR) ──────────────────────

    [Fact]
    public void APathologicallyWideEvent_StopsBeingCopiedAtTheByteCap_NotAfterTheWholeCopyIsBuilt()
    {
        // MEASURED, and the reason this exists: 20 properties wide and 4 deep is well inside
        // MaxCollectionItems, so nothing else stopped it. The line is 7,823,168 B; copying it whole
        // and THEN checking the 4 KB cap allocated 39,890,392 B in 47.7 ms to produce a 62-byte
        // marker. With the leading byte guard the same line measures ~16 KB in ~0.2 ms.
        var line = "{\"type\":\"x\",\"p\":" + Wide(width: 20, depth: 4) + "}";
        using var document = JsonDocument.Parse(line, RawEventRelay.ParseOptions);

        // Thread-local, deliberately: GC.GetTotalAllocatedBytes is process-wide and this suite runs
        // test classes in parallel, so only the per-thread counter can attribute allocations to the
        // call under test.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var relayed = RawEventRelay.Relay(document.RootElement, RawEventRelay.ByteCountOf(line));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(relayed.Truncated);

        // A loose bound on purpose — 250x the measured figure, and still 10x BELOW what the
        // unbounded version allocated. This asserts the copy stops early, not a particular
        // allocation profile, so it does not become a tripwire for unrelated tuning.
        Assert.True(
            allocated < 4 * 1024 * 1024,
            $"Relaying a {RawEventRelay.ByteCountOf(line):N0} B line allocated {allocated:N0} B. The "
            + "byte cap is supposed to abort the copy on the way IN; an allocation at the scale of "
            + "the line itself means it is being enforced only on the finished copy again.");
    }

    [Fact]
    public void TheOverLongLineMarker_IsLabelLessAndSmall()
    {
        // A line past MaxEventLineChars is never parsed, so its type/stepId are unknowable here —
        // which is exactly why GetRunEventsOrchestrator only admits this marker to an unfiltered page.
        var marker = RawEventRelay.OverLongLineMarker(sourceBytes: 5_000_000);

        Assert.True(marker.Truncated);
        Assert.True(marker.Element.GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.Equal(5_000_000, marker.Element.GetProperty(RawEventRelay.OriginalBytesMarkerProperty).GetInt32());
        Assert.False(marker.Element.TryGetProperty("type", out _));
        Assert.True(marker.SerialisedBytes < RawEventRelay.MaxEventBytes);
    }

    [Fact]
    public void TheDepthBounds_AreDeliberatelyDifferentNumbers()
    {
        // The whole point of the split: parse deeper than you relay, so a too-deep event is a
        // reported refusal rather than a line that failed to parse and vanished.
        Assert.True(
            RawEventRelay.MaxParseDepth > RawEventRelay.MaxDepth,
            "MaxParseDepth must stay strictly greater than MaxDepth, or a too-deep event becomes a "
            + "silent skip again instead of a truncation marker.");
    }

    /// <summary>A <paramref name="levels"/>-deep chain of single-property objects, innermost value <c>1</c>.</summary>
    private static string Nested(int levels)
    {
        var json = "1";
        for (var i = 0; i < levels; i++)
        {
            json = "{\"n\":" + json + "}";
        }

        return json;
    }

    /// <summary>A <paramref name="width"/>-property, <paramref name="depth"/>-deep object whose leaves are 40-character strings.</summary>
    private static string Wide(int width, int depth)
    {
        if (depth == 0)
        {
            return "\"" + new string('y', 40) + "\"";
        }

        var inner = Wide(width, depth - 1);
        var parts = new string[width];
        for (var i = 0; i < width; i++)
        {
            parts[i] = "\"f" + i + "\":" + inner;
        }

        return "{" + string.Join(',', parts) + "}";
    }

    [Fact]
    public void AnEventOverTheByteCap_BecomesATruncationMarkerRatherThanASilentlyTrimmedEvent()
    {
        // Trimming field by field would produce an object that LOOKS like the engine's event but is
        // missing content — the misreading a raw-event tool must never invite.
        var payload = string.Join(',', Enumerable.Range(0, 40).Select(i => $"\"f{i}\":\"{new string('x', 300)}\""));
        var line = $$"""{"type":"step-attempt","stepId":"verify-order",{{payload}}}""";

        var relayed = Relay(line);

        Assert.True(relayed.Truncated);
        Assert.True(relayed.Element.GetProperty(RawEventRelay.TruncatedMarkerProperty).GetBoolean());
        Assert.Equal(
            RawEventRelay.ByteCountOf(line),
            relayed.Element.GetProperty(RawEventRelay.OriginalBytesMarkerProperty).GetInt32());

        // The marker keeps the identifying labels, so a host paging a timeline still knows THAT an
        // event of this type happened for this step.
        Assert.Equal("step-attempt", relayed.Element.GetProperty("type").GetString());
        Assert.Equal("verify-order", relayed.Element.GetProperty("stepId").GetString());

        // …and is itself small, which is what keeps the page budget's forward-progress arithmetic true.
        Assert.True(
            relayed.SerialisedBytes < RawEventRelay.MaxEventBytes,
            $"The truncation marker measured {relayed.SerialisedBytes} B, which is not smaller than the "
            + $"{RawEventRelay.MaxEventBytes} B cap it exists to stay under.");
    }

    [Fact]
    public void AnEventWithAPathologicalNumberOfProperties_BecomesATruncationMarker()
    {
        var payload = string.Join(',', Enumerable.Range(0, RawEventRelay.MaxCollectionItems + 5).Select(i => $"\"f{i}\":1"));

        Assert.True(Relay($$"""{"type":"x",{{payload}}}""").Truncated);
    }

    [Fact]
    public void TheMarkerProperties_AreUnderscorePrefixedSoTheyCannotBeMistakenForEngineFields()
    {
        // The engine's event contract uses camelCase without a leading underscore, so this prefix is
        // what keeps a marker distinguishable from a field the engine might one day add.
        Assert.StartsWith("_", RawEventRelay.TruncatedMarkerProperty, StringComparison.Ordinal);
        Assert.StartsWith("_", RawEventRelay.OriginalBytesMarkerProperty, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayedElement_OutlivesTheDocumentThatProducedIt()
    {
        // Relay clones the root out of its parsing JsonDocument; without that, every event in a page
        // would be a use-after-dispose the moment the scan moved on.
        var relayed = Relay("""{"type":"x","s":"kept"}""");
        GC.Collect();

        Assert.Equal("kept", relayed.Element.GetProperty("s").GetString());
    }

    [Fact]
    public void SerialisedBytes_IsTheMeasuredSizeOfTheRelayedElement()
    {
        // The page budget is spent against this number, so it has to BE the size, not an estimate.
        var relayed = Relay("""{"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}""");

        Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(relayed.Element.GetRawText()),
            relayed.SerialisedBytes);
    }

    private static RelayedEvent Relay(string line)
    {
        using var document = JsonDocument.Parse(line, RawEventRelay.ParseOptions);
        return RawEventRelay.Relay(document.RootElement, RawEventRelay.ByteCountOf(line));
    }
}
