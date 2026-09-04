using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Schema;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Schema;

/// <summary>
/// US-S2-01: <c>get_schema</c>'s pipeline — offline-first service from the embedded vendored
/// composed schema, plus the live cross-verification against <c>vouchfx schema</c> that
/// <see cref="LiveSchemaDocument"/> (fully implemented since REQ-010 but never constructed by
/// <see cref="VouchfxMcpServerRegistration"/> until this story) finally has a caller for.
/// </summary>
/// <remarks>
/// <para>
/// <b>get_schema is CLI-OPTIONAL, not CLI-backed.</b> That distinction is the point of the first
/// two tests here: unlike <c>list_step_types</c>/<c>plan_coverage</c>, a missing or mismatched CLI
/// does not fail this tool — it degrades to exactly the answer the embedded, drift-gated vendored
/// schema already gives, the same way <c>validate_suite</c> and <c>search_docs</c> already work
/// offline. What the CLI adds when present is a CHECK, never the content.
/// </para>
/// <para>
/// Every CLI here is a <see cref="FakeVouchfxCli"/>, so nothing in this class depends on a real
/// <c>vouchfx</c> install — see <see cref="RealGetSchemaAgainstPinnedCliTests"/> for the self-gating
/// real-CLI counterpart.
/// </para>
/// </remarks>
public class GetSchemaOrchestratorTests
{
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", new string('a', 40));
    private static readonly string PinCliVersion = CliVersionNormaliser.Normalise(Pin.Version);

    /// <summary>
    /// Used once, to re-emit the vendored schema with DIFFERENT formatting than the committed file
    /// carries. Cached in a field because CA1869 (rightly) forbids allocating a
    /// <see cref="JsonSerializerOptions"/> per serialisation.
    /// </summary>
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [Fact]
    public async Task GetSchemaAsync_WithNoCliInstalled_ServesTheVendoredSchemaAndSucceeds()
    {
        using var live = CreateLiveSchema(FakeVouchfxCli.NotFound());
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: null, format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.Equal("full", completed.Result.Section);
        Assert.Equal(VendoredSchemaVersion.Value, completed.Result.SchemaVersion);
        Assert.NotNull(completed.Result.JsonSchema);
        Assert.Null(completed.Result.Summary);

        // Byte-for-byte the embedded resource, once both sides are canonicalised (the vendored file
        // is pretty-printed; JsonElement.GetRawText is not re-indented, so this is a content check
        // rather than a whitespace one).
        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.Canonicalise(completed.Result.JsonSchema!.Value.GetRawText()));

        // A missing CLI is not a finding: offline is a supported mode, not a degraded one.
        Assert.Null(completed.Result.Diagnostics);
    }

    [Fact]
    public async Task GetSchemaAsync_WithAVersionMismatchedCli_StillServesTheVendoredSchemaWithoutADiagnostic()
    {
        // The pin handshake fails closed inside LiveSchemaDocument, so no cross-verification runs —
        // and that is a silent, successful offline answer, not a mismatch report. Reporting a
        // mismatch here would be a lie: nothing was compared.
        using var live = CreateLiveSchema(FakeVouchfxCli.ReportingVersion("9.9.9"));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: "json-schema", CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.NotNull(completed.Result.JsonSchema);
        Assert.Null(completed.Result.Diagnostics);
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTheLiveSchemaAgreesWithTheVendoredOne_EmitsNoDiagnostic()
    {
        using var live = CreateLiveSchema(
            FakeVouchfxCli.WithExports(PinCliVersion, listJson: "{}", schemaJson: VendoredComposedSchema.RawJson));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.Null(completed.Result.Diagnostics);
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTheLiveSchemaDiffersOnlyInFormatting_EmitsNoDiagnostic()
    {
        // THE reason the comparison is canonical rather than byte-exact: CLAUDE.md and
        // vendored/README.md both record that `vouchfx schema` output differs from the committed
        // vendored file in CRLF/trailing-newline alone, which is why the drift gate refuses to
        // regenerate vendored/ from it. A byte comparison here would therefore report a mismatch on
        // every machine with the correct CLI installed — the loudest possible false positive.
        using var vendored = VendoredComposedSchema.Parse();
        var reformatted = JsonSerializer.Serialize(vendored.RootElement, IndentedJson) + "\r\n";

        using var live = CreateLiveSchema(
            FakeVouchfxCli.WithExports(PinCliVersion, listJson: "{}", schemaJson: reformatted));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.Null(completed.Result.Diagnostics);
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTheLiveSchemaDiffersInContent_ReportsADiagnosticAndStillServesTheVendoredSchema()
    {
        const string DivergentSchema = """
            { "x-vouchfx-schema-version": "v2", "type": "object", "properties": {} }
            """;

        using var live = CreateLiveSchema(
            FakeVouchfxCli.WithExports(PinCliVersion, listJson: "{}", schemaJson: DivergentSchema));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);

        var diagnostic = Assert.Single(completed.Result.Diagnostics!);
        Assert.Equal(VfxCodeCatalogue.LiveSchemaMismatch, diagnostic.Code);
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Equal(VfxCodeCatalogue.DocsUrlFor(VfxCodeCatalogue.LiveSchemaMismatch), diagnostic.DocsUrl);

        // "Never silently prefers one source over the other": the served content is still the
        // vendored schema (deterministic, drift-gated, offline-reproducible) AND the divergence is
        // stated in the result, so a host can see both facts.
        Assert.Equal(
            SchemaJsonCanonicaliser.Canonicalise(VendoredComposedSchema.RawJson),
            SchemaJsonCanonicaliser.Canonicalise(completed.Result.JsonSchema!.Value.GetRawText()));
        Assert.Contains("vendored", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSchemaAsync_WhenTheLiveExportIsNotWellFormedJson_ReportsExactlyOneMismatchDiagnostic()
    {
        // The orchestrator's unparseable-live-output arm, made deterministic. LiveSchemaDocument's
        // own shape check only requires a leading '{', so this text reaches the canonicaliser and
        // fails there — which is the real-world case documented in
        // RealGetSchemaAgainstPinnedCliTests: on a non-UTF-8 Windows console, code-page transcoding
        // injects a raw control byte inside a JSON string and the live document does not parse at
        // all. Until this test existed that arm was covered ONLY by the self-gating real-CLI test,
        // i.e. not at all on a machine without the pinned engine installed.
        using var live = CreateLiveSchema(
            FakeVouchfxCli.WithExports(PinCliVersion, listJson: "{}", schemaJson: "{ not json"));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: null, CancellationToken.None);

        // Unparseable IS a divergence, reported in the same terms as any other — never swallowed,
        // and never escalated into a tool failure: the vendored schema still came back.
        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.NotNull(completed.Result.JsonSchema);

        var diagnostic = Assert.Single(completed.Result.Diagnostics!);
        Assert.Equal(VfxCodeCatalogue.LiveSchemaMismatch, diagnostic.Code);
        Assert.Equal("warning", diagnostic.Severity);

        // The malformed text is untrusted subprocess output and is never echoed into the message.
        Assert.DoesNotContain("not json", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSchemaAsync_MismatchDiagnostic_QuotesNoSchemaContentIntoItsMessage()
    {
        // The live document is untrusted process output. The diagnostic reports THAT the two
        // disagree, never a diff of them — a diff would relay unbounded engine-controlled text into
        // an agent-facing message, which is exactly what BoundedStreamReader/TextSanitiser exist to
        // prevent elsewhere in this codebase.
        var divergent = "{ \"x-vouchfx-schema-version\": \"v1\", \"secretish\": \"" + new string('q', 5000) + "\" }";

        using var live = CreateLiveSchema(
            FakeVouchfxCli.WithExports(PinCliVersion, listJson: "{}", schemaJson: divergent));
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(section: "full", format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        var diagnostic = Assert.Single(completed.Result.Diagnostics!);

        Assert.DoesNotContain("secretish", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('q', 100), diagnostic.Message, StringComparison.Ordinal);
        Assert.True(diagnostic.Message.Length < 1024, "The mismatch message must stay small and bounded.");
    }

    [Fact]
    public async Task GetSchemaAsync_StepSection_ReturnsOnlyThatStepTypesSubtreeAndEchoesTheSection()
    {
        using var live = CreateLiveSchema(FakeVouchfxCli.NotFound());
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync("step:mq-expect.kafka", format: null, CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.Equal("step:mq-expect.kafka", completed.Result.Section);

        var raw = completed.Result.JsonSchema!.Value.GetRawText();
        Assert.Contains("mq-expect.kafka", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("http.rest", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSchemaAsync_UnknownStepProvider_ReturnsSectionNotFoundRatherThanAnEmptySuccess()
    {
        using var live = CreateLiveSchema(FakeVouchfxCli.NotFound());
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync(
            "step:mq-expect.nonexistent-provider", format: null, CancellationToken.None);

        var notFound = Assert.IsType<GetSchemaOutcome.SectionNotFound>(outcome);
        Assert.Contains("mq-expect.nonexistent-provider", notFound.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSchemaAsync_UnrecognisedFormat_ReturnsInvalidArgument()
    {
        using var live = CreateLiveSchema(FakeVouchfxCli.NotFound());
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync("full", format: "yaml", CancellationToken.None);

        var invalid = Assert.IsType<GetSchemaOutcome.InvalidArgument>(outcome);
        Assert.Contains("format", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSchemaAsync_SummaryFormat_ReturnsOnlyTheSummary()
    {
        using var live = CreateLiveSchema(FakeVouchfxCli.NotFound());
        var orchestrator = new GetSchemaOrchestrator(live);

        var outcome = await orchestrator.GetSchemaAsync("metadata", "summary", CancellationToken.None);

        var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
        Assert.Null(completed.Result.JsonSchema);
        Assert.NotNull(completed.Result.Summary);
        Assert.NotEmpty(completed.Result.Summary!);
    }

    [Fact]
    public async Task GetSchemaAsync_CrossVerifiesOnceAndThenServesFromTheCachedLiveLoad()
    {
        // LiveSchemaDocument caches a successful load for the process lifetime; a second call must
        // not re-spawn `vouchfx schema`. Counting invocations is the only way to see that from
        // outside, and it matters: get_schema is a cheap, frequently-called authoring tool.
        var schemaInvocations = 0;
        var cli = FakeVouchfxCli.WithRunHandler(
            PinCliVersion,
            args =>
            {
                if (args.Count == 1 && args[0] == "schema")
                {
                    schemaInvocations++;
                    return VendoredComposedSchema.RawJson;
                }

                return args.Count == 1 && args[0] == "--version" ? PinCliVersion : null;
            });

        using var live = CreateLiveSchema(cli);
        var orchestrator = new GetSchemaOrchestrator(live);

        await orchestrator.GetSchemaAsync("full", null, CancellationToken.None);
        await orchestrator.GetSchemaAsync("metadata", null, CancellationToken.None);

        Assert.Equal(1, schemaInvocations);
    }

    [Fact]
    public async Task GetSchemaAsync_WithAVersionMismatchedCli_ProbesItOnceForTheWholeProcessNotOncePerCall()
    {
        // The counterpart to the test above, for the FAILING side of the probe. LiveSchemaDocument
        // deliberately caches only Ok — right for the five CLI-BACKED tools, whose only answer is
        // the engine's, and which must start working the moment one is installed. get_schema is the
        // opposite case: it already has a complete offline answer, so an uncached failure means
        // every single call re-runs `vouchfx --version` (or, with no CLI at all, re-walks PATH) to
        // re-learn a fact that has not changed — ~100-300 ms each, serialised behind that type's
        // load gate, on a cheap and frequently-called authoring tool. The orchestrator therefore
        // memoises the cross-verification OUTCOME, whatever it turns out to be.
        var cli = new VersionProbeCountingCli(FakeVouchfxCli.ReportingVersion("9.9.9"));

        using var live = CreateLiveSchema(cli);
        var orchestrator = new GetSchemaOrchestrator(live);

        for (var call = 0; call < 4; call++)
        {
            var outcome = await orchestrator.GetSchemaAsync("full", null, CancellationToken.None);

            // Still a clean offline answer every time — memoising the failure must not turn a
            // supported mode into a finding.
            var completed = Assert.IsType<GetSchemaOutcome.Completed>(outcome);
            Assert.Null(completed.Result.Diagnostics);
        }

        Assert.Equal(1, cli.VersionProbes);
    }

    /// <summary>
    /// Counts <c>vouchfx --version</c> handshakes, which <see cref="FakeVouchfxCli"/> serves from a
    /// dedicated method rather than through its run handler — so a counting decorator is the only
    /// way to observe the pin probe from outside.
    /// </summary>
    private sealed class VersionProbeCountingCli : IVouchfxCli
    {
        private readonly IVouchfxCli _inner;

        public VersionProbeCountingCli(IVouchfxCli inner)
        {
            _inner = inner;
        }

        public int VersionProbes { get; private set; }

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default)
        {
            VersionProbes++;
            return _inner.TryGetVersionOutputAsync(cancellationToken);
        }

        public Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            CancellationToken cancellationToken = default) =>
            _inner.TryRunStdoutAsync(arguments, maxStreamBytes, cancellationToken);

        public Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            _inner.RunAsync(arguments, maxStreamBytes, timeout, cancellationToken);
    }

    /// <summary>
    /// Builds the live-schema loader the orchestrator cross-verifies through, wired to
    /// <paramref name="cli"/> and this class's own <see cref="Pin"/> — never the real
    /// <c>ENGINE_PIN</c> file and never the real CLI.
    /// </summary>
    /// <remarks>
    /// Returned (rather than folded into a <c>CreateOrchestrator</c> helper) so each test owns the
    /// <see cref="IDisposable"/> loader in a <c>using</c>. The orchestrator deliberately does NOT
    /// dispose it: in production the loader is process-scoped and owned by
    /// <see cref="VouchfxMcpServerRegistration"/>, so an orchestrator that disposed it would be
    /// disposing something it does not own.
    /// </remarks>
    private static LiveSchemaDocument CreateLiveSchema(IVouchfxCli cli) =>
        new(cli, new CliPinVerifier(cli, Pin));
}
