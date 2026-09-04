using System.Globalization;
using System.Text.Json;
using Json.Schema;
using Vouchfx.Mcp.Contracts;
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
/// <b>Architecture mirrors the vouchfx engine's own validation pipeline</b> (see
/// <c>Vouchfx.Engine.Compilation.Schema</c>'s <c>SchemaComposer</c>/<c>YamlSchemaValidator</c>/
/// <c>DocumentValidator</c>): convert YAML to JSON via <see cref="YamlToJsonConverter"/>,
/// evaluate with <see cref="OutputFormat.List"/>, then resolve each error's instance location
/// back to a source line via <see cref="YamlLineResolver"/>. Three things are deliberately
/// different from the engine's own (unfiltered, untrusted-input-agnostic) pipeline:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>"if" discriminator noise is filtered out.</b> The composed schema's <c>$defs.step.allOf</c>
/// is 25 unconditional <c>if</c>/<c>then</c> clauses, one per registered type. Evaluating with
/// <see cref="OutputFormat.List"/> reports EVERY clause's own <c>if</c> evaluation as an
/// "invalid" node with a populated <c>Errors</c> dictionary whenever a step's type doesn't match
/// that clause's <c>const</c> — i.e. up to 24 spurious "Expected &lt;other type&gt;" entries per
/// invalid step, none of which describe a real document problem.
/// <see cref="IsIfDiscriminatorNoise"/> recognises and drops these; only a clause's <c>then</c>
/// branch (reached once its <c>if</c> genuinely matched) can produce a real error here.
/// </description></item>
/// <item><description>
/// <b>Roll-up/aggregate noise is filtered out.</b> JsonSchema.Net 9.3.0 (see the fleet's
/// <c>Vouchfx.Mcp.csproj</c> package-reference remarks) started attaching a generic message to
/// EVERY composite/applicator keyword that fails only because one of its own subschemas failed —
/// e.g. <c>properties</c> ("Some properties did not match the required schema"), <c>items</c>,
/// <c>allOf</c>, and the <c>if</c>/<c>then</c> pairing's own combined result all now carry a
/// message that adds no information beyond "something underneath me failed". Previously (9.2.1)
/// only the genuinely-failing leaf keyword (e.g. <c>required</c>) carried a message at all.
/// <see cref="CollectSchemaErrors"/> drops any node for which a MORE SPECIFIC node also failed —
/// i.e. another invalid node whose <c>evaluationPath</c> nests strictly deeper AND whose
/// <c>instanceLocation</c> is the same or nests deeper too (both checked, since sibling array
/// items such as two different steps repeat the SAME schema <c>evaluationPath</c> at DIFFERENT
/// <c>instanceLocation</c>s, and matching one without the other would misattribute failures
/// across them). <b>Deliberately does not use <see cref="EvaluationResults.Parent"/></b>: it
/// looks like the real evaluation tree, but empirically (see the PR this shipped in) the
/// outermost node's own <c>properties</c>-keyword roll-up and its per-member child (e.g.
/// <c>/properties/steps</c>) are SIBLINGS under the true root, not parent/child, so a
/// Parent-based "has a failing child" check misses exactly this top-level case.
/// </description></item>
/// <item><description>
/// <b>Unknown step types are cross-checked separately</b> against <see cref="StepTypeCatalogue"/>
/// (<c>unknown-step-type</c>), because the schema's if/then-with-no-else structure lets an
/// unregistered type pass raw evaluation with zero errors.
/// </description></item>
/// <item><description>
/// <b>The <c>unevaluatedProperties</c> cascade is suppressed, and its message made actionable.</b>
/// From engine <c>v1.0.0-rc.4</c> the composed schema closes <c>$defs/step</c> with
/// <c>"unevaluatedProperties": false</c> (it was <c>"additionalProperties": true</c> — an open
/// surface — up to and including <c>v1.0.0-rc.3</c>), which is what finally makes a typo'd step
/// field an error at all. It also brings a well-known false-positive shape with it: a property is
/// only ever "evaluated" by whichever <c>if</c>/<c>then</c> clause matched the step's <c>type</c>,
/// and a subschema that FAILS withholds its <c>properties</c> annotations, so the moment a step
/// has any other defect (a missing <c>required</c> field, an unregistered <c>type</c>) every one
/// of its perfectly legitimate fields is also reported as unevaluated. Measured against
/// <c>v1.0.0-rc.4</c>: a single <c>http.rest</c> step missing <c>target</c> yielded FOUR errors
/// here versus the engine CLI's ONE, three of them naming valid fields.
/// <see cref="SuppressUnevaluatedPropertiesCascade"/> drops a step's unevaluated entries whenever
/// that same step already carries an error of any other kind (schema or <c>unknown-step-type</c>),
/// and <see cref="FormatUnevaluatedPropertiesError"/> replaces JsonSchema.Net's opaque
/// blank-keyword text ("All values fail against the false schema") with the offending property's
/// own name and its step type. Both mirror the engine's <c>SchemaErrorCollector</c> /
/// <c>DocumentValidator</c> at the pinned commit — deliberately, since a suite that
/// <c>vouchfx validate</c> accepts must never be rejected by <c>validate_suite</c>, nor be
/// rejected here for different reasons.
/// </description></item>
/// <item><description>
/// <b>Every input runs through <see cref="PathSafetyGuard"/> and <see cref="YamlSafetyGuard"/>
/// first</b> (see <see cref="ValidateFile"/> and <see cref="ValidateYaml"/>). Unlike the engine
/// (which only ever validates a suite the operator themselves committed to source control), this
/// server validates a <c>path</c> supplied by whoever is calling the <c>validate_suite</c> MCP
/// tool — untrusted input that must never be able to crash the server or make it reach out over
/// the network. See those two types' remarks for the specific threats.
/// </description></item>
/// </list>
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
    /// Never throws. In order: a network/UNC path is rejected as <c>invalid-path</c> (M2) before
    /// any filesystem call is made against it; a missing file is <c>file-not-found</c>; an
    /// oversized file is rejected as <c>too-large</c> by its length ALONE, before its content is
    /// ever read into memory (B1); any other file-access failure (permissions, a locked file, a
    /// path that is too long, …) is <c>file-access-error</c> (N1). Every message that could carry
    /// caller-supplied text (the path itself, or a BCL exception's message) is sanitised via
    /// <see cref="TextSanitiser"/> (M1) before it reaches the result.
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
    /// guards, same never-throws contract; <see cref="ValidateFile"/> is now simply this method
    /// narrowed to the schema pass.
    /// </remarks>
    public static SuiteAnalysis AnalyseFile(string path, ValidationLevel level)
    {
        var fastRejectError = CheckFastRejects(path);
        if (fastRejectError is not null)
        {
            return SuiteAnalysis.FromValidation(Invalid(fastRejectError), level);
        }

        string yamlText;
        try
        {
            yamlText = File.ReadAllText(path);
        }
        catch (Exception ex) when (IsExpectedFileAccessException(ex))
        {
            return SuiteAnalysis.FromValidation(Invalid(BuildFileAccessError(path, ex)), level);
        }

        return AnalyseYaml(yamlText, level);
    }

    /// <summary>
    /// Runs the fast, bounded pre-checks that can never hang or crash: <see cref="PathSafetyGuard"/>'s
    /// UNC/network-path rejection (M2), then file existence/readability, then size against
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
    public static SuiteValidationError? CheckFastRejects(string path)
    {
        var pathError = PathSafetyGuard.CheckLocalPath(path);
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
    /// Never throws. <see cref="YamlSafetyGuard"/> runs FIRST, before any YamlDotNet call — see
    /// its remarks for why that ordering is not optional (B1). Unparseable YAML is reported as a
    /// <c>yaml-parse</c> error with line/column where the parser derives them (EDGE-003b); schema
    /// violations and unknown step types are reported as one or more entries in the result's
    /// <c>Errors</c> (EDGE-003c).
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
    /// method narrowed to <see cref="ValidationLevel.Schema"/>.
    /// </para>
    /// <para>
    /// <b><paramref name="level"/> gates the PASSES, never the guards.</b> Everything up to and
    /// including the YAML→JSON conversion below runs identically for all three levels: those steps
    /// are the shared input both passes consume, and <see cref="YamlSafetyGuard"/> in particular is
    /// a safety property that a caller must not be able to switch off by naming a level. See
    /// <see cref="ValidationLevel"/>'s own remarks.
    /// </para>
    /// </remarks>
    public static SuiteAnalysis AnalyseYaml(string yamlText, ValidationLevel level)
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
            // it instead of re-walking the document for the same facts.
            //
            // Built unconditionally even though ONE caller throws it away: run_suite's EDGE-003
            // pre-flight goes through ValidateFile/ValidateYaml, which narrow this analysis via
            // AsValidationResult() and so drop the summary (and the semantic channel) entirely. That
            // waste is deliberately not optimised away with a "does the caller want it?" flag: the
            // cost is one linear walk of the already-parsed document, O(total string bytes) with a
            // MaxEntriesPerList-bounded output — the same order as the YAML→JSON conversion that
            // just ran, and nowhere near the 10-second worker budget the measured hazards in this
            // file are all about. A conditional would buy nothing measurable and would add a way for
            // the summary to be silently absent on a path that expects it.
            var summary = SuiteSummaryBuilder.Build(document.RootElement);

            var errors = level is ValidationLevel.Schema or ValidationLevel.Full
                ? RunSchemaPass(document.RootElement, yamlRoot)
                : [];

            var semanticDiagnostics = level is ValidationLevel.Semantic or ValidationLevel.Full
                ? SemanticAnalyser.Analyse(new SemanticAnalysisContext(document.RootElement, yamlRoot, summary))
                : [];

            // `Valid` reports the SCHEMA channel only, unchanged from v1: it is the answer to "will
            // the engine accept this suite?", and a semantic finding — this server's own advice
            // about a document the schema accepts — must never flip it. That is the same
            // separation SuiteAnalysis's two arrays exist to preserve.
            return new SuiteAnalysis(errors.Count == 0, errors, semanticDiagnostics, summary, level);
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
        var schemaErrors = new List<CollectedSchemaError>();

        var results = Schema.Evaluate(root, Options);
        if (!results.IsValid)
        {
            CollectSchemaErrors(results, yamlRoot, root, schemaErrors);
        }

        // Always cross-checked, independent of results.IsValid: a step whose type matches
        // none of the 25 known consts satisfies every allOf clause vacuously (see remarks
        // above), so the schema alone would report no error for it at all.
        var unknownTypeErrors = new List<SuiteValidationError>();
        AppendUnknownStepTypeErrors(root, yamlRoot, unknownTypeErrors);

        // Runs AFTER the unknown-type cross-check because an unregistered type is itself a
        // step-level defect that withholds every if/then annotation (the engine's
        // DocumentValidator scopes its own suppression by exactly the same fact) — so the
        // cascade cannot be judged from the schema errors alone.
        var afterConstDedup = SuppressRedundantConstWhenEnumPresent(schemaErrors);
        var afterForbiddenContainer = SuppressErrorsInsideForbiddenContainer(afterConstDedup);
        var survivingSchemaErrors =
            SuppressUnevaluatedPropertiesCascade(afterForbiddenContainer, unknownTypeErrors);

        // Schema errors first, unknown-type errors last — matching the engine's own ordering,
        // so a consumer that picks the first error for a given instance location keeps seeing
        // the schema violation there.
        var errors = new List<SuiteValidationError>(
            survivingSchemaErrors.Count + unknownTypeErrors.Count);
        errors.AddRange(survivingSchemaErrors);
        errors.AddRange(unknownTypeErrors);

        return errors;
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

    /// <summary>
    /// Walks every node <paramref name="root"/>'s evaluation produced and appends one
    /// <see cref="SuiteValidationError"/> per real (non-noise) failing keyword to
    /// <paramref name="sink"/>.
    /// </summary>
    /// <remarks>
    /// Two independent noise filters are applied — see this type's remarks for the full
    /// rationale of each:
    /// <list type="number">
    /// <item><description>
    /// <see cref="IsIfDiscriminatorNoise"/> — an "if" branch that didn't match this step's type,
    /// or anything nested under it.
    /// </description></item>
    /// <item><description>
    /// <see cref="HasMoreSpecificFailure"/> — a 9.3.0 aggregate/roll-up message whose real
    /// explanation is reported by a deeper, more specific node instead.
    /// </description></item>
    /// </list>
    /// Every node is visited via <see cref="FlattenResults"/> rather than acting purely
    /// top-down, because filter 2 needs the FULL node set gathered up front before it can decide
    /// whether any given node has a more specific failure elsewhere in the tree.
    /// </remarks>
    private static void CollectSchemaErrors(
        EvaluationResults root,
        YamlMappingNode? yamlRoot,
        JsonElement instance,
        List<CollectedSchemaError> sink)
    {
        var allNodes = new List<EvaluationResults>();
        FlattenResults(root, allNodes);

        // Every node's two pointers are stringified exactly ONCE here. JsonPointer.ToString()
        // allocates, and the roll-up check below compares every reportable node against every other
        // one — so calling it inside that loop made the cost quadratic in ALLOCATIONS, not just in
        // comparisons. Measured on a 1 000-step suite where each step carries a defect: 135s before
        // this projection, against the validation worker's 10-second budget.
        var projected = new List<(EvaluationResults Node, string EvalPath, string InstLoc)>(allNodes.Count);
        foreach (var node in allNodes)
        {
            projected.Add((node, node.EvaluationPath.ToString(), node.InstanceLocation.ToString()));
        }

        // Tallied over EVERY node, valid ones included, because a branch that satisfies its
        // composite is by definition itself valid — so the losing-branch filter cannot be built
        // from the failing nodes alone.
        var compositeGroups = FindCompositeGroups(projected);
        var satisfiedGroups = compositeGroups.Satisfied;

        // The candidate set for the roll-up check: only nodes this validator would actually report
        // may explain away another. Built once rather than re-filtered per comparison.
        //
        // This is a STRICT SUBSET of the old "any invalid node" candidate set, so it can only make
        // more errors survive, never fewer. Two independent narrowings, both deliberate:
        //   - !IsIfDiscriminatorNoise — a node dropped at emission time must not delete one that
        //     would have been shown (measured on rc.4's $defs/security).
        //   - Errors.Count > 0 — an invalid node carrying no message explains nothing, so it cannot
        //     stand in for the parent roll-up that IS the only signal the author would see.
        // Verified against the engine's rejected corpus: zero fixtures where this validator now
        // reports fewer findings than the CLI.
        //   - IsUnderAnySatisfiedCompositeGroup — same rationale as the if-discriminator exclusion,
        //     and it was missing: a losing branch of a SATISFIED composite is dropped moments later
        //     at emission, but while it sat in this list it could delete a sibling that survives.
        var candidates = new List<(string EvalPath, string InstLoc)>();
        foreach (var (node, evalPath, instLoc) in projected)
        {
            if (!node.IsValid &&
                node.Errors is { Count: > 0 } &&
                !IsIfDiscriminatorNoise(evalPath) &&
                !IsUnderAnySatisfiedCompositeGroup(evalPath, instLoc, satisfiedGroups))
            {
                candidates.Add((evalPath, instLoc));
            }
        }

        foreach (var (node, evaluationPath, instancePath) in projected)
        {
            if (node.IsValid || node.Errors is not { Count: > 0 })
            {
                continue;
            }

            if (IsIfDiscriminatorNoise(evaluationPath))
            {
                continue;
            }

            // MUST run before anything downstream treats this error as evidence of a real defect
            // — in particular before SuppressUnevaluatedPropertiesCascade counts it as a step's
            // "other error". A losing branch of a SATISFIED composite describes no document
            // problem at all, and letting one reach the cascade makes the cascade hide a genuine
            // unevaluatedProperties finding behind a phantom.
            if (IsUnderAnySatisfiedCompositeGroup(evaluationPath, instancePath, satisfiedGroups))
            {
                continue;
            }

            long? line = null;
            var lineResolved = false;

            foreach (var (keyword, message) in node.Errors)
            {
                // Roll-up suppression is decided per keyword AND scoped to that keyword's own
                // subschema. One evaluation node can carry both a genuine leaf assertion and an
                // aggregate roll-up whose only content is "something underneath me failed"
                // (measured on rc.4's $defs/security: the same node reports `required` AND an
                // `unevaluatedProperties` roll-up), so the decision cannot be made per node.
                //
                // Scoping it to `<evaluationPath>/<keyword>` rather than to the node is the second
                // half, and it is what makes the rule correct rather than merely narrower. A
                // node-scoped test lets a failure under one keyword delete a DIFFERENT keyword's
                // finding on the same node: measured on a step matching two branches of its
                // `oneOf`, where a failure under the sibling `anyOf` deleted the `oneOf` finding
                // entirely — and on mq-expect.azureservicebus that deletion then disarmed the
                // cascade, turning one correct finding into five reporting REQUIRED fields
                // (including `target`) as unknown properties.
                if (IsAggregateKeyword(keyword) &&
                    CanDeferToDeeperFailure(keyword, evaluationPath, instancePath, compositeGroups) &&
                    HasMoreSpecificFailure($"{evaluationPath}/{keyword}", instancePath, candidates))
                {
                    continue;
                }

                var isUnevaluated = IsUnevaluatedPropertiesShape(keyword, evaluationPath);
                var isForbidden = IsForbiddenPropertyShape(keyword, evaluationPath);

                // JsonSchema.Net reports a closed-object rejection as a BLANK keyword carrying the
                // generic "All values fail against the false schema" — true, and useless to an
                // author, who is shown an empty "[]" tag and left to guess. Rewritten to name the
                // offending property, as the engine does.
                var text = isUnevaluated
                    ? FormatClosureError("unevaluatedProperties", instancePath, instance)
                    : IsAdditionalPropertiesShape(keyword, evaluationPath)
                        ? FormatClosureError("additionalProperties", instancePath, instance)
                        : isForbidden
                            ? FormatForbiddenPropertyError(instancePath, instance)
                            : $"[{keyword}] {message}";

                // Container enrichment is per-KEYWORD, matching the engine's FormatError: only
                // `required` carries it (the closure formatters above add their own). Measured:
                // applying it to every keyword diverged from the CLI on `[type]` and `[enum]`
                // findings that would otherwise have matched.
                if (keyword == "required")
                {
                    text = AppendRequiredContainer(text, instancePath);
                }

                // Resolved lazily, and only once per node: YamlLineResolver walks the document, and
                // rc.4's closed step surface means a suite with many unknown keys produces an error
                // per key. Paying that walk for a keyword about to be suppressed above turned a
                // 25 KB suite into a 20-second validation (measured) against a 10-second worker
                // budget.
                if (!lineResolved)
                {
                    line = YamlLineResolver.ResolveLine(yamlRoot, instancePath);
                    lineResolved = true;
                }

                // Both caller-influenced fields are sanitised (M1). The MESSAGE can echo a
                // caller-supplied value back — some keyword messages ("pattern", "enum") do it
                // natively, and the rewritten closure messages above splice in a property name and
                // step type by construction. The INSTANCE PATH now systematically carries an
                // author-chosen key too: with the step surface closed at rc.4, every typo'd field
                // name lands in a pointer. Raw ASCII control bytes cannot reach here (they fail
                // earlier as yaml-parse), but bidi overrides and other non-printables can, and
                // TextSanitiser's contract is that no such value reaches output unrendered.
                sink.Add(new CollectedSchemaError(
                    new SuiteValidationError(
                        VfxCodeCatalogue.SchemaViolation,
                        TextSanitiser.SanitiseForDisplay(instancePath),
                        TextSanitiser.SanitiseForDisplay(text),
                        line,
                        null),
                    isUnevaluated,
                    isForbidden,
                    keyword));
            }
        }
    }

    /// <summary>
    /// A schema error paired with the one fact <see cref="SuppressUnevaluatedPropertiesCascade"/>
    /// needs about it and which the emitted <see cref="SuiteValidationError"/> does not carry:
    /// whether it came from the step surface's <c>unevaluatedProperties: false</c> closure.
    /// Tracked structurally, from the evaluation node's own keyword and path, rather than by
    /// sniffing the rendered message — the message is caller-influenced text that has already been
    /// through <see cref="TextSanitiser"/> by the time it lands in the record.
    /// </summary>
    private readonly record struct CollectedSchemaError(
        SuiteValidationError Error,
        bool IsUnevaluatedProperties,
        bool IsForbiddenProperty,
        string Keyword);

    /// <summary>
    /// Flattens <paramref name="node"/> and every descendant reachable via
    /// <see cref="EvaluationResults.Details"/> into <paramref name="sink"/>.
    /// </summary>
    private static void FlattenResults(EvaluationResults node, List<EvaluationResults> sink)
    {
        sink.Add(node);
        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                FlattenResults(child, sink);
            }
        }
    }

    /// <summary>
    /// Recognises a JsonSchema.Net 9.3.0 applicator roll-up: <paramref name="node"/> failed only
    /// because some OTHER, more specific node in <paramref name="allNodes"/> also failed — see
    /// this type's remarks for why <see cref="EvaluationResults.Parent"/> cannot be used for this
    /// instead. "More specific" requires BOTH: the other node's <c>evaluationPath</c> nests
    /// strictly deeper than <paramref name="node"/>'s, AND its <c>instanceLocation</c> is the same
    /// as or nests deeper than <paramref name="node"/>'s. Both must hold together — two sibling
    /// array items (e.g. two different steps) evaluate the SAME schema <c>evaluationPath</c> at
    /// DIFFERENT <c>instanceLocation</c>s, so checking evaluationPath alone would let one step's
    /// failure wrongly explain away an unrelated step's genuine one.
    /// </summary>
    /// <remarks>
    /// <paramref name="candidates"/> is pre-filtered by the caller to nodes this validator would
    /// actually report — failing, non-<c>if</c>-discriminator — with both pointers already
    /// stringified. A candidate that is never shown to anyone must not be able to delete one that
    /// would have been: measured on rc.4's <c>$defs/security</c>, whose two <c>allOf</c>/<c>if</c>
    /// profile clauses were deleting the block's own genuine <c>required</c> failure.
    /// </remarks>
    private static bool HasMoreSpecificFailure(
        string keywordSchemaPath,
        string instanceLocation,
        List<(string EvalPath, string InstLoc)> candidates)
    {
        foreach (var (otherEvalPath, otherInstLoc) in candidates)
        {
            // AT or below <evaluationPath>/<keyword>, not strictly below it. JsonSchema.Net gives
            // the subschema evaluated by a keyword an evaluation path that ENDS at that keyword
            // segment — the `oneOf` node's own path is `…/then/oneOf`, and the closure leaves' path
            // is `…/$ref/unevaluatedProperties`. A strict-descendant test therefore matches nothing
            // and every aggregate survives, which measured as a duplicated roll-up beside each
            // finding it was supposed to defer to.
            if (IsPointerPrefixOfOrEqual(keywordSchemaPath, otherEvalPath) &&
                IsPointerPrefixOfOrEqual(instanceLocation, otherInstLoc))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// True when JSON Pointer <paramref name="other"/> is strictly nested under
    /// <paramref name="prefix"/> (never equal to it) — segment-boundary aware, so e.g.
    /// <c>"/steps"</c> is not wrongly treated as a prefix of <c>"/steps1"</c>.
    /// </summary>
    private static bool IsStrictPointerPrefixOf(string prefix, string other) =>
        other != prefix && (prefix.Length == 0 || other.StartsWith(prefix + "/", StringComparison.Ordinal));

    /// <summary>
    /// True when JSON Pointer <paramref name="other"/> equals <paramref name="prefix"/> or is
    /// nested under it — segment-boundary aware, see <see cref="IsStrictPointerPrefixOf"/>.
    /// </summary>
    private static bool IsPointerPrefixOfOrEqual(string prefix, string other) =>
        other == prefix || IsStrictPointerPrefixOf(prefix, other);

    /// <summary>
    /// Recognises an evaluation node that only failed because one of the composed schema's 25
    /// discriminator clauses' own <c>if</c> keyword didn't match this step's <c>type</c> — see
    /// this type's remarks. Such a node's <c>evaluationPath</c> contains an <c>allOf/&lt;N&gt;/if</c>
    /// segment sequence; a clause's genuine <c>then</c> failure never does.
    /// </summary>
    private static bool IsIfDiscriminatorNoise(string evaluationPath)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i] == "allOf" && segments[i + 2] == "if")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A <c>oneOf</c>/<c>anyOf</c> group, identified by the schema <c>evaluationPath</c> prefix up
    /// to and including the keyword, paired with the instance location it was evaluated against.
    /// </summary>
    /// <remarks>
    /// The instance location is part of the identity, not decoration: two sibling array items
    /// (two steps) evaluate the SAME schema <c>evaluationPath</c>, so a group satisfied at
    /// <c>/steps/0</c> must not suppress the losing-branch errors of a genuinely failing composite
    /// at <c>/steps/1</c>.
    /// </remarks>
    private readonly record struct CompositeGroupKey(string Prefix, string InstanceLocation);

    /// <summary>
    /// Tallies every <c>oneOf</c>/<c>anyOf</c> group in the evaluation, returning both the ones that
    /// were SATISFIED (an <c>anyOf</c> with at least one valid branch, a <c>oneOf</c> with exactly
    /// one) and the ones with ANY valid branch — see <see cref="CompositeGroups"/> for why those are
    /// different questions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports the engine's <c>SchemaErrorCollector</c> group tally (<c>IsCompositeBranchRoot</c> +
    /// <c>CompositeGroupState.IsSatisfied</c>) at the pinned commit. A composite reports each
    /// LOSING branch's failure even when a sibling branch won, and neither of this validator's
    /// other two filters can drop those: <see cref="IsIfDiscriminatorNoise"/> only matches
    /// <c>allOf/&lt;N&gt;/if</c>, and <see cref="HasMoreSpecificFailure"/> needs a strictly deeper
    /// failing node, which a losing <c>required</c> leaf never has.
    /// </para>
    /// <para>
    /// A <c>oneOf</c> matching two-or-more branches is deliberately NOT treated as satisfied: that
    /// is a real authoring error, and leaving the group unsatisfied lets the <c>oneOf</c> keyword's
    /// own failure through rather than silently swallowing it. The engine additionally synthesises
    /// a friendlier "matched N branches" message for that case; this validator reports
    /// JsonSchema.Net's own wording instead — less polished, but never silent.
    /// </para>
    /// <para>
    /// Reachability is why this is not optional at this pin. Under <c>v1.0.0-rc.3</c> the only
    /// composites in the composed schema were <c>script.csharp</c>'s and <c>step.timeout</c>'s;
    /// <c>v1.0.0-rc.4</c> takes that to nine, including <c>$defs/service</c>'s
    /// <c>image</c>/<c>project</c> choice — which every suite that declares a service now
    /// evaluates.
    /// </para>
    /// </remarks>
    private static CompositeGroups FindCompositeGroups(
        List<(EvaluationResults Node, string EvalPath, string InstLoc)> projected)
    {
        Dictionary<CompositeGroupKey, (bool IsOneOf, int ValidBranchCount)>? groups = null;

        foreach (var (node, evalPath, instLoc) in projected)
        {
            // Branch ROOT only — the node's own path must terminate exactly at oneOf/<N> or
            // anyOf/<N>. The tally needs the branch's aggregate validity (does its whole nested
            // sub-schema pass?), which only the root node carries; a valid descendant of a failing
            // branch would otherwise be miscounted as a win.
            if (!node.IsValid || !IsCompositeBranchRoot(evalPath, out var prefix, out var isOneOf))
            {
                continue;
            }

            var key = new CompositeGroupKey(prefix, instLoc);
            groups ??= [];
            var count = groups.TryGetValue(key, out var existing) ? existing.ValidBranchCount : 0;
            groups[key] = (isOneOf, count + 1);
        }

        if (groups is null)
        {
            return new CompositeGroups([], []);
        }

        HashSet<CompositeGroupKey> satisfied = [];
        HashSet<CompositeGroupKey> withAnyValidBranch = [];
        foreach (var (key, (isOneOf, validBranchCount)) in groups)
        {
            if (isOneOf ? validBranchCount == 1 : validBranchCount >= 1)
            {
                satisfied.Add(key);
            }

            if (validBranchCount >= 1)
            {
                withAnyValidBranch.Add(key);
            }
        }

        return new CompositeGroups(satisfied, withAnyValidBranch);
    }

    /// <summary>
    /// The composite groups an evaluation produced, split by the two questions this validator asks
    /// of them.
    /// </summary>
    /// <param name="Satisfied">
    /// Groups whose contract is MET — an <c>anyOf</c> with at least one valid branch, a
    /// <c>oneOf</c> with exactly one. Their losing branches describe no document problem and are
    /// dropped.
    /// </param>
    /// <param name="WithAnyValidBranch">
    /// Groups where at least one branch validated, whether or not the group is satisfied. For a
    /// <c>oneOf</c> the difference is the whole point: two matching branches leave it UNsatisfied
    /// while still meaning "nothing underneath me failed", which is what makes its own failure
    /// self-contained.
    /// </param>
    private readonly record struct CompositeGroups(
        HashSet<CompositeGroupKey> Satisfied,
        HashSet<CompositeGroupKey> WithAnyValidBranch);

    /// <summary>
    /// True when <paramref name="evaluationPath"/> passes through some satisfied group's branch AND
    /// <paramref name="instanceLocation"/> is that group's own instance location or below it — i.e.
    /// this error belongs to a branch that lost only because a sibling branch of the same,
    /// already-satisfied composite won.
    /// </summary>
    private static bool IsUnderAnySatisfiedCompositeGroup(
        string evaluationPath,
        string instanceLocation,
        HashSet<CompositeGroupKey> satisfiedGroups)
    {
        if (satisfiedGroups.Count == 0)
        {
            return false;
        }

        foreach (var prefix in FindCompositeBranchPrefixes(evaluationPath))
        {
            foreach (var group in satisfiedGroups)
            {
                if (string.Equals(prefix, group.Prefix, StringComparison.Ordinal) &&
                    IsPointerPrefixOfOrEqual(group.InstanceLocation, instanceLocation))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="evaluationPath"/> terminates exactly at a composite branch root
    /// (<c>…/oneOf/&lt;N&gt;</c> or <c>…/anyOf/&lt;N&gt;</c>), yielding the group prefix up to and
    /// including the keyword.
    /// </summary>
    private static bool IsCompositeBranchRoot(string evaluationPath, out string prefix, out bool isOneOf)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 &&
            (segments[^2] == "oneOf" || segments[^2] == "anyOf") &&
            int.TryParse(segments[^1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            isOneOf = segments[^2] == "oneOf";
            prefix = "/" + string.Join('/', segments[..^1]);
            return true;
        }

        prefix = string.Empty;
        isOneOf = false;
        return false;
    }

    /// <summary>
    /// Every composite group prefix <paramref name="evaluationPath"/> passes through, scanning all
    /// positions rather than only the final two segments.
    /// </summary>
    /// <remarks>
    /// Depth-independent by necessity, not convenience: a losing branch's failure can sit
    /// arbitrarily deep inside that branch's own sub-schema (<c>…/anyOf/1/properties/x/required</c>,
    /// not merely <c>…/anyOf/1</c>), and every such descendant belongs to the same branch for
    /// suppression purposes. Mirrors <see cref="IsIfDiscriminatorNoise"/>'s own full-path scan.
    /// </remarks>
    private static IEnumerable<string> FindCompositeBranchPrefixes(string evaluationPath)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if ((segments[i] == "oneOf" || segments[i] == "anyOf") &&
                int.TryParse(segments[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                yield return "/" + string.Join('/', segments[..(i + 1)]);
            }
        }
    }

    /// <summary>
    /// Drops every error located AT or INSIDE an object that a <c>properties/&lt;name&gt;: false</c>
    /// clause has already rejected outright.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports the engine's <c>SuppressErrorsInsideForbiddenContainer</c>. Once a whole block is
    /// refused, its contents are moot: reporting them is not merely noise but misdirection. Measured
    /// before this pass, on a <c>security</c> block declared on a redis dependency (which no profile
    /// supports, so the block must be deleted) with a wrong-case <c>profile</c>: this validator
    /// reported THREE findings to the CLI's one, two of them telling the author to fix <c>profile</c>
    /// and to add <c>endpoint</c> — to a block that cannot exist at all. That is the same
    /// advice-that-loops failure the <c>image</c>/<c>project</c> case demonstrates.
    /// </para>
    /// <para>
    /// The forbidden-shape error is the SUBSUMING one and always survives. Containment is by JSON
    /// Pointer segment boundary and includes the container's own location, because a sibling
    /// <c>required</c> failure reports AT the container path, not below it.
    /// </para>
    /// </remarks>
    private static List<CollectedSchemaError> SuppressErrorsInsideForbiddenContainer(
        List<CollectedSchemaError> errors)
    {
        HashSet<string>? forbiddenLocations = null;

        foreach (var collected in errors)
        {
            if (collected.IsForbiddenProperty && collected.Error.InstancePath is { } path)
            {
                forbiddenLocations ??= new HashSet<string>(StringComparer.Ordinal);
                forbiddenLocations.Add(path);
            }
        }

        if (forbiddenLocations is null)
        {
            return errors;
        }

        var survivors = new List<CollectedSchemaError>(errors.Count);
        foreach (var collected in errors)
        {
            if (collected.IsForbiddenProperty)
            {
                survivors.Add(collected);
                continue;
            }

            var subsumed = false;
            if (collected.Error.InstancePath is { } path)
            {
                foreach (var forbidden in forbiddenLocations)
                {
                    if (IsPointerPrefixOfOrEqual(forbidden, path))
                    {
                        subsumed = true;
                        break;
                    }
                }
            }

            if (!subsumed)
            {
                survivors.Add(collected);
            }
        }

        return survivors;
    }

    /// <summary>
    /// Drops a <c>const</c> error whenever an <c>enum</c> error is also reported at the SAME
    /// instance location — they are two statements of one mistake, and the enum one names the full
    /// accepted set.
    /// </summary>
    /// <remarks>
    /// The composed schema pins <c>healthCheck.type</c> to <c>const: "tcp"</c> under a conditional
    /// (a ports-only service) while <c>healthCheck.type</c> also carries an unconditional
    /// <c>enum: ["tcp","http"]</c>. A wrong-case value fails BOTH at the same location. Measured on
    /// the engine's own rejected corpus, this was the last remaining case where this validator
    /// reported a different NUMBER of errors than <c>vouchfx validate</c> — two against one.
    /// </remarks>
    private static List<CollectedSchemaError> SuppressRedundantConstWhenEnumPresent(
        List<CollectedSchemaError> errors)
    {
        HashSet<string>? locationsWithEnum = null;

        foreach (var collected in errors)
        {
            if (collected.Keyword == "enum" && collected.Error.InstancePath is { } path)
            {
                locationsWithEnum ??= new HashSet<string>(StringComparer.Ordinal);
                locationsWithEnum.Add(path);
            }
        }

        if (locationsWithEnum is null)
        {
            return errors;
        }

        var survivors = new List<CollectedSchemaError>(errors.Count);
        foreach (var collected in errors)
        {
            if (collected.Keyword == "const" &&
                collected.Error.InstancePath is { } path &&
                locationsWithEnum.Contains(path))
            {
                continue;
            }

            survivors.Add(collected);
        }

        return survivors;
    }

    /// <summary>
    /// Drops a step's <c>unevaluatedProperties</c> entries when that SAME step also carries at
    /// least one error of a different kind — either another schema error or an
    /// <c>unknown-step-type</c> cross-check finding. Ports the engine's
    /// <c>SchemaErrorCollector.SuppressUnevaluatedPropertiesCascade</c> together with the
    /// unknown-type half that its <c>DocumentValidator</c> applies on top.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step's fields can only be judged "unevaluated" relative to whichever <c>if</c>/<c>then</c>
    /// clause matched its <c>type</c>. Once that clause has failed for an unrelated reason — or
    /// never matched at all, because the type is unregistered — JSON Schema withholds its
    /// <c>properties</c> annotations, so every legitimate field of that step presents as unknown.
    /// </para>
    /// <para>
    /// This trades completeness for correctness: a step carrying BOTH a genuine defect and a real
    /// typo has the typo hidden this round, and the author sees it on the next run once the
    /// reported defect is fixed. That is strictly better than asserting a false "unknown property"
    /// beside a true one. When the ONLY thing wrong with a step is an unevaluated property,
    /// nothing here touches it — that is the whole point of the closure.
    /// </para>
    /// <para>
    /// Scoping is by the step's own instance path (<c>/steps/&lt;N&gt;</c>) derived from each
    /// error's location, never from list position, so two steps in one document are judged
    /// independently. An error that does not sit under a numbered <c>steps</c> element has no step
    /// scope and is never touched.
    /// </para>
    /// </remarks>
    private static List<SuiteValidationError> SuppressUnevaluatedPropertiesCascade(
        List<CollectedSchemaError> schemaErrors,
        List<SuiteValidationError> unknownTypeErrors)
    {
        HashSet<string>? stepsWithOtherErrors = null;

        foreach (var collected in schemaErrors)
        {
            if (!collected.IsUnevaluatedProperties &&
                TryGetStepScope(collected.Error.InstancePath, out var scope))
            {
                stepsWithOtherErrors ??= new HashSet<string>(StringComparer.Ordinal);
                stepsWithOtherErrors.Add(scope);
            }
        }

        foreach (var unknownTypeError in unknownTypeErrors)
        {
            if (TryGetStepScope(unknownTypeError.InstancePath, out var scope))
            {
                stepsWithOtherErrors ??= new HashSet<string>(StringComparer.Ordinal);
                stepsWithOtherErrors.Add(scope);
            }
        }

        var survivors = new List<SuiteValidationError>(schemaErrors.Count);
        foreach (var collected in schemaErrors)
        {
            if (stepsWithOtherErrors is not null &&
                collected.IsUnevaluatedProperties &&
                TryGetStepScope(collected.Error.InstancePath, out var scope) &&
                stepsWithOtherErrors.Contains(scope))
            {
                continue;
            }

            survivors.Add(collected.Error);
        }

        return survivors;
    }

    /// <summary>
    /// True for the blank-keyword <c>unevaluatedProperties: false</c> rejection shape — the node's
    /// own keyword is empty and its <c>evaluationPath</c>'s terminal segment is
    /// <c>unevaluatedProperties</c>. Mirrors the engine's <c>IsUnevaluatedPropertiesShape</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "blank keyword": the schema also uses <c>additionalProperties:
    /// false</c> and per-field <c>"&lt;name&gt;": false</c> closures, which produce the SAME
    /// generic message and are distinguished only by that terminal segment. Neither of those is
    /// subject to the annotation-withholding cascade this suppression exists for, so neither may
    /// be swept up by it.
    /// </remarks>
    private static bool IsUnevaluatedPropertiesShape(string keyword, string evaluationPath) =>
        keyword.Length == 0 && EndsWithSegment(evaluationPath, "unevaluatedProperties");

    /// <summary>
    /// True for the blank-keyword <c>additionalProperties: false</c> rejection shape — an unknown
    /// key on a plainly-closed object (<c>$defs/metadata</c>, <c>$defs/service</c>,
    /// <c>$defs/dependency</c>, <c>$defs/serviceHealthCheck</c>, the document root, a provider's
    /// nested <c>expect</c>/<c>match</c> block, …) rather than the step surface's
    /// <c>unevaluatedProperties</c> closure. Same generic underlying message as
    /// <see cref="IsUnevaluatedPropertiesShape"/>, distinguished only by the evaluation path's
    /// terminal segment — exactly as that shape is.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than "any blank keyword": the schema also uses per-field
    /// <c>"&lt;name&gt;": false</c> closures (a property forbidden by a conditional, such as
    /// <c>$defs/service</c>'s <c>image</c>/<c>project</c> exclusion), whose terminal segment is the
    /// property name. Rewriting those as "unknown property" would be actively wrong — the property
    /// is known, it is forbidden HERE — so they keep JsonSchema.Net's own text.
    /// </remarks>
    private static bool IsAdditionalPropertiesShape(string keyword, string evaluationPath) =>
        keyword.Length == 0 && EndsWithSegment(evaluationPath, "additionalProperties");

    /// <summary>
    /// True for the blank-keyword per-field <c>"&lt;name&gt;": false</c> shape — one specific,
    /// already-declared property forbidden by a conditional clause rather than an unknown key.
    /// </summary>
    /// <remarks>
    /// Recognised structurally: JsonSchema.Net only produces a leaf node whose evaluation path
    /// terminates EXACTLY at <c>properties/&lt;name&gt;</c> when the subschema mapped to that
    /// property is a bare boolean; a normal subschema's failing keywords always add at least one
    /// more segment. The composed schema uses this for <c>$defs/service</c>'s
    /// <c>image</c>/<c>project</c> exclusion and <c>ports</c>-on-project rejection, the per-profile
    /// <c>clientCert</c>/<c>clientKey</c> rules on <c>$defs/security</c>, and the per-kind
    /// dependency exclusions — the whole rc.4 authoring surface.
    /// </remarks>
    private static bool IsForbiddenPropertyShape(string keyword, string evaluationPath)
    {
        if (keyword.Length != 0)
        {
            return false;
        }

        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[^2] == "properties";
    }

    /// <summary>
    /// Message for a per-field <c>false</c> rejection. Says the property is not valid HERE — never
    /// that it is unknown, which would be wrong: the property is perfectly well known, and
    /// forbidden by a conditional the author has already satisfied some other way.
    /// </summary>
    /// <remarks>
    /// Deliberately structural and generic. The engine derives a specific sentence per clause
    /// (<c>"Property 'project' cannot be combined with 'image' on service 'app'"</c>,
    /// <c>"Property 'clientCert' is not valid when 'profile' is 'tls'"</c>, and a paragraph for the
    /// non-kafka <c>security</c> case). Those are hand-authored per schema clause, sit inside the
    /// engine's frozen message surface, and carry release-position prose that would rot here
    /// independently of the engine — so they are NOT copied. This wording is the honest subset: it
    /// names the offending property and its container and stops, rather than guessing at a reason.
    /// The gap is recorded in <c>RealValidateAgainstPinnedCliTests</c>'s known-divergence list.
    /// </remarks>
    private static string FormatForbiddenPropertyError(string instancePath, JsonElement instance)
    {
        var propertyName = LastPointerSegment(instancePath);
        var container = TryDescribeContainer(instancePath, instance)
            ?? TryDescribeEnvironmentContainer(instancePath);

        return container is null
            ? $"[properties] Property '{propertyName}' is not valid here"
            : $"[properties] Property '{propertyName}' is not valid on {container}";
    }

    /// <summary>
    /// The draft 2020-12 applicator keywords — the ones whose failure means only "a subschema
    /// underneath me failed" and which therefore defer to a more specific node when one exists.
    /// </summary>
    /// <remarks>
    /// Assertion keywords (<c>required</c>, <c>enum</c>, <c>const</c>, <c>type</c>, <c>pattern</c>,
    /// …) are deliberately absent: they state a real, self-contained defect and are never explained
    /// away by something deeper. The blank keyword is absent too — that is a closure LEAF (see
    /// <see cref="IsUnevaluatedPropertiesShape"/>), the most specific node there is.
    /// <para>
    /// <c>contains</c> is deliberately absent despite being an applicator, and its absence is a
    /// PARITY fix rather than a precaution. Its failure means "NO item matched", and the per-item
    /// failures it would defer to are the losing attempts that prove exactly that. It is also not a
    /// 9.3-era roll-up at all: the engine's own pinned JsonSchema.Net emits the identical
    /// <c>contains</c> message, and none of its suppression passes touches it — so listing it here
    /// would have DELETED a finding the CLI reports. The schema carries no <c>contains</c> at this
    /// pin, so the case is unreachable today and untestable against the CLI; the reasoning is
    /// recorded because the next schema to add one will not come with it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> AggregateKeywords = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "additionalProperties", "unevaluatedProperties",
        "items", "prefixItems", "unevaluatedItems", "propertyNames",
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "dependentSchemas",
        "$ref", "$dynamicRef",
    };

    private static bool IsAggregateKeyword(string keyword) => AggregateKeywords.Contains(keyword);

    /// <summary>
    /// Whether an aggregate keyword's failure may defer to a deeper one at all. True for every
    /// aggregate except a <c>oneOf</c> that failed by matching TOO MANY branches, whose failure
    /// nothing deeper can explain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>oneOf</c> has two failure modes and they are opposites. Matching NO branch is explained
    /// by the branches' own failures, and deferring to them is right — measured against the CLI on
    /// a <c>script.csharp</c> step declaring neither <c>code</c> nor <c>file</c>, where both sides
    /// report the two <c>required</c> findings and no <c>oneOf</c>. Matching SEVERAL branches is a
    /// self-contained defect: the author must remove one, and every branch that could be blamed for
    /// it validated.
    /// </para>
    /// <para>
    /// Without this the rule inverts on any <c>oneOf</c> of three or more branches — two matching
    /// and one failing would delete the "matched 2" finding and replace it with a demand to satisfy
    /// the third, which would make it three. Every <c>oneOf</c> in the schema at this pin has
    /// exactly two branches, where "matched 2" implies no branch failed and the bug is unreachable;
    /// this closes it by construction instead of resting on that census, because the census is
    /// exactly the kind of assumption a repin silently invalidates.
    /// </para>
    /// <para>
    /// <b>The governing rule is PARITY with the engine's own emissions, not the semantics.</b> That
    /// distinction matters because <c>not</c> looks identical under a semantic reading — it fails
    /// exactly when nothing beneath it failed — yet it must stay deferrable, because the engine
    /// emits nothing for it while it DOES synthesise a "matched N branches" message for a
    /// <c>oneOf</c>. Applying the "self-contained failure" reasoning to <c>not</c> would introduce
    /// an over-report. <c>anyOf</c> needs no case at all: it fails if and only if zero branches
    /// validated, so a failing <c>anyOf</c> can never appear in
    /// <see cref="CompositeGroups.WithAnyValidBranch"/> and the guard would be a provable no-op.
    /// </para>
    /// </remarks>
    private static bool CanDeferToDeeperFailure(
        string keyword,
        string evaluationPath,
        string instancePath,
        CompositeGroups compositeGroups)
    {
        if (keyword != "oneOf")
        {
            return true;
        }

        return !compositeGroups.WithAnyValidBranch.Contains(
            new CompositeGroupKey($"{evaluationPath}/oneOf", instancePath));
    }

    /// <summary>
    /// True when the last <c>/</c>-separated segment of <paramref name="evaluationPath"/> equals
    /// <paramref name="keyword"/> exactly.
    /// </summary>
    private static bool EndsWithSegment(string evaluationPath, string keyword)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1] == keyword;
    }

    /// <summary>
    /// Builds the actionable message for an <c>unevaluatedProperties: false</c> rejection: the
    /// offending property's name (the last segment of <paramref name="instancePath"/>) and, when
    /// its containing object carries a string <c>type</c>, the step type — e.g.
    /// <c>[unevaluatedProperties] Unknown property 'taget' on step type 'http.rest'</c>. The
    /// suffix is omitted, never fabricated, when the type cannot be resolved.
    /// </summary>
    private static string FormatClosureError(string keyword, string instancePath, JsonElement instance)
    {
        var propertyName = LastPointerSegment(instancePath);

        // A closure rejection names its container from EITHER surface: a step (by its type) or an
        // environment entry (by its declared key). Unlike the plain keyword messages, where only
        // `required` carries the environment container, all three engine closure formatters do.
        var container = TryDescribeContainer(instancePath, instance)
            ?? TryDescribeEnvironmentContainer(instancePath);

        return container is null
            ? $"[{keyword}] Unknown property '{propertyName}'"
            : $"[{keyword}] Unknown property '{propertyName}' on {container}";
    }

    /// <summary>
    /// Names the thing a closure rejection happened inside — <c>step type 'http.rest'</c>,
    /// <c>dependency 'orders-db'</c>, <c>service 'orders-api'</c> — or <see langword="null"/> when
    /// the location is somewhere this validator cannot name confidently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately keyed on WHERE the pointer sits, not merely on a <c>type</c> member being
    /// resolvable. <see cref="TryResolveContainerType"/> reads <c>type</c> off whatever object the
    /// pointer's parent names, and the schema's other closed objects have one too — a dependency's
    /// <c>type</c> is its technology ("kafka"), not a step type. Resolving that and labelling it
    /// "step type" would be a fabricated attribution.
    /// </para>
    /// <para>
    /// A dependency or service is named by its own key rather than by any member, which is what the
    /// engine's own <c>TryResolveEnvironmentContainer</c> does and what makes the label useful: the
    /// author's logical name is the thing they can search their file for. Nothing is invented — an
    /// unrecognised location simply gets no suffix.
    /// </para>
    /// </remarks>
    private static string? TryDescribeContainer(string instancePath, JsonElement instance)
    {
        if (!TryGetStepScope(instancePath, out _))
        {
            // Environment-scoped locations are named by TryDescribeEnvironmentContainer instead,
            // applied to EVERY keyword rather than only to closures — see its remarks.
            return null;
        }

        var stepType = TryResolveContainerType(instancePath, instance);
        return stepType is null ? null : $"step type '{stepType}'";
    }

    /// <summary>
    /// Adds the owning dependency/service to a <c>required</c> message, in the same two forms the
    /// engine uses: <c>on &lt;kind&gt; '&lt;name&gt;'</c> for a direct field, and
    /// <c>in service '&lt;name&gt;' (at healthCheck)</c> when the incomplete object is the nested
    /// health-check block.
    /// </summary>
    /// <remarks>
    /// The health-check case is intercepted FIRST and deliberately. A <c>required</c> violation
    /// always reports the CONTAINER missing the property, so
    /// <c>/environment/services/&lt;name&gt;/healthCheck</c> is itself depth 4 and would otherwise
    /// take the direct-field form — announcing "… are not present on service '&lt;name&gt;'" when
    /// the health-check block is what is incomplete, and colliding with a dependency's own
    /// unrelated required <c>type</c> field. Where neither form applies (a deeper nesting this
    /// method does not model), no suffix is added rather than a wrong one.
    /// </remarks>
    private static string AppendRequiredContainer(string text, string instancePath)
    {
        var segments = instancePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Depth FOUR, not five: a `required` violation reports the CONTAINER missing the property,
        // so the pointer is /environment/services/<name>/healthCheck and `healthCheck` is
        // segments[3]. Written as length 5 / segments[4] first, which the schema cannot produce —
        // the branch never fired, and the fixture named after it passed on a count-only assertion.
        if (segments.Length == 4 &&
            segments[0] == "environment" &&
            segments[1] == "services" &&
            segments[3] == "healthCheck")
        {
            return $"{text} in service '{DecodePointerSegment(segments[2])}' (at healthCheck)";
        }

        return TryDescribeEnvironmentContainer(instancePath) is { } container
            ? $"{text} on {container}"
            : text;
    }

    /// <summary>
    /// Names the declared dependency or service an environment-scoped error sits inside —
    /// <c>dependency 'orders-db'</c>, <c>service 'orders-api'</c> — or <see langword="null"/>
    /// anywhere else.
    /// </summary>
    /// <remarks>
    /// Applied to the closure and forbidden-property formatters, and to <c>required</c> via
    /// <see cref="AppendRequiredContainer"/> — NOT to every keyword. That narrowing is the engine's
    /// rule (see <c>FormatError</c>) and was learned by measurement: applying it universally
    /// diverged from the CLI on <c>[type]</c> and <c>[enum]</c> findings that otherwise matched.
    /// Step-scoped errors get no such suffix from either side — a step is identified by its line
    /// number and its own <c>step type</c> descriptor.
    /// <para>
    /// The container is named by its own key, which is the string the author can search their file
    /// for, rather than by any member of it.
    /// </para>
    /// </remarks>
    private static string? TryDescribeEnvironmentContainer(string instancePath)
    {
        var segments = instancePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The engine's TryResolveEnvironmentContainer rule, ported exactly: a DIRECT field of the
        // container (depth 4), or a field nested below its `security` block. Nothing else.
        //
        // The depth restriction is the whole substance of this method and was learned by
        // measurement, not read off the engine first. An earlier revision appended the container to
        // ANY environment-scoped error, which read as harmless enrichment and was not: at depth 3
        // the container IS the failing object, and the engine adds no suffix there (measured on
        // service-neither-image-nor-project, where the unconditional form diverged from the CLI on
        // a fixture that would otherwise have matched); at arbitrary depth the suffix names the
        // wrong object entirely.
        //
        // KNOWN GAP, stated rather than implied: below a `security` block this returns the plain
        // "on <kind> '<name>'" form, where the engine renders the more precise
        // "in <kind> '<name>' (at security.serverArtifacts[0].<field>)". The finding and its
        // location agree with the CLI; only the wording is coarser. Pinned by the
        // "nested security locator" fixture in RealValidateAgainstPinnedCliTests'
        // KnownWordingGapFixtures, whose NotEqual guard will report when the engine closes it.
        var isDirectField = segments.Length == 4;
        var isNestedSecurityField = segments.Length > 4 && segments[3] == "security";

        if (!(isDirectField || isNestedSecurityField) ||
            segments.Length < 3 ||
            segments[0] != "environment")
        {
            return null;
        }

        var kind = segments[1] switch
        {
            "dependencies" => "dependency",
            "services" => "service",
            _ => null,
        };

        return kind is null ? null : $"{kind} '{DecodePointerSegment(segments[2])}'";
    }

    /// <summary>
    /// Resolves the <c>type</c> of the object CONTAINING the property named by the final segment of
    /// <paramref name="instancePath"/>, by walking <paramref name="instance"/> down every earlier
    /// segment. Returns <see langword="null"/> the moment the walk cannot proceed or the container
    /// has no string <c>type</c>.
    /// </summary>
    private static string? TryResolveContainerType(string instancePath, JsonElement instance)
    {
        var segments = instancePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var current = instance;

        // Every segment except the last: that final one names the unevaluated property itself,
        // not a step down into it.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = DecodePointerSegment(segments[i]);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                // NumberStyles.None + InvariantCulture, matching TryGetStepScope: a JSON Pointer
                // array index is a bare run of digits, so a leading sign, embedded whitespace or a
                // culture-specific group separator is malformed input, not a number to coerce
                // helpfully. Bare int.TryParse accepts all three.
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else
            {
                return null;
            }
        }

        if (current.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return current.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
    }

    /// <summary>
    /// Extracts the owning step's own instance path (<c>/steps/&lt;N&gt;</c>) from an error's
    /// location: <c>/steps/0/target</c> and <c>/steps/0</c> both yield <c>/steps/0</c>. Returns
    /// <see langword="false"/> for any location not under a numbered <c>steps</c> element, so a
    /// document-level violation is never scoped to a step.
    /// </summary>
    private static bool TryGetStepScope(string? instancePath, out string stepScope)
    {
        stepScope = string.Empty;
        if (instancePath is null)
        {
            return false;
        }

        var segments = instancePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0] == "steps" &&
            int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            stepScope = $"/steps/{segments[1]}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// The last segment of a JSON Pointer, RFC 6901 escapes decoded.
    /// </summary>
    private static string LastPointerSegment(string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? pointer : DecodePointerSegment(segments[^1]);
    }

    /// <summary>
    /// Decodes RFC 6901's two pointer escapes. Order matters: <c>~1</c> first, then <c>~0</c>.
    /// </summary>
    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
               .Replace("~0", "~", StringComparison.Ordinal);

    private static void AppendUnknownStepTypeErrors(JsonElement root, YamlMappingNode? yamlRoot, List<SuiteValidationError> sink)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("steps", out var steps) ||
            steps.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.Object &&
                step.TryGetProperty("type", out var typeProperty) &&
                typeProperty.ValueKind == JsonValueKind.String)
            {
                var type = typeProperty.GetString()!;
                if (StepTypeCatalogue.Find(type) is null)
                {
                    var instancePath = $"/steps/{index}/type";
                    var line = YamlLineResolver.ResolveLine(yamlRoot, instancePath);
                    var knownTypes = string.Join(", ", StepTypeCatalogue.All.Select(t => t.Type));

                    // The step type itself is caller-supplied (M1): sanitised before it is
                    // spliced into the message.
                    sink.Add(new SuiteValidationError(
                        VfxCodeCatalogue.UnknownStepType,
                        instancePath,
                        $"Unknown step type '{TextSanitiser.SanitiseForDisplay(type)}'. Known types: {knownTypes}.",
                        line,
                        null));
                }
            }

            index++;
        }
    }
}
