using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Schema;

/// <summary>
/// <c>get_schema</c>'s pipeline (US-S2-01): resolve a section of the embedded, drift-gated composed
/// schema, render it in the requested format, and — when a pinned CLI is actually present —
/// cross-verify the embedded document against that engine's own <c>vouchfx schema</c> export.
/// </summary>
/// <remarks>
/// <para>
/// <b>CLI-OPTIONAL, not CLI-backed — and that is the whole design.</b> The five pinned-CLI-backed
/// tools (<c>list_step_types</c>, <c>describe_step_type</c>, <c>plan_coverage</c>,
/// <c>scaffold_suite</c>, <c>run_suite</c>) fail closed without an engine because they have no
/// offline answer: only the engine knows a suite's verdict or the live catalogue's field metadata.
/// <c>get_schema</c> DOES have an offline answer — the vendored composed schema, byte-pinned to the
/// engine commit in <c>ENGINE_PIN</c> and drift-gated in CI — so it serves it, exactly as
/// <c>validate_suite</c> and <c>search_docs</c> already do. The CLI contributes a CHECK, never the
/// content.
/// </para>
/// <para>
/// <b>Why the vendored copy is what gets SERVED, even in live mode.</b> Two reasons, and neither is
/// a preference for one source over the other: (1) determinism — the vendored document is the same
/// bytes on every machine, CI included, whereas the live export varies with whatever happens to be
/// installed; (2) consistency with the rest of this server — <c>validate_suite</c>'s isolated
/// worker evaluates the EMBEDDED schema (see <see cref="LiveSchemaDocument"/>'s own remarks on why
/// it stays that way), so serving a different document from <c>get_schema</c> would let an author
/// design against one contract and be validated against another. When the two disagree, that fact
/// is REPORTED (<see cref="VfxCodeCatalogue.LiveSchemaMismatch"/>) rather than resolved silently in
/// either direction — the caller learns both that its engine differs and which document it just
/// received.
/// </para>
/// <para>
/// <b>This is <see cref="LiveSchemaDocument"/>'s first caller.</b> That type has been fully
/// implemented since REQ-010 but was never constructed by
/// <see cref="VouchfxMcpServerRegistration"/>; this story is that wiring. Everything about the
/// fail-closed pin handshake, the output cap, and the never-cache-a-failure policy already lives
/// there and is deliberately not re-implemented here.
/// </para>
/// </remarks>
public sealed class GetSchemaOrchestrator
{
    /// <summary>The default <c>format</c>: the section as a JSON Schema document.</summary>
    public const string JsonSchemaFormat = "json-schema";

    /// <summary>The markdown-digest <c>format</c> (see <see cref="SchemaSummaryRenderer"/>).</summary>
    public const string SummaryFormat = "summary";

    /// <summary>Every accepted <c>format</c> value, in the order the tool advertises them.</summary>
    public static IReadOnlyList<string> Formats { get; } = [JsonSchemaFormat, SummaryFormat];

    /// <summary>
    /// The embedded schema in <see cref="SchemaJsonCanonicaliser"/> form, computed ONCE at type
    /// initialisation. It is one side of every cross-verification comparison and never changes for
    /// the process's lifetime, so re-serialising it per call would be pure waste (measured
    /// 2026-09-15: 167&#160;854 raw characters in, 125&#160;655 canonical characters out). A
    /// <see langword="string"/> is immutable and therefore safe to share across the concurrent tool
    /// calls this server serves.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="SchemaJsonCanonicaliser.CanonicaliseTolerant"/> so BOTH sides of
    /// every comparison run the identical pipeline (issue #89). On the vendored document that is a
    /// no-op — the committed file carries no raw control characters — so this value is byte-identical
    /// to the strict <see cref="SchemaJsonCanonicaliser.Canonicalise(string)"/> form the tests still
    /// compute; the symmetry is what stops a future pre-pass change from silently applying to one
    /// side only.
    /// </remarks>
    private static readonly string VendoredCanonicalJson =
        SchemaJsonCanonicaliser.CanonicaliseTolerant(VendoredComposedSchema.RawJson);

    private readonly LiveSchemaDocument _liveSchema;

    /// <summary>
    /// The encoding the engine child writes its redirected stdout in — the one this server ALSO
    /// decodes that stdout with (<see cref="EngineOutputEncoding.Current"/> is the single resolution
    /// site for both). Injectable purely so tests can exercise the code-page projection path below on
    /// a host whose own console is UTF-8; production never passes it.
    /// </summary>
    private readonly Encoding _engineOutputEncoding;

    /// <summary>
    /// The vendored schema as it would look after a round trip through
    /// <see cref="_engineOutputEncoding"/> — i.e. what the engine would have handed this server had
    /// its export been byte-identical to the pin. <see langword="null"/> when the encoding is UTF-8
    /// (nothing is transcoded, so there is nothing to model) or when the projection itself cannot be
    /// canonicalised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the projection is taken from <see cref="VendoredComposedSchema.RawJson"/> and not from
    /// <see cref="VendoredCanonicalJson"/>.</b> MEASURED 2026-09-15:
    /// <c>JsonSerializer.Serialize(JsonElement)</c> uses <c>JavaScriptEncoder.Default</c>, which
    /// escapes every non-ASCII character as <c>\uXXXX</c> — the canonical form of the pinned schema is
    /// 125&#160;655 characters of PURE ASCII. Projecting THAT through a code page would be an identity
    /// transform and the comparison would never fire. The raw vendored text still carries the literal
    /// <c>—</c>, <c>§</c> and <c>…</c>, which is what the console page actually mangles, so the round
    /// trip has to happen before canonicalisation.
    /// </para>
    /// <para>
    /// <b>Lazy, and per-instance.</b> Computing it costs a 167&#160;854-character encode/decode plus a
    /// parse, and a UTF-8 host must never pay it. Per-instance rather than static because the encoding is a
    /// constructor parameter; production builds exactly one orchestrator per server process, so
    /// "process lifetime" and "instance lifetime" coincide there.
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is the default and is what makes the
    /// value computed at most once under the concurrent tool calls this server serves.
    /// </para>
    /// </remarks>
    private readonly Lazy<string?> _vendoredConsoleProjection;

    /// <summary>
    /// The cross-verification OUTCOME, memoised for the process lifetime — computed at most once
    /// however it turns out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the memo lives here and not in <see cref="LiveSchemaDocument"/>.</b> That type caches
    /// only <see cref="LiveSchemaLoadResult.Ok"/>, deliberately: for the five CLI-BACKED tools a
    /// failure must never be sticky — an engine installed (or a PATH fixed) mid-session has to start
    /// working without a server restart, because those tools have no other answer. <c>get_schema</c>
    /// is the opposite case: it already has a complete offline answer, and the probe is pure
    /// optional verification. Left uncached, every single call re-walks PATH when no CLI is present,
    /// or re-spawns <c>vouchfx --version</c> when an installed one mismatches the pin (~100–300&#160;ms
    /// each, serialised behind that type's load gate) — a per-call cost on a cheap, frequently-called
    /// authoring tool, paid for a fact that does not change.
    /// </para>
    /// <para>
    /// <b>The deliberate trade-off:</b> a CLI installed or repaired mid-session gets live
    /// cross-verification only after this server restarts. Accepted — the verification is an
    /// environment check, not the answer, and a stale "no engine present" costs the caller nothing
    /// beyond the absence of a warning it was already living without.
    /// </para>
    /// <para>
    /// Runs on <see cref="CancellationToken.None"/> so one caller's cancellation cannot poison the
    /// memoised task for every later call; the caller's own token is honoured at the await instead
    /// (<see cref="GetCrossVerificationAsync"/>). This is safe precisely because the probe is
    /// wall-clock bounded from the inside — <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/>
    /// applies its own timeout to every spawn — so the memoised task always completes.
    /// </para>
    /// <para>
    /// <b>Memoising an outcome PERMANENTLY is only safe because the probe cannot fault, and that
    /// property is spread across three files plus one method in this one.</b> A memoised faulted
    /// task would re-throw the same exception to every later <c>get_schema</c> call for the life of
    /// the process, turning one transient environment hiccup into a permanently broken
    /// offline-capable tool. It cannot happen today because, in full:
    /// <list type="number">
    /// <item><description><see cref="LiveSchemaDocument"/> converts every non-cancellation failure
    /// into <see cref="LiveSchemaLoadResult.Unavailable"/> (its <c>LoadCoreAsync</c>);</description></item>
    /// <item><description><see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> resolves its OWN
    /// wall clock to a <c>TimedOut</c> result and rethrows
    /// <see cref="OperationCanceledException"/> only for the caller's token;</description></item>
    /// <item><description>the factory above passes <see cref="CancellationToken.None"/>, so there is
    /// no caller token to cancel;</description></item>
    /// <item><description><see cref="ProjectVendoredThroughEngineOutputEncoding"/> — the
    /// <see cref="_vendoredConsoleProjection"/> factory, added in issue #89 — is TOTAL: it catches
    /// every non-cancellation failure and answers <see langword="null"/>. This one needs naming
    /// because a <see cref="Lazy{T}"/> factory exception is CACHED and rethrown on every later read,
    /// so a throwing projection would fault this memo permanently rather than transiently. Belt and
    /// braces: <see cref="CrossVerifyAgainstLiveEngineAsync"/> also reads that
    /// <see cref="Lazy{T}"/> from inside its own guard.</description></item>
    /// </list>
    /// Change any one of those four and this memo starts caching a fault — a <c>try</c>/<c>catch</c>
    /// here would be the fix.
    /// </para>
    /// <para>
    /// <b>The factory is dispatched through <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>,
    /// and that is not ceremony.</b> That overload — the one that UNWRAPS the inner task rather than
    /// handing back a <c>Task&lt;Task&lt;…&gt;&gt;</c> — is what makes the memoised
    /// <see cref="Lazy{T}"/> hold the probe's real completion rather than merely its scheduling.
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> runs the factory
    /// under a Monitor, and an <c>async</c> method runs SYNCHRONOUSLY on its caller's thread until
    /// its first genuine yield — for <see cref="CrossVerifyAgainstLiveEngineAsync"/> that stretch
    /// covers <see cref="LiveSchemaDocument"/>'s semaphore fast path, the pin handshake, a PATH walk,
    /// and <c>Process.Start</c>, all of which touch the filesystem. Called directly, the FIRST caller
    /// would block its thread-pool thread inside that lock while every concurrent caller queued
    /// behind the same lock — and, worse, would do so before reaching the
    /// <see cref="Task.WaitAsync(CancellationToken)"/> in <see cref="GetCrossVerificationAsync"/>,
    /// so its cancellation token would not be observed for the whole synchronous stretch. Task.Run
    /// makes the Monitor-held section a bare scheduling call, so the lock is released immediately and
    /// the awaiting callers stay asynchronous and cancellable throughout.
    /// </para>
    /// </remarks>
    private readonly Lazy<Task<IReadOnlyList<Diagnostic>?>> _crossVerification;

    /// <param name="liveSchema">
    /// The live <c>vouchfx schema</c> loader used for cross-verification only. Owned by the caller
    /// (<see cref="VouchfxMcpServerRegistration"/> constructs one per server process); this type
    /// never disposes it.
    /// </param>
    /// <param name="engineOutputEncoding">
    /// The encoding the engine child writes its redirected stdout in.
    /// <see langword="null"/> — the production value — means
    /// <see cref="EngineOutputEncoding.Current"/>, so <see cref="VouchfxMcpServerRegistration"/>
    /// stays the single DI configuration and gains no second registration path.
    /// <para>
    /// <b>Why tests inject rather than read the ambient page.</b> The ambient console output code
    /// page varies by HOST and by LAUNCHER, not just by platform: observed 2026-09-15 on one Windows
    /// machine, a test host started from one shell resolved 852 while a <c>dotnet run</c> from
    /// another resolved 65001, and a Linux CI runner is always UTF-8. A test that read the ambient
    /// page would therefore assert a different thing on each of them — and would silently stop
    /// exercising the projection path on any host that happens to sit at 65001. Injecting the
    /// encoding is what makes the same assertion hold everywhere.
    /// </para>
    /// </param>
    public GetSchemaOrchestrator(LiveSchemaDocument liveSchema, Encoding? engineOutputEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(liveSchema);

        _liveSchema = liveSchema;
        _engineOutputEncoding = engineOutputEncoding ?? EngineOutputEncoding.Current;
        _vendoredConsoleProjection = new Lazy<string?>(
            () => ProjectVendoredThroughEngineOutputEncoding(_engineOutputEncoding));
        _crossVerification = new Lazy<Task<IReadOnlyList<Diagnostic>?>>(
            () => Task.Run(() => CrossVerifyAgainstLiveEngineAsync(CancellationToken.None)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Resolves and renders one schema section.</summary>
    /// <param name="section">
    /// A <see cref="SchemaSectionResolver"/> token, or <see langword="null"/>/blank for
    /// <see cref="SchemaSectionResolver.FullSection"/>.
    /// </param>
    /// <param name="format">
    /// One of <see cref="Formats"/>, or <see langword="null"/>/blank for
    /// <see cref="JsonSchemaFormat"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the (optional) live cross-verification probe.</param>
    public async Task<GetSchemaOutcome> GetSchemaAsync(
        string? section,
        string? format,
        CancellationToken cancellationToken = default)
    {
        var effectiveSection = string.IsNullOrWhiteSpace(section) ? SchemaSectionResolver.FullSection : section;
        var effectiveFormat = string.IsNullOrWhiteSpace(format) ? JsonSchemaFormat : format;

        if (!Formats.Contains(effectiveFormat, StringComparer.Ordinal))
        {
            // Ordinal, like the section table: the advertised enum is lower-case and must mean what
            // it says. `format` is caller input, so it is sanitised before being echoed back (M1).
            return new GetSchemaOutcome.InvalidArgument(
                $"Unknown format '{VfxCode.SanitiseForEcho(effectiveFormat)}'. Valid formats are: "
                + $"{string.Join(", ", Formats)}.");
        }

        // The cross-verification runs regardless of the requested format and regardless of which
        // section was addressed: it is a statement about the DOCUMENT this server is serving from,
        // not about the fragment the caller happened to ask for, and a host that only ever asks for
        // summaries deserves to hear about drift just as much as one asking for full schemas.
        //
        // Awaited BEFORE the 167,854-character document is parsed, not after: a JsonDocument held across
        // await would pin its pooled buffers for the whole (first-call) probe. Ordering it this way
        // costs an unknown-section call the memo's one-time probe, which is the cheaper trade.
        var diagnostics = await GetCrossVerificationAsync(cancellationToken).ConfigureAwait(false);

        using var document = VendoredComposedSchema.Parse();

        var resolution = SchemaSectionResolver.Resolve(document.RootElement, effectiveSection);
        if (resolution is SchemaSectionResolution.NotFound notFound)
        {
            return new GetSchemaOutcome.SectionNotFound(notFound.Message);
        }

        var subtree = ((SchemaSectionResolution.Ok)resolution).Subtree;

        var result = new GetSchemaResult(
            VendoredSchemaVersion.Value,
            effectiveSection,
            // Clone(): `document` is disposed when this method returns, and an un-cloned JsonElement
            // reads through its owning JsonDocument's pooled buffers — the payload would serialise
            // from freed memory (or throw) at the wire. Clone copies the subtree out, which is also
            // what keeps the returned value safe to hand across the async boundary.
            JsonSchema: string.Equals(effectiveFormat, SummaryFormat, StringComparison.Ordinal)
                ? null
                : subtree.Clone(),
            Summary: string.Equals(effectiveFormat, SummaryFormat, StringComparison.Ordinal)
                ? SchemaSummaryRenderer.Render(effectiveSection, VendoredSchemaVersion.Value, subtree, document.RootElement)
                : null,
            diagnostics);

        return new GetSchemaOutcome.Completed(result);
    }

    /// <summary>
    /// The memoised cross-verification outcome (see <see cref="_crossVerification"/>), awaited under
    /// the CALLER's cancellation token even though the probe itself runs detached from it — and
    /// genuinely awaited, never blocked on, because the memo's factory is dispatched through
    /// <see cref="Task.Run{TResult}(Func{Task{TResult}})"/> — the unwrapping overload — rather than
    /// invoked under the <see cref="Lazy{T}"/>'s lock.
    /// </summary>
    private Task<IReadOnlyList<Diagnostic>?> GetCrossVerificationAsync(CancellationToken cancellationToken) =>
        _crossVerification.Value.WaitAsync(cancellationToken);

    /// <summary>
    /// Compares the embedded composed schema against the pinned engine's own <c>vouchfx schema</c>
    /// export, returning the mismatch diagnostic or <see langword="null"/> when there is nothing to
    /// report. Called at most ONCE per server process — see <see cref="_crossVerification"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="LiveSchemaLoadResult.Unavailable"/> is NOT a finding.</b> No CLI on PATH, a
    /// version mismatch, a pre-Spec-A engine with no <c>schema</c> verb — every one of those means
    /// nothing was compared, and reporting a "mismatch" for an absent comparison would be a false
    /// claim about the engine. Offline is a supported mode of this tool, not a degraded one; the
    /// message <see cref="LiveSchemaDocument"/> attaches to that case is deliberately dropped rather
    /// than surfaced, because a host that never installed the engine does not need a warning on
    /// every schema lookup.
    /// </para>
    /// <para>
    /// <b>The message states THAT the two differ, never HOW.</b> The live document is unbounded,
    /// engine-controlled process output; splicing a diff of it into an agent-facing message would
    /// relay that text straight to a model, which is precisely what <c>TextSanitiser</c> and
    /// <c>BoundedStreamReader</c> exist to prevent everywhere else in this codebase. The remedy a
    /// caller needs is the same either way: reconcile the install against <c>ENGINE_PIN</c>.
    /// </para>
    /// <para>
    /// <b>The comparison MODELS the console code page (issue #89), in two stages, and the order is
    /// the contract.</b> Before #89 this method compared the live export against the vendored
    /// document and nothing else, which made VFX-D-1106 fire on EVERY call on any host whose console
    /// output code page cannot carry the schema's three non-ASCII characters — measured 2026-09-15
    /// under cp852, and, because a window-less-console (<c>CreateNoWindow</c>) parent and child BOTH read that same OEM page (see
    /// <see cref="EngineOutputEncoding"/>'s remarks), on headless MCP deployments too. Now:
    /// </para>
    /// <para>
    /// (1) EXACT first — an identical canonical form means the engine's schema IS the pinned one and
    /// nothing else needs saying. (2) Otherwise, and only when the engine's output encoding is not
    /// UTF-8, the live text is compared against the vendored document PROJECTED through that same
    /// encoding (<see cref="_vendoredConsoleProjection"/>). Equal means the two documents agree at
    /// every position the console can carry and differ only where the console's own best-fit mapping
    /// altered both identically — not drift, and not worth a warning. (3) Different from BOTH, or
    /// unparseable even after the control-character pre-pass, is a real finding.
    /// </para>
    /// <para>
    /// <b>The accepted residual, stated rather than hidden, and stated as the general class it is.</b>
    /// Step (2) masks any SUBSTITUTION whose old and new characters the active code page's ENCODER
    /// COLLAPSES TO THE SAME BYTE. That class includes REPRESENTABLE/UNREPRESENTABLE pairs, not only
    /// pairs of unrepresentable characters: MEASURED 2026-09-15 on cp852 — and identically on cp437
    /// and cp850 — <c>—</c> U+2014, <c>–</c> U+2013, <c>‑</c> U+2010, <c>−</c> U+2212 and plain ASCII
    /// <c>-</c> U+002D ALL encode to <c>0x2D</c> and decode back to U+002D, so an engine release that
    /// replaced the schema's em dashes with hyphen-minus would be invisible to this comparison on
    /// every OEM-page host. That is not hypothetical scale: the pinned document carries 164 em dashes,
    /// and replacing just the first of them (index 2443) with a hyphen yields a cp852 round trip
    /// BYTE-IDENTICAL to the unmodified document (measured, and pinned by
    /// <c>GetSchemaOrchestratorTests.GetSchemaAsync_OnACp852Host_AnEmDashChangedToAHyphen_IsMaskedByDesign</c>).
    /// What still diverges is an INSERTION or a DELETION, which shifts the surrounding text rather
    /// than swapping one byte for the same byte. This is the price of not firing on every call; the
    /// exact comparison in step (1) is what catches everything on a host whose page can carry every
    /// character — which is always the case on a UTF-8 console, where step (2) never runs at all.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Diagnostic>?> CrossVerifyAgainstLiveEngineAsync(CancellationToken cancellationToken)
    {
        // No try/catch here on purpose: LiveSchemaDocument already converts every non-cancellation
        // failure into Unavailable (see its LoadCoreAsync), and a cancellation must propagate as
        // cancellation rather than be reported as a schema finding.
        var load = await _liveSchema.GetOrLoadAsync(cancellationToken).ConfigureAwait(false);

        if (load is not LiveSchemaLoadResult.Ok ok)
        {
            return null;
        }

        // EVERY comparison stage sits inside this ONE guard, and the boundary is drawn here rather
        // than around the canonicalisation alone for a reason a review caught: `Lazy<T>` CACHES a
        // factory exception and rethrows it on every subsequent read, so a projection read outside
        // the guard would escape this method — and `_crossVerification` would then memoise a FAULTED
        // task and rethrow it from every get_schema call for the life of the process, exactly the
        // failure that field's remarks exist to rule out. Unreachable today (see
        // ProjectVendoredThroughEngineOutputEncoding, which is total), but "unreachable" is not a
        // structure.
        try
        {
            // Tolerant, not strict: a console code page that cannot represent a schema character
            // best-fit-maps it to a raw control byte AT THE SOURCE (MEASURED under cp852: `…` U+2026
            // becomes 0x07 inside a JSON string), so the live text does not parse at all. The pre-pass
            // escapes exactly those characters, on BOTH sides of every comparison below, and cannot
            // make a broken document valid in any other way — see
            // SchemaJsonCanonicaliser.EscapeRawControlCharacters for why that is a property of JSON
            // rather than a hope.
            var liveCanonical = SchemaJsonCanonicaliser.CanonicaliseTolerant(ok.SchemaJson);

            // (1) Exact. Unchanged, and deliberately first: on a UTF-8 host this is the only
            // comparison that ever runs and the projection is never even computed.
            if (string.Equals(liveCanonical, VendoredCanonicalJson, StringComparison.Ordinal))
            {
                return null;
            }

            // (2) The same document as seen through this host's console output code page.
            if (_vendoredConsoleProjection.Value is { } projected
                && string.Equals(liveCanonical, projected, StringComparison.Ordinal))
            {
                return null;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: see below.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // The engine emitted something that starts with '{' (LiveSchemaDocument's own shape
            // check) but does not parse even after the pre-pass. That IS a disagreement with the
            // vendored document, and the caller should hear about it in the same terms as any other
            // divergence — silently dropping it would hide a broken install behind a clean result.
            //
            // Catching Exception rather than JsonException alone, matching
            // LiveSchemaDocument.LoadCoreAsync's posture for this SAME untrusted source: this input
            // is subprocess output, and this method's whole contract is that it never throws. A
            // pathological document that trips something other than the JSON reader (an
            // OutOfMemory-adjacent guard, an encoding failure, a future canonicaliser change) must
            // still land on "the two disagree", not escape as an unhandled tool failure.
            // OperationCanceledException is excluded so cancellation stays cancellation.
            return [BuildMismatchDiagnostic()];
        }

        // (3) Different from both — a real finding.
        return [BuildMismatchDiagnostic()];
    }

    /// <summary>
    /// Round-trips the raw vendored schema through <paramref name="encoding"/> and canonicalises the
    /// result — the factory behind <see cref="_vendoredConsoleProjection"/>. Returns
    /// <see langword="null"/> when there is nothing to model (UTF-8) or when the projection cannot be
    /// canonicalised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a managed round trip faithfully models what the CHILD did.</b> MEASURED 2026-09-15:
    /// <c>Encoding.GetEncoding(852)</c> from <c>System.Text.Encoding.CodePages</c> produces exactly
    /// the bytes observed coming off the real pinned CLI — <c>GetBytes("—") = 0x2D</c>,
    /// <c>GetBytes("§") = 0xF5</c>, <c>GetBytes("…") = 0x07</c>. Also MEASURED, by a throwaway
    /// P/Invoke probe written for this story (<c>encprobe</c>): those managed bytes are IDENTICAL to
    /// <c>WideCharToMultiByte(cp, dwFlags: 0, …)</c> for all three characters on cp852, cp1250 and
    /// cp1252. That the child's own <c>OSEncoding</c> is what calls
    /// <c>WideCharToMultiByte</c> with those flags is INFERRED from the runtime's implementation, not
    /// measured — the measured part is the byte-for-byte agreement of the two encoders. So the parent
    /// can model the child's best-fit transcoding without P/Invoke of its own, and without ever
    /// changing the console (<c>SetConsoleOutputCP</c> is never called anywhere in this server).
    /// </para>
    /// <para>
    /// The null-on-failure arm is not defensive noise: a code page whose best-fit mapping produced a
    /// <c>"</c> or a <c>\</c> would make the projection itself unparseable, and the right answer then
    /// is "there is no projection to compare against" — i.e. fall through to the diagnostic — rather
    /// than an exception escaping a method whose caller's whole contract is that it never throws.
    /// </para>
    /// </remarks>
    private static string? ProjectVendoredThroughEngineOutputEncoding(Encoding encoding)
    {
        if (EngineOutputEncoding.IsUtf8(encoding))
        {
            return null;
        }

        try
        {
            var projected = encoding.GetString(encoding.GetBytes(VendoredComposedSchema.RawJson));
            return SchemaJsonCanonicaliser.CanonicaliseTolerant(projected);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: see above. No
        // `when (ex is not OperationCanceledException)` filter here, unlike the live-side arm: nothing
        // on this path takes a CancellationToken, so that filter would be dead code pretending to
        // preserve a cancellation that cannot arrive. Catching everything is what makes this factory
        // TOTAL, which is the property _crossVerification's remarks depend on.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static Diagnostic BuildMismatchDiagnostic() =>
        VfxCodeCatalogue.CreateDiagnostic(
            VfxCodeCatalogue.LiveSchemaMismatch,
            // "warning", not "error": the caller received a usable, self-consistent schema — the one
            // this server also validates against — so nothing about the answer is wrong. What is in
            // question is the pairing between this server and the engine on PATH.
            severity: "warning",
            // The message names the CAUSE and stops there; the remedy lives on the docsUrl this
            // diagnostic already carries (docs/errors/VFX-D-1106.md). Two reasons not to inline the
            // workaround prose here: it would be a second copy of that page's Fixes section, drifting
            // the moment either is edited, and a message shipped in a released binary cannot be
            // corrected when the remedies change, whereas the docs page can.
            //
            // REWORDED for issue #89. It used to say "on Windows the most likely explanation is not
            // schema drift at all but a console code-page transcoding loss" — advice that was correct
            // when the comparison ignored the console page, and is MISLEADING now that it models it:
            // by the time this diagnostic is built, the live export has already been compared against
            // the vendored document projected through that exact encoding and still differed. Telling
            // a caller to go and check the thing the comparison just ruled out would send them down
            // the one path that cannot be the answer.
            message:
                "The installed vouchfx CLI's `vouchfx schema` output differs from the vendored "
                + "composed schema embedded in this server. The vendored copy is what was returned "
                + "(it is also what validate_suite evaluates against, so the two stay consistent), "
                + "but suites authored against it may not match what the installed engine enforces. "
                + "This comparison already models the console output code page the CLI encodes its "
                + "redirected output in, so a code-page transcoding loss has been accounted for and "
                + "is not the explanation: either the installed engine's schema genuinely differs "
                + "from the one ENGINE_PIN names, or its export could not be parsed at all. See this "
                + "diagnostic's documentation for the remedies.");
}
