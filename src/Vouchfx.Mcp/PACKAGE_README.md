# vouchfx-mcp

**A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents.**

`vouchfx-mcp` wraps the packaged [`vouchfx`](https://www.nuget.org/packages/vouchfx) end-to-end integration-testing
CLI so an agent can work with `.e2e.yaml` suites directly — without shelling out to `vouchfx` and parsing its
output by hand.

## What it gives an agent

Seventeen tools:

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
- **`normalize_suite`** — returns a suite's canonical formatting (schema-derived key order, one consistent
  quoting and block-layout style) alongside the full `validate_suite` result. The server **never writes the
  file** — the canonical text comes back to the host, which decides whether and where to write it. Normalization
  discards comments and is therefore opt-in (`normalize: true`). CLI-free; works fully offline.
- **`get_run_events`** — pages a completed run's raw JSON Lines events exactly as the engine wrote them, for
  hosts building their own timeline instead of consuming `explain_run`'s summary. Takes the `runId`
  `run_suite` returns. Filters by event type and step id **before** paging, so `limit` (default 200, max
  2000) bounds matching events rather than lines scanned, and returns an opaque `nextCursor` while more
  remain. Events carry the engine's wire vocabulary (`PASS`/`FAIL`/`ENV_ERROR`/`INCONCLUSIVE`); unknown
  event types and fields pass through untouched, with non-ASCII text escaped as `\uXXXX` and every bound
  that applied marked in the event. CLI-free, and never takes the run lock.
- **`get_run_status`** — one run's current lifecycle state from the persisted run registry: status
  (`running`/`completed`/`cancelled`), verdict, timestamps, the suites it covered, its events file, and its
  labels. The same record `explain_run` and `get_run_events` resolve a `runId` through, so it can never
  disagree with them. CLI-free, and never takes the run lock.
- **`list_runs`** — pages the run registry newest first, filtered by `label` (`key=value` or a bare `key`) and/or
  `since`, returning `runId`/`status`/`outcome`/`startedAt`/`finishedAt` per run plus the same opaque
  `nextCursor` contract `get_run_events` uses (`limit` default 200, max 2000). CLI-free, and never takes the
  run lock.
- **`cancel_run`** — asks an in-flight run to stop through exactly the mechanism `run_suite` already uses: the
  engine's stdin is closed for a graceful shutdown, and only after the grace period is the process tree
  killed. Returns `cancelled` or `already_finished` (the latter is a normal answer, not an error); a cancelled
  run is reported `Inconclusive`, never `Fail`. Cancellation is same-process only — a run held by another
  server process is refused by name (`VFX-E-1507`) rather than silently reported as cancelled, and a `running`
  entry left behind by a killed server is identified as residue (`VFX-E-1508`).
- **`get_step_timeline`** — returns one step's **complete** RETRY attempt timeline from a finished run: every
  poll the engine recorded, with what each observed. Unlike `explain_run`, whose response-size tiers shrink its
  `attempts` arrays first, this tool never shortens the list — it drops per-attempt evidence text instead and
  says so. Each attempt's `outcome` is its own three-value vocabulary (`matched`/`unmatched`/`error`), never the
  four-way verdict taxonomy. CLI-free, and never takes the run lock.

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

> **Early prerelease.** `vouchfx-mcp` is feature-complete (all seventeen tools, both vendored-document resources, and
> the diagnostic-catalogue resource are real, not stubs) but has not yet had a tagged release or wide validation
> as a *published, globally-installed* tool. Expect rough edges; issues and feedback are welcome on the source
> repository above.
