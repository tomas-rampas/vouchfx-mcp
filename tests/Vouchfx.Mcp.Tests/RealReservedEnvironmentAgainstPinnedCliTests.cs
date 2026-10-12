using System.Diagnostics;
using System.Text.Json;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Run;
using Vouchfx.Mcp.Validation.Semantics;
using Xunit.Abstractions;

namespace Vouchfx.Mcp.Tests;

/// <summary>Required release evidence: the complete pinned source table and every real refusal.</summary>
/// <remarks>
/// Configure VOUCHFX_ENGINE_REPO to a checkout containing ENGINE_PIN's commit. The source blob is
/// read at that full SHA, never from the working tree. These probes refuse before Docker startup.
/// An absent CLI or source blob is a failure after the existing required-real-probe gate opens.
/// </remarks>
public class RealReservedEnvironmentAgainstPinnedCliTests(ITestOutputHelper output)
{
    private const string PrivateValue = "reserved-env-private-value-19a7";

    [RequiredRealProbeFact]
    public async Task CompleteEngineOwnedTable_AndEveryTypeKeyRefusal_AgreeWithThePinnedCli()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pin = EnginePin.Load(RepoLayout.ResolveEnginePinPath());
        var cli = new VouchfxCliProcessRunner();
        Assert.IsType<CliPinResult.Ok>(await new CliPinVerifier(cli, pin).VerifyAsync(cts.Token));
        var versionOutput = await cli.TryGetVersionOutputAsync(cts.Token);
        Assert.NotNull(versionOutput);
        Assert.Contains(pin.CommitSha, versionOutput, StringComparison.Ordinal);
        var engineRepo = Environment.GetEnvironmentVariable("VOUCHFX_ENGINE_REPO");
        Assert.False(string.IsNullOrWhiteSpace(engineRepo), "Required parity needs VOUCHFX_ENGINE_REPO containing the exact ENGINE_PIN commit.");
        var source = await ReadPinnedSourceAsync(engineRepo!, pin.CommitSha, cts.Token);
        var expectedRows = ReservedDependencyEnvironmentRule.EngineOwnedKeys
            .SelectMany(pair => pair.Value.Select(key => pair.Key + "/" + key))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedRows, EngineReservedEnvironmentSource.Rows(source));
        output.WriteLine($"MEASURED exact source table at {pin.CommitSha}: {expectedRows.Length} type/key pairs.");

        var workspace = Directory.CreateTempSubdirectory("vfx-mcp-reserved-env-");
        try
        {
            foreach (var row in expectedRows)
            {
                var split = row.IndexOf('/', StringComparison.Ordinal);
                var type = row[..split];
                var key = row[(split + 1)..];
                var suitePath = Path.Combine(workspace.FullName, "refusal.e2e.yaml");
                var eventsPath = Path.Combine(workspace.FullName, "events.jsonl");
                await File.WriteAllTextAsync(suitePath, ProbeSuite(type, key), cts.Token);
                var result = await new VouchfxCliSuiteRunner().RunAsync(new SuiteRunSpec(suitePath, [], eventsPath), _ => { }, cts.Token);
                Assert.Equal(RunTermination.CompletedNormally, result.Termination);
                Assert.Equal(4, result.ExitCode);
                Assert.NotNull(result.StdoutDiagnosticExcerpt);
                Assert.Contains(key, result.StdoutDiagnosticExcerpt, StringComparison.Ordinal);
                Assert.DoesNotContain(PrivateValue, result.StdoutDiagnosticExcerpt, StringComparison.Ordinal);
                Assert.True(File.Exists(eventsPath));
                var eventsText = await File.ReadAllTextAsync(eventsPath, cts.Token);
                Assert.DoesNotContain(PrivateValue, eventsText, StringComparison.Ordinal);
                var events = SuiteEventParser.Parse(eventsText);
                Assert.Equal(RunVerdict.Inconclusive, events.AggregateVerdict);
                Assert.False(events.SawStepEvent);
                output.WriteLine($"MEASURED {row}: exitCode={result.ExitCode}, stepless Inconclusive, value absent.");
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static async Task<string> ReadPinnedSourceAsync(string repo, string sha, CancellationToken ct)
    {
        Assert.Matches("^[0-9a-f]{40}$", sha);
        var start = new ProcessStartInfo(
            VouchfxCliPathResolver.ResolveAbsolutePath("git")
                ?? throw new InvalidOperationException("git is not on PATH, so the pinned source cannot be read."))
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.longpaths=true");
        start.ArgumentList.Add("show");
        start.ArgumentList.Add(sha + ":src/Engine/Vouchfx.Engine.Orchestration/EnvironmentMapper.cs");
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
            var stderr = process.StandardError.ReadToEndAsync(budget.Token);
            await process.WaitForExitAsync(budget.Token);
            var source = await stdout;
            var error = await stderr;
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(error);
            Assert.InRange(source.Length, 1, 4 * 1024 * 1024);
            return source;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            }
        }
    }

    private static string ProbeSuite(string type, string key) => $$"""
        metadata:
          name: reserved-environment-parity
          owner: mcp-tests
          tags: [parity]
        environment:
          dependencies:
            backend:
              type: {{type}}
              env:
                {{JsonSerializer.Serialize(key)}}: {{JsonSerializer.Serialize(PrivateValue)}}
        steps:
          - id: unreachable
            type: script.csharp
            code: "return;"
        """;
}
