using Vouchfx.Mcp;
using Vouchfx.Mcp.Cli;
using Vouchfx.Mcp.Validation;

namespace Vouchfx.Mcp.Tests.Validation;

/// <summary>
/// Covers <see cref="LiveStepCatalogue"/>: pin handshake + <c>list --json</c> load, process-lifetime
/// success cache, and EDGE-004 fail-fast (failures not cached so a retry after install works).
/// </summary>
public class LiveStepCatalogueTests
{
    private static readonly EnginePin Pin = new("v1.0.0-alpha.9", "8c579ab4315cacba4066bc3f33dc24a19ca6c3d1");

    [Fact]
    public async Task GetOrLoadAsync_RichListJson_ReturnsOkWithHttpRestShape()
    {
        var cli = FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.SingleHttpRestJson);
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        var result = await catalogue.GetOrLoadAsync();

        var ok = Assert.IsType<StepCatalogueLoadResult.Ok>(result);
        var httpRest = Assert.Single(ok.StepTypes);
        Assert.Equal("http.rest", httpRest.Type);
        Assert.Equal(["method", "path", "target"], httpRest.RequiredFields);
        Assert.True(httpRest.CaptureSupported);
        Assert.False(string.IsNullOrWhiteSpace(httpRest.FamilyIntent));
    }

    [Fact]
    public async Task GetOrLoadAsync_CachesSuccessfulLoad()
    {
        var callCount = 0;
        var cli = FakeVouchfxCli.WithRunHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                if (args.Count == 1 && args[0] == "--version")
                {
                    return CliVersionNormaliser.Normalise(Pin.Version);
                }

                if (args.Count == 2 && args[0] == "list" && args[1] == "--json")
                {
                    Interlocked.Increment(ref callCount);
                    return RichListJsonFixture.SingleHttpRestJson;
                }

                return null;
            });
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        _ = await catalogue.GetOrLoadAsync();
        _ = await catalogue.GetOrLoadAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetOrLoadAsync_ThinCatalogue_FailsFastWithSpecAMessage()
    {
        var cli = FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.ThinJson);
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        var result = await catalogue.GetOrLoadAsync();

        var failed = Assert.IsType<StepCatalogueLoadResult.Failed>(result);
        Assert.Contains("requiredFields", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Spec A", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOrLoadAsync_CliNotFound_FailsWithInstallGuidance()
    {
        var cli = FakeVouchfxCli.NotFound();
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        var result = await catalogue.GetOrLoadAsync();

        var failed = Assert.IsType<StepCatalogueLoadResult.Failed>(result);
        Assert.Contains("not found", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrLoadAsync_FailureIsNotCached_RetrySucceedsAfterFix()
    {
        var listCalls = 0;
        var serveRich = false;
        var cli = FakeVouchfxCli.WithRunHandler(
            CliVersionNormaliser.Normalise(Pin.Version),
            args =>
            {
                if (args.Count == 1 && args[0] == "--version")
                {
                    return CliVersionNormaliser.Normalise(Pin.Version);
                }

                if (args.Count == 2 && args[0] == "list" && args[1] == "--json")
                {
                    Interlocked.Increment(ref listCalls);
                    return serveRich ? RichListJsonFixture.SingleHttpRestJson : null;
                }

                return null;
            });
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        var first = await catalogue.GetOrLoadAsync();
        Assert.IsType<StepCatalogueLoadResult.Failed>(first);
        Assert.Equal(1, listCalls);

        serveRich = true;
        var second = await catalogue.GetOrLoadAsync();
        Assert.IsType<StepCatalogueLoadResult.Ok>(second);
        Assert.Equal(2, listCalls);
    }

    [Fact]
    public async Task FindAsync_UnknownType_ReturnsNullInfoWithOkLoad()
    {
        var cli = FakeVouchfxCli.WithRichListJson(
            CliVersionNormaliser.Normalise(Pin.Version),
            RichListJsonFixture.SingleHttpRestJson);
        var catalogue = new LiveStepCatalogue(cli, new CliPinVerifier(cli, Pin), Pin);

        var (load, info) = await catalogue.FindAsync("nope.nope");

        Assert.IsType<StepCatalogueLoadResult.Ok>(load);
        Assert.Null(info);
    }
}
