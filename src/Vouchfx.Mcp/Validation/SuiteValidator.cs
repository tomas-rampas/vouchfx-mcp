using System.Text.Json;
using Json.Schema;
using YamlDotNet.Core;

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
/// <b>Unknown step types are cross-checked separately</b> against <see cref="StepTypeCatalogue"/>
/// (<c>unknown-step-type</c>), because the schema's if/then-with-no-else structure lets an
/// unregistered type pass raw evaluation with zero errors.
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
    public static ValidateSuiteResult ValidateFile(string path)
    {
        var fastRejectError = CheckFastRejects(path);
        if (fastRejectError is not null)
        {
            return Invalid(fastRejectError);
        }

        string yamlText;
        try
        {
            yamlText = File.ReadAllText(path);
        }
        catch (Exception ex) when (IsExpectedFileAccessException(ex))
        {
            return Invalid(BuildFileAccessError(path, ex));
        }

        return ValidateYaml(yamlText);
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
                "too-large",
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
    public static ValidateSuiteResult ValidateYaml(string yamlText)
    {
        // MUST run before any YamlDotNet call (see YamlSafetyGuard's remarks for the full threat
        // model): a native StackOverflowException from deeply nested input cannot be caught by
        // any try/catch, so the only fix is to never let YamlDotNet see text shaped like that.
        var safetyError = YamlSafetyGuard.Check(yamlText);
        if (safetyError is not null)
        {
            return Invalid(safetyError);
        }

        JsonDocument document;
        try
        {
            document = YamlToJsonConverter.Convert(yamlText);
        }
        catch (YamlException ex)
        {
            return Invalid(new SuiteValidationError(
                "yaml-parse", null, TextSanitiser.SanitiseForDisplay(ex.Message), ex.Start.Line, ex.Start.Column));
        }
        catch (InvalidOperationException ex)
        {
            // The YAML is syntactically empty (YamlToJsonConverter.Convert's own guard) — not a
            // YamlException, but the same "cannot proceed past parsing" family of problem.
            return Invalid(new SuiteValidationError(
                "yaml-parse", null, TextSanitiser.SanitiseForDisplay(ex.Message), null, null));
        }
        catch (JsonException ex)
        {
            // A raw control character embedded in a quoted YAML scalar (built numerically in
            // tests, never as a literal) can round-trip through YamlDotNet's
            // SerializerBuilder().JsonCompatible() re-emission as an UNESCAPED control byte
            // inside the JSON text it produces — invalid JSON that JsonDocument.Parse then
            // rejects. Caught here so a hostile value like that is reported as a structured
            // yaml-parse error instead of escaping ValidateYaml's "never throws" contract.
            return Invalid(new SuiteValidationError(
                "yaml-parse", null, TextSanitiser.SanitiseForDisplay(ex.Message), null, null));
        }

        using (document)
        {
            var errors = new List<SuiteValidationError>();

            var results = Schema.Evaluate(document.RootElement, Options);
            if (!results.IsValid)
            {
                CollectSchemaErrors(results, yamlText, errors);
            }

            // Always cross-checked, independent of results.IsValid: a step whose type matches
            // none of the 25 known consts satisfies every allOf clause vacuously (see remarks
            // above), so the schema alone would report no error for it at all.
            AppendUnknownStepTypeErrors(document.RootElement, yamlText, errors);

            return new ValidateSuiteResult(errors.Count == 0, errors);
        }
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
            return new SuiteValidationError("file-not-found", null, $"File not found: '{sanitisedPath}'.", null, null);
        }

        return new SuiteValidationError(
            "file-access-error",
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

    private static void CollectSchemaErrors(EvaluationResults node, string yamlText, List<SuiteValidationError> sink)
    {
        if (node.IsValid)
        {
            return;
        }

        if (node.Errors is { Count: > 0 } && !IsIfDiscriminatorNoise(node.EvaluationPath.ToString()))
        {
            var instancePath = node.InstanceLocation.ToString();
            var line = YamlLineResolver.ResolveLine(yamlText, instancePath);

            foreach (var (keyword, message) in node.Errors)
            {
                // Sanitised (M1): some JSON Schema keyword messages (e.g. "pattern", "enum") can
                // echo back part of the actual, caller-supplied instance value.
                sink.Add(new SuiteValidationError(
                    "schema", instancePath, TextSanitiser.SanitiseForDisplay($"[{keyword}] {message}"), line, null));
            }
        }

        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                CollectSchemaErrors(child, yamlText, sink);
            }
        }
    }

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

    private static void AppendUnknownStepTypeErrors(JsonElement root, string yamlText, List<SuiteValidationError> sink)
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
                    var line = YamlLineResolver.ResolveLine(yamlText, instancePath);
                    var knownTypes = string.Join(", ", StepTypeCatalogue.All.Select(t => t.Type));

                    // The step type itself is caller-supplied (M1): sanitised before it is
                    // spliced into the message.
                    sink.Add(new SuiteValidationError(
                        "unknown-step-type",
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
