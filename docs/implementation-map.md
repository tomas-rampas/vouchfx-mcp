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
| Listing available step/provider types | [`list_step_types`](tools-and-resources.md#list_step_types) and [`describe_step_type`](tools-and-resources.md#describe_step_type) — kept as two tools (a cheap list, an expensive per-type detail lookup) rather than merged into one, and backed by the pinned engine's live catalogue. Catalogue fields split into derivable today (family, provider, capture support, description, required/optional fields, `requiredResources`) and pending upstream ask U5 (tier, vouched, supportsVerifyMode, example, docsUrl). |
| Suggesting what to test next | [`plan_coverage`](tools-and-resources.md#plan_coverage) — deterministic, read-only coverage-and-gap analysis over a declared suite set and run history, engine-delegated. |
| Drafting a new suite skeleton | [`scaffold_suite`](tools-and-resources.md#scaffold_suite) — generates a schema-valid `.e2e.yaml` skeleton from structured step types and ids; free text stays with the host LLM. |
| Validating a suite against the schema | [`validate_suite`](tools-and-resources.md#validate_suite) — schema validation and semantic analysis at a chosen level, process-isolated, with structured suite summary and separate semantic-diagnostics channel. |
| Returning a suite's canonical form | [`normalize_suite`](tools-and-resources.md#normalize_suite) — returns the canonical (normalized) text of a suite together with full validation, opt-in because comments are discarded; the server never writes to disk, leaving that to the host's own file tools. |
| Running suites/globs and getting a verdict | [`run_suite`](tools-and-resources.md#run_suite) — spawns the pinned CLI for each suite (sequentially, under one run) and returns the taxonomy-faithful verdict (pass / fail / environment error / inconclusive). |
| Reading back what a run decided | [`explain_run`](tools-and-resources.md#explain_run) — parses a run's JSON Lines event stream; never re-runs anything. |
| Turning a failure into a proposed fix | [`diagnose_run`](tools-and-resources.md#diagnose_run) — Fail-only patch proposals from that same event stream, never auto-applied. |
| Documentation lookup | [`search_docs`](tools-and-resources.md#search_docs) plus the two vendored-document resources (language reference, recipes) and the per-code error-catalogue resources (`vouchfx-docs:///errors/{code}`). |
| A diagnostic-code lookup | [`explain_diagnostic`](tools-and-resources.md#explain_diagnostic) — looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title, explanation, common causes, and fixes, entirely offline. |
| Schema lookup | [`get_schema`](tools-and-resources.md#get_schema) — returns the composed JSON Schema (whole document, major section, or single step type) formatted as JSON Schema or markdown digest; works offline from the embedded schema and optionally cross-verifies against a running pinned CLI. |
| Paged access to a run's raw events | [`get_run_events`](tools-and-resources.md#get_run_events) — returns the engine's own JSON Lines event objects (wire tokens, unknown fields and all — though non-ASCII text is `\uXXXX`-escaped and any bound that applied is marked in the event), filtered by event type and step id before paging, with an opaque cursor. Addressed by the `runId` `run_suite` returns. Complements `explain_run` rather than replacing it: one summarises, this one hands over the raw stream. |
| Polling a run's state | [`get_run_status`](tools-and-resources.md#get_run_status) — one run's record from the persisted run registry (status, verdict, timestamps, spec paths, events file, labels). The registry entry itself, not a second status model, so it can never disagree with the tools that resolve a `runId` through the same entry. Per-step detail stays in the event stream. |
| Listing recent runs | [`list_runs`](tools-and-resources.md#list_runs) — pages the registry newest first, filtered by `label` and/or `since`, reusing `get_run_events`' opaque cursor under its own scope. Returns spec §5.8's five-field projection; positions on a `startedAt` boundary rather than an index, so runs started mid-walk cannot shift a page. |
| Stopping a run in flight | [`cancel_run`](tools-and-resources.md#cancel_run) — fires the cancellation token the run is already executing under, so the stop is `run_suite`'s own graceful sequence (stdin close, grace period, then force-kill) rather than a second path. Cancelled runs are `Inconclusive`, never `Fail`. Same-process only: a run held by another server process is refused by name (`VFX-E-1507`) rather than reported as cancelled, and a `running` entry with a free workspace lock is identified as residue (`VFX-E-1508`). |

A handful of proposed capabilities line up with work this server already has the pieces for but has
not yet wired into a tool. A dedicated step-timeline view is on the near-term roadmap rather than
shipped today; so is asynchronous (`wait: false`) execution, which needs upstream ask U4 before
`cancel_run` and `get_run_status` become a full detached-run workflow rather than a way to manage a
blocking one. None of these are dropped or blocked — they are simply not built yet.

## Deliberately dropped

Two proposed capabilities are not going to appear here, on purpose:

- **Blindly writing a suite file back to disk.** This server is strictly read-only — it never writes, modifies,
  or deletes a suite file, full stop. Where the proposal expected an auto-write tool, this server instead offers
  `normalize_suite`: it returns the canonical (normalized) text of a suite for the host to review in a diff and
  decide whether to write — leaving all disk I/O to the host's own file tools, which already have the user's
  trust and can be audited in version control. `scaffold_suite` returns schema-valid YAML for a brand-new suite
  skeleton, and `validate_suite` checks an existing suite file against the schema and reports validity plus any
  errors. In all three cases, any disk write is the host's own responsibility, never this server's.
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
