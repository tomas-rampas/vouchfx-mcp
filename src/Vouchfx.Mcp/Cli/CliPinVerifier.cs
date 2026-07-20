namespace Vouchfx.Mcp.Cli;

/// <summary>
/// REQ-008's CLI presence + version handshake: verifies the <c>vouchfx</c> CLI on PATH matches
/// <c>ENGINE_PIN</c> before a CLI-dependent tool performs its first CLI-dependent operation.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>run_suite</c> calls this. Schema/catalogue/docs tools (<c>validate_suite</c>,
/// <c>list_step_types</c>, <c>describe_step_type</c>, <c>search_docs</c>) never do — they work
/// entirely from the embedded vendored schema/docs (see <c>StepTypeCatalogue</c>,
/// <c>VendoredDocRepository</c>) and must keep working even when the CLI is not installed at all.
/// <c>explain_run</c> reads a local events file, not the live CLI, so it does not need this either.
/// </para>
/// <para>
/// <b>Caching:</b> a successful <see cref="CliPinResult.Ok"/> is cached for the lifetime of this
/// instance — one instance per server session, constructed once in
/// <c>VouchfxMcpServerRegistration</c> — since the CLI resolved via
/// <see cref="VouchfxCliPathResolver"/>'s PATH-ONLY, ABSOLUTE-PATH search cannot silently change
/// under a running session the way a bare, CWD-searched name plausibly could (see
/// <see cref="VouchfxCliPathResolver"/>'s CWE-427 remarks): PATH itself does not change mid-session,
/// so a resolved absolute path stays valid for the session's lifetime. A FAILURE (any of the
/// other three cases) is never cached: the user may install or update the CLI and retry the very
/// next call without restarting the server. <c>_cachedOk</c> is read/written via
/// <see cref="Volatile"/> to document — not just assume — that this is safe under concurrent
/// calls: a reference assignment is atomic either way, but the explicit barrier makes that
/// intentional, not incidental.
/// </para>
/// </remarks>
public sealed class CliPinVerifier
{
    /// <summary>
    /// Maximum length, in characters, of a CLI-reported version/output excerpt embedded in a
    /// <c>Unparseable</c> or <c>VersionMismatch</c> message — applied to the RAW CLI output before
    /// normalisation, parsing, or <see cref="TextSanitiser"/>. Mirrors
    /// <see cref="Vouchfx.Mcp.Validation.ValidationWorkerClient"/>'s identical 500-character stderr
    /// excerpt bound: <see cref="VouchfxCliProcessRunner.MaxCliOutputBytes"/> (64&#160;KB) already
    /// bounds what THIS class receives at all, but 64&#160;KB is still far too much to usefully embed
    /// in an agent-facing message — and a value that merely "looks like a version" per
    /// <see cref="CliVersionNormaliser.LooksLikeAVersion"/> (starts with a digit) could otherwise be
    /// an arbitrarily long string (e.g. <c>"1"</c> followed by tens of thousands of other bytes) and
    /// still reach a <c>VersionMismatch</c> message uncapped. Truncating the raw output ONCE, before
    /// any other processing, bounds every downstream field and message uniformly.
    /// </summary>
    private const int MaxOutputExcerptLength = 500;

    private readonly IVouchfxCli _cli;
    private readonly EnginePin _pin;
    private readonly string _expectedCoreVersion;
    private CliPinResult.Ok? _cachedOk;

    public CliPinVerifier(IVouchfxCli cli, EnginePin pin)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(pin);

        _cli = cli;
        _pin = pin;
        _expectedCoreVersion = CliVersionNormaliser.Normalise(pin.Version);
    }

    /// <summary>
    /// Verifies the CLI, using the cached <see cref="CliPinResult.Ok"/> from an earlier call on
    /// this same instance if one exists.
    /// </summary>
    public async Task<CliPinResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cachedOk) is { } cached)
        {
            return cached;
        }

        var rawOutput = await _cli.TryGetVersionOutputAsync(cancellationToken);
        if (rawOutput is null)
        {
            return new CliPinResult.NotFound(BuildNotFoundMessage());
        }

        // Truncate FIRST, before anything else touches the CLI's own (untrusted — whatever binary
        // was resolved from PATH) output: bounds every value derived from it below, uniformly,
        // regardless of which result case is ultimately returned. Sanitised only AFTER truncating,
        // mirroring ValidationWorkerClient.ReadExcerptQuietlyAsync's stderr-excerpt ordering
        // exactly — including that precedent's choice not to append a "truncated" marker: doing so
        // BEFORE sanitising would risk the marker itself being a non-printable-ASCII character that
        // TextSanitiser then expands (each escaped character becomes a 6-character "\uXXXX"
        // sequence), silently defeating the very bound this truncation exists to enforce; appending
        // one AFTER would need its own accounting. Simplest and safest is neither.
        var trimmed = rawOutput.Trim();
        var excerpt = trimmed.Length > MaxOutputExcerptLength ? trimmed[..MaxOutputExcerptLength] : trimmed;
        var sanitisedExcerpt = TextSanitiser.SanitiseForDisplay(excerpt);
        var detectedCoreVersion = CliVersionNormaliser.Normalise(excerpt);

        if (!CliVersionNormaliser.LooksLikeAVersion(detectedCoreVersion))
        {
            return new CliPinResult.Unparseable(sanitisedExcerpt, BuildUnparseableMessage(sanitisedExcerpt));
        }

        var sanitisedDetectedVersion = TextSanitiser.SanitiseForDisplay(detectedCoreVersion);

        if (!string.Equals(detectedCoreVersion, _expectedCoreVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new CliPinResult.VersionMismatch(
                sanitisedDetectedVersion, _pin.Version, BuildMismatchMessage(sanitisedDetectedVersion));
        }

        var ok = new CliPinResult.Ok(sanitisedDetectedVersion);
        Volatile.Write(ref _cachedOk, ok);
        return ok;
    }

    private string BuildNotFoundMessage() =>
        $"The vouchfx CLI (version {_pin.Version}) is required but was not found on PATH. " +
        $"Install it with: dotnet tool install --global vouchfx --version {_expectedCoreVersion}";

    private string BuildMismatchMessage(string sanitisedDetectedVersion) =>
        $"The installed vouchfx CLI is version {sanitisedDetectedVersion}, but this server is " +
        $"pinned to {_pin.Version}. Update it with: dotnet tool update --global vouchfx " +
        $"--version {_expectedCoreVersion}";

    private string BuildUnparseableMessage(string sanitisedRawOutput) =>
        $"The vouchfx CLI reported a version this server could not recognise: " +
        $"'{sanitisedRawOutput}'. Reinstall it with: dotnet tool install --global vouchfx " +
        $"--version {_expectedCoreVersion}";
}
