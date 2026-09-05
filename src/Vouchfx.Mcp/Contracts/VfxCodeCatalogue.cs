using System.Text.Json;

namespace Vouchfx.Mcp.Contracts;

// Vouchfx.Mcp.Contracts — VfxCodeCatalogue (Sprint 1 / US-S1-04, spec §4.4).
//
// THE single source of kind → code truth for this server. Before this story, every error and
// diagnostic this server emitted carried an ad-hoc string `kind` ("file-not-found", "suite-invalid",
// …) minted at its call site, with no central registry, no stability guarantee, and no way to ask
// "what codes can this server produce?". This file is that registry: one table, one entry per code,
// each carrying the rationale for its range placement and its retryable decision.
//
// ---------------------------------------------------------------------------------------------
// The classification rule (spec §4.4), applied consistently by every entry below
// ---------------------------------------------------------------------------------------------
//
//   VFX-D-####  A DIAGNOSTIC. The pipeline DETERMINED something about the suite/run and reports it
//               as data on a SUCCESSFUL call (isError: false). "Your suite has a schema violation"
//               is an answer, not a failure to answer.
//
//   VFX-E-####  An ERROR. The call itself could not be performed and the answer was never
//               determined (isError: true). "I could not find the file" is a failure to answer.
//
// The discriminator that decides which one a given legacy `kind` becomes is already written, in
// prose, in SuiteValidationModels.cs's own field documentation: `validation-timeout` and
// `validation-worker-failed` are documented there as cases where "the suite's actual validity was
// never determined". That is exactly the line above. Everything that reaches a determination about
// the suite (schema, yaml-parse, too-large, too-deep, alias-limit, unknown-step-type) is a
// diagnostic; everything that fails before a determination is possible (file-not-found,
// file-access-error, invalid-path, validation-timeout, validation-worker-failed) is an error.
//
// "About the suite", deliberately, rather than "about the suite's CONTENT": SuiteFileTooLarge
// reaches its determination from the file's LENGTH ALONE, without ever reading a byte of content
// (that is the whole point of the guard — it refuses to load what it is rejecting). It is still a
// determination about the suite, and still an answer rather than a failure to answer, which is what
// the rule actually turns on.
//
// ---------------------------------------------------------------------------------------------
// What was deliberately NOT migrated, and why
// ---------------------------------------------------------------------------------------------
//
// Two `kind` fields that a naive grep finds are ENGINE-minted values this server merely relays, and
// rewriting either would break this repository's governing invariant ("CLI and MCP must not
// drift" — see CLAUDE.md):
//
//   * PlanCoverageFinding.Kind ("dependency-missing-step-type", "step-flaky", …) is read verbatim
//     out of `vouchfx plan --json`. A host comparing MCP output against CLI output must see the
//     same string.
//   * EnvironmentErrorSummary/EnvironmentErrorDiagnosis.ErrorKind ("ImagePull", "Provision", …) is
//     the engine's own OrchestrationErrorKind name, relayed out of the run's event stream. The
//     `environment-error` token in SuiteEventParser is likewise the ENGINE's event-type
//     discriminator being MATCHED on input, not a kind this server emits — and an environment error
//     must never surface as a tool failure anyway (that is the four-verdict taxonomy invariant:
//     Docker being down is an EnvironmentError verdict on a SUCCESSFUL call, never isError).
//
// ---------------------------------------------------------------------------------------------
// Why the table is enumerable rather than a bag of loose constants
// ---------------------------------------------------------------------------------------------
//
// US-S1-05 generates a docs/errors/VFX-*.md catalogue page per code, and US-S1-06 gates both
// directions of that mapping in CI ("no orphan codes, no orphan docs"). Both need to ENUMERATE the
// full set, which a scattering of `const string` fields cannot offer. Hence All: one ordered,
// exhaustive list, from which every consumer — the doc generator, the completeness gate, and this
// server's own emission helpers — derives its answer.
//
// The emission helpers (CreateError/CreateDiagnostic) are what make this table load-bearing at
// runtime rather than decorative: a call site names a code and a message, and `retryable` and
// `docsUrl` come from THIS table. They therefore cannot drift from it, because there is no second
// place to write them down.

/// <summary>Which of the two canonical shapes a catalogued code belongs to.</summary>
internal enum VfxCodeKind
{
    /// <summary>A <c>VFX-D-####</c> finding, returned as data on a successful call.</summary>
    Diagnostic,

    /// <summary>A <c>VFX-E-####</c> failure, returned as <c>isError: true</c>.</summary>
    Error,
}

/// <summary>One catalogued code and everything this server knows about it.</summary>
/// <param name="Code">The full <c>VFX-E-####</c>/<c>VFX-D-####</c> code.</param>
/// <param name="Name">
/// A stable symbolic name (e.g. <c>PathOutsideWorkspace</c>) — the title US-S1-05's catalogue page
/// is expected to carry, and the name a reader greps for when a code shows up in a host's logs.
/// </param>
/// <param name="Kind">Diagnostic (data, successful call) or Error (the call could not be performed).</param>
/// <param name="Retryable">
/// Whether retrying the same call, UNCHANGED, might succeed. Always <see langword="false"/> for a
/// <see cref="VfxCodeKind.Diagnostic"/>, which is not a failure at all; meaningful only for an
/// error, where it is surfaced on <see cref="VfxError.Retryable"/>.
/// </param>
/// <param name="LegacyKind">
/// The ad-hoc <c>kind</c> string this code replaced, or <see langword="null"/> for a call site that
/// previously returned a bare, code-less message. Kept so the migration stays auditable — a reader
/// (or a future bug report quoting an old string) can trace any pre-US-S1-04 kind to its code.
/// </param>
/// <param name="Summary">A one-line description of what the code means.</param>
internal sealed record VfxCodeEntry(
    string Code,
    string Name,
    VfxCodeKind Kind,
    bool Retryable,
    string? LegacyKind,
    string Summary)
{
    /// <summary>This code's catalogue URL, derived from <see cref="Code"/> — never stored separately.</summary>
    public string DocsUrl => VfxCodeCatalogue.DocsUrlFor(Code);
}

/// <summary>
/// The single source of <c>kind</c> → <c>VFX-*</c> code truth (US-S1-04). See this file's header
/// comment for the classification rule, the two deliberately-unmigrated engine-minted kinds, and
/// why the table is enumerable.
/// </summary>
internal static class VfxCodeCatalogue
{
    // ── 1000-1099 Workspace / path / config ───────────────────────────────────────────────────

    /// <summary>A path that resolves outside the addressable workspace — today, a UNC/network location.</summary>
    public const string PathOutsideWorkspace = "VFX-E-1001";

    /// <summary>The named suite file does not exist on disk.</summary>
    public const string SuiteFileNotFound = "VFX-E-1002";

    /// <summary>The suite file exists but could not be opened or read.</summary>
    public const string SuiteFileUnreadable = "VFX-E-1003";

    /// <summary>The named run events file does not exist on disk.</summary>
    public const string EventsFileNotFound = "VFX-E-1004";

    /// <summary>The run events file exists but could not be opened or read.</summary>
    public const string EventsFileUnreadable = "VFX-E-1005";

    /// <summary>A tool argument failed validation before anything was spawned.</summary>
    public const string InvalidToolArgument = "VFX-E-1006";

    // ── 1100-1199 Schema validation ───────────────────────────────────────────────────────────

    /// <summary>Umbrella code for <c>run_suite</c>'s "the suite did not pass pre-flight validation" envelope.</summary>
    public const string SuiteInvalid = "VFX-D-1100";

    /// <summary>A genuine JSON Schema violation in the suite document.</summary>
    public const string SchemaViolation = "VFX-D-1101";

    /// <summary>The suite's YAML could not be parsed.</summary>
    public const string YamlParseError = "VFX-D-1102";

    /// <summary>The suite file exceeds the size bound the YAML-bomb guard enforces.</summary>
    public const string SuiteFileTooLarge = "VFX-D-1103";

    /// <summary>The suite's YAML nests deeper than the YAML-bomb guard allows.</summary>
    public const string SuiteNestingTooDeep = "VFX-D-1104";

    /// <summary>The suite's YAML declares more anchors/aliases than the YAML-bomb guard allows.</summary>
    public const string SuiteAliasLimitExceeded = "VFX-D-1105";

    /// <summary>The pinned CLI's live <c>vouchfx schema</c> export disagrees with the embedded vendored schema.</summary>
    public const string LiveSchemaMismatch = "VFX-D-1106";

    /// <summary>The suite's YAML contains a line longer than the YAML-bomb guard's per-line limit.</summary>
    public const string SuiteLineTooLong = "VFX-D-1107";

    /// <summary>The isolated validation worker exceeded its wall-clock budget and was killed.</summary>
    public const string ValidationTimeout = "VFX-E-1150";

    /// <summary><c>get_schema</c> was asked for a section the composed schema does not contain.</summary>
    public const string SchemaSectionNotFound = "VFX-E-1151";

    /// <summary><c>validate_suite</c> was given both <c>path</c> and <c>yaml</c>, or neither.</summary>
    public const string AmbiguousSuiteInput = "VFX-E-1152";

    // ── 1200-1299 Semantic validation ─────────────────────────────────────────────────────────

    /// <summary>A step's <c>type</c> matches no step type the engine defines.</summary>
    public const string UnknownStepType = "VFX-D-1201";

    /// <summary>A step's <c>target</c> names no service or dependency the <c>environment</c> declares.</summary>
    public const string DanglingTargetReference = "VFX-D-1202";

    /// <summary>A <c>{placeholder}</c> is interpolated before any capture or variable provides it.</summary>
    public const string PlaceholderUsedBeforeDefinition = "VFX-D-1203";

    /// <summary>A <c>capture</c> declares a variable name nothing in the suite ever interpolates.</summary>
    public const string UnusedCapture = "VFX-D-1204";

    /// <summary>A step type needs a dependency kind <c>environment.dependencies</c> does not declare.</summary>
    public const string UndeclaredDependencyType = "VFX-D-1205";

    /// <summary><c>verifyMode: RETRY</c> with no <c>timeout</c>, or a <c>timeout</c> above the advisory maximum.</summary>
    public const string RetryTimeoutPolicy = "VFX-D-1206";

    /// <summary>A secret LITERAL is embedded in the suite where a <c>${secret:…}</c> reference belongs.</summary>
    public const string SecretLiteralInSuite = "VFX-D-1207";

    /// <summary>Two or more steps declare the same <c>id</c>.</summary>
    public const string DuplicateStepId = "VFX-D-1208";

    /// <summary>An asynchronous step type is left on the default <c>IMMEDIATE</c> verify mode.</summary>
    public const string AsyncStepWithoutRetry = "VFX-D-1209";

    /// <summary>
    /// A step names a topic/path/table absent from the extracted topology. <b>Reserved and
    /// implemented, but never emitted</b> until upstream ask U1 lands — see this code's catalogue
    /// entry and <c>Validation/Semantics/TopologyCrossCheckRule.cs</c>.
    /// </summary>
    public const string TopologyCrossCheck = "VFX-D-1210";

    /// <summary>The suite declares no <c>metadata.owner</c> and/or no <c>metadata.tags</c>.</summary>
    public const string MetadataIncomplete = "VFX-D-1211";

    /// <summary><c>describe_step_type</c> was asked about a type the live catalogue does not contain.</summary>
    public const string StepTypeNotInCatalogue = "VFX-E-1250";

    // ── 1300-1399 Compilation ─────────────────────────────────────────────────────────────────

    /// <summary>The scaffold call produced no suite — the engine rejected the intent, or the CLI
    /// timed out, overran its output cap, or produced nothing (see the catalogue entry).</summary>
    public const string ScaffoldFailed = "VFX-E-1301";

    // ── 1400-1499 Orchestration / environment ─────────────────────────────────────────────────

    /// <summary>The pinned engine CLI is missing, version-mismatched, unparseable, or not launchable.</summary>
    public const string EngineCliUnavailable = "VFX-E-1401";

    // ── 1500-1599 Execution / run lifecycle ───────────────────────────────────────────────────

    /// <summary>Another <c>run_suite</c> call is already active on this server instance.</summary>
    public const string RunInProgress = "VFX-E-1501";

    // ── 1600-1699 Analysis (topology / impact) ────────────────────────────────────────────────

    /// <summary>No events path was given and no run has completed in this session.</summary>
    public const string NoRunToExplain = "VFX-E-1601";

    /// <summary>The events file was read but contained no recognisable vouchfx event.</summary>
    public const string NoRecognisableEvents = "VFX-E-1602";

    /// <summary>The Planner ran but could not complete its coverage analysis.</summary>
    public const string PlanAnalysisFailed = "VFX-E-1603";

    // ── 1900-1999 Internal / unexpected ───────────────────────────────────────────────────────

    /// <summary>The isolated validation worker could not be started, crashed, or produced unusable output.</summary>
    public const string ValidationWorkerFailed = "VFX-E-1901";

    /// <summary>An orchestrator returned an outcome case this server does not know how to render.</summary>
    public const string UnrecognisedOutcome = "VFX-E-1902";

    /// <summary><c>explain_diagnostic</c> was asked about a code with no catalogue entry.</summary>
    public const string UnknownDiagnosticCode = "VFX-E-1903";

    /// <summary>
    /// The URL prefix every code's <see cref="VfxCodeEntry.DocsUrl"/> is built on. US-S1-05 is what
    /// makes these resolve, by publishing <c>docs/errors/&lt;CODE&gt;.md</c> on THIS repository's own
    /// site: spec §4.4 named <c>https://vouchfx.io/docs/errors/</c> — the ENGINE's site — as a
    /// placeholder before this story existed to publish anything at all, but the pages this story
    /// actually ships render at <c>vouchfx-mcp.vouchfx.io</c> (see <c>scripts/build_site.py</c> and
    /// this repo's own <c>docs/</c> tree, which is auto-published there — never the engine's site).
    /// Repo reality wins over the spec's placeholder naming here, the same rule <c>VfxCode.cs</c>'s
    /// header already applies elsewhere in this file. Measured against a real <c>_site/</c> build:
    /// every code's <see cref="DocsUrlFor"/> value is byte-exact against its own <c>sitemap.xml</c>
    /// entry, e.g. <c>https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1002.html</c>.
    /// </summary>
    public const string DocsUrlPrefix = "https://vouchfx-mcp.vouchfx.io/docs/errors/";

    /// <summary>
    /// The file extension every rendered catalogue page carries on the published site (see
    /// <see cref="DocsUrlPrefix"/>'s remarks) — <c>scripts/build_site.py</c> renders every
    /// <c>docs/errors/&lt;CODE&gt;.md</c> source to a sibling <c>&lt;CODE&gt;.html</c>, never an
    /// extensionless or trailing-slash path, so <see cref="DocsUrlFor"/> must append it to resolve.
    /// </summary>
    private const string DocsUrlSuffix = ".html";

    /// <summary>
    /// Every code this server can emit, in ascending numeric order. Exhaustive by construction and
    /// asserted exhaustive by <c>VfxCodeCatalogueTests</c>, which cross-checks this list against a
    /// scan of every <c>VFX-*</c> literal in <c>src/</c> (the pattern <c>SecretHygieneSourceGuardTests</c>
    /// established) so a code minted at a call site without an entry here fails by name.
    /// </summary>
    public static IReadOnlyList<VfxCodeEntry> All { get; } =
    [
        // ── 1000-1099 Workspace / path / config ──────────────────────────────────────────────
        //
        // Range rationale (shared by 1001-1006): every code here fires BEFORE any suite content is
        // reached — the file could not be located, opened, or the argument naming it was rejected
        // outright. None of them is a statement about what the suite says.

        new(PathOutsideWorkspace, "PathOutsideWorkspace", VfxCodeKind.Error, Retryable: false, "invalid-path",
            // Mandated as exactly 1001 by the sprint plan: this is the PathOutsideWorkspace-family
            // code, populated now for what PathSafetyGuard already rejects (UNC/network paths, the
            // M2 forced-authentication guard). Full workspace containment lands in Sprint 3 and
            // will widen what reaches this code WITHOUT minting a second one for the same idea.
            // Not retryable: a path does not stop being a network path on a second attempt.
            "The path resolves to a network/UNC location rather than a local file inside the workspace."),

        new(SuiteFileNotFound, "SuiteFileNotFound", VfxCodeKind.Error, Retryable: false, "file-not-found",
            // Not retryable: a file that does not exist will not exist on a retry of the identical
            // call. The caller must change the path or create the file — both of which make it a
            // DIFFERENT call.
            "The named .e2e.yaml suite file does not exist."),

        new(SuiteFileUnreadable, "SuiteFileUnreadable", VfxCodeKind.Error, Retryable: false, "file-access-error",
            // Not retryable, deliberately, even though ONE of the causes (a momentarily locked
            // file) is transient while the others (permissions, an over-long path) are not. This
            // code cannot tell them apart, and `retryable: true` invites a host to spin on a
            // permission error forever. False is the honest answer when the server cannot
            // distinguish; a host that knows better can still retry on its own judgement.
            "The suite file exists but could not be read (permissions, a lock, or an over-long path)."),

        new(EventsFileNotFound, "EventsFileNotFound", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Distinct from SuiteFileNotFound rather than shared with it: the remediation differs
            // (re-run the suite to produce events, versus fix the suite path), and US-S1-05's
            // catalogue page is only useful if it can say which. Previously a bare message on
            // explain_run/diagnose_run with no code at all.
            "The run events file explain_run/diagnose_run was pointed at does not exist."),

        new(EventsFileUnreadable, "EventsFileUnreadable", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Not retryable for the same reason as SuiteFileUnreadable — see its rationale.
            "The run events file exists but could not be read."),

        new(InvalidToolArgument, "InvalidToolArgument", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // One code shared by run_suite / plan_coverage / scaffold_suite, because it is one
            // condition: the call as written cannot be performed and the caller must change an
            // argument. Splitting it per tool would multiply catalogue pages that all say the same
            // thing. Never retryable by definition — the fix is a different call.
            "A tool argument failed validation (argument injection, an out-of-range value, or a rejected path)."),

        // ── 1100-1199 Schema validation ──────────────────────────────────────────────────────
        //
        // Range rationale: findings produced BY, and failures produced WITHIN, validate_suite's own
        // schema-validation pipeline. Note the deliberate sub-split — 1100-1149 are the D findings
        // this pipeline reaches, 1150+ are the E failures of the pipeline itself. Prefix and number
        // are orthogonal (spec §4.4's ranges are numeric areas shared by both prefixes), so without
        // that convention a reader scanning the range cannot tell at a glance which entries are
        // data and which are failures.

        new(SuiteInvalid, "SuiteInvalid", VfxCodeKind.Diagnostic, Retryable: false, "suite-invalid",
            // THE highest-risk mapping in this story, and the reason it is a D and not an E: this
            // is run_suite's EDGE-003 envelope, and it must keep coming back through
            // StructuredToolResult.Success with isError false. An MCP client keying off isError has
            // never seen an invalid suite as a tool failure and must not start now. 1100 is the
            // range's umbrella value precisely because this code carries no finding of its own —
            // the specific findings (1101-1105, 1201) travel inside its `validation.errors` array.
            "run_suite did not run the suite because it failed pre-flight validation."),

        new(SchemaViolation, "SchemaViolation", VfxCodeKind.Diagnostic, Retryable: false, "schema",
            "A field in the suite violates the engine's JSON Schema."),

        new(YamlParseError, "YamlParseError", VfxCodeKind.Diagnostic, Retryable: false, "yaml-parse",
            // A diagnostic, not an error: "your YAML does not parse" IS the validation answer the
            // caller asked for, and validate_suite has always returned it as valid:false data.
            "The suite's YAML could not be parsed."),

        new(SuiteFileTooLarge, "SuiteFileTooLarge", VfxCodeKind.Diagnostic, Retryable: false, "too-large",
            // Beyond the sprint plan's confirmed list — found by this story's audit. A YAML-bomb
            // guard rejection is still a DETERMINATION about the suite ("this suite is not
            // acceptable"), so it stays a diagnostic and validate_suite's isError stays false,
            // exactly as before the migration.
            "The suite file exceeds the maximum size the YAML-bomb guard permits."),

        new(SuiteNestingTooDeep, "SuiteNestingTooDeep", VfxCodeKind.Diagnostic, Retryable: false, "too-deep",
            // Audit finding, same rationale as SuiteFileTooLarge.
            "The suite's YAML nests deeper than the YAML-bomb guard permits."),

        new(SuiteAliasLimitExceeded, "SuiteAliasLimitExceeded", VfxCodeKind.Diagnostic, Retryable: false, "alias-limit",
            // Audit finding, same rationale as SuiteFileTooLarge. This is the "billion laughs"
            // defence's own finding.
            "The suite's YAML declares more anchors/aliases than the YAML-bomb guard permits."),

        new(LiveSchemaMismatch, "LiveSchemaMismatch", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // Sprint 2 / US-S2-01: get_schema's live cross-verification finding, and the FIRST code
            // in this catalogue to be emitted through the rich Contracts/Diagnostic record rather
            // than grafted onto the pre-existing SuiteValidationError wire shape (see Diagnostic.cs's
            // header, which anticipated exactly this).
            //
            // WHY 1100-1199 rather than 1400-1499 (orchestration/environment), which is where a
            // reader might first look given the trigger is a mismatched INSTALL. Two reasons, and
            // the second is the decisive one:
            //   1. The finding's subject is the SCHEMA DOCUMENT — "these two composed schemas
            //      disagree" — not the engine's availability. VFX-E-1401 (EngineCliUnavailable)
            //      already owns "the environment this server orchestrates through is not there",
            //      and this fires only when the CLI IS there and DID answer.
            //   2. This range is where a reader looking up "something about the schema" will
            //      already be, next to the schema-validation findings a suite author sees daily.
            //
            // 1106 rather than 115x: this file's own D-low/E-high sub-split convention for the
            // 1100-1199 range (see the range header above) puts diagnostics at 1100-1149 and the
            // pipeline's own failures at 1150+. The convention is what lets a reader scanning the
            // range tell data from failure at a glance, and it applies to any code in the range,
            // not only to validate_suite's.
            //
            // Retryable is false by definition for every Diagnostic (see VfxCodeEntry.Retryable) —
            // a finding is not a failure to retry. It is also a fact about this one: the installed
            // engine does not change between two identical calls.
            "The installed vouchfx CLI's composed schema differs from the vendored schema this server embeds."),

        new(SuiteLineTooLong, "SuiteLineTooLong", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // Issue #71 (Sprint 2 follow-up): a fourth YAML-bomb-guard finding, alongside
            // SuiteFileTooLarge (1103) / SuiteNestingTooDeep (1104) / SuiteAliasLimitExceeded (1105),
            // and classified exactly as they are — a guard rejection is a DETERMINATION about the
            // suite ("this suite is not acceptable"), so it is a Diagnostic and validate_suite's
            // isError stays false. Retryable is false by definition for every Diagnostic, and a fact
            // about this one too: the offending line does not shorten between two identical calls.
            //
            // WHY 1107, in the 1100-1149 diagnostic sub-range: this is a validate_suite pre-flight
            // guard finding, so it belongs with the other three in the schema-validation range's
            // D-low band (see the range header's D-low/E-high convention), taking the next free
            // number after LiveSchemaMismatch (1106).
            //
            // WHY it exists at all: the three existing guards bound size, nesting depth, and
            // anchor/alias count, but NOT the length of a single line. A plain-scalar mapping key
            // longer than YamlDotNet's 1024-char simple-key bound drives the tokeniser pathological
            // (measured: a ~2 KB key runs the isolated worker past its 10 s wall clock, killed at
            // >90 s → VFX-E-1150 on every validation), so the length of one unbroken line is now
            // bounded before the parse. See YamlSafetyGuard.MaxLineLength for the full root cause.
            "The suite's YAML contains a line longer than the YAML-bomb guard's per-line limit."),

        new(ValidationTimeout, "ValidationTimeout", VfxCodeKind.Error, Retryable: true, "validation-timeout",
            // Range mandated by the sprint plan: this is the validation pipeline's OWN worker
            // timing out, so it belongs to the range that owns that pipeline — neither a
            // Docker/health-gate orchestration timeout (1400-1499) nor a run-lifecycle timeout
            // (1500-1599).
            //
            // The one code in this catalogue where retryable is TRUE on a non-lifecycle failure: a
            // wall-clock kill can be provoked by transient load on the host rather than by the
            // suite, so the identical call genuinely might succeed next time. Contrast
            // SuiteFileNotFound, where it provably cannot.
            "The isolated validation worker exceeded its wall-clock budget and was killed; the suite's validity was never determined."),

        new(SchemaSectionNotFound, "SchemaSectionNotFound", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Sprint 2 / US-S2-01: get_schema was handed a `section` token that addresses nothing —
            // an unrecognised named section, or a `step:<family>.<provider>` whose dotted name the
            // composed schema does not define. The story's acceptance criteria mandate the
            // 1100-1199 range for this specific condition; 1151 follows this range's E-high
            // sub-split convention (see VFX-E-1150's own note) and sits immediately after the only
            // other E code in the range.
            //
            // Deliberately NOT VFX-E-1250 (StepTypeNotInCatalogue), despite the step:<type> arm
            // sounding identical: 1250 is a statement about the LIVE ENGINE CATALOGUE, which
            // describe_step_type consults and which can differ from the vendored schema; this code
            // is a statement about THE EMBEDDED SCHEMA DOCUMENT, which get_schema serves offline.
            // Collapsing them would make one code mean "your type is unknown" in two different
            // sources of truth that this server deliberately keeps distinguishable — and it would
            // also have to cover the unknown-NAMED-section arm, which has nothing to do with step
            // types at all.
            //
            // Deliberately NOT VFX-E-1006 (InvalidToolArgument) either, even though both are
            // "change an argument": 1006 is a value this server rejects on its own terms (an
            // injection-shaped string, an out-of-range number), whereas this one is a well-formed
            // request for something the SCHEMA does not contain. A host can act on the difference —
            // 1151's remedy is "call list_step_types and pick a real type", which is a lookup, not
            // an input fix. The `format` argument, by contrast, IS rejected on this server's own
            // terms and does map to 1006 — see GetSchemaTool's switch.
            //
            // Not retryable: the embedded schema does not grow a section between two identical calls.
            "get_schema was asked for a section or step type the embedded composed schema does not contain."),

        new(AmbiguousSuiteInput, "AmbiguousSuiteInput", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Sprint 2 / US-S2-02: validate_suite gained an inline `yaml` input alongside `path`,
            // and with it the one rule neither JSON Schema nor the pipeline can enforce — exactly
            // one of the two. Both, or neither, is this code.
            //
            // WHY 1100-1199 rather than VFX-E-1006 (InvalidToolArgument), which is where "the caller
            // must change an argument" normally lives, and which get_schema's own `format` arm uses.
            // The story's acceptance criteria mandate this range, and the mandate is defensible on
            // the same distinction VFX-E-1151's entry already draws: 1006 is a value this server
            // rejects on its own terms (an injection-shaped string, an out-of-range number), whereas
            // this is a well-formed pair of arguments that fails a rule specific to
            // VALIDATE_SUITE'S OWN CONTRACT. A host can act on the difference — the remedy here is
            // "drop one of the two arguments", which is knowable from the code alone without reading
            // the message. validate_suite's `level` argument, by contrast, IS rejected on this
            // server's own terms and does map to 1006 — see Tools/ValidateSuiteInput.
            //
            // 1152 follows this range's E-high sub-split convention (see VFX-E-1150's own note) and
            // sits immediately after the range's other two E codes.
            //
            // ONE code for both shapes, not two: they are the same condition (the call does not
            // identify exactly one suite) with the same remedy, so a second code would be a second
            // catalogue page saying the same thing. The two shapes are told apart by their MESSAGES,
            // which is where a human — not a host's switch statement — needs the distinction.
            //
            // Not retryable: the identical call carries the identical arguments and fails identically.
            "validate_suite was given both 'path' and 'yaml', or neither; exactly one is required."),

        // ── 1200-1299 Semantic validation ────────────────────────────────────────────────────
        //
        // Range rationale: findings that need the step-type VOCABULARY, not just the schema's
        // shape. Same D-low/E-high sub-split convention as 1100-1199.

        new(UnknownStepType, "UnknownStepType", VfxCodeKind.Diagnostic, Retryable: false, "unknown-step-type",
            // Mandated as EXACTLY 1201 — spec §5.5 already names this code, and Sprint 2's
            // semantic-rules work (which adds the Levenshtein closest-match suggestion to this
            // finding's MESSAGE) builds on it. US-S1-04 maps the code and leaves the message alone;
            // minting a second code for the same finding is the specific mistake this entry exists
            // to prevent.
            "A step's type matches no step type the engine's catalogue defines."),

        // ── VFX-D-1202 … VFX-D-1211: the semantic RULE set (Sprint 2 / US-S2-03) ─────────────
        //
        // Ten codes landing together, from spec §5.5's own rule table, in the table's own order.
        // Three properties they share, stated once here rather than ten times below:
        //
        //   * Every one is a DIAGNOSTIC, and cannot be anything else. A semantic rule's output type
        //     is Diagnostic (see ISemanticRule), whose constructor rejects a non-VFX-D code — so
        //     "this rule decided the call could not be performed" is not expressible by construction.
        //     Retryable is therefore false for all ten, by the rule VfxCodeEntry.Retryable states.
        //
        //   * SEVERITY is a property of the FINDING, not of the code — literally so since the
        //     US-S2-03 review: VFX-D-1207 emits at BOTH severities, `error` for its three
        //     structural arms (a private-key PEM header, an AKIA/ASIA body, a `password=` with a
        //     real value) and `warning` for its entropy arm, which is a guess about an opaque token
        //     and measurably fired on valid build paths. Only the structural arms flip the verdict.
        //     See SecretLiteralRule's remarks. And this server is deliberately
        //     conservative about it. Spec §5.5's table annotates five entries explicitly — 1204
        //     (warning), 1206 (warning), 1209 (warning), 1210 (warning), 1211 (info) — and marks
        //     exactly ONE as an error: 1207. The five it leaves unannotated (1201, 1202, 1203, 1205,
        //     1208) all ship as WARNINGS here. That is a decision, not a default: `errors` is the
        //     channel that answers "will the engine accept this suite?", and this channel is this
        //     server's own advice about a document the schema already accepted. Only 1207 flips the
        //     suite's verdict, and only because the story's own Gherkin says it must (see
        //     SuiteValidator.AnalyseYaml, where that reconciliation is recorded at the computation
        //     site).
        //
        //   * The range is 1200-1299 ("semantic validation") for the reason the range header above
        //     gives: every one of them needs the step-type VOCABULARY or the document's own
        //     declaration sets, not just the schema's shape. None of them is expressible as a JSON
        //     Schema keyword, which is precisely why they are rules.

        new(DanglingTargetReference, "DanglingTargetReference", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // Both halves of the union are legitimate: the composed schema's own `target`
            // description for a broker step says a declared DEPENDENCY or a declared SERVICE will
            // do, so the rule tests membership against both sets and this summary says both.
            "A step's target names no service or dependency the suite's environment declares."),

        new(PlaceholderUsedBeforeDefinition, "PlaceholderUsedBeforeDefinition", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // The ORDER-AWARE one: the same token is correct after its capture and wrong before it,
            // so this code says "not yet", never "never".
            "A {placeholder} is interpolated before any capture or root variable provides its value."),

        new(UnusedCapture, "UnusedCapture", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            "A step captures a variable no later step ever interpolates."),

        new(UndeclaredDependencyType, "UndeclaredDependencyType", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            "A step type needs a dependency kind the suite's environment.dependencies never declares."),

        new(RetryTimeoutPolicy, "RetryTimeoutPolicy", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // ONE code for both arms of spec §5.5's row, deliberately, on the same reasoning
            // AmbiguousSuiteInput's entry gives: they are one subject (a RETRY step's polling
            // window is not stated well) with one remedy (state a sensible timeout), and the
            // messages — not a host's switch statement — are where the two are told apart.
            "A RETRY step declares no timeout (the engine's default applies), or one above the advisory maximum."),

        new(SecretLiteralInSuite, "SecretLiteralInSuite", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // THE one error-severity semantic finding, and the one whose code number the spec's own
            // §4.8 got wrong: that section names an E-prefixed code in the 1210 slot
            // ("SecretLiteralInSpec") for a tool (`write_spec`) plan D3 dropped entirely. Spec
            // §5.5's rule table — the authority for this channel — says VFX-D-1207, and a D is also
            // the only classification consistent with this catalogue's own rule: validate_suite
            // still SUCCEEDS and returns the finding as data. Deliberately not renumbered to match
            // §4.8's prose: the 1210 slot is spelled out in §5.5's table as the topology
            // cross-check, and this repo follows the table. (The §4.8 code is written here in
            // words rather than as a literal on purpose — VfxCodeCatalogueTests scans src/ for
            // prefixed literals and treats every one as a code this server has claimed.)
            //
            // The finding never reproduces the offending value — that is the point of it.
            "A secret literal is embedded in the suite where a ${secret:...} reference belongs."),

        new(DuplicateStepId, "DuplicateStepId", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // Not expressible as a schema keyword: the composed schema constrains an id's SHAPE
            // (^[A-Za-z_][A-Za-z0-9_-]*$) but JSON Schema has no cross-element uniqueness keyword
            // for "distinct by property" over an array of objects.
            "Two or more steps in the suite declare the same id."),

        new(AsyncStepWithoutRetry, "AsyncStepWithoutRetry", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // The only code in the set whose finding carries a MACHINE-APPLICABLE fix
            // (DiagnosticFix.Replacement), because it is the only one whose remedy is a single
            // literal line with no authoring judgement in it.
            "An asynchronous step type asserts once instead of polling — verifyMode: RETRY is missing."),

        new(TopologyCrossCheck, "TopologyCrossCheck", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // RESERVED AND IMPLEMENTED, BUT NEVER EMITTED IN THIS SPRINT. The rule body exists
            // (Validation/Semantics/TopologyCrossCheckRule.cs) and is unit-tested against a
            // hand-built topology, but it is NOT in SemanticAnalyser.Rules and nothing in src/
            // constructs a topology to hand it — because the only source of one is upstream ask U1
            // (`vouchfx topology --json`), which is outstanding (sprint-00-overview.md §3).
            //
            // Catalogued anyway, on purpose. US-S1-06's bidirectional completeness gate defines an
            // "emitting site" as a VFX-*-#### literal occurring anywhere in src/, and this entry is
            // that site — so the code owns a docs/errors/VFX-D-1210.md page today, exactly as the
            // sprint spec's fifth Gherkin scenario requires ("the sprint's completeness gate still
            // recognises VFX-D-1210 as a catalogued, reserved code"). Reserving the number now is
            // also what keeps the eventual landing ADDITIVE: no renumbering, no second page, no
            // host-visible change beyond findings starting to arrive.
            //
            // There is deliberately no configuration flag that turns it on. Per sprint-00 §3's
            // gated-feature stances, a partially-derived topology verdict would be a fabricated
            // value for the missing portion; the honest shape pre-U1 is silence.
            "A step names a topic, path, or table absent from the workspace's extracted topology."),

        new(MetadataIncomplete, "MetadataIncomplete", VfxCodeKind.Diagnostic, Retryable: false, LegacyKind: null,
            // The set's only `info`: nothing about the suite's execution changes, but an unowned,
            // untagged suite cannot be routed when it fails or selected by the runner's own
            // selection language.
            "The suite declares no metadata.owner and/or no metadata.tags."),

        new(StepTypeNotInCatalogue, "StepTypeNotInCatalogue", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Deliberately NOT VFX-D-1201, despite describing the same underlying confusion: this
            // is describe_step_type being ASKED about a type that does not exist, which is a call
            // that cannot be performed (there is nothing to describe), not a finding about a suite.
            // A D code here would have to arrive with isError true, contradicting the rule the
            // whole catalogue rests on.
            "describe_step_type was asked about a step type the live engine catalogue does not contain."),

        // ── 1300-1399 Compilation ────────────────────────────────────────────────────────────

        new(ScaffoldFailed, "ScaffoldFailed", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // The compilation range: scaffold_suite compiles a structured intent into a YAML
            // artefact via `vouchfx scaffold --intent`, and this code covers every way that
            // compilation can end without producing one.
            //
            // FOUR distinct producers in ScaffoldSuiteOrchestrator, not one — the summary below is
            // worded to cover all of them because US-S1-05 generates this code's PUBLIC page from
            // it, and a page that describes only the first would misinform a reader who hit any of
            // the other three:
            //   1. the CLI exited non-zero — the engine rejected the intent (unknown step type,
            //      unknown dependency kind, …);
            //   2. the CLI exceeded its wall-clock budget and was terminated;
            //   3. the CLI overran its output cap and was terminated before capture;
            //   4. the CLI exited 0 but produced empty stdout.
            // Each arm's own MESSAGE stays specific (it names which of the four happened, and what
            // to do); only the catalogue summary is the union.
            //
            // Retryable: false, and this is a MIXED-CAUSE decision of exactly the kind
            // SuiteFileUnreadable (VFX-E-1003) already models — one code must answer once, for all
            // its producers. Arm 2 (timeout) IS transient in isolation, so it is worth stating why
            // this does not simply follow ValidationTimeout (VFX-E-1150), which is retryable: true.
            // The difference is producer count, not principle. VFX-E-1150 has exactly ONE producer,
            // so "true" is precisely right for the whole code. This code's DOMINANT arm is 1, an
            // intent rejection that is deterministic in the input: retrying the identical call
            // loops forever. Marking the union retryable would invite that loop to buy correctness
            // on the one arm where a retry could help — and arms 2 and 3 already tell the caller
            // the useful thing in their own messages ("retry with a smaller intent document"),
            // which is a DIFFERENT call and therefore not what `retryable` means.
            "The scaffold call produced no suite — the engine rejected the intent, or the CLI timed out, "
            + "overran its output cap, or produced nothing."),

        // ── 1400-1499 Orchestration / environment ────────────────────────────────────────────

        new(EngineCliUnavailable, "EngineCliUnavailable", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // The environment this server orchestrates THROUGH is not available. Shared by all five
            // CLI-backed tools, because the condition and its remediation ("install the pinned
            // vouchfx CLI") are identical for every one of them.
            //
            // NOTE for a reader tracing the sprint plan's `environment-error` → VFX-E-14## mapping:
            // this is the code that occupies that range, but it is NOT a migration of the engine's
            // `environment-error` EVENT. See this file's header — that token is engine-minted and
            // relayed as success-path data, and turning it into a VfxError would violate the
            // four-verdict taxonomy invariant.
            //
            // Not retryable: the CLI does not install itself between two identical calls.
            "The pinned vouchfx CLI is missing, version-mismatched, unparseable, or not launchable."),

        // ── 1500-1599 Execution / run lifecycle ──────────────────────────────────────────────

        new(RunInProgress, "RunInProgress", VfxCodeKind.Error, Retryable: true, LegacyKind: null,
            // Both the code number and retryable: true are pre-committed — VfxError.cs's own
            // documentation, written in US-S1-03, cites "VFX-E-1501 RunInProgress" by name as its
            // worked example of a retryable error. Honoured here rather than re-derived.
            //
            // Genuinely retryable, and the clearest case in the catalogue: the identical call will
            // succeed once the in-flight run finishes.
            "Another run_suite call is already active on this server; only one run may be in flight at a time."),

        // ── 1600-1699 Analysis (topology / impact) ───────────────────────────────────────────
        //
        // Range rationale: the read-only analysis tools — explain_run, diagnose_run, plan_coverage —
        // failing to produce an analysis. Distinct from 1000-1099: the input was located and read
        // fine; there was simply nothing analysable in it, or the analysis itself did not complete.

        new(NoRunToExplain, "NoRunToExplain", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Not retryable: the session gains a last-run record only when a run_suite call
            // completes, which is a different call, not a repeat of this one.
            "No eventsPath was given and no run has completed in this session."),

        new(NoRecognisableEvents, "NoRecognisableEvents", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            "The events file was read successfully but contained no recognisable vouchfx event."),

        new(PlanAnalysisFailed, "PlanAnalysisFailed", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // Deliberately NOT a gap-related code. plan_coverage finding gaps is a SUCCESS carrying
            // data (that precedent is guard-tested); this code fires only when the Planner could
            // not produce an analysis at all.
            //
            // FIVE producers in PlanCoverageOrchestrator, enumerated in full for the same
            // public-page reason as ScaffoldFailed above:
            //   1. an unrecognised non-zero exit — notably exit 3, incomplete catalogue metadata;
            //   2. the CLI exceeded its wall-clock budget and was terminated;
            //   3. the CLI's report overran its output cap and was terminated before capture;
            //   4. the CLI exited 0 but produced empty stdout;
            //   5. stdout could not be parsed as the plan report document.
            // A usage error (exit 2 — a bad path, an empty suite folder, an out-of-range threshold)
            // is deliberately NOT here: that is the caller's actionable mistake and maps to
            // InvalidToolArgument instead.
            //
            // Retryable: false, on the same mixed-cause reasoning as ScaffoldFailed — see that
            // entry for the full argument. Arms 2 and 3 are transient in isolation, but the code
            // must answer once for all five, and arms 1/4/5 are deterministic engine-side failures
            // that an identical retry cannot fix. Both transient arms already tell the caller the
            // genuinely useful thing in their own messages ("narrow path or eventsPath and retry"),
            // which is a different call, not a retry.
            "The Planner ran but could not produce a coverage analysis — it failed, timed out, "
            + "overran its output cap, or returned output that could not be read."),

        // ── 1900-1999 Internal / unexpected ──────────────────────────────────────────────────

        new(ValidationWorkerFailed, "ValidationWorkerFailed", VfxCodeKind.Error, Retryable: true, "validation-worker-failed",
            // Range mandated by the sprint plan (internal/unexpected): unlike ValidationTimeout,
            // which is a known-and-handled bound being hit, this is the worker doing something it
            // should not have — failing to start, exiting non-zero, overflowing its output cap, or
            // emitting output that does not parse.
            //
            // Retryable: a failed process START or a one-off crash may well not recur, and unlike a
            // permission error there is no persistent external state making the retry pointless.
            "The isolated validation worker could not be started, crashed, or produced unusable output."),

        new(UnrecognisedOutcome, "UnrecognisedOutcome", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // The `_ =>` arm of each tool's outcome switch. Unreachable by construction today —
            // every outcome type is a closed union with a private constructor — but the arms exist,
            // they can return isError, and an isError result without a code would be a hole in the
            // very contract this story establishes. If this code is ever seen in the wild it means
            // a union grew a case and a tool's switch was not updated: a server bug, which is
            // precisely what the internal range is for.
            "The server produced an outcome it does not know how to render — a bug in this server."),

        new(UnknownDiagnosticCode, "UnknownDiagnosticCode", VfxCodeKind.Error, Retryable: false, LegacyKind: null,
            // US-S1-05: explain_diagnostic's own "I don't recognise that code" answer. Deliberately
            // its own code rather than reusing UnrecognisedOutcome (VFX-E-1902) above, even though
            // both describe "a code this server does not know" on the surface: 1902 fires when THIS
            // SERVER'S OWN outcome switch hits its unreachable default arm — a bug in this server's
            // code, never provoked by caller input. This code fires on a value a CALLER typed into
            // explain_diagnostic's `code` argument — ordinary bad input, the same shape as
            // StepTypeNotInCatalogue (VFX-E-1250) for an unknown step type. Collapsing the two would
            // make a caller typo indistinguishable from a server defect in the one place a host
            // might alert on 1902 specifically.
            //
            // Also load-bearing for US-S1-06's coming bidirectional catalogue-completeness gate
            // (every emitted code has a docs/errors/ page, every page has an emitter): reusing 1902
            // here would still need this code's OWN page anyway (1902 already means something else),
            // so minting it is strictly simpler than the alternative and gives this one condition
            // its own honest page rather than a paragraph bolted onto 1902's.
            //
            // Not retryable: the code argument does not become catalogued between two identical
            // calls.
            "explain_diagnostic was asked to explain a VFX-*-#### code this server has no catalogue entry for."),
    ];

    private static readonly Dictionary<string, VfxCodeEntry> ByCode =
        All.ToDictionary(entry => entry.Code, StringComparer.Ordinal);

    /// <summary>
    /// Builds this code's catalogue URL — <see cref="DocsUrlPrefix"/> plus the code plus
    /// <see cref="DocsUrlSuffix"/>, byte-exact against the real published page (see
    /// <see cref="DocsUrlPrefix"/>'s remarks for how that was measured).
    /// </summary>
    public static string DocsUrlFor(string code) => DocsUrlPrefix + code + DocsUrlSuffix;

    /// <summary>
    /// Looks up a catalogued code without throwing when it is absent — for the one caller whose
    /// input does not originate inside this assembly.
    /// </summary>
    /// <remarks>
    /// <see cref="Get"/> throws on a miss because every ordinary call site names one of this type's
    /// own constants, making a miss a programming error. That reasoning does NOT hold across a
    /// process boundary: <c>ValidationOutcomeRenderer</c> classifies codes that arrived as text on
    /// the validation worker's stdout, so a malformed or unexpected value there is untrusted INPUT,
    /// not a bug in this assembly — and <c>validate_suite</c>'s "never throws" contract has to hold
    /// regardless of what the child process said. That caller uses this method; everything else
    /// should keep using <see cref="Get"/> and its fail-fast behaviour.
    /// </remarks>
    public static bool TryGet(string? code, out VfxCodeEntry entry)
    {
        if (code is null)
        {
            entry = null!;
            return false;
        }

        return ByCode.TryGetValue(code, out entry!);
    }

    /// <summary>Looks up a catalogued code.</summary>
    /// <exception cref="ArgumentException"><paramref name="code"/> has no catalogue entry.</exception>
    public static VfxCodeEntry Get(string code) =>
        ByCode.TryGetValue(code, out var entry)
            ? entry
            // A miss is always a programming error inside this assembly (every call site names one
            // of this type's own constants), never something caller input can provoke — so it
            // throws rather than degrading to an uncatalogued code that would slip past US-S1-06's
            // completeness gate.
            : throw new ArgumentException(
                $"Code '{VfxCode.SanitiseForEcho(code)}' has no entry in VfxCodeCatalogue.All. "
                + "Every emitted code must be catalogued — add an entry rather than emitting an unknown code.",
                nameof(code));

    /// <summary>
    /// Builds a <see cref="VfxError"/> for <paramref name="code"/>, taking <c>retryable</c> and
    /// <c>docsUrl</c> from this catalogue so a call site cannot state either of them differently
    /// from the table.
    /// </summary>
    /// <param name="code">One of this type's <c>VFX-E-####</c> constants.</param>
    /// <param name="message">
    /// The human-readable, one-line explanation. Callers pass their EXISTING message text unchanged:
    /// US-S1-04 migrates the code, never the wording (rewording is Sprint 2's semantic-rules work).
    /// </param>
    /// <param name="details">
    /// Optional structured extras. Subject to <see cref="VfxError.Details"/>'s normative
    /// secret-hygiene constraint — read its remarks before populating this.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is uncatalogued, or is catalogued as a <see cref="VfxCodeKind.Diagnostic"/>
    /// — a diagnostic is data on a successful call and must never be rendered as a tool error.
    /// </exception>
    public static VfxError CreateError(string code, string message, JsonElement? details = null)
    {
        var entry = Get(code);

        if (entry.Kind != VfxCodeKind.Error)
        {
            throw new ArgumentException(
                $"Code '{code}' is catalogued as a {entry.Kind} and cannot be returned as a tool error. "
                + "Diagnostics are data on a successful call (spec §4.4).",
                nameof(code));
        }

        return new VfxError(code, message, details, entry.DocsUrl, entry.Retryable);
    }

    /// <summary>
    /// Builds a <see cref="Diagnostic"/> for <paramref name="code"/>, taking <c>docsUrl</c> from
    /// this catalogue for the same non-drift reason <see cref="CreateError"/> does.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is uncatalogued, or is catalogued as a <see cref="VfxCodeKind.Error"/>.
    /// </exception>
    public static Diagnostic CreateDiagnostic(
        string code,
        string severity,
        string message,
        DiagnosticLocation? location = null,
        string? path = null,
        DiagnosticFix? fix = null)
    {
        var entry = Get(code);

        if (entry.Kind != VfxCodeKind.Diagnostic)
        {
            throw new ArgumentException(
                $"Code '{code}' is catalogued as a {entry.Kind} and cannot be returned as a diagnostic.",
                nameof(code));
        }

        return new Diagnostic(code, severity, message, location, path, fix, entry.DocsUrl);
    }
}
