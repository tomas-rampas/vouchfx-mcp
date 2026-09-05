namespace Vouchfx.Mcp.Tests;

/// <summary>
/// US-S3-08 / spec §4.2: <see cref="Workspace"/> resolution — the defaults, the canonicalisation,
/// the optional config path, and the fail-closed command-line parse.
/// </summary>
/// <remarks>
/// Covers the story's first Gherkin scenario ("A workspace is resolved at server start with its
/// documented defaults") directly. The scenario is written against <c>--workspace "/repo"</c>; these
/// tests use a real temp root instead, because <see cref="Path.GetFullPath(string)"/> resolves a
/// rooted-but-driveless path like <c>/repo</c> against the current drive on Windows, which would
/// make the assertion platform-dependent for no gain. What the scenario actually says — root as
/// given, <c>&lt;root&gt;/e2e</c>, <c>&lt;root&gt;/.vouchfx/runs</c> — is asserted exactly.
/// </remarks>
public class WorkspaceTests : IDisposable
{
    private readonly string _root;

    public WorkspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-mcp-workspace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Test hygiene only — a temp directory this run created. Never worth failing a green
            // assertion over.
        }
    }

    [Fact]
    public void Resolve_AppliesSpecSection42Defaults()
    {
        var workspace = Workspace.Resolve(_root);

        Assert.Equal(_root, workspace.Root);
        Assert.Equal(Path.Combine(_root, "e2e"), workspace.SpecsDir);
        Assert.Equal(Path.Combine(_root, ".vouchfx", "runs"), workspace.OutputDir);
    }

    [Fact]
    public void Resolve_NoConfigFilePresent_LeavesConfigPathUnset()
    {
        // Spec §4.2 types configPath as OPTIONAL. "Absent" must be distinguishable from "a path to a
        // file that happens not to exist", so the property is null rather than a speculative path.
        Assert.Null(Workspace.Resolve(_root).ConfigPath);
    }

    [Fact]
    public void Resolve_ConfigFilePresent_PointsConfigPathAtIt()
    {
        var configPath = Path.Combine(_root, "vouchfx.config.json");
        File.WriteAllText(configPath, "{}");

        Assert.Equal(configPath, Workspace.Resolve(_root).ConfigPath);
    }

    [Fact]
    public void Resolve_TrailingSeparatorOrTraversalSegments_AreCanonicalisedAway()
    {
        var noisy = Path.Combine(_root, "sub", "..") + Path.DirectorySeparatorChar;

        var workspace = Workspace.Resolve(noisy);

        Assert.Equal(_root, workspace.Root);
        Assert.False(
            Path.EndsInDirectorySeparator(workspace.Root),
            "Root must carry no trailing separator: PathSafetyGuard's containment comparison appends "
            + "one itself, and a doubled separator would make every contained path look like an escape.");
    }

    [Fact]
    public void Resolve_RelativeRoot_IsMadeAbsolute()
    {
        // Spec §4.2 says root is an absolute path. A host may still spell it relatively.
        var workspace = Workspace.Resolve(".");

        Assert.True(Path.IsPathFullyQualified(workspace.Root));
    }

    [Fact]
    public void Resolve_CreatesNothingOnDisk()
    {
        // The read-only invariant (CLAUDE.md; ReadOnlySourceGuardTests holds it structurally). US-S3-01
        // owns whatever the run registry needs to create under OutputDir — resolution itself must be
        // pure path computation plus the one config-existence read.
        var workspace = Workspace.Resolve(_root);

        Assert.False(Directory.Exists(workspace.SpecsDir));
        Assert.False(Directory.Exists(workspace.OutputDir));
        Assert.Equal(new[] { _root }, EnumerateTree(_root));
    }

    /// <param name="joinedArgs">
    /// The argument vector, '|'-separated. A joined string rather than a <c>string[]</c> because
    /// xUnit's <c>[InlineData]</c> takes <c>params object[]</c> and cannot carry an array argument
    /// without wrapping ceremony that would obscure the cases themselves.
    /// </param>
    [Theory]
    [InlineData("")]
    [InlineData("--validate-worker|x.e2e.yaml")]
    [InlineData("--workspacey|value")]
    public void TryParseCommandLine_NoWorkspaceFlag_YieldsNoWorkspaceAndNoError(string joinedArgs)
    {
        Assert.True(Workspace.TryParseCommandLine(Split(joinedArgs), out var workspace, out var error));

        // Null is the fully supported legacy mode, not a failure — plan §2.1's whole point.
        Assert.Null(workspace);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCommandLine_SeparateValue_ResolvesTheWorkspace()
    {
        Assert.True(Workspace.TryParseCommandLine(["--workspace", _root], out var workspace, out var error));

        Assert.Null(error);
        Assert.Equal(_root, workspace!.Root);
    }

    [Fact]
    public void TryParseCommandLine_EqualsForm_ResolvesTheWorkspace()
    {
        // Accepted deliberately: unsupported, it would be silently ignored, leaving containment off
        // while the host believed it was on.
        Assert.True(Workspace.TryParseCommandLine([$"--workspace={_root}"], out var workspace, out var error));

        Assert.Null(error);
        Assert.Equal(_root, workspace!.Root);
    }

    /// <param name="joinedArgs">See <see cref="TryParseCommandLine_NoWorkspaceFlag_YieldsNoWorkspaceAndNoError"/>.</param>
    [Theory]
    // The flag is the last argument — there is no value at all.
    [InlineData("--workspace")]
    // The next token is plainly another flag, not a directory.
    [InlineData("--workspace|--verbose")]
    // Present but empty, in either spelling.
    [InlineData("--workspace|   ")]
    [InlineData("--workspace=")]
    public void TryParseCommandLine_FlagWithoutAUsableValue_IsStartupFatal(string joinedArgs)
    {
        Assert.False(Workspace.TryParseCommandLine(Split(joinedArgs), out var workspace, out var error));

        Assert.Null(workspace);
        Assert.NotNull(error);
        Assert.Contains("--workspace", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseCommandLine_RepeatedFlag_IsStartupFatal()
    {
        // Last-wins would resolve, but an ambiguous root is not a root: which directory containment
        // is enforced against must never depend on argument order.
        Assert.False(
            Workspace.TryParseCommandLine(["--workspace", _root, "--workspace", Path.GetTempPath()], out var workspace, out var error));

        Assert.Null(workspace);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseCommandLine_ValueThatCannotBeCanonicalised_IsStartupFatalRatherThanThrowing()
    {
        // A NUL byte is rejected by Path.GetFullPath on every platform. The point of the assertion is
        // not the specific input but that Resolve's throw is converted into a reportable one-liner
        // instead of escaping as an unhandled exception at startup.
        Assert.False(Workspace.TryParseCommandLine(["--workspace", "bad\0path"], out var workspace, out var error));

        Assert.Null(workspace);
        Assert.NotNull(error);

        // Sanitised for a terminal — the value is echoed through VfxCode.SanitiseForEcho, so no raw
        // control byte reaches stderr.
        Assert.DoesNotContain('\0', error!);
    }

    [Fact]
    public void Resolve_NullOrBlankRoot_Throws() =>
        Assert.Throws<ArgumentException>(() => Workspace.Resolve("   "));

    // ── A network/UNC root is refused before any filesystem call ────────────────────────────────

    /// <summary>
    /// A security review's MAJOR finding: <c>--workspace \\attacker\share</c> resolved fine and then
    /// had <see cref="Workspace.Resolve"/>'s <c>File.Exists</c> config probe fire an outbound
    /// SMB/NTLM authentication at the attacker's host — the exact forced-authentication primitive
    /// <c>PathSafetyGuard</c> exists to prevent, reached through the flag meant to TIGHTEN path
    /// safety.
    /// </summary>
    /// <remarks>
    /// <b>These assertions touch no network, and cannot.</b> The rejection is decided by
    /// <c>PathSafetyGuard.IsNetworkPath</c> — pure string inspection — placed BEFORE the probe, so a
    /// regression that removed it would be caught by the assertion failing, not by a test hanging on
    /// a DNS/SMB timeout. (A regression would also make the probe fire, which is why the hostnames
    /// below are non-routable rather than merely fictional.)
    /// </remarks>
    [Theory]
    [InlineData(@"\\attacker-host\share")]
    [InlineData("//attacker-host/share")]
    [InlineData(@"\\attacker-host\share\repo")]
    [InlineData(@"\\?\UNC\attacker-host\share")]
    public void Resolve_NetworkRoot_ThrowsBeforeAnyFilesystemProbe(string uncRoot)
    {
        var thrown = Assert.Throws<ArgumentException>(() => Workspace.Resolve(uncRoot));

        Assert.Contains("network/UNC", thrown.Message, StringComparison.Ordinal);
    }

    /// <param name="joinedArgs">See <see cref="TryParseCommandLine_NoWorkspaceFlag_YieldsNoWorkspaceAndNoError"/>.</param>
    [Theory]
    [InlineData(@"--workspace|\\attacker-host\share")]
    [InlineData("--workspace|//attacker-host/share")]
    [InlineData(@"--workspace=\\attacker-host\share")]
    public void TryParseCommandLine_NetworkRoot_IsStartupFatalAndSaysWhy(string joinedArgs)
    {
        Assert.False(Workspace.TryParseCommandLine(Split(joinedArgs), out var workspace, out var error));

        Assert.Null(workspace);
        Assert.NotNull(error);

        // Not the generic "not a usable directory path (ArgumentException)" fallback: an operator
        // who typed a UNC root is told what was wrong with it.
        Assert.Contains("network/UNC", error!, StringComparison.Ordinal);
        Assert.Contains("--workspace", error, StringComparison.Ordinal);
    }

    private static string[] Split(string joinedArgs) =>
        joinedArgs.Length == 0 ? [] : joinedArgs.Split('|');

    private static IEnumerable<string> EnumerateTree(string root) =>
        new[] { root }
            .Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            .OrderBy(entry => entry, StringComparer.Ordinal);
}
