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
    /// Maximum length, in characters, of the RAW CLI output truncated to BEFORE any further
    /// processing (normalisation, parsing, or <see cref="TextSanitiser"/>).
    /// </summary>
    /// <remarks>
    /// Keeps that later processing cheap: the read itself is already bounded at
    /// <see cref="VouchfxCliProcessRunner.MaxCliOutputBytes"/> (64&#160;KB), but there is no reason
    /// to run normalisation/sanitisation over the full 64&#160;KB when a genuine version string is
    /// a handful of characters. <b>This is NOT the final agent-facing bound</b> — sanitisation can
    /// EXPAND length (see <see cref="MaxSanitisedExcerptLength"/>'s remarks), so a value truncated
    /// only here can still grow well past this figure before it reaches a message. Applied to the
    /// raw output once, up front, so every value derived from it below — both
    /// <see cref="CliPinResult.Unparseable.RawOutput"/> and
    /// <see cref="CliPinResult.VersionMismatch.DetectedVersion"/> — is bounded uniformly regardless
    /// of which result case is ultimately returned.
    /// </remarks>
    private const int MaxRawExcerptLength = 500;

    /// <summary>
    /// Maximum length, in characters, of a CLI-derived value as it actually reaches an agent-facing
    /// message — applied AFTER <see cref="TextSanitiser.SanitiseForDisplay"/>, not before. This is
    /// the TRUE bound; <see cref="MaxRawExcerptLength"/> alone is not.
    /// </summary>
    /// <remarks>
    /// <see cref="TextSanitiser.SanitiseForDisplay"/> replaces each non-printable-ASCII character
    /// with a 6-character <c>\uXXXX</c> escape. A <see cref="MaxRawExcerptLength"/>-character raw
    /// excerpt consisting entirely of such characters would therefore sanitise to roughly 6x that —
    /// around 3,000 characters for the 500-character raw cap — which is still finite, but far
    /// larger than "500 characters" would suggest, and defeats the purpose of a small, predictable
    /// excerpt bound. This second cap, applied to the ALREADY-sanitised text, is what actually
    /// keeps <see cref="CliPinResult.Unparseable.RawOutput"/> and
    /// <see cref="CliPinResult.VersionMismatch.DetectedVersion"/> — and therefore every message
    /// built from them — bounded at a small, predictable size regardless of what the raw excerpt
    /// contained.
    /// </remarks>
    private const int MaxSanitisedExcerptLength = 500;

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

        // Two-stage cap, in this order, for two DIFFERENT reasons:
        //   1. Truncate the RAW output to MaxRawExcerptLength first — keeps normalisation and
        //      sanitisation cheap regardless of how much of the (already 64 KB-bounded, at the
        //      read) output there is.
        //   2. Sanitise.
        //   3. Truncate the SANITISED result to MaxSanitisedExcerptLength — the TRUE agent-facing
        //      bound, because step 2 can EXPAND length (each non-printable character becomes a
        //      6-character \uXXXX escape), so step 1 alone does not bound what an agent actually
        //      sees. Mirrors ValidationWorkerClient.ReadExcerptQuietlyAsync's truncate-before-
        //      sanitise ordering for step 1's cost rationale, while step 3 closes the gap that
        //      precedent does not itself have to worry about.
        var trimmed = rawOutput.Trim();
        var rawExcerpt = trimmed.Length > MaxRawExcerptLength ? trimmed[..MaxRawExcerptLength] : trimmed;
        var sanitisedExcerpt = SanitiseAndCap(rawExcerpt);
        var detectedCoreVersion = CliVersionNormaliser.Normalise(rawExcerpt);

        if (!CliVersionNormaliser.LooksLikeAVersion(detectedCoreVersion))
        {
            return new CliPinResult.Unparseable(sanitisedExcerpt, BuildUnparseableMessage(sanitisedExcerpt));
        }

        var sanitisedDetectedVersion = SanitiseAndCap(detectedCoreVersion);

        if (!string.Equals(detectedCoreVersion, _expectedCoreVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new CliPinResult.VersionMismatch(
                sanitisedDetectedVersion, _pin.Version, BuildMismatchMessage(sanitisedDetectedVersion));
        }

        var ok = new CliPinResult.Ok(sanitisedDetectedVersion);
        Volatile.Write(ref _cachedOk, ok);
        return ok;
    }

    /// <summary>Sanitises <paramref name="rawText"/>, then applies the final, POST-sanitisation length cap.</summary>
    private static string SanitiseAndCap(string rawText)
    {
        var sanitised = TextSanitiser.SanitiseForDisplay(rawText);
        return sanitised.Length > MaxSanitisedExcerptLength ? sanitised[..MaxSanitisedExcerptLength] : sanitised;
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
