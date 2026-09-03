namespace Vouchfx.Mcp;

/// <summary>
/// Builds the friendly, terminal-safe message printed to stderr when the ENGINE_PIN file cannot
/// be read or parsed at startup.
/// </summary>
/// <remarks>
/// Extracted out of <c>Program.cs</c> — which cannot itself be unit tested, being a top-level
/// statements entry point — so the message-building and sanitising behaviour has direct test
/// coverage (see <c>PinFailureReportingTests</c>).
/// <para>
/// <b>Message-forwarding policy</b> (security-relevant, not just formatting): <em>only</em>
/// <see cref="FormatException"/> messages are ever forwarded to the printed message, and even
/// then only after <see cref="TextSanitiser.SanitiseForDisplay"/>. That is deliberate and narrow —
/// every <see cref="FormatException"/> <see cref="EnginePin"/> throws is authored by
/// <see cref="EnginePin"/> itself (never the BCL), already describes the malformed pin usefully,
/// and already sanitises any raw pin value it splices in; forwarding it here is a second,
/// independent scrub, not a duplicate of that fix.
/// </para>
/// <para>
/// Every <em>other</em> exception type's <see cref="Exception.Message"/> is never forwarded at
/// all, because it cannot be trusted to be path-free: BCL exceptions such as
/// <see cref="UnauthorizedAccessException"/>, <see cref="DirectoryNotFoundException"/>, and
/// plain <see cref="IOException"/> routinely embed a full, printable-ASCII file system path in
/// their message, which a control-character-only sanitiser would pass straight through.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="FileNotFoundException"/> is special-cased: rather than trust its message either,
/// this builds the reported text itself from <see cref="FileNotFoundException.FileName"/> — a
/// structured property, not free text — via <see cref="Path.GetFileName(string?)"/>, then
/// sanitises that bare name too before use.
/// </description></item>
/// <item><description>
/// Every remaining exception type gets a path-free, type-based summary instead, e.g.
/// <c>"ENGINE_PIN could not be read (UnauthorizedAccessException)."</c> — enough to diagnose
/// what went wrong without ever risking a leaked path.
/// </description></item>
/// </list>
/// </remarks>
public static class PinFailureReporting
{
    /// <summary>
    /// Builds the one-line message to print to stderr for a failure to load or parse
    /// ENGINE_PIN. Never includes a file path or any unsanitised, BCL-authored exception
    /// message — see the message-forwarding policy above.
    /// </summary>
    public static string DescribeLoadFailure(Exception exception)
    {
        var detail = exception switch
        {
            FileNotFoundException fileNotFound => $"{ResolveFileName(fileNotFound.FileName)} not found beside the executable.",
            FormatException => TextSanitiser.SanitiseForDisplay(exception.Message),
            _ => $"ENGINE_PIN could not be read ({exception.GetType().Name}).",
        };

        return $"vouchfx-mcp: could not read ENGINE_PIN: {detail}";
    }

    /// <summary>
    /// Builds the one-line message to print to stderr when the US-S1-02 provenance stamp cannot be
    /// derived at startup — in practice, an embedded composed schema that no longer declares its
    /// own version marker (see <see cref="Vouchfx.Mcp.Validation.VendoredSchemaVersion"/>).
    /// </summary>
    /// <remarks>
    /// Held to the SAME message-forwarding policy as <see cref="DescribeLoadFailure"/> (see this
    /// type's remarks): the only exception whose own message is forwarded is
    /// <see cref="InvalidOperationException"/>, which is authored by this repo's own code and so is
    /// known to be path-free — and it is sanitised anyway. Anything else is reduced to its type
    /// name. The startup path unwraps <see cref="TypeInitializationException"/> first, since a
    /// static-initialiser fault arrives wrapped and the inner exception is the informative one.
    /// </remarks>
    public static string DescribeToolMetaFailure(Exception exception)
    {
        var unwrapped = exception is TypeInitializationException { InnerException: { } inner } ? inner : exception;

        var detail = unwrapped switch
        {
            InvalidOperationException => TextSanitiser.SanitiseForDisplay(unwrapped.Message),
            _ => $"the vendored schema could not be read ({unwrapped.GetType().Name}).",
        };

        return $"vouchfx-mcp: could not derive the result provenance stamp: {detail}";
    }

    /// <summary>
    /// Builds the one-line message to print to stderr when the US-S1-05 diagnostic catalogue
    /// (<see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageRepository"/>) cannot be loaded at
    /// startup — in practice, a <c>docs/errors/*.md</c> page missing from the embedded manifest, or
    /// one that fails <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageParser"/>'s fixed heading
    /// structure.
    /// </summary>
    /// <remarks>
    /// Held to the SAME message-forwarding policy as <see cref="DescribeToolMetaFailure"/> (see that
    /// method's remarks): the repository's own <see cref="InvalidOperationException"/> (a missing or
    /// mis-titled embedded page) and <see cref="FormatException"/> (a malformed page — see
    /// <see cref="Vouchfx.Mcp.ErrorCatalogue.DiagnosticPageParser"/>) are both authored entirely by
    /// this repo's own code and known to be path-free, so both are forwarded, sanitised; anything
    /// else collapses to a type name. The startup path unwraps <see cref="TypeInitializationException"/>
    /// first, for the same reason <see cref="DescribeToolMetaFailure"/> does: a static-initialiser
    /// fault arrives wrapped, and the inner exception is the one that names which page and why.
    /// </remarks>
    public static string DescribeDiagnosticCatalogueFailure(Exception exception)
    {
        var unwrapped = exception is TypeInitializationException { InnerException: { } inner } ? inner : exception;

        var detail = unwrapped switch
        {
            InvalidOperationException or FormatException => TextSanitiser.SanitiseForDisplay(unwrapped.Message),
            _ => $"a docs/errors/*.md catalogue page could not be read ({unwrapped.GetType().Name}).",
        };

        return $"vouchfx-mcp: could not load the diagnostic catalogue: {detail}";
    }

    private static string ResolveFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return "ENGINE_PIN";
        }

        var bareName = Path.GetFileName(fileName);
        return string.IsNullOrEmpty(bareName) ? "ENGINE_PIN" : TextSanitiser.SanitiseForDisplay(bareName);
    }
}
