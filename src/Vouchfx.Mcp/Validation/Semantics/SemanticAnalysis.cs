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
/// <b>A rule must not resolve or echo a <c>${secret:…}</c> reference</b>, quote raw suite content
/// beyond the bounded identifiers a finding needs, or reach outside
/// <see cref="SemanticAnalysisContext"/> for anything — no filesystem, no network, no process
/// environment. Enforced for the whole assembly by <c>SecretHygieneSourceGuardTests</c>.
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
    /// their findings, flattened in rule order.
    /// </summary>
    /// <remarks>
    /// Materialised eagerly rather than returned lazily: the result crosses a process boundary as
    /// JSON, and the <see cref="JsonDocument"/> backing
    /// <see cref="SemanticAnalysisContext.Document"/> is disposed as soon as the enclosing
    /// <c>using</c> in <see cref="SuiteValidator"/> ends — a deferred enumerable would be walked
    /// after that, reading a disposed document.
    /// </remarks>
    public static IReadOnlyList<Diagnostic> Analyse(SemanticAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Rules.Count == 0)
        {
            return [];
        }

        var findings = new List<Diagnostic>();
        foreach (var rule in Rules)
        {
            findings.AddRange(rule.Evaluate(context));
        }

        return findings;
    }
}
