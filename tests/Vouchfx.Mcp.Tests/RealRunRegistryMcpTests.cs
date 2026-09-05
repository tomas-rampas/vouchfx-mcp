using System.Text.Json;
using ModelContextProtocol.Protocol;
using Vouchfx.Mcp.Run;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-01 end to end over the real MCP wire protocol (the in-memory <see cref="McpTestHarness"/>
/// with a <see cref="FakeVouchfxCli"/> and a <see cref="FakeSuiteRunner"/> — no real engine, no
/// Docker): restart survival, the no-workspace compatibility path, and the registry's secret-hygiene
/// boundary asserted against its actual on-disk bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Restart survival is proven with TWO independent servers, not asserted.</b> The sprint's exit
/// checklist calls for exactly that, and it is the only construction that proves anything: a single
/// harness holding one registry instance would pass identically whether the registry persisted or
/// not. Each test below starts harness A against a workspace, DISPOSES it completely (host stopped,
/// pipes completed — every in-process object gone), and then starts harness B against the same
/// workspace root. Nothing but the directory on disk crosses that boundary, so anything B can still
/// see came off the filesystem.
/// </para>
/// <para>
/// <b>The harnesses share no injected registry, deliberately.</b> Neither call passes
/// <c>runRegistry:</c>, so each server builds its own through the production
/// <see cref="VouchfxMcpServerRegistration.AddVouchfxMcpServer"/> path — which is where the
/// workspace-configured-ness choice between <see cref="FileRunRegistry"/> and
/// <see cref="InMemoryRunRegistry"/> actually lives. Injecting one shared instance would have made
/// these tests pass while proving only that a shared object is shared.
/// </para>
/// </remarks>
public class RealRunRegistryMcpTests : IDisposable
{
    /// <summary>
    /// An environment variable name no real suite would ever use, so a resolved value appearing in
    /// the registry could only have come from this server resolving a reference it must never
    /// resolve. <see cref="FakeSuiteRunner"/> never resolves anything at all, which is what makes
    /// the assertion about THIS server rather than about the engine.
    /// </summary>
    private const string SecretSentinelName = "VOUCHFX_MCP_REGISTRY_SENTINEL_NEVER_RESOLVED";

    /// <summary>The value that must never appear in the registry's on-disk representation.</summary>
    private const string SecretSentinelValue = "s3ntinel-resolved-secret-2f9c41ab";

    /// <summary>The literal reference the suite (and the events stream) carries, unresolved.</summary>
    private const string SecretReference = "${secret:env/" + SecretSentinelName + "}";

    private const string ValidSuiteYaml = """
        metadata:
          name: "Orders API health smoke test"
          owner: "platform-team"

        steps:
          - id: check-health
            type: http.rest
            description: "Confirms the health endpoint responds successfully."
            target: orders-api
            method: GET
            path: /health
        """;

    private const string PassingEventsFileContent = """
        {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50}
        {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
        """;

    private readonly string _sandbox;
    private readonly string _root;
    private readonly string _suitePath;
    private readonly Workspace _workspace;

    public RealRunRegistryMcpTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-registry-mcp-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_sandbox, "workspace");
        Directory.CreateDirectory(_root);

        _suitePath = Path.Combine(_root, "orders.e2e.yaml");
        File.WriteAllText(_suitePath, ValidSuiteYaml);

        _workspace = Workspace.Resolve(_root);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        Environment.SetEnvironmentVariable(SecretSentinelName, null);

        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp-directory hygiene only.
        }
    }

    // ── Restart survival (the sprint exit-checklist item) ──────────────────────────────────────

    /// <summary>
    /// The story's second Gherkin scenario: a run completed before a server restart is still what
    /// <c>explain_run</c> defaults to afterwards.
    /// </summary>
    [Fact]
    public async Task ExplainRun_DefaultsToARunRecordedByAPreviousServerProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        string eventsFilePath;
        await using (var serverA = await StartWorkspaceHarnessAsync(cts.Token))
        {
            var run = await CallToolAsync(serverA, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
            Assert.False(run.IsError ?? false);
            Assert.Equal("Pass", GetStructuredContent(run).GetProperty("verdict").GetString());

            eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString()!;
        }

        // Everything server A held is gone at this point. A fresh server against the same workspace
        // root is the closest in-process equivalent of a restarted `vouchfx-mcp` process.
        await using var serverB = await StartWorkspaceHarnessAsync(cts.Token);

        var explain = await CallToolAsync(serverB, "explain_run", [], cts.Token);

        Assert.False(explain.IsError ?? false);
        var diagnosis = GetStructuredContent(explain);
        Assert.Equal("Pass", diagnosis.GetProperty("verdict").GetString());
        Assert.Equal(eventsFilePath, diagnosis.GetProperty("eventsFilePath").GetString());
    }

    /// <summary>
    /// The story's first Gherkin scenario, at the level US-S3-01 can reach: the persisted entry's
    /// own <c>status</c> and <c>outcome</c> fields — the two <c>get_run_status</c> will project —
    /// are what was recorded before the restart.
    /// </summary>
    /// <remarks>
    /// Asserted against the registry's own read path — a production <see cref="FileRunRegistry"/>
    /// pointed at the same <see cref="Workspace.OutputDir"/> — which pins what survives on DISK. The
    /// companion above
    /// (<see cref="ARecordedRunIsReadableThroughGetRunStatusAndListRuns_AfterTheServerThatWroteItIsGone"/>)
    /// now pins the same fact through US-S3-03's tools, over the wire. Both are kept: this one fails
    /// if persistence breaks, that one fails if the tools stop reading it, and neither implies the
    /// other.
    /// </remarks>
    [Fact]
    public async Task ARecordedRunsStatusAndOutcomeSurviveTheServerThatWroteThem()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        await using (var serverA = await StartWorkspaceHarnessAsync(cts.Token, RunVerdict.Fail))
        {
            var run = await CallToolAsync(serverA, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
            Assert.False(run.IsError ?? false);
            Assert.Equal("Fail", GetStructuredContent(run).GetProperty("verdict").GetString());
        }

        var entry = new FileRunRegistry(_workspace.OutputDir, _workspace).MostRecentFinishedRun();

        Assert.NotNull(entry);
        Assert.Equal(RunRegistryStatus.Completed, entry.Status);

        // The MCP response vocabulary, not the engine's `FAIL` wire token — see
        // RunRegistryEntry.Outcome.
        Assert.Equal("Fail", entry.Outcome);
        Assert.NotNull(entry.FinishedAtUtc);
        Assert.Equal([_suitePath], entry.SpecPaths);
        Assert.Empty(entry.Labels);
    }

    /// <summary>
    /// The leg US-S3-01 deliberately deferred, now closed: a run recorded before a restart is
    /// readable through <c>get_run_status</c> and appears in <c>list_runs</c> from a server that never
    /// saw it run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this belongs here rather than in <c>RealRunLifecycleMcpTests</c>.</b> The restart claim
    /// is the property of THIS class — two independent harnesses, nothing but a directory crossing
    /// between them — and reproducing that construction elsewhere would be a second copy of the one
    /// thing that makes it a proof. US-S3-01 asserted it against a production
    /// <see cref="FileRunRegistry"/> instead, with a comment saying the tools were US-S3-03's; those
    /// tools now exist, so the assertion moves onto the surface a HOST actually uses (a spec review's
    /// recommendation). The registry-level assertion above stays: it pins the persisted bytes, this
    /// one pins the wire.
    /// </para>
    /// <para>
    /// <b>Both tools, not one.</b> They read the registry differently — <c>get_run_status</c> looks
    /// one id up, <c>list_runs</c> walks the output directory and pages — so a persistence fault that
    /// broke the directory walk while leaving direct lookups working (or the reverse) would be missed
    /// by either alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARecordedRunIsReadableThroughGetRunStatusAndListRuns_AfterTheServerThatWroteItIsGone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        string runId;
        string eventsFilePath;
        await using (var serverA = await StartWorkspaceHarnessAsync(cts.Token))
        {
            var run = await CallToolAsync(
                serverA,
                "run_suite",
                new()
                {
                    ["path"] = _suitePath,
                    ["labels"] = new Dictionary<string, object?> { ["trigger"] = "restart-probe" },
                },
                cts.Token);
            Assert.False(run.IsError ?? false);

            runId = GetStructuredContent(run).GetProperty("runId").GetString()!;
            eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString()!;
        }

        // Server A is entirely gone — host stopped, pipes completed, every in-process object with it.
        // Anything B can see came off the filesystem.
        await using var serverB = await StartWorkspaceHarnessAsync(cts.Token);

        var status = await CallToolAsync(serverB, "get_run_status", new() { ["runId"] = runId }, cts.Token);
        Assert.False(status.IsError ?? false);

        var recorded = GetStructuredContent(status).GetProperty("run");
        Assert.Equal(runId, recorded.GetProperty("runId").GetString());
        Assert.Equal(RunRegistryStatus.Completed, recorded.GetProperty("status").GetString());
        Assert.Equal(nameof(RunVerdict.Pass), recorded.GetProperty("outcome").GetString());

        // The hand-off survives too: B can point get_run_events/explain_run at A's stream.
        Assert.Equal(eventsFilePath, recorded.GetProperty("eventsFilePath").GetString());
        Assert.Equal("restart-probe", recorded.GetProperty("labels").GetProperty("trigger").GetString());

        // And the run is discoverable without knowing its id at all — which is what a host that lost
        // its runId across the restart actually needs, and the whole reason list_runs exists.
        var page = GetStructuredContent(
            await CallToolAsync(serverB, "list_runs", new() { ["label"] = "trigger=restart-probe" }, cts.Token));

        var listed = Assert.Single(page.GetProperty("runs").EnumerateArray());
        Assert.Equal(runId, listed.GetProperty("runId").GetString());
        Assert.Equal(RunRegistryStatus.Completed, listed.GetProperty("status").GetString());
        Assert.Equal(nameof(RunVerdict.Pass), listed.GetProperty("outcome").GetString());
    }

    /// <summary>
    /// With a workspace configured, a run's artefacts live INSIDE the workspace — which is what
    /// makes restart survival a property of the layout rather than an accident of the OS temp
    /// directory's retention policy.
    /// </summary>
    [Fact]
    public async Task WorkspaceConfigured_RunArtefactsArePlacedUnderTheWorkspaceOutputDirectory()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await StartWorkspaceHarnessAsync(cts.Token);

        var run = await CallToolAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
        Assert.False(run.IsError ?? false);

        var eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString()!;
        var entry = new FileRunRegistry(_workspace.OutputDir, _workspace).MostRecentFinishedRun();

        Assert.NotNull(entry);
        Assert.Equal(Path.Combine(_workspace.OutputDir, entry.RunId, FileRunRegistry.EventsFileName), eventsFilePath);
        Assert.True(File.Exists(eventsFilePath));
        Assert.True(File.Exists(Path.Combine(_workspace.OutputDir, entry.RunId, FileRunRegistry.EntryFileName)));

        // Inside the root, so US-S3-08's containment passes over this server's own events file on its
        // merits. This is the property that let explain_run's two containment exemptions be RETIRED
        // rather than kept: with artefacts inside the workspace there is nothing left for them to do.
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, eventsFilePath, StringComparison.OrdinalIgnoreCase);
    }

    // ── No-workspace compatibility: a host that never opted in sees no new files ────────────────

    [Fact]
    public async Task NoWorkspace_NothingIsPersistedAndTheEventsFileStaysInTheOsTempDirectory()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string eventsFilePath;
        await using (var serverA = await McpTestHarness.StartAsync(
            cts.Token, suiteRunner: FakeSuiteRunner.Succeeding([], PassingEventsFileContent, exitCode: 0)))
        {
            var run = await CallToolAsync(serverA, "run_suite", new() { ["path"] = _suitePath }, cts.Token);
            Assert.False(run.IsError ?? false);
            eventsFilePath = GetStructuredContent(run).GetProperty("eventsFilePath").GetString()!;
        }

        // US-S3-08's compatibility rule, extended to persistence: no --workspace means no directory
        // this server was given permission to write into, so it creates none. Only the OS temp
        // events file — pre-existing, documented behaviour — appears.
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(eventsFilePath));
        Assert.False(Directory.Exists(_workspace.OutputDir));
        Assert.False(Directory.Exists(Path.Combine(_root, ".vouchfx")));
        Assert.Equal(["orders.e2e.yaml"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));

        // And the session-scoped default is unchanged: a NEW server has no memory of that run.
        await using var serverB = await McpTestHarness.StartAsync(cts.Token);
        var explain = await CallToolAsync(serverB, "explain_run", [], cts.Token);
        Assert.True(explain.IsError ?? false);
        Assert.Equal("VFX-E-1601", ErrorCodeOf(explain));

        TryDelete(eventsFilePath);
    }

    // ── The registry's own storage failing is a CATALOGUED error, not a stack trace ─────────────

    /// <summary>
    /// US-S3-01 made <c>run_suite</c>'s first disk-touching action its own bookkeeping: the registry
    /// records the run before any gate that could produce a verdict. A read-only workspace root or an
    /// exhausted volume must therefore surface as <c>VFX-E-1502</c> with <c>isError: true</c> — not as
    /// a bare <see cref="IOException"/> escaping the tool handler with a stack trace attached.
    /// </summary>
    /// <remarks>
    /// The failure is INJECTED rather than provoked from a real unwritable directory — see
    /// <see cref="UnwritableRunRegistry"/> for why that would make a taxonomy test depend on the CI
    /// runner's identity. What is exercised for real here is everything downstream of the throw: the
    /// orchestrator's catch, <c>RunSuiteTool</c>'s outcome mapping, the catalogue lookup that supplies
    /// <c>retryable</c>/<c>docsUrl</c>, and the MCP wire shape.
    /// </remarks>
    [Fact]
    public async Task RunSuite_WhenTheRegistryCannotRecordTheRun_ReturnsVfxE1502AndNeverAStackTrace()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,

            // If the runner is ever reached, the gate ordering regressed: the registry write happens
            // before the spawn, so nothing must run.
            suiteRunner: FakeSuiteRunner.NeverExpectedToRun(),
            runRegistry: UnwritableRunRegistry.WithAccessDenied(),
            workspace: _workspace);

        var run = await CallToolAsync(harness, "run_suite", new() { ["path"] = _suitePath }, cts.Token);

        Assert.True(run.IsError ?? false);
        Assert.Equal("VFX-E-1502", ErrorCodeOf(run));

        var errorJson = Assert.IsType<TextContentBlock>(Assert.Single(run.Content)).Text;
        using var error = JsonDocument.Parse(errorJson);

        // Retryable, per the catalogue: the producing conditions are host storage availability, which
        // clears. Taken from VfxCodeCatalogue rather than restated at the call site, which is what
        // this assertion is really pinning.
        Assert.True(error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.EndsWith("VFX-E-1502.html", error.RootElement.GetProperty("docsUrl").GetString(), StringComparison.Ordinal);

        var message = error.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("could not be recorded", message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnauthorizedAccessException), message, StringComparison.Ordinal);

        // No stack trace, and no BCL exception message quoted back: filesystem exceptions routinely
        // embed a full path the caller never named, so only the TYPE name crosses this boundary.
        Assert.DoesNotContain("   at ", errorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Access to the path is denied", errorJson, StringComparison.Ordinal);

        // No RUN was recorded, because nothing got as far as needing to be.
        //
        // Since US-S3-04 this is asserted as "no run directory" rather than the older "the output
        // directory does not exist at all". The claim ahead of the registry — spec §4.6's lock at
        // <outputDir>/.lock — creates that directory before StartRun is ever attempted, and it has
        // to: a lock file cannot live in a directory that does not exist, and the claim must be
        // settled BEFORE a run id is minted or two processes would each mint one. So the directory is
        // now created by a component whose write succeeded, while the component whose write failed
        // left nothing behind. The lock is also released again, which is what the second assertion
        // checks — a rejected call must not leave the workspace claimed.
        Assert.Empty(Directory.EnumerateDirectories(_workspace.OutputDir));

        var probe = new WorkspaceRunLock(_workspace.OutputDir, _workspace);
        var reacquired = Assert.IsType<RunLockResult.Acquired>(probe.TryAcquire());
        reacquired.Release.Dispose();
    }

    // ── Secret hygiene against the registry's on-disk bytes (plan §2.7 invariant 4) ─────────────

    /// <summary>
    /// The story's third Gherkin scenario. A run whose suite declares a <c>${secret:…}</c> reference
    /// completes; the registry's on-disk representation is then read back as raw bytes and held to
    /// the invariant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the "the literal reference survives" half is asserted, and why.</b> The Gherkin
    /// phrases it as the registry entry containing <c>${secret:db-password}</c>; the registry
    /// deliberately stores no suite or log CONTENT at all (see <see cref="IRunRegistry"/>), so the
    /// literal cannot appear there — and its absence is the stronger property, not a weaker one.
    /// This test therefore asserts the surviving-literal half where the reference legitimately lives
    /// — the events stream this server relays verbatim and never resolves — and holds the registry
    /// itself to a strictly stronger standard than the scenario asks for: neither the resolved value
    /// NOR any suite/log content appears in its bytes, and its property set is exactly the closed
    /// metadata set.
    /// </para>
    /// <para>
    /// The sentinel is placed in THIS SERVER's own process environment, mirroring
    /// <see cref="RealSecretHygieneMcpTests"/>: <see cref="FakeSuiteRunner"/> spawns nothing and
    /// resolves nothing, so a resolved value reaching the registry could only be this server
    /// resolving a reference it must never resolve.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheRegistrysOnDiskBytesCarryNoResolvedSecretAndNoSuiteOrLogContent()
    {
        Environment.SetEnvironmentVariable(SecretSentinelName, SecretSentinelValue);

        var secretSuitePath = Path.Combine(_root, "secretful.e2e.yaml");
        File.WriteAllText(secretSuitePath, $"""
            metadata:
              name: "Orders API smoke test"
              owner: "platform-team"

            steps:
              - id: check-health
                type: http.rest
                description: "Authenticates with {SecretReference} before probing health."
                target: orders-api
                method: GET
                path: /health
            """);

        // $$$ (three) rather than $$: the JSON's own `"}}` tail is two consecutive literal closing
        // braces, which a $$-interpolated raw string would read as an interpolation terminator.
        var eventsWithReference =
            $$$"""
            {"type":"step-completed","stepId":"check-health","verdict":"PASS","durationMs":50,"observation":{"auth":"{{{SecretReference}}}"}}
            {"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await McpTestHarness.StartAsync(
            cts.Token,
            suiteRunner: FakeSuiteRunner.Succeeding([], eventsWithReference, exitCode: 0),
            workspace: _workspace);

        var run = await CallToolAsync(harness, "run_suite", new() { ["path"] = secretSuitePath }, cts.Token);
        Assert.False(run.IsError ?? false);

        var entry = new FileRunRegistry(_workspace.OutputDir, _workspace).MostRecentFinishedRun();
        Assert.NotNull(entry);

        // The reference is relayed, never resolved — asserted on the artefact that legitimately
        // carries suite content.
        Assert.Contains(SecretReference, await File.ReadAllTextAsync(entry.EventsFilePath, cts.Token), StringComparison.Ordinal);

        var entryPath = Path.Combine(_workspace.OutputDir, entry.RunId, FileRunRegistry.EntryFileName);
        var entryJson = await File.ReadAllTextAsync(entryPath, cts.Token);

        Assert.DoesNotContain(SecretSentinelValue, entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretSentinelName, entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${secret:", entryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("scenario-completed", entryJson, StringComparison.Ordinal);
        Assert.DoesNotContain("observation", entryJson, StringComparison.Ordinal);

        // Structural, not just textual: the entry's property set is closed, so a future field
        // carrying log or environment content fails here rather than slipping past a string search.
        using var document = JsonDocument.Parse(entryJson);
        Assert.Equal(
            ["eventsFilePath", "finishedAt", "labels", "outcome", "runId", "specPaths", "startedAt", "status"],
            document.RootElement.GetProperty("run").EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        // Nothing else in the whole output directory carries the sentinel either — the run.json is
        // the only thing the registry itself writes, but scanning the tree is what would catch a
        // future sidecar file added beside it.
        foreach (var file in Directory.EnumerateFiles(_workspace.OutputDir, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(path, entry.EventsFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain(SecretSentinelValue, await File.ReadAllTextAsync(file, cts.Token), StringComparison.OrdinalIgnoreCase);
        }
    }

    private Task<McpTestHarness> StartWorkspaceHarnessAsync(CancellationToken cancellationToken, RunVerdict verdict = RunVerdict.Pass)
    {
        var events = verdict == RunVerdict.Pass
            ? PassingEventsFileContent
            : """
                {"type":"step-completed","stepId":"check-health","verdict":"FAIL","durationMs":50}
                {"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}
                """;

        return McpTestHarness.StartAsync(
            cancellationToken,
            suiteRunner: FakeSuiteRunner.Succeeding([], events, exitCode: verdict == RunVerdict.Pass ? 0 : 1),
            workspace: _workspace);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp-file hygiene only.
        }
    }

    private static ValueTask<CallToolResult> CallToolAsync(
        McpTestHarness harness, string toolName, Dictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        harness.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

    private static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent
            ?? throw new InvalidOperationException("Expected the tool result to carry StructuredContent.");

    private static string? ErrorCodeOf(CallToolResult result)
    {
        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));

        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.TryGetProperty("code", out var value) ? value.GetString() : null;
    }
}
