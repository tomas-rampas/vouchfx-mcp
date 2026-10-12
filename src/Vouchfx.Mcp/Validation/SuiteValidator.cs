using System.Text.Json;
using Json.Schema;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Normalization;
using Vouchfx.Mcp.Validation.Semantics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Validates a <c>.e2e.yaml</c> suite against the embedded composed JSON Schema — REQ-003's
/// validate_suite logic, and EDGE-003's validate path.
/// </summary>
/// <remarks>
/// <para>
/// Converts YAML to JSON via <see cref="YamlToJsonConverter"/>, evaluates the embedded schema
/// with <see cref="OutputFormat.List"/>, and delegates collection, noise suppression, formatting
/// and the unknown-step-type merge to <see cref="SchemaErrorCollector"/>. The collector mirrors
/// the engine's schema-error pipeline while preserving this server's sanitised diagnostics.
/// </para>
/// <para>
/// Every input runs through <see cref="PathSafetyGuard"/> and <see cref="YamlSafetyGuard"/>
/// first (see <see cref="ValidateFile"/> and <see cref="ValidateYaml"/>). Unlike the engine,
/// this server validates a path supplied by the caller of <c>validate_suite</c>: untrusted input
/// must not crash the server or make it access the network. See those guards' threat models.
/// </para>
/// </remarks>
public static class SuiteValidator
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// Reads and validates the <c>.e2e.yaml</c> file at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws, with one deliberate exception: a semantic rule that violates the no-secret-echo
    /// contract fails the call at <see cref="Semantics.SemanticAnalyser.Analyse(SemanticAnalysisContext)"/>, and the
    /// <c>--validate-worker</c> boundary converts that crash into <c>VFX-E-1901</c>. (Unreachable
    /// from here today — this overload narrows to <see cref="ValidationLevel.Schema"/>, where the
    /// semantic pass does not run — but stated so the contract reads identically at all four entry
    /// points, and stays true if that narrowing ever changes.)
    /// </para>
    /// <para>
    /// In order: a network/UNC path is rejected as <c>invalid-path</c> (M2) before
    /// any filesystem call is made against it; a missing file is <c>file-not-found</c>; an
    /// oversized file is rejected as <c>too-large</c> by its length ALONE, before its content is
    /// ever read into memory (B1); any other file-access failure (permissions, a locked file, a
    /// path that is too long, …) is <c>file-access-error</c> (N1). Every message that could carry
    /// caller-supplied text (the path itself, or a BCL exception's message) is sanitised via
    /// <see cref="TextSanitiser"/> (M1) before it reaches the result.
    /// </para>
    /// </remarks>
    public static ValidateSuiteResult ValidateFile(string path) =>
        AnalyseFile(path, ValidationLevel.Schema).AsValidationResult();

    /// <summary>
    /// Reads the <c>.e2e.yaml</c> file at <paramref name="path"/> and runs the passes
    /// <paramref name="level"/> selects, returning the schema verdict, the semantic findings, and
    /// the document's summary together (US-S2-02).
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="ValidateFile"/> in every safety respect — same fast rejects, same
    /// guards, same never-throws contract, including its one deliberate exception: at
    /// <see cref="ValidationLevel.Semantic"/> or <see cref="ValidationLevel.Full"/>, a semantic rule
    /// that violates the no-secret-echo contract fails the call at
    /// <see cref="Semantics.SemanticAnalyser.Analyse(SemanticAnalysisContext)"/>, and the <c>--validate-worker</c> boundary
    /// converts that crash into <c>VFX-E-1901</c>. <see cref="ValidateFile"/> is now simply this
    /// method narrowed to the schema pass.
    /// </remarks>
    public static SuiteAnalysis AnalyseFile(string path, ValidationLevel level) =>
        TryReadSuiteText(path, level, out var yamlText, out var readFailure)
            // The path is what a semantic finding's location names, and it is the one identity this
            // pipeline has that a host can act on. AnalyseYaml sanitises it; it is deliberately NOT
            // resolved to an absolute path first — the finding should echo what the caller asked about.
            ? AnalyseYaml(yamlText!, level, path)
            : readFailure!;

    /// <summary>
    /// The fast rejects plus the file read, shared by <see cref="AnalyseFile"/> and
    /// <see cref="NormaliseFile"/> (US-S2-04) so the two entry points cannot drift on which
    /// conditions refuse a file or on what they report when one does.
    /// </summary>
    /// <param name="path">The suite file to read.</param>
    /// <param name="level">The level to echo back on a failure analysis; see <see cref="SuiteAnalysis.FromValidation"/>.</param>
    /// <param name="yamlText">The file's text; meaningful only when this returns <see langword="true"/>.</param>
    /// <param name="failure">The refusal as a finished analysis, or <see langword="null"/> on success.</param>
    private static bool TryReadSuiteText(
        string path, ValidationLevel level, out string? yamlText, out SuiteAnalysis? failure)
    {
        yamlText = null;

        // An EXPLICIT null workspace — containment off, in writing: this overload only ever runs INSIDE the
        // --validate-worker child process, which is handed a path the parent has already contained
        // (ValidationWorkerClient runs the identical check with the workspace before it spawns
        // anything). The worker is not given the workspace at all, so re-deriving containment here
        // would mean inventing a second, weaker answer to a question already settled.
        var fastRejectError = CheckFastRejects(path, workspace: null);
        if (fastRejectError is not null)
        {
            failure = SuiteAnalysis.FromValidation(Invalid(fastRejectError), level);
            return false;
        }

        try
        {
            yamlText = File.ReadAllText(path);
        }
        catch (Exception ex) when (IsExpectedFileAccessException(ex))
        {
            failure = SuiteAnalysis.FromValidation(Invalid(BuildFileAccessError(path, ex)), level);
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>
    /// <see cref="AnalyseFile"/> plus the suite's canonical text — <c>normalize_suite</c>'s entry
    /// point for a file source (US-S2-04).
    /// </summary>
    public static SuiteNormalization NormaliseFile(string path, ValidationLevel level) =>
        TryReadSuiteText(path, level, out var yamlText, out var readFailure)
            ? NormaliseYaml(yamlText!, level, path)
            : SuiteNormalization.WithoutCanonicalYaml(readFailure!);

    /// <summary>
    /// <see cref="AnalyseYaml"/> plus the suite's canonical text — <c>normalize_suite</c>'s entry
    /// point for an inline source (US-S2-04).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wrapper, never a fork.</b> The verdict is whatever <see cref="AnalyseYaml"/> produced,
    /// field for field — asking for canonical text must not be able to perturb a single thing
    /// <c>validate_suite</c> would have said about the same suite, which is what
    /// <c>SuiteNormalisationPipelineTests</c> pins directly.
    /// </para>
    /// <para>
    /// <b><see cref="SuiteAnalysis.Summary"/> is a NECESSARY but not sufficient gate on whether a
    /// canonical form exists</b>, and both halves of that matter. It is necessary and cheap: the
    /// property is <see langword="null"/> precisely when no document was ever built (a guard
    /// rejection, a parse failure, an empty document), so checking it here means this method never
    /// asks the normalizer to re-parse text the pipeline has already established cannot be parsed. It
    /// is not sufficient: a document whose ROOT is not a mapping — a sequence, a bare scalar — has a
    /// summary and is still not a suite, and <see cref="SuiteNormalizer"/> refuses it on its own
    /// <c>as YamlMappingNode</c> cast. A canonical form is therefore absent for either reason, and
    /// both mechanisms are load-bearing; neither may be removed on the grounds that the other exists.
    /// </para>
    /// <para>
    /// <b>The gate also fixes an ORDERING obligation.</b> Building the summary converts the document
    /// to JSON, which is what rejects a self-referential (anchor-cycle) graph — measured: VFX-D-1102,
    /// "too much recursion when traversing the object graph", summary <see langword="null"/>. So a
    /// cyclic graph can never reach the normalizer's emit-and-re-parse gate at all. Pinned by
    /// <c>SuiteNormalisationPipelineTests</c>.
    /// </para>
    /// <para>
    /// Never throws, on exactly the terms <see cref="AnalyseYaml"/> does not — see its remarks.
    /// <see cref="SuiteNormalizer.NormaliseText"/> inherits that contract too, including for the
    /// emission gate: a refusal is a return value, never an exception.
    /// </para>
    /// </remarks>
    public static SuiteNormalization NormaliseYaml(
        string yamlText, ValidationLevel level, string? sourceName = null)
    {
        var analysis = AnalyseYaml(yamlText, level, sourceName);

        if (analysis.Summary is null)
        {
            return SuiteNormalization.WithoutCanonicalYaml(analysis);
        }

        var canonicalYaml = SuiteNormalizer.NormaliseText(yamlText, out var refusedReason);

        if (refusedReason is not null)
        {
            return SuiteNormalization.RefusedCanonicalYaml(analysis, refusedReason);
        }

        return new SuiteNormalization(canonicalYaml, analysis)
        {
            // Issue #85: the flag reports what THIS document lost, so the input is measured rather
            // than inferred from the existence of canonical text. Asked only when there IS canonical
            // text — short-circuiting in that order is not just an optimisation: with none, the
            // property's own AND reads false regardless, so a scan there could only spend the
            // measured worst case (~77 ms at the 2 MB cap, on one developer host) to reach a
            // foregone conclusion.
            CommentsDropped = canonicalYaml is not null && SuiteNormalizer.ContainsComment(yamlText),
        };
    }

    /// <summary>
    /// Runs the fast, bounded pre-checks that can never hang or crash: <see cref="PathSafetyGuard"/>'s
    /// UNC/network-path rejection (M2) plus — when <paramref name="workspace"/> is non-null —
    /// US-S3-08's containment check, then file existence/readability, then size against
    /// <see cref="YamlSafetyGuard.MaxSuiteSizeBytes"/> (B1) — all without ever handing untrusted
    /// YAML text to YamlDotNet. Returns <see langword="null"/> when <paramref name="path"/> passes
    /// every one of them and is safe to read and validate.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="ValidateFile"/> (which calls this first, then proceeds
    /// to read and validate) so <c>ValidationWorkerClient</c> — the <c>validate_suite</c>
    /// orchestrator that spawns the <c>--validate-worker</c> child process wrapping this whole
    /// pipeline — can run exactly these checks itself, in-process, before deciding whether a
    /// child process is even needed. A missing file or a UNC path needs no worker at all: neither
    /// check ever hands untrusted YAML text to YamlDotNet, so neither can hang. Only a present,
    /// local, size-bounded file's actual content reaches the child.
    /// </remarks>
    /// <param name="path">The suite file to check.</param>
    /// <param name="workspace">
    /// The workspace resolved at server start, or an explicit <see langword="null"/> for the
    /// containment-off mode. <b>Required, with no default</b> (a peer review's MAJOR finding) — see
    /// <see cref="PathSafetyGuard.CheckLocalPath"/>'s own parameter documentation for why a security
    /// parameter must not have "off" as the value a forgetful call site gets.
    /// </param>
    public static SuiteValidationError? CheckFastRejects(string path, Workspace? workspace)
    {
        var pathError = PathSafetyGuard.CheckLocalPath(path, workspace);
        if (pathError is not null)
        {
            return pathError;
        }

        long fileLength;
        try
        {
            fileLength = new FileInfo(path).Length;
        }
        catch (Exception ex) when (IsExpectedFileAccessException(ex))
        {
            return BuildFileAccessError(path, ex);
        }

        if (fileLength > YamlSafetyGuard.MaxSuiteSizeBytes)
        {
            return new SuiteValidationError(
                VfxCodeCatalogue.SuiteFileTooLarge,
                null,
                $"File is {fileLength:N0} bytes, which exceeds the {YamlSafetyGuard.MaxSuiteSizeBytes:N0}-byte " +
                "limit. Not read.",
                null,
                null);
        }

        return null;
    }

    /// <summary>
    /// Validates raw <c>.e2e.yaml</c> text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws, with one deliberate exception: a semantic rule that violates the no-secret-echo
    /// contract fails the call at <see cref="Semantics.SemanticAnalyser.Analyse(SemanticAnalysisContext)"/>, and the
    /// <c>--validate-worker</c> boundary converts that crash into <c>VFX-E-1901</c>. (Unreachable
    /// from here today — this overload narrows to <see cref="ValidationLevel.Schema"/>, where the
    /// semantic pass does not run — but stated so the contract reads identically at all four entry
    /// points, and stays true if that narrowing ever changes.)
    /// </para>
    /// <para>
    /// <see cref="YamlSafetyGuard"/> runs FIRST, before any YamlDotNet call — see
    /// its remarks for why that ordering is not optional (B1). Unparseable YAML is reported as a
    /// <c>yaml-parse</c> error with line/column where the parser derives them (EDGE-003b); schema
    /// violations and unknown step types are reported as one or more entries in the result's
    /// <c>Errors</c> (EDGE-003c).
    /// </para>
    /// </remarks>
    public static ValidateSuiteResult ValidateYaml(string yamlText) =>
        AnalyseYaml(yamlText, ValidationLevel.Schema).AsValidationResult();

    /// <summary>
    /// Runs the passes <paramref name="level"/> selects over raw <c>.e2e.yaml</c> text, returning
    /// the schema verdict, the semantic findings, and the document's summary together (US-S2-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws, exactly as <see cref="ValidateYaml"/> does not — which is now simply this
    /// method narrowed to <see cref="ValidationLevel.Schema"/> — with the one deliberate exception
    /// this overload can actually reach: at <see cref="ValidationLevel.Semantic"/> or
    /// <see cref="ValidationLevel.Full"/>, a semantic rule that violates the no-secret-echo contract
    /// fails the call at <see cref="Semantics.SemanticAnalyser.Analyse(SemanticAnalysisContext)"/> rather than publishing the
    /// finding, and the <c>--validate-worker</c> boundary converts that crash into
    /// <c>VFX-E-1901</c>. That is a server-bug path by construction (see that guard's remarks), not
    /// an input the caller can provoke.
    /// </para>
    /// <para>
    /// <b><paramref name="level"/> gates the PASSES, never the guards.</b> Everything up to and
    /// including the YAML→JSON conversion below runs identically for all three levels: those steps
    /// are the shared input both passes consume, and <see cref="YamlSafetyGuard"/> in particular is
    /// a safety property that a caller must not be able to switch off by naming a level. See
    /// <see cref="ValidationLevel"/>'s own remarks.
    /// </para>
    /// </remarks>
    /// <param name="yamlText">The suite's YAML text.</param>
    /// <param name="level">Which passes to run.</param>
    /// <param name="sourceName">
    /// What the suite is CALLED, for a semantic finding's <see cref="DiagnosticLocation.File"/> —
    /// the caller's own path, or <see langword="null"/> for inline text, which reports
    /// <see cref="SuiteSource.InlineSourceName"/>. Sanitised here, at the boundary, rather than at
    /// each consumer: it is caller-supplied and reaches a tool result.
    /// </param>
    public static SuiteAnalysis AnalyseYaml(
        string yamlText, ValidationLevel level, string? sourceName = null)
    {
        // MUST run before any YamlDotNet call (see YamlSafetyGuard's remarks for the full threat
        // model): a native StackOverflowException from deeply nested input cannot be caught by
        // any try/catch, so the only fix is to never let YamlDotNet see text shaped like that.
        var safetyError = YamlSafetyGuard.Check(yamlText);
        if (safetyError is not null)
        {
            return SuiteAnalysis.FromValidation(Invalid(safetyError), level);
        }

        JsonDocument document;
        try
        {
            document = YamlToJsonConverter.Convert(yamlText);
        }
        catch (YamlException ex)
        {
            return SuiteAnalysis.FromValidation(Invalid(new SuiteValidationError(
                VfxCodeCatalogue.YamlParseError, null, TextSanitiser.SanitiseForDisplay(ex.Message), ex.Start.Line, ex.Start.Column)), level);
        }
        catch (InvalidOperationException ex)
        {
            // The YAML is syntactically empty (YamlToJsonConverter.Convert's own guard) — not a
            // YamlException, but the same "cannot proceed past parsing" family of problem.
            return SuiteAnalysis.FromValidation(Invalid(new SuiteValidationError(
                VfxCodeCatalogue.YamlParseError, null, TextSanitiser.SanitiseForDisplay(ex.Message), null, null)), level);
        }
        catch (JsonException ex)
        {
            // A raw control character embedded in a quoted YAML scalar (built numerically in
            // tests, never as a literal) can round-trip through YamlDotNet's
            // SerializerBuilder().JsonCompatible() re-emission as an UNESCAPED control byte
            // inside the JSON text it produces — invalid JSON that JsonDocument.Parse then
            // rejects. Caught here so a hostile value like that is reported as a structured
            // yaml-parse error instead of escaping this method's "never throws" contract.
            return SuiteAnalysis.FromValidation(Invalid(new SuiteValidationError(
                VfxCodeCatalogue.YamlParseError, null, TextSanitiser.SanitiseForDisplay(ex.Message), null, null)), level);
        }

        using (document)
        {
            // Parsed ONCE for the whole document and shared by BOTH error sources — and, since
            // US-S2-02, by the summary and the semantic pass as well. See YamlLineResolver's
            // overload remarks: with the step surface closed at rc.4 the error count tracks the
            // document's key count, and a re-parse per error is quadratic. An earlier revision
            // hoisted this for the schema path only and left the unknown-type cross-check
            // re-parsing — measured at 31.9s for a 2 000-unknown-type suite against the validation
            // worker's 10-second budget. That measurement is why the semantic seam takes the parsed
            // document (see Semantics/SemanticAnalysis.cs's header) rather than the raw text.
            var yamlRoot = YamlLineResolver.TryParseYamlRoot(yamlText);

            // Derived from the SAME JsonDocument the schema pass evaluates, before either pass runs,
            // so it is available regardless of which of them level selected — and so a rule can read
            // it instead of re-walking the document for the same facts. One call yields both halves:
            // the caller-facing summary and the internal, uncapped/unfiltered fact set the semantic
            // rules decide set membership against (see SuiteFacts).
            //
            // Built unconditionally even though ONE caller throws it away: run_suite's EDGE-003
            // pre-flight goes through ValidateFile/ValidateYaml, which narrow this analysis via
            // AsValidationResult() and so drop the summary (and the semantic channel) entirely. That
            // waste is deliberately not optimised away with a "does the caller want it?" flag. The
            // full cost on that path is the walk PLUS the wire trip it feeds: one linear walk of the
            // already-parsed document (O(total string bytes), MaxEntriesPerList-bounded output),
            // then — because run_suite reaches this code inside the spawned --validate-worker —
            // worker-side JSON serialisation of up to five 1 000-entry lists, the pipe write and
            // read, and the parent's deserialisation of them, all so ValidateSuiteResult can discard
            // the result. That is the same order as the YAML→JSON conversion that just ran, and
            // nowhere near the 10-second worker budget the measured hazards in this file are all
            // about; the fact set adds nothing to it at all, since it never leaves this process. A
            // conditional would buy nothing measurable and would add a way for the summary to be
            // silently absent on a path that expects it.
            var digest = SuiteSummaryBuilder.Build(document.RootElement);

            var errors = level is ValidationLevel.Schema or ValidationLevel.Full
                ? RunSchemaPass(document.RootElement, yamlRoot)
                : [];

            var semantic = level is ValidationLevel.Semantic or ValidationLevel.Full
                ? SemanticAnalyser.Analyse(new SemanticAnalysisContext(
                    document.RootElement,
                    yamlRoot,
                    digest.Summary,
                    digest.Facts,
                    sourceName is null
                        ? SuiteSource.InlineSourceName
                        : TextSanitiser.SanitiseForDisplay(sourceName)))
                : SemanticAnalysisOutcome.Empty;

            var semanticDiagnostics = semantic.Findings;

            // ── THE VERDICT, and the one reconciliation US-S2-03 had to make ────────────────────
            //
            // v1's rule was `errors.Count == 0` and its rationale was categorical: `valid` answers
            // "will the engine accept this suite?", so this server's own advice must never flip it.
            // US-S2-03's spec has a Gherkin scenario that says otherwise, in as many words — "Given
            // a suite step embedding a connection string containing a literal password / Then
            // semanticDiagnostics contains an entry with code VFX-D-1207, severity error / And ok is
            // false" (`ok` being the spec's spelling of the shipped `valid`, per US-S2-02's
            // field-name note). Both cannot hold. The spec wins, and the line is drawn at SEVERITY
            // rather than at channel:
            //
            //   * a semantic finding of severity "error" makes the suite invalid;
            //   * "warning" and "info" findings never do.
            //
            // A semantic ERROR makes the document unfit as written: VFX-D-1207 detects a secret
            // literal and VFX-D-1213 detects an environment key owned by a managed provider.
            // Warnings and info remain advisory. The verdict includes findings omitted by the
            // publication cap; a large warning list must not hide a later error.
            //
            // What this does NOT change, and the containment is what makes it safe:
            //   * The ARRAYS still never merge. A semantic finding is in semanticDiagnostics and
            //     nowhere else; `errors` is untouched, so US-S2-06's corpus agreement oracle and
            //     every host filtering one channel from the other see exactly what they saw.
            //   * run_suite's EDGE-003 pre-flight cannot be affected. It reaches this method through
            //     ValidateFile/ValidateYaml, which run at ValidationLevel.Schema — where the
            //     semantic pass does not run at all and this clause is vacuous.
            //   * AsValidationResult() can now narrow to {valid:false, errors:[]}, which reads oddly
            //     in isolation, and IS reachable: three call sites narrow, and two of them
            //     (ValidateSuiteTool, and — since US-S2-04 — NormalizeSuiteTool, which always runs at
            //     Full) can be looking at a secret-literal verdict when they do. It is left as-is
            //     rather than papered over because the safety property does not depend on the flag:
            //     every narrowing feeds ValidationOutcomeRenderer.TryRenderCallFailure, which decides
            //     purely on whether `errors` carries a call-failure CODE. An empty `errors` array
            //     therefore yields "no call failure" — the outcome those tools want — whatever
            //     `valid` says. The narrowing's job is to drop the semantic channel, not to restate
            //     its verdict, and no caller reads `valid` off it.
            var semanticallyInvalid = semantic.HasErrors;

            return new SuiteAnalysis(
                errors.Count == 0 && !semanticallyInvalid,
                errors,
                semanticDiagnostics,
                semantic.Truncated,
                digest.Summary,
                level);
        }
    }

    /// <summary>
    /// The JSON Schema pass: schema evaluation plus the unknown-step-type cross-check, with the
    /// measured noise suppression applied — <c>validate_suite</c> v1's entire behaviour, lifted out
    /// of <see cref="AnalyseYaml"/> unchanged so <see cref="ValidationLevel"/> can skip it without
    /// disturbing any of it.
    /// </summary>
    private static List<SuiteValidationError> RunSchemaPass(
        JsonElement root, YamlMappingNode? yamlRoot)
    {
        var results = Schema.Evaluate(root, Options);
        return SchemaErrorCollector.Collect(results, root, yamlRoot);
    }

    private static ValidateSuiteResult Invalid(SuiteValidationError error) =>
        new(false, [error]);

    /// <summary>
    /// Exception types <see cref="ValidateFile"/> treats as an expected, structured "could not
    /// access this file" outcome rather than letting them propagate (N1). Deliberately broader
    /// than just "the file doesn't exist": a caller-supplied path can just as easily name a file
    /// this process has no permission to read, one that is locked by another process, or one
    /// whose length exceeds the filesystem's own limits.
    /// </summary>
    private static bool IsExpectedFileAccessException(Exception ex) =>
        ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or System.Security.SecurityException
            or ArgumentException;

    /// <summary>
    /// Builds a <c>file-not-found</c> or <c>file-access-error</c> result for a failed file-system
    /// call against <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Follows the same policy as <c>PinFailureReporting.DescribeLoadFailure</c>, which this is
    /// modelled on: the file-not-found case echoes the sanitised PATH (the useful,
    /// already-caller-supplied datum), but for every other, generic file-access failure, the
    /// BCL exception's own <see cref="Exception.Message"/> is never forwarded at all — it cannot
    /// be trusted to be path-free (<see cref="UnauthorizedAccessException"/>,
    /// <see cref="IOException"/>, and friends routinely embed a full path in their message,
    /// which could differ from — or add detail beyond — the one already-sanitised path this
    /// method reports). Instead, a path-free, exception-type-named summary is built, e.g.
    /// <c>"The suite file could not be read (UnauthorizedAccessException)."</c>
    /// </remarks>
    private static SuiteValidationError BuildFileAccessError(string path, Exception ex)
    {
        var sanitisedPath = TextSanitiser.SanitiseForDisplay(path);

        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SuiteValidationError(VfxCodeCatalogue.SuiteFileNotFound, null, $"File not found: '{sanitisedPath}'.", null, null);
        }

        return new SuiteValidationError(
            VfxCodeCatalogue.SuiteFileUnreadable,
            null,
            $"The suite file could not be read ({ex.GetType().Name}).",
            null,
            null);
    }

    private static JsonSchema LoadSchema()
    {
        const string resourceName = "Vouchfx.Mcp.Vendored.composed-schema.v1.json";
        var assembly = typeof(SuiteValidator).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' was not found in '{assembly.FullName}'.");
        using var reader = new StreamReader(stream);

        return JsonSchema.FromText(reader.ReadToEnd());
    }

}
