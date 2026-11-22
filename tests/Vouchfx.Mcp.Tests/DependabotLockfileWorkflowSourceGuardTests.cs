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
/// The shell and Python in the workflow are not executed here (the test project has no
/// pattern for running bash, and Windows runners have none). Their behaviour is exercised by
/// <c>scripts/dependabot-lockfiles-harness.py</c>, a Linux-only harness that is not run by CI:
/// it reads the <c>run:</c> blocks out of the workflow file and runs them offline against a
/// local bare repository with valid and hostile artefacts (untracked path, symbolic link,
/// non-JSON, repeated and case-duplicate package keys, invented project entries, a package
/// version not on the branch, a push destination outside <c>dependabot/nuget/</c>, failing git
/// commands, planted git hooks and <c>json.py</c>). Run it after changing any <c>run:</c> block;
/// the string checks below pin the lines that behaviour depends on.
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

    // The validation's Python, pinned as contiguous code lines (indentation and comments are
    // not compared). Any line added inside a block, such as a `continue`, an `or True`, or
    // `known` read from the artefact instead of HEAD, breaks the match.
    private const string DuplicateKeyRefusal = """
        def unique_keys(pairs):
            if len({key for key, _ in pairs}) != len(pairs):
                raise ValueError("a key is repeated in one object")
            return dict(pairs)

        def load(text, where):
            try:
                doc = json.loads(text, object_pairs_hook=unique_keys)
            except (ValueError, RecursionError):
                fail(f"Refusing {where}: not JSON, or a key repeated in one object.")
            if not (isinstance(doc, dict) and list(doc) == ["version", "dependencies"]
                    and type(doc["version"]) is int and isinstance(doc["dependencies"], dict)):
                fail(f"Refusing {where}: not a NuGet lock document.")
            targets = {}
            for target, packages in doc["dependencies"].items():
                if not isinstance(packages, dict):
                    fail(f"Refusing {where}: malformed target {ascii(target)}.")
                entries = targets[target] = {}
                for package, entry in packages.items():
                    if not PACKAGE_ID.fullmatch(package):
                        fail(f"Refusing {where}: package id {ascii(package)} has unexpected characters.")
                    if package.lower() in entries:
                        fail(f"Refusing {where}: {ascii(package)} appears twice in {ascii(target)}, differing only in case.")
                    if not (isinstance(entry, dict) and isinstance(entry.get("type"), str)
                            and entry["type"] in ENTRY_TYPES):
                        fail(f"Refusing {where}: malformed entry {ascii(package)}.")
                    entries[package.lower()] = (package, entry)
            return doc["version"], targets
        """;

    private const string PythonDefinitions = """
        PACKAGE_ID = re.compile(r"[A-Za-z0-9_.-]+")
        ENTRY_TYPES = {"Direct", "Transitive", "Project", "CentralTransitive"}
        """;

    private const string KnownFromHead = """
        known = set()
        for path in tracked:
            if path.rsplit("/", 1)[-1] == "packages.lock.json":
                for entries in load(at_head(path), f"branch file {path}")[1].values():
                    for key, (_, entry) in entries.items():
                        if entry["type"] != "Project":
                            known.add((key, entry.get("resolved"), entry.get("contentHash")))
        """;

    private const string ClosureCheck = """
        for path in sys.argv[2:]:
            old_version, old = load(at_head(path), f"branch file {path}")
            with open(path, encoding="utf-8") as handle:
                new_version, new = load(handle.read(), path)
            if new_version != old_version or new.keys() != old.keys():
                fail(f"Refusing {path}: its format version or target frameworks differ from the branch's copy.")
            for target, entries in new.items():
                names = old[target].keys() | entries.keys()
                for key, (package, entry) in entries.items():
                    if entry["type"] == "Project":
                        was = old[target].get(key)
                        links = entry.get("dependencies", {})
                        if not (was is not None and was[1]["type"] == "Project"
                                and set(entry) <= {"type", "dependencies"} and isinstance(links, dict)
                                and all(PACKAGE_ID.fullmatch(name) and name.lower() in names
                                        and isinstance(value, str) for name, value in links.items())):
                            fail(f"Refusing {path}: project entry {ascii(package)} is not one the branch's copy has,"
                                 " or names a package that is not in its target.")
                        continue
                    resolved, digest = entry.get("resolved"), entry.get("contentHash")
                    if not (isinstance(resolved, str) and isinstance(digest, str)
                            and (key, resolved, digest) in known):
                        fail(f"Refusing {path}: {ascii(package)} {ascii(resolved)} with that content hash"
                             " is not already in a lock file on the branch."
                             " Regenerate by hand: see .github/dependabot.yml (REMEDY).")
                for key in sorted(old[target].keys() | entries.keys()):
                    before = old[target][key][1].get("resolved") if key in old[target] else None
                    after = entries[key][1].get("resolved") if key in entries else None
                    if before != after:
                        name = (entries.get(key) or old[target][key])[0]
                        report.append("| " + " | ".join(cell(text) for text in (path, name, before or "-", after or "-")) + " |")
        """;

    private const string SummaryEscape = """
        def cell(text):
            return re.sub(r"([\\|`<>])", r"\\\1", str(text))
        """;

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
        // toJSON(github) and github['token'] reach the token without spelling `github.token`.
        Assert.DoesNotMatch(WholeContextRegex(), text);
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
        // Tracing would print the base64 credential and the command lines that carry it: no
        // xtrace in any spelling, no -x among any `set` or `bash` flags, and every explicit
        // shell is plain `bash` (GitHub runs it as `bash --noprofile --norc -eo pipefail {0}`),
        // with no `defaults:` that could change the shell of the steps that name none.
        var root = LoadWorkflow(WorkflowFile);
        Assert.DoesNotMatch(TracingRegex(), Text(root));
        Assert.False(root.Children.ContainsKey(new YamlScalarNode("defaults")));
        foreach (var jobName in JobNames)
        {
            Assert.False(Job(root, jobName).Children.ContainsKey(new YamlScalarNode("defaults")));
            foreach (var step in Steps(Job(root, jobName)))
            {
                if (step.Children.TryGetValue(new YamlScalarNode("shell"), out var shell))
                {
                    Assert.Equal("bash", Scalar(shell));
                }
            }
        }
    }

    [Fact]
    public void EveryJob_RunsOnUbuntuLatest_NoStepContinuesOnError_OnlyTheUploadIsConditional()
    {
        // A self-hosted runner would hold the write token on a machine that outlives the job;
        // continue-on-error, or a step-level `if:`, would turn a refusal into a skipped step.
        var root = LoadWorkflow(WorkflowFile);
        Assert.DoesNotContain("continue-on-error", Text(root));
        var conditional = new List<string>();
        foreach (var jobName in JobNames)
        {
            Assert.Equal("ubuntu-latest", Scalar(Job(root, jobName).Children[new YamlScalarNode("runs-on")]));
            foreach (var step in Steps(Job(root, jobName)))
            {
                if (step.Children.TryGetValue(new YamlScalarNode("if"), out var condition))
                {
                    conditional.Add($"{jobName}/{Name(step)}: {Scalar(condition)}");
                }
            }
        }

        Assert.Equal(["refresh/Upload regenerated lock files: steps.collect.outputs.changed == 'true'"], conditional);
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
        // --force*, a `+` refspec and a second refspec. The destination is $HEAD_REF, which
        // the step checks at run time and which no script may change (both below).
        Assert.Equal(
            "git -c core.hooksPath=/dev/null push origin \"HEAD:refs/heads/$HEAD_REF\"",
            GitCommandRegex().Match(push).Value.TrimEnd());
        Assert.DoesNotMatch(ForcePushRegex(), push);

        var pushLines = CodeLines(Scalar(pushStep.Children[new YamlScalarNode("run")]));
        var pushAt = Array.FindIndex(pushLines, line => GitPushRegex().IsMatch(line));
        // A refused push fails the step.
        Assert.Equal(["exit 1", "fi"], pushLines[(pushAt + 2)..(pushAt + 4)]);
        // Runtime guard, before anything is committed: only a dependabot/nuget/ branch.
        var guardAt = AssertContiguous(pushLines,
            "case \"$HEAD_REF\" in",
            "dependabot/nuget/*) ;;",
            "*) echo \"::error::Refusing to push to a branch outside dependabot/nuget/.\"; exit 1;;",
            "esac");
        Assert.True(guardAt < Array.FindIndex(pushLines, line => GitCommitRegex().IsMatch(line)));

        // No script in the file assigns, exports, reads or defaults HEAD_REF or PUSH_TOKEN, and
        // no other step's env carries them: their only source is the push step's env.
        var allLines = JobNames.SelectMany(jobName => RunScripts(Job(root, jobName))).SelectMany(CodeLines).ToArray();
        Assert.DoesNotContain(allLines, line => PushVariableWriteRegex().IsMatch(line));
        foreach (var step in JobNames.SelectMany(jobName => Steps(Job(root, jobName))).Where(step => Name(step) != PushStepName))
        {
            if (step.Children.TryGetValue(new YamlScalarNode("env"), out var env))
            {
                var keys = Assert.IsType<YamlMappingNode>(env).Children.Keys.Select(Scalar).ToArray();
                Assert.DoesNotContain("HEAD_REF", keys);
                Assert.DoesNotContain("PUSH_TOKEN", keys);
            }
        }

        // The token is read once, to build the masked header value; nothing else touches it.
        Assert.Equal(
            "auth=\"$(printf 'x-access-token:%s' \"$PUSH_TOKEN\" | base64 | tr -d '\\n')\"",
            Assert.Single(allLines, line => PushTokenRegex().IsMatch(line)));
        Assert.Single(PushTokenRegex().Matches(string.Join('\n', allLines)));

        // The credential reaches git through GIT_CONFIG_* in its environment, never its argv
        // (readable in /proc/<pid>/cmdline) and never .git/config. The mask is a code line
        // (not a comment) and is set before the push.
        Assert.Contains("GIT_CONFIG_COUNT=1 ", push);
        Assert.Contains("GIT_CONFIG_KEY_0=\"http.https://github.com/.extraheader\" ", push);
        Assert.Contains("GIT_CONFIG_VALUE_0=\"AUTHORIZATION: basic $auth\" ", push);
        Assert.Single(ExtraHeaderRegex().Matches(Text(root)));
        var maskAt = Array.IndexOf(pushLines, "echo \"::add-mask::$auth\"");
        Assert.InRange(maskAt, 0, pushAt - 1);
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
        // pattern: no `..`, no dot-directories), already tracked, printed with %q. Each
        // refusal is pinned with the `exit 1` that makes it one.
        Assert.Contains("shown=\"$(printf '%q' \"$rel\")\"", validateLines);
        AssertContiguous(validateLines,
            "if [ -L \"$file\" ] || [ ! -f \"$file\" ]; then",
            "echo \"::error::Refusing non-regular artefact entry: $shown\"; exit 1",
            "fi");
        AssertContiguous(validateLines,
            @"if ! [[ ""$rel"" =~ ^([A-Za-z0-9_][A-Za-z0-9._-]*/)*packages\.lock\.json$ ]]; then",
            "echo \"::error::Refusing artefact entry that is not a packages.lock.json at a plain path: $shown\"; exit 1",
            "fi");
        AssertContiguous(validateLines,
            "if ! git ls-files --error-unmatch -- \"$rel\" > /dev/null 2>&1; then",
            "echo \"::error::Refusing lock file that is not already tracked: $shown\"; exit 1",
            "fi");
        AssertContiguous(validateLines,
            "if [ \"$count\" -eq 0 ]; then",
            "echo \"::error::The artefact held no lock files.\"; exit 1",
            "fi");
        // The tree differs from HEAD in modified lock files only. The list is captured first,
        // so a failing git status stops the step instead of reading as an empty list.
        AssertContiguous(validateLines,
            "tree_status=\"$(git status --porcelain --untracked-files=all)\"",
            "if [ -n \"$tree_status\" ] && grep -v -E '^ M (.*/)?packages\\.lock\\.json$' <<<\"$tree_status\"; then",
            "echo \"::error::Something other than a modified packages.lock.json is in the tree.\"; exit 1",
            "fi");
        // Each file is a strict lock document (no repeated or case-duplicate keys, exact top
        // level, known entry types) whose packages already appear on the branch and whose
        // project entries the branch already has; the summary is built from checked entries
        // and escaped. Only the changed files are staged, one way.
        AssertContiguous(validateLines, PythonLines(PythonDefinitions));
        AssertContiguous(validateLines, PythonLines(DuplicateKeyRefusal));
        AssertContiguous(validateLines, PythonLines(SummaryEscape));
        AssertContiguous(validateLines, PythonLines(KnownFromHead));
        AssertContiguous(validateLines, PythonLines(ClosureCheck));
        Assert.Contains("python3 -I - \"$RUNNER_TEMP/lockfile-changes.md\" \"${changed[@]}\" <<'PY'", validateLines);
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
        // The finished commit is checked again before pushing; a failing git diff-tree stops
        // the step, and an empty list is refused.
        AssertContiguous(pushLines,
            "committed=\"$(git diff-tree --no-commit-id --name-only -r HEAD)\"",
            "if grep -v -E '(^|/)packages\\.lock\\.json$' <<<\"$committed\"; then",
            "echo \"::error::The commit contains a file other than packages.lock.json.\"; exit 1",
            "fi");
    }

    /// <summary>
    /// Asserts that <paramref name="block"/> appears exactly once in <paramref name="lines"/> as
    /// consecutive lines, and returns the index of its first line.
    /// </summary>
    private static int AssertContiguous(string[] lines, params string[] block)
    {
        var starts = Enumerable.Range(0, lines.Length - block.Length + 1)
            .Where(start => lines.AsSpan(start, block.Length).SequenceEqual(block))
            .ToArray();
        return Assert.Single(starts);
    }

    private static string[] PythonLines(string source) => CodeLines(source.ReplaceLineEndings("\n"));

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

    // xtrace in any spelling (set -o, shopt -o, SHELLOPTS), -x or +x among any flags of `set` or
    // `bash` on the same command, BASH_ENV (a startup file), and git's own tracing.
    [GeneratedRegex(@"(?i:xtrace)|\bset\b[^\n;]*\s[-+][a-zA-Z]*x|\bbash\b[^\n;]*\s-[a-zA-Z]*x|\bSHELLOPTS\b|\bBASH_ENV\b|\bGIT_TRACE|\bGIT_CURL_VERBOSE")]
    private static partial Regex TracingRegex();

    // toJSON(github...) or github[...]: the whole context, or an indexed member, which can
    // reach the token without the literal `github.token`.
    [GeneratedRegex(@"(?i)\btojson\s*\(\s*github\b|\bgithub\s*\[")]
    private static partial Regex WholeContextRegex();

    // An assignment, export, read, default or loop over HEAD_REF or PUSH_TOKEN.
    [GeneratedRegex(@"(?:^|[\s;&|(])(?:HEAD_REF|PUSH_TOKEN)\+?=|\b(?:export|read|readonly|declare|typeset|local|unset|mapfile|readarray|for|printf\s+-v)\b[^\n]*\b(?:HEAD_REF|PUSH_TOKEN)\b|\$\{(?:HEAD_REF|PUSH_TOKEN):?[-=+?]")]
    private static partial Regex PushVariableWriteRegex();

    [GeneratedRegex(@"\bPUSH_TOKEN\b")]
    private static partial Regex PushTokenRegex();

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
