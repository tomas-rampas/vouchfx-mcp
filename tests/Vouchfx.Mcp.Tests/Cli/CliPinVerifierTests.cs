using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests.Cli;

/// <summary>
/// Covers <see cref="CliPinVerifier"/> — REQ-008's CLI presence + version handshake — entirely
/// via the injected <see cref="FakeVouchfxCli"/>, never the real CLI.
/// </summary>
public class CliPinVerifierTests
{
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    // ── Ok ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CliReportsMatchingVersion_ReturnsOk()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion("1.0.0-alpha.9"), Pin);

        var result = await verifier.VerifyAsync();

        var ok = Assert.IsType<CliPinResult.Ok>(result);
        Assert.Equal("1.0.0-alpha.9", ok.DetectedVersion);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsMatchingVersionWithBuildMetadata_ReturnsOk()
    {
        // The exact shape the real, currently-installed CLI reports.
        var cli = FakeVouchfxCli.ReportingVersion("1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");
        var verifier = new CliPinVerifier(cli, Pin);

        var result = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.Ok>(result);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsVersionDifferingOnlyByCase_ReturnsOk()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion("1.0.0-ALPHA.9"), Pin);

        var result = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.Ok>(result);
    }

    // ── NotFound ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CliNotFound_ReturnsNotFoundWithInstallCommandContainingThePinVersion()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.NotFound(), Pin);

        var result = await verifier.VerifyAsync();

        var notFound = Assert.IsType<CliPinResult.NotFound>(result);
        Assert.Contains("dotnet tool install --global vouchfx --version 1.0.0-alpha.9", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("not found on PATH", notFound.Message, StringComparison.Ordinal);
    }

    // ── VersionMismatch ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CliReportsAnOlderVersion_ReturnsVersionMismatchNamingBothVersionsAndTheUpdateCommand()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion("1.0.0-alpha.8"), Pin);

        var result = await verifier.VerifyAsync();

        var mismatch = Assert.IsType<CliPinResult.VersionMismatch>(result);
        Assert.Equal("1.0.0-alpha.8", mismatch.DetectedVersion);
        Assert.Equal("v1.0.0-alpha.9", mismatch.ExpectedVersion);
        Assert.Contains("1.0.0-alpha.8", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("v1.0.0-alpha.9", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet tool update --global vouchfx --version 1.0.0-alpha.9", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsANewerVersion_StillReturnsVersionMismatch()
    {
        // The pin is exact-match, not a floor — REQ-008 gates on the pinned version specifically.
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion("1.0.0-alpha.10"), Pin);

        var result = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.VersionMismatch>(result);
    }

    // ── Unparseable ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CliReportsUnrecognisableOutput_ReturnsUnparseableWithoutCrashing()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion("usage: vouchfx [options]"), Pin);

        var result = await verifier.VerifyAsync();

        var unparseable = Assert.IsType<CliPinResult.Unparseable>(result);
        Assert.Contains("usage: vouchfx", unparseable.RawOutput, StringComparison.Ordinal);
        Assert.Contains("could not recognise", unparseable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsEmptyOutput_ReturnsUnparseableWithoutCrashing()
    {
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion(string.Empty), Pin);

        var result = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.Unparseable>(result);
    }

    // ── Security MAJOR 2: a huge CLI output must never reach an agent-facing message uncapped ────

    [Fact]
    public async Task VerifyAsync_CliReportsHugeUnparseableOutput_TruncatesTheExcerptRatherThanEmbeddingItInFull()
    {
        var hugeOutput = "not-a-version-" + new string('x', 10_000);
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion(hugeOutput), Pin);

        var result = await verifier.VerifyAsync();

        var unparseable = Assert.IsType<CliPinResult.Unparseable>(result);
        Assert.True(
            unparseable.RawOutput.Length <= 500, // the excerpt cap; all-ASCII input, so sanitising
            $"Expected the excerpt to be capped at 500 characters, was {unparseable.RawOutput.Length}."); // is a no-op on length here
        Assert.Contains(unparseable.RawOutput, unparseable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsAVersionLikeStringFollowedByHugeGarbage_TruncatesTheMismatchDetectedVersion()
    {
        // "Looks like a version" only requires starting with a digit (see
        // CliVersionNormaliser.LooksLikeAVersion) — a hostile/broken CLI could satisfy that with
        // "1" followed by tens of thousands of other bytes and still reach VersionMismatch. This
        // proves the SAME truncation applies on that path too, not only Unparseable's.
        var hugeVersionLikeOutput = "1" + new string('y', 10_000);
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion(hugeVersionLikeOutput), Pin);

        var result = await verifier.VerifyAsync();

        var mismatch = Assert.IsType<CliPinResult.VersionMismatch>(result);
        Assert.True(
            mismatch.DetectedVersion.Length <= 500,
            $"Expected the detected version to be capped at 500 characters, was {mismatch.DetectedVersion.Length}.");
        Assert.True(
            mismatch.Message.Length < hugeVersionLikeOutput.Length,
            "Expected the mismatch message to be far shorter than the huge raw CLI output.");
    }

    [Fact]
    public async Task VerifyAsync_CliReportsOutputFullOfNonPrintableCharacters_TheFinalSanitisedExcerptStaysCappedNotSixTimesLarger()
    {
        // The exact gap a Copilot review caught: TextSanitiser.SanitiseForDisplay expands EVERY
        // non-printable character into a 6-character "\uXXXX" escape, so bounding only the RAW,
        // PRE-sanitisation length does not bound the SANITISED result an agent actually sees — a
        // 500-character raw excerpt consisting entirely of non-printable characters would sanitise
        // to roughly 3,000 characters without a second, post-sanitisation cap. This proves the
        // final agent-facing length stays at the small, stated bound regardless.
        var hostileOutput = new string((char)1, 2_000); // Ctrl-A throughout: non-printable, not whitespace
        var verifier = new CliPinVerifier(FakeVouchfxCli.ReportingVersion(hostileOutput), Pin);

        var result = await verifier.VerifyAsync();

        var unparseable = Assert.IsType<CliPinResult.Unparseable>(result);
        Assert.True(
            unparseable.RawOutput.Length <= 500,
            $"Expected the final, POST-sanitisation excerpt to be capped at 500 characters — not " +
            $"~3,000 from escaping expansion — was {unparseable.RawOutput.Length}.");
        Assert.DoesNotContain((char)1, unparseable.RawOutput);
    }

    // ── Sanitisation: the CLI's own output is untrusted ─────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_CliReportsOutputWithAControlCharacter_SanitisesItInTheMismatchMessage()
    {
        var disallowedByte = ((char)27).ToString(); // ESC
        var cli = FakeVouchfxCli.ReportingVersion($"1.0.0-alpha.8{disallowedByte}");
        var verifier = new CliPinVerifier(cli, Pin);

        var result = await verifier.VerifyAsync();

        var mismatch = Assert.IsType<CliPinResult.VersionMismatch>(result);
        Assert.DoesNotContain(disallowedByte, mismatch.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(disallowedByte, mismatch.DetectedVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_CliReportsUnparseableOutputWithAControlCharacter_SanitisesItInTheMessage()
    {
        var disallowedByte = ((char)27).ToString();
        var cli = FakeVouchfxCli.ReportingVersion($"not a version{disallowedByte}");
        var verifier = new CliPinVerifier(cli, Pin);

        var result = await verifier.VerifyAsync();

        var unparseable = Assert.IsType<CliPinResult.Unparseable>(result);
        Assert.DoesNotContain(disallowedByte, unparseable.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(disallowedByte, unparseable.RawOutput, StringComparison.Ordinal);
    }

    // ── Caching: a successful Ok is cached for the instance's lifetime; a failure is never cached ─

    [Fact]
    public async Task VerifyAsync_CalledTwiceAfterOk_OnlyProbesTheCliOnce()
    {
        var probeCallCount = 0;
        var cli = new CountingFakeVouchfxCli(() =>
        {
            probeCallCount++;
            return "1.0.0-alpha.9";
        });
        var verifier = new CliPinVerifier(cli, Pin);

        var first = await verifier.VerifyAsync();
        var second = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.Ok>(first);
        Assert.IsType<CliPinResult.Ok>(second);
        Assert.Equal(1, probeCallCount);
    }

    [Fact]
    public async Task VerifyAsync_CalledTwiceAfterNotFound_ProbesTheCliAgainEachTime()
    {
        // A failed check must NOT be cached: the user may install the CLI between calls.
        var probeCallCount = 0;
        var cli = new CountingFakeVouchfxCli(() =>
        {
            probeCallCount++;
            return null;
        });
        var verifier = new CliPinVerifier(cli, Pin);

        await verifier.VerifyAsync();
        await verifier.VerifyAsync();

        Assert.Equal(2, probeCallCount);
    }

    [Fact]
    public async Task VerifyAsync_NotFoundThenInstalled_TheVerySameInstanceReflectsTheNewOkResultOnRetry()
    {
        // Simulates "the user installed the CLI and retried" without restarting the server.
        var callCount = 0;
        var cli = new CountingFakeVouchfxCli(() =>
        {
            callCount++;
            return callCount == 1 ? null : "1.0.0-alpha.9";
        });
        var verifier = new CliPinVerifier(cli, Pin);

        var first = await verifier.VerifyAsync();
        var second = await verifier.VerifyAsync();

        Assert.IsType<CliPinResult.NotFound>(first);
        Assert.IsType<CliPinResult.Ok>(second);
    }

    private sealed class CountingFakeVouchfxCli(Func<string?> produce) : IVouchfxCli
    {
        public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(produce());

        public async Task<string?> TryRunStdoutAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            CancellationToken cancellationToken = default)
        {
            var result = await RunAsync(arguments, maxStreamBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result is { Launched: true, ExitCode: 0 } ? result.Stdout : null;
        }

        public Task<CliInvocationResult> RunAsync(
            IReadOnlyList<string> arguments,
            long maxStreamBytes,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (arguments.Count == 1 && arguments[0] == "--version")
            {
                var version = produce();
                return Task.FromResult(
                    version is null
                        ? CliInvocationResult.NotLaunched
                        : CliInvocationResult.Completed(0, version, string.Empty));
            }

            return Task.FromResult(CliInvocationResult.NotLaunched);
        }
    }
}
