using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Validation.Semantics;

// Vouchfx.Mcp.Validation.Semantics — the semantic-rules SEAM (Sprint 2 / US-S2-02), now populated
// (US-S2-03).
//
// US-S2-02 built this extension point; US-S2-03 filled it. SemanticAnalyser.Rules holds ten rules —
// the VFX-D-12xx semantic range minus 1210, which is implemented but U1-gated (see that rule, and
// the Rules property's own remarks). SuiteAnalysis.SemanticDiagnostics is present at every level and
// empty at ValidationLevel.Schema — a channel with no traffic there, never an absent field.
//
// ---------------------------------------------------------------------------------------------
// VFX-D-1201's channel: ADJUDICATED — both channels carry it, from ONE detector
// ---------------------------------------------------------------------------------------------
//
// US-S2-02 wrote this paragraph as a forecast that US-S2-03 would MIGRATE the existing
// unknown-step-type detector out of the schema `errors` array and into this one. US-S2-03 examined
// that and did NOT do it. The decision, and the three facts that decided it, are recorded on
// UnknownStepTypeRule itself so a reader meets them beside the code; in one line: the schema pass
// STRUCTURALLY needs its own unknown-type findings (SuppressUnevaluatedPropertiesCascade consumes
// them), run_suite's EDGE-003 pre-flight and US-S2-06's 33/13/0 agreement oracle both read only the
// schema channel, and the sprint spec's own instruction is to reuse the DETECTOR rather than mint a
// second code — which is what UnknownStepTypeDetector now is.
//
// So: one detector, two renderings. The schema channel keeps its byte-for-byte engine-matching
// message; this channel adds the Levenshtein closest-match suggestion spec §5.5 asks for. The two
// ARRAYS still never merge, which is what the channel-separation criterion actually requires.
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
    /// <param name="sourceName">
    /// The suite's own IDENTITY — a caller-supplied path, or the inline marker — for
    /// <see cref="DiagnosticLocation.File"/>. See <see cref="SourceName"/>.
    /// </param>
    public SemanticAnalysisContext(
        JsonElement document,
        YamlMappingNode? yamlRoot,
        SuiteSummary summary,
        SuiteFacts facts,
        string sourceName = SuiteSource.InlineSourceName)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        Document = document;
        YamlRoot = yamlRoot;
        Summary = summary;
        Facts = facts;
        SourceName = sourceName;
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

    /// <summary>
    /// What a finding's <see cref="DiagnosticLocation.File"/> says this suite IS — the caller's own
    /// path for a file source, or <see cref="SuiteSource.InlineSourceName"/> for inline YAML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Caller-supplied identity, never document content</b>, and the distinction is the whole
    /// reason <see cref="SemanticAnalyser"/>'s hygiene guard excludes
    /// <see cref="DiagnosticLocation.File"/> from its <c>${…}</c> check: a workspace directory whose
    /// name happens to contain those characters must not crash every finding on every suite under
    /// it. Nothing in this seam derives this value from what the suite says, and nothing may start.
    /// </para>
    /// <para>
    /// <b>Sanitised for display by the site that builds the context</b>
    /// (<see cref="SuiteValidator.AnalyseYaml"/>), not here, for the same reason every other
    /// caller-supplied path in this pipeline is sanitised where it enters: one call, at the
    /// boundary, rather than one per consumer.
    /// </para>
    /// <para>
    /// Defaulted rather than required, so the many existing constructions that predate
    /// location-bearing findings — and every test that only cares about a rule's verdict — keep
    /// compiling and get the honest inline marker rather than a fabricated path.
    /// </para>
    /// </remarks>
    public string SourceName { get; }
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
/// What one semantic pass produced: the findings it publishes, and whether the cap dropped any.
/// </summary>
/// <param name="Findings">
/// The findings, in rule-registration order, at most
/// <see cref="SemanticAnalyser.MaxPublishedFindings"/> of them.
/// </param>
/// <param name="Truncated">
/// <see langword="true"/> when at least one finding the rules produced was dropped because the cap
/// had already been reached — i.e. <paramref name="Findings"/> is known to be incomplete.
/// </param>
/// <remarks>
/// A pair rather than a bare list, so the cap cannot be applied without the flag travelling with it.
/// <see cref="SuiteAnalysis.SemanticDiagnosticsTruncated"/> is where it lands on the wire.
/// </remarks>
public sealed record SemanticAnalysisOutcome(IReadOnlyList<Diagnostic> Findings, bool Truncated)
{
    /// <summary>A pass that produced nothing and dropped nothing.</summary>
    public static SemanticAnalysisOutcome Empty { get; } = new([], false);
}

/// <summary>
/// Runs the semantic rule set over one parsed suite document — <c>validate_suite</c>'s semantic
/// pass, and the only caller of <see cref="ISemanticRule"/>.
/// </summary>
public static class SemanticAnalyser
{
    /// <summary>
    /// Every semantic rule, in the order their findings are reported (Sprint 2 / US-S2-03).
    /// </summary>
    /// <remarks>
    /// <para>
    /// In spec §5.5's own table order, which is also ascending code order. <b>Append-only</b>, like
    /// <c>Tools/ToolRegistry</c>: the order is the reported order, so inserting in the middle
    /// reshuffles output nobody asked to have reshuffled.
    /// </para>
    /// <para>
    /// <b>Ten entries for eleven codes, and the missing one is deliberate.</b>
    /// <see cref="TopologyCrossCheckRule"/> (VFX-D-1210) is implemented, catalogued and
    /// unit-tested, but is NOT registered here and cannot be: it needs an extracted topology, whose
    /// only source is upstream ask U1 (<c>vouchfx topology --json</c>), which is outstanding — see
    /// <c>specs/sprints/sprint-00-overview.md</c> §3 and that rule's own remarks. There is
    /// deliberately no flag, option, or environment variable that adds it; registering it is the
    /// one-line change the day U1 lands.
    /// </para>
    /// <para>
    /// A rule is a class implementing <see cref="ISemanticRule"/>, plus an entry here, plus its
    /// <c>docs/errors/&lt;CODE&gt;.md</c> page (the bidirectional completeness gate demands the page
    /// the moment the code appears in <c>src/</c> at all). Every rule below is stateless and
    /// constructed once for the process, which is safe precisely because
    /// <see cref="ISemanticRule.Evaluate"/> takes everything it needs as an argument and keeps
    /// nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ISemanticRule> Rules { get; } =
    [
        new UnknownStepTypeRule(),
        new DanglingTargetRule(),
        new PlaceholderDefinitionOrderRule(),
        new UnusedCaptureRule(),
        new UndeclaredDependencyRule(),
        new RetryTimeoutRule(),
        new SecretLiteralRule(),
        new DuplicateStepIdRule(),
        new AsyncVerifyModeRule(),
        new MetadataCompletenessRule(),
    ];

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
    public static SemanticAnalysisOutcome Analyse(SemanticAnalysisContext context) =>
        Analyse(context, Rules);

    /// <summary>
    /// The implementation <see cref="Analyse(SemanticAnalysisContext)"/> delegates to, with the rule
    /// set supplied — <b>an internal seam for the guard tests only</b>, so
    /// <c>SemanticSeamTests</c> can drive a deliberately-misbehaving rule through the real choke
    /// point instead of asserting against a re-implementation of it. Production has exactly one
    /// caller and it passes <see cref="Rules"/>.
    /// </summary>
    internal static SemanticAnalysisOutcome Analyse(
        SemanticAnalysisContext context,
        IReadOnlyList<ISemanticRule> rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return SemanticAnalysisOutcome.Empty;
        }

        var findings = new List<Diagnostic>();
        var truncated = false;

        foreach (var rule in rules)
        {
            // A null ELEMENT in the rule set is a registration bug rather than a rule contract
            // violation — there is no rule, and therefore no ISemanticRule.Code, to name — so it
            // stays an ArgumentNullException naming the parameter. That asymmetry with the two
            // checks below is the point: the exception type says whether the defect is in the
            // registry or in a rule.
            ArgumentNullException.ThrowIfNull(rule, nameof(rules));

            // Materialised per rule, and checked for null, before anything is read out of it. A
            // rule returning `null` instead of an empty sequence is breaking the same contract a
            // rule yielding a null FINDING breaks, and both are named as such rather than reaching
            // the worker boundary as a bare NullReferenceException whose message would say only
            // "Object reference not set" — losing the one fact that makes the defect fixable.
            var evaluated = rule.Evaluate(context)
                ?? throw new SemanticRuleContractViolationException(
                    rule.Code, SemanticRuleContractViolation.NullFindingSequence);

            foreach (var finding in evaluated)
            {
                // The hygiene gate runs on EVERY finding a rule produced, including ones past the
                // cap that will never be published. Checking before the cap rather than after is
                // deliberate: the check exists to catch a server BUG (a rule quoting a reference),
                // and a bug that only manifests on finding number 1 001 must not be one this server
                // silently stops looking for on large documents.
                RejectIfItEchoesASecretReference(rule, finding);

                if (findings.Count >= MaxPublishedFindings)
                {
                    truncated = true;
                    continue;
                }

                findings.Add(finding);
            }
        }

        return new SemanticAnalysisOutcome(findings, truncated);
    }

    /// <summary>
    /// The most findings this channel ever publishes on one call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wire is what must stay bounded</b> — the same rationale
    /// <see cref="SuiteSummaryBuilder.MaxEntriesPerList"/> states for the digest's lists, and the
    /// same measurement shape behind it. A semantic finding is per-NODE for some rules, so a
    /// document is not bounded to a small number of them: a 3.3&#160;MB <b>valid</b> suite produced
    /// <b>200 000 findings and a 94&#160;MB result</b>, which crosses the worker pipe (50&#160;MB
    /// cap) and lands in a host's context window. A thousand findings is already far past the point
    /// where a reader or an agent acts on the list rather than on the first few; beyond it the array
    /// is cost without information.
    /// </para>
    /// <para>
    /// <b>Cap-plus-flag, never cap-and-be-quiet</b> — see
    /// <see cref="SemanticAnalysisOutcome.Truncated"/>. A consumer must never have to infer
    /// incompleteness from a count of exactly this number, which is both wrong for a document with
    /// exactly this many findings and unavailable to anything reading one entry.
    /// </para>
    /// <para>
    /// One overall cap rather than one per rule: the question a consumer can act on is "is this list
    /// complete?", and a per-rule budget would answer a question nobody asks while making the
    /// reported set depend on registration order.
    /// </para>
    /// </remarks>
    public const int MaxPublishedFindings = 1000;

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
    /// <b>The five checked surfaces, and why exactly those.</b>
    /// <see cref="Diagnostic.Message"/>, <see cref="Diagnostic.Path"/>,
    /// <see cref="DiagnosticFix.Description"/>, <see cref="DiagnosticFix.Replacement"/> and
    /// <see cref="Diagnostic.DocsUrl"/> are every wire-serialised STRING a
    /// <see cref="Diagnostic"/> carries except one. The first four are the finding's RULE-COMPOSED
    /// text: prose (or a path, or a literal replacement) the rule itself builds out of
    /// document-derived material, all of it reaching the caller. A fix is not a lesser surface than
    /// a message — a <c>Replacement</c> is the one field a host may apply verbatim — so leaving
    /// either half of it unchecked would leave the natural "here is the corrected line" draft as an
    /// unguarded door out of the fact set.
    /// </para>
    /// <para>
    /// <b><see cref="Diagnostic.DocsUrl"/> is checked even though it is derived from the code</b>
    /// (fifth-round peer follow-up). The derivation is real —
    /// <see cref="VfxCodeCatalogue.CreateDiagnostic"/> takes it from the catalogue — but it is a
    /// CONVENTION, not a constraint: <see cref="Diagnostic"/>'s constructor is public and takes
    /// <c>docsUrl</c> as a free string, so a rule bypassing the helper could put anything there. A
    /// catalogue URL can never legitimately contain <c>${</c>, so the check has exactly zero
    /// false-positive risk and closes the gap that "it is derived" only ever closed by good
    /// behaviour. The remaining fields cannot carry text at all:
    /// <see cref="Diagnostic.Code"/> and <see cref="Diagnostic.Severity"/> are validated against
    /// closed sets at construction, and <see cref="DiagnosticLocation"/>'s numbers are numbers. Its
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
    /// <b>US-S2-03 has now done exactly that</b> — see
    /// <see cref="SemanticAnalysisContext.SourceName"/> — and the exclusion held for the stated
    /// reason: that value is the caller's own path (or the inline marker), never anything the
    /// document said.
    /// </para>
    /// <para>
    /// <b>An ACCEPTED coverage gap, recorded here rather than left to be rediscovered.</b> The
    /// worker-boundary arm of this guard — a contract violation crossing the
    /// <c>--validate-worker</c> process boundary and arriving at the caller as <c>VFX-E-1901</c> —
    /// is exercised IN-PROCESS only (<c>SemanticSeamTests</c> drives a deliberately-misbehaving fake
    /// rule through <see cref="Analyse(SemanticAnalysisContext, IReadOnlyList{ISemanticRule})"/>).
    /// There is no end-to-end test, and there cannot be a legitimate one: every rule in
    /// <see cref="Rules"/> routes document-derived names through
    /// <c>SemanticFinding.Identifier</c>, so no correct production rule can trip this guard on any
    /// input — which is the property the rules' own tests assert. A test-only registration seam (an
    /// internal setter on <see cref="Rules"/>, or an environment variable the worker reads) was
    /// considered and REJECTED: it would add a way to inject arbitrary rules into a process whose
    /// whole job is to evaluate untrusted content, buying coverage of one <c>catch</c> arm in
    /// <c>Program.cs</c> at the price of a real attack surface. The arm itself is two lines and is
    /// covered by <c>RealValidationWorkerProcessTests</c>' general crash-to-VFX-E-1901 path.
    /// </para>
    /// </remarks>
    private static void RejectIfItEchoesASecretReference(ISemanticRule rule, Diagnostic finding)
    {
        // A rule that yields a null element is breaking its own contract, and naming WHICH rule is
        // the whole value of the report — so this is the dedicated type carrying a sanctioned
        // reason, not a bare ArgumentNullException that would reach the worker boundary as an
        // unprintable message. (Contrast the null-RULE check in Analyse: there is no rule to name
        // there, so that one stays an ArgumentNullException.)
        if (finding is null)
        {
            throw new SemanticRuleContractViolationException(
                rule.Code, SemanticRuleContractViolation.NullFinding);
        }

        string offendingField;
        if (CarriesSecretReference(finding.Message))
        {
            offendingField = nameof(Diagnostic.Message);
        }
        else if (CarriesSecretReference(finding.Path))
        {
            offendingField = nameof(Diagnostic.Path);
        }
        else if (CarriesSecretReference(finding.DocsUrl))
        {
            offendingField = nameof(Diagnostic.DocsUrl);
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
