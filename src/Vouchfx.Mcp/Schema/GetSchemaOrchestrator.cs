using System.Text.Json;
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
    /// the process's lifetime, so re-serialising ~150&#160;KB per call would be pure waste. A
    /// <see langword="string"/> is immutable and therefore safe to share across the concurrent tool
    /// calls this server serves.
    /// </summary>
    private static readonly string VendoredCanonicalJson =
        SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson);

    private readonly LiveSchemaDocument _liveSchema;

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
    /// property is spread across three files.</b> A memoised faulted task would re-throw the same
    /// exception to every later <c>get_schema</c> call for the life of the process, turning one
    /// transient environment hiccup into a permanently broken offline-capable tool. It cannot
    /// happen today because: <see cref="LiveSchemaDocument"/> converts every non-cancellation
    /// failure into <see cref="LiveSchemaLoadResult.Unavailable"/> (its <c>LoadCoreAsync</c>);
    /// <see cref="Vouchfx.Mcp.Cli.VouchfxCliProcessRunner"/> resolves its OWN wall clock to a
    /// <c>TimedOut</c> result and rethrows <see cref="OperationCanceledException"/> only for the
    /// caller's token; and the factory above passes <see cref="CancellationToken.None"/>, so there
    /// is no caller token to cancel. Change any one of those three and this memo starts caching a
    /// fault — a <c>try</c>/<c>catch</c> here would be the fix.
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
    public GetSchemaOrchestrator(LiveSchemaDocument liveSchema)
    {
        ArgumentNullException.ThrowIfNull(liveSchema);

        _liveSchema = liveSchema;
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
        // Awaited BEFORE the ~150 KB document is parsed, not after: a JsonDocument held across this
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

        string liveCanonical;
        try
        {
            liveCanonical = SchemaJsonCanonicaliser.Canonicalise(ok.SchemaJson);
        }
#pragma warning disable CA1031 // Do not catch general exception types — deliberate: see below.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // The engine emitted something that starts with '{' (LiveSchemaDocument's own shape
            // check) but does not parse. That IS a disagreement with the vendored document, and the
            // caller should hear about it in the same terms as any other divergence — silently
            // dropping it would hide a broken install behind a clean result.
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

        return string.Equals(liveCanonical, VendoredCanonicalJson, StringComparison.Ordinal)
            ? null
            : [BuildMismatchDiagnostic()];
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
            // the moment either is edited, and it goes stale the day the decoding defect (issue #70)
            // is fixed — a message shipped in a released binary cannot be corrected then, whereas the
            // docs page can.
            message:
                "The installed vouchfx CLI's `vouchfx schema` output differs from the vendored "
                + "composed schema embedded in this server. The vendored copy is what was returned "
                + "(it is also what validate_suite evaluates against, so the two stay consistent), "
                + "but suites authored against it may not match what the installed engine enforces. "
                + "On Windows the most likely explanation is not schema drift at all but a console "
                + "code-page transcoding loss: the CLI writes its output in the console's active code "
                + "page, and if that page cannot represent every character the schema uses (an "
                + "em-dash or section sign, for example) those characters are altered before this "
                + "server receives them. Check that before reconciling the install against "
                + "ENGINE_PIN; see this diagnostic's documentation for the remedies.");
}
