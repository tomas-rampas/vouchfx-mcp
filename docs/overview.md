# What vouchfx-mcp is

`vouchfx-mcp` is a local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for
AI coding agents. It wraps the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) engine —
a compiler and runner for declarative `.e2e.yaml` integration-test suites that prove a distributed .NET
system end-to-end, across a REST call, a Kafka event, a database mutation and an outbound webhook — so
an agent can validate, run and diagnose a suite directly, without shelling out to `vouchfx` and parsing
its console output by hand.

It does **not** build or embed the engine. It spawns the published `vouchfx` command-line tool as a
subprocess for suite runs **and** for the live step-type catalogue (`vouchfx list --json`), and
vendors byte-exact copies of the engine's JSON Schema and documentation for offline suite validation
and doc search — see [Install & registration](install.md) and the [engine pin](#the-engine-pin) below.

## The six tools, at a glance

| Tool | What it does |
| --- | --- |
| [`validate_suite`](tools-and-resources.md#validate_suite) | Validates an `.e2e.yaml` suite against the engine's JSON Schema, without running it. |
| [`list_step_types`](tools-and-resources.md#list_step_types) | Lists every step type the pinned engine supports, grouped by family. |
| [`describe_step_type`](tools-and-resources.md#describe_step_type) | Returns one step type's full required/optional field contract. |
| [`search_docs`](tools-and-resources.md#search_docs) | Free-text search over the vendored language reference and recipe library. |
| [`run_suite`](tools-and-resources.md#run_suite) | Runs a suite through the installed `vouchfx` CLI and reports its taxonomy-faithful verdict. |
| [`explain_run`](tools-and-resources.md#explain_run) | Diagnoses a completed run purely by reading its JSON Lines event stream — never re-running anything. |

The full field-level contract, result shape and notable behaviours for each tool are on the
[tool & resource reference](tools-and-resources.md) page.

## Two documentation resources

Alongside the six tools, the server advertises two MCP resources: the generated
**vouchfx language reference** and the **vouchfx recipes** library, each the byte-exact vendored copy of
the pinned engine commit's own Markdown documentation. An agent can read either directly as a resource,
or reach the same content indirectly through `search_docs`. See
[Resources](tools-and-resources.md#resources) for both.

## Status: early prerelease

This project is being built spec-first: features land against approved specs in a spec → build →
review loop, one requirement at a time. As things stand:

- All **six tools** and **both vendored-document resources** are real, fully functional implementations
  — not stubs. The server is feature-complete for its current scope.
- `validate_suite` and `search_docs` work from the embedded vendored schema and documentation and keep
  working when the `vouchfx` CLI is not installed.
- `list_step_types` and `describe_step_type` load the **live** shape-level catalogue from the pinned
  engine via `vouchfx list --json` (required/optional fields, capture support, family intent). They
  require a CLI that implements Spec A (engine-schema-and-catalogue-export) and fail fast rather than
  returning type keys alone without field metadata.
- `run_suite` and `explain_run` touch the wider environment — the former spawns the `vouchfx` CLI
  (and, through it, Docker), the latter only ever reads a local events file.
- The `Vouchfx.Mcp` package is built as a `dotnet tool` (`PackAsTool`, command `vouchfx-mcp`) but **has
  not yet had a tagged release published to NuGet.org** — what remains is the first tagged release.
  Expect rough edges; see [Install & registration](install.md) for what that means in practice today.

## The engine pin

This server never builds the vouchfx engine from source. It is currently pinned to
**v1.0.0-rc.2** (commit `44e07e4f194a4fcaba3f9a51e154be44d3f53862`) — recorded in this repository's
[`ENGINE_PIN`](https://github.com/tomas-rampas/vouchfx-mcp/blob/main/ENGINE_PIN) file, which explains
exactly what each field pins, how the vendored schema and documentation stay drift-gated against it, and
how the pin is advanced over time. `run_suite`, `list_step_types`, and `describe_step_type` refuse to
use a mismatched or missing CLI — a mismatch is always a structured result, never silent behavioural
drift; see [Troubleshooting](troubleshooting.md#cli-pin-version-mismatch).

### Minimum engine for the live catalogue

Shape-level catalogue tools need **Spec A** on the installed engine: `vouchfx schema` and a rich
`vouchfx list --json` document where every entry carries `requiredFields`, `optionalFields`,
`captureSupported`, and `familyIntent`. Engines that only emit thin type/family/provider keys are
rejected with an explicit error (EDGE-004). Advance `ENGINE_PIN` to a published build that includes
that export when it is available; this server does not invent field metadata from a thin list.

## Secret hygiene

`vouchfx-mcp` never resolves `${secret:...}` references itself, and never reads or echoes its own
process environment into a tool result, progress notification, or resource. The vouchfx engine remains
the sole redaction authority: the `--events` JSON Lines fields `run_suite` and `explain_run` relay are
already redacted at source, and this server passes them through untouched. The `vouchfx` CLI child
process inherits this server's environment unmodified, which is what lets a suite's own
`${secret:env/...}` reference resolve inside the engine — this server never builds or reads that
environment for any other purpose.

## Where to go next

- [Install & registration](install.md) — get the tool on your machine and registered with your MCP
  client.
- [Tool & resource reference](tools-and-resources.md) — the full contract for every tool and resource.
- [Troubleshooting](troubleshooting.md) — the CLI pin, Docker, and timeout scenarios you are most
  likely to hit.
