using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Optionally loads the composed JSON Schema from the pinned engine via <c>vouchfx schema</c>
/// (REQ-010), caching the result for the process lifetime. Falls back to the embedded vendored
/// schema when the CLI is unavailable — validate_suite's worker always uses the embedded
/// resource for process isolation, so this live path is available to callers that want the
/// engine's current composition without a pin bump of vendored artefacts.
/// </summary>
/// <remarks>
/// <para>
/// <b>validate_suite path:</b> the validation worker (<c>--validate-worker</c>) still evaluates
/// the embedded <c>composed-schema.v1.json</c> (refreshed from the engine pin via
/// <c>scripts/sync-vendored.ps1</c>; prefer regenerating from <c>vouchfx schema</c> at the pin
/// once Spec A is published — see <c>vendored/README.md</c>). That keeps the worker Docker-free,
/// CLI-free, and killable in isolation.
/// </para>
/// <para>
/// This type is the preferred live export entry for hosts that already have a verified CLI and
/// want the engine's current composed schema without re-vendoring.
/// </para>
/// </remarks>
public sealed class LiveSchemaDocument : IDisposable
{
    private readonly IVouchfxCli _cli;
    private readonly CliPinVerifier _pinVerifier;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private LiveSchemaLoadResult.Ok? _cachedOk;
    private bool _disposed;

    public LiveSchemaDocument(IVouchfxCli cli, CliPinVerifier pinVerifier)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(pinVerifier);

        _cli = cli;
        _pinVerifier = pinVerifier;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _loadGate.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Loads the composed schema from <c>vouchfx schema</c> when the pin handshake succeeds.
    /// Failures are not cached.
    /// </summary>
    public async Task<LiveSchemaLoadResult> GetOrLoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cachedOk) is { } cached)
        {
            return cached;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedOk is { } lockedCached)
            {
                return lockedCached;
            }

            var result = await LoadCoreAsync(cancellationToken);
            if (result is LiveSchemaLoadResult.Ok ok)
            {
                Volatile.Write(ref _cachedOk, ok);
            }

            return result;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<LiveSchemaLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pin = await _pinVerifier.VerifyAsync(cancellationToken);
            if (pin is not CliPinResult.Ok)
            {
                return new LiveSchemaLoadResult.Unavailable(
                    "Pinned vouchfx CLI is not available for `vouchfx schema`; "
                    + "validate_suite continues to use the embedded vendored composed schema.");
            }

            var stdout = await _cli.TryRunStdoutAsync(
                ["schema"],
                VouchfxCliProcessRunner.MaxSchemaOutputBytes,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return new LiveSchemaLoadResult.Unavailable(
                    "The pinned vouchfx CLI did not emit a composed schema via `vouchfx schema` "
                    + "(command missing or failed). This engine may predate Spec A. "
                    + "validate_suite continues to use the embedded vendored composed schema. "
                    + $"Minimum for live schema export: {EngineExportCapability.MinimumRequirementDescription}.");
            }

            var trimmed = stdout.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                return new LiveSchemaLoadResult.Unavailable(
                    "`vouchfx schema` output did not look like a JSON object; "
                    + "validate_suite continues to use the embedded vendored composed schema.");
            }

            return new LiveSchemaLoadResult.Ok(stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LiveSchemaLoadResult.Unavailable(
                "Unexpected failure loading live schema (" + ex.GetType().Name + "); "
                + "validate_suite continues to use the embedded vendored composed schema.");
        }
    }
}

/// <summary>Outcome of attempting to load the composed schema from the live CLI.</summary>
public abstract record LiveSchemaLoadResult
{
    private LiveSchemaLoadResult()
    {
    }

    /// <summary>Raw composed JSON Schema text from <c>vouchfx schema</c>.</summary>
    public sealed record Ok(string SchemaJson) : LiveSchemaLoadResult;

    /// <summary>
    /// CLI unavailable or export not supported — callers should keep using the vendored schema.
    /// </summary>
    public sealed record Unavailable(string Message) : LiveSchemaLoadResult;
}
