# vouchfx-mcp

**A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents.**

`vouchfx-mcp` wraps the packaged [`vouchfx`](https://www.nuget.org/packages/vouchfx) end-to-end integration-testing
CLI so an agent can work with `.e2e.yaml` suites directly — without shelling out to `vouchfx` and parsing its
output by hand.

## What it gives an agent

Six tools:

- **`validate_suite`** — validates a `.e2e.yaml` file against the vouchfx JSON Schema, with structured errors and
  unknown-step-type detection. Runs in an isolated, killable child process, so a hostile or malformed suite can
  never hang the server.
- **`list_step_types`** — enumerates all 25 Core provider step types (`http.rest`, `db-assert.postgres`,
  `mq-publish.kafka`, …).
- **`describe_step_type`** — returns the field schema for a given `<family>.<provider>` step type.
- **`search_docs`** — free-text search over the vendored language reference and recipe library, with deep links to
  [vouchfx.io](https://vouchfx.io).
- **`run_suite`** — runs a suite through the installed `vouchfx` CLI, verifying the CLI is present and matches the
  pinned engine version before spawning anything, reporting best-effort progress, and returning the
  taxonomy-faithful verdict (pass / fail / environment error / inconclusive) with each step's outcome.
- **`explain_run`** — diagnoses a completed run purely by reading its JSON Lines event stream (never re-running
  anything), naming the failing or inconclusive step(s) with their retry timeline and observation/diff evidence.

Plus two MCP resources exposing the vendored vouchfx language reference and recipe library directly.

## Install

```bash
dotnet tool install --global Vouchfx.Mcp --prerelease
```

Requires the .NET 8 SDK. `run_suite` additionally requires the [`vouchfx`](https://www.nuget.org/packages/vouchfx)
CLI itself to be installed and on `PATH` (`dotnet tool install --global vouchfx --prerelease`), plus a running
Docker engine for any suite it executes — `vouchfx-mcp` does not bundle or replace either.

## Register with an MCP client

Add an entry to your client's `.mcp.json` (Claude Code, Claude Desktop, and most other MCP-aware agents use this
shape):

```json
{
  "mcpServers": {
    "vouchfx": {
      "command": "vouchfx-mcp"
    }
  }
}
```

No arguments or environment variables are required — the server speaks MCP over stdio and locates its own
`ENGINE_PIN` and vendored documentation relative to the installed tool, wherever `dotnet tool install` places it.

## Engine pin

This server never builds the vouchfx engine from source — it wraps the *published* `vouchfx` CLI and vendors a
byte-exact copy of its JSON Schema and documentation, both pinned to a single engine commit recorded in
`ENGINE_PIN` (shipped inside this package). At startup the server refuses to run if `ENGINE_PIN` cannot be read,
and `run_suite` refuses to invoke the CLI if the installed `vouchfx` version does not match the pin — so a version
mismatch is always reported as a structured result, never a silent behavioural drift.

## Learn more

- **Source**: <https://github.com/tomas-rampas/vouchfx-mcp> (Apache-2.0)
- **The engine this server wraps**: <https://github.com/tomas-rampas/vouchfx>
- **Documentation**: <https://vouchfx.io/>

> **Early prerelease.** `vouchfx-mcp` is feature-complete (all six tools and both vendored-document resources are
> real, not stubs) but has not yet had a tagged release or wide validation as a *published, globally-installed*
> tool. Expect rough edges; issues and feedback are welcome on the source repository above.
