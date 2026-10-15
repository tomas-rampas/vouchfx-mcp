"""Upload a missing release asset or reuse only an identical existing package."""
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.parse import quote


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def gh(arguments, output=None):
    return subprocess.run(
        ["gh", *arguments], check=True, stdin=subprocess.DEVNULL,
        stdout=output if output is not None else subprocess.PIPE,
        stderr=None, timeout=60,
    ).stdout


def lookup(endpoint):
    raw = gh(["api", "--method", "GET", endpoint])
    require(len(raw) <= 2 * 1024 * 1024, "Release metadata exceeds the lookup limit.")
    return json.loads(raw)


def fingerprint(path):
    size = 0
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            size += len(block)
            digest.update(block)
    return size, digest.hexdigest()


def upload_asset(package, repository, release_id, expected):
    # GitHub's release-assets API uploads raw bytes to a specific release ID.
    # HTTPSConnection never follows redirects; credentials stay on this fixed host.
    token = os.environ.get("GH_TOKEN", "")
    require(token and all(33 <= ord(char) <= 126 for char in token), "Missing or invalid upload credential.")
    endpoint = f"/repos/{repository}/releases/{release_id}/assets?name={quote(package.name, safe='')}"
    connection = http.client.HTTPSConnection("uploads.github.com", timeout=60)
    try:
        with package.open("rb") as source:
            connection.request("POST", endpoint, body=source, headers={
                "Authorization": f"Bearer {token}",
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2026-03-10",
                "User-Agent": "vouchfx-mcp-release-asset",
                "Content-Type": "application/octet-stream",
                "Content-Length": str(expected[0]),
            })
        response = connection.getresponse()
        require(response.status == 201, f"Upload refused (HTTP {response.status}); no redirect or retry.")
        raw = response.read(2 * 1024 * 1024 + 1)
        require(len(raw) <= 2 * 1024 * 1024, "Upload response exceeds the metadata limit.")
        asset = json.loads(raw)
        require(isinstance(asset, dict) and type(asset.get("id")) is int and asset["id"] > 0
                and asset.get("name") == package.name and asset.get("state") == "uploaded"
                and type(asset.get("size")) is int and asset["size"] == expected[0],
                "Uploaded asset identity or size differs.")
    finally:
        connection.close()


def ensure_asset(package, repository, release_id, tag):
    require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository), "Invalid repository.")
    require(type(release_id) is int and release_id > 0, "Invalid release ID.")
    require(re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", tag), "Invalid release tag.")
    require(package.name == f"Vouchfx.Mcp.{tag[1:]}.nupkg" and package.is_file(), "Expected package is missing or misnamed.")
    local = fingerprint(package)
    endpoint = f"repos/{repository}/releases/{release_id}"
    release = lookup(endpoint)
    require(isinstance(release, dict) and release.get("id") == release_id
            and release.get("tag_name") == tag and release.get("draft") is False,
            "Triggering release identity differs or is not published.")
    assets = []
    seen_ids = set()
    for page in range(1, 11):
        batch = lookup(f"{endpoint}/assets?per_page=100&page={page}")
        require(isinstance(batch, list) and len(batch) <= 100, "Unknown asset lookup response.")
        for asset in batch:
            require(isinstance(asset, dict) and type(asset.get("id")) is int
                    and asset["id"] > 0 and isinstance(asset.get("name"), str)
                    and asset["id"] not in seen_ids, "Invalid or repeated release asset.")
            seen_ids.add(asset["id"])
            if asset["name"].casefold() == package.name.casefold():
                assets.append(asset)
        if len(batch) < 100:
            break
    else:
        raise RuntimeError("Asset pagination exceeded the lookup limit.")
    require(len(assets) <= 1, "The expected package asset is ambiguous.")
    if not assets:
        upload_asset(package, repository, release_id, local)
        return "uploaded"
    asset = assets[0]
    require(asset["name"] == package.name and asset.get("state") == "uploaded"
            and type(asset.get("size")) is int and asset["size"] == local[0],
            "Existing asset name, state or size differs.")
    with tempfile.TemporaryDirectory(prefix="vouchfx-mcp-release-asset-") as scratch:
        downloaded = Path(scratch) / "existing.nupkg"
        with downloaded.open("xb") as output:
            gh(["api", "--method", "GET", f"repos/{repository}/releases/assets/{asset['id']}",
                "--header", "Accept: application/octet-stream"], output)
        require(fingerprint(downloaded) == local, "Existing asset bytes differ; refusing publication.")
    return "reused-identical"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--release-id", type=int, required=True)
    parser.add_argument("--tag", required=True)
    args = parser.parse_args()
    print(ensure_asset(args.package, args.repository, args.release_id, args.tag))


if __name__ == "__main__":
    main()
