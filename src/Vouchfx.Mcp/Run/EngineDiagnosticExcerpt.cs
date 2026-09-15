namespace Vouchfx.Mcp.Run;

/// <summary>
/// The ENUMERATED set of engine stdout signatures this server retains a bounded excerpt of, and the
/// bound it retains them under (vouchfx-mcp#96). Read by <see cref="VouchfxCliSuiteRunner"/>'s single
/// relay loop and surfaced as <see cref="SuiteProcessResult.StdoutDiagnosticExcerpt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an enumerated signature rather than a general stdout tail.</b> The child's stdout is
/// UNTRUSTED engine output that can echo suite-derived text (step ids, script output, a resource name
/// the author chose), so retaining an arbitrary tail of it would mint a new relay surface — a second
/// place where agent-influenced bytes reach a tool result — with nothing to say about what may appear
/// there. Matching an enumerated signature narrows the surface to text MATCHING the engine's
/// diagnostic wording — which is evidence of the WORDING, never of its provenance. A match does not
/// establish that the engine emitted the line from the code path that wording belongs to, and nothing
/// downstream is allowed to assume it did: suite-derived text echoed onto stdout can carry the phrase,
/// so the retained line is still sanitised, still capped, and still relayed only when the events
/// stream produced no verdict at all (<see cref="RunSuiteOrchestrator"/>'s fallback classifier, whose
/// hint therefore states the two things this server observed and not a mechanism it inferred). What
/// the narrowing buys is a bounded, reviewable surface instead of an open one — not trust. That is
/// also why the list is a closed enumeration in one place rather than a predicate inlined at the
/// relay: adding a signature is a reviewable edit to this file.
/// </para>
/// <para>
/// <b>The one signature, and where it comes from (MEASURED at <c>ENGINE_PIN</c> v1.0.0-rc.5,
/// 2026-09-15).</b> A suite whose <c>environment.dependencies.&lt;name&gt;.env</c> names a variable the
/// engine sets itself for that dependency type (at rc.5: elasticsearch's <c>discovery.type</c>,
/// <c>xpack.security.enabled</c>, <c>ES_JAVA_OPTS</c>,
/// <c>cluster.routing.allocation.disk.threshold_enabled</c>; minio's <c>MINIO_ROOT_USER</c>,
/// <c>MINIO_ROOT_PASSWORD</c>; azureservicebus's <c>ACCEPT_EULA</c>, <c>MSSQL_SA_PASSWORD</c>,
/// <c>SQL_SERVER</c>) is REFUSED by the engine's <c>EnvironmentMapper</c>, which
/// <c>RunSuiteAsync</c> reports as a single stdout line prefixed
/// <c>"RunSuiteAsync: environment configuration error - "</c>. Measured shape of that run: exit code 4
/// (Inconclusive), stdout exactly one line, stderr EMPTY, and <b>no events file written at all</b> —
/// the refusal fires before any topology is built, so nothing downstream of the events stream can see
/// it. That is precisely the gap this type closes: without it a host receives
/// <c>verdict: Inconclusive</c>, no steps, and no hint, and a later <c>explain_run</c>/<c>diagnose_run</c>
/// finds no events file either. <c>vouchfx validate</c> does not report the condition at all (the
/// vendored schema's own <c>env</c> description says so), which is why the run path is the only place
/// this text exists.
/// </para>
/// <para>
/// <b>The constant is the engine's wording at the pin, not a contract.</b> A pin bump can reword or
/// re-route it; the consequence of a miss is exactly the pre-#96 behaviour (an Inconclusive verdict
/// with a <see langword="null"/> hint), never a wrong answer — the verdict comes from the exit code
/// and the events stream, never from this match. Matching is case-insensitive SUBSTRING so a prefix
/// change ahead of the phrase does not silence it.
/// </para>
/// <para>
/// <b>What is deliberately NOT retained: the author's VALUE.</b> The engine omits it from that message
/// on purpose — two of the nine refused names are passwords — and this server relays the engine's
/// sentence verbatim rather than enriching it from the suite, so the omission survives the relay. This
/// type never reads a suite, an environment, or a secret reference; it matches and caps text the child
/// already printed.
/// </para>
/// </remarks>
internal static class EngineDiagnosticExcerpt
{
    /// <summary>
    /// The engine's own wording for a pre-topology environment configuration refusal, as
    /// <c>ScenarioRunner.RunSuiteAsync</c> prints it when <c>EnvironmentMapper</c> rejects a
    /// dependency's <c>env</c> entry — <b>the engine's spelling at <c>ENGINE_PIN</c> v1.0.0-rc.5</b>,
    /// see this type's remarks for the full measurement and for why a miss is harmless.
    /// </summary>
    internal const string EnvironmentConfigurationErrorSignature = "environment configuration error";

    /// <summary>
    /// The largest retained excerpt, in characters of ALREADY-SANITISED text (the
    /// <see cref="TruncationMarker"/> is appended past it when a clip happens, exactly as
    /// <c>VfxCode.SanitiseForEcho</c> appends its own). The measured rc.5 refusal sentence is ~640
    /// characters, so this fits it whole with room for a reworded successor; the bound exists for a
    /// signature-matching line that is arbitrarily long (the engine could splice a long resource name
    /// into one), not to clip the known case.
    /// </summary>
    internal const int MaxExcerptChars = 1_000;

    /// <summary>
    /// What a clip is marked with. <b>Deliberately printable ASCII rather than this codebase's usual
    /// <c>…</c> glyph</b> — which is what makes <see cref="SanitiseAndCap"/> idempotent, and therefore
    /// safe to apply at BOTH the retention boundary and the wire boundary. U+2026 would be rewritten
    /// into a six-character <c>…</c> escape by a second sanitising pass, so the marker itself
    /// would visibly rot every time the helper was reapplied.
    /// </summary>
    internal const string TruncationMarker = " [truncated]";

    /// <summary>
    /// Whether <paramref name="line"/> carries one of the enumerated engine signatures — the ONLY
    /// condition under which a stdout line is retained.
    /// </summary>
    internal static bool IsDiagnosticLine(string line) =>
        line.Contains(EnvironmentConfigurationErrorSignature, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders one engine diagnostic line safely: through
    /// <see cref="TextSanitiser.SanitiseForDisplay"/> (the security boundary — see that type's
    /// remarks) and then clipped to <see cref="MaxExcerptChars"/>, marking a clip with
    /// <see cref="TruncationMarker"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sanitise BEFORE clipping</b>, because the bound that has to hold is on what this server
    /// stores and eventually puts on the wire, and sanitising can expand a single non-ASCII character
    /// into six. A clip can therefore land inside a <c>\uXXXX</c> escape, which is cosmetic only:
    /// every character of such an escape is printable ASCII, so no control character can survive it.
    /// </para>
    /// <para>
    /// <b>IDEMPOTENT — <c>f(f(x)) == f(x)</c> — and that is load-bearing, not incidental.</b> This is
    /// called twice on the same text: once by <see cref="VouchfxCliSuiteRunner"/>'s relay, which is
    /// where the memory bound has to be applied, and again by
    /// <c>RunSuiteOrchestrator.BuildEngineRefusalHint</c>, which is the agent-facing boundary and must
    /// not depend on a comment in <see cref="ISuiteRunner"/> having been honoured by whatever runner
    /// was injected. Both properties hold by construction: sanitising output is entirely printable
    /// ASCII, so a second sanitising pass is the identity; and a clipped result is
    /// <see cref="MaxExcerptChars"/> characters plus the marker, so a second clip removes exactly the
    /// marker and re-appends it unchanged.
    /// </para>
    /// </remarks>
    internal static string SanitiseAndCap(string line)
    {
        var sanitised = TextSanitiser.SanitiseForDisplay(line);

        return sanitised.Length > MaxExcerptChars
            ? sanitised[..MaxExcerptChars] + TruncationMarker
            : sanitised;
    }
}
