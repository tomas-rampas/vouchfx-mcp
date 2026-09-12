# What vouchfx-mcp is

`vouchfx-mcp` is a local, stdio-first [Model Context Protocol](https://modelcontextprotocol.io/) server (with an optional, flag-gated HTTP transport) for
AI coding agents. It wraps the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) engine —
a compiler and runner for declarative `.e2e.yaml` integration-test suites that prove a distributed .NET
system end-to-end, across a REST call, a Kafka event, a database mutation and an outbound webhook — so
an agent can validate, run and diagnose a suite directly, without shelling out to `vouchfx` and parsing
its console output by hand.

It does **not** build or embed the engine. It spawns the published `vouchfx` command-line tool as a
subprocess for suite runs **and** for the live step-type catalogue (`vouchfx list --json`), and
vendors byte-exact copies of the engine's JSON Schema and documentation for offline suite validation
and doc search — see [Install & registration](install.md) and the [engine pin](#the-engine-pin) below.

## The eighteen tools, at a glance

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
| [`diagnose_run`](tools-and-resources.md#diagnose_run) | Healer: same taxonomy diagnosis as `explain_run`, plus two proposal kinds — Fail review patches and EnvironmentError/Inconclusive scoped spec-edit proposals (never auto-applied). |
| [`explain_diagnostic`](tools-and-resources.md#explain_diagnostic) | Looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title, explanation, common causes, and fixes. |
| [`get_schema`](tools-and-resources.md#get_schema) | Returns the composed JSON Schema — the whole document or one addressable section — as a schema document or markdown digest. |
| [`get_run_events`](tools-and-resources.md#get_run_events) | Pages a completed run's raw JSON Lines events exactly as the engine wrote them — filtered by event type and step before paging, with an opaque cursor. For hosts building their own timeline instead of consuming `explain_run`'s summary. |
| [`get_run_status`](tools-and-resources.md#get_run_status) | Returns one run's current lifecycle state from the persisted run registry — status, verdict, timestamps, the suites it covered, its events file, and its labels. |
| [`cancel_run`](tools-and-resources.md#cancel_run) | Asks an in-flight run to stop, through exactly the graceful-stop mechanism `run_suite` already uses. A cancelled run is `Inconclusive`, never `Fail`. |
| [`list_runs`](tools-and-resources.md#list_runs) | Pages the run registry newest first, filtered by label and/or start time, with the same opaque cursor contract `get_run_events` uses. |
| [`get_step_timeline`](tools-and-resources.md#get_step_timeline) | Returns one step's **complete** RETRY attempt timeline from a finished run. `explain_run` shrinks its own attempt arrays under response-size pressure; this tool never shortens the list, dropping per-attempt evidence text instead. |
| [`get_run_artifacts`](tools-and-resources.md#get_run_artifacts) | Reports what a finished run left behind — its event-stream artefact and the environment resources the run's own events named. **Honestly partial**: every result carries `partial: true` and a `gaps` array naming each field this build cannot populate and why. |

The full field-level contract, result shape and notable behaviours for each tool are on the
[tool & resource reference](tools-and-resources.md) page.

## Resources

Alongside the eighteen tools, `resources/list` advertises **four concrete resources** and
`resources/templates/list` advertises **seven URI templates** — six families, with error pages served
under two schemes. The two vendored engine documents — **language reference** and **recipes** — are
byte-exact copies of the pinned commit's own Markdown documentation; a **workspace specs index** lists
the suites under a configured workspace (and says `workspaceConfigured: false` when none is, rather
than looking empty); and a **DSL guide for agents** (`vouchfx://docs/dsl-guide`) teaches the
`.e2e.yaml` language in one read — this repository's own writing rather than a vendored copy, with
every YAML example a complete document that the test suite validates against the vendored schema. The
templated families are **schema versions** (with a `latest` alias),
**error pages** (under both `vouchfx-docs:///errors/{code}` and `vouchfx://docs/errors/{code}` — two
templates, identical bytes), three **example suites** teaching common patterns, and **run resources**
(`verdict`, `events`, `logs/{container}`) serving the same payloads the corresponding tools return. An
agent can read any of them directly as a resource, or reach the vendored-document content indirectly
through `search_docs`. See [Resources](tools-and-resources.md#resources) for specifications of all
four concrete resources and all seven templates.

## Prompts

Alongside the tools and resources, `prompts/list` advertises **four MCP prompts**. A prompt is a
reusable, parameterised instruction a host can invoke on the user's behalf; these encode the
*method* a trained vouchfx operator follows, so any MCP host behaves like vouchfx's own authoring
agent with no separate installation. Each ships as a markdown file with YAML front matter, embedded
into the assembly — the front matter is the single source of truth for the prompt's name,
description and arguments, so what `prompts/list` advertises is built from the same file a human
edits.

**`author_scenario`** walks a host through authoring a passing `.e2e.yaml` scenario using only this
server's tools: ground yourself in `get_schema` and the vendored documents, find the gap with
`plan_coverage` and scaffold from it, copy step contracts from `list_step_types`/`describe_step_type`,
validate at `level: full` and loop (capped at five iterations), normalize, let the HOST write the file
with its own tools (this server never writes one), run, and interpret the verdict by taxonomy — with
the rule that a `Fail` is a defect and its assertion must never be weakened to force a pass.

**`heal_run`** walks a host through healing a run that ended in `EnvironmentError` or `Inconclusive`:
resolve the run with `get_run_status` (the diagnosis tools take an events path, not a run id),
diagnose with `diagnose_run`, read each non-Pass step's `reason.kind` and the run's
`classificationHints`, then filter `specEditProposals` against an `allowedScopes` permission list —
applying only in-scope proposals, with the HOST making the edit — re-run once, and report root cause,
quoted evidence, change and confidence. It states the same taxonomy rule first: a `Fail` is a defect,
and must not be acted on except to explain it.

**`review_spec`** is a pre-flight checklist over an existing suite: run `validate_suite` at
`level: full` first — three of the seven review categories are already decided there mechanically —
then apply judgment to the four that no tool can decide (tautological assertions, hard-coded ids,
missing negative-path coverage, and coverage gaps from `plan_coverage`, which is this repository's
substitute for the retired `get_topology`). It produces a checklist with a severity and a concrete
edit per finding, and changes nothing itself.

**`explain_failure`** explains one step of one run to a developer who has never used vouchfx, in 200
words or fewer: what the step attempted, what it observed (from `get_step_timeline`, whose `specPath`
comes from `get_run_status`), and what the outcome means by taxonomy — using the response strings
`Pass`, `Fail`, `EnvironmentError` and `Inconclusive`, never the engine's wire tokens.

These four are the complete prompt set. See [Prompts](tools-and-resources.md#prompts).

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
3. `diagnose_run` returns **two kinds of review-only proposals**:
   - **Fail steps**: patch proposals (`stepId`, `rationale`, unified-diff style `patch`) for genuine product failures with observation evidence.
   - **EnvironmentError/Inconclusive**: scoped spec-edit proposals (`stepId` or null, `scope` one of environment/timeouts/match/capture, `rationale`, YAML fragment `suggestedEdit`) when the reason classifier assigned a structured reason to the outcome.
   The host LLM may refine wording; **this server never auto-applies**, never writes the suite file, and never hosts a model.
4. A human (or host under human review) applies any accepted change, then re-validates and re-runs.

**Proposal scoping:** Fail steps get review patch proposals only. EnvironmentError/Inconclusive get
scoped spec-edit proposals only (never suite-rewrite patches for Fail). **EnvironmentError**
additionally returns infrastructure guidance (image pull, health, provision) **alongside** (not
instead of) spec-edit proposals. **Inconclusive** may include guidance but gets proposals only when
the reason classifier assigned a kind. Free text belongs only in the host conversation — not as a
diagnose tool parameter. See [diagnose_run](tools-and-resources.md#diagnose_run).

## Status: early prerelease

This project is being built spec-first: features land against approved specs in a spec → build →
review loop, one requirement at a time. As things stand:

- All **eighteen tools**, **both vendored-document resources**, and the **diagnostic catalogue resource**
  are real, fully functional implementations — not stubs. The server is feature-complete for its
  current scope.
- The server serves over **stdio by default**, with an optional bearer-authenticated **HTTP transport**
  behind `--transport http` (see [Install](install.md#optional-serving-over-http-instead-of-stdio)).
  Every tool call emits **one bounded span**, and all of this server's own stderr output is **one JSON
  object per line** (see [reading the logs](troubleshooting.md#reading-the-servers-log-output)).
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
  `ENGINE_PIN` (v1.0.0-rc.5) implements it. MCP CI tests use a fake CLI so they stay green regardless of
  what CLI (if any) is installed on the runner.
- `scaffold_suite` requires a CLI that implements Spec B (`vouchfx scaffold --intent`). The current
  `ENGINE_PIN` (v1.0.0-rc.5) implements it. MCP CI tests use a fake CLI so they stay green regardless of
  what CLI (if any) is installed on the runner.
- `run_suite` spawns the `vouchfx` CLI (and, through it, Docker). `explain_run`, `diagnose_run`,
  `get_run_events`, `get_step_timeline` and `get_run_artifacts` only ever read a local events file —
  never re-run anything. `get_run_status` and `list_runs` read only the run registry, so they need
  neither an events file nor a CLI, and none of the seven ever takes the workspace run lock — they are
  safe to call while a run is in flight.
- `get_run_artifacts` is deliberately **partial** while the engine exposes no artifacts directory: it
  returns what the run registry and the run's own event stream hold, marks every result `partial: true`,
  and names each missing field in a `gaps` array rather than leaving an empty array to be interpreted.
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
**v1.0.0-rc.5** (commit `cc5e8efa9c84f59e1135568456f7c156261f6263`) — recorded in this repository's
[`ENGINE_PIN`](https://github.com/tomas-rampas/vouchfx-mcp/blob/main/ENGINE_PIN) file, which explains
exactly what each field pins, how the vendored schema and documentation stay drift-gated against it, and
how the pin is advanced over time. `run_suite`, `list_step_types`, `describe_step_type`, `plan_coverage`,
and `scaffold_suite` refuse to use a mismatched or missing CLI — a mismatch is always a structured result,
never silent behavioural drift; see [Troubleshooting](troubleshooting.md#cli-pin-version-mismatch).

### Minimum engine for the live catalogue

Shape-level catalogue tools need **Spec A** on the installed engine: `vouchfx schema` and a rich
`vouchfx list --json` document where every entry carries `requiredFields`, `optionalFields`,
`captureSupported`, and `familyIntent`. Engines that only emit thin type/family/provider keys are
rejected with an explicit error. Advance `ENGINE_PIN` to a published build that includes
that export when it is available; this server does not invent field metadata from a thin list.

### Minimum engine for plan_coverage (Planner)

`plan_coverage` needs the **M3 Planner** on the installed engine: `vouchfx plan <path> [--events
<path>] --json`. `ENGINE_PIN` (v1.0.0-rc.5) implements it. If a LOCALLY installed CLI still lacks that
subcommand (predates the pin), the tool returns a clear CLI-unavailable error rather than inventing a
report locally (CLI and MCP must not drift) — advancing `ENGINE_PIN` further in future remains a
release step, never a silent in-server fallback.

### Minimum engine for scaffold (Generator)

`scaffold_suite` needs **Spec B** on the installed engine: `vouchfx scaffold --intent <file|->`.
`ENGINE_PIN` (v1.0.0-rc.5) implements it. If a LOCALLY installed CLI still lacks that subcommand
(predates the pin), the tool returns a clear CLI-unavailable error rather than inventing YAML locally
(CLI and MCP must not drift) — advancing `ENGINE_PIN` further in future remains a release step, never
a silent in-server fallback.

## Secret hygiene

`vouchfx-mcp` never resolves `${secret:...}` references itself, and never reads or echoes its own
process environment into a tool result, progress notification, or resource. The vouchfx engine remains
the sole redaction authority: the `--events` JSON Lines fields `run_suite` and `explain_run` relay are
already redacted at source, and this server passes them through untouched. The `vouchfx` CLI child
process inherits this server's environment **except for this server's own HTTP bearer token**
(`VOUCHFX_MCP_HTTP_TOKEN`), which is stripped from every child's environment unconditionally.
Everything else passes through unmodified, which is what lets a suite's own `${secret:env/...}`
reference resolve inside the engine — this server never builds or reads that environment for any
other purpose. That single removal is a narrowing rather than an injection: the token is a credential
this server owns and the engine has no use for, so a suite writing
`${secret:env/VOUCHFX_MCP_HTTP_TOKEN}` deliberately fails to resolve.

## Observability: one span per tool call

Every tool call emits exactly one `ActivitySource` span (OpenTelemetry-compatible — emitted on the BCL instrumentation API, with no OpenTelemetry package in this server; see the collection note below), so you can see tool latency and failure rates in
whatever tracing pipeline you already run.

**Span name.** `vouchfx.mcp.tool/<toolName>` — for example `vouchfx.mcp.tool/validate_suite` or
`vouchfx.mcp.tool/run_suite`. The spans come from an activity source named `Vouchfx.Mcp`, which is
what you filter on to select this server's spans specifically. The MCP C# SDK emits its own span per
request from its own source; this server's span is a child of it, so a call appears as one trace
rather than two disconnected roots.

**Exactly four attributes, and never a fifth.**

| Attribute | Value |
|---|---|
| `workspace.hash` | A truncated SHA-256 of the resolved workspace root — **never the raw path**. Present only when the server was launched with `--workspace`. |
| `runId` | The run the call concerned. Present only on run-lifecycle tools (`run_suite`, `get_run_status`, `get_run_events`, `get_step_timeline`, `get_run_artifacts`, `cancel_run`); absent everywhere else, including `list_runs`, which concerns many runs and no single one. |
| `duration_ms` | How long the call took, in whole milliseconds. |
| `outcome` | `success` or `error`. Never the error's message text. |

That list is exhaustive and is enforced mechanically rather than by convention, in two complementary
ways. The emission helper accepts only these as typed parameters and exposes neither a way to set an
arbitrary key nor the underlying span object, so adding a fifth attribute *through the helper* is a
compile error; `duration_ms` is computed by the helper rather than accepted as a parameter, precisely
so a caller cannot report a number that disagrees with the span it is on. The escape hatches around
the helper — starting a span on another activity source, reaching for the ambient `Activity.Current`,
or calling `SetTag` directly — are closed by a source-level guard test instead, which fails if any of
those appears anywhere in `src/` outside the helper. **No suite YAML, no diagnostic message, no log
line, no environment variable, and no filesystem path beyond `workspace.hash` ever reaches a span.**
The `runId` attribute is shape-checked before it is recorded (`run-` plus 32 lowercase hex), so a
caller passing arbitrary text in that field gets no attribute at all rather than their text on a span.
The workspace hash exists so that traces from different projects can be told apart in a shared backend
without that backend learning your directory layout — treat it as a correlation key, not as a secret:
a path is low-entropy, so someone holding a list of candidate paths could confirm a guess.

**It is additive, and there is nothing to configure.** Instrumentation uses
`System.Diagnostics.ActivitySource` from the BCL. With nothing listening, starting a span returns
`null` and no span object is created at all — so a host with no collector sees no behaviour change, no
new configuration, and no new failure mode. This is a property of the runtime rather than a promise:
there is no exporter in this server, no background export loop, and no network call. It also writes
nothing to stdout — which, when the server runs over its default stdio transport, is the JSON-RPC
channel and must carry nothing else.

**How a collector actually gets these spans.** Choosing an exporter belongs to whoever runs the
process, and that holds on both transports. Over stdio your MCP host spawns `vouchfx-mcp` as a child
and passes no OTLP endpoint, so a built-in exporter would export nothing while adding startup cost
and failure modes. Over `--transport http` the server is long-lived and an exporter COULD reach a
collector — but the attach route below reaches the same collector without this server owning exporter
configuration, credentials or an export loop. Either way it emits on the standard
.NET instrumentation API that any consumer can subscribe to. The usual route is the
[OpenTelemetry .NET automatic instrumentation](https://opentelemetry.io/docs/zero-code/net/) agent,
which attaches to a .NET process and harvests `ActivitySource` output with no changes to the
application; point it at the `vouchfx-mcp` process and configure its exporter as you would for any
other service. If you embed this server in a host you control, registering your own `TracerProvider`
with `AddSource("Vouchfx.Mcp")` works equally well.

For the stderr side of the same picture — the structured JSON log records, their `runId` correlation,
and why the span's `duration_ms` and the log record's duration are two different numbers — see
[Reading the server's log output](troubleshooting.md#reading-the-servers-log-output).

> **Never send traces, or an agent's own diagnostics, to the console for this process.** Over the
> default stdio transport `vouchfx-mcp` speaks JSON-RPC on stdout and nothing else may write there.
> Under `--transport http` stdout is not the protocol channel, but keep this rule anyway: the same
> configuration is usually shared across deployments, and a console exporter that is harmless on one
> corrupts every frame on the other. A console exporter — for example
> `OTEL_TRACES_EXPORTER=console`, or an auto-instrumentation agent configured to log its own
> diagnostics to stdout — interleaves its output with the protocol stream and corrupts the session;
> the host will see malformed frames rather than a tracing problem, which makes it an unpleasant fault
> to diagnose. Use an OTLP exporter, or a file-based one, and direct any agent logging to a file or to
> stderr. This server itself writes nothing to stdout but the protocol, and adds no exporter of its
> own precisely so it never has to make this choice on your behalf.

## Where to go next

- [Install & registration](install.md) — get the tool on your machine and registered with your MCP
  client.
- [Tool & resource reference](tools-and-resources.md) — the full contract for every tool and resource.
- [Troubleshooting](troubleshooting.md) — the CLI pin, Docker, and timeout scenarios you are most
  likely to hit.
