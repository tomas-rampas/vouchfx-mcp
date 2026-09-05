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
        : base(BuildEchoMessage(ruleCode, offendingField))
    {
        RuleCode = VfxCode.SanitiseForEcho(ruleCode);
        OffendingField = VfxCode.SanitiseForEcho(offendingField);
        Violation = SemanticRuleContractViolation.SecretReferenceEcho;
    }

    /// <summary>
    /// Builds the exception for a violation that is not about a FIELD's content — a rule that
    /// yielded a null finding, or returned a null sequence instead of an empty one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these two joined the same type (fifth-round peer follow-up).</b> They used to throw
    /// bare <see cref="ArgumentNullException"/>s, which cost the operator the one fact that makes
    /// the defect fixable: WHICH rule. <c>Program.cs</c>'s <c>--validate-worker</c> catch prints
    /// this type's <see cref="Exception.Message"/> and every other exception's type name only, so
    /// routing them here is what turns "crashed: ArgumentNullException." into a message naming the
    /// rule.
    /// </para>
    /// <para>
    /// <b>A closed enum, not free text, and that is the whole design.</b> The type's guarantee is
    /// "content-free by construction" — no constructor takes prose. Adding a second reason
    /// therefore adds an enum VALUE mapped to a compile-time constant sentence, never a
    /// <c>string reason</c> parameter, which would hand every future throw site the ability to
    /// splice suite content into a message the worker boundary prints verbatim.
    /// </para>
    /// </remarks>
    public SemanticRuleContractViolationException(string ruleCode, SemanticRuleContractViolation violation)
        : base(BuildViolationMessage(ruleCode, violation))
    {
        RuleCode = VfxCode.SanitiseForEcho(ruleCode);
        OffendingField = string.Empty;
        Violation = violation;
    }

    /// <summary>The offending rule's code, as it appears in <see cref="Exception.Message"/>.</summary>
    public string RuleCode { get; }

    /// <summary>
    /// The offending field's name, as it appears in <see cref="Exception.Message"/>, or the empty
    /// string for a violation that is not about a field (see <see cref="Violation"/>).
    /// </summary>
    public string OffendingField { get; }

    /// <summary>Which of the sanctioned contract violations this is.</summary>
    public SemanticRuleContractViolation Violation { get; }

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
    /// <para>
    /// <b>Every character of the constant prose below is PURE ASCII</b>, and that is load-bearing
    /// rather than stylistic (fifth-round peer follow-up). This is the ONE exception message that
    /// crosses the child-stderr relay: <c>Program.cs</c>'s <c>--validate-worker</c> catch prints it,
    /// <c>ValidationWorkerClient.ReadExcerptQuietlyAsync</c> takes a 500-character excerpt, and that
    /// excerpt reaches the HOST inside the VFX-E-1901 message. The decode of that relay is the
    /// tracked defect in issue #70, so a typographic ellipsis or em dash here would arrive mojibaked
    /// in the one place an operator is trying to read a rule's name. Hence <c>'${...}'</c> with
    /// three full stops, and hyphens rather than dashes. (The single non-ASCII character that can
    /// still appear is <see cref="VfxCode.SanitiseForEcho"/>'s own truncation ellipsis, which is
    /// part of that helper's tested contract and only appears for an over-long rule code — never
    /// from this file's constants.)
    /// </para>
    /// </remarks>
    private static string BuildEchoMessage(string ruleCode, string offendingField) =>
        $"Semantic rule '{VfxCode.SanitiseForEcho(ruleCode)}' produced a finding whose "
        + $"{VfxCode.SanitiseForEcho(offendingField)} contains a "
        // The literal `${` lives in a NON-interpolated segment: in an interpolated string it would
        // open a hole rather than print.
        + "'${...}' reference (a secret reference, a connection reference, or any other form), so "
        + "the call was failed rather than published. The offending text is deliberately not "
        + "reproduced here. Fix the rule: name the identifier the finding is about via bounded, "
        + "sanitised identifiers, never by interpolating SemanticAnalysisContext.Facts content "
        + "wholesale.";

    /// <summary>
    /// Composes the message for a non-field violation, from the same sanitised code plus one of
    /// two compile-time constant sentences.
    /// </summary>
    /// <remarks>
    /// Pure ASCII for the reason <see cref="BuildEchoMessage"/>'s remarks give in full: this text
    /// crosses the same child-stderr relay.
    /// </remarks>
    private static string BuildViolationMessage(string ruleCode, SemanticRuleContractViolation violation)
    {
        var detail = violation switch
        {
            SemanticRuleContractViolation.NullFinding =>
                "yielded a null finding. A rule reports one Diagnostic per problem and nothing at "
                + "all otherwise; there is no 'no opinion' element.",
            SemanticRuleContractViolation.NullFindingSequence =>
                "returned a null sequence instead of an empty one. A rule with nothing to say "
                + "returns an empty enumerable, never null.",
            _ =>
                "violated the semantic-rule contract in a way this build does not have prose for. "
                + "That is itself a defect: add the reason to SemanticRuleContractViolation.",
        };

        return $"Semantic rule '{VfxCode.SanitiseForEcho(ruleCode)}' {detail} The call was failed "
            + "rather than published, because a rule that cannot keep its own contract cannot be "
            + "trusted to have kept the no-secret-echo one either.";
    }
}

/// <summary>
/// The sanctioned reasons a <see cref="SemanticRuleContractViolationException"/> can be thrown for,
/// each mapped to one compile-time constant sentence.
/// </summary>
/// <remarks>
/// <b>A closed enum is what keeps the exception content-free.</b> The alternative — a
/// <c>string reason</c> constructor parameter — would let any throw site put arbitrary text into a
/// message the worker boundary prints verbatim and the parent relays to the host, which is exactly
/// the property the whole type exists to deny. Adding a reason therefore means adding a value here
/// and a sentence beside it, never widening the constructor.
/// </remarks>
internal enum SemanticRuleContractViolation
{
    /// <summary>A finding's rule-composed text carried a <c>${…}</c> reference.</summary>
    SecretReferenceEcho,

    /// <summary>A rule yielded <see langword="null"/> where a <see cref="Diagnostic"/> was required.</summary>
    NullFinding,

    /// <summary>A rule returned <see langword="null"/> instead of an empty finding sequence.</summary>
    NullFindingSequence,
}
