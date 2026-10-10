"""Real-process vendoring regressions; fixtures and logs are retained under TEMP.

Run: python -B -m unittest discover -s scripts/tests -p test_sync_vendored.py -v
Requires PowerShell 7, the .NET 8 SDK, and Git supporting --no-lazy-fetch. HTTP is intercepted in
the child PowerShell process: local mode must never call it; remote-mode controls
verify the existing URI/raw-body contract without a network dependency.
"""

import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "sync-vendored.ps1"
ARTIFACTS = {
    "composed-schema.v1.json": "tools/vscode-vouchfx/src/schema/composed-schema.v1.json",
    "language-reference.md": "docs/language-reference.md",
    "recipes.md": "docs/recipes.md",
}
PAYLOADS = {
    "composed-schema.v1.json": b'\xef\xbb\xbf{"title":"Prague"}\r\n',
    "language-reference.md": "# Příliš žluťoučký\nλ without final newline".encode(),
    "recipes.md": b"# CRLF\r\nraw bytes: \x00\x80\xff",
}
SCRATCH = Path(tempfile.mkdtemp(prefix="vouchfx-vendored-tests-"))
GIT = shutil.which("git")
PWSH = shutil.which("pwsh")
DOTNET = shutil.which("dotnet")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def snapshot(root):
    return {str(p.relative_to(root)): digest(p.read_bytes())
            for p in root.rglob("*") if p.is_file()}


class Fixture:
    def __init__(self, name, payloads=PAYLOADS):
        self.root = Path(tempfile.mkdtemp(prefix=name + "-", dir=SCRATCH))
        self.engine = self.root / "engine repo ; & $ (literal)"
        self.engine.mkdir()
        self.mcp = self.root / "mcp"
        (self.mcp / "scripts").mkdir(parents=True)
        self.script = self.mcp / "scripts/sync-vendored.ps1"
        shutil.copyfile(SOURCE, self.script)
        assert digest(self.script.read_bytes()) == digest(SOURCE.read_bytes())
        self.git("init", "-q")
        self.git("config", "user.name", "Vendored fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "commit.gpgsign", "false")
        for local, path in ARTIFACTS.items():
            target = self.engine / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(payloads[local])
        self.sha = self.commit()
        self.pin(self.sha)
        self.vendored = self.mcp / "vendored"
        self.vendored.mkdir()
        for name in ARTIFACTS:
            (self.vendored / name).write_bytes(b"existing " + name.encode())
        self.http_dir = self.root / "http-bodies"
        self.http_dir.mkdir()
        for local, path in ARTIFACTS.items():
            (self.http_dir / Path(path).name).write_bytes(PAYLOADS[local])
        self.http_log = self.root / "http.jsonl"
        self.launcher = self.root / "invoke.ps1"
        self.launcher.write_text("""param([string]$Target, [string]$Mode, [string]$LocalRepo,
    [string]$Location, [string]$DriveRoot)
$ErrorActionPreference = 'Stop'
function global:Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile, [int]$MaximumRedirection)
    [IO.File]::AppendAllText($env:VENDOR_TEST_HTTP_LOG,
        ((@{ uri=$Uri; redirect=$MaximumRedirection } | ConvertTo-Json -Compress) + "`n"))
    if ($env:VENDOR_TEST_HTTP_FAIL -eq '1') { throw 'HTTP forbidden by local fixture' }
    $name = ([Uri]$Uri).Segments[-1]
    if ($env:VENDOR_TEST_HTTP_FAIL -eq $name) { throw 'Simulated transport failure' }
    [IO.File]::WriteAllBytes($OutFile, [IO.File]::ReadAllBytes((Join-Path $env:VENDOR_TEST_HTTP_BODIES $name)))
}
$arguments = @{}
if ($Mode) { $arguments[$Mode] = $true }
if ($LocalRepo) { $arguments['LocalEngineRepo'] = $LocalRepo }
if ($DriveRoot) {
    New-PSDrive -Name VendorFixture -PSProvider FileSystem -Root $DriveRoot | Out-Null
}
if ($Location) {
    Set-Location -LiteralPath $Location
    [IO.File]::WriteAllText($env:VENDOR_TEST_LOCATION,
        (@{ pwd=$PWD.ProviderPath; processCwd=[Environment]::CurrentDirectory } | ConvertTo-Json))
}
& $Target @arguments
exit $LASTEXITCODE
""", encoding="utf-8")
        self.runs = 0

    def git(self, *args, input_bytes=None, check=True, cwd=None):
        env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        result = subprocess.run([GIT, "-C", str(cwd or self.engine), *args],
                                input=input_bytes, capture_output=True, env=env, timeout=30)
        if check and result.returncode:
            raise AssertionError(f"Git {args}: exit {result.returncode}: {result.stderr!r}")
        return result

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "fixture")
        return self.git("rev-parse", "HEAD").stdout.decode().strip()

    def pin(self, sha):
        (self.mcp / "ENGINE_PIN").write_text(f"v1.0.0-fixture {sha}\n", encoding="ascii")

    def invoke(self, mode="Verify", local=True, repository=None, env_extra=None, expected=0,
               location=None, process_cwd=None, drive_root=None):
        self.runs += 1
        stem = self.root / f"run-{self.runs}"
        env = os.environ.copy()
        env.update(VENDOR_TEST_HTTP_LOG=str(self.http_log),
                   VENDOR_TEST_HTTP_BODIES=str(self.http_dir),
                   VENDOR_TEST_HTTP_FAIL="1" if local else "",
                   VENDOR_TEST_LOCATION=str(stem.with_suffix(".location.json")))
        env.update(env_extra or {})
        command = [PWSH, "-NoLogo", "-NoProfile", "-File", str(self.launcher),
                   "-Target", str(self.script), "-Mode", mode]
        if local:
            command += ["-LocalRepo", str(repository or self.engine)]
        if location:
            command += ["-Location", str(location)]
        if drive_root:
            command += ["-DriveRoot", str(drive_root)]
        result = subprocess.run(command, capture_output=True, env=env, cwd=process_cwd, timeout=50)
        stem.with_suffix(".stdout").write_bytes(result.stdout)
        stem.with_suffix(".stderr").write_bytes(result.stderr)
        stem.with_suffix(".exit").write_text(str(result.returncode))
        if result.returncode != expected:
            raise AssertionError(f"{command}: expected {expected}, exit {result.returncode}; "
                                 f"stdout={result.stdout!r}; stderr={result.stderr!r}; logs={self.root}")
        if local and self.http_log.exists():
            raise AssertionError(f"Explicit local mode invoked HTTP: {self.http_log.read_text()}")
        return result

    def assert_payloads(self, case):
        for name, data in PAYLOADS.items():
            case.assertEqual(digest(data), digest((self.vendored / name).read_bytes()), name)


class SyncVendoredTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not GIT or not PWSH or not DOTNET:
            raise AssertionError("Git, PowerShell 7 and .NET 8 SDK are mandatory; no conditional skip")
        supported = subprocess.run([GIT, "--no-lazy-fetch", "--no-replace-objects", "--version"],
                                   capture_output=True, timeout=10)
        if supported.returncode:
            raise AssertionError(f"Unsupported mandatory Git flags: {supported.stderr!r}")
        print(f"Retained real Git fixtures and integer exits: {SCRATCH}", flush=True)
        # A native apphost exercises the real script's pipe/process limits. It is
        # built only in TEMP; no product project or external package is involved.
        native = SCRATCH / "native-git-controls"
        native.mkdir()
        (native / "NuGet.Config").write_text("<configuration><packageSources><clear /></packageSources></configuration>")
        (native / "git.csproj").write_text("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><AssemblyName>git</AssemblyName>
<ImplicitUsings>enable</ImplicitUsings><RestoreIgnoreFailedSources>false</RestoreIgnoreFailedSources>
</PropertyGroup></Project>""")
        (native / "Program.cs").write_text("""var mode = Environment.GetEnvironmentVariable("VENDOR_TEST_NATIVE_MODE");
File.WriteAllText(Environment.GetEnvironmentVariable("VENDOR_TEST_NATIVE_PID")!, Environment.ProcessId.ToString());
if (mode == "hang") { Thread.Sleep(120000); return; }
using var stream = mode == "stderr" ? Console.OpenStandardError() : Console.OpenStandardOutput();
var bytes = new byte[mode == "stderr" ? 16 * 1024 + 1 : 4 * 1024 * 1024 + 1];
Array.Fill(bytes, (byte)'x');
stream.Write(bytes);
stream.Flush();
""")
        build = subprocess.run([DOTNET, "build", str(native / "git.csproj"), "-c", "Release",
                                "-o", str(native / "bin")], capture_output=True, timeout=120)
        (native / "build.stdout").write_bytes(build.stdout)
        (native / "build.stderr").write_bytes(build.stderr)
        (native / "build.exit").write_text(str(build.returncode))
        if build.returncode:
            raise AssertionError(f"Native process control build exited {build.returncode}: {build.stdout!r} {build.stderr!r}")
        cls.native_bin = native / "bin"

    def assert_process_limit(self, mode, diagnostic):
        f = Fixture("native-" + mode)
        before = snapshot(f.vendored)
        pid_file = f.root / "native.pid"
        result = f.invoke("Update", expected=1, env_extra={
            "PATH": str(self.native_bin) + os.pathsep + os.environ["PATH"],
            "VENDOR_TEST_NATIVE_MODE": mode, "VENDOR_TEST_NATIVE_PID": str(pid_file),
        })
        self.assertIn(diagnostic, result.stderr)
        self.assertEqual(before, snapshot(f.vendored))
        self.assertTrue(pid_file.is_file(), "The native fixture must actually start")
        pid = int(pid_file.read_text())
        stopped = subprocess.run([PWSH, "-NoProfile", "-Command",
                                  f"if (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ exit 1 }}; exit 0"],
                                 capture_output=True, timeout=10)
        (f.root / "stopped.exit").write_text(str(stopped.returncode))
        self.assertEqual(0, stopped.returncode, "Bound failure must not leave its native process alive")

    def test_native_stdout_overflow_is_bounded_and_killed(self):
        self.assert_process_limit("stdout", b"4 MiB artefact limit")

    def test_native_stderr_overflow_is_bounded_and_killed(self):
        self.assert_process_limit("stderr", b"16 KiB diagnostic limit")

    def test_native_process_deadline_is_bounded_and_killed(self):
        self.assert_process_limit("hang", b"30-second process limit")

    def test_update_exact_binary_bytes_and_verify_without_writes(self):
        f = Fixture("bytes")
        before = snapshot(f.engine)
        f.invoke("Update")
        f.assert_payloads(self)
        self.assertEqual(before, snapshot(f.engine))
        vendor = snapshot(f.vendored)
        f.invoke("Verify")
        f.invoke("")  # default parameter set remains Verify with explicit local input
        self.assertEqual(vendor, snapshot(f.vendored))

    def test_dirty_worktree_and_newer_head_do_not_replace_pinned_bytes(self):
        f = Fixture("immutable")
        for local, path in ARTIFACTS.items():
            (f.engine / path).write_bytes(b"newer commit " + local.encode())
        newer = f.commit()
        self.assertNotEqual(newer, f.sha)
        for path in ARTIFACTS.values():
            (f.engine / path).write_bytes(b"uncommitted worktree")
        before = snapshot(f.engine)
        f.invoke("Update")
        f.assert_payloads(self)
        f.invoke("Verify")
        self.assertEqual(before, snapshot(f.engine))

    def test_relative_repository_uses_powershell_location_not_process_cwd(self):
        f = Fixture("relative-location")
        before = snapshot(f.engine)
        relative = "../" + f.engine.name  # Spaces and shell punctuation remain literal.
        f.invoke("Update", repository=relative, location=f.mcp, process_cwd=f.root)
        location = json.loads((f.root / "run-1.location.json").read_text())
        self.assertEqual(f.mcp.resolve(), Path(location["pwd"]).resolve())
        self.assertEqual(f.root.resolve(), Path(location["processCwd"]).resolve())
        self.assertNotEqual(location["pwd"], location["processCwd"])
        f.assert_payloads(self)
        vendor = snapshot(f.vendored)
        f.invoke("Verify", repository=relative, location=f.mcp, process_cwd=f.root)
        self.assertEqual(vendor, snapshot(f.vendored))
        self.assertEqual(before, snapshot(f.engine))

    def test_filesystem_psdrive_resolves_relative_and_drive_qualified_input(self):
        f = Fixture("filesystem-drive")
        before = snapshot(f.engine)
        f.invoke("Update", repository="../" + f.engine.name,
                 location="VendorFixture:/mcp", process_cwd=f.root, drive_root=f.root)
        f.assert_payloads(self)
        vendor = snapshot(f.vendored)
        f.invoke("Verify", repository="VendorFixture:/" + f.engine.name,
                 location=f.mcp, process_cwd=f.root, drive_root=f.root)
        self.assertEqual(vendor, snapshot(f.vendored))
        self.assertEqual(before, snapshot(f.engine))

    def test_nonfilesystem_provider_input_is_refused_with_safe_diagnostics(self):
        f = Fixture("nonfilesystem")
        before = snapshot(f.vendored)
        before_engine = snapshot(f.engine)
        result = f.invoke("Update", repository="Env:PATH", expected=1)
        self.assertIn(b"LocalEngineRepo must resolve to the FileSystem provider", result.stderr)
        result = f.invoke("Update", repository="Env:missing\x1b[31m", expected=1)
        self.assertNotIn(b"\x1b", result.stderr)
        self.assertEqual(before, snapshot(f.vendored))
        self.assertEqual(before_engine, snapshot(f.engine))

    def test_verify_drift_and_absent_destination_are_read_only(self):
        f = Fixture("drift")
        before = snapshot(f.vendored)
        f.invoke(expected=1)
        self.assertEqual(before, snapshot(f.vendored))
        shutil.move(f.vendored, f.root / "saved-vendored")
        f.invoke(expected=1)
        self.assertFalse(f.vendored.exists())

    def test_invalid_or_missing_pin_fails_before_http_or_git(self):
        for label, pin in [
            ("missing", None), ("blank", b"# comment\n"), ("fields", b"v1 one two\n"),
            ("zero", b"v1 " + b"0" * 40), ("short", b"v1 abc"),
            ("nonhex", b"v1 " + b"G" * 40), ("control", b"v1\x1b[31m " + b"a" * 40),
        ]:
            with self.subTest(label=label):
                f = Fixture("pin-" + label)
                path = f.mcp / "ENGINE_PIN"
                if pin is None:
                    path.unlink()
                else:
                    path.write_bytes(pin)
                before = snapshot(f.vendored)
                result = f.invoke("Update", expected=1)
                self.assertNotIn(b"\x1b", result.stderr)
                f.invoke("Update", local=False, expected=1)
                self.assertFalse(f.http_log.exists())
                self.assertEqual(before, snapshot(f.vendored))

    def test_uppercase_hex_preserves_remote_parser_but_is_not_local_object_input(self):
        f = Fixture("uppercase")
        (f.mcp / "ENGINE_PIN").write_text("v1 " + "A" * 40)
        before = snapshot(f.vendored)
        f.invoke("Update", expected=1)
        self.assertEqual(before, snapshot(f.vendored))
        f.invoke("Update", local=False)  # Existing remote regex is case-insensitive.
        f.assert_payloads(self)

    def test_missing_repository_and_nonrepository_fail_without_vendor_changes(self):
        f = Fixture("repository")
        before = snapshot(f.vendored)
        for repository in [f.root, f.root / "absent\x1b[31m"]:
            with self.subTest(repository=repository):
                result = f.invoke("Update", repository=repository, expected=1)
                self.assertNotIn(b"\x1b", result.stderr)
                self.assertEqual(before, snapshot(f.vendored))

    def test_missing_commit_and_noncommit_objects_are_refused(self):
        f = Fixture("objects")
        blob = f.git("hash-object", "-w", "--stdin", input_bytes=b"not a commit").stdout.decode().strip()
        tree = f.git("rev-parse", "HEAD^{tree}").stdout.decode().strip()
        f.git("tag", "-a", "fixture-tag", "-m", "tag")
        tag = f.git("rev-parse", "fixture-tag").stdout.decode().strip()
        before = snapshot(f.vendored)
        for sha in ["b" * 40, blob, tree, tag]:
            with self.subTest(sha=sha):
                f.pin(sha)
                f.invoke("Update", expected=1)
                self.assertEqual(before, snapshot(f.vendored))

    def test_missing_last_artifact_does_not_partially_update(self):
        f = Fixture("missing-last")
        (f.engine / ARTIFACTS["recipes.md"]).unlink()
        f.pin(f.commit())
        before = snapshot(f.vendored)
        f.invoke("Update", expected=1)
        f.invoke("Verify", expected=1)
        self.assertEqual(before, snapshot(f.vendored))

    def test_symlink_and_gitlink_are_not_regular_blob_inputs(self):
        for mode in ["120000", "160000"]:
            with self.subTest(mode=mode):
                f = Fixture("mode-" + mode)
                oid = (f.sha if mode == "160000" else
                       f.git("hash-object", "-w", "--stdin", input_bytes=b"../language-reference.md").stdout.decode().strip())
                f.git("update-index", "--cacheinfo", f"{mode},{oid},{ARTIFACTS['recipes.md']}")
                f.git("commit", "-q", "-m", "nonregular fixture")
                f.pin(f.git("rev-parse", "HEAD").stdout.decode().strip())
                before = snapshot(f.vendored)
                f.invoke("Update", expected=1)
                self.assertEqual(before, snapshot(f.vendored))

    def test_tree_entry_is_not_a_regular_blob_input(self):
        f = Fixture("tree-entry")
        path = f.engine / ARTIFACTS["recipes.md"]
        path.unlink()
        path.mkdir()
        (path / "child").write_bytes(b"tree")
        f.pin(f.commit())
        before = snapshot(f.vendored)
        f.invoke("Update", expected=1)
        self.assertEqual(before, snapshot(f.vendored))

    def test_four_mib_boundary_is_exact_and_oversize_fails_before_copy(self):
        for size in [4 * 1024 * 1024, 4 * 1024 * 1024 + 1]:
            with self.subTest(size=size):
                f = Fixture("size")
                payload = b"x" * size
                (f.engine / ARTIFACTS["recipes.md"]).write_bytes(payload)
                f.pin(f.commit())
                before = snapshot(f.vendored)
                f.invoke("Update", expected=0 if size == 4 * 1024 * 1024 else 1)
                if size == 4 * 1024 * 1024:
                    self.assertEqual(digest(payload), digest((f.vendored / "recipes.md").read_bytes()))
                else:
                    self.assertEqual(before, snapshot(f.vendored))

    def test_replacement_ref_control_cannot_redirect_pin(self):
        f = Fixture("replace")
        for path in ARTIFACTS.values():
            (f.engine / path).write_bytes(b"replacement poison")
        replacement = f.commit()
        f.git("replace", f.sha, replacement)
        self.assertEqual(b"replacement poison", f.git("show", f"{f.sha}:{ARTIFACTS['recipes.md']}").stdout)
        before = snapshot(f.engine)
        f.invoke("Update")
        f.assert_payloads(self)
        self.assertEqual(before, snapshot(f.engine))

    def test_missing_promisor_blob_never_fetches_even_from_available_local_remote(self):
        f = Fixture("promisor")
        remote = f.root / "available-remote.git"
        f.git("clone", "--bare", str(f.engine), str(remote), cwd=f.root)
        f.git("config", "remote.origin.url", str(remote))
        f.git("config", "remote.origin.promisor", "true")
        f.git("config", "extensions.partialClone", "origin")
        blob = f.git("rev-parse", f"{f.sha}:{ARTIFACTS['recipes.md']}").stdout.decode().strip()
        object_path = f.engine / ".git/objects" / blob[:2] / blob[2:]
        self.assertTrue(object_path.is_relative_to(f.root))
        object_path.chmod(stat.S_IWRITE | stat.S_IREAD)
        object_path.unlink()
        # Independent positive control: ordinary Git really repairs this same missing
        # object from the configured local promisor remote in a separate fixture copy.
        control = f.root / "lazy-fetch-control"
        shutil.copytree(f.engine, control)
        recovered = f.git("cat-file", "blob", blob, cwd=control)
        self.assertEqual(PAYLOADS["recipes.md"], recovered.stdout)
        before_engine = snapshot(f.engine)
        before_vendor = snapshot(f.vendored)
        f.invoke("Update", expected=1)
        self.assertEqual(before_engine, snapshot(f.engine))
        self.assertEqual(before_vendor, snapshot(f.vendored))

    def test_inherited_git_environment_cannot_redirect_input(self):
        f = Fixture("environment")
        other = Fixture("wrong-environment", payloads={name: b"incompatible repository " + name.encode()
                                                       for name in ARTIFACTS})
        self.assertNotEqual(f.sha, other.sha)
        # Independent control proves this is a real redirection threat: ordinary
        # Git obeys ambient GIT_DIR despite -C and cannot find the requested pin.
        redirected_env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
        redirected_env["GIT_DIR"] = str(other.engine / ".git")
        redirected = subprocess.run([GIT, "-C", str(f.engine), "cat-file", "-t", f.sha],
                                    env=redirected_env, capture_output=True, timeout=10)
        (f.root / "ambient-git-dir-control.exit").write_text(str(redirected.returncode))
        (f.root / "ambient-git-dir-control.stderr").write_bytes(redirected.stderr)
        self.assertEqual(128, redirected.returncode)
        self.assertEqual(b"commit\n", f.git("cat-file", "-t", f.sha).stdout)
        before = snapshot(f.engine)
        before_other = snapshot(other.engine)
        f.invoke("Update", env_extra={"GIT_DIR": str(other.engine / ".git")})
        f.assert_payloads(self)
        f.invoke("Update", env_extra={
            "GIT_DIR": str(other.engine / ".git"), "GIT_COMMON_DIR": str(other.engine / ".git"),
            "GIT_WORK_TREE": str(other.engine), "GIT_OBJECT_DIRECTORY": str(f.root / "missing-objects"),
            "GIT_ALTERNATE_OBJECT_DIRECTORIES": str(other.engine / ".git/objects"),
            "GIT_NAMESPACE": "unrelated", "GIT_REPLACE_REF_BASE": "refs/unrelated/",
            "GIT_TRACE": str(f.engine / "trace-should-not-exist"),
        })
        f.assert_payloads(self)
        self.assertEqual(before, snapshot(f.engine))
        self.assertEqual(before_other, snapshot(other.engine))

    def test_native_git_failure_is_nonzero_and_preserves_vendored(self):
        f = Fixture("native-failure")
        fake_bin = f.root / "native git failure"
        fake_bin.mkdir()
        native = (Path(os.environ["SystemRoot"]) / "System32/where.exe" if os.name == "nt"
                  else Path(shutil.which("false")))
        shutil.copy2(native, fake_bin / ("git.exe" if os.name == "nt" else "git"))
        before = snapshot(f.vendored)
        result = f.invoke("Update", expected=1,
                          env_extra={"PATH": str(fake_bin) + os.pathsep + os.environ["PATH"]})
        self.assertIn(b"Native Git exited", result.stderr)
        self.assertEqual(before, snapshot(f.vendored))

    def test_remote_default_uri_and_raw_body_contract_is_unchanged(self):
        f = Fixture("remote")
        f.invoke("Update", local=False)
        f.assert_payloads(self)
        before = snapshot(f.vendored)
        f.invoke("", local=False)
        self.assertEqual(before, snapshot(f.vendored))
        calls = [json.loads(line) for line in f.http_log.read_text().splitlines()]
        self.assertEqual(6, len(calls))
        expected = [f"https://raw.githubusercontent.com/tomas-rampas/vouchfx/{f.sha}/{path}"
                    for path in ARTIFACTS.values()]
        self.assertEqual(expected * 2, [call["uri"] for call in calls])
        self.assertTrue(all(call["redirect"] == 0 for call in calls))

    def test_remote_late_failure_preserves_all_existing_vendored_bytes(self):
        f = Fixture("remote-late-failure")
        before = snapshot(f.vendored)
        f.invoke("Update", local=False, expected=1, env_extra={"VENDOR_TEST_HTTP_FAIL": "recipes.md"})
        self.assertEqual(before, snapshot(f.vendored))
        self.assertEqual(3, len(f.http_log.read_text().splitlines()))


if __name__ == "__main__":
    unittest.main(verbosity=2)
