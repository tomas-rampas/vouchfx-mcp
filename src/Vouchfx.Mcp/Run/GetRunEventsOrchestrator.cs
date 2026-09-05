using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Run;

/// <summary>
/// US-S3-05's <c>get_run_events</c> pipeline: resolve a <c>runId</c> through the run registry, read
/// that run's JSON Lines event stream, apply the caller's filters, and return ONE page of raw events
/// plus an opaque continuation cursor. Purely read + filter + relay — it never re-runs anything, and
/// it never takes the run lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built ON <see cref="EventsFileReader"/>, not beside it.</b> The story's acceptance criteria say
/// so explicitly, and the reason is the 50&#160;MB cap that reader already enforces: a second reader
/// would be a second answer to "how much of a hostile stream may this server buffer", and the two
/// would drift. Everything downstream here works from the string that reader returns.
/// </para>
/// <para>
/// <b>What that reuse COSTS, recorded rather than glossed.</b> The reader has no notion of a
/// position, so every page re-reads and re-scans the WHOLE file from byte zero and then skips to the
/// cursor's line. The shape is quadratic in pages: a 50&#160;MB stream of realistic events walks in
/// roughly 1500 pages at the measured ~224 events/page, so a full walk re-reads on the order of
/// <b>75&#160;GB</b> in total. Accepted deliberately for Sprint 3 — one bounded read with one cap is
/// the correctness property the story asked for, the common case is a handful of pages over a stream
/// of a few hundred KB, and nothing here holds a lock or blocks a run while it reads. The sanctioned
/// upgrade, when a workload makes it worth measuring again, is a BYTE OFFSET in the cursor plus a
/// seeking read: the cursor is already opaque (see <see cref="OpaqueCursor"/>) and its payload is
/// already documented as an implementation detail, so that change costs no contract at all.
/// </para>
/// <para>
/// <b>Read-only, and lock-free by design (US-S3-04's AC).</b> Nothing in this type touches
/// <see cref="IRunLock"/>. Spec §4.6's "read-only tools are safe to call concurrently" is what that
/// buys, and <c>RunLockSourceGuardTests</c> holds it structurally by asserting the lock has exactly
/// one call site in <c>src/</c>.
/// </para>
/// <para>
/// <b>Filters run BEFORE pagination.</b> <c>limit</c> bounds the number of MATCHING events returned,
/// never the number of lines scanned — a run of 5000 events with 40 matches and <c>limit: 10</c>
/// returns ten matches, not ten lines' worth of whatever happened to be at the front. That ordering
/// is the acceptance criterion, and it is also the only ordering that makes a filtered page walk
/// terminate in a predictable number of calls.
/// </para>
/// <para>
/// <b>Path resolution mirrors <c>explain_run</c>'s exactly</b> (see
/// <c>Diagnosis.ExplainRunOrchestrator</c>): the registry's minted events path goes through
/// <see cref="PathSafetyGuard.CheckLocalPath"/> with no exemption, so a workspace-configured server
/// containment-checks it like any other path, and the display rendering is capped and sanitised
/// ONCE and reused by every message. The caller never names a path here at all — only a
/// <c>runId</c> — which is a strictly smaller attack surface than <c>explain_run</c>'s.
/// </para>
/// </remarks>
public sealed class GetRunEventsOrchestrator
{
    /// <summary>Spec §4.5's default page size.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Spec §4.5's maximum page size.</summary>
    public const int MaxLimit = 2_000;

    /// <summary>
    /// The INTENDED cap on this tool's response, mirroring
    /// <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c>'s value and — importantly — its
    /// documented caveat: this bounds the PAYLOAD budget arithmetic below, not the real wire
    /// envelope, which is larger because the payload travels twice and the text copy is escaped.
    /// </summary>
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// The budget <see cref="BuildPage"/> actually measures the events array against — half of
    /// <see cref="MaxResponseBytes"/>, because <c>StructuredToolResult.Success</c> serialises every
    /// payload twice (a text <c>Content</c> block plus <c>StructuredContent</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why 2000 events cannot simply be returned — MEASURED, not assumed</b> (risk 4's discipline:
    /// re-measure by serialising every time a field is added). Against a realistic engine event (a
    /// <c>step-attempt</c> carrying a step id, an attempt number, a <c>tMs</c> and a small
    /// three-field observation object), asking for the full <c>limit</c> of 2000:
    /// <list type="bullet">
    /// <item><description>events actually returned: <b>224</b>;</description></item>
    /// <item><description>serialised result: <b>32,827&#160;B</b> — 59&#160;B over this budget, which
    /// is the <c>eventSchemaVersion</c> and <c>nextCursor</c> scalars sitting outside the events
    /// array the budget is spent against;</description></item>
    /// <item><description>per event: <b>146.5&#160;B</b>, so the full 2000 would have been about
    /// <b>293,000&#160;B</b> — roughly <b>9x</b> this budget.</description></item>
    /// </list>
    /// The byte budget is therefore the binding constraint on most pages, not <c>limit</c>, and that
    /// is expected rather than a degradation: a caller who asks for 2000 gets as many as fit plus a
    /// <c>nextCursor</c>, and the walk completes in a few more calls.
    /// <c>GetRunEventsOrchestratorTests.ThePayloadBudget_IsEnforcedByMeasurementEvenWhenLimitWouldAllowMore</c>
    /// re-measures this on every run and pins it as ranges (the literals move with the fixture's own
    /// field lengths; the order of magnitude does not).
    /// <para>
    /// <b>And the same caveat <c>ExplainRunOrchestrator.MaxDiagnosisResponseBytes</c> records applies
    /// here:</b> this budget bounds the PAYLOAD, and the real wire envelope is larger still, because
    /// <c>StructuredToolResult.Success</c> carries the payload twice and the text copy is an ESCAPED
    /// JSON string rather than a second verbatim one (measured there at 2.213x, not 2x). Halving is a
    /// large and necessary correction, not a sufficient one; Sprint 4 owns the re-budget for every
    /// tool at once, and the sanctioned answer there is a <c>resourceUri</c> hand-off, never a raised
    /// cap.
    /// </para>
    /// </para>
    /// <para>
    /// <b>Forward progress is guaranteed by arithmetic, not by hope.</b>
    /// <see cref="RawEventRelay.MaxEventBytes"/> (4&#160;KB) is one eighth of this budget, so at least
    /// eight events always fit and a page can never come back empty while a matching event remains. A
    /// cursor that advanced by zero events would be an infinite page walk, which is the one
    /// pagination bug that turns a bounded call into an unbounded one.
    /// <b>The "admit the first match whatever it costs" branch in <see cref="BuildPage"/> is therefore
    /// DEFENSIVE, not load-bearing</b> (a gatekeeper review corrected an earlier version of this
    /// paragraph, which presented it as the guarantee): it is unreachable for as long as
    /// <c>MaxEventBytes &lt; EffectiveEventsBudgetBytes</c> holds, because no single relayed event can
    /// then exceed an empty page's budget. That inequality is the real guarantee, and
    /// <c>GetRunEventsOrchestratorTests.TheForwardProgressInequality_IsWhatMakesTheGuaranteeTrue</c>
    /// asserts it directly rather than leaving it as an assumption the branch quietly depends on.
    /// </para>
    /// </remarks>
    internal const int EffectiveEventsBudgetBytes = MaxResponseBytes / 2;

    /// <summary>
    /// Maximum entries accepted in <c>types</c>. Generous against the engine's own event vocabulary
    /// (a handful of types), and a bound rather than an unbounded set this server would then hash
    /// into every cursor.
    /// </summary>
    public const int MaxTypeFilters = 64;

    /// <summary>
    /// Maximum characters accepted in one <c>types</c> entry, in <c>stepId</c>, and in <c>runId</c> —
    /// the same 2000 <c>SuiteEventParser</c> caps a label at, since these arguments are compared
    /// against exactly those fields.
    /// </summary>
    public const int MaxFilterValueChars = 2_000;

    /// <summary>
    /// How many leading lines are examined for an event-contract version marker. See
    /// <see cref="ResolveEventSchemaVersion"/>; fixed and independent of the cursor so every page of
    /// one run reports the SAME version, which a value derived from the current page could not
    /// promise.
    /// </summary>
    private const int VersionMarkerProbeLines = 50;

    /// <summary>Property names an event may carry its own contract version under, in preference order.</summary>
    private static readonly string[] VersionMarkerProperties = ["eventSchemaVersion", "schemaVersion"];

    /// <summary>
    /// Backstop on lines examined from one events file, mirroring <c>SuiteEventParser</c>'s own
    /// constant and rationale — a pathological file of millions of tiny lines must not balloon this
    /// scan's working set even though every individual line is already bounded.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> only so <see cref="BuildPage"/>'s own test seam can pass a small
    /// value in its place — reaching this figure honestly costs two million parsed lines per case,
    /// which buys nothing the seam does not prove more precisely.
    /// </remarks>
    internal const int MaxLinesProcessed = 2_000_000;

    private static readonly JsonSerializerOptions SizeProbeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The tool's own name, taken from the ONE place that owns it — <see cref="Tools.GetRunEventsTool"/>'s
    /// <c>Create()</c> factory, per this codebase's rule that a tool's name/description/schema belong
    /// to that factory. Read from there rather than restated so a rename cannot leave this type's
    /// caller-facing messages naming a tool that no longer exists.
    /// </summary>
    private static readonly string ToolName = Tools.GetRunEventsTool.Name;

    private readonly IRunRegistry _runRegistry;
    private readonly Workspace? _workspace;

    /// <param name="runRegistry">
    /// US-S3-01's run registry — the ONLY way a <c>runId</c> becomes a file path here. This tool only
    /// ever reads it.
    /// </param>
    /// <param name="workspace">
    /// US-S3-08's workspace, or <see langword="null"/> when none was configured. With none, the
    /// registry is <c>InMemoryRunRegistry</c> and so the reachable runs are exactly this session's —
    /// which is the honest scope of this tool in that mode, and needs no separate enforcement here.
    /// </param>
    public GetRunEventsOrchestrator(IRunRegistry runRegistry, Workspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(runRegistry);
        _runRegistry = runRegistry;
        _workspace = workspace;
    }

    /// <summary>Resolves, reads, filters, and pages one run's raw event stream.</summary>
    public async Task<GetRunEventsOutcome> GetAsync(GetRunEventsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ValidateArguments(request, out var filters, out var limit) is { } argumentError)
        {
            return argumentError;
        }

        // The cursor is verified BEFORE the registry is touched: a cursor this server did not issue
        // is a fact about the ARGUMENTS, knowable without any I/O, and reporting "no such run" first
        // for a call that also carries a foreign cursor would send the caller after the wrong
        // problem.
        var startLine = 0L;
        if (request.Cursor is not null)
        {
            if (!OpaqueCursor.TryDecode(
                    request.Cursor, CursorScopes.RunEvents, filters.CursorBinding, out startLine, out var rejection))
            {
                return new GetRunEventsOutcome.InvalidCursor(
                    OpaqueCursor.DescribeRejection(rejection, ToolName));
            }
        }

        var entry = _runRegistry.TryGetRun(filters.RunId);
        if (entry is null)
        {
            return new GetRunEventsOutcome.RunNotFound(
                $"No run with id '{VfxCode.SanitiseForEcho(filters.RunId)}' is in the run registry. The "
                + "registry spans server restarts when the server was launched with --workspace, and is "
                + "session-scoped otherwise; call run_suite first, or check the 'runId' run_suite "
                + "returned for the run you meant.");
        }

        // Already absolute (every registry mints an absolute path), and still checked — the same
        // "nothing is exempt from containment" rule ExplainRunOrchestrator records at length.
        var resolvedPath = entry.EventsFilePath;
        var displayPath = PathSafetyGuard.CapAndSanitisePathForDisplay(resolvedPath);

        if (PathSafetyGuard.CheckLocalPath(resolvedPath, _workspace, displayPath) is { } pathError)
        {
            return new GetRunEventsOutcome.InvalidPath(pathError.Message);
        }

        if (!File.Exists(resolvedPath))
        {
            return new GetRunEventsOutcome.EventsFileNotFound(
                $"The run '{VfxCode.SanitiseForEcho(entry.RunId)}' is recorded in the registry, but its "
                + $"events file no longer exists: '{displayPath}'. The run's metadata outlives its event "
                + "stream when the file is deleted or the output directory is cleaned.");
        }

        var (content, contentTruncated) = await EventsFileReader.TryReadBoundedAsync(resolvedPath, cancellationToken);
        if (content is null)
        {
            return new GetRunEventsOutcome.EventsFileUnreadable(
                $"The events file could not be read: '{displayPath}'.");
        }

        return new GetRunEventsOutcome.Paged(BuildPage(content, filters, limit, startLine, contentTruncated));
    }

    /// <summary>The caller's filters, normalised once — and the cursor binding derived from them.</summary>
    /// <remarks>
    /// <see langword="internal"/>, with <see cref="ValidateArguments"/> and <see cref="BuildPage"/>,
    /// purely so a test can drive the page builder directly with the SAME filters the production path
    /// produces rather than an imitation of them. Nothing outside this assembly sees any of the three.
    /// </remarks>
    internal sealed record Filters(string RunId, HashSet<string>? Types, string? StepId, string CursorBinding)
    {
        /// <summary>
        /// <see langword="true"/> when this request selects EVERY event — the condition under which an
        /// over-long line's label-less marker may be admitted (see <see cref="BuildPage"/>).
        /// </summary>
        public bool SelectsEveryEvent => Types is null && StepId is null;
    }

    /// <summary>
    /// Applies every argument bound. Returns the refusal, or <see langword="null"/> with
    /// <paramref name="filters"/> and <paramref name="limit"/> set.
    /// </summary>
    /// <remarks>
    /// Typed as the concrete <see cref="GetRunEventsOutcome.InvalidArgument"/> rather than the base
    /// union, because every refusal this method can produce IS one — an argument this server rejects
    /// on its own terms, VFX-E-1006. Widening it to the union would be a claim this method can also
    /// report a cursor, registry, or filesystem outcome, which it cannot (and CA1859 objects to the
    /// unnecessary abstraction).
    /// </remarks>
    internal static GetRunEventsOutcome.InvalidArgument? ValidateArguments(
        GetRunEventsRequest request, out Filters filters, out int limit)
    {
        filters = null!;
        limit = DefaultLimit;

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            return new GetRunEventsOutcome.InvalidArgument(
                $"{ToolName} requires 'runId' — the id run_suite returned (as 'runId') for the run "
                + "whose events you want.");
        }

        if (request.RunId.Length > MaxFilterValueChars)
        {
            return new GetRunEventsOutcome.InvalidArgument(
                $"{ToolName}'s 'runId' must be at most {MaxFilterValueChars} characters.");
        }

        if (request.Limit is { } requestedLimit && (requestedLimit < 1 || requestedLimit > MaxLimit))
        {
            return new GetRunEventsOutcome.InvalidArgument(
                $"{ToolName}'s 'limit' must be between 1 and {MaxLimit} (spec §4.5); the default is "
                + $"{DefaultLimit}. Got: {requestedLimit}. It is refused rather than clamped so a short "
                + "page is never mistaken for the end of the stream.");
        }

        limit = request.Limit ?? DefaultLimit;

        if (request.StepId is { Length: > MaxFilterValueChars })
        {
            return new GetRunEventsOutcome.InvalidArgument(
                $"{ToolName}'s 'stepId' must be at most {MaxFilterValueChars} characters.");
        }

        HashSet<string>? types = null;
        if (request.Types is { Count: > 0 } requestedTypes)
        {
            if (requestedTypes.Count > MaxTypeFilters)
            {
                return new GetRunEventsOutcome.InvalidArgument(
                    $"{ToolName}'s 'types' must name at most {MaxTypeFilters} event types. Got: "
                    + $"{requestedTypes.Count}. Omit 'types' to receive every type.");
            }

            types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in requestedTypes)
            {
                if (type is null)
                {
                    return new GetRunEventsOutcome.InvalidArgument(
                        $"{ToolName}'s 'types' must not contain a null entry.");
                }

                if (type.Length > MaxFilterValueChars)
                {
                    return new GetRunEventsOutcome.InvalidArgument(
                        $"Each entry in {ToolName}'s 'types' must be at most {MaxFilterValueChars} characters.");
                }

                types.Add(type);
            }
        }

        filters = new Filters(request.RunId, types, request.StepId, ComposeBinding(request.RunId, types, request.StepId));
        return null;
    }

    /// <summary>
    /// Builds the cursor's filter binding from the arguments that decide WHICH events the page walk
    /// enumerates.
    /// </summary>
    /// <remarks>
    /// <b><c>types</c> is bound as a SORTED SET, not as the caller's array.</b> Two calls that name
    /// the same types in a different order — or that repeat one — select an identical result set, so
    /// refusing the second caller's cursor would be a false alarm. <b><c>limit</c> is deliberately
    /// absent</b>: changing the page size mid-walk is legitimate (see
    /// <see cref="OpaqueCursor.ComposeBinding"/>'s remarks). <b><c>runId</c> IS bound</b>, so a cursor
    /// from one run can never be replayed against another.
    /// </remarks>
    private static string ComposeBinding(string runId, HashSet<string>? types, string? stepId)
    {
        var canonicalTypes = types is null
            ? null
            : string.Join('\n', types.OrderBy(type => type, StringComparer.Ordinal));

        return OpaqueCursor.ComposeBinding(runId, canonicalTypes, stepId);
    }

    /// <summary>
    /// Scans <paramref name="content"/> from <paramref name="startLine"/>, keeping matching events
    /// until <paramref name="limit"/> or <see cref="EffectiveEventsBudgetBytes"/> stops the page,
    /// then looks ONE further match ahead to decide whether a cursor is owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The position a cursor carries is a LINE INDEX</b> into the events file — the 0-based index
    /// of the next line to examine. That is an implementation detail and stays one: it is never named
    /// in the tool's description, in the docs, or in a result field (see <see cref="OpaqueCursor"/>),
    /// so it can become a byte offset or an event ordinal without any contract changing. It is a
    /// sound position specifically because the engine writes an events file ONCE, complete, when the
    /// run ends (<c>SuiteEventParser</c>'s "buffered, not tailable" finding) — a line index into an
    /// append-in-progress file would not be.
    /// </para>
    /// <para>
    /// <b>The look-ahead is what makes <c>nextCursor</c> mean something.</b> Without it, the last full
    /// page of a filtered walk would still carry a cursor, and the caller would learn the walk was
    /// over only by fetching an empty page. With it, <c>nextCursor</c> present ⇒ at least one further
    /// matching event exists, and the cursor points AT that event rather than at the gap before it.
    /// </para>
    /// <para>
    /// <b>An OVER-LONG line is refused before it is parsed, and reported rather than dropped.</b> A
    /// line past <see cref="RawEventRelay.MaxEventLineChars"/> is never handed to
    /// <see cref="JsonDocument"/> at all (see that constant: the parse is work, and bounding only the
    /// OUTPUT left the work unbounded), so nothing here knows its <c>type</c> or <c>stepId</c>. It is
    /// therefore admitted only to an UNFILTERED page, as
    /// <see cref="RawEventRelay.OverLongLineMarker"/>: with a <c>types</c>/<c>stepId</c> filter
    /// active, claiming a line matches when this server cannot read what it is would corrupt exactly
    /// the timeline the filter was asked to produce, so it is passed over instead — and the page then
    /// reports <see cref="GetRunEventsResult.Truncated"/>. That last part is NOT the treatment an
    /// ordinary non-matching line gets, deliberately: a line skipped on a filter it demonstrably fails
    /// is absent because the caller asked for it to be, whereas this one is absent with its match
    /// status UNKNOWN, so the filtered page may be missing an event the filter would have selected.
    /// Leaving <c>truncated</c> false there contradicted both that field's own meaning and
    /// <see cref="RawEventRelay"/>'s "nothing is dropped silently" rule.
    /// </para>
    /// </remarks>
    /// <param name="contentTruncated">
    /// Whether <see cref="EventsFileReader"/> reported that <paramref name="content"/> is only the
    /// first <see cref="EventsFileReader.MaxEventsFileBytes"/> of a larger file — surfaced on the
    /// result as <see cref="GetRunEventsResult.Truncated"/>.
    /// </param>
    /// <param name="maxLines">
    /// The line-scan backstop, defaulting to <see cref="MaxLinesProcessed"/>. A parameter purely so
    /// tests can reach the cap without a two-million-line fixture (see that constant's remarks).
    /// </param>
    internal static GetRunEventsResult BuildPage(
        string content,
        Filters filters,
        int limit,
        long startLine,
        bool contentTruncated,
        int maxLines = MaxLinesProcessed)
    {
        var events = new List<JsonElement>();
        var usedBytes = 0;
        long? nextPosition = null;
        var lineCapReached = false;
        var overLongLineSkippedUnderFilter = false;

        using var reader = new StringReader(content);
        var lineIndex = 0L;
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            // Tested AFTER the read rather than in the loop condition, deliberately: reaching the cap
            // is only TRUNCATION if a further line actually existed, and having read it is the only
            // way to know that without a second look-ahead.
            if (lineIndex >= maxLines)
            {
                lineCapReached = true;
                break;
            }

            var currentLine = lineIndex++;
            if (currentLine < startLine)
            {
                continue;
            }

            if (rawLine.Length > RawEventRelay.MaxEventLineChars)
            {
                if (!filters.SelectsEveryEvent)
                {
                    // Passed over, and SAID SO. Skipping is the right call — this line's type was
                    // never read, so asserting it matches the caller's filter would corrupt the
                    // timeline they narrowed on purpose — but doing it silently was not: unlike an
                    // ordinary non-matching line, this one was dropped with its match status UNKNOWN,
                    // so what came back may be missing an event the filter would have selected.
                    // `truncated` is exactly the "what was read is not all there was" signal for that,
                    // and RawEventRelay's own header rule is that nothing is dropped silently.
                    overLongLineSkippedUnderFilter = true;
                    continue;
                }

                if (!TryAdmit(
                        RawEventRelay.OverLongLineMarker(RawEventRelay.ByteCountOf(rawLine)),
                        currentLine, events, limit, ref usedBytes, ref nextPosition))
                {
                    break;
                }

                continue;
            }

            if (!TryParseEvent(rawLine, out var document))
            {
                continue;
            }

            using (document)
            {
                var source = document.RootElement;
                if (!Matches(source, filters))
                {
                    continue;
                }

                if (!TryAdmit(
                        RawEventRelay.Relay(source, RawEventRelay.ByteCountOf(rawLine)),
                        currentLine, events, limit, ref usedBytes, ref nextPosition))
                {
                    break;
                }
            }
        }

        var nextCursor = nextPosition is { } position
            ? OpaqueCursor.Encode(CursorScopes.RunEvents, filters.CursorBinding, position)
            : null;

        return new GetRunEventsResult(
            ResolveEventSchemaVersion(content),
            events,
            nextCursor,
            contentTruncated || lineCapReached || overLongLineSkippedUnderFilter);
    }

    /// <summary>
    /// Adds one matched event to the page, or reports that the page is full and
    /// <paramref name="currentLine"/> is where the next one starts.
    /// </summary>
    /// <returns><see langword="false"/> when the caller must stop scanning.</returns>
    private static bool TryAdmit(
        RelayedEvent relayed,
        long currentLine,
        List<JsonElement> events,
        int limit,
        ref int usedBytes,
        ref long? nextPosition)
    {
        // The page is already full by count: this match is the proof that a further page exists, and
        // its own line is where that page starts.
        if (events.Count >= limit)
        {
            nextPosition = currentLine;
            return false;
        }

        // `+ 1` per event covers the comma the JSON array separator costs, so the budget is spent
        // against what the array will actually serialise to rather than against the sum of its
        // elements in isolation.
        //
        // The `events.Count > 0` guard admits the first match whatever it costs. It is DEFENSIVE and
        // currently unreachable — see EffectiveEventsBudgetBytes' remarks: no relayed event can
        // exceed RawEventRelay.MaxEventBytes, which is one eighth of the budget. It stays because the
        // day those two constants are re-tuned against each other, this is the line that keeps a page
        // from coming back empty while a matching event remains, i.e. keeps the walk terminating.
        var cost = relayed.SerialisedBytes + 1;
        if (events.Count > 0 && usedBytes + cost > EffectiveEventsBudgetBytes)
        {
            nextPosition = currentLine;
            return false;
        }

        usedBytes += cost;
        events.Add(relayed.Element);
        return true;
    }

    /// <summary>
    /// Parses one line as an event object, tolerating anything that is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line that is not valid JSON, that exceeds <see cref="RawEventRelay.MaxParseDepth"/>, or that
    /// is JSON but not an OBJECT is skipped rather than reported — the same tolerance
    /// <c>SuiteEventParser</c> applies, and for the same reason: one malformed or
    /// forward-incompatible line must never make an otherwise-good run's events unreadable. A trailing
    /// partial line left by the 50&#160;MB read cap falls out of this naturally.
    /// </para>
    /// <para>
    /// A line whose EVENT would merely nest deeper than <see cref="RawEventRelay.MaxDepth"/> is
    /// deliberately NOT in that list: it parses here (the parse allowance is the larger
    /// <see cref="RawEventRelay.MaxParseDepth"/>) and is then refused visibly by the relay, as the
    /// truncation marker. Skipping it here instead would put a silent hole in a raw-event stream.
    /// </para>
    /// <para>
    /// The <see cref="RawEventRelay.MaxEventLineChars"/> guard is repeated here as a backstop for
    /// <see cref="ResolveEventSchemaVersion"/>, which is the one caller that has no marker to fall
    /// back on and simply wants the next candidate line. <see cref="BuildPage"/> checks the same bound
    /// BEFORE calling in, because there an over-long line is reportable rather than skippable.
    /// </para>
    /// </remarks>
    private static bool TryParseEvent(string rawLine, out JsonDocument document)
    {
        document = null!;

        if (rawLine.Length > RawEventRelay.MaxEventLineChars)
        {
            return false;
        }

        var line = rawLine.Trim('\r', ' ', '\t');
        if (line.Length == 0)
        {
            return false;
        }

        try
        {
            var parsed = JsonDocument.Parse(line, RawEventRelay.ParseOptions);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                return false;
            }

            document = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Matches(JsonElement source, Filters filters)
    {
        if (filters.Types is { } types)
        {
            var type = RawEventRelay.RawStringProperty(source, "type");
            if (type is null || !types.Contains(type))
            {
                return false;
            }
        }

        if (filters.StepId is { } stepId)
        {
            var eventStepId = RawEventRelay.RawStringProperty(source, "stepId");
            if (!string.Equals(eventStepId, stepId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The event-contract version to report — read from the STREAM when it declares one, and
    /// otherwise the vendored composed schema's own version marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MEASURED against the real pinned CLI: the probe FIRES in production.</b> Every event the
    /// engine writes at the pinned commit carries a <c>"v":1,"schemaVersion":"v1"</c> prefix, so
    /// <c>schemaVersion</c> — the second entry in <see cref="VersionMarkerProperties"/> — is found on
    /// the FIRST line of a real events file and <c>"v1"</c> is what a caller receives. (An earlier
    /// version of this remark asserted the opposite, "the stream declares nothing", inferred from
    /// <c>SuiteEventParser.RunEvent</c> having no version field and from this repo's own fixtures
    /// carrying none. Both observations were true and the conclusion drawn from them was wrong: this
    /// server's parser ignores the field, which is not the same as the engine not writing it. A spec
    /// review caught it by running the engine.)
    /// </para>
    /// <para>
    /// <b>The fallback therefore covers a stream that declares NO version</b> — an events file
    /// produced by an older engine, one truncated before its first complete line, or one whose first
    /// <see cref="VersionMarkerProbeLines"/> lines are all unparseable. It is the vendored schema's
    /// version rather than <see langword="null"/> or a literal: spec §5.11 types the field as a
    /// non-optional <c>string</c>, so <see langword="null"/> would break the declared shape, and a
    /// hardcoded <c>"v1"</c> would be a second, hand-maintained copy of a version that already exists
    /// in exactly one place — the vendored schema's own <c>x-vouchfx-schema-version</c> marker,
    /// byte-exact from the engine at the pinned commit and drift-gated in CI.
    /// <c>VendoredSchemaVersion</c> exists for precisely this "do not restate the pinned version"
    /// reason (it is also what <c>meta.schemaVersion</c> reports), so the fallback is the honest
    /// statement "these events were read under the contract this server is pinned to", not a claim
    /// about a marker that particular stream never carried.
    /// </para>
    /// <para>
    /// <b><c>eventSchemaVersion</c> is probed first even though the engine does not emit it.</b> It is
    /// spec §5.11's own name for this concept, so a future engine adopting the spec's spelling wins
    /// over the one it uses today, additively and with no contract change. The probe reads a FIXED
    /// leading window (<see cref="VersionMarkerProbeLines"/> lines) rather than the current page, so
    /// every page of one run reports the same value — a version derived from whatever happened to be
    /// on page 7 would be a field whose meaning depended on the cursor.
    /// </para>
    /// </remarks>
    internal static string ResolveEventSchemaVersion(string content)
    {
        using var reader = new StringReader(content);
        string? rawLine;
        var linesProbed = 0;
        while (linesProbed < VersionMarkerProbeLines && (rawLine = reader.ReadLine()) is not null)
        {
            linesProbed++;

            if (!TryParseEvent(rawLine, out var document))
            {
                continue;
            }

            using (document)
            {
                foreach (var property in VersionMarkerProperties)
                {
                    if (RawEventRelay.RawStringProperty(document.RootElement, property) is { Length: > 0 } declared)
                    {
                        var capped = declared.Length > MaxMarkerVersionChars
                            ? declared[..MaxMarkerVersionChars]
                            : declared;
                        return TextSanitiser.SanitiseForDisplay(capped);
                    }
                }
            }
        }

        return VendoredSchemaVersion.Value;
    }

    /// <summary>Cap on a stream-declared version string — a version is a short token, and this one reaches every result.</summary>
    private const int MaxMarkerVersionChars = 64;

    /// <summary>
    /// The serialised UTF-8 byte count of <paramref name="result"/> — the measurement
    /// <c>GetRunEventsOrchestratorTests</c> uses to pin the budget figures documented on
    /// <see cref="EffectiveEventsBudgetBytes"/>, rather than asserting them from arithmetic.
    /// </summary>
    internal static int SerialisedByteCount(GetRunEventsResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, SizeProbeOptions).Length;
}
