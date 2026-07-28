# Vendored engine artefacts

The three files in this directory are byte-exact copies of artefacts authored in the
[vouchfx engine repository](https://github.com/tomas-rampas/vouchfx), taken at the exact commit
recorded in the repo-root [`ENGINE_PIN`](../ENGINE_PIN) file. They are **never hand-edited**.

## Why these files exist here

`vouchfx-mcp` wraps the published `vouchfx` dotnet tool as a subprocess rather than building the
engine from source (see `ENGINE_PIN`). **Catalogue tools** (`list_step_types`, `describe_step_type`)
use the **live** engine export `vouchfx list --json` (REQ-010) — not these vendored files — so
shape-level field metadata always matches the installed CLI. **`validate_suite`** (JSON Schema
validation) and **`search_docs`** (documentation lookup) use the vendored schema and docs at run
time so the validation worker can stay process-isolated and offline-capable. Vendoring embeds the
files into the `Vouchfx.Mcp` assembly (see the `EmbeddedResource` items in
[`src/Vouchfx.Mcp/Vouchfx.Mcp.csproj`](../src/Vouchfx.Mcp/Vouchfx.Mcp.csproj)).

### Prefer `vouchfx schema` when refreshing the composed schema (Spec A)

If the pinned engine includes Spec A (`vouchfx schema` — available from `v1.0.0-rc.2` onwards),
prefer regenerating `composed-schema.v1.json` from that CLI at the pin (byte-identical to the
engine's composed draft 2020-12 document) rather than only from the VS Code tree path below. If
the pinned engine predates Spec A, `-Update` continues to fetch the engine-repo path listed in
the mapping table. Either way, the vendored schema must match the same pin the live catalogue
tools handshake against. Catalogue tools already consume the live `list --json` export when the
pinned CLI is installed (REQ-010).

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

Because `language-reference.md` and `recipes.md` are byte-exact copies of files authored in the
engine repository, they may contain relative links (to other engine docs, source paths, and the
like) that do not resolve from within this repository. Follow those links on the
[engine repo](https://github.com/tomas-rampas/vouchfx) or [vouchfx.io](https://vouchfx.io) at the
pinned ref instead.

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
