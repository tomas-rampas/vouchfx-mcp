# Implementation map: the vouchfx.ai proposal vs. this server

There is a broader proposal circulating for the vouchfx ecosystem — a fuller AI/agent tool surface
plus three model-driven agents (an authoring agent, a healing agent, and an impact-analysis agent)
built on top of the engine. It was written with a different repository in mind: one that builds the
engine itself, hosts a model, and can reach into engine internals directly.

`vouchfx-mcp` is not that repository. It never builds or embeds the engine — it spawns the
**published** `vouchfx` CLI as a subprocess — and it never hosts a model: free-text authoring intent
is host-LLM territory, never something this server interprets itself. Those two constraints shape
every answer below.

This page is the honest accounting: for each capability the wider proposal describes, what exists in
`vouchfx-mcp` today, under what name, and why anything is missing, dropped, or waiting on the engine.
See the [tool & resource reference](tools-and-resources.md) for the full contract of everything that
is actually shipped.

## Implemented here, under this server's own names

The proposal and this server frequently converge on the same capability with different names. Where
that happens, the repo's existing name wins — no capability here carries a borrowed prefix or a
renamed identifier from the wider proposal.

| Proposed capability | Implemented here as |
| --- | --- |
| Listing available step/provider types | [`list_step_types`](tools-and-resources.md#list_step_types) and [`describe_step_type`](tools-and-resources.md#describe_step_type) — kept as two tools (a cheap list, an expensive per-type detail lookup) rather than merged into one, and backed by the pinned engine's live catalogue. |
| Suggesting what to test next | [`plan_coverage`](tools-and-resources.md#plan_coverage) — deterministic, read-only coverage-and-gap analysis over a declared suite set and run history, engine-delegated. |
| Drafting a new suite skeleton | [`scaffold_suite`](tools-and-resources.md#scaffold_suite) — generates a schema-valid `.e2e.yaml` skeleton from structured step types and ids; free text stays with the host LLM. |
| Validating a suite against the schema | [`validate_suite`](tools-and-resources.md#validate_suite) — schema validation today, running as a process-isolated worker against the vendored engine schema. |
| Running a suite and getting a verdict | [`run_suite`](tools-and-resources.md#run_suite) — spawns the pinned CLI and returns the taxonomy-faithful verdict (pass / fail / environment error / inconclusive). |
| Reading back what a run decided | [`explain_run`](tools-and-resources.md#explain_run) — parses a run's JSON Lines event stream; never re-runs anything. |
| Turning a failure into a proposed fix | [`diagnose_run`](tools-and-resources.md#diagnose_run) — Fail-only patch proposals from that same event stream, never auto-applied. |
| Documentation lookup | [`search_docs`](tools-and-resources.md#search_docs) plus the two vendored-document resources (language reference, recipes) and the per-code error-catalogue resources (`vouchfx-docs:///errors/{code}`). |
| A diagnostic-code lookup | [`explain_diagnostic`](tools-and-resources.md#explain_diagnostic) — looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title, explanation, common causes, and fixes, entirely offline. |
| Schema lookup | [`get_schema`](tools-and-resources.md#get_schema) — returns the composed JSON Schema (whole document, major section, or single step type) formatted as JSON Schema or markdown digest; works offline from the embedded schema and optionally cross-verifies against a running pinned CLI. |

A handful of proposed capabilities line up with work this server already has the pieces for but has
not yet wired into a tool. Richer run-lifecycle tools (status/cancel/list of runs, paged raw event
access, a dedicated step-timeline view) are on the near-term roadmap rather than shipped today. None
of these are dropped or blocked — they are simply not built yet.

## Deliberately dropped

Two proposed capabilities are not going to appear here, on purpose:

- **Writing a suite file back to disk.** This server is strictly read-only — it never writes, modifies,
  or deletes a suite file, full stop. Where the proposal expected a tool that saves authored YAML, this
  server instead offers two narrower building blocks: `scaffold_suite` returns schema-valid YAML for a
  brand-new suite skeleton, and `validate_suite` checks an existing suite file against the schema and
  reports validity plus any errors. Neither round-trips or returns canonical YAML for content the host
  already authored — in both cases, any disk write is the host's own file tools, never this server's.
- **A separate "suggest scenarios" tool.** `plan_coverage` and `scaffold_suite` already cover this
  ground, and cover it better: `plan_coverage` is gap-driven (finds what coverage is actually missing)
  rather than skeleton-driven, and both tools are already engine-delegated so their output can never
  drift from what the engine itself would produce.

Neither of these is present under any name in this server. Nothing described as dropped in this
section ships here.

## Blocked on engine-side work

A few proposed capabilities require analysis the published `vouchfx` CLI does not perform yet. This
server never re-implements engine logic in-process — that is the one invariant everything else here is
built around — so these stay unavailable until the engine itself exposes them as a CLI subcommand with
machine-readable output:

- **Compiling a suite without running it.** The engine has no compile-only subcommand today, so there
  is nothing for this server to relay.
- **Mapping how a contract change ripples through a suite set (topology and impact analysis).** Static
  analysis of that kind belongs in the engine, where the source actually lives; building it here would
  mean this server inventing engine-shaped logic on its own, which is exactly the drift this server
  exists to avoid.
- **Richer step/provider catalogue fields** (tiering, resource requirements, verify-mode support,
  worked examples, documentation links) depend on the engine's catalogue export growing those fields;
  this server surfaces whatever the pinned engine reports and nothing more.

When the engine ships the missing subcommand, this server's answer is always the same pattern already
used for coverage planning and scaffolding: advance the pinned engine version, resync the vendored
copies, and add a thin tool that relays the new subcommand's `--json` output — never a locally
recomputed answer.

## Where this leaves things

`vouchfx-mcp` already covers the proposal's authoring, validation, execution, and diagnosis loop
end to end, using its own tool names and its own read-only, model-free constraints. What is missing
either has no engine capability to relay yet, or was a deliberate design choice to keep this server
simple and drift-free. Nothing on this page changes today's tool count or contract — see the
[tool & resource reference](tools-and-resources.md) for what actually ships.
