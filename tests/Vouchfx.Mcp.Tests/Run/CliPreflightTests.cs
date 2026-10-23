using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Contracts;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests.Run;

public class CliPreflightTests
{
    [Fact]
    public async Task ValidateAsync_OverlongReportedPath_ReturnsUnusableReportWithoutThrowing()
    {
        await WithSuiteAsync(async path =>
        {
            var report = Report(new string('a', 40_000) + ".e2e.yaml", true);
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", _ => CliInvocationResult.Completed(0, report, ""));
            Vouchfx.Mcp.Validation.ValidateSuiteResult? result = null;

            var exception = await Record.ExceptionAsync(async () =>
                result = await CliSuitePreflight.ValidateAsync(cli, path, "1.2.0", null, CancellationToken.None));

            Assert.True(exception is null, "An overlong reported path must fail closed without throwing.");
            Assert.NotNull(result);
            Assert.False(result.Valid);
            Assert.Equal(VfxCodeCatalogue.ValidationWorkerFailed, Assert.Single(result.Errors).Code);
        });
    }

    [Fact]
    public async Task ValidateAsync_ManyCliFindings_PreservesExactWireOmissionCount()
    {
        await WithSuiteAsync(async path =>
        {
            var report = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                engineVersion = "1.2.0",
                scenarios = new[] { new { path, valid = false, diagnostics = Enumerable.Range(0, 500).Select(_ => new { stage = "schema", message = "private-marker" }) } },
                valid = false,
            });
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", _ => CliInvocationResult.Completed(4, report, ""));
            var result = await CliSuitePreflight.ValidateAsync(cli, path, "1.2.0", null, CancellationToken.None);

            // Keep the retained legacy first-suite field small, so the second suite's capped
            // entry fits and its exact omitted count remains observable on the wire.
            var payload = Vouchfx.Mcp.Tools.RunSuiteTool.BuildInvalidSuitesPayload(
                new RunSuiteOutcome.SuiteInvalid([
                    new PreflightSuiteFailure(path, new(false, [result.Errors[0]])),
                    new PreflightSuiteFailure(path, result),
                ]));

            Assert.Equal(500, result.Errors.Count);
            Assert.Equal(2, payload.InvalidSuites.Count);
            var invalid = payload.InvalidSuites[1];
            Assert.Equal(10, invalid.Errors.Count);
            Assert.Equal(490, invalid.OmittedErrorCount);
        });
    }

    [Theory]
    [InlineData("schema", VfxCodeCatalogue.SchemaViolation)]
    [InlineData("parse", VfxCodeCatalogue.YamlParseError)]
    [InlineData("pipeline", VfxCodeCatalogue.SuiteInvalid)]
    [InlineData("roslyn", VfxCodeCatalogue.SuiteInvalid)]
    public async Task ValidateAsync_InvalidCliReport_ReturnsFixedStageDiagnosticWithoutRawText(string stage, string code)
    {
        await WithSuiteAsync(async path =>
        {
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", _ => CliInvocationResult.Completed(
                4, Report(path, false, stage), "private-stderr-marker"));

            var result = await CliSuitePreflight.ValidateAsync(cli, path, "1.2.0", null, CancellationToken.None);

            Assert.False(result.Valid);
            var error = Assert.Single(result.Errors);
            Assert.Equal(code, error.Code);
            Assert.True(!error.Message.Contains("private", StringComparison.Ordinal), "CLI text must not reach MCP diagnostics.");
            Assert.True(!error.Message.Contains(path, StringComparison.Ordinal), "Host paths must not reach MCP diagnostics.");
            Assert.Contains(stage, error.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("json")]
    [InlineData("version")]
    [InlineData("schema")]
    [InlineData("no-scenarios")]
    [InlineData("wrong-path")]
    [InlineData("exit")]
    [InlineData("contradictory")]
    [InlineData("usage")]
    public async Task ValidateAsync_UnusableCliReport_FailsClosed(string fault)
    {
        await WithSuiteAsync(async path =>
        {
            var report = fault switch
            {
                "empty" => "",
                "json" => "{",
                "version" => Report(path, true).Replace("1.2.0", "1.1.0", StringComparison.Ordinal),
                "schema" => Report(path, true).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
                "no-scenarios" => "{\"schemaVersion\":1,\"engineVersion\":\"1.2.0\",\"valid\":true,\"scenarios\":[]}",
                "wrong-path" => Report(path + ".other", true),
                "contradictory" => Report(path, false).Replace("\"valid\":false}", "\"valid\":true}", StringComparison.Ordinal),
                _ => Report(path, true),
            };
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", _ => CliInvocationResult.Completed(
                fault == "usage" ? 2 : fault == "exit" ? 4 : 0, report, "private-stderr-marker"));

            var result = await CliSuitePreflight.ValidateAsync(cli, path, "1.2.0", null, CancellationToken.None);

            Assert.False(result.Valid);
            Assert.Equal(VfxCodeCatalogue.ValidationWorkerFailed, Assert.Single(result.Errors).Code);
        });
    }

    [Fact]
    public async Task RunAsync_ValidationFailure_ReleasesClaimWithoutRegisteringRun()
    {
        await WithSuiteAsync(async path =>
        {
            var attempts = 0;
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", _ => ++attempts == 1
                ? CliInvocationResult.TimedOut
                : CliInvocationResult.Completed(0, Report(path, true), ""));
            var registry = new InMemoryRunRegistry();
            var runner = FakeSuiteRunner.Succeeding([], "", 0);
            var orchestrator = new RunSuiteOrchestrator(
                new CliPinVerifier(cli, new EnginePin("1.2.0", new string('a', 40))), runner, registry);

            var failed = await orchestrator.RunAsync(path, null, null, null, CancellationToken.None);

            var invalid = Assert.IsType<RunSuiteOutcome.SuiteInvalid>(failed);
            Assert.Equal(VfxCodeCatalogue.ValidationTimeout, Assert.Single(invalid.Validation.Errors).Code);
            Assert.Empty(registry.ListRuns());
            Assert.Equal(0, runner.InvocationCount);
            Assert.IsType<RunSuiteOutcome.Completed>(await orchestrator.RunAsync(path, null, null, null, CancellationToken.None));
        });
    }

    [Fact]
    public async Task RunAsync_CancelledDuringCliValidation_ReleasesClaimAndDoesNotRegisterRun()
    {
        await WithSuiteAsync(async path =>
        {
            using var cancellation = new CancellationTokenSource();
            var cli = new CancellingValidationCli(path, cancellation);
            var registry = new InMemoryRunRegistry();
            var runner = FakeSuiteRunner.Succeeding([], "", 0);
            var orchestrator = new RunSuiteOrchestrator(
                new CliPinVerifier(cli, new EnginePin("1.2.0", new string('a', 40))), runner, registry);

            var outcome = await orchestrator.RunAsync(path, null, null, null, cancellation.Token);

            var aborted = Assert.IsType<RunSuiteOutcome.Completed>(outcome);
            Assert.True(aborted.Result.Cancelled);
            Assert.Empty(registry.ListRuns());
            Assert.Equal(0, runner.InvocationCount);
            Assert.IsType<RunSuiteOutcome.Completed>(await orchestrator.RunAsync(path, null, null, null, CancellationToken.None));
        });
    }

    private sealed class CancellingValidationCli(string path, CancellationTokenSource cancellation) : IVouchfxCli
    {
        private bool _cancel = true;

        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("1.2.0");

        public Task<string?> TryRunStdoutAsync(IReadOnlyList<string> arguments, long maxStreamBytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<CliInvocationResult> RunAsync(IReadOnlyList<string> arguments, long maxStreamBytes, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(CliSuitePreflight.Timeout, timeout);
            Assert.Equal(CliSuitePreflight.MaxOutputBytes, maxStreamBytes);
            if (_cancel)
            {
                _cancel = false;
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CliInvocationResult.Completed(0, Report(path, true), "");
        }
    }

    private static string Report(string path, bool valid, string stage = "schema") => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        engineVersion = "1.2.0",
        scenarios = new[] { new { path, valid, diagnostics = valid ? [] : new[] { new { stage, message = "private-literal-marker " + path } } } },
        valid,
    });

    private static async Task WithSuiteAsync(Func<string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-preflight-{Guid.NewGuid():N}.e2e.yaml");
        try
        {
            await File.WriteAllTextAsync(path, "steps: []\n");
            await body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_ExternalStepAcceptedByPinnedCli_ReachesRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-external-{Guid.NewGuid():N}.e2e.yaml");
        try
        {
            await File.WriteAllTextAsync(path, "steps:\n  - id: external\n    type: example.external\n");
            var invoked = false;
            var cli = FakeVouchfxCli.WithInvocationHandler("1.2.0", arguments =>
            {
                Assert.True(arguments.Count == 3 && arguments[0] == "validate" && arguments[1] == path && arguments[2] == "--json",
                    "Preflight must validate the selected suite through the CLI argument list.");
                invoked = true;
                return CliInvocationResult.Completed(0, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    engineVersion = "1.2.0",
                    scenarios = new[] { new { path, valid = true, diagnostics = Array.Empty<object>() } },
                    valid = true,
                }), "");
            });
            var runner = FakeSuiteRunner.Succeeding([], "", 0);
            var orchestrator = new RunSuiteOrchestrator(
                new CliPinVerifier(cli, new EnginePin("1.2.0", new string('a', 40))), runner, new InMemoryRunRegistry());

            var outcome = await orchestrator.RunAsync(path, null, null, null, CancellationToken.None);

            Assert.IsType<RunSuiteOutcome.Completed>(outcome);
            Assert.True(invoked, "The pinned CLI must own run preflight.");
            Assert.Equal(1, runner.InvocationCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
