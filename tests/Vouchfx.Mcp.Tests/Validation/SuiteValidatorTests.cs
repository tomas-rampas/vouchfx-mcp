using System.Globalization;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="SuiteValidator"/>: REQ-003's validate_suite logic, and EDGE-003's validate
/// path — three distinct structured error shapes, none of which crash the validator.
/// </summary>
public class SuiteValidatorTests
{
    [Fact]
    public void ValidateYaml_ConformingSuite_ReturnsValidTrueWithNoErrors()
    {
        const string yaml = """
            steps:
              - id: check-health
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    // ── EDGE-003(c): schema-invalid — unknown step type AND a missing required field ──────────

    [Fact]
    public void ValidateYaml_UnknownStepTypeAndMissingRequiredField_SurfacesBothErrorsWithDistinctPaths()
    {
        const string yaml = """
            steps:
              - id: unknown-step
                type: totally.unknown
                target: something
              - id: incomplete-http
                type: http.rest
                target: orders-api
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        Assert.Equal(2, result.Errors.Count);

        var unknownTypeError = Assert.Single(result.Errors, e => e.InstancePath == "/steps/0/type");
        Assert.Equal("unknown-step-type", unknownTypeError.Kind);
        Assert.Contains("totally.unknown", unknownTypeError.Message, StringComparison.Ordinal);

        var missingFieldError = Assert.Single(result.Errors, e => e.InstancePath == "/steps/1");
        Assert.Equal("schema", missingFieldError.Kind);
        Assert.Contains("method", missingFieldError.Message, StringComparison.Ordinal);
        Assert.Contains("path", missingFieldError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateYaml_InvalidSuite_NeverSurfacesIfDiscriminatorNoise()
    {
        // Schema-structure surprise (see STEP 0): the composed schema's step definition is 25
        // unconditional if/then branches, one per registered type. Evaluating with
        // OutputFormat.List reports EVERY branch's "if" mismatch (up to 24 per invalid step) as
        // its own "invalid" node with a populated Errors dictionary ("Expected \"<other type>\"")
        // — noise that must be filtered out, not surfaced as if it were a real document problem.
        const string yaml = """
            steps:
              - id: incomplete-http
                type: http.rest
                target: orders-api
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("Expected", StringComparison.Ordinal));
    }

    // ── EDGE-003(b): unparseable YAML ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidateYaml_MalformedYaml_ReturnsYamlParseErrorWithLineAndColumn()
    {
        // A tab character used for indentation is invalid YAML syntax.
        const string yaml = "steps:\n  - id: s1\n\ttype: http.rest\n";

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("yaml-parse", error.Kind);
        Assert.NotNull(error.Line);
        Assert.True(error.Line > 0);
        Assert.NotNull(error.Column);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void ValidateYaml_EmptyDocument_ReturnsYamlParseErrorWithoutCrashing()
    {
        var result = SuiteValidator.ValidateYaml(string.Empty);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("yaml-parse", error.Kind);
    }

    // ── EDGE-003(a): file not found ────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateFile_MissingFile_ReturnsFileNotFoundErrorWithoutCrashing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");

        var result = SuiteValidator.ValidateFile(missingPath);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("file-not-found", error.Kind);
        Assert.Contains(missingPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFile_ExistingValidFile_ReturnsValidTrue()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        File.WriteAllText(tempPath, """
            steps:
              - id: check-health
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        try
        {
            var result = SuiteValidator.ValidateFile(tempPath);

            Assert.True(result.Valid);
            Assert.Empty(result.Errors);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── M1: untrusted values must be sanitised before they reach a message ────────────────────

    [Fact]
    public void ValidateFile_MissingFileWithControlCharacterInPath_ReturnsSanitisedError()
    {
        // The path is caller-supplied (the validate_suite MCP tool argument) and must be
        // sanitised the same way EnginePin's own failure messages are: built numerically
        // (27 equals ASCII ESC), never as a literal.
        //
        // Which exact error Kind fires here is a platform/runtime detail — a control character
        // in a path can surface as a plain "not found" or as a syntactically-rejected path
        // (IOException et al.), and that distinction isn't the point of this test. What must
        // hold regardless is EDGE-003(a)'s "never crashes" contract AND M1's "never raw" one.
        var disallowedByte = ((char)27).ToString();
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{disallowedByte}.e2e.yaml");

        var result = SuiteValidator.ValidateFile(missingPath);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.True(
            error.Kind is "file-not-found" or "file-access-error",
            $"Expected a file-related error kind, got '{error.Kind}'.");
        Assert.DoesNotContain(disallowedByte, error.Message, StringComparison.Ordinal);
        foreach (var c in error.Message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    [Fact]
    public void ValidateYaml_UnknownStepTypeWithNonPrintableCharacter_ReturnsSanitisedMessage()
    {
        // Unlike a raw ASCII control byte (see the yaml-parse test below), a non-ASCII Unicode
        // character such as U+202E (RIGHT-TO-LEFT OVERRIDE — a real terminal-spoofing character)
        // survives the YAML->JSON bridge intact and reaches this message untouched. It must
        // still be sanitised: SanitiseForDisplay treats anything outside printable ASCII as
        // suspect, not only the ASCII control range.
        var disallowedCharacter = ((char)0x202E).ToString();
        var yaml = $"steps:\n  - id: s1\n    type: \"totally.unknown{disallowedCharacter}\"\n";

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors, e => e.Kind == "unknown-step-type");
        Assert.DoesNotContain(disallowedCharacter, error.Message, StringComparison.Ordinal);
        foreach (var c in error.Message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    [Fact]
    public void ValidateYaml_UnknownPropertyNameWithNonPrintableCharacter_SanitisesBothMessageAndInstancePath()
    {
        // Newly reachable at engine v1.0.0-rc.4: with $defs/step closed by
        // "unevaluatedProperties": false, ANY key an author invents now produces an error naming
        // it — in the message AND in the instance path, which is serialised to the MCP client
        // verbatim alongside it. Both are caller-authored text and both must be rendered.
        var disallowedCharacter = ((char)0x202E).ToString();
        var yaml = "steps:\n" +
                   "  - id: s1\n" +
                   "    type: http.rest\n" +
                   "    target: api\n" +
                   "    method: GET\n" +
                   "    path: /x\n" +
                   $"    \"ta{disallowedCharacter}get\": nope\n";

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors, e => e.Kind == "schema");

        Assert.DoesNotContain(disallowedCharacter, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InstancePath);
        Assert.DoesNotContain(disallowedCharacter, error.InstancePath!, StringComparison.Ordinal);

        foreach (var c in error.Message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }

        foreach (var c in error.InstancePath!)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }

        // Sanitising the pointer must not damage the part callers navigate by: the step scope has
        // to survive, or the cascade below could no longer attribute the error to its step.
        Assert.StartsWith("/steps/0/", error.InstancePath!, StringComparison.Ordinal);
    }

    // ── The rc.4 closed-step-surface rules (unevaluatedProperties) ────────────────────────────

    [Fact]
    public void ValidateYaml_StepWithOnlyAnUnknownProperty_NamesThePropertyAndItsStepType()
    {
        var yaml = """
            steps:
              - id: typo-only
                type: http.rest
                method: GET
                path: /orders
                target: orders-api
                bogusField: nope
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal("schema", error.Kind);
        Assert.Equal("/steps/0/bogusField", error.InstancePath);
        Assert.Equal(
            "[unevaluatedProperties] Unknown property 'bogusField' on step type 'http.rest'",
            error.Message);
    }

    [Fact]
    public void ValidateYaml_StepWithBothADefectAndATypo_ReportsOnlyTheDefect()
    {
        // The cascade. A step's fields are only "evaluated" by whichever if/then clause matched its
        // type, and a clause that FAILS withholds its annotations — so every legitimate field of a
        // defective step also presents as unknown. Reporting those would be a false accusation
        // against valid fields; the author fixes the real defect and sees the typo next round.
        var yaml = """
            steps:
              - id: typo-and-defect
                type: http.rest
                method: GET
                path: /orders
                taget: orders-api
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal("/steps/0", error.InstancePath);
        Assert.Contains("[required]", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unevaluatedProperties", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateYaml_OneDefectiveStepAndOneTypodStep_ScopesTheCascadePerStep()
    {
        // The cascade is scoped by the step's own instance path, never by list position or
        // document-wide state: step 0's defect must not silence step 1's genuine typo.
        var yaml = """
            steps:
              - id: defective
                type: http.rest
                target: api
                method: GET
              - id: typod
                type: http.rest
                target: api
                method: GET
                path: /x
                bogusField: nope
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.Equal(2, result.Errors.Count);
        Assert.Single(result.Errors, e => e.InstancePath == "/steps/0" && e.Message.Contains("[required]", StringComparison.Ordinal));
        Assert.Single(
            result.Errors,
            e => e.InstancePath == "/steps/1/bogusField" &&
                 e.Message == "[unevaluatedProperties] Unknown property 'bogusField' on step type 'http.rest'");
    }

    [Fact]
    public void ValidateYaml_StepWithAnUnknownTypeAndOtherFields_DoesNotAccuseThoseFieldsOfBeingUnknown()
    {
        // An unregistered type is itself a step-level defect: no if/then clause can claim the
        // step's properties, so every one of them would otherwise be reported as unknown on top of
        // the (correct, and far more useful) unknown-step-type finding.
        var yaml = """
            steps:
              - id: mystery
                type: totally.unknown
                target: something
                method: GET
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal("unknown-step-type", error.Kind);
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("Unknown property", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateYaml_UnknownKeyOnAClosedDependency_UsesTheAdditionalPropertiesWordingAndNamesTheDependency()
    {
        // additionalProperties: false is a DIFFERENT closure from the step surface's
        // unevaluatedProperties, reported with the same opaque blank-keyword message by
        // JsonSchema.Net. It must be rewritten too — an author shown "[] All values fail against
        // the false schema" has been told nothing — but it must not be swept into the
        // unevaluatedProperties cascade, which does not apply to it.
        var yaml = """
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  bogusKey: nope
            steps:
              - id: ok
                type: http.rest
                target: orders-api
                method: GET
                path: /x
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal(
            "[additionalProperties] Unknown property 'bogusKey' on dependency 'orders-db'",
            error.Message);
    }

    [Fact]
    public void ValidateYaml_StepSatisfyingItsOneOfAndAnyOf_DoesNotReportTheLosingBranches()
    {
        // mq-expect.azureservicebus carries a oneOf (queue | topic+subscription) and an anyOf
        // (expectPayloadContains | expectProperties). Both are SATISFIED here, but a composite
        // still reports each losing branch. Surfacing those would be pure noise; worse, counting
        // them as the step's "other errors" made the cascade hide the genuine typo below.
        var yaml = """
            steps:
              - id: asb
                type: mq-expect.azureservicebus
                target: bus
                queue: orders
                expectPayloadContains: "ok"
                taget: typo
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal(
            "[unevaluatedProperties] Unknown property 'taget' on step type 'mq-expect.azureservicebus'",
            error.Message);
    }

    [Fact]
    public void ValidateYaml_ServiceSatisfyingItsImageProjectChoice_DoesNotDemandTheOtherForm()
    {
        // $defs/service's image|project choice is satisfied by 'image'. The losing 'project' branch
        // must not be reported: its advice is not merely noise but a LOOP, because adding 'project'
        // alongside 'image' is itself forbidden by the schema.
        var yaml = """
            environment:
              services:
                orders-api:
                  image: orders:1
                  httpPort: 8080
            steps:
              - id: broken
                type: http.rest
                target: orders-api
                method: GET
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal("/steps/0", error.InstancePath);
        Assert.DoesNotContain("project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateYaml_SecurityBlockWithBothARequiredFailureAndAnUnknownKey_ReportsBoth()
    {
        // Both errors hang off the SAME evaluation node, so a node-level roll-up filter drops the
        // real one along with the aggregate. They describe unrelated defects and both must survive.
        var yaml = """
            environment:
              dependencies:
                events-kafka:
                  type: kafka
                  security:
                    bogusSecurityKey: nope
            steps:
              - id: ok
                type: http.rest
                target: orders-api
                method: GET
                path: /x
            """;

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.Equal(2, result.Errors.Count);
        Assert.Single(
            result.Errors,
            e => e.Message == "[required] Required properties [\"profile\",\"endpoint\"] are not present on dependency 'events-kafka'");
        Assert.Single(
            result.Errors,
            e => e.Message == "[unevaluatedProperties] Unknown property 'bogusSecurityKey' on dependency 'events-kafka'");
    }

    [Fact]
    public void ValidateYaml_ManyUnknownKeys_CompletesWellInsideTheValidationWorkerBudget()
    {
        // The closed step surface means the error count now tracks the document's key count rather
        // than the schema's shape. That made two pre-existing quadratic costs reachable — a full
        // YAML re-parse per error, and a pointer re-stringification per roll-up comparison — and a
        // 33 KB suite took ~14s against ValidationWorkerClient's 10-second budget. Guarded at a
        // deliberately loose 10s: this asserts the quadratic is gone, not a performance figure.
        var yaml = new System.Text.StringBuilder()
            .AppendLine("steps:")
            .AppendLine("  - id: s0")
            .AppendLine("    type: http.rest")
            .AppendLine("    target: api")
            .AppendLine("    method: GET")
            .AppendLine("    path: /x");
        for (var i = 0; i < 2000; i++)
        {
            yaml.AppendLine(CultureInfo.InvariantCulture, $"    junk{i}: v");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = SuiteValidator.ValidateYaml(yaml.ToString());
        stopwatch.Stop();

        Assert.Equal(2000, result.Errors.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Validating a 2 000-unknown-key suite took {stopwatch.Elapsed.TotalSeconds:F1}s, at or " +
            "beyond the validation worker's 10-second timeout — the quadratic has returned.");
    }

    [Fact]
    public void ValidateYaml_StepTypeWithRawAsciiControlCharacter_ReturnsSanitisedYamlParseErrorWithoutCrashing()
    {
        // A genuine discovery made while testing the above: a raw ASCII control byte (7 equals
        // ASCII bell) embedded in a quoted YAML scalar round-trips through YamlDotNet's
        // SerializerBuilder().JsonCompatible() re-emission as an UNESCAPED control byte inside
        // the JSON text it produces — invalid JSON, which System.Text.Json.JsonDocument.Parse
        // then rejects with a JsonException. Before this fix, that exception was uncaught and
        // escaped ValidateYaml's "never throws" contract entirely; it must now surface as a
        // structured, sanitised yaml-parse error instead.
        var disallowedByte = ((char)7).ToString();
        var yaml = $"steps:\n  - id: s1\n    type: \"totally.unknown{disallowedByte}\"\n";

        var result = SuiteValidator.ValidateYaml(yaml);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("yaml-parse", error.Kind);
        Assert.DoesNotContain(disallowedByte, error.Message, StringComparison.Ordinal);
        foreach (var c in error.Message)
        {
            Assert.InRange(c, (char)0x20, (char)0x7E);
        }
    }

    // ── B1: untrusted YAML that could crash the process must be rejected, not parsed ───────────

    [Fact]
    public void ValidateYaml_DeeplyNestedFlowBrackets_ReturnsTooDeepWithoutCrashing()
    {
        // The proven native-StackOverflowException shape from the security review. The guard
        // must reject this from the raw text alone, strictly before YamlToJsonConverter (and so
        // YamlDotNet) ever sees it — if it didn't, this test process itself would be the one that
        // crashes, uncatchably, since a StackOverflowException cannot be handled by any try/catch.
        var deeplyNested = new string('[', 20_000) + new string(']', 20_000);

        var result = SuiteValidator.ValidateYaml(deeplyNested);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("too-deep", error.Kind);
    }

    [Fact]
    public void ValidateYaml_BillionLaughsShapedDocument_ReturnsAliasLimitWithoutExpanding()
    {
        // A handful of anchors, each aliased several times by the next: the proven "small file,
        // huge expansion" shape. Rejected by raw token count before YamlToJsonConverter (and so
        // SerializerBuilder().JsonCompatible().Serialize(), which is what would actually re-expand
        // every alias) ever runs.
        const string billionLaughs = """
            a0: &a0 "x"
            a1: &a1 [*a0, *a0, *a0, *a0, *a0, *a0, *a0, *a0]
            a2: &a2 [*a1, *a1, *a1, *a1, *a1, *a1, *a1, *a1]
            a3: &a3 [*a2, *a2, *a2, *a2, *a2, *a2, *a2, *a2]
            steps: []
            """;

        var result = SuiteValidator.ValidateYaml(billionLaughs);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("alias-limit", error.Kind);
    }

    [Fact]
    public void ValidateFile_OversizedFile_ReturnsTooLargeWithoutReadingItsContent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        try
        {
            using (var stream = File.Create(tempPath))
            {
                stream.SetLength(YamlSafetyGuard.MaxSuiteSizeBytes + 1);
            }

            var result = SuiteValidator.ValidateFile(tempPath);

            Assert.False(result.Valid);
            var error = Assert.Single(result.Errors);
            Assert.Equal("too-large", error.Kind);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── M2: a network/UNC path must never reach a filesystem call ──────────────────────────────

    [Theory]
    [InlineData(@"\\attacker-host\share\suite.e2e.yaml")]
    [InlineData("//attacker-host/share/suite.e2e.yaml")]
    public void ValidateFile_UncPath_ReturnsInvalidPathWithoutTouchingTheFilesystem(string uncPath)
    {
        var result = SuiteValidator.ValidateFile(uncPath);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("invalid-path", error.Kind);
    }

    // ── CheckFastRejects: the validate_suite orchestrator's no-spawn-needed pre-checks ─────────

    [Fact]
    public void CheckFastRejects_MissingFile_ReturnsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");

        var error = SuiteValidator.CheckFastRejects(missingPath);

        Assert.NotNull(error);
        Assert.Equal("file-not-found", error!.Kind);
    }

    [Fact]
    public void CheckFastRejects_UncPath_ReturnsInvalidPath()
    {
        var error = SuiteValidator.CheckFastRejects(@"\\attacker-host\share\suite.e2e.yaml");

        Assert.NotNull(error);
        Assert.Equal("invalid-path", error!.Kind);
    }

    [Fact]
    public void CheckFastRejects_OversizedFile_ReturnsTooLargeWithoutReadingItsContent()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        try
        {
            using (var stream = File.Create(tempPath))
            {
                stream.SetLength(YamlSafetyGuard.MaxSuiteSizeBytes + 1);
            }

            var error = SuiteValidator.CheckFastRejects(tempPath);

            Assert.NotNull(error);
            Assert.Equal("too-large", error!.Kind);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CheckFastRejects_ExistingValidFile_ReturnsNull()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.e2e.yaml");
        File.WriteAllText(tempPath, """
            steps:
              - id: check-health
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """);

        try
        {
            var error = SuiteValidator.CheckFastRejects(tempPath);

            Assert.Null(error);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
