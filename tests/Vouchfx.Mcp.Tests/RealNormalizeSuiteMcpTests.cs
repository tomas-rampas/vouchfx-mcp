using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S2-04 end to end over the real MCP wire: <c>normalize_suite</c> returns canonical YAML plus a
/// full <c>validate_suite</c>-shaped validation result, and the SERVER never writes the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>The read-only proof here is behavioural and deliberately narrow</b> — one call against a
/// suite in a directory of its own, with the file's bytes, its last-write time, AND the directory's
/// whole entry list compared before and after (a tool that wrote its output to a SIBLING would
/// otherwise pass every check about the named file). Its structural companion,
/// <see cref="ReadOnlySourceGuardTests"/>, is what proves there is no code path that COULD write;
/// the pairing is the same one <see cref="SecretHygieneSourceGuardTests"/> and
/// <see cref="RealSecretHygieneMcpTests"/> already use for secret hygiene, for the same reason.
/// </para>
/// <para>
/// <b>Normalization is opt-in</b> (<c>normalize: true</c>) because it drops comments on the pinned
/// YamlDotNet — spec open decision #2, closed as outcome (b); see
/// <c>Vouchfx.Mcp.Normalization.SuiteNormalizer</c>'s remarks for the measured evidence. Most tests
/// below therefore pass the flag explicitly; <see cref="NormalizeSuite_ByDefault_ReturnsTheValidationButNoCanonicalYaml"/>
/// is the one that pins the default.
/// </para>
/// </remarks>
public class RealNormalizeSuiteMcpTests
{
    /// <summary>
    /// A schema-VALID suite written in deliberately non-canonical order: root keys reversed, a step's
    /// keys shuffled, and a flow sequence where the canonical form uses a block one. Valid on purpose
    /// — a suite the schema rejected would let a test claim the canonical form was produced when the
    /// interesting assertions were really riding on an error path.
    /// </summary>
    private const string NonCanonicalSuite = """
        steps:
          - path: /orders
            method: GET
            type: http.rest
            target: orders-api
            id: call
        variables:
          baseUrl: 'https://api.example.test'
        metadata:
          tags: [smoke]
          name: checkout
          owner: platform
        """;

    [Fact]
    public async Task NormalizeSuite_WithNormalizeTrue_ReturnsCanonicalYamlAndTheValidateSuiteShapedValidation()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?> { ["yaml"] = NonCanonicalSuite, ["normalize"] = true },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        Assert.Equal(
            ["normalizedYaml", "commentsDropped", "normalizationRefused", "validation", "meta"],
            payload.EnumerateObject().Select(p => p.Name));

        var normalized = payload.GetProperty("normalizedYaml").GetString();
        Assert.False(string.IsNullOrWhiteSpace(normalized));

        // Spec open decision #2, outcome (b): the comment loss is stated on the OUTPUT, not only in
        // the tool description — true on exactly the responses that carry canonical text.
        Assert.True(payload.GetProperty("commentsDropped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("normalizationRefused").ValueKind);

        // Canonical key order, derived from the vendored schema's own root `properties` order
        // (metadata, environment, variables, steps) — the exact inverse of how the source is written.
        Assert.StartsWith("metadata:\n", normalized, StringComparison.Ordinal);
        Assert.True(
            normalized!.IndexOf("variables:", StringComparison.Ordinal)
                < normalized.IndexOf("steps:", StringComparison.Ordinal),
            $"variables must precede steps in the canonical form. Got:\n{normalized}");

        // And inside the step: the general fields the step definition declares come before the
        // http.rest branch's own, so a step still leads with its identity.
        Assert.Contains(
            "  - id: call\n    type: http.rest\n    target: orders-api\n    method: GET\n    path: /orders\n",
            normalized,
            StringComparison.Ordinal);

        // The validation field is the FULL validate_suite payload shape, minus meta (which the
        // StructuredToolResult choke point stamps once, at the top level, never per nested object).
        var validation = payload.GetProperty("validation");
        Assert.Equal(
            ["valid", "errors", "semanticDiagnostics", "semanticDiagnosticsTruncated", "summary", "level"],
            validation.EnumerateObject().Select(p => p.Name));
        Assert.False(validation.TryGetProperty("meta", out _));
        Assert.Equal("full", validation.GetProperty("level").GetString());
        Assert.Empty(validation.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, validation.GetProperty("summary").GetProperty("steps").GetInt32());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_ByDefault_ReturnsTheValidationButNoCanonicalYaml()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?> { ["yaml"] = NonCanonicalSuite },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        // Outcome (b) of spec open decision #2: the lossy half is off unless asked for, and the
        // validation half — which loses nothing — is always there.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("normalizedYaml").ValueKind);
        Assert.Empty(payload.GetProperty("validation").GetProperty("errors").EnumerateArray());

        // Nothing was normalized, so nothing was lost, and nothing was refused. All three fields
        // agree — which is what makes them tellable apart from the refusal case.
        Assert.False(payload.GetProperty("commentsDropped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("normalizationRefused").ValueKind);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_IsIdempotentAcrossTheMcpBoundary()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var first = await NormalizeAsync(harness, NonCanonicalSuite, cts.Token);
        var second = await NormalizeAsync(harness, first, cts.Token);

        // US-S2-04's second Gherkin scenario, driven exactly as written: the first call's own
        // normalizedYaml is fed back in as inline yaml, and the second call's output must be
        // byte-identical to it.
        Assert.Equal(first, second);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_GivenAPathOnDisk_LeavesTheFileByteIdenticalAndCanonicalisesOnlyTheReturnedText()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Its own directory, so the sibling-file check below is exact rather than a claim about the
        // whole OS temp directory, which anything on the machine may be writing to concurrently.
        var suiteDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"normalize-readonly-{Guid.NewGuid():N}"));
        var suitePath = Path.Combine(suiteDirectory.FullName, "suite.e2e.yaml");
        var originalBytes = Encoding.UTF8.GetBytes(NonCanonicalSuite);
        await File.WriteAllBytesAsync(suitePath, originalBytes, cts.Token);
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(suitePath);
        var originalDirectoryContents = Directory
            .GetFileSystemEntries(suiteDirectory.FullName, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        try
        {
            await using var harness = await McpTestHarness.StartAsync(cts.Token);

            var result = await harness.Client.CallToolAsync(
                "normalize_suite",
                new Dictionary<string, object?> { ["path"] = suitePath, ["normalize"] = true },
                cancellationToken: cts.Token);

            Assert.False(result.IsError ?? false);
            var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
            var normalized = payload.GetProperty("normalizedYaml").GetString();

            // The result genuinely differs from the file — otherwise "the file is unchanged" would be
            // trivially true and this test would prove nothing (the first Gherkin scenario's own
            // "differs from the file's on-disk bytes in key order" clause).
            Assert.NotNull(normalized);
            Assert.NotEqual(NonCanonicalSuite, normalized);
            Assert.StartsWith("metadata:\n", normalized, StringComparison.Ordinal);

            // …and the file itself is byte-identical, with its last-write time untouched.
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(suitePath, cts.Token));
            Assert.Equal(originalWriteTimeUtc, File.GetLastWriteTimeUtc(suitePath));

            // "The named file is unchanged" is not the whole invariant. A tool that wrote its output
            // to `suite.e2e.yaml.normalized`, or dropped a temp file beside the suite, would satisfy
            // every assertion above — so the directory's contents are compared too.
            Assert.Equal(
                originalDirectoryContents,
                Directory.GetFileSystemEntries(suiteDirectory.FullName, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(suiteDirectory.FullName, recursive: true);
        }

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_WithASecretLiteral_SurfacesVfxD1207AndAnInvalidVerdict()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?>
            {
                // Schema-VALID on purpose: `valid: false` below must be the secret literal's doing,
                // not a schema error's, or the assertion proves nothing about VFX-D-1207.
                ["yaml"] = """
                    steps:
                      - id: call
                        type: http.rest
                        target: orders-api
                        method: GET
                        path: /orders
                        headers:
                          authorization: "AKIAIOSFODNN7EXAMPLE"
                    """,
                ["normalize"] = true,
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.");
        var validation = payload.GetProperty("validation");

        Assert.Contains(
            validation.GetProperty("semanticDiagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "VFX-D-1207");
        Assert.False(validation.GetProperty("valid").GetBoolean());

        // BOTH halves of the story's pairing, not just the diagnostic. The contract is "never
        // canonical YAML for a secret-carrying suite WITHOUT the diagnostic" — withholding the text
        // is not the contract either, and asserting only the diagnostic would pass just as happily if
        // the tool had started refusing to normalize such suites at all.
        Assert.Equal(JsonValueKind.String, payload.GetProperty("normalizedYaml").ValueKind);
        Assert.Contains(
            "AKIAIOSFODNN7EXAMPLE",
            payload.GetProperty("normalizedYaml").GetString()!,
            StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_WhenTheCanonicalTextWouldNotReParse_ReturnsNullAndTheRefusalReason()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // Schema-VALID, and measured returning unparseable canonical YAML beside `valid: true` before
        // the emission gate existed: an alias used as a mapping KEY is emitted as `*k:`, which does
        // not parse back. End to end over the wire, because this is the boundary a host reads.
        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?>
            {
                ["yaml"] = """
                    metadata:
                      name: alias-key
                      owner: platform
                      tags: [smoke]
                    steps:
                      - id: call
                        type: http.rest
                        target: orders-api
                        method: POST
                        path: /orders
                        body:
                          anchor: &k v
                          nested:
                            *k : value
                    """,
                ["normalize"] = true,
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("normalizedYaml").ValueKind);
        Assert.Equal(
            "canonical-text-did-not-re-parse",
            payload.GetProperty("normalizationRefused").GetString());
        Assert.False(payload.GetProperty("commentsDropped").GetBoolean());

        // The verdict is untouched by the refusal — nothing is wrong with the SUITE.
        Assert.True(payload.GetProperty("validation").GetProperty("valid").GetBoolean());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_IgnoresALevelArgument_AndAlwaysValidatesAtFull()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        // `level` is not a parameter of this tool. A host sending one anyway must not be able to turn
        // the semantic pass — and with it the VFX-D-1207 secret gate — off on the one result a host
        // is invited to write over the author's file. The MCP layer drops the unknown argument; this
        // asserts the OUTCOME of that, which is what actually matters.
        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?>
            {
                ["yaml"] = """
                    steps:
                      - id: call
                        type: http.rest
                        target: orders-api
                        method: GET
                        path: /orders
                        headers:
                          authorization: "AKIAIOSFODNN7EXAMPLE"
                    """,
                ["normalize"] = true,
                ["level"] = "schema",
            },
            cancellationToken: cts.Token);

        Assert.False(result.IsError ?? false);
        var validation = (result.StructuredContent
            ?? throw new InvalidOperationException("Expected StructuredContent.")).GetProperty("validation");

        Assert.Equal("full", validation.GetProperty("level").GetString());
        Assert.Contains(
            validation.GetProperty("semanticDiagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == "VFX-D-1207");

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_ForASchemaInvalidSuite_IsASuccessfulCallCarryingTheErrors()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?>
            {
                ["yaml"] = "steps:\n  - id: a\n    type: not-a-real.type\n",
                ["normalize"] = true,
            },
            cancellationToken: cts.Token);

        // Invalid is DATA, not a tool error — the same split validate_suite already makes.
        Assert.False(result.IsError ?? false);
        var payload = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.False(payload.GetProperty("validation").GetProperty("valid").GetBoolean());

        // A suite the schema rejects is still parseable text, so it still has a canonical form — the
        // formatting pass has no reason to withhold it, and an author fixing the suite wants both.
        Assert.Equal(JsonValueKind.String, payload.GetProperty("normalizedYaml").ValueKind);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NormalizeSuite_WithBothOrNeitherOfPathAndYaml_IsAToolErrorInTheSchemaValidationRange(
        bool sendPath, bool sendYaml)
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var arguments = new Dictionary<string, object?>();
        if (sendPath)
        {
            arguments["path"] = "suite.e2e.yaml";
        }

        if (sendYaml)
        {
            arguments["yaml"] = "steps: []";
        }

        var result = await harness.Client.CallToolAsync("normalize_suite", arguments, cancellationToken: cts.Token);

        Assert.True(result.IsError ?? false);
        var error = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");

        // The SAME code validate_suite reports for the same condition — reused, not minted afresh.
        Assert.Equal(VfxCodeCatalogue.AmbiguousSuiteInput, error.GetProperty("code").GetString());
        Assert.Contains("normalize_suite", error.GetProperty("message").GetString()!, StringComparison.Ordinal);

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_WithTheWorkerStdinMarkerAsAPath_IsRefusedRatherThanSilentlyReinterpreted()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?> { ["path"] = ValidationWorkerProtocol.InlineYamlArgument },
            cancellationToken: cts.Token);

        Assert.True(result.IsError ?? false);
        var error = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal(VfxCodeCatalogue.AmbiguousSuiteInput, error.GetProperty("code").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    [Fact]
    public async Task NormalizeSuite_ForAMissingFile_IsAToolErrorBecauseValidityWasNeverDetermined()
    {
        using var consoleOut = new ConsoleOutCapture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?>
            {
                ["path"] = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.e2e.yaml"),
            },
            cancellationToken: cts.Token);

        Assert.True(result.IsError ?? false);
        var error = result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent.");
        Assert.Equal(VfxCodeCatalogue.SuiteFileNotFound, error.GetProperty("code").GetString());

        Assert.Empty(consoleOut.Writer.ToString());
    }

    private static async Task<string> NormalizeAsync(McpTestHarness harness, string yaml, CancellationToken cancellationToken)
    {
        var result = await harness.Client.CallToolAsync(
            "normalize_suite",
            new Dictionary<string, object?> { ["yaml"] = yaml, ["normalize"] = true },
            cancellationToken: cancellationToken);

        Assert.False(result.IsError ?? false);

        return (result.StructuredContent ?? throw new InvalidOperationException("Expected StructuredContent."))
            .GetProperty("normalizedYaml").GetString()
            ?? throw new InvalidOperationException("Expected normalizedYaml.");
    }
}
