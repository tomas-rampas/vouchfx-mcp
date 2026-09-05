# Install & registration

## Prerequisites

- The **.NET 8 SDK**, to install and run the `vouchfx-mcp` tool itself.
- For `run_suite`, `list_step_types`, `describe_step_type`, `plan_coverage`, and `scaffold_suite`: the
  [`vouchfx`](https://www.nuget.org/packages/vouchfx) CLI installed and on `PATH`, at the exact
  version this server is pinned to (see [the engine pin](overview.md#the-engine-pin)). Catalogue tools
  need an engine with Spec A rich `list --json` (see [minimum engine](overview.md#minimum-engine-for-the-live-catalogue)).
  `plan_coverage` needs the M3 Planner (`vouchfx plan` — see [minimum engine for plan_coverage](overview.md#minimum-engine-for-plan_coverage-planner)).
  `scaffold_suite` needs Spec B (`vouchfx scaffold` — see [minimum engine for scaffold](overview.md#minimum-engine-for-scaffold-generator)).
  For `run_suite` only: a running Docker engine for any suite it executes.
  `validate_suite`, `search_docs`, and `explain_diagnostic` work without the CLI.

## Install the vouchfx-mcp tool

```bash
dotnet tool install --global Vouchfx.Mcp --prerelease
```

> **Not yet published.** As of this writing, `Vouchfx.Mcp` has not had a tagged release pushed to
> NuGet.org — the command above is the intended, documented install path once it has, and is exactly
> what the packaging (`PackAsTool`, `ToolCommandName=vouchfx-mcp`) already supports. Until then, build
> and install it from a source checkout instead:
>
> ```bash
> git clone https://github.com/tomas-rampas/vouchfx-mcp.git
> cd vouchfx-mcp
> dotnet pack src/Vouchfx.Mcp -c Release -o ./nupkg
> dotnet tool install --global --add-source ./nupkg Vouchfx.Mcp --prerelease
> ```
>
> Track publication status on the [source repository](https://github.com/tomas-rampas/vouchfx-mcp).

## Install the vouchfx CLI (required by `run_suite`, catalogue tools, `plan_coverage`, and `scaffold_suite`)

```bash
dotnet tool install --global vouchfx --version 1.0.0-rc.4
```

Unlike `vouchfx-mcp` itself, the `vouchfx` engine CLI **is** published and installable today. Match the
version to this server's `ENGINE_PIN` exactly — `run_suite`, `list_step_types`,
`describe_step_type`, `plan_coverage`, and `scaffold_suite` perform a handshake against the installed
CLI's own `--version` output and refuse to proceed on a mismatch (see
[Troubleshooting](troubleshooting.md#cli-pin-version-mismatch)).

For full field metadata on catalogue tools, the installed CLI must implement Spec A
(`vouchfx list --json` with `requiredFields` / `optionalFields` / `captureSupported` /
`familyIntent`). `ENGINE_PIN` implements Spec A; a locally installed CLI that predates it still fails
fast with an upgrade message rather than returning incomplete type keys.

For the Planner path (`plan_coverage`), the installed CLI must also implement the M3 Planner
(`vouchfx plan --json`). `ENGINE_PIN` implements it; a locally installed CLI that predates it fails
closed with an explicit error rather than inventing a coverage-and-gap report in-process.

For the Generator path (`scaffold_suite`), the installed CLI must also implement Spec B
(`vouchfx scaffold --intent`). `ENGINE_PIN` implements it; a locally installed CLI that predates it
fails closed with an explicit error rather than inventing YAML in-process.

## Register with an MCP client

Add an entry to your client's `.mcp.json` (Claude Code, Claude Desktop, and most other MCP-aware agents
use this shape):

```json
{
  "mcpServers": {
    "vouchfx": {
      "command": "vouchfx-mcp"
    }
  }
}
```

The server speaks MCP over stdio and locates its own `ENGINE_PIN` file and vendored documentation
relative to the installed tool's own location — wherever `dotnet tool install` placed it. No arguments
or environment variables are required for basic operation.

### Optional: workspace containment

Optionally pass the `--workspace <path>` flag to configure path containment:

```json
{
  "mcpServers": {
    "vouchfx": {
      "command": "vouchfx-mcp",
      "args": ["--workspace", "/path/to/workspace"]
    }
  }
}
```

When this flag is supplied, the server resolves a workspace with the following directories:

- **Root** (canonicalised and absolute from `<path>`)
- **Specs directory** — `<root>/e2e`, where suites are expected to live
- **Output directory** — `<root>/.vouchfx/runs`, where the run registry and events files are persisted
  (US-S3-01); one JSON document per run stores metadata, and one JSON Lines stream stores the events
- **Config file** — `<root>/vouchfx.config.json`, if present

The root itself must be a local directory. A network/UNC root (`--workspace \\host\share`) is
refused at startup, before any filesystem call is made against it, for the same
forced-authentication reason UNC path *arguments* are refused.

**Startup banner.** When the server starts, it prints a message to stderr stating whether a workspace
is configured and, if so, the root path. This helps confirm the server is operating in the mode you
intended. A typo in the flag name (e.g. `--workspce` or any flag starting `--worksp` but not
`--workspace`) is a startup-fatal error with a "did-you-mean" suggestion; misspelled flags are
caught immediately rather than silently ignored.

**Behaviour change with `--workspace`:** every path parameter passed to `validate_suite`,
`normalize_suite`, `run_suite`, `explain_run`, and `diagnose_run` is canonicalised (symlinks resolved
segment by segment, iterated until nothing more resolves) and must resolve inside the workspace root.
Paths that try to escape the root — via `../` traversal or symlink target resolution — are rejected
with error `VFX-E-1001 PathOutsideWorkspace`.

**Relative paths resolve against the workspace root** when one is configured, which is what makes
`nested/suite.e2e.yaml` mean what a caller expects rather than depending on whichever directory your
MCP client happened to launch the server from. Without `--workspace`, a relative path still resolves
against the server process's current directory, exactly as it always has.

**Nothing is exempt from containment.** Every events path is checked the same way, whether a caller
named it or the server chose it — including `explain_run`/`diagnose_run`'s default (the events file
of the most recent finished run in the run registry). The documented `run_suite` → `explain_run`
round trip works because run artefacts live *inside* the workspace: with `--workspace`, `run_suite`
writes its events file under `<root>/.vouchfx/runs/`, so handing the returned `eventsFilePath`
straight back to `explain_run` passes containment on its merits. Without `--workspace`, artefacts
live in the OS temp directory, the registry is session-scoped (in-memory only), and containment is
off entirely — behaviour is unchanged from before this policy existed.

**Run artefacts accumulate, and retention is the host's job.** In workspace mode every `run_suite`
call leaves a directory under `<root>/.vouchfx/runs/<runId>/` holding two files: `run.json`, that
run's metadata document (id, status, outcome, timestamps, suite paths, labels — metadata only, never
suite or log content), and `events.jsonl`, the engine's own JSON Lines event stream for the run —
step outcomes, timings, and the observation payloads recorded while your suite exercised the system
under test, already redacted by the engine. That second file is run *evidence*, so it is worth
deciding deliberately whether it belongs in version control. The server never deletes either — a
later `explain_run` is expected to read the events file, and deciding when a run stops being
interesting is the host's call, not this server's. (The best-effort 24-hour sweep that exists applies
only to the OS temp files no-workspace mode produces.) Since the server is usually launched inside a
git working tree, adding `.vouchfx/` to that repo's `.gitignore` is recommended.

**Not yet guarded:** `plan_coverage`'s `path` and `eventsPath` arguments bypass the path guard
entirely — neither the network/UNC rejection nor workspace containment applies to them — tracked as
[issue #76](https://github.com/tomas-rampas/vouchfx-mcp/issues/76).

**Without `--workspace`:** omitting the flag entirely is fully supported and leaves every path
behaving exactly as it did before this containment policy. A relative path with `../` traversal
remains allowed on purpose.

For details on both UNC-path rejection (which applies in both modes) and containment behaviour, see
the [VFX-E-1001 error documentation](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1001.html).

## What each tool needs at runtime

| Requirement | `validate_suite`, `normalize_suite`, `search_docs`, `explain_diagnostic` | `get_schema` | `list_step_types`, `describe_step_type` | `plan_coverage` | `scaffold_suite` | `run_suite` | `explain_run`, `diagnose_run` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `vouchfx` CLI on PATH | Not needed | Optional — cross-verifies the embedded schema against `vouchfx schema` when present | **Required**, version-checked, Spec A rich `list --json` | **Required**, version-checked, M3 Planner `plan --json` | **Required**, version-checked, Spec B `scaffold` | **Required**, version-checked | Not needed |
| Docker engine running | Not needed | Not needed | Not needed | Not needed | Not needed | Required for any suite it runs | Not needed |
| Reads a local events file | No | No | No | Optional (`eventsPath`) | No | Writes one, then reads it back | **Required** — its whole job |

`validate_suite`, `normalize_suite`, `search_docs`, and `explain_diagnostic` work entirely from this
server's embedded vendored schema/docs/catalogue even without a CLI. `get_schema` is **CLI-optional**:
it serves the embedded schema offline and, when the pinned CLI is present, cross-verifies it against
`vouchfx schema` and reports any divergence as diagnostic `VFX-D-1106`. Catalogue tools always prefer
the live engine export and fail closed when it is unavailable or too thin.

## Verifying the install

Once registered, ask your agent to call `list_step_types` (no arguments) — with the pinned `vouchfx`
CLI on `PATH` (Spec A rich catalogue), a working install returns Core provider step types grouped by
family with `familyIntent` and `captureSupported`. Without that CLI, catalogue tools return a clear
tool error rather than inventing field metadata. If instead your MCP client reports it could not
start the `vouchfx-mcp` process, confirm `vouchfx-mcp` resolves on `PATH`
(`dotnet tool list --global` should list `Vouchfx.Mcp`) and that the `.NET 8` runtime is installed.
If the process starts but exits immediately, see
[Server exits at startup](troubleshooting.md#server-exits-at-startup) for the three fatal-at-startup
conditions and their exact stderr prefixes — these indicate a broken install, not a `PATH` problem.
