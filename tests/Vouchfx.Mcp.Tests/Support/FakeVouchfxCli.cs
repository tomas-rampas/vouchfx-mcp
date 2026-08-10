using System.Text;
using System.Text.Json;
using Vouchfx.Mcp.Cli;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// A canned <see cref="IVouchfxCli"/> for tests: returns fixed results instantly, with no process
/// spawned — so no test ever depends on the real <c>vouchfx</c> CLI being installed on the machine
/// running it (REQ-008 / REQ-010). Includes a Spec B <c>scaffold --intent</c> fake so Generator
/// MCP tests stay green without a published scaffold engine.
/// </summary>
internal sealed class FakeVouchfxCli : IVouchfxCli
{
    private readonly string? _rawVersionOutput;
    private readonly string? _listJson;
    private readonly string? _schemaJson;
    private readonly Func<IReadOnlyList<string>, CliInvocationResult>? _runHandler;
    private readonly bool _scaffoldEnabled;
    private readonly Func<IReadOnlyList<string>, CliInvocationResult>? _planHandler;

    private FakeVouchfxCli(
        string? rawVersionOutput,
        string? listJson = null,
        string? schemaJson = null,
        Func<IReadOnlyList<string>, CliInvocationResult>? runHandler = null,
        bool scaffoldEnabled = false,
        Func<IReadOnlyList<string>, CliInvocationResult>? planHandler = null)
    {
        _rawVersionOutput = rawVersionOutput;
        _listJson = listJson;
        _schemaJson = schemaJson;
        _runHandler = runHandler;
        _scaffoldEnabled = scaffoldEnabled;
        _planHandler = planHandler;
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
        new(rawVersionOutput, listJson: listJson, scaffoldEnabled: true);

    /// <summary>
    /// A fake with a custom run handler for arbitrary argument lists (schema export, thin list, scaffold, …).
    /// </summary>
    public static FakeVouchfxCli WithRunHandler(
        string? rawVersionOutput,
        Func<IReadOnlyList<string>, string?> runHandler) =>
        new(
            rawVersionOutput,
            runHandler: args =>
            {
                var stdout = runHandler(args);
                return stdout is null
                    ? CliInvocationResult.NotLaunched
                    : CliInvocationResult.Completed(0, stdout, stderr: string.Empty);
            });

    /// <summary>
    /// A fake with a custom full-result run handler (exit code + streams), for scaffold failure tests.
    /// </summary>
    public static FakeVouchfxCli WithInvocationHandler(
        string? rawVersionOutput,
        Func<IReadOnlyList<string>, CliInvocationResult> runHandler) =>
        new(rawVersionOutput, runHandler: runHandler);

    /// <summary>
    /// A fake whose pin handshake succeeds, <c>list --json</c> returns <paramref name="listJson"/>,
    /// and <c>schema</c> returns <paramref name="schemaJson"/>.
    /// </summary>
    public static FakeVouchfxCli WithExports(string rawVersionOutput, string listJson, string schemaJson) =>
        new(rawVersionOutput, listJson: listJson, schemaJson: schemaJson, scaffoldEnabled: true);

    /// <summary>
    /// A fake whose pin handshake succeeds and whose <c>plan &lt;path&gt; --json [...]</c> invocation
    /// is handled by <paramref name="planHandler"/> (Spec D / M3 Planner tests inject a canned report
    /// document, a specific non-zero exit code, or malformed/empty stdout, without spawning a real
    /// CLI process — this server's own tests never depend on one being installed, even though the
    /// engine release carrying <c>vouchfx plan</c> — v1.0.0-rc.3 — is published and the pin is at
    /// or beyond it). Also
    /// enables the Spec B <c>scaffold --intent</c> fake (mirrors <see cref="WithRichListJson"/>'s own
    /// rationale) so a single fake instance can drive a <c>plan_coverage</c> → <c>scaffold_suite</c>
    /// hand-off test without composing two separate fakes.
    /// </summary>
    public static FakeVouchfxCli WithPlanHandler(
        string rawVersionOutput,
        Func<IReadOnlyList<string>, CliInvocationResult> planHandler) =>
        new(rawVersionOutput, scaffoldEnabled: true, planHandler: planHandler);

    public Task<string?> TryGetVersionOutputAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_rawVersionOutput);

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
        ArgumentNullException.ThrowIfNull(arguments);

        if (_runHandler is not null)
        {
            return Task.FromResult(_runHandler(arguments));
        }

        if (IsVersionProbe(arguments))
        {
            return Task.FromResult(
                _rawVersionOutput is null
                    ? CliInvocationResult.NotLaunched
                    : CliInvocationResult.Completed(0, _rawVersionOutput, string.Empty));
        }

        if (IsListJson(arguments))
        {
            return Task.FromResult(
                _listJson is null
                    ? CliInvocationResult.NotLaunched
                    : CliInvocationResult.Completed(0, _listJson, string.Empty));
        }

        if (IsSchema(arguments))
        {
            return Task.FromResult(
                _schemaJson is null
                    ? CliInvocationResult.NotLaunched
                    : CliInvocationResult.Completed(0, _schemaJson, string.Empty));
        }

        if (_scaffoldEnabled && IsScaffold(arguments))
        {
            return Task.FromResult(HandleScaffold(arguments));
        }

        if (_planHandler is not null && IsPlan(arguments))
        {
            return Task.FromResult(_planHandler(arguments));
        }

        return Task.FromResult(CliInvocationResult.NotLaunched);
    }

    private static CliInvocationResult HandleScaffold(IReadOnlyList<string> arguments)
    {
        var intentPath = GetOptionValue(arguments, "--intent");
        if (string.IsNullOrWhiteSpace(intentPath) || !File.Exists(intentPath))
        {
            return CliInvocationResult.Completed(
                2,
                stdout: string.Empty,
                stderr: "Scaffold requires --intent <file|-> with a JSON intent document.");
        }

        string intentJson;
        try
        {
            intentJson = File.ReadAllText(intentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliInvocationResult.Completed(2, string.Empty, $"Cannot read scaffold intent: {ex.Message}");
        }

        using var document = JsonDocument.Parse(intentJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
        {
            return CliInvocationResult.Completed(2, string.Empty, "Scaffold intent is missing steps.");
        }

        var steps = stepsEl.EnumerateArray().ToArray();
        if (steps.Length == 0)
        {
            return CliInvocationResult.Completed(3, string.Empty, "Scaffold requires at least one step.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var stepInfos = new List<(string Id, string Type, string? Label)>();
        foreach (var step in steps)
        {
            var id = step.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            var type = step.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
            var label = step.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;

            if (string.Equals(type, "nope.fake", StringComparison.Ordinal)
                || type.StartsWith("nope.", StringComparison.Ordinal))
            {
                return CliInvocationResult.Completed(
                    3,
                    string.Empty,
                    $"Unknown step type '{type}'. Not present in the live step catalogue.");
            }

            if (!ids.Add(id))
            {
                return CliInvocationResult.Completed(3, string.Empty, $"Duplicate step id '{id}'.");
            }

            stepInfos.Add((id, type, label));
        }

        if (root.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in depsEl.EnumerateArray())
            {
                var depType = dep.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                if (string.Equals(depType, "not-a-real-dep", StringComparison.OrdinalIgnoreCase))
                {
                    return CliInvocationResult.Completed(
                        3,
                        string.Empty,
                        $"Unknown dependency kind '{depType}'.");
                }
            }
        }

        var yaml = BuildScaffoldYaml(root, stepInfos);
        return CliInvocationResult.Completed(0, yaml, string.Empty);
    }

    private static string BuildScaffoldYaml(
        JsonElement root,
        IReadOnlyList<(string Id, string Type, string? Label)> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Machine-drafted by vouchfx scaffold (fake test CLI).");
        sb.AppendLine("# Generated skeleton grounded in the live step catalogue — human review required before trust.");
        sb.AppendLine("# Placeholders only; fill semantics before treating this document as production-ready.");
        sb.AppendLine();
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: scaffolded-suite");
        sb.AppendLine("  tags: [scaffolded]");
        sb.AppendLine("  description: Machine-drafted suite skeleton from vouchfx scaffold; review and fill before trust.");
        sb.AppendLine();

        var hasServices = root.TryGetProperty("services", out var servicesEl)
            && servicesEl.ValueKind == JsonValueKind.Array
            && servicesEl.GetArrayLength() > 0;
        var hasDeps = root.TryGetProperty("dependencies", out var depsEl)
            && depsEl.ValueKind == JsonValueKind.Array
            && depsEl.GetArrayLength() > 0;

        if (hasServices || hasDeps)
        {
            sb.AppendLine("environment:");
            if (hasServices)
            {
                sb.AppendLine("  services:");
                foreach (var svc in servicesEl.EnumerateArray())
                {
                    var name = svc.TryGetProperty("name", out var n) ? n.GetString() ?? "svc" : "svc";
                    var image = svc.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String
                        ? img.GetString()!
                        : "traefik/whoami";
                    sb.Append("    ").Append(name).AppendLine(":");
                    sb.Append("      image: ").AppendLine(image);
                    sb.AppendLine("      httpPort: 80");
                }
            }

            if (hasDeps)
            {
                sb.AppendLine("  dependencies:");
                foreach (var dep in depsEl.EnumerateArray())
                {
                    var name = dep.TryGetProperty("name", out var n) ? n.GetString() ?? "dep" : "dep";
                    var type = dep.TryGetProperty("type", out var t) ? t.GetString() ?? "postgres" : "postgres";
                    sb.Append("    ").Append(name).AppendLine(":");
                    sb.Append("      type: ").AppendLine(type);
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("steps:");
        foreach (var (id, type, label) in steps)
        {
            if (!string.IsNullOrWhiteSpace(label))
            {
                sb.Append("  # label: ").AppendLine(label);
            }

            sb.Append("  - id: ").AppendLine(id);
            sb.Append("    type: ").AppendLine(type);
            AppendPlaceholders(sb, type);
        }

        return sb.ToString();
    }

    private static void AppendPlaceholders(StringBuilder sb, string type)
    {
        // Minimal schema-valid placeholders for common Core types used in Generator tests.
        if (string.Equals(type, "http.rest", StringComparison.Ordinal))
        {
            sb.AppendLine("    target: api");
            sb.AppendLine("    method: GET");
            sb.AppendLine("    path: /");
            return;
        }

        if (string.Equals(type, "db-assert.postgres", StringComparison.Ordinal)
            || type.StartsWith("db-assert.", StringComparison.Ordinal))
        {
            sb.AppendLine("    target: db");
            sb.AppendLine("    query: \"SELECT 1 AS scaffold\"");
            sb.AppendLine("    expect:");
            sb.AppendLine("      rowCount: 1");
            return;
        }

        // Generic fallback: enough for tools/list-shaped smoke content; may fail validate for
        // some types — tests that need validate_suite use http.rest + db-assert.postgres.
        sb.AppendLine("    target: scaffold-target");
    }

    private static string? GetOptionValue(IReadOnlyList<string> arguments, string optionName)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], optionName, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private static bool IsVersionProbe(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] == "--version";

    private static bool IsListJson(IReadOnlyList<string> arguments) =>
        arguments.Count == 2
        && string.Equals(arguments[0], "list", StringComparison.Ordinal)
        && string.Equals(arguments[1], "--json", StringComparison.Ordinal);

    private static bool IsSchema(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && string.Equals(arguments[0], "schema", StringComparison.Ordinal);

    private static bool IsScaffold(IReadOnlyList<string> arguments) =>
        arguments.Count >= 1 && string.Equals(arguments[0], "scaffold", StringComparison.Ordinal);

    private static bool IsPlan(IReadOnlyList<string> arguments) =>
        arguments.Count >= 1 && string.Equals(arguments[0], "plan", StringComparison.Ordinal);
}
