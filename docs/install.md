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
  `validate_suite` and `search_docs` work without the CLI.

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
dotnet tool install --global vouchfx --version 1.0.0-rc.3
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

No arguments or environment variables are required. The server speaks MCP over stdio and locates its
own `ENGINE_PIN` file and vendored documentation relative to the installed tool's own location —
wherever `dotnet tool install` placed it — so the registration entry above is complete as written.

## What each tool needs at runtime

| Requirement | `validate_suite`, `search_docs` | `list_step_types`, `describe_step_type` | `plan_coverage` | `scaffold_suite` | `run_suite` | `explain_run` |
| --- | --- | --- | --- | --- | --- | --- |
| `vouchfx` CLI on PATH | Not needed | **Required**, version-checked, Spec A rich `list --json` | **Required**, version-checked, M3 Planner `plan --json` | **Required**, version-checked, Spec B `scaffold` | **Required**, version-checked | Not needed |
| Docker engine running | Not needed | Not needed | Not needed | Not needed | Required for any suite it runs | Not needed |
| Reads a local events file | No | No | Optional (`eventsPath`) | No | Writes one, then reads it back | **Required** — its whole job |

`validate_suite` and `search_docs` work from this server's embedded vendored schema/docs even without
a CLI. Catalogue tools always prefer the live engine export and fail closed when it is unavailable or
too thin.

## Verifying the install

Once registered, ask your agent to call `list_step_types` (no arguments) — with the pinned `vouchfx`
CLI on `PATH` (Spec A rich catalogue), a working install returns Core provider step types grouped by
family with `familyIntent` and `captureSupported`. Without that CLI, catalogue tools return a clear
tool error rather than inventing field metadata. If instead your MCP client reports it could not
start the `vouchfx-mcp` process, confirm `vouchfx-mcp` resolves on `PATH`
(`dotnet tool list --global` should list `Vouchfx.Mcp`) and that the `.NET 8` runtime is installed.
