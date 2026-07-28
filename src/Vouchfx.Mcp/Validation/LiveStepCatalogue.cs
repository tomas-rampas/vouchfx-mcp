using System.Diagnostics.CodeAnalysis;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Validation;

/// <summary>
/// Loads the shape-level step catalogue from the pinned engine's live
/// <c>vouchfx list --json</c> export (REQ-010). Cached for the process lifetime after the first
/// successful load. Fail-fast on CLI/pin failure or thin/incomplete JSON (EDGE-004).
/// </summary>
/// <remarks>
/// <para>
/// <b>Process-lifetime ownership:</b> one instance is created per MCP server process (see
/// <c>VouchfxMcpServerRegistration</c>) and lives until process exit. It deliberately does
/// <em>not</em> implement <see cref="IDisposable"/>: the internal <see cref="SemaphoreSlim"/> is
/// held for the process lifetime (the same model as other process-scoped server state), and the
/// OS reclaims it on exit. Callers and tests create instances freely without a dispose obligation.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime SemaphoreSlim for a single MCP server process; never "
        + "disposed by design (OS reclaims on exit). See type remarks.")]
public sealed class LiveStepCatalogue
{
    private readonly IVouchfxCli _cli;
    private readonly CliPinVerifier _pinVerifier;
    private readonly EnginePin _enginePin;
    // Process-lifetime gate: never disposed — see type remarks. Safe for a single long-lived
    // server process; tests create short-lived catalogues that are GC'd with the process/fixture.
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private StepCatalogueLoadResult.Ok? _cachedOk;

    public LiveStepCatalogue(IVouchfxCli cli, CliPinVerifier pinVerifier, EnginePin enginePin)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(pinVerifier);
        ArgumentNullException.ThrowIfNull(enginePin);

        _cli = cli;
        _pinVerifier = pinVerifier;
        _enginePin = enginePin;
    }

    /// <summary>
    /// Returns the live catalogue, loading (and caching on success) from the pinned CLI on first
    /// call. Failures are never cached so a user can install/upgrade the CLI and retry without
    /// restarting the server.
    /// </summary>
    public async Task<StepCatalogueLoadResult> GetOrLoadAsync(CancellationToken cancellationToken = default)
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
            if (result is StepCatalogueLoadResult.Ok ok)
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

    /// <summary>Finds one type in the live catalogue, or <see langword="null"/> when unknown.</summary>
    public async Task<(StepCatalogueLoadResult Load, StepTypeInfo? Info)> FindAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        var load = await GetOrLoadAsync(cancellationToken);
        if (load is not StepCatalogueLoadResult.Ok ok)
        {
            return (load, null);
        }

        var info = ok.StepTypes.FirstOrDefault(
            candidate => string.Equals(candidate.Type, type, StringComparison.Ordinal));
        return (load, info);
    }

    private async Task<StepCatalogueLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var pin = await _pinVerifier.VerifyAsync(cancellationToken);
            switch (pin)
            {
                case CliPinResult.NotFound notFound:
                    return new StepCatalogueLoadResult.Failed(notFound.Message);
                case CliPinResult.VersionMismatch mismatch:
                    return new StepCatalogueLoadResult.Failed(mismatch.Message);
                case CliPinResult.Unparseable unparseable:
                    return new StepCatalogueLoadResult.Failed(unparseable.Message);
                case CliPinResult.Ok:
                    break;
                default:
                    return new StepCatalogueLoadResult.Failed(
                        EngineExportCapability.CatalogueUnavailableMessage(_enginePin.Version));
            }

            string? stdout;
            try
            {
                stdout = await _cli.TryRunStdoutAsync(
                    ["list", "--json"],
                    VouchfxCliProcessRunner.MaxListJsonOutputBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new StepCatalogueLoadResult.Failed(
                    EngineExportCapability.CatalogueUnavailableMessage(_enginePin.Version));
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return new StepCatalogueLoadResult.Failed(
                    EngineExportCapability.CatalogueUnavailableMessage(_enginePin.Version));
            }

            IReadOnlyList<StepTypeInfo> stepTypes;
            try
            {
                stepTypes = StepCatalogueParser.Parse(stdout);
            }
            catch (StepCatalogueParseException ex)
            {
                return new StepCatalogueLoadResult.Failed(
                    EngineExportCapability.ThinOrInvalidCatalogueMessage(ex.Message));
            }

            return new StepCatalogueLoadResult.Ok(stepTypes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: never let an unexpected failure escape as an unhandled tool crash.
            return new StepCatalogueLoadResult.Failed(
                EngineExportCapability.ThinOrInvalidCatalogueMessage(
                    "Unexpected catalogue load failure: " + ex.GetType().Name));
        }
    }
}

/// <summary>Outcome of loading the live shape-level step catalogue.</summary>
public abstract record StepCatalogueLoadResult
{
    private StepCatalogueLoadResult()
    {
    }

    /// <summary>Complete bar-B catalogue from the pinned engine.</summary>
    public sealed record Ok(IReadOnlyList<StepTypeInfo> StepTypes) : StepCatalogueLoadResult;

    /// <summary>CLI/pin/parse failure — message is agent-safe and actionable.</summary>
    public sealed record Failed(string Message) : StepCatalogueLoadResult;
}
