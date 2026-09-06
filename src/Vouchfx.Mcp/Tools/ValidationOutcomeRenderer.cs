using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tools;

/// <summary>
/// Applies spec §4.4's diagnostic/error split to a <see cref="ValidateSuiteResult"/> — once, in one
/// place, for the two tools that surface one (<c>validate_suite</c> and <c>run_suite</c>'s EDGE-003
/// pre-flight).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the split lives here and not in the validation pipeline:</b> the pipeline's job is to
/// report every problem it finds, in one uniform record; deciding which of those problems means
/// "here is your answer, and it is bad" versus "I could not produce an answer" is a WIRE-shape
/// decision, and the wire is this layer. Keeping the pipeline uniform is also what let US-S1-04 be
/// a rename inside <c>SuiteValidator</c>'s ~1450 lines of measured noise-suppression logic rather
/// than a restructuring of it.
/// </para>
/// <para>
/// <b>Why both tools share this type rather than each doing the check:</b> two copies of a
/// classification rule are two copies that can disagree, and the specific disagreement available
/// here is the expensive one — <c>run_suite</c> deciding a missing file is a successful
/// "suite-invalid" while <c>validate_suite</c> calls the same condition a tool error, so a host
/// gets two different answers about one file. One method, called twice.
/// </para>
/// </remarks>
internal static class ValidationOutcomeRenderer
{
    /// <summary>
    /// Renders <paramref name="validation"/> as a tool error when it reports a
    /// <see cref="VfxCodeKind.Error"/> code — a condition under which the suite's validity was never
    /// determined, so there is no validation answer to return as data.
    /// </summary>
    /// <param name="validation">The pipeline's result.</param>
    /// <param name="failure">
    /// The rendered error result, or <see langword="null"/> when every reported problem is a
    /// diagnostic (including the ordinary case of no problems at all).
    /// </param>
    /// <param name="subject">
    /// An ALREADY-CAPPED-AND-SANITISED rendering of the file this result is about, prefixed to the
    /// message as <c>'&lt;subject&gt;': </c>. <see langword="null"/> (<c>validate_suite</c>'s case)
    /// leaves the message exactly as the guard that produced it wrote it.
    /// <para>
    /// Exists for <c>run_suite</c>, whose pre-flight is all-or-nothing across every suite a call
    /// names: a forty-suite glob whose third file is unreadable produced a <c>VFX-E-1003</c> whose
    /// message named no file at all, because the message is written by a guard that was only ever
    /// asked about one (a gatekeeper review's MAJOR finding). <c>validate_suite</c> needs no prefix —
    /// there the caller named the single file the answer is about.
    /// </para>
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="failure"/> was populated.</returns>
    public static bool TryRenderCallFailure(
        ValidateSuiteResult validation, out CallToolResult? failure, string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(validation);

        // FirstOrDefault, not "collect them all": spec §4.4 specifies a single VfxError object as an
        // error result's whole body, and the pipeline cannot in fact produce two of these anyway —
        // every VFX-E-producing path (missing file, unreadable file, rejected path, worker timeout,
        // worker failure) returns its error as the sole entry and stops, because each of them means
        // validation could not proceed at all. Taking the first is therefore exact, not lossy.
        var callFailure = validation.Errors.FirstOrDefault(error => !IsDiagnostic(error.Code));

        if (callFailure is null)
        {
            failure = null;
            return false;
        }

        // The message is passed through verbatim: it is already sanitised for display by whichever
        // guard produced it (paths through TextSanitiser, exception text likewise), and US-S1-04
        // migrates codes, never wording. The optional subject prefix is likewise pre-sanitised by its
        // caller (PathSafetyGuard.CapAndSanitisePathForDisplay), so nothing here re-escapes text that
        // has already been made safe — double-escaping is how a displayed path stops looking like one.
        failure = StructuredToolResult.Error(BuildError(callFailure, subject));
        return true;
    }

    /// <summary>
    /// Whether <paramref name="code"/> is a catalogued diagnostic — i.e. something to return as
    /// data rather than as a tool error.
    /// </summary>
    /// <remarks>
    /// <b>An UNCATALOGUED code is deliberately not a diagnostic.</b> These codes cross a PROCESS
    /// BOUNDARY before reaching here: <c>ValidationWorkerClient</c> deserialises them from the
    /// isolated worker's stdout, so their values are untrusted input rather than constants named
    /// inside this assembly. Two consequences follow, and both are why this uses
    /// <c>TryGet</c> rather than <c>Get</c> (which throws on a miss):
    /// <list type="number">
    /// <item><description><c>validate_suite</c> promises, in its own tool description and in the
    /// published docs, that it never throws. Routing an unrecognised code through a throwing lookup
    /// would have made that promise depend on the child process's honesty.</description></item>
    /// <item><description>Failing CLOSED — treating the unknown as an error rather than silently
    /// letting it through as data — is the safe direction: a code this server cannot interpret must
    /// not be reported as a validation finding a host might act on.</description></item>
    /// </list>
    /// </remarks>
    private static bool IsDiagnostic(string code) =>
        VfxCodeCatalogue.TryGet(code, out var entry) && entry.Kind == VfxCodeKind.Diagnostic;

    /// <summary>
    /// Builds the <see cref="VfxError"/> for a call-failure entry, substituting
    /// <see cref="VfxCodeCatalogue.UnrecognisedOutcome"/> when the worker reported a code this
    /// server does not know — see <see cref="IsDiagnostic"/> for why that can happen at all.
    /// </summary>
    private static VfxError BuildError(SuiteValidationError callFailure, string? subject)
    {
        var message = subject is null ? callFailure.Message : $"'{subject}': {callFailure.Message}";

        if (VfxCodeCatalogue.TryGet(callFailure.Code, out _))
        {
            return VfxCodeCatalogue.CreateError(callFailure.Code, message);
        }

        // The uncatalogued code itself is NOT echoed into the message: it is unvalidated text from
        // another process, and the internal range is exactly where "this server received something
        // it cannot explain" belongs. The worker's own message still travels, already sanitised by
        // the guard that produced it.
        return VfxCodeCatalogue.CreateError(VfxCodeCatalogue.UnrecognisedOutcome, message);
    }
}
