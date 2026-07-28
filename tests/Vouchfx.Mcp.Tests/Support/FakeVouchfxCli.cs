using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A canned <see cref="IVouchfxCli"/> for tests: returns fixed results instantly, with no process
/// spawned — so no test ever depends on the real <c>vouchfx</c> CLI being installed on the machine
/// running it (REQ-008 / REQ-010).
/// </summary>
internal sealed class FakeVouchfxCli : IVouchfxCli
{
    private readonly string? _rawVersionOutput;
    private readonly string? _listJson;
    private readonly string? _schemaJson;
    private readonly Func<IReadOnlyList<string>, string?>? _runHandler;

    private FakeVouchfxCli(
        string? rawVersionOutput,
        string? listJson = null,
        string? schemaJson = null,
        Func<IReadOnlyList<string>, string?>? runHandler = null)
    {
        _rawVersionOutput = rawVersionOutput;
        _listJson = listJson;
        _schemaJson = schemaJson;
        _runHandler = runHandler;
    }

    /// <summary>A fake reporting the CLI is not installed (mirrors a launch failure / not on PATH).</summary>
    public static FakeVouchfxCli NotFound() => new(null);

    /// <summary>
    /// A fake reporting <paramref name="rawVersionOutput"/> verbatim for <c>--version</c>, with no
    /// catalogue/schema export (pre-Spec-A or incomplete install for catalogue tools).
    /// </summary>
    public static FakeVouchfxCli ReportingVersion(string rawVersionOutput) => new(rawVersionOutput);

    /// <summary>
    /// A fake whose pin handshake succeeds and whose <c>list --json</c> returns the given bar-B
    /// catalogue document (REQ-010 unit tests inject fixture JSON without a real CLI).
    /// </summary>
    public static FakeVouchfxCli WithRichListJson(string rawVersionOutput, string listJson) =>
        new(rawVersionOutput, listJson: listJson);

    /// <summary>
    /// A fake with a custom run handler for arbitrary argument lists (schema export, thin list, …).
    /// </summary>
    public static FakeVouchfxCli WithRunHandler(
        string? rawVersionOutput,
        Func<IReadOnlyList<string>, string?> runHandler) =>
        new(rawVersionOutput, runHandler: runHandler);

    /// <summary>
    /// A fake whose pin handshake succeeds, <c>list --json</c> returns <paramref name="listJson"/>,
    /// and <c>schema</c> returns <paramref name="schemaJson"/>.
    /// </summary>
    public static FakeVouchfxCli WithExports(string rawVersionOutput, string listJson, string schemaJson) =>
        new(rawVersionOutput, listJson: listJson, schemaJson: schemaJson);

    public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_rawVersionOutput);

    public Task<string?> TryRunStdoutAsync(
        IReadOnlyList<string> arguments,
        long maxStdoutBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (_runHandler is not null)
        {
            return Task.FromResult(_runHandler(arguments));
        }

        if (IsVersionProbe(arguments))
        {
            return Task.FromResult(_rawVersionOutput);
        }

        if (IsListJson(arguments))
        {
            return Task.FromResult(_listJson);
        }

        if (IsSchema(arguments))
        {
            return Task.FromResult(_schemaJson);
        }

        return Task.FromResult<string?>(null);
    }

    private static bool IsVersionProbe(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] == "--version";

    private static bool IsListJson(IReadOnlyList<string> arguments) =>
        arguments.Count == 2
        && string.Equals(arguments[0], "list", StringComparison.Ordinal)
        && string.Equals(arguments[1], "--json", StringComparison.Ordinal);

    private static bool IsSchema(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && string.Equals(arguments[0], "schema", StringComparison.Ordinal);
}
