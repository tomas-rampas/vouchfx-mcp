using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Pins the security-relevant properties of <c>.github/workflows/dependabot-lockfiles.yml</c>,
/// the workflow that regenerates stale <c>packages.lock.json</c> files on Dependabot NuGet pull
/// requests and pushes them to the pull request branch.
/// </summary>
/// <remarks>
/// A static read of the workflow definition: it proves the file still says what its design
/// requires, not that GitHub behaves as designed. Only a real Dependabot pull request proves the
/// latter. Each assertion names the attack or regression it closes, because the workflow holds a
/// push credential and runs pull request MSBuild logic in a sibling job.
/// </remarks>
public partial class DependabotLockfileWorkflowSourceGuardTests
{
    private const string WorkflowFile = "dependabot-lockfiles.yml";

    private static readonly string[] JobNames = ["refresh", "commit"];

    [Fact]
    public void Trigger_IsPullRequestOnly_NeverPullRequestTarget()
    {
        var root = LoadWorkflow(WorkflowFile);
        var on = Mapping(root, "on");

        Assert.Equal(["pull_request"], on.Children.Keys.Select(Scalar).ToArray());
        // Parsed content only: the header comment names pull_request_target to forbid it.
        Assert.DoesNotContain("pull_request_target", Text(root));

        var pullRequest = Mapping(on, "pull_request");
        Assert.Equal(
            ["opened", "reopened", "synchronize"],
            Sequence(pullRequest, "types").Select(Scalar).Order(StringComparer.Ordinal).ToArray());

        var paths = Sequence(pullRequest, "paths").Select(Scalar).ToArray();
        Assert.Contains("**/*.csproj", paths);
        Assert.Contains("**/packages.lock.json", paths);
        Assert.Contains("**/Directory.*.props", paths);
    }

    [Fact]
    public void Permissions_AreReadOnlyEverywhere_AndNoJobCanWrite()
    {
        var root = LoadWorkflow(WorkflowFile);
        Assert.Equal("read", Scalar(Mapping(root, "permissions").Children[new YamlScalarNode("contents")]));
        Assert.Single(Mapping(root, "permissions").Children);

        foreach (var jobName in JobNames)
        {
            var permissions = Mapping(Job(root, jobName), "permissions");
            Assert.Equal("read", Scalar(permissions.Children[new YamlScalarNode("contents")]));
            Assert.Single(permissions.Children);
        }

        // The push is authenticated by the stored token, never by GITHUB_TOKEN, so no scope may
        // be raised to write anywhere in the file (a write scope would also be reachable by the
        // job that runs pull request MSBuild logic if it ever moved there).
        Assert.DoesNotMatch(WriteScopeRegex(), File.ReadAllText(WorkflowPath(WorkflowFile)));
    }

    [Fact]
    public void EveryJob_IsGatedToDependabotActor_Author_SameRepositoryBranch_AndNuGetBranchPrefix()
    {
        var root = LoadWorkflow(WorkflowFile);
        foreach (var jobName in JobNames)
        {
            var condition = Scalar(Job(root, jobName).Children[new YamlScalarNode("if")]);

            // Author: the pull request is Dependabot's.
            Assert.Contains("github.event.pull_request.user.login == 'dependabot[bot]'", condition);
            // Actor: Dependabot made this push. A human commit on a Dependabot branch makes the
            // human the actor, so the push credential is never used on human-edited content, and
            // the workflow's own push (made by the token owner) cannot re-trigger it.
            Assert.Contains("github.actor == 'dependabot[bot]'", condition);
            // Same repository: a fork's head repository can never equal this one.
            Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", condition);
            Assert.Contains("startsWith(github.event.pull_request.head.ref, 'dependabot/nuget/')", condition);
        }

        Assert.Contains("needs.refresh.outputs.changed == 'true'",
            Scalar(Job(root, "commit").Children[new YamlScalarNode("if")]));
    }

    [Fact]
    public void EveryJob_HasTimeout_AndWorkflowIsConcurrencyGroupedPerPullRequest()
    {
        var root = LoadWorkflow(WorkflowFile);
        foreach (var jobName in JobNames)
        {
            Assert.True(int.Parse(Scalar(Job(root, jobName).Children[new YamlScalarNode("timeout-minutes")]), System.Globalization.CultureInfo.InvariantCulture) is > 0 and <= 20);
        }

        var concurrency = Mapping(root, "concurrency");
        Assert.Contains("github.event.pull_request.number", Scalar(concurrency.Children[new YamlScalarNode("group")]));
    }

    [Fact]
    public void EveryAction_IsPinnedByFullCommitSha_MatchingThePinsOtherWorkflowsUse()
    {
        var uses = Uses(LoadWorkflow(WorkflowFile));
        Assert.NotEmpty(uses);

        var others = Directory.EnumerateFiles(Path.GetDirectoryName(WorkflowPath(WorkflowFile))!, "*.yml")
            .Where(file => Path.GetFileName(file) != WorkflowFile)
            .SelectMany(file => Uses(LoadWorkflowFromPath(file)))
            .GroupBy(reference => reference.Split('@')[0])
            .ToDictionary(group => group.Key, group => group.Distinct().ToArray());

        foreach (var reference in uses.Distinct())
        {
            Assert.Matches(PinnedActionRegex(), reference);
            var action = reference.Split('@')[0];
            if (others.TryGetValue(action, out var existing))
            {
                Assert.True(existing.Contains(reference),
                    $"{reference} differs from the pin(s) already used elsewhere: {string.Join(", ", existing)}.");
            }
        }
    }

    [Fact]
    public void EveryCheckout_DoesNotPersistCredentials_AndChecksOutTheEventHeadSha()
    {
        var root = LoadWorkflow(WorkflowFile);
        var checkouts = JobNames
            .SelectMany(jobName => Steps(Job(root, jobName)))
            .Where(step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses)
                && Scalar(uses).StartsWith("actions/checkout@", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, checkouts.Length);

        foreach (var checkout in checkouts)
        {
            var with = Mapping(checkout, "with");
            Assert.Equal("false", Scalar(with.Children[new YamlScalarNode("persist-credentials")]));
            // Never the merge ref, and never a branch name that can move under the run.
            Assert.Equal("${{ github.event.pull_request.head.sha }}", Scalar(with.Children[new YamlScalarNode("ref")]));
        }
    }

    [Fact]
    public void NoRunScript_InterpolatesExpressions()
    {
        // Untrusted values (branch names, titles) must reach shell scripts through env:, never
        // by ${{ }} substitution into the script text.
        var root = LoadWorkflow(WorkflowFile);
        foreach (var jobName in JobNames)
        {
            foreach (var step in Steps(Job(root, jobName)))
            {
                if (step.Children.TryGetValue(new YamlScalarNode("run"), out var run))
                {
                    Assert.DoesNotContain("${{", Scalar(run));
                }
            }
        }
    }

    [Fact]
    public void PushSecret_IsReachableOnlyFromTheCommitJob_AndTheRestoreJobHasNone()
    {
        var root = LoadWorkflow(WorkflowFile);
        var refreshText = Text(Job(root, "refresh"));
        var commitText = Text(Job(root, "commit"));

        Assert.DoesNotContain("secrets.", refreshText);
        Assert.DoesNotContain("github.token", refreshText);
        Assert.DoesNotContain("git push", refreshText);
        Assert.Contains("secrets.DEPENDABOT_LOCKFILE_PUSH_TOKEN", commitText);
        Assert.DoesNotContain("secrets.GITHUB_TOKEN", commitText);

        // Only the single push step may see the secret (plus the explicit presence check).
        var withSecret = Steps(Job(root, "commit"))
            .Where(step => Text(step).Contains("secrets.DEPENDABOT_LOCKFILE_PUSH_TOKEN", StringComparison.Ordinal))
            .Select(step => Scalar(step.Children[new YamlScalarNode("name")]))
            .ToArray();
        Assert.Equal(["Require the push token", "Commit and push"], withSecret);
    }

    [Fact]
    public void RestoreJob_RegeneratesWithoutLockedMode_AndCommitJobRunsNoDotnet()
    {
        var root = LoadWorkflow(WorkflowFile);

        var restore = Assert.Single(Steps(Job(root, "refresh")), step =>
            step.Children.TryGetValue(new YamlScalarNode("run"), out var run)
            && Scalar(run).StartsWith("dotnet restore", StringComparison.Ordinal));
        var command = Scalar(restore.Children[new YamlScalarNode("run")]);
        Assert.Equal("dotnet restore Vouchfx.Mcp.sln --force-evaluate", command);
        Assert.DoesNotContain("--locked-mode", command);

        // The job that holds the push credential executes nothing from the pull request: no SDK
        // setup and no dotnet/msbuild invocation, so restore-time MSBuild logic never runs beside it.
        var commitText = Text(Job(root, "commit"));
        Assert.DoesNotContain("setup-dotnet", commitText);
        // A command at the start of a script line (the error message text may still name the
        // manual remedy).
        Assert.DoesNotMatch(ToolInvocationRegex(), commitText);
    }

    [Fact]
    public void CommitJob_CommitsOnlyLockFiles()
    {
        var root = LoadWorkflow(WorkflowFile);
        var commitText = Text(Job(root, "commit"));

        // Artefact entries are accepted only when named packages.lock.json at a plain path ...
        Assert.Contains(@"packages\.lock\.json$", commitText);
        Assert.Contains("git ls-files --error-unmatch", commitText);
        Assert.Contains("[ -L \"$file\" ]", commitText);
        // ... the tree is checked to differ from HEAD in lock files only, and the finished
        // commit is checked again before pushing.
        Assert.Contains("git status --porcelain --untracked-files=all", commitText);
        Assert.Contains("git diff-tree --no-commit-id --name-only -r HEAD", commitText);
        // No blanket staging or committing.
        Assert.DoesNotMatch(BlanketStagingRegex(), commitText);
        // Plain push: a moved branch is refused, never overwritten.
        Assert.DoesNotMatch(ForcePushRegex(), commitText);
        Assert.DoesNotContain("+HEAD", commitText);
    }

    private static string WorkflowPath(string workflowFile) =>
        Path.Combine(SourceGuardScan.RepoRoot.FullName, ".github", "workflows", workflowFile);

    private static YamlMappingNode LoadWorkflow(string workflowFile) => LoadWorkflowFromPath(WorkflowPath(workflowFile));

    private static YamlMappingNode LoadWorkflowFromPath(string path)
    {
        using var reader = File.OpenText(path);
        var workflow = new YamlStream();
        workflow.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(workflow.Documents).RootNode);
    }

    private static YamlMappingNode Job(YamlMappingNode root, string jobName) => Mapping(Mapping(root, "jobs"), jobName);

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        Sequence(job, "steps").OfType<YamlMappingNode>();

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key) =>
        Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode parent, string key) =>
        Assert.IsType<YamlSequenceNode>(parent.Children[new YamlScalarNode(key)]).Children;

    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value!;

    /// <summary>Every scalar beneath <paramref name="node"/>, joined: keys and values alike.</summary>
    private static string Text(YamlNode node) => string.Join('\n', Scalars(node));

    private static IEnumerable<string> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar.Value ?? string.Empty],
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        YamlMappingNode mapping => mapping.Children.SelectMany(pair => Scalars(pair.Key).Concat(Scalars(pair.Value))),
        _ => [],
    };

    private static string[] Uses(YamlMappingNode root) =>
        Mapping(root, "jobs").Children.Values
            .OfType<YamlMappingNode>()
            .SelectMany(job => job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)
                ? ((YamlSequenceNode)steps).Children.OfType<YamlMappingNode>()
                : [])
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("uses")))
            .Select(step => Scalar(step.Children[new YamlScalarNode("uses")]))
            .ToArray();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")]
    private static partial Regex PinnedActionRegex();

    [GeneratedRegex(@"(?m)^\s*[\w-]+:\s*write\b|write-all")]
    private static partial Regex WriteScopeRegex();

    [GeneratedRegex(@"(?m)^\s*(dotnet|msbuild|nuget)\b")]
    private static partial Regex ToolInvocationRegex();

    [GeneratedRegex(@"--force(?!-evaluate)|--force-with-lease|\+HEAD")]
    private static partial Regex ForcePushRegex();

    [GeneratedRegex(@"git add (-A|--all|\.)|git commit (-a|--all)|git add -u")]
    private static partial Regex BlanketStagingRegex();
}
