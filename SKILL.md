---
name: vouchfx-e2e-suites
description: Author, review, run, or heal a vouchfx `.e2e.yaml` end-to-end integration suite using the vouchfx-mcp MCP server — use when writing a new suite, fixing a failing one, reviewing one before it merges, or explaining a run's outcome. Requires the vouchfx-mcp server to be registered.
---

# Working with vouchfx `.e2e.yaml` suites

The `vouchfx-mcp` server is registered. It serves eighteen tools, four prompts and the language's own
documentation. **Use them instead of writing YAML from memory** — this DSL is not the one you know
from another testing tool, and the server can tell you its exact contract offline.

This file is a router, not a procedure. The procedures live in the server's prompts, which are kept
in sync with the tools they name; anything restated here would drift from them.

## Start by invoking the right prompt

Get the prompt with `prompts/get` and follow it. Do not reconstruct its steps from this page.

| You are asked to… | Invoke |
| --- | --- |
| Write a new scenario for a flow | `author_scenario` (args: `flowDescription`, optional `flowId`, `specPath`, `constraints`) |
| Fix a suite whose run failed | `heal_run` |
| Review an existing suite before it merges | `review_spec` |
| Explain one failing step to someone new to vouchfx | `explain_failure` |

If none fits — a one-off question about a step type, a schema lookup, a diagnostic code — call the
tool directly; a prompt is for a multi-step procedure, not for a single lookup.

## Read this before writing any YAML

- `vouchfx://docs/dsl-guide` — the whole language in one read: the file's four blocks, state threading
  with `capture` and `{placeholder}`, `verifyMode: RETRY` with an explicit `timeout`, secrets as
  `${secret:...}` references, and the four outcomes. Every example in it is a complete, schema-valid
  document. **Read this first.**
- `get_schema` — the exact contract, whole or by section.
- `describe_step_type` / `list_step_types` — a step type's real fields. Copy parameter names from
  here, never from memory. If a field is not in this output, it does not exist.
- `vouchfx-docs:///language-reference`, `vouchfx-docs:///recipes`, `vouchfx://examples/{name}` — full
  reference, worked patterns, and three complete suites to start from.
- `explain_diagnostic` — what any `VFX-D-####` / `VFX-E-####` code means and how to fix it.

## Two rules that override anything else you infer

**Never weaken an assertion to make a `Fail` pass.** A run ends in exactly one of `Pass`, `Fail`,
`EnvironmentError`, `Inconclusive`, and they mean different things. `Fail` means the system under test
did not do what the suite asserted — that is a defect to report with expected and actual values.
Loosening the `expect`, widening a `timeout` to hide a wrong result, or deleting the step removes the
finding, not the bug. `EnvironmentError` means the infrastructure never came up and says nothing about
the product; `Inconclusive` means no verdict was reached at all. Report the outcome you got.

**This server never writes, modifies or deletes a suite file — you do.** Every tool here is read-only
over suite files. `scaffold_suite` returns skeleton YAML and `normalize_suite` returns canonical YAML
as *text in a response*; writing it to disk is your job, with your own file-editing tools, exactly as
`author_scenario` instructs. Validate before you write (`validate_suite` at `level: full`, fixing both
the `errors` and `semanticDiagnostics` channels), and show the user what you wrote.

## Running and reading back

`run_suite` runs a suite and returns a verdict plus a `runId`. Pass that id to `get_run_status`,
`get_run_events`, `get_step_timeline`, `get_run_artifacts`, or `cancel_run`; call `list_runs` when you
no longer have the id. Use `explain_run` for the interpreted summary and `diagnose_run` for proposed
fixes — proposals are never applied automatically, and a Fail proposal is a suggestion to review, not
a patch to run.

## What works without the engine installed

Check this before promising the user anything. If a tool you need is unavailable, say so — never
skip validation or invent the answer it would have given.

- **Offline** — `validate_suite`, `normalize_suite`, `get_schema`, `search_docs` and
  `explain_diagnostic` need no engine at all; so do every resource and prompt above. The run-reading
  tools read a recorded event file, not the engine, so they work on an already-finished run too.
- **Needs the pinned `vouchfx` CLI on `PATH`** — `list_step_types`, `describe_step_type`,
  `plan_coverage` and `scaffold_suite`.
- **Needs the CLI and a container runtime** — `run_suite`, and only `run_suite`. A missing Docker
  daemon is an `EnvironmentError`, not a test failure.

Two of the pin-gated four are in the reading list above, so a host without the CLI is not a host
where authoring "just works offline": you can still validate a suite against the schema, but you
cannot look up a step type's real fields or scaffold a skeleton. Lean on the schema and the DSL guide
instead, and tell the user what you could not check.
