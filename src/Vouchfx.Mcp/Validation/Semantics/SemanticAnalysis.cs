using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Validation.Semantics;

// Vouchfx.Mcp.Validation.Semantics — the semantic-rules SEAM (Sprint 2 / US-S2-02).
//
// US-S2-02 builds this extension point; US-S2-03 fills it with the rules themselves. Until then
// SemanticAnalyser.Rules is empty and SuiteAnalysis.SemanticDiagnostics is present and empty at
// every level — a channel with no traffic, never an absent field.
//
// The codes US-S2-03 lands are the NEW rules at 1202 through 1211 in the VFX-D-12xx semantic range
// (unused captures, placeholders that name nothing, targets that name no declared service or
// dependency, and their siblings) — plus one REUSED code. (Those ten are written here without their
// `VFX-D-` prefix on purpose: VfxCodeCatalogueTests scans src/ for prefixed literals and treats each
// one as a code this server has claimed, owing a catalogue entry and a docs page. US-S2-03 mints
// them; this comment only forecasts them.)
//
// VFX-D-1201 (UnknownStepType) is already live TODAY as the SCHEMA pass's
// cross-check: SuiteValidator.AppendUnknownStepTypeErrors emits it into the `errors` array, its
// catalogue entry is in VfxCodeCatalogue, and its docs/errors/VFX-D-1201.md page ships. Per the
// sprint spec, US-S2-03 MIGRATES that existing detector into this channel rather than minting a
// second code for the same finding — the code, its meaning, and its page are unchanged; only the
// channel that carries it moves. It is deliberately NOT a numbering collision to be renumbered
// around (the framing VFX-E-1250's catalogue entry uses for its own "deliberately not X"
// distinction is the model here).
//
// ---------------------------------------------------------------------------------------------
// The one constraint a rule author must not design around
// ---------------------------------------------------------------------------------------------
//
// A rule is handed the ALREADY-PARSED document and never the raw YAML. That is not a convenience:
// SuiteValidator.ValidateYaml's own comment records the measurement — an earlier revision that
// re-parsed per finding took 31.9 SECONDS on a 2 000-error suite, against the validation worker's
// 10-second wall clock. Eleven rules each re-parsing a large suite would reintroduce exactly that
// failure mode, and it would surface as VFX-E-1150 (a killed worker) rather than as a slow rule, so
// the cause would not be obvious from the symptom. Hence SemanticAnalysisContext carries the parsed
// artefacts and there is no overload that takes text.

/// <summary>
/// Everything a semantic rule is given: the suite document as already parsed, and the facts already
/// derived from it.
/// </summary>
/// <remarks>
/// <para>
/// Constructed exactly once per <c>validate_suite</c> call, inside the isolated validation worker,
/// from the single YAML→JSON conversion <see cref="SuiteValidator"/> performs — see this file's
/// header for why no rule may parse anything itself.
/// </para>
/// <para>
/// <b><see cref="Document"/>'s lifetime is the enclosing <see cref="JsonDocument"/>'s.</b> A rule
/// may read it freely during <see cref="ISemanticRule.Evaluate"/> but must not retain the
/// <see cref="JsonElement"/> (or anything holding one) past its return: the document it views is
/// disposed as soon as the analysis completes. Copy out what a finding needs — a
/// <see cref="Diagnostic"/> carries strings and numbers, never live element handles.
/// </para>
/// </remarks>
public sealed class SemanticAnalysisContext
{
    /// <summary>Builds the context for one suite document.</summary>
    /// <param name="document">The suite's JSON projection — the root element of the single parse.</param>
    /// <param name="yamlRoot">
    /// The YAML representation-model root the same text produced, for resolving a finding back to a
    /// source line, or <see langword="null"/> when the text did not yield a mapping root.
    /// </param>
    /// <param name="summary">The document's own digest, already derived from <paramref name="document"/>.</param>
    /// <param name="facts">
    /// The complete name sets the same walk derived — the set-membership authority (see
    /// <see cref="Facts"/>).
    /// </param>
    public SemanticAnalysisContext(
        JsonElement document,
        YamlMappingNode? yamlRoot,
        SuiteSummary summary,
        SuiteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(facts);

        Document = document;
        YamlRoot = yamlRoot;
        Summary = summary;
        Facts = facts;
    }

    /// <summary>The suite document's JSON projection — the root of the single parse.</summary>
    public JsonElement Document { get; }

    /// <summary>
    /// The YAML representation-model root, for line resolution via <see cref="YamlLineResolver"/>,
    /// or <see langword="null"/> when the text did not parse to a mapping.
    /// </summary>
    public YamlMappingNode? YamlRoot { get; }

    /// <summary>
    /// The CALLER-FACING digest this call will return — the same object the host receives in
    /// <c>summary</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never authoritative for set membership.</b> It is a digest, and lossy in two ways that
    /// both manufacture the same false negative: every list stops at
    /// <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> (1 000) entries, and every name
    /// containing <c>${</c> is dropped for secret hygiene. A rule that computed, say,
    /// <c>Summary.Placeholders \ Summary.Captures</c> would report "this placeholder names nothing"
    /// for a capture literally named <c>${secret:…}</c>, and for every capture past the thousandth
    /// on a large suite — a wrong <c>VFX-D</c> finding on a valid document, which is the one failure
    /// mode a semantic rule must never have.
    /// </para>
    /// <para>
    /// Read this when the QUESTION is what the caller will be shown (say, a message that quotes the
    /// same names the host sees). For anything of the form "X is not declared", use
    /// <see cref="Facts"/> — or <see cref="Document"/> when the fact set does not carry the shape
    /// you need.
    /// </para>
    /// </remarks>
    public SuiteSummary Summary { get; }

    /// <summary>
    /// The complete, unfiltered, uncapped name sets the document declares — step types, service and
    /// dependency names, capture names, placeholder usages, and root <c>variables</c> names.
    /// <b>The set-membership authority, and the reason a rule that needs any of them does not
    /// re-walk the document for it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived by <see cref="SuiteSummaryBuilder"/> in the SAME single walk that produced
    /// <see cref="Summary"/> — no extra traversal is paid for it (see this file's header for why a
    /// second walk per rule is the measured hazard this seam is shaped around).
    /// </para>
    /// <para>
    /// <b>Internal to the worker process; never serialised.</b> It deliberately retains names the
    /// summary excludes, including <c>${secret:…}</c>-shaped identifiers. A rule may TEST against
    /// them freely — that is what makes "is this capture declared?" answerable — but must not quote
    /// one into a <see cref="Diagnostic"/>'s message or path, which would publish through the
    /// finding exactly what the summary's filter keeps out of the digest.
    /// </para>
    /// </remarks>
    public SuiteFacts Facts { get; }
}

/// <summary>
/// One semantic rule: a check that the engine's JSON Schema cannot express, reported as a
/// <see cref="Diagnostic"/> on a successful call.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rule may only ever produce diagnostics, never errors.</b> Its output is
/// <see cref="Diagnostic"/>, whose constructor rejects anything that is not a <c>VFX-D-</c> code —
/// so "this rule decided the call could not be performed" is not expressible, by construction. A
/// semantic finding is always data on a successful call (spec §4.4).
/// </para>
/// <para>
/// <b>A rule must not throw.</b> <see cref="SemanticAnalyser"/> does not wrap rules in a catch:
/// <c>validate_suite</c>'s "never throws" promise is kept by the worker process boundary, so a
/// throwing rule degrades the whole call to <c>VFX-E-1901</c> (validation-worker-failed) instead of
/// reporting the ten rules that did work. Treat a shape you did not expect as "nothing to say".
/// </para>
/// <para>
/// <b>A rule must not resolve or echo a <c>${…}</c> reference</b> (<c>${secret:…}</c>,
/// <c>${conn:…}</c>, or any other form). That one obligation is ENFORCED, at
/// <see cref="SemanticAnalyser.Analyse"/>: every finding this seam produces passes through that
/// single choke point, and one whose <see cref="Diagnostic.Message"/>,
/// <see cref="Diagnostic.Path"/>, <see cref="DiagnosticFix.Description"/> or
/// <see cref="DiagnosticFix.Replacement"/> contains <c>${</c> fails the call rather than being
/// published. See that method's remarks for the four checked surfaces, the failure semantics, and
/// why they are deliberate.
/// </para>
/// <para>
/// <b>How to write a finding about a secret-NAMED capture, then.</b>
/// <see cref="SemanticAnalysisContext.Facts"/> deliberately retains identifiers literally spelled
/// <c>${secret:vault/prod-db-password}</c>, so a rule that interpolates fact-set content wholesale
/// into its message — the natural first draft of "capture '{name}' is never used" — trips the guard
/// and fails the whole call. Reference such a finding through
/// <c>VfxCode.SanitiseForEcho</c>-bounded identifiers, or omit the name and locate the finding by
/// <see cref="Diagnostic.Path"/> instead; never splice a raw name or a raw slice of the document
/// into prose.
/// </para>
/// <para>
/// <b>The rest of this paragraph's obligations have NO automated guard — they are author
/// obligations.</b> A rule must not quote raw suite content beyond the bounded identifiers a
/// finding needs, and must not reach outside <see cref="SemanticAnalysisContext"/> for anything —
/// no filesystem, no network, no process environment. Nothing checks those; a reviewer does.
/// (<c>SecretHygieneSourceGuardTests</c> is a DIFFERENT guard and does not cover this file: it
/// asserts that the three process-spawn sites in <c>src/</c> never build, mutate, or filter a
/// child's environment dictionary. Do not conflate the two.)
/// </para>
/// </remarks>
public interface ISemanticRule
{
    /// <summary>
    /// The single <c>VFX-D-####</c> code this rule emits, from <see cref="VfxCodeCatalogue"/>.
    /// </summary>
    /// <remarks>
    /// One code per rule, declared up front rather than only appearing inside
    /// <see cref="Evaluate"/>: it is what lets the rule set be listed, documented, and cross-checked
    /// against the <c>docs/errors/</c> catalogue without executing anything.
    /// </remarks>
    string Code { get; }

    /// <summary>Evaluates this rule against <paramref name="context"/>, yielding one finding per problem.</summary>
    IEnumerable<Diagnostic> Evaluate(SemanticAnalysisContext context);
}

/// <summary>
/// Runs the semantic rule set over one parsed suite document — <c>validate_suite</c>'s semantic
/// pass, and the only caller of <see cref="ISemanticRule"/>.
/// </summary>
public static class SemanticAnalyser
{
    /// <summary>
    /// Every semantic rule, in the order their findings are reported.
    /// </summary>
    /// <remarks>
    /// <b>Empty until US-S2-03.</b> That story's whole job is to populate this list: the new rules
    /// at 1202 through 1211 in the VFX-D-12xx range, plus the migration of the existing
    /// unknown-step-type detector — which already emits VFX-D-1201 from the schema pass today —
    /// into this channel, per the sprint spec (see this file's header, including why the ten new
    /// numbers are written without their prefix). Nothing else in this seam has to change for it to
    /// do so — a rule is a class implementing <see cref="ISemanticRule"/> plus an entry here plus its
    /// <c>docs/errors/&lt;CODE&gt;.md</c> page (the bidirectional completeness gate will demand the
    /// page the moment the code is emitted). Append-only, like <c>Tools/ToolRegistry</c>: the order
    /// is the reported order, so inserting in the middle reshuffles output nobody asked to have
    /// reshuffled.
    /// </remarks>
    public static IReadOnlyList<ISemanticRule> Rules { get; } = [];

    /// <summary>
    /// Evaluates every rule in <see cref="Rules"/> against <paramref name="context"/> and returns
    /// their findings, flattened in rule order — <b>and the single place a finding is checked for
    /// secret-reference echo before anyone can see it</b> (see
    /// <see cref="RejectIfItEchoesASecretReference"/>).
    /// </summary>
    /// <remarks>
    /// Materialised eagerly rather than returned lazily: the result crosses a process boundary as
    /// JSON, and the <see cref="JsonDocument"/> backing
    /// <see cref="SemanticAnalysisContext.Document"/> is disposed as soon as the enclosing
    /// <c>using</c> in <see cref="SuiteValidator"/> ends — a deferred enumerable would be walked
    /// after that, reading a disposed document. Eager materialisation is also what makes the hygiene
    /// check below a real gate rather than an advisory one: every finding is inspected here, before
    /// the list exists, so no caller can receive one that was never looked at.
    /// </remarks>
    public static IReadOnlyList<Diagnostic> Analyse(SemanticAnalysisContext context) =>
        Analyse(context, Rules);

    /// <summary>
    /// The implementation <see cref="Analyse(SemanticAnalysisContext)"/> delegates to, with the rule
    /// set supplied — <b>an internal seam for the guard tests only</b>, so
    /// <c>SemanticSeamTests</c> can drive a deliberately-misbehaving rule through the real choke
    /// point instead of asserting against a re-implementation of it. Production has exactly one
    /// caller and it passes <see cref="Rules"/>.
    /// </summary>
    internal static IReadOnlyList<Diagnostic> Analyse(
        SemanticAnalysisContext context,
        IReadOnlyList<ISemanticRule> rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return [];
        }

        var findings = new List<Diagnostic>();
        foreach (var rule in rules)
        {
            foreach (var finding in rule.Evaluate(context))
            {
                RejectIfItEchoesASecretReference(rule, finding);
                findings.Add(finding);
            }
        }

        return findings;
    }

    /// <summary>
    /// The token that opens the engine's reference syntax — <c>${secret:…}</c>, <c>${conn:…}</c>, or
    /// any other form: the predicate is the OPENER, so the scope is every reference shape, exactly
    /// as in <c>SuiteSummaryBuilder.NameCollector.Add</c>. Substring-tested rather than
    /// prefix-tested for the same reason it is there: a reference can be embedded
    /// (<c>prefix-${secret:…}</c>) as easily as it can lead.
    /// </summary>
    private const string SecretReferenceOpener = "${";

    /// <summary>
    /// Fails the call when any of <paramref name="finding"/>'s rule-composed text fields — its
    /// <see cref="Diagnostic.Message"/>, its <see cref="Diagnostic.Path"/>, or either half of its
    /// <see cref="Diagnostic.Fix"/> — carries a <c>${…}</c> reference (<c>${secret:…}</c>,
    /// <c>${conn:…}</c>, or any other form).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The four checked surfaces, and why exactly those.</b>
    /// <see cref="Diagnostic.Message"/>, <see cref="Diagnostic.Path"/>,
    /// <see cref="DiagnosticFix.Description"/> and <see cref="DiagnosticFix.Replacement"/> are the
    /// finding's RULE-COMPOSED, wire-serialised text: every one of them is prose (or a path, or a
    /// literal replacement) the rule itself builds out of document-derived material, and every one
    /// of them reaches the caller. A fix is not a lesser surface than a message — a
    /// <c>Replacement</c> is the one field a host may apply verbatim — so leaving either half of it
    /// unchecked would leave the natural "here is the corrected line" draft as an unguarded door out
    /// of the fact set. The remaining fields are not prose at all:
    /// <see cref="Diagnostic.Code"/> and <see cref="Diagnostic.Severity"/> are validated
    /// constants, <see cref="Diagnostic.DocsUrl"/> is derived from the code, and
    /// <see cref="DiagnosticLocation"/>'s numbers cannot carry text. Its
    /// <see cref="DiagnosticLocation.File"/> is deliberately excluded — see the last paragraph.
    /// </para>
    /// <para>
    /// <b>Why this exists at all.</b> <see cref="SemanticAnalysisContext.Facts"/> deliberately
    /// retains names the published <see cref="SuiteSummary"/> filters out, including identifiers
    /// literally spelled <c>${secret:vault/prod-db-password}</c> — that retention is what makes "is
    /// this capture declared?" answerable. The cost of that decision is that the most natural way to
    /// write a rule ("capture '{name}' is never used", interpolating a fact-set entry) would publish
    /// the caller's secret STORE LAYOUT through the semantic channel, on a <c>valid: true</c> result
    /// — the exact disclosure <c>NameCollector.Add</c>'s filter keeps out of the digest, arriving
    /// through a different door. Prose in the rule-author contract is not enforcement; this is.
    /// </para>
    /// <para>
    /// <b>Why it THROWS rather than dropping or redacting the finding.</b> A rule that quotes a
    /// secret reference IS a server bug, and inside the isolated validation worker this exception
    /// crosses the crash boundary as <c>VFX-E-1901</c> (validation-worker-failed) — a call that
    /// honestly reports "this server malfunctioned" instead of one that silently published a secret
    /// path, or one that returned a mysteriously incomplete finding list. Failing the call beats
    /// leaking, and beats hiding the defect from whoever has to fix it. Redacting was rejected for
    /// the reason CLAUDE.md gives: this server is not the redaction authority, the engine is.
    /// </para>
    /// <para>
    /// <b>The exception names the rule, never the content.</b> It is a dedicated type —
    /// <see cref="SemanticRuleContractViolationException"/> — whose message carries
    /// <see cref="ISemanticRule.Code"/> and which FIELD offended, and deliberately not one character
    /// of the offending text: an exception message reaches a log, and reproducing the reference
    /// there would be the same disclosure this guard exists to prevent, merely relocated. That shape
    /// is enforced in the exception's own constructor (it has none taking free text) rather than
    /// trusted to this throw site, which is what lets the worker boundary print the message verbatim
    /// — see that type's remarks, and <c>Program.cs</c>'s <c>--validate-worker</c> catch. The code is
    /// routed through <c>VfxCode.SanitiseForEcho</c> by house rule (it is a compile-time constant on
    /// an in-repo rule class, not caller data, so the cap and control-character escaping are
    /// belt-and-braces).
    /// </para>
    /// <para>
    /// <b><see cref="DiagnosticLocation.File"/> is deliberately NOT checked.</b> The four fields
    /// above are prose the RULE composes; a location's file is the caller's own suite path echoed
    /// back. A workspace whose directory name happens to contain <c>${</c> would otherwise crash
    /// every finding on every suite under it — a false positive on legitimate input, which is the
    /// one failure mode a validation guard must not have. Nothing in this seam derives that path
    /// from document content — indeed <see cref="SemanticAnalysisContext"/> carries no suite path at
    /// all today, so a rule cannot populate that field yet. This exclusion is written for the day
    /// US-S2-03 needs file-located findings: the source identity (the suite path, or the marker
    /// standing in for inline text) gets threaded into the context then, and the exclusion stays
    /// correct because that value will still be CALLER-SUPPLIED rather than document-derived.
    /// </para>
    /// </remarks>
    private static void RejectIfItEchoesASecretReference(ISemanticRule rule, Diagnostic finding)
    {
        // A rule that yields a null element is breaking its own contract too; say so, rather than
        // letting the field reads below produce a bare NullReferenceException at the same boundary.
        ArgumentNullException.ThrowIfNull(finding);

        string offendingField;
        if (CarriesSecretReference(finding.Message))
        {
            offendingField = nameof(Diagnostic.Message);
        }
        else if (CarriesSecretReference(finding.Path))
        {
            offendingField = nameof(Diagnostic.Path);
        }
        else if (CarriesSecretReference(finding.Fix?.Description))
        {
            offendingField = $"{nameof(Diagnostic.Fix)}.{nameof(DiagnosticFix.Description)}";
        }
        else if (CarriesSecretReference(finding.Fix?.Replacement))
        {
            offendingField = $"{nameof(Diagnostic.Fix)}.{nameof(DiagnosticFix.Replacement)}";
        }
        else
        {
            return;
        }

        throw new SemanticRuleContractViolationException(rule.Code, offendingField);
    }

    private static bool CarriesSecretReference(string? text) =>
        text is not null && text.Contains(SecretReferenceOpener, StringComparison.Ordinal);
}
