using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Tripwires for the documented state "this server does not implement the MCP Tasks extension"
/// (CLAUDE.md, Transport notes; <c>docs/troubleshooting.md</c>, "Long-running runs: poll (MCP Tasks is
/// not implemented)"). Each one fails on a change that makes that documentation untrue, and each
/// failure message names the section to update.
/// </summary>
/// <remarks>
/// <para>
/// <b>These replace an earlier tripwire that could never fire.</b> It watched for a <c>TasksCapability</c>
/// type in the three pinned SDK assemblies, but the SDK ships Tasks in a separate package
/// (<c>ModelContextProtocol.Extensions.Tasks</c>), so no update of those assemblies could ever add it.
/// The three guards here watch what can actually change: this server's own advertised extensions and
/// its answer to a Tasks request, the SDK's protocol-revision handling (the documented measurement was
/// made on the revisions listed below), and the project reference to the Tasks package.
/// </para>
/// <para>
/// A test failing here does not mean the change is wrong. It means the documentation and this class
/// must be updated together with it, after re-measuring.
/// </para>
/// </remarks>
public class TasksAdoptionTripwireTests
{
    private const string TasksExtensionId = "io.modelcontextprotocol/tasks";
    private const string TasksPackageId = "ModelContextProtocol.Extensions.Tasks";

    private const string UpdateTogether =
        "Update the section 'Long-running runs: poll (MCP Tasks is not implemented)' in docs/troubleshooting.md, "
        + "the Transport notes in CLAUDE.md, and this test together.";

    private static readonly string[] KnownSupportedRevisions =
        ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28"];

    private static readonly string[] KnownHandshakeRevisions =
        ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    [Fact]
    public async Task Server_AdvertisesNoTasksExtension_AndAnswersTasksGetWithMethodNotFound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        var extensions = harness.Client.ServerCapabilities?.Extensions;
        Assert.True(
            extensions is null || !extensions.ContainsKey(TasksExtensionId),
            $"This server now advertises the '{TasksExtensionId}' extension: Tasks has been enabled. {UpdateTogether}");

        var request = new JsonRpcRequest
        {
            Method = "tasks/get",
            Params = JsonNode.Parse("{\"taskId\":\"no-such-task\"}"),
        };
        var ex = await Record.ExceptionAsync(() => harness.Client.SendRequestAsync(request, cts.Token));

        var protocolException = Assert.IsType<McpProtocolException>(ex);
        Assert.True(
            protocolException.ErrorCode == McpErrorCode.MethodNotFound,
            $"'tasks/get' answered {(int)protocolException.ErrorCode} instead of MethodNotFound (-32601): "
            + $"Tasks has been enabled (or the SDK now answers it). {UpdateTogether}");
    }

    [Fact]
    public void PinnedSdk_ProtocolRevisionHandling_MatchesTheMeasuredSets()
    {
        var core = typeof(McpClient).Assembly;
        var versions = core.GetType("ModelContextProtocol.Protocol.McpProtocolVersions", throwOnError: false);
        Assert.True(
            versions is not null,
            "ModelContextProtocol.Core no longer has the McpProtocolVersions type: an SDK update changed revision "
            + "handling. Re-measure Tasks (does a task-augmented tools/call create a task, and on which revisions?). "
            + UpdateTogether);

        AssertRevisionSet(versions, "SupportedProtocolVersions", KnownSupportedRevisions);
        AssertRevisionSet(versions, "InitializeHandshakeProtocolVersions", KnownHandshakeRevisions);
    }

    [Fact]
    public async Task Client_NegotiatesTheRevisionTheMeasurementWasMadeOn()
    {
        // The observable half of the reflection test above: the SDK client negotiates 2026-07-28 through
        // server/discover against this server. If this stops being true the measured state is stale.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await McpTestHarness.StartAsync(cts.Token);

        Assert.True(
            harness.Client.NegotiatedProtocolVersion == "2026-07-28",
            $"The client negotiated '{harness.Client.NegotiatedProtocolVersion}' instead of 2026-07-28: an SDK update "
            + "changed revision handling. Re-measure Tasks. " + UpdateTogether);
    }

    [Fact]
    public void ServerProject_DoesNotReferenceTheTasksPackage()
    {
        var projectPath = Path.Combine(
            SourceGuardScan.RepoRoot.FullName, "src", "Vouchfx.Mcp", "Vouchfx.Mcp.csproj");
        var text = File.ReadAllText(projectPath);

        // Only an element that references the package counts: the project's comments name it.
        var withoutComments = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        Assert.False(
            Regex.IsMatch(withoutComments, "<PackageReference\\s[^>]*" + Regex.Escape(TasksPackageId), RegexOptions.IgnoreCase),
            $"src/Vouchfx.Mcp/Vouchfx.Mcp.csproj now references {TasksPackageId}: Tasks has been adopted. {UpdateTogether}");
    }

    private static void AssertRevisionSet(Type versions, string memberName, string[] expected)
    {
        const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var value = versions.GetField(memberName, All)?.GetValue(null)
            ?? versions.GetProperty(memberName, All)?.GetValue(null);
        var actual = Assert.IsAssignableFrom<IEnumerable<string>>(value).ToArray();

        Assert.True(
            actual.OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(v => v, StringComparer.Ordinal)),
            $"McpProtocolVersions.{memberName} is [{string.Join(", ", actual)}], measured as [{string.Join(", ", expected)}]: "
            + "an SDK update changed revision handling. Re-measure Tasks (which revisions it works on, and whether this "
            + "server still serves 2026-07-28). " + UpdateTogether);
    }
}
