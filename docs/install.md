# Install & registration

## Prerequisites

- The **.NET 8 SDK**, to install and run the `vouchfx-mcp` tool itself.
- For `run_suite` only: the [`vouchfx`](https://www.nuget.org/packages/vouchfx) CLI installed and on
  `PATH`, at the exact version this server is pinned to (see [the engine pin](overview.md#the-engine-pin)),
  plus a running Docker engine for any suite it executes. Every other tool works without either.

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

## Install the vouchfx CLI (required by `run_suite`)

```bash
dotnet tool install --global vouchfx --version 1.0.0-alpha.10
```

Unlike `vouchfx-mcp` itself, the `vouchfx` engine CLI **is** published and installable today. Match the
version to this server's `ENGINE_PIN` exactly — `run_suite` performs a handshake against the installed
CLI's own `--version` output before every run and refuses to proceed on a mismatch (see
[Troubleshooting](troubleshooting.md#cli-pin-version-mismatch)).

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

| Requirement | `validate_suite`, `list_step_types`, `describe_step_type`, `search_docs` | `run_suite` | `explain_run` |
| --- | --- | --- | --- |
| `vouchfx` CLI on PATH | Not needed | **Required**, version-checked | Not needed |
| Docker engine running | Not needed | Required for any suite it runs | Not needed |
| Reads a local events file | No | Writes one, then reads it back | **Required** — its whole job |

The four schema/catalogue/documentation tools work entirely from this server's own embedded, vendored
artefacts, and keep working even on a machine with no `vouchfx` CLI or Docker installed at all.

## Verifying the install

Once registered, ask your agent to call `list_step_types` (no arguments) — a working install returns
all 25 Core provider step types grouped by family. If instead your MCP client reports it could not
start the `vouchfx-mcp` process, confirm `vouchfx-mcp` resolves on `PATH`
(`dotnet tool list --global` should list `Vouchfx.Mcp`) and that the `.NET 8` runtime is installed.
