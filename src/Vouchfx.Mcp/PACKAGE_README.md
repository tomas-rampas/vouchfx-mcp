# vouchfx-mcp

**A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents.**

`vouchfx-mcp` wraps the packaged [`vouchfx`](https://www.nuget.org/packages/vouchfx) end-to-end integration-testing
CLI so an agent can work with `.e2e.yaml` suites directly — without shelling out to `vouchfx` and parsing its
output by hand.

## What it gives an agent

Eleven tools:

- **`validate_suite`** — validates a `.e2e.yaml` file against the vouchfx JSON Schema, with structured errors and
  unknown-step-type detection. Runs in an isolated, killable child process, so a hostile or malformed suite can
  never hang the server.
- **`list_step_types`** — enumerates Core provider step types from the live pinned engine
  (`vouchfx list --json`), with family intent and capture support.
- **`describe_step_type`** — returns required/optional fields, capture support, and family intent for a
  given `<family>.<provider>` step type from the same live export.
- **`search_docs`** — free-text search over the vendored language reference and recipe library, with deep links to
  [vouchfx.io](https://vouchfx.io).
- **`plan_coverage`** — runs the engine's deterministic, read-only coverage-and-gap analysis over a declared
  `.e2e.yaml` suite set and an optional event history (Planner). A run that finds gaps is a successful result;
  every gap finding carries a suggested step type/id that feeds `scaffold_suite` unchanged. Invokes pinned
  `vouchfx plan --json`.
- **`scaffold_suite`** — generates a machine-drafted, schema-valid `.e2e.yaml` skeleton from structured step
  types, ids, and an environment outline (Generator). Free text is host-LLM only; invokes pinned
  `vouchfx scaffold --intent`.
- **`run_suite`** — runs a suite through the installed `vouchfx` CLI, verifying the CLI is present and matches the
  pinned engine version before spawning anything, reporting best-effort progress, and returning the
  taxonomy-faithful verdict (pass / fail / environment error / inconclusive) with each step's outcome.
- **`explain_run`** — diagnoses a completed run purely by reading its JSON Lines event stream (never re-running
  anything), naming the failing or inconclusive step(s) with their retry timeline and observation/diff evidence.
- **`diagnose_run`** — Healer: same taxonomy diagnosis as `explain_run`, plus Fail-only review patch proposals
  (never auto-applied; EnvironmentError gets infrastructure guidance only).
- **`explain_diagnostic`** — looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title,
  explanation, common causes, and fixes. CLI-free; works fully offline.
- **`get_schema`** — browses the language's own composed JSON Schema: the whole document or one addressable
  section (`metadata`, `environment`, `variables`, `steps`, or `step:<family>.<provider>`), as a schema
  document or an 8 KB markdown digest. Works fully offline from the vendored schema; when a pinned CLI is
  installed, the embedded copy is cross-checked against that engine's own `vouchfx schema` export.

Plus two MCP resources exposing the vendored vouchfx language reference and recipe library directly, and a
templated `vouchfx-docs:///errors/{code}` resource covering every code `explain_diagnostic` can explain.

## Install

```bash
dotnet tool install --global Vouchfx.Mcp --prerelease
```

Requires the .NET 8 SDK. `run_suite`, `list_step_types`, `describe_step_type`, `plan_coverage`, and
`scaffold_suite` require the [`vouchfx`](https://www.nuget.org/packages/vouchfx) CLI on `PATH` at this
package's `ENGINE_PIN` (`dotnet tool install --global vouchfx --prerelease`). Catalogue tools need Spec A rich
`list --json`; `plan_coverage` needs the M3 Planner's `vouchfx plan`; `scaffold_suite` needs Spec B
`vouchfx scaffold`. `run_suite` additionally needs a running Docker engine for any suite it executes —
`vouchfx-mcp` does not bundle or replace either.

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
- **Documentation**: <https://vouchfx-mcp.vouchfx.io/>
- **Engine documentation**: <https://vouchfx.io/>

> **Early prerelease.** `vouchfx-mcp` is feature-complete (all eleven tools, both vendored-document resources, and
> the diagnostic-catalogue resource are real, not stubs) but has not yet had a tagged release or wide validation
> as a *published, globally-installed* tool. Expect rough edges; issues and feedback are welcome on the source
> repository above.
