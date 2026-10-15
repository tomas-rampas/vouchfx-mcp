"""Inert release-asset regressions; no GitHub requests or uploads are performed."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "publish_release_asset", Path(__file__).resolve().parents[1] / "publish-release-asset.py")
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class FakeConnection:
    def __init__(self, fixture):
        self.fixture = fixture

    def request(self, method, endpoint, body, headers):
        self.fixture.uploads.append({"method": method, "endpoint": endpoint,
                                     "body": body.read(), "headers": headers})

    def getresponse(self):
        fixture = self.fixture

        class Response:
            status = fixture.upload_status

            def read(self, limit):
                return json.dumps(fixture.existing(id=789)).encode()[:limit]

        return Response()

    def close(self):
        self.fixture.upload_connection_closed = True


class ReleaseAssetTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="mcp-asset-tests-")
        self.addCleanup(self.scratch.cleanup)
        self.package = Path(self.scratch.name) / "Vouchfx.Mcp.1.0.0.nupkg"
        self.package.write_bytes(b"built package bytes")
        self.repo = "owner/repository"
        self.endpoint = f"repos/{self.repo}/releases/123"
        self.release = {"id": 123, "tag_name": "v1.0.0", "draft": False}
        self.pages = [[]]
        self.remote = self.package.read_bytes()
        self.calls = []
        self.fail_lookup = False
        self.fail_download = False
        self.upload_status = 201
        self.uploads = []
        self.legacy_upload_targets = []
        self.current_tag_release_id = 123
        self.replace_tag = False
        self.upload_connection_closed = False
        self.credentials = patch.dict(os.environ, {"GH_TOKEN": "inert-test-only"})
        self.credentials.start()
        self.addCleanup(self.credentials.stop)
        self.https = patch.object(GATE.http.client, "HTTPSConnection",
                                  side_effect=lambda *args, **kwargs: FakeConnection(self))
        self.connection_factory = self.https.start()
        self.addCleanup(self.https.stop)
        self.transport = patch.object(GATE, "gh", side_effect=self.fake_gh)
        self.transport.start()
        self.addCleanup(self.transport.stop)

    def fake_gh(self, args, output=None):
        self.calls.append(args)
        if args == ["api", "--method", "GET", self.endpoint]:
            if self.fail_lookup:
                raise subprocess.CalledProcessError(1, ["gh", *args])
            return json.dumps(self.release).encode()
        if args[:3] == ["api", "--method", "GET"] and args[3].startswith(self.endpoint + "/assets?"):
            if self.replace_tag:
                self.current_tag_release_id = 124
            page = int(args[3].split("&page=")[1])
            return json.dumps(self.pages[page - 1]).encode()
        if args == ["api", "--method", "GET", f"repos/{self.repo}/releases/assets/456",
                    "--header", "Accept: application/octet-stream"]:
            if self.fail_download:
                raise subprocess.CalledProcessError(1, ["gh", *args])
            output.write(self.remote)
            return None
        if args == ["release", "upload", "v1.0.0", str(self.package), "--repo", self.repo]:
            self.legacy_upload_targets.append(self.current_tag_release_id)
            return b""
        self.fail(f"Unexpected transport call: {args}")

    def existing(self, **overrides):
        return {"id": 456, "name": self.package.name, "state": "uploaded",
                "size": self.package.stat().st_size, **overrides}

    def run_gate(self):
        return GATE.ensure_asset(self.package, self.repo, 123, "v1.0.0")

    def assert_no_upload(self):
        self.assertFalse(self.uploads)
        self.assertFalse(self.legacy_upload_targets)

    def test_absent_asset_uploads_exact_package_once_to_verified_release_id(self):
        self.assertEqual("uploaded", self.run_gate())
        self.assertEqual(1, len(self.uploads))
        self.assertEqual("POST", self.uploads[0]["method"])
        self.assertEqual("/repos/owner/repository/releases/123/assets?name=Vouchfx.Mcp.1.0.0.nupkg",
                         self.uploads[0]["endpoint"])
        self.assertEqual(self.package.read_bytes(), self.uploads[0]["body"])
        self.assertEqual(str(self.package.stat().st_size), self.uploads[0]["headers"]["Content-Length"])
        self.connection_factory.assert_called_once_with("uploads.github.com", timeout=60)
        self.assertTrue(self.upload_connection_closed)
        self.assertFalse(self.legacy_upload_targets)

    def test_matching_asset_reuses_download_without_upload(self):
        self.pages = [[self.existing()]]
        self.assertEqual("reused-identical", self.run_gate())
        self.assert_no_upload()
        self.assertTrue(any("repos/owner/repository/releases/assets/456" in call for call in self.calls))

    def test_same_size_different_bytes_refuse_without_upload(self):
        self.pages = [[self.existing()]]
        self.remote = b"other package bytes"
        self.assertEqual(len(self.package.read_bytes()), len(self.remote))
        with self.assertRaisesRegex(RuntimeError, "bytes differ"):
            self.run_gate()
        self.assert_no_upload()

    def test_different_recorded_size_refuses_without_upload(self):
        self.pages = [[self.existing(size=1)]]
        with self.assertRaisesRegex(RuntimeError, "size differs"):
            self.run_gate()
        self.assert_no_upload()

    def test_duplicate_names_refuse_without_upload(self):
        self.pages = [[self.existing(), self.existing(id=457)]]
        with self.assertRaisesRegex(RuntimeError, "ambiguous"):
            self.run_gate()
        self.assert_no_upload()

    def test_failed_download_refuses_without_upload_or_retry(self):
        self.pages = [[self.existing()]]
        self.fail_download = True
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_gate()
        self.assert_no_upload()
        self.assertEqual(1, sum("repos/owner/repository/releases/assets/456" in call for call in self.calls))

    def test_failed_lookup_is_not_treated_as_absence(self):
        self.fail_lookup = True
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_gate()
        self.assertEqual(1, len(self.calls))
        self.assert_no_upload()

    def test_unknown_asset_lookup_is_not_treated_as_absence(self):
        self.pages = [{"message": "unknown response"}]
        with self.assertRaisesRegex(RuntimeError, "Unknown asset lookup"):
            self.run_gate()
        self.assert_no_upload()

    def test_other_release_or_draft_refuses_before_asset_lookup(self):
        for override in ({"id": 124}, {"tag_name": "v1.0.1"}, {"draft": True}):
            with self.subTest(override=override):
                self.release = {"id": 123, "tag_name": "v1.0.0", "draft": False, **override}
                with self.assertRaisesRegex(RuntimeError, "release identity"):
                    self.run_gate()
        self.assert_no_upload()

    def test_lookup_reads_next_page_before_deciding_asset_is_absent(self):
        self.pages = [[self.existing(id=1000 + i, name=f"unrelated-{i}") for i in range(100)],
                      [self.existing()]]
        self.assertEqual("reused-identical", self.run_gate())
        self.assert_no_upload()
        self.assertTrue(any(self.endpoint + "/assets?per_page=100&page=2" in call for call in self.calls))

    def test_failed_upload_is_not_retried(self):
        self.upload_status = 502
        with self.assertRaisesRegex(RuntimeError, "HTTP 502"):
            self.run_gate()
        self.assertEqual(1, len(self.uploads))
        self.assertTrue(self.upload_connection_closed)

    def test_replacement_tag_cannot_retarget_the_verified_release_id(self):
        self.replace_tag = True
        self.assertEqual("uploaded", self.run_gate())
        self.assertEqual(124, self.current_tag_release_id)
        self.assertEqual(1, len(self.uploads))
        self.assertIn("/releases/123/assets?", self.uploads[0]["endpoint"])
        self.assertFalse(self.legacy_upload_targets)

    def test_deleted_original_release_refuses_without_replacement_fallback(self):
        self.replace_tag = True
        self.upload_status = 404
        with self.assertRaisesRegex(RuntimeError, "HTTP 404"):
            self.run_gate()
        self.assertEqual(1, len(self.uploads))
        self.assertIn("/releases/123/assets?", self.uploads[0]["endpoint"])
        self.assertFalse(self.legacy_upload_targets)

    def test_redirect_is_refused_without_another_authenticated_request(self):
        self.upload_status = 307
        with self.assertRaisesRegex(RuntimeError, "HTTP 307"):
            self.run_gate()
        self.assertEqual(1, len(self.uploads))
        self.assertEqual(1, self.connection_factory.call_count)
        self.assertTrue(self.upload_connection_closed)


if __name__ == "__main__":
    unittest.main()
