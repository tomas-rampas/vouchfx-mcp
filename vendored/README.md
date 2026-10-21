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

### Do NOT regenerate the composed schema from `vouchfx schema`

An earlier revision of this file recommended regenerating `composed-schema.v1.json` from the Spec A
CLI (`vouchfx schema`, available from `v1.0.0-rc.3` onwards) on the grounds that its output is
"byte-identical" to the engine's composed document. **That is not true, and following it fails the
drift gate.** Measured at the `v1.0.0-rc.4` repin and re-exercised at the `v1.0.0-rc.5` resync, on Windows against the pinned CLI: the JSON
*text* is identical — every character, once newlines are normalised — but the byte stream is not.
`vouchfx schema` emits **CRLF** line endings and a **trailing newline**; the vendored file has LF
and no trailing newline. The gate compares SHA-256 of the raw bytes (that is the whole point of
it), so a schema refreshed that way is reported as DRIFT even though nothing about its content is
wrong.

`pwsh ./scripts/sync-vendored.ps1 -Update` is therefore the only supported way to refresh any file
in this directory. It fetches the exact bytes the gate will later re-fetch and compare against, so
`-Update` followed by `-Verify` is green by construction.

`vouchfx schema` remains useful as an independent **semantic** cross-check — if its output differs
from the vendored schema by more than line endings and the trailing newline, something really is
out of step. Catalogue tools separately consume the live `list --json` export when the pinned CLI
is installed (REQ-010).

## File → engine-path mapping

| Vendored file (this directory)  | Engine repo path                                              |
| -------------------------------- | --------------------------------------------------------------- |
| `composed-schema.v1.json`        | `tools/vscode-vouchfx/src/schema/composed-schema.v1.json`       |
| `composed-schema.v2.json` | `tests/Vouchfx.Engine.Compilation.Tests/Golden/composed-schema.v2.json` |
| `language-reference.md`          | `docs/language-reference.md`                                     |
| `recipes.md`                     | `docs/recipes.md`                                                |

By default, each file's contents are the raw `raw.githubusercontent.com` response body for
`https://raw.githubusercontent.com/tomas-rampas/vouchfx/<ENGINE_PIN commit SHA>/<engine repo path>`,
saved exactly as received — no re-encoding, no newline normalisation, no BOM addition. Byte
fidelity is the entire point: it is what lets the CI drift gate detect a single differing byte
anywhere in these files, not just a whole-file rewrite.

Explicit local mode reads the same bytes from regular Git blobs at that full commit SHA. It does
not read working files, apply checkout filters, follow replacement objects or fetch missing
objects. A dirty worktree or a newer `HEAD` therefore does not change the selected input.

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

For an engine commit already present in a local repository, including an unpublished candidate:

```powershell
pwsh ./scripts/sync-vendored.ps1 -Update -LocalEngineRepo ../vouchfx
pwsh ./scripts/sync-vendored.ps1 -Verify -LocalEngineRepo ../vouchfx
```

Relative paths use the current PowerShell filesystem location, including filesystem PSDrives;
non-filesystem providers are refused. `ENGINE_PIN` remains authoritative; this argument never
changes the pin or selects the local branch tip. Local mode requires Git supporting
`--no-lazy-fetch`; an older Git or an incomplete
repository fails without HTTP fallback. Each mapped artefact is limited to **4 MiB**, each Git
process to **30 seconds**, and native stderr to **16 KiB**. Symlinks, submodules and tree entries
are refused. The full native stdout byte stream is copied without text conversion.

Both inputs are staged completely before Update writes any vendored file. A failed third read
leaves all existing copies intact. The final copy phase is not transactional: review any failed
Update before committing. Verify never writes vendored files. Synchronising local bytes alone
does not establish that an engine candidate or MCP package runs successfully.

Run the real Git/PowerShell regression harness with Python 3:

```sh
python -B -m unittest discover -s scripts/tests -p test_sync_vendored.py -v
```

It retains fixtures and process logs under TEMP. Git, PowerShell 7 and the .NET 8 SDK are mandatory;
the SDK builds a small native process-limit fixture outside the checkout. HTTP controls
use an intercepted transport and explicit local tests fail if that transport is invoked.

## CI enforcement

The `vendored-drift` job in [`.github/workflows/build.yml`](../.github/workflows/build.yml) runs
`scripts/sync-vendored.ps1` in `-Verify` mode on every push and pull request against `main`. If a
vendored file here has drifted from what the pinned engine commit actually serves — including a
change to `ENGINE_PIN` itself that was not followed by re-running `-Update` — that job fails the
build. **Never edit a file in this directory by hand**: run `-Update` instead, so the committed
content and its provenance (the pinned engine commit) always agree.

## Trust model

Default `-Verify` re-fetches from `raw.githubusercontent.com` at the exact commit SHA `ENGINE_PIN`
records and compares against that same source, rather than checking against an independently
recorded hash captured at vendoring time — an accepted tradeoff equivalent to a Git submodule
pinned by commit: trust is anchored in the commit SHA itself (and in GitHub serving that
immutable, content-addressed history faithfully), not in a second, out-of-band record that could
itself go stale or be compromised separately.

Explicit local Verify anchors trust in the same commit's object tree in the selected repository.
It disables Git replacement interpretation and lazy fetching, and rejects missing or nonregular
objects instead of consulting another source.
