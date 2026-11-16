"""Regressions for the global.json guard in .claude/hooks/session-start.sh.

The guard function is extracted from the real hook file at test time and run alone in a
fresh bash; nothing else in the hook is executed. No pwsh needed. The one test that needs
the dotnet host skips cleanly when `dotnet` is absent.
"""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


HOOK = Path(__file__).resolve().parents[2] / ".claude" / "hooks" / "session-start.sh"
REPO_GLOBAL_JSON = HOOK.parents[2] / "global.json"
HOOK_TEXT = HOOK.read_text(encoding="utf-8")
MATCH = re.search(r"^global_json_safe\(\) \{\n.*?^\}\n", HOOK_TEXT, re.S | re.M)
assert MATCH, "global_json_safe not found in the hook"

# `log` is the only helper the function uses; set -euo pipefail matches the hook.
HARNESS = (
    "set -euo pipefail\n"
    "log() { printf '[session-start] %s\\n' \"$*\" >&2; }\n"
    + MATCH.group(0)
    + 'global_json_safe "$1"\n'
)

SAFE = '{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}'


def run_guard(directory, lc_all="C", timeout=20):
    env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "LC_ALL": lc_all}
    return subprocess.run(["bash", "-c", HARNESS, "guard", str(directory)], env=env,
                          capture_output=True, timeout=timeout)


class GlobalJsonGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(tempfile.mkdtemp(prefix="vfx-gj-")).resolve()
        for ancestor in cls.root.parents:
            if (ancestor / "global.json").exists():
                shutil.rmtree(cls.root, ignore_errors=True)
                raise unittest.SkipTest(f"{ancestor}/global.json exists on this machine")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.root, ignore_errors=True)

    def case(self, content=None):
        directory = Path(tempfile.mkdtemp(dir=self.root))
        if content is not None:
            data = content if isinstance(content, bytes) else content.encode("utf-8")
            (directory / "global.json").write_bytes(data)
        return directory

    def assertAccepted(self, directory, **kw):
        result = run_guard(directory, **kw)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))
        self.assertEqual(result.stdout, b"")

    def assertRefused(self, directory, **kw):
        result = run_guard(directory, **kw)
        self.assertEqual(result.returncode, 1, result.stderr.decode(errors="replace"))
        self.assertEqual(result.stdout, b"", "a refusal must not print to stdout")
        self.assertIn(b"ERROR", result.stderr)
        return result

    # --- accepted ---------------------------------------------------------------------
    def test_repository_global_json_is_accepted(self):
        directory = self.case(REPO_GLOBAL_JSON.read_bytes())
        self.assertAccepted(directory)

    def test_pretty_printed_crlf_is_accepted(self):
        self.assertAccepted(self.case(
            '{\r\n  "sdk": {\r\n    "version": "10.0.100",\r\n    "rollForward": "latestFeature"\r\n  }\r\n}\r\n'))

    def test_allow_prerelease_is_accepted(self):
        self.assertAccepted(self.case(
            '{"sdk":{"version":"10.0.100","allowPrerelease":true,"rollForward":"latestFeature"}}'))

    def test_missing_file_is_accepted(self):
        self.assertAccepted(self.case())

    # --- refused: content -------------------------------------------------------------
    def test_refused_content(self):
        bad = {
            "sdk.paths": '{"sdk":{"version":"10.0.100","paths":["./sdk"]}}',
            "errorMessage": '{"sdk":{"version":"10.0.100","errorMessage":"x"}}',
            "top-level paths": '{"paths":["./sdk"],"sdk":{"version":"10.0.100"}}',
            "msbuild-sdks": '{"sdk":{"version":"10.0.100"},"msbuild-sdks":{"A":"1.0.0"}}',
            "line comment": '{"sdk":{"version":"10.0.100"} // c\n}',
            "block comment": '{"sdk":{"version":"10.0.100"} /* c */}',
            "second root": SAFE + SAFE,
            "trailing content": SAFE + " x",
            "duplicate sdk": '{"sdk":{"version":"10.0.100"},"sdk":{"version":"10.0.101"}}',
            "duplicate key": '{"sdk":{"version":"10.0.100","version":"10.0.101"}}',
            "upper-case SDK": '{"SDK":{"version":"10.0.100"}}',
            "empty": "",
        }
        for name, text in bad.items():
            with self.subTest(name):
                self.assertRefused(self.case(text))

    def test_byte_order_mark_is_refused(self):
        self.assertRefused(self.case(b"\xef\xbb\xbf" + SAFE.encode()))

    # --- refused: file type -----------------------------------------------------------
    def test_symlink_is_refused(self):
        directory = self.case()
        target = self.case(SAFE) / "global.json"
        (directory / "global.json").symlink_to(target)
        self.assertRefused(directory)

    def test_fifo_is_refused_without_blocking(self):
        directory = self.case()
        os.mkfifo(directory / "global.json")
        self.assertRefused(directory, timeout=10)

    def test_directory_is_refused(self):
        directory = self.case()
        (directory / "global.json").mkdir()
        self.assertRefused(directory)

    # --- refused: a parent directory --------------------------------------------------
    def test_bad_global_json_in_a_parent_is_refused(self):
        parent = self.case('{"sdk":{"version":"10.0.100","paths":["./sdk"]}}')
        child = parent / "child"
        child.mkdir()
        self.assertRefused(child)

    # --- size cap, in bytes, in either locale -----------------------------------------
    def test_oversize_multibyte_file_is_refused_in_both_locales(self):
        # 40000 two-byte characters: 80000 bytes (over the 64 KiB cap) but only 40000
        # characters, so a cap that counted characters in a UTF-8 locale would let it
        # through to be read, with `paths` after where a bounded read would have stopped.
        padding = "é" * 40000
        text = '{"sdk":{"version":"10.0.100"},"pad":"' + padding + '","paths":["./sdk"]}'
        for locale in ("C", "C.UTF-8"):
            with self.subTest(locale=locale):
                directory = self.case(text)
                result = self.assertRefused(directory, lc_all=locale)
                self.assertIn(b"larger than 64 KiB", result.stderr)

    def test_file_at_the_cap_is_still_classified_by_content(self):
        # Exactly 65536 bytes of valid pin plus whitespace is within the cap and accepted.
        text = SAFE + " " * (65536 - len(SAFE))
        self.assertAccepted(self.case(text))
        self.assertRefused(self.case(text + " "))


class ScratchDirectoryTests(unittest.TestCase):
    def test_hook_writes_the_stopper_right_after_creating_the_scratch_directory(self):
        self.assertRegex(
            HOOK_TEXT,
            r'SCRATCH="\$\(make_scratch\)" \|\| exit 1\ntrap [^\n]*\nprintf \'\{\}\' >"\$SCRATCH/global\.json" \|\| exit 1\n')

    @unittest.skipUnless(shutil.which("dotnet"), "dotnet is not installed")
    def test_nearer_empty_global_json_stops_the_upward_search(self):
        root = Path(tempfile.mkdtemp(prefix="vfx-gj-host-")).resolve()
        try:
            # Pins an SDK that is not installed, so a host that reads it exits non-zero.
            (root / "global.json").write_text(
                '{"sdk":{"version":"99.0.100","rollForward":"disable"}}', encoding="utf-8")
            scratch = root / "scratch"
            scratch.mkdir()
            env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
            without = subprocess.run(["dotnet", "--version"], cwd=scratch, env=env,
                                     capture_output=True, timeout=120)
            self.assertNotEqual(without.returncode, 0, "the parent global.json was not honoured")
            (scratch / "global.json").write_text("{}", encoding="utf-8")
            with_stopper = subprocess.run(["dotnet", "--version"], cwd=scratch, env=env,
                                          capture_output=True, timeout=120)
            self.assertEqual(with_stopper.returncode, 0, with_stopper.stderr.decode(errors="replace"))
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
