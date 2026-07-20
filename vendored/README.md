# Vendored engine artefacts

The three files in this directory are byte-exact copies of artefacts authored in the
[vouchfx engine repository](https://github.com/tomas-rampas/vouchfx), taken at the exact commit
recorded in the repo-root [`ENGINE_PIN`](../ENGINE_PIN) file. They are **never hand-edited**.

## Why these files exist here

`vouchfx-mcp` wraps the published `vouchfx` dotnet tool as a subprocess rather than building the
engine from source (see `ENGINE_PIN`). Several of this server's tools — `validate_suite` (JSON
Schema validation) and `search_docs` (documentation lookup) in particular — need the engine's own
schema and documentation content at run time, without shelling out to the engine or requiring a
source checkout of it. Vendoring solves that: the files are embedded into the `Vouchfx.Mcp`
assembly itself (see the `EmbeddedResource` items in
[`src/Vouchfx.Mcp/Vouchfx.Mcp.csproj`](../src/Vouchfx.Mcp/Vouchfx.Mcp.csproj)) and read from
there, so the packaged dotnet tool works standalone with no loose-file path assumptions.

## File → engine-path mapping

| Vendored file (this directory)  | Engine repo path                                              |
| -------------------------------- | --------------------------------------------------------------- |
| `composed-schema.v1.json`        | `tools/vscode-vouchfx/src/schema/composed-schema.v1.json`       |
| `language-reference.md`          | `docs/language-reference.md`                                     |
| `recipes.md`                     | `docs/recipes.md`                                                |

Each file's contents are the raw `raw.githubusercontent.com` response body for
`https://raw.githubusercontent.com/tomas-rampas/vouchfx/<ENGINE_PIN commit SHA>/<engine repo path>`,
saved exactly as received — no re-encoding, no newline normalisation, no BOM addition. Byte
fidelity is the entire point: it is what lets the CI drift gate detect a single differing byte
anywhere in these files, not just a whole-file rewrite.

## Keeping these files in sync

[`scripts/sync-vendored.ps1`](../scripts/sync-vendored.ps1) is the single source of truth for both
producing and checking these copies:

- **`pwsh ./scripts/sync-vendored.ps1 -Update`** re-downloads all three files at the commit
  `ENGINE_PIN` currently records and overwrites this directory with them. Run this after bumping
  `ENGINE_PIN`, review the diff, then commit the result.
- **`pwsh ./scripts/sync-vendored.ps1`** (or `-Verify` explicitly — this is the default mode)
  re-downloads all three files to a temporary location and byte-compares each against what is
  committed here, printing a per-file `OK`/`DRIFT` line that names the file and the pinned ref.
  It exits non-zero if anything has drifted, is missing, or fails to download.

## CI enforcement

The `vendored-drift` job in [`.github/workflows/build.yml`](../.github/workflows/build.yml) runs
`scripts/sync-vendored.ps1` in `-Verify` mode on every push and pull request against `main`. If a
vendored file here has drifted from what the pinned engine commit actually serves — including a
change to `ENGINE_PIN` itself that was not followed by re-running `-Update` — that job fails the
build. **Never edit a file in this directory by hand**: run `-Update` instead, so the committed
content and its provenance (the pinned engine commit) always agree.

## Trust model

`-Verify` re-fetches from `raw.githubusercontent.com` at the exact commit SHA `ENGINE_PIN`
records and compares against that same source, rather than checking against an independently
recorded hash captured at vendoring time — an accepted tradeoff equivalent to a Git submodule
pinned by commit: trust is anchored in the commit SHA itself (and in GitHub serving that
immutable, content-addressed history faithfully), not in a second, out-of-band record that could
itself go stale or be compromised separately.
