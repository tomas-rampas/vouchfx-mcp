using Vouchfx.Mcp.Contracts;

namespace Vouchfx.Mcp.Validation.Semantics;

/// <summary>
/// Thrown by <see cref="SemanticAnalyser"/>'s hygiene choke point when a semantic rule produces a
/// finding that echoes a <c>${…}</c> reference (<c>${secret:…}</c>, <c>${conn:…}</c>, or any other
/// form) — the one deliberate exception to <see cref="SuiteValidator"/>'s never-throws contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its message is content-free BY CONSTRUCTION, which is the whole reason the type exists.</b>
/// There is no constructor taking free text: the only constructor takes a rule code and a field
/// NAME and composes the message itself, routing both through
/// <see cref="VfxCode.SanitiseForEcho"/>. A throw site therefore cannot splice suite content into
/// it even by accident, and nothing downstream has to trust that it did not.
/// </para>
/// <para>
/// <b>That guarantee is what lets the worker boundary print the message rather than just the type
/// name.</b> <c>Program.cs</c>'s <c>--validate-worker</c> catch prints <c>ex.GetType().Name</c> for
/// every other exception precisely because a general exception's message may quote suite content (a
/// <c>YamlException</c> quotes the offending line); for this type it prints <c>Message</c>, so a
/// production operator sees WHICH rule broke the contract and in which field instead of a bare
/// <c>InvalidOperationException</c>. Inside the isolated validation worker the crash still surfaces
/// to the caller as <c>VFX-E-1901</c> (validation-worker-failed) — an honest "this server
/// malfunctioned", never a published secret path.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> rather than <see cref="Exception"/>: a
/// rule violating its own contract is exactly an invalid operation, and the derivation keeps every
/// existing <c>catch (InvalidOperationException)</c> boundary behaving as it did.
/// </para>
/// </remarks>
internal sealed class SemanticRuleContractViolationException : InvalidOperationException
{
    /// <summary>
    /// Builds the exception for <paramref name="ruleCode"/>'s finding, naming
    /// <paramref name="offendingField"/> as the field that carried the reference.
    /// </summary>
    /// <param name="ruleCode">
    /// The offending rule's <see cref="ISemanticRule.Code"/> — a <c>VFX-D-####</c> constant on an
    /// in-repo rule class, capped and control-character-escaped anyway (see the remarks).
    /// </param>
    /// <param name="offendingField">
    /// The NAME of the <see cref="Diagnostic"/> field that carried the reference (e.g.
    /// <c>"Message"</c>, <c>"Fix.Replacement"</c>) — a name, never the value it held.
    /// </param>
    public SemanticRuleContractViolationException(string ruleCode, string offendingField)
        : base(BuildMessage(ruleCode, offendingField))
    {
        RuleCode = VfxCode.SanitiseForEcho(ruleCode);
        OffendingField = VfxCode.SanitiseForEcho(offendingField);
    }

    /// <summary>The offending rule's code, as it appears in <see cref="Exception.Message"/>.</summary>
    public string RuleCode { get; }

    /// <summary>The offending field's name, as it appears in <see cref="Exception.Message"/>.</summary>
    public string OffendingField { get; }

    /// <summary>
    /// Composes the whole message from a sanitised code and a sanitised field name plus compile-time
    /// constant prose — the enforcement point for "content-free by construction".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arguments go through <see cref="VfxCode.SanitiseForEcho"/> (64-character cap plus
    /// control-character escaping) rather than being trusted as in-repo constants: the cap is what
    /// makes the shape a PROPERTY of this type instead of a convention its callers happen to follow,
    /// and it is what makes the message safe to print verbatim at the worker boundary.
    /// </para>
    /// <para>
    /// The prose names the reference FORMS in words ("a secret reference, a connection reference, or
    /// any other form") rather than spelling <c>${secret:…}</c> and <c>${conn:…}</c> out. The
    /// distinction is not cosmetic: <c>SemanticSeamTests</c> asserts that this message contains no
    /// <c>secret:</c> substring at all, which is a sharp, cheap canary for "the offending text
    /// leaked into the message" — and a constant example spelled in full would blunt it permanently.
    /// The full token forms belong in the doc comments, where nothing prints them.
    /// </para>
    /// </remarks>
    private static string BuildMessage(string ruleCode, string offendingField) =>
        $"Semantic rule '{VfxCode.SanitiseForEcho(ruleCode)}' produced a finding whose "
        + $"{VfxCode.SanitiseForEcho(offendingField)} contains a "
        // The literal `${` lives in a NON-interpolated segment: in an interpolated string it would
        // open a hole rather than print.
        + "'${…}' reference — a secret reference, a connection reference, or any other form — so "
        + "the call was failed rather than published. The offending text is deliberately not "
        + "reproduced here. Fix the rule: name the identifier the finding is about via bounded, "
        + "sanitised identifiers, never by interpolating SemanticAnalysisContext.Facts content "
        + "wholesale.";
}
