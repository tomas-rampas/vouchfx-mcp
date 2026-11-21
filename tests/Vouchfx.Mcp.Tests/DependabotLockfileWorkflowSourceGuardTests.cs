using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Mcp.Tests;

/// <summary>
/// Pins the security-relevant properties of <c>.github/workflows/dependabot-lockfiles.yml</c>,
/// the workflow that regenerates stale <c>packages.lock.json</c> files on Dependabot NuGet pull
/// requests and pushes them to the pull request branch with the commit job's own
/// <c>GITHUB_TOKEN</c>.
/// </summary>
/// <remarks>
/// A static read of the workflow definition: it proves the file still says what its design
/// requires, not that GitHub behaves as designed. Only a real Dependabot pull request proves the
/// latter. Each assertion names the attack or regression it closes, because the workflow holds a
/// write-scoped token and runs pull request MSBuild logic in a sibling job.
/// The shell and Python in the commit job are not executed here (the test project has no
/// pattern for running bash, and Windows runners have none). Their behaviour was verified with
/// a harness that extracts the <c>run:</c> blocks and runs them in an offline Linux container
/// against valid and hostile artefacts (untracked path, symbolic link, <c>..</c>, non-JSON,
/// newline in a name, a package version not on the branch, planted git hooks and
/// <c>json.py</c>); the string checks below pin the lines that behaviour depends on.
/// </remarks>
public partial class DependabotLockfileWorkflowSourceGuardTests
{
    private const string WorkflowFile = "dependabot-lockfiles.yml";

    private const string DependabotGate =
        "github.event.pull_request.user.login == 'dependabot[bot]' && " +
        "github.actor == 'dependabot[bot]' && " +
        "github.event.pull_request.head.repo.full_name == github.repository && " +
        "startsWith(github.event.pull_request.head.ref, 'dependabot/nuget/')";

    private const string PushStepName = "Commit and push";

    private static readonly string[] JobNames = ["commit", "refresh"];

    [Fact]
    public void Trigger_IsPullRequestOnly_OnMain_NeverPullRequestTarget()
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
        Assert.Equal(["main"], Sequence(pullRequest, "branches").Select(Scalar).ToArray());
        Assert.Equal(
            ["**/*.csproj", "**/Directory.*.props", "**/Directory.*.targets", "**/packages.lock.json"],
            Sequence(pullRequest, "paths").Select(Scalar).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Jobs_AreExactlyRefreshAndCommit()
    {
        // Every other guarantee here is checked per job; a third job would escape all of them.
        var root = LoadWorkflow(WorkflowFile);
        Assert.Equal(JobNames, Mapping(root, "jobs").Children.Keys.Select(Scalar).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Permissions_AreReadEverywhere_ExceptContentsWriteOnTheCommitJobOnly()
    {
        // Read from the parsed YAML, so block and flow style (`{ contents: write }`) are alike,
        // and a scalar such as `write-all` fails the mapping cast.
        var root = LoadWorkflow(WorkflowFile);
        Assert.Equal(["contents=read"], Permissions(root));
        // The job that runs pull request MSBuild logic can never write.
        Assert.Equal(["contents=read"], Permissions(Job(root, "refresh")));
        // The push uses the commit job's own GITHUB_TOKEN, so that job, and only that job,
        // needs contents: write, and nothing else.
        Assert.Equal(["contents=write"], Permissions(Job(root, "commit")));
    }

    [Fact]
    public void EveryJob_IsGatedToDependabotActor_Author_SameRepositoryBranch_AndNuGetBranchPrefix()
    {
        // Whole-condition equality, whitespace collapsed: a clause dropped, `&&` turned into
        // `||` or a negation added fails; a reflowed line does not.
        //   author: the pull request is Dependabot's;
        //   actor: Dependabot made this push. A human commit makes the human the actor, and the
        //     workflow's own push is made as github-actions[bot], so it cannot re-trigger itself;
        //   same repository: a fork's head repository can never equal this one.
        var root = LoadWorkflow(WorkflowFile);
        Assert.Equal(DependabotGate, Condition(Job(root, "refresh")));
        Assert.Equal("needs.refresh.outputs.changed == 'true' && " + DependabotGate, Condition(Job(root, "commit")));
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
    public void EachJob_UsesOnlyItsAllowedFirstPartyActions()
    {
        // Any other action in the commit job would run with the write-scoped token in reach.
        var root = LoadWorkflow(WorkflowFile);
        Assert.Equal(
            ["actions/checkout", "actions/download-artifact"],
            ActionNames(Job(root, "commit")));
        Assert.Equal(
            ["actions/checkout", "actions/setup-dotnet", "actions/upload-artifact"],
            ActionNames(Job(root, "refresh")));
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
            foreach (var run in RunScripts(Job(root, jobName)))
            {
                Assert.DoesNotContain("${{", run);
            }
        }
    }

    [Fact]
    public void TheOnlySecret_IsGitHubToken_InTheEnvironmentOfTheSinglePushStep()
    {
        var root = LoadWorkflow(WorkflowFile);
        var text = Text(root);

        // One reference to `secrets` in the whole file: no stored credential, no toJSON(secrets),
        // no job-level or workflow-level env carrying the token.
        Assert.Single(SecretsReferenceRegex().Matches(text));
        Assert.DoesNotContain("github.token", text);
        var pushStep = Assert.Single(Steps(Job(root, "commit")), step => Name(step) == PushStepName);
        Assert.Equal("${{ secrets.GITHUB_TOKEN }}", Scalar(Mapping(pushStep, "env").Children[new YamlScalarNode("PUSH_TOKEN")]));

        Assert.False(Job(root, "commit").Children.ContainsKey(new YamlScalarNode("env")));
        Assert.False(Job(root, "refresh").Children.ContainsKey(new YamlScalarNode("env")));
        Assert.Equal(
            ["DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"],
            Mapping(root, "env").Children.Keys.Select(Scalar).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void RestoreJob_RegeneratesWithoutLockedMode_AndCommitJobRunsNothingFromThePullRequest()
    {
        var root = LoadWorkflow(WorkflowFile);

        var restore = Assert.Single(RunScripts(Job(root, "refresh")), run => run.StartsWith("dotnet restore", StringComparison.Ordinal));
        Assert.Equal("dotnet restore Vouchfx.Mcp.sln --force-evaluate", restore);
        Assert.DoesNotContain(RunScripts(Job(root, "refresh")), run => GitPushRegex().IsMatch(run));

        // The job that holds the write-scoped token executes nothing from the pull request: no SDK
        // setup, no dotnet/msbuild/nuget, no interpreter or script started from the checkout, at
        // the start of a line or after ;, &&, |, $( and the like.
        var commitScripts = string.Join('\n', RunScripts(Job(root, "commit")));
        Assert.DoesNotContain("setup-dotnet", Text(Job(root, "commit")));
        Assert.DoesNotMatch(ToolInvocationRegex(), commitScripts);
    }

    [Fact]
    public void Python_RunsIsolated_AndOnlyFromInlineSource()
    {
        // Without -I, python3 puts the current directory (the pull request checkout) first on
        // sys.path, so a planted json.py runs, and it could write .git/hooks used by the push.
        var root = LoadWorkflow(WorkflowFile);
        var scripts = string.Join('\n', JobNames.SelectMany(jobName => RunScripts(Job(root, jobName))));
        var invocations = PythonInvocationRegex().Matches(scripts);
        Assert.NotEmpty(invocations);
        Assert.All(invocations, invocation => Assert.Matches(IsolatedPythonRegex(), invocation.Value));
    }

    [Fact]
    public void NothingTracesTheScripts()
    {
        // Tracing would print the base64 credential and the command lines that carry it.
        Assert.DoesNotMatch(TracingRegex(), Text(LoadWorkflow(WorkflowFile)));
    }

    [Fact]
    public void CommitJob_PushesOnce_WithoutForce_ToThePullRequestBranch_CredentialInEnvironmentOnly()
    {
        var root = LoadWorkflow(WorkflowFile);
        var pushStep = Assert.Single(Steps(Job(root, "commit")), step => Name(step) == PushStepName);
        Assert.Equal("${{ github.event.pull_request.head.ref }}", Scalar(Mapping(pushStep, "env").Children[new YamlScalarNode("HEAD_REF")]));

        var pushes = Steps(Job(root, "commit"))
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .SelectMany(step => CodeLines(Scalar(step.Children[new YamlScalarNode("run")])))
            .Where(line => GitPushRegex().IsMatch(line))
            .ToArray();
        var push = Assert.Single(pushes);

        // Plain push of HEAD to the pull request's own branch, hooks off. This rejects -f,
        // --force*, a `+` refspec, a second refspec and any other destination such as main.
        Assert.Equal(
            "git -c core.hooksPath=/dev/null push origin \"HEAD:refs/heads/$HEAD_REF\"",
            GitCommandRegex().Match(push).Value.TrimEnd());
        Assert.DoesNotMatch(ForcePushRegex(), push);

        // The credential reaches git through GIT_CONFIG_* in its environment, never its argv
        // (readable in /proc/<pid>/cmdline) and never .git/config.
        Assert.Contains("GIT_CONFIG_COUNT=1 ", push);
        Assert.Contains("GIT_CONFIG_KEY_0=\"http.https://github.com/.extraheader\" ", push);
        Assert.Contains("GIT_CONFIG_VALUE_0=\"AUTHORIZATION: basic $auth\" ", push);
        Assert.Single(ExtraHeaderRegex().Matches(Text(root)));
        Assert.Contains("echo \"::add-mask::$auth\"", Scalar(pushStep.Children[new YamlScalarNode("run")]));
    }

    [Fact]
    public void CommitJob_CommitsOnlyValidatedLockFiles_WithTheDependabotSkipMarker()
    {
        var root = LoadWorkflow(WorkflowFile);
        var commit = Job(root, "commit");
        var download = Assert.Single(Steps(commit), step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses)
            && Scalar(uses).StartsWith("actions/download-artifact@", StringComparison.Ordinal));
        Assert.Equal("${{ runner.temp }}/lockfiles-in", Scalar(Mapping(download, "with").Children[new YamlScalarNode("path")]));

        var validate = Assert.Single(Steps(commit), step => Name(step) == "Validate and stage lock files");
        Assert.Equal("${{ runner.temp }}/lockfiles-in", Scalar(Mapping(validate, "env").Children[new YamlScalarNode("INCOMING")]));
        var validateLines = CodeLines(Scalar(validate.Children[new YamlScalarNode("run")]));

        // Artefact entries: a regular file, named packages.lock.json at a plain path (exact
        // pattern: no `..`, no dot-directories), already tracked, printed with %q.
        Assert.Contains("if [ -L \"$file\" ] || [ ! -f \"$file\" ]; then", validateLines);
        Assert.Contains(@"if ! [[ ""$rel"" =~ ^([A-Za-z0-9_][A-Za-z0-9._-]*/)*packages\.lock\.json$ ]]; then", validateLines);
        Assert.Contains("if ! git ls-files --error-unmatch -- \"$rel\" > /dev/null 2>&1; then", validateLines);
        Assert.Contains("shown=\"$(printf '%q' \"$rel\")\"", validateLines);
        // The tree differs from HEAD in modified lock files only; each is a lock document whose
        // packages already appear on the branch; only the changed files are staged, one way.
        Assert.Contains("if git status --porcelain --untracked-files=all | grep -v -E '^ M (.*/)?packages\\.lock\\.json$'; then", validateLines);
        Assert.Contains("is not already in a lock file on the branch.\")", string.Join('\n', validateLines));
        var adds = string.Join('\n', RunScripts(commit)).Split('\n').Where(line => GitAddRegex().IsMatch(line)).ToArray();
        Assert.Equal("git diff --name-only -z | xargs -0 git add --", Assert.Single(adds).Trim());

        var pushStep = Assert.Single(Steps(commit), step => Name(step) == PushStepName);
        var pushLines = CodeLines(Scalar(pushStep.Children[new YamlScalarNode("run")]));
        var gitCommit = Assert.Single(pushLines, line => GitCommitRegex().IsMatch(line));
        // [dependabot skip]: Dependabot otherwise stops rebasing a pull request with extra commits.
        Assert.StartsWith(
            "git -c core.hooksPath=/dev/null commit -m \"build(deps): regenerate packages.lock.json files [dependabot skip]\" -m ",
            gitCommit);
        Assert.EndsWith("--trailer \"Workflow-run: $run_url\"", gitCommit);
        Assert.DoesNotMatch(BlanketCommitRegex(), gitCommit);
        Assert.Contains("run_url=\"$GITHUB_SERVER_URL/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID\"", pushLines);
        Assert.Contains("git config user.name \"github-actions[bot]\"", pushLines);
        // The finished commit is checked again before pushing.
        Assert.Contains("if git diff-tree --no-commit-id --name-only -r HEAD | grep -v -E '(^|/)packages\\.lock\\.json$'; then", pushLines);
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

    private static string Name(YamlMappingNode step) =>
        step.Children.TryGetValue(new YamlScalarNode("name"), out var name) ? Scalar(name) : string.Empty;

    private static string[] RunScripts(YamlMappingNode job) =>
        Steps(job)
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("run")))
            .Select(step => Scalar(step.Children[new YamlScalarNode("run")]))
            .ToArray();

    /// <summary>Script lines with backslash continuations joined, trimmed, comments dropped.</summary>
    private static string[] CodeLines(string script) =>
        ContinuationRegex().Replace(script, string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

    private static string[] Permissions(YamlMappingNode owner) =>
        Mapping(owner, "permissions").Children
            .Select(pair => $"{Scalar(pair.Key)}={Scalar(pair.Value)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Condition(YamlMappingNode job) =>
        WhitespaceRegex().Replace(Scalar(job.Children[new YamlScalarNode("if")]), " ").Trim();

    private static string[] ActionNames(YamlMappingNode job) =>
        Steps(job)
            .Where(step => step.Children.ContainsKey(new YamlScalarNode("uses")))
            .Select(step => Scalar(step.Children[new YamlScalarNode("uses")]).Split('@')[0])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

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

    [GeneratedRegex(@"\\\n\s*")]
    private static partial Regex ContinuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\bsecrets\b")]
    private static partial Regex SecretsReferenceRegex();

    // A tool or interpreter at command position (line start, or after ; & | ( ` $( then do else
    // exec eval xargs env command), or a path executed from the checkout (./x, ../x).
    [GeneratedRegex(@"(?m)(?:^|[;&|(`]|\$\(|\b(?:then|do|else|exec|eval|xargs(?:\s+-\S+)*|env|command|time|nohup|sudo)\b)\s*(?:dotnet|msbuild|nuget|bash|sh|zsh|pwsh|source|node|npm|npx|perl|ruby|make|eval)\b|(?:^|[\s;&|(])\.{1,2}/|(?m)^\s*\.\s")]
    private static partial Regex ToolInvocationRegex();

    [GeneratedRegex(@"\bpython3?\b[^\n]*")]
    private static partial Regex PythonInvocationRegex();

    [GeneratedRegex(@"^python3 -I (?:-c |- )")]
    private static partial Regex IsolatedPythonRegex();

    [GeneratedRegex(@"\bset\s+-[a-wyz]*x|\bset\s+-o\s+xtrace|\bGIT_TRACE|\bGIT_CURL_VERBOSE|\bbash\s+-x")]
    private static partial Regex TracingRegex();

    [GeneratedRegex(@"\bgit\b[^\n]*\spush\b")]
    private static partial Regex GitPushRegex();

    [GeneratedRegex(@"\bgit\b.*?(?=;\s*then\s*$|$)")]
    private static partial Regex GitCommandRegex();

    [GeneratedRegex(@"--force|--mirror|--delete|--all|--tags|\s-[a-zA-Z]*[fd]\b|[\s""']\+[\w$]")]
    private static partial Regex ForcePushRegex();

    [GeneratedRegex("extraheader")]
    private static partial Regex ExtraHeaderRegex();

    [GeneratedRegex(@"\bgit\b[^\n]*\s(?:add|stage)\b")]
    private static partial Regex GitAddRegex();

    [GeneratedRegex(@"\bgit\b.*\scommit\b")]
    private static partial Regex GitCommitRegex();

    [GeneratedRegex(@"\s(?:-a\w*|--all|-i|--include|-o|--only)\b")]
    private static partial Regex BlanketCommitRegex();
}
