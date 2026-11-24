#!/usr/bin/env python3
"""Behavioural harness for .github/workflows/dependabot-lockfiles.yml. LINUX ONLY.

Runs the workflow's own shell steps (read out of the workflow file at run time, never
copied) against valid and hostile lock-file artefacts, offline, with a local bare
repository standing in for GitHub. It exits 0 only if every case behaves as expected.

    python3 -I scripts/dependabot-lockfiles-harness.py [path/to/dependabot-lockfiles.yml]

Needs Linux (the steps use GNU cp --parents and bash 4+ mapfile), bash, git and Python 3.9+;
no network and no .NET. On another OS, run it in a container, for example:

    docker run --rm --network none -v "$PWD:/repo:ro" python:3.12-bookworm \
        python3 -I /repo/scripts/dependabot-lockfiles-harness.py

CI runs it in the build job (build.yml). DependabotLockfileWorkflowSourceGuardTests pins
the lines this behaviour depends on; run this harness after changing any `run:` block of
that workflow.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_WORKFLOW = os.path.join(REPO, ".github", "workflows", "dependabot-lockfiles.yml")
BRANCH = "dependabot/nuget/x"
TOKEN = "dummy-token"

STEPS = {
    "collect": ("refresh", "Collect changed lock files"),
    "validate": ("commit", "Validate and stage lock files"),
    "publish": ("commit", "Commit and push"),
}


# --- reading the run: blocks out of the workflow ----------------------------------------

def indent_of(line):
    return len(line) - len(line.lstrip(" "))


def run_block(lines, job, step):
    """The literal `run: |` block of one step, as YAML would load it (clip chomping)."""
    start = lines.index("jobs:")
    job_at = next(i for i in range(start, len(lines)) if lines[i] == f"  {job}:")
    job_end = next((i for i in range(job_at + 1, len(lines))
                    if lines[i].strip() and indent_of(lines[i]) <= 2), len(lines))
    step_at = next(i for i in range(job_at, job_end) if lines[i].strip() == f"- name: {step}")
    dash = indent_of(lines[step_at])
    step_end = next((i for i in range(step_at + 1, job_end)
                     if lines[i].strip() and indent_of(lines[i]) <= dash), job_end)
    run_at = next(i for i in range(step_at, step_end) if lines[i].strip() == "run: |")
    body = []
    content = None
    for line in lines[run_at + 1:step_end]:
        if line.strip():
            if content is None:
                content = indent_of(line)
            if indent_of(line) < content:
                break
            body.append(line[content:])
        else:
            body.append("")
    while body and not body[-1]:
        body.pop()
    return "\n".join(body) + "\n"


def extract(workflow):
    with open(workflow, encoding="utf-8") as handle:
        lines = handle.read().split("\n")
    scripts = {key: run_block(lines, job, step) for key, (job, step) in STEPS.items()}
    try:
        import yaml  # optional cross-check of the reader above
    except ImportError:
        return scripts
    with open(workflow, encoding="utf-8") as handle:
        doc = yaml.safe_load(handle)
    for key, (job, step) in STEPS.items():
        loaded = next(s["run"] for s in doc["jobs"][job]["steps"] if s.get("name") == step)
        if loaded != scripts[key]:
            sys.exit(f"harness: the run: block of '{step}' was read differently from PyYAML")
    return scripts


# --- a fake GitHub: a bare repository, a checkout like actions/checkout ---------------

class World:
    def __init__(self, root, scripts):
        self.root = root
        self.scripts = {}
        for key, text in scripts.items():
            path = os.path.join(root, f"{key}.sh")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(text)
            self.scripts[key] = path
        self.real_git = shutil.which("git")
        self.home = os.path.join(root, "home")
        os.makedirs(self.home)
        with open(os.path.join(self.home, ".gitconfig"), "w", encoding="utf-8") as handle:
            handle.write("[init]\n\tdefaultBranch = main\n[advice]\n\tdetachedHead = false\n")
        self.bin = os.path.join(root, "fakebin")
        os.makedirs(self.bin)
        self.push_log = os.path.join(root, "push.log")
        wrapper = os.path.join(self.bin, "git")
        with open(wrapper, "w", encoding="utf-8") as handle:
            handle.write(
                "#!/bin/bash\n"
                "# Records every push's argv and GIT_CONFIG_* environment; can fail chosen subcommands.\n"
                'for a in "$@"; do case "$a" in\n'
                "  push) { printf 'ARGV:'; printf ' [%s]' \"$@\"; echo;"
                ' echo "ENV: COUNT=${GIT_CONFIG_COUNT:-} KEY=${GIT_CONFIG_KEY_0:-} VALUE=${GIT_CONFIG_VALUE_0:-}"; }'
                ' >> "$HARNESS_PUSH_LOG";;\n'
                '  status) [ -z "${HARNESS_FAIL_STATUS:-}" ] || exit 128;;\n'
                '  diff-tree) [ -z "${HARNESS_FAIL_DIFF_TREE:-}" ] || exit 128;;\n'
                "esac; done\n"
                f'exec "{self.real_git}" "$@"\n')
        os.chmod(wrapper, 0o755)
        self.remote = os.path.join(root, "remote.git")
        self.ws = os.path.join(root, "ws")
        self.temp = os.path.join(root, "runner-temp")
        self.incoming = os.path.join(root, "incoming")
        self.summary = os.path.join(root, "summary.md")
        self.hook_log = os.path.join(root, "hook.log")

    def env(self, **extra):
        env = {
            "PATH": f"{self.bin}:{os.environ.get('PATH', '/usr/bin:/bin')}",
            "HOME": self.home,
            "LANG": "C.UTF-8",
            "HARNESS_PUSH_LOG": self.push_log,
            "GITHUB_SERVER_URL": "https://github.example",
            "GITHUB_REPOSITORY": "o/r",
            "GITHUB_RUN_ID": "42",
            "RUNNER_TEMP": self.temp,
        }
        env.update(extra)
        return env

    def git(self, *args, cwd):
        author = {"GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
                  "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}
        result = subprocess.run([self.real_git, *args], cwd=cwd, env=self.env(**author),
                                capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"git {' '.join(args)}: {result.stderr}")
        return result.stdout

    def seed(self, files):
        """The branch as Dependabot left it; `files` maps paths to text."""
        for path in (self.remote, os.path.join(self.root, "seed")):
            shutil.rmtree(path, ignore_errors=True)
        seed = os.path.join(self.root, "seed")
        self.git("init", "-q", "--bare", self.remote, cwd=self.root)
        self.git("init", "-q", seed, cwd=self.root)
        write_tree(seed, files)
        self.git("add", "-A", cwd=seed)
        self.git("commit", "-qm", "seed", cwd=seed)
        self.git("update-ref", f"refs/heads/{BRANCH}", "HEAD", cwd=seed)
        self.git("push", "-q", self.remote, "main", BRANCH, cwd=seed)

    def move_branch(self):
        seed = os.path.join(self.root, "seed")
        with open(os.path.join(seed, "README.md"), "a", encoding="utf-8") as handle:
            handle.write("moved\n")
        self.git("commit", "-qam", "moved", cwd=seed)
        self.git("push", "-q", self.remote, f"HEAD:refs/heads/{BRANCH}", cwd=seed)

    def tip(self, ref=BRANCH):
        return self.git("rev-parse", ref, cwd=self.remote).strip()

    def checkout(self):
        """actions/checkout with ref=<sha>, fetch-depth 1, persist-credentials false."""
        shutil.rmtree(self.ws, ignore_errors=True)
        self.git("init", "-q", self.ws, cwd=self.root)
        self.git("remote", "add", "origin", f"file://{self.remote}", cwd=self.ws)
        sha = self.tip()
        self.git("fetch", "-q", "--depth=1", "origin", f"+{sha}:refs/remotes/pull/1/merge", cwd=self.ws)
        self.git("checkout", "-q", "--force", sha, cwd=self.ws)

    def step(self, key, **env):
        """Runs a step as `shell: bash` does: bash --noprofile --norc -eo pipefail {0}."""
        result = subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", self.scripts[key]],
                                cwd=self.ws, env=self.env(**env), capture_output=True, text=True)
        return result.returncode, result.stdout + result.stderr

    def fresh_temp(self):
        shutil.rmtree(self.temp, ignore_errors=True)
        os.makedirs(self.temp)
        for path in (self.push_log, self.summary, self.hook_log):
            if os.path.exists(path):
                os.remove(path)

    def validate(self, **env):
        return self.step("validate", INCOMING=self.incoming, **env)

    def publish(self, head_ref=BRANCH, **env):
        open(self.summary, "w").close()
        return self.step("publish", PUSH_TOKEN=TOKEN, HEAD_REF=head_ref,
                         GITHUB_STEP_SUMMARY=self.summary, **env)


def read(path):
    if not os.path.exists(path):
        return ""
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def write_tree(root, files):
    for path, text in files.items():
        full = os.path.join(root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as handle:
            handle.write(text)


def lock(packages, version=1, target="net10.0"):
    return json.dumps({"version": version, "dependencies": {target: packages}}, indent=2) + "\n"


def pkg(resolved, digest, kind="Transitive", **more):
    return {"type": kind, "resolved": resolved, "contentHash": digest, **more}


def project(**links):
    return {"type": "Project", "dependencies": links} if links else {"type": "Project"}


# src/A is the project Dependabot bumped (P 1.0.0 -> 2.0.0); tests/B and "tests/C D" reference
# it and are stale, including their project entry's requested range.
BRANCH_FILES = {
    "README.md": "readme\n",
    "src/A/packages.lock.json": lock({"P": pkg("2.0.0", "H2", "Direct", requested="[2.0.0, )")}),
    "tests/B/packages.lock.json": lock({"P": pkg("1.0.0", "H1"), "a": project(P="[1.0.0, )")}),
    "tests/C D/packages.lock.json": lock({"P": pkg("1.0.0", "H1"), "a": project(P="[1.0.0, )")}),
}
CURRENT_B = lock({"P": pkg("2.0.0", "H2"), "a": project(P="[2.0.0, )")})

# The security reviewer's reproduction: at HEAD `Dep` is 1.0.0; the artefact adds "DEP" 2.0.0
# (a real content hash) before the real "Dep" entry. NuGet's locked restore takes the first.
CASE_HEAD = """{
  "version": 1,
  "dependencies": {
    "net11.0": {
      "Mid": {
        "type": "Direct",
        "requested": "[1.0.0, )",
        "resolved": "1.0.0",
        "contentHash": "XqftZzg6gr1PXyEwKmkvb2PKrk0sSzCjkWj3ZK5d1I+mjquByyoSZOlKPocfGv4RIx47n7EDoF4rIGAdc8Locw==",
        "dependencies": {
          "Dep": "1.0.0"
        }
      },
      "Dep": {
        "type": "Transitive",
        "resolved": "1.0.0",
        "contentHash": "1QbzdblpHK0i8563NHvPpbuvkw5GwG8PX7NOo4ltyYg8IBPznfcLtqKf70P/8Yz5ltCu9oIVLeSBCLCplDDJFQ=="
      }
    }
  }
}"""
CASE_HOSTILE = CASE_HEAD.replace('      "Dep": {', """      "DEP": {
        "type": "Transitive",
        "resolved": "2.0.0",
        "contentHash": "gFTZ0tKOg6Qqsae4dB1MMnmujqND0qoMJh9uIC4oMWrZW+5Pxtl023bCS9ADCcKKUDsU5to7mQuntt/8Lc9S0w=="
      },
      "Dep": {""")


# --- cases ------------------------------------------------------------------------------

class Harness:
    def __init__(self, world):
        self.w = world
        self.failures = []

    def check(self, name, condition, detail=""):
        print(f"{'PASS' if condition else 'FAIL'}  {name}")
        if not condition:
            self.failures.append(name)
            # The "| " keeps a quoted line from starting with "::" once the runner has
            # trimmed its leading spaces, so a "::" workflow command in a step's log is
            # shown, not acted on. The runner also reads the older "##[" form anywhere in
            # a line; the lock-file workflow prints none.
            for line in str(detail).splitlines()[-25:]:
                print(f"        | {line}")

    # refresh: Collect changed lock files ------------------------------------------------
    def collect(self, name, mutate, expect_rc, expect_changed, expect_files):
        w = self.w
        w.seed(BRANCH_FILES)
        w.checkout()
        w.fresh_temp()
        mutate(w.ws)
        output = os.path.join(w.root, "github-output")
        open(output, "w").close()
        rc, log = w.step("collect", GITHUB_OUTPUT=output)
        with open(output, encoding="utf-8") as handle:
            produced = handle.read()
        out = os.path.join(w.temp, "lockfiles-out")
        files = sorted(os.path.relpath(os.path.join(d, f), out)
                       for d, _, names in os.walk(out) for f in names) if os.path.isdir(out) else []
        ok = (rc == 0) == (expect_rc == 0)
        if expect_rc == 0:
            ok = ok and f"changed={expect_changed}" in produced and files == expect_files
        self.check(f"collect: {name}", ok, f"rc={rc} output={produced!r} files={files}\n{log}")

    # commit: Validate and stage, then Commit and push -------------------------------------
    def artefact(self, files, branch_files=None, before=None):
        w = self.w
        w.seed(branch_files or BRANCH_FILES)
        w.checkout()
        w.fresh_temp()
        shutil.rmtree(w.incoming, ignore_errors=True)
        os.makedirs(w.incoming)
        if callable(files):
            files(w.incoming)
        else:
            write_tree(w.incoming, files)
        if before:
            before(w.ws)

    def refused(self, name, files, message, branch_files=None, **env):
        self.artefact(files, branch_files)
        tip = self.w.tip()
        rc, log = self.w.validate(**env)
        injected = any(line.startswith("::warning::INJECTED") for line in log.splitlines())
        self.check(f"refuse: {name}",
                   rc != 0 and message in log and not injected and self.w.tip() == tip,
                   f"rc={rc}\n{log}")

    def accepted_and_pushed(self, name, files, branch_files=None, before=None, summary_has=()):
        w = self.w
        self.artefact(files, branch_files, before)
        tip = w.tip()
        rc, log = w.validate()
        if rc != 0:
            self.check(f"accept: {name}", False, f"validate rc={rc}\n{log}")
            return
        rc, plog = w.publish()
        log += plog
        new_tip = w.tip()
        message = w.git("log", "-1", "--format=%an <%ae>%n%B", BRANCH, cwd=w.remote)
        parent = w.git("rev-parse", f"{BRANCH}~1", cwd=w.remote).strip() if new_tip != tip else ""
        pushes, summary = read(w.push_log), read(w.summary)
        leaked = w.git("config", "--list", "--local", cwd=w.ws)
        argv = "".join(line for line in pushes.splitlines(True) if line.startswith("ARGV:"))
        problems = []
        if rc != 0:
            problems.append(f"publish rc={rc}")
        if new_tip == tip or parent != tip:
            problems.append("the branch did not gain exactly one commit on top of the old tip")
        if not message.startswith("github-actions[bot] <41898282+github-actions[bot]@users.noreply.github.com>"):
            problems.append("wrong author")
        if "[dependabot skip]" not in message or "Workflow-run: https://github.example/o/r/actions/runs/42" not in message:
            problems.append("commit message lacks the marker or the run trailer")
        if pushes.count("ARGV:") != 1 or f"[HEAD:refs/heads/{BRANCH}]" not in argv:
            problems.append("not exactly one push to the pull request branch")
        if "KEY=http.https://github.com/.extraheader VALUE=AUTHORIZATION: basic " not in pushes:
            problems.append("credential not passed through GIT_CONFIG_* environment")
        if TOKEN in argv or "basic" in argv or "eC1hY2Nlc3M" in argv:
            problems.append("credential on the command line")
        if any(word in leaked.lower() for word in ("extraheader", "token", "hookspath")):
            problems.append("credential or hook setting left in .git/config")
        if os.path.exists(w.hook_log):
            problems.append("a planted hook or module ran")
        changed = w.git("diff-tree", "--no-commit-id", "--name-only", "-r", BRANCH, cwd=w.remote).split("\n")
        if any(path and not path.endswith("packages.lock.json") for path in changed):
            problems.append("a file other than a lock file was committed")
        for text in ("Approve workflows to run", "close it and reopen it straight away", *summary_has):
            if text not in summary:
                problems.append(f"summary lacks {text!r}")
        self.check(f"accept: {name}", not problems, "; ".join(problems) + f"\n{log}\n{pushes}\n{summary}")

    def run(self):
        w = self.w

        def nothing(_):
            pass

        def regenerate(ws):
            write_tree(ws, {"tests/B/packages.lock.json": CURRENT_B, "tests/C D/packages.lock.json": CURRENT_B})

        def delete_one(ws):
            os.remove(os.path.join(ws, "tests/B/packages.lock.json"))

        def add_new(ws):
            write_tree(ws, {"tests/E/packages.lock.json": CURRENT_B})

        self.collect("lock files already current", nothing, 0, "false", [])
        self.collect("two stale lock files, one path with a space", regenerate, 0, "true",
                     ["tests/B/packages.lock.json", "tests/C D/packages.lock.json"])
        self.collect("a lock file deleted fails the step", delete_one, 1, None, None)
        self.collect("a new untracked lock file is not collected", add_new, 0, "false", [])

        valid = {"tests/B/packages.lock.json": CURRENT_B, "src/A/packages.lock.json": BRANCH_FILES["src/A/packages.lock.json"]}
        self.accepted_and_pushed("valid artefact (one stale file with its project entry, one current)", valid,
                                 summary_has=("| tests/B/packages.lock.json | P | 1.0.0 | 2.0.0 |",))

        # The same artefact again on the new tip: nothing to commit and nothing pushed.
        w.checkout()
        w.fresh_temp()
        tip = w.tip()
        rc1, log = w.validate()
        rc2, plog = w.publish()
        self.check("accept: the same artefact again commits nothing",
                   rc1 == 0 and rc2 == 0 and w.tip() == tip and not os.path.exists(w.push_log)
                   and "nothing to commit" in log, log + plog)

        def plant(ws):
            hooks = os.path.join(ws, ".git", "hooks")
            for hook in ("pre-commit", "commit-msg", "pre-push"):
                path = os.path.join(hooks, hook)
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(f"#!/bin/sh\necho HOOK-{hook}-RAN >> '{w.hook_log}'\n")
                os.chmod(path, 0o755)
            with open(os.path.join(ws, "json.py"), "w", encoding="utf-8") as handle:
                handle.write(f"open({w.hook_log!r}, 'a').write('PLANTED-JSON-PY-RAN\\n')\n")
            with open(os.path.join(ws, ".git", "info", "exclude"), "a", encoding="utf-8") as handle:
                handle.write("json.py\n__pycache__/\n")

        self.accepted_and_pushed("planted git hooks and json.py in the checkout do not run",
                                 {"tests/B/packages.lock.json": CURRENT_B}, before=plant)

        escaped_branch = dict(BRANCH_FILES)
        escaped_branch["tests/B/packages.lock.json"] = lock(
            {"P": pkg("1.0.0", "H1"), "Q": pkg("1|0`x", "HQ"), "a": project(P="[1.0.0, )")})
        self.accepted_and_pushed("the summary table escapes | and backticks",
                                 {"tests/B/packages.lock.json": CURRENT_B}, branch_files=escaped_branch,
                                 summary_has=("| tests/B/packages.lock.json | Q | 1\\|0\\`x | - |",))

        # The branch moves between checkout and push: a plain push is refused, nothing forced.
        self.artefact({"tests/B/packages.lock.json": CURRENT_B})
        rc1, log = w.validate()
        w.move_branch()
        moved = w.tip()
        rc2, plog = w.publish()
        self.check("refuse: the branch moved after checkout (non-fast-forward)",
                   rc1 == 0 and rc2 != 0 and w.tip() == moved and "@dependabot recreate" in plog, log + plog)

        # The push step refuses any destination outside dependabot/nuget/.
        self.artefact({"tests/B/packages.lock.json": CURRENT_B})
        main = w.tip("main")
        rc1, log = w.validate()
        rc2, plog = w.publish(head_ref="main")
        self.check("refuse: HEAD_REF outside dependabot/nuget/ is never pushed to",
                   rc1 == 0 and rc2 != 0 and w.tip("main") == main and not os.path.exists(w.push_log), log + plog)

        # A failing producer must stop the step, not read as an empty (clean) list.
        self.refused("git status fails (no fail-open)", {"tests/B/packages.lock.json": CURRENT_B}, "",
                     HARNESS_FAIL_STATUS="1")
        self.artefact({"tests/B/packages.lock.json": CURRENT_B})
        tip = w.tip()
        rc1, log = w.validate()
        rc2, plog = w.publish(HARNESS_FAIL_DIFF_TREE="1")
        self.check("refuse: git diff-tree fails (no fail-open)",
                   rc1 == 0 and rc2 != 0 and w.tip() == tip and not os.path.exists(w.push_log), log + plog)

        # Artefact shape.
        stale = lock({"P": pkg("2.0.0", "H2"), "a": project(P="[2.0.0, )")})

        def symlink_file(root):
            os.makedirs(os.path.join(root, "tests/B"))
            os.symlink("/etc/passwd", os.path.join(root, "tests/B/packages.lock.json"))

        def symlink_dir(root):
            elsewhere = os.path.join(w.root, "elsewhere")
            shutil.rmtree(elsewhere, ignore_errors=True)
            write_tree(elsewhere, {"B/packages.lock.json": stale})
            os.symlink(elsewhere, os.path.join(root, "tests"))

        self.refused("empty artefact", {}, "The artefact held no lock files.")
        self.refused("untracked path", {"tests/Z/packages.lock.json": stale}, "not already tracked")
        self.refused("symbolic link", symlink_file, "Refusing non-regular artefact entry")
        self.refused("symbolically linked directory", symlink_dir, "Refusing non-regular artefact entry")
        self.refused(".git path", {".git/packages.lock.json": stale}, "not a packages.lock.json at a plain path")
        self.refused("upper-case file name", {"tests/B/Packages.lock.json": stale}, "not a packages.lock.json at a plain path")
        self.refused("extra file that is not a lock file", {"tests/B/packages.lock.json": stale, "README.md": "x\n"},
                     "not a packages.lock.json at a plain path")
        self.refused("newline in a sibling's name", {"tests/B/packages.lock.json": stale,
                                                     "tests/B/x\n::warning::INJECTED": "x"},
                     "not a packages.lock.json at a plain path")

        # Document shape.
        b = "tests/B/packages.lock.json"
        self.refused("JSON array", {b: "[1]\n"}, "not a NuGet lock document")
        self.refused("not JSON", {b: "<xml/>\n"}, "not JSON")
        self.refused("deep nesting", {b: "[" * 200000 + "\n"}, "not JSON")
        self.refused("exactly repeated package key", {b: CASE_HOSTILE.replace('"DEP"', '"Dep"')},
                     "a key repeated in one object", branch_files={"README.md": "r\n", b: CASE_HEAD})
        self.refused("case-duplicate package id (security reviewer's artefact)", {b: CASE_HOSTILE},
                     "differing only in case", branch_files={"README.md": "r\n", b: CASE_HEAD})
        self.refused("extra top-level key", {b: json.dumps({**json.loads(stale), "x": 1})}, "not a NuGet lock document")
        self.refused("top-level keys reordered",
                     {b: json.dumps({"dependencies": json.loads(stale)["dependencies"], "version": 1})},
                     "not a NuGet lock document")
        self.refused("boolean version", {b: stale.replace('"version": 1', '"version": true')}, "not a NuGet lock document")
        self.refused("different format version", {b: lock(json.loads(stale)["dependencies"]["net10.0"], version=2)},
                     "format version or target frameworks")
        self.refused("different target framework", {b: lock(json.loads(stale)["dependencies"]["net10.0"], target="net9.0")},
                     "format version or target frameworks")
        self.refused("unknown entry type", {b: lock({"P": pkg("2.0.0", "H2", "transitive"), "a": project(P="[2.0.0, )")})},
                     "malformed entry")
        self.refused("package id with markup", {b: lock({"P": pkg("2.0.0", "H2"), "<img>": pkg("2.0.0", "H2"),
                                                        "a": project(P="[2.0.0, )")})}, "unexpected characters")

        # The closure check.
        self.refused("package version not on the branch", {b: lock({"P": pkg("6.6.6", "HX"), "a": project(P="[2.0.0, )")})},
                     "is not already in a lock file on the branch. Regenerate by hand")
        self.refused("known version, different content hash", {b: lock({"P": pkg("2.0.0", "HX"), "a": project(P="[2.0.0, )")})},
                     "is not already in a lock file on the branch")
        self.refused("entry without resolved", {b: lock({"P": {"type": "Transitive"}, "a": project(P="[2.0.0, )")})},
                     "is not already in a lock file on the branch")
        self.refused("invented Project entry with resolved text (code reviewer's artefact)",
                     {b: lock({"P": pkg("2.0.0", "H2"), "a": project(P="[2.0.0, )"),
                               "Evil.Pkg": {"type": "Project", "resolved": "<img src=x> | injected"}})},
                     "is not one the branch's copy has")
        self.refused("existing Project entry given a resolved version",
                     {b: lock({"P": pkg("2.0.0", "H2"), "a": {**project(P="[2.0.0, )"), "resolved": "1.0.0"}})},
                     "is not one the branch's copy has")
        self.refused("Project entry naming a package that is not in its target",
                     {b: lock({"P": pkg("2.0.0", "H2"), "a": project(P="[2.0.0, )", Other="[1.0.0, )")})},
                     "names a package that is not in its target")
        self.refused("package entry turned into a Project entry",
                     {b: lock({"P": {"type": "Project"}, "a": project(P="[2.0.0, )")})},
                     "is not one the branch's copy has")

        # The code reviewer's known-limit case: a version that changes only because of a
        # test-only dependency is on no branch lock file, so a legitimate result is refused.
        limit_branch = {
            "README.md": "r\n",
            "src/A/packages.lock.json": lock({"X": pkg("2.0.0", "HX2", "Direct", requested="[2.0.0, )")}),
            b: lock({"Z": pkg("1.0.0", "HZ", "Direct", requested="[1.0.0, )"), "X": pkg("1.0.0", "HX1"),
                     "Y": pkg("3.0.0", "HY3"), "a": project()}),
        }
        self.refused("known limit: test-only dependency moves to a version on no branch lock file",
                     {b: lock({"Z": pkg("1.0.0", "HZ", "Direct", requested="[1.0.0, )"), "X": pkg("2.0.0", "HX2"),
                               "Y": pkg("2.0.0", "HY2"), "a": project()})},
                     "Regenerate by hand", branch_files=limit_branch)

        print()
        if self.failures:
            print(f"{len(self.failures)} case(s) did not behave as expected:")
            for name in self.failures:
                print(f"  {name}")
            return 1
        print("All cases behaved as expected.")
        return 0


def main():
    if not sys.platform.startswith("linux"):
        sys.exit("harness: Linux only (GNU cp --parents, bash 4+); run it in a Linux container.")
    workflow = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_WORKFLOW)
    scripts = extract(workflow)
    with tempfile.TemporaryDirectory(prefix="dependabot-lockfiles-harness-") as root:
        return Harness(World(root, scripts)).run()


if __name__ == "__main__":
    sys.exit(main())
