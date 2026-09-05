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

## The sixteen tools, at a glance

| Tool | What it does |
| --- | --- |
| [`validate_suite`](tools-and-resources.md#validate_suite) | Validates an `.e2e.yaml` suite from a file or inline YAML against the engine's JSON Schema and ten semantic rules (eleven codes, one reserved) at a chosen validation level (`schema`, `semantic`, or `full`), without running it. |
| [`normalize_suite`](tools-and-resources.md#normalize_suite) | Returns a suite's canonical text and its full validation result to the HOST. Normalization is opt-in (comment loss is permanent, and the result says so via `commentsDropped`); without it, returns the validation result wrapped with `null`. The canonical text is proved to re-parse to the same document before it is returned — otherwise `normalizedYaml` is `null` with a `normalizationRefused` reason. The server never writes; the host decides whether and where to write the returned text. |
| [`list_step_types`](tools-and-resources.md#list_step_types) | Lists every step type the pinned engine supports, grouped by family. |
| [`describe_step_type`](tools-and-resources.md#describe_step_type) | Returns one step type's full required/optional field contract. |
| [`search_docs`](tools-and-resources.md#search_docs) | Free-text search over the vendored language reference and recipe library. |
| [`plan_coverage`](tools-and-resources.md#plan_coverage) | Deterministic, read-only coverage-and-gap analysis over a declared suite set and an optional event history (Planner). |
| [`scaffold_suite`](tools-and-resources.md#scaffold_suite) | Generates a machine-drafted, schema-valid `.e2e.yaml` skeleton from structured step types, ids, and an environment outline (Generator). |
| [`run_suite`](tools-and-resources.md#run_suite) | Runs a suite through the installed `vouchfx` CLI and reports its taxonomy-faithful verdict. |
| [`explain_run`](tools-and-resources.md#explain_run) | Diagnoses a completed run purely by reading its JSON Lines event stream — never re-running anything. |
| [`diagnose_run`](tools-and-resources.md#diagnose_run) | Healer: same taxonomy diagnosis as `explain_run`, plus Fail-only review patch proposals (never auto-applied). |
| [`explain_diagnostic`](tools-and-resources.md#explain_diagnostic) | Looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title, explanation, common causes, and fixes. |
| [`get_schema`](tools-and-resources.md#get_schema) | Returns the composed JSON Schema — the whole document or one addressable section — as a schema document or markdown digest. |
| [`get_run_events`](tools-and-resources.md#get_run_events) | Pages a completed run's raw JSON Lines events exactly as the engine wrote them — filtered by event type and step before paging, with an opaque cursor. For hosts building their own timeline instead of consuming `explain_run`'s summary. |
| [`get_run_status`](tools-and-resources.md#get_run_status) | Returns one run's current lifecycle state from the persisted run registry — status, verdict, timestamps, the suites it covered, its events file, and its labels. |
| [`cancel_run`](tools-and-resources.md#cancel_run) | Asks an in-flight run to stop, through exactly the graceful-stop mechanism `run_suite` already uses. A cancelled run is `Inconclusive`, never `Fail`. |
| [`list_runs`](tools-and-resources.md#list_runs) | Pages the run registry newest first, filtered by label and/or start time, with the same opaque cursor contract `get_run_events` uses. |

The full field-level contract, result shape and notable behaviours for each tool are on the
[tool & resource reference](tools-and-resources.md) page.

## Documentation resources

Alongside the sixteen tools, the server advertises two static MCP resources — the generated
**vouchfx language reference** and the **vouchfx recipes** library, each the byte-exact vendored copy of
the pinned engine commit's own Markdown documentation — plus a templated **diagnostic catalogue**
resource family (`vouchfx-docs:///errors/{code}`) covering every code `explain_diagnostic` can explain.
An agent can read any of them directly as a resource, or reach the vendored-document content indirectly
through `search_docs`. See [Resources](tools-and-resources.md#resources) for all three.

## Planner workflow (plan → scaffold → validate → run)

For a team with a suite folder and a pile of run history but no mechanical way to see what to test
next, `plan_coverage` (Spec D / M3 Planner) answers "what should I test next?" deterministically:

1. Host calls **`plan_coverage`** with the declared suite path (and, optionally, the event history
   `run_suite` writes). A call that finds gaps is a **successful** result — gaps are the data this
   tool exists to surface, never an error.
2. The host picks a gap finding and passes its `suggestedTypes[0]`/`suggestedStepId` **unchanged**
   into **`scaffold_suite`**'s own `steps[].type`/`steps[].id` — no re-derivation needed.
3. Continue the Generator path below: fill semantics, `validate_suite`, `run_suite`.

`plan_coverage` never writes a suite file, never calls a model, and never invokes git — see
[plan_coverage](tools-and-resources.md#plan_coverage) for the full finding-kind list and threshold
overrides.

## Generator workflow (scaffold → validate → run)

Authoring is the adoption bottleneck: free-text goals live **only in the host LLM** (Claude Code, an
MCP-capable IDE, etc.). This server never hosts a model and never accepts free text on
`scaffold_suite`. The deterministic path is:

1. Host LLM turns a free-text goal into structured step types and ids (using `list_step_types` /
   `describe_step_type` for catalogue grounding — Spec A).
2. Host calls **`scaffold_suite`** with structured args only (steps, optional services/dependencies).
3. Host LLM (or human) **fills semantics** in the returned YAML skeleton (paths, queries, expects).
4. Host calls **`validate_suite`**, then **`run_suite`**.

Scaffold output is **schema-valid with placeholders**, marked as machine-drafted (provenance
comments); a human must review before trust. Secrets appear only as `${secret:…}` references, never
literals. See [scaffold_suite](tools-and-resources.md#scaffold_suite). Humans are **not** expected to
maintain a parallel JSON-intent product as primary UX — intent JSON is an engine CLI transport detail.

## Healer workflow (run → explain/diagnose → human applies)

After a suite run fails or is unclear, authors and MCP hosts use the **Healer** path (M2):

1. Host calls **`run_suite`** (events file path returned).
2. Host calls **`explain_run`** and/or **`diagnose_run`** on that events path (or omits the path to
   default to the most recent finished run in the registry, which spans server restarts when
   launched with `--workspace`).
3. For genuine product **Fail**s with observation evidence, `diagnose_run` returns **review-only**
   patch proposals (`stepId`, `rationale`, unified-diff style `patch`). The host LLM may refine
   wording; **this server never auto-applies**, never writes the suite file, and never hosts a model.
4. A human (or host under human review) applies any accepted change, then re-validates and re-runs.

**Fail vs EnvironmentError:** only step-level **Fail** with usable observation evidence yields
proposals. **EnvironmentError** returns infrastructure guidance only (image pull, health, provision)
and **never** YAML rewrite patches. **Inconclusive** may include non-patch guidance but **must not**
include suite-rewrite patches. Free text belongs only in the host conversation — not as a diagnose
tool parameter. See [diagnose_run](tools-and-resources.md#diagnose_run).

## Status: early prerelease

This project is being built spec-first: features land against approved specs in a spec → build →
review loop, one requirement at a time. As things stand:

- All **sixteen tools**, **both vendored-document resources**, and the **diagnostic catalogue resource**
  are real, fully functional implementations — not stubs. The server is feature-complete for its
  current scope.
- `validate_suite`, `search_docs`, and `explain_diagnostic` work from embedded vendored/catalogue
  content and keep working when the `vouchfx` CLI is not installed. `get_schema` (CLI-optional)
  serves the embedded composed schema offline and optionally cross-verifies it against a running CLI
  when one matching `ENGINE_PIN` is present, reporting any divergence as a diagnostic on the
  still-successful result.
- `list_step_types` and `describe_step_type` load the **live** shape-level catalogue from the pinned
  engine via `vouchfx list --json` (required/optional fields, capture support, family intent). They
  require a CLI that implements Spec A (engine-schema-and-catalogue-export) and fail fast rather than
  returning type keys alone without field metadata.
- `plan_coverage` requires a CLI that implements the M3 Planner (`vouchfx plan --json`). The current
  `ENGINE_PIN` (v1.0.0-rc.4) implements it. MCP CI tests use a fake CLI so they stay green regardless of
  what CLI (if any) is installed on the runner.
- `scaffold_suite` requires a CLI that implements Spec B (`vouchfx scaffold --intent`). The current
  `ENGINE_PIN` (v1.0.0-rc.4) implements it. MCP CI tests use a fake CLI so they stay green regardless of
  what CLI (if any) is installed on the runner.
- `run_suite` spawns the `vouchfx` CLI (and, through it, Docker). `explain_run`, `diagnose_run` and
  `get_run_events` only ever read a local events file — never re-run anything. `get_run_status` and
  `list_runs` read only the run registry, so they need neither an events file nor a CLI, and none of
  the five ever takes the workspace run lock — they are safe to call while a run is in flight.
- `cancel_run` needs no CLI either, but is **not** read-only: it stops an in-flight run through
  exactly the graceful mechanism `run_suite` uses. Cancellation reaches only runs held by the server
  process you are calling — a run held by another server process against the same workspace is
  refused by name (`VFX-E-1507`) rather than silently reported as cancelled, because there is no IPC
  channel through the file lock that serialises them. Asynchronous (`wait: false`) execution, which
  would make this a full detached-run workflow rather than a way to manage a blocking one, awaits
  upstream ask U4.
- The `Vouchfx.Mcp` package is built as a `dotnet tool` (`PackAsTool`, command `vouchfx-mcp`) but **has
  not yet had a tagged release published to NuGet.org** — what remains is the first tagged release.
  Expect rough edges; see [Install & registration](install.md) for what that means in practice today.

## The engine pin

This server never builds the vouchfx engine from source. It is currently pinned to
**v1.0.0-rc.4** (commit `be12ebd126fdf03dcea9eade7bcec3afbcba001b`) — recorded in this repository's
[`ENGINE_PIN`](https://github.com/tomas-rampas/vouchfx-mcp/blob/main/ENGINE_PIN) file, which explains
exactly what each field pins, how the vendored schema and documentation stay drift-gated against it, and
how the pin is advanced over time. `run_suite`, `list_step_types`, `describe_step_type`, `plan_coverage`,
and `scaffold_suite` refuse to use a mismatched or missing CLI — a mismatch is always a structured result,
never silent behavioural drift; see [Troubleshooting](troubleshooting.md#cli-pin-version-mismatch).

### Minimum engine for the live catalogue

Shape-level catalogue tools need **Spec A** on the installed engine: `vouchfx schema` and a rich
`vouchfx list --json` document where every entry carries `requiredFields`, `optionalFields`,
`captureSupported`, and `familyIntent`. Engines that only emit thin type/family/provider keys are
rejected with an explicit error (EDGE-004). Advance `ENGINE_PIN` to a published build that includes
that export when it is available; this server does not invent field metadata from a thin list.

### Minimum engine for plan_coverage (Planner)

`plan_coverage` needs the **M3 Planner** on the installed engine: `vouchfx plan <path> [--events
<path>] --json`. `ENGINE_PIN` (v1.0.0-rc.4) implements it. If a LOCALLY installed CLI still lacks that
subcommand (predates the pin), the tool returns a clear CLI-unavailable error rather than inventing a
report locally (CLI and MCP must not drift) — advancing `ENGINE_PIN` further in future remains a
release step, never a silent in-server fallback.

### Minimum engine for scaffold (Generator)

`scaffold_suite` needs **Spec B** on the installed engine: `vouchfx scaffold --intent <file|->`.
`ENGINE_PIN` (v1.0.0-rc.4) implements it. If a LOCALLY installed CLI still lacks that subcommand
(predates the pin), the tool returns a clear CLI-unavailable error rather than inventing YAML locally
(CLI and MCP must not drift) — advancing `ENGINE_PIN` further in future remains a release step, never
a silent in-server fallback.

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
