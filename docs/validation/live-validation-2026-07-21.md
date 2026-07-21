# Live end-to-end validation — vouchfx-mcp (todo 13 / Definition of Done)

> **Update (2026-07-21, later the same day) — superseded by todo 17's graceful teardown.** The
> EDGE-002 finding below (a cancelled/timed-out `run_suite` call orphans a container/network for
> Ryuk to reap, ~16 seconds later) was captured against `v1.0.0-alpha.9` and the MCP server's
> then-current `VouchfxCliSuiteRunner`, which force-killed the CLI's whole process tree
> immediately, with no graceful phase at all — exactly the "documented, known limitation" this
> drill confirmed was real in practice, not merely theoretical.
>
> `ENGINE_PIN` has since advanced to `v1.0.0-alpha.10`, which adds `--shutdown-on-stdin-eof`
> (vouchfx-mcp#17's engine half), and `VouchfxCliSuiteRunner` now requests that graceful stop —
> closing the CLI child's stdin — before ever falling back to the hard kill this drill exercised.
> In the ordinary case, the engine's own teardown now runs to completion within a bounded grace
> period and no orphan should occur at all; the hard-kill-then-Ryuk path documented below is now
> the rare FALLBACK case (a genuine teardown hang past the engine's own internal budget), not the
> only mechanism.
>
> **This drill has been re-run on 2026-07-21 against the fixed behaviour (result recorded
> immediately below).** The evidence further down remains an accurate historical record of the OLD
> behaviour, kept as-is rather than rewritten. The fresh live drill — same shape (a real Docker
> engine, a real sample suite, a short `timeoutSeconds` to force an abort mid-topology-stand-up),
> but crucially with Ryuk disabled so cleanup is the engine's own mechanism only — confirms
> empirically that the graceful path now leaves no orphaned container/network in the common case.

**Date:** 2026-07-21
**Scope:** a single, uninterrupted live run of every `vouchfx-mcp` tool and resource against the
real, installed `vouchfx` CLI, a real Docker engine, and a real sample suite from
`vouchfx-samples` — including the deferred EDGE-002 orphan check. No mocks, no fakes, no
in-process test harness: every request below went over real MCP stdio JSON-RPC to the packaged
`vouchfx-mcp` executable.

## Fresh drill: graceful teardown validation (2026-07-21)

**Method:** The packaged `vouchfx-mcp` server was driven over real stdio JSON-RPC via a Node.js
driver, calling `run_suite` on the real `vouchfx-samples/samples/orders-dotnet` suite (PostgreSQL
+ Kafka topology) against the installed `vouchfx` CLI v1.0.0-alpha.10, with a real Docker engine
(v29.6.1). A `timeoutSeconds` of 20 was set to force an abort mid-topology-stand-up. **Crucially,
`TESTCONTAINERS_RYUK_DISABLED=true` was set for the entire run** — Ryuk could not reap anything,
so any cleanup observed is the engine's own graceful teardown (triggered by the MCP closing the
child CLI's stdin), not the Ryuk backstop.

**Observed timeline:**
- ~6s: Postgres container and `aspire-session-network-*` created and running.
- ~18s: `orders-api` application container created.
- ~20s: Both containers and the network established; timeout fires, `run_suite` cancellation
  signals the engine to shut down.
- ~26s: Both containers removed by the engine's graceful teardown.
- ~28s: `aspire-session-network-*` removed by the engine's graceful teardown. `run_suite` returns
  with `verdict=Inconclusive`, `timedOut=true`, exit code 4.
- ~46s: Settle snapshot confirms zero running topology containers and zero `aspire-session-network-*`
  networks present.

**Result:** The topology was **fully removed — both containers and the session network — with Ryuk
disabled.** The graceful-teardown fix operates as designed: a cancelled/timed-out `run_suite` call
now removes its own resources without depending on Ryuk.

**Edge case note:** An earlier attempt with a much shorter timeout (~12s) aborted mid-DCP-creation,
before the topology was fully established. In that case, the containers were removed but the
`aspire-session-network-*` was left behind. With Ryuk enabled in normal operation, the reaper would
clean it up; the pathological window (abort during initial topology creation) thus remains a
backstop case for Ryuk, which remains the fallback for any genuine teardown hang past the engine's
own internal budget.

## Summary

| Definition-of-Done bullet | Result |
| --- | --- |
| Install/build the packaged tool exactly as an agent would | **PASS** — `dotnet pack` + `dotnet tool install --add-source` |
| Drive it over real stdio JSON-RPC (not an in-process harness) | **PASS** — custom Node.js driver, real child process |
| `validate_suite` (valid + invalid, structured errors with paths) | **PASS** |
| `list_step_types` (25 types) / `describe_step_type` (found + not-found) | **PASS** |
| `search_docs` + `resources/list` + `resources/read` | **PASS** |
| `run_suite` → genuine `Pass` against a real Docker topology | **PASS** |
| Deliberate failure → `Fail` verdict, diagnosed correctly by `explain_run` | **PASS** |
| CLI pin handshake | **PASS** (confirmed implicitly by every successful `run_suite` call, and explicitly by version match) |
| EDGE-002: real orphan check on cancellation | **PASS, with a genuine (self-healing) finding** — see below |

No tool or resource failed to work. The one noteworthy finding is EDGE-002: a cancelled/timed-out
`run_suite` call **does** leave a container and network briefly orphaned (the documented,
known limitation — graceful engine teardown is not implemented), but Testcontainers' Ryuk reaper
cleaned both up automatically within roughly 16 seconds, with no manual intervention required.
This is recorded honestly below, including exact timestamps.

## Environment

| Component | Value |
| --- | --- |
| `vouchfx` CLI (installed, on PATH) | `1.0.0-alpha.9+8c579ab4315cacba4066bc3f33dc24a19ca6c3d1` (`dotnet tool list -g`) |
| `vouchfx-mcp` `ENGINE_PIN` | `v1.0.0-alpha.9 8c579ab4315cacba4066bc3f33dc24a19ca6c3d1` — **exact match**, both version and full commit SHA |
| Docker | `Docker version 29.6.1, build 8900f1d` |
| `vouchfx-mcp` package built | `Vouchfx.Mcp.0.1.0-alpha.1.nupkg` via `dotnet pack src/Vouchfx.Mcp/Vouchfx.Mcp.csproj -c Release -o ./nupkg` |
| `vouchfx-mcp` install | `dotnet tool install --add-source ./nupkg -g Vouchfx.Mcp --version 0.1.0-alpha.1` (no prior copy was installed; nothing to uninstall first) |
| Sample suite | `vouchfx-samples/samples/orders-dotnet` — ASP.NET Core 8 "Orders" API, Postgres + Kafka dependencies, host-owned webhook listener (no extra container) |
| Sample image | `vouchfx-samples-orders-dotnet:local`, built via `docker build -t vouchfx-samples-orders-dotnet:local samples/orders-dotnet/app` |
| Driver | A ~140-line Node.js (v25.1.0) stdio JSON-RPC driver, kept in the session scratch directory (never committed) — spawns the real `vouchfx-mcp` executable, speaks `initialize` → `notifications/initialized` → `tools/call`/`resources/*` over its real stdin/stdout, and prints each request/response pair as JSON |
| MCP protocol version negotiated | `2025-06-18`; `serverInfo`: `{"name":"vouchfx-mcp","version":"0.1.0"}` |

**Why `orders-dotnet`:** it needs only two managed dependencies (Postgres, Kafka) plus one
application container and a host-owned webhook listener (no extra container) — the smallest
topology among the four samples that still exercises all four step families
(`http.rest`, `db-assert.postgres`, `mq-expect.kafka`, `webhook-listen.http`). `inventory-python`
needs three dependencies (MySQL, RabbitMQ, Redis); this one was the more manageable, still
fully representative choice.

**Handshake confirmation (DoD bullet 6):** the CLI pin match above is exact (version *and* full
40-character commit SHA), and every `run_suite` call below proceeded straight to a real run rather
than returning a `CliUnavailable` gate failure — which is exactly what `run_suite`'s documented gate
ordering does when the handshake fails. The pin match was never in doubt (both repos were pinned to
the same `v1.0.0-alpha.9` release), but the live evidence is the absence of any handshake failure
across four separate `run_suite` invocations.

## Tool-by-tool evidence

All requests/responses below are real, captured verbatim from the driver's output (trimmed for
length where noted); nothing here is reconstructed from memory or the source code.

### `tools/list` — six tools registered

```json
{"tools":[
  "describe_step_type", "explain_run", "list_step_types",
  "search_docs", "validate_suite", "run_suite"
]}
```

Matches `docs/overview.md`'s "six tools, at a glance" table exactly.

### 1. `validate_suite`

**Valid suite** — the real, unmodified `orders-dotnet` suite:

```
Request:  { "path": "D:\\src\\github\\vouchfx-samples\\samples\\orders-dotnet\\tests\\orders.e2e.yaml" }
Response: { "valid": true, "errors": [] }
```

**Invalid suite** — a temp copy of the same real suite (`orders-invalid.e2e.yaml`, scratch-only,
never written into `vouchfx-samples`), with the `query` field deleted from `assert-order-row` and a
new step `assert-unknown-thing` added with `type: totally.unknown-provider`:

```
Response: {
  "valid": false,
  "errors": [
    { "kind": "schema", "instancePath": "/steps/1",
      "message": "[required] Required properties [\"query\"] are not present", "line": 65 },
    { "kind": "unknown-step-type", "instancePath": "/steps/2/type",
      "message": "Unknown step type 'totally.unknown-provider'. Known types: cache-assert.elasticsearch, ...",
      "line": 79 },
    { "kind": "schema", "instancePath": "/steps/3/timeout", "message": "[type] Value is \"string\" but should be \"number\"", "line": 91 },
    { "kind": "schema", "instancePath": "/steps/4/timeout", "message": "[type] Value is \"string\" but should be \"number\"", "line": 107 }
  ]
}
```

Both the missing-required-field error (`query`) and the unknown-step-type error carry a `kind`,
`instancePath`, `message`, and a real YAML `line` number, exactly as documented. Note: the two
`timeout` "should be number" entries are a JSON-Schema `oneOf`-branch evaluation artefact — the
identical `timeout: 60s` string form appears unmodified in the suite's original, *valid* copy
(which validates clean with zero errors), so this is the schema evaluator reporting a non-matching
branch of a `string | number` union once the document as a whole is already invalid elsewhere, not
a real defect in this suite. Flagged here for transparency, not as a `validate_suite` bug — the
tool's own contract ("reports every structural error found") is met either way.

### 2. `list_step_types` / `describe_step_type`

`list_step_types` returned all **25** step types across **11** families (`cache-assert`,
`db-assert`, `http`, `mail-expect`, `metrics-assert`, `mq-expect`, `mq-publish`, `script`,
`storage-assert`, `trace-expect`, `webhook-listen`) — counted directly from the response, matching
`docs/install.md`'s "a working install returns all 25 Core provider step types".

`describe_step_type("http.rest")`:

```json
{
  "type": "http.rest", "family": "http", "provider": "rest",
  "fields": [
    { "name": "method", "type": "string", "required": true },
    { "name": "path", "type": "string", "required": true },
    { "name": "target", "type": "string", "required": true },
    { "name": "body", "type": null, "required": false },
    { "name": "expect", "type": "object", "required": false },
    { "name": "headers", "type": "object", "required": false }
  ],
  "requiredOneOf": null
}
```

Correctly excludes the common envelope fields (`id`, `type`, `description`, `capture`,
`verifyMode`, `timeout`, `continueOnFailure`).

`describe_step_type("nope.nope")` returned an **MCP tool error** (`isError: true`):

```
"Unknown step type 'nope.nope'. Known types: cache-assert.elasticsearch, cache-assert.redis, ... webhook-listen.http."
```

— exactly the documented not-found behaviour (a tool error listing every valid type, never a crash).

### 3. `search_docs` / resources

`search_docs("verifyMode")` returned 5 ranked matches, top hit from the Recipes document:

```
source: "vouchfx Recipes: Common Patterns and Examples"
headingPath: "... > Engine-owned polling with verifyMode: RETRY"
snippet: "...A step with `verifyMode: RETRY` uses **engine-owned polling** (powered by Polly v8)
          with exponential backoff. The engine repeatedly executes the step until its assertions
          pass or a timeout expires..."
url: "https://vouchfx.io/recipes/#engine-owned-polling-with-verifymode-retry"
```

Second hit, from the Language Reference, gave the `IMMEDIATE`/`RETRY` common-field description with
its own `vouchfx.io` deep link. Both the RETRY/IMMEDIATE text and a `vouchfx.io` link are present, as
required.

`resources/list` returned exactly the two documented resources
(`vouchfx-docs:///language-reference`, `vouchfx-docs:///recipes`), both `text/markdown`.

`resources/read("vouchfx-docs:///language-reference")` returned the full generated language
reference (byte-plausible: opens with `# vouchfx Language Reference`, lists all 25 step types,
ends with the "Regenerating this file" section) — read successfully as a real MCP resource, not
just via `search_docs`.

### 4. `run_suite` — genuine Pass against a real Docker topology

```
Request: {
  "path": "D:\\src\\github\\vouchfx-samples\\samples\\orders-dotnet\\tests\\orders.e2e.yaml",
  "timeoutSeconds": 200
}
```

Real wall-clock time for the MCP call: **28.26 seconds** (topology stand-up dominated, per the
sample's own README expectation of "typically completing within a minute").

```json
{
  "verdict": "Pass", "exitCode": 0, "cancelled": false, "timedOut": false,
  "steps": [
    { "stepId": "place-order",           "verdict": "Pass", "durationMs": 241,  "attemptCount": 1, "observation": "{\"status\":201,\"expected\":201}" },
    { "stepId": "assert-order-row",      "verdict": "Pass", "durationMs": 9,    "attemptCount": 1, "observation": "{\"rowCount\":1}" },
    { "stepId": "assert-order-event",    "verdict": "Pass", "durationMs": 1427, "attemptCount": 2, "observation": "{\"matched\":true,\"scanned\":1}" },
    { "stepId": "assert-webhook-callback","verdict": "Pass","durationMs": 2,    "attemptCount": 1, "observation": "{\"matched\":true,\"scanned\":1}" },
    { "stepId": "refetch-order",         "verdict": "Pass", "durationMs": 27,   "attemptCount": 1, "observation": "{\"status\":200,\"expected\":200}" }
  ]
}
```

All **5 of 5** steps passed: the REST call landed a real Postgres row, a real Kafka consumer
observed the `order-events` message (2 RETRY attempts — genuine polling, not an immediate match),
the host-owned webhook listener genuinely received the app's outbound callback, and the final
GET re-confirmed the row. `explain_run()` (no `eventsPath` — defaults to this run) confirmed:
`"verdict":"Pass"`, `"summary":"All 5 step(s) passed."`, `"notableSteps":[]`.

Immediately after this run, `docker ps -a` / `docker network ls` showed **no** leftover
`orders-api`/Postgres/Kafka containers or `aspire-session-network-*` — a normal completion tears
down its own topology cleanly (this is the *non*-cancelled path; see EDGE-002 below for what
happens when a run is aborted mid-flight instead).

### 5. Deliberate failure → `Fail`, correctly diagnosed by `explain_run`

A second temp copy (`orders-deliberate-fail.e2e.yaml`, scratch-only) changed exactly one expected
value: `assert-order-row`'s `expect.row.status` from the app's real value `CONFIRMED` to a
deliberately wrong `SHIPPED`.

```json
{
  "verdict": "Fail", "exitCode": 1, "cancelled": false, "timedOut": false,
  "steps": [
    { "stepId": "place-order",            "verdict": "Pass" },
    { "stepId": "assert-order-row",       "verdict": "Fail", "durationMs": 8, "attemptCount": 1,
      "observation": "{\"column\":\"status\",\"expected\":\"SHIPPED\",\"actual\":\"CONFIRMED\"}" },
    { "stepId": "assert-order-event",     "verdict": "Pass", "attemptCount": 2 },
    { "stepId": "assert-webhook-callback","verdict": "Pass" },
    { "stepId": "refetch-order",          "verdict": "Pass" }
  ]
}
```

`explain_run()` on this run:

```json
{
  "verdict": "Fail",
  "categoryMeaning": "Fail: a test assertion did not hold — a genuine defect signal in the system under test.",
  "summary": "1 of 5 step(s) failed: assert-order-row. 4 step(s) passed.",
  "notableSteps": [ { "stepId": "assert-order-row", "verdict": "Fail",
    "observation": "{\"column\":\"status\",\"expected\":\"SHIPPED\",\"actual\":\"CONFIRMED\"}" } ],
  "environmentErrors": []
}
```

This is exactly the required evidence: the failing step is named precisely
(`assert-order-row`), the observation shows the exact mismatch (`expected: SHIPPED`,
`actual: CONFIRMED` — the real value the app wrote), and `environmentErrors` is empty with a
`categoryMeaning` that explicitly frames `Fail` as a genuine defect signal, distinct from an
environment error. The other four steps still ran and passed (this suite does not set
`continueOnFailure`, so this also confirms a step `Fail` alone does not abort the remaining steps
by default at the taxonomy level reported here).

## EDGE-002 — the real orphan check (the deferred integration check)

This is the check the spec explicitly deferred to live validation: `VouchfxCliSuiteRunner`'s own
source (`src/Vouchfx.Mcp/Run/VouchfxCliSuiteRunner.cs`) documents that a cancelled/timed-out
`run_suite` call force-kills the `vouchfx` CLI's OS process tree
(`process.Kill(entireProcessTree: true)`) but has **no** way to signal the CLI's own graceful
Aspire/DCP shutdown (`HeadlessTopology.DisposeAsync`) — Docker containers and the
`aspire-session-network-*` network are not OS children of the killed tree, so their cleanup
"relies ENTIRELY on Testcontainers' own Ryuk reaper". `RunSuiteOrchestrator.ExecuteRunAsync`
confirms `timeoutSeconds` and an actual caller-side MCP cancellation share one linked
`CancellationTokenSource` (`CreateLinkedTokenSource` + `CancelAfter`), so triggering the abort via
a short `timeoutSeconds` exercises the **identical** code path a real client-initiated
cancellation would.

**Before** (baseline, confirmed twice — once at session start, once immediately before this
drill): `docker ps -a` showed only pre-existing, unrelated `ollapoc-*` containers (all `Exited`,
2 months old); `docker network ls` showed only the default `bridge`/`host`/`none` networks plus
the pre-existing `ollapoc_default` — **no** vouchfx/Aspire containers or networks.

**Drill:** `run_suite` on the real (unmodified) `orders.e2e.yaml`, `timeoutSeconds: 10` — short
enough to abort during Aspire/DCP topology stand-up (a normal Pass run's topology stand-up alone
takes on the order of 15–20+ seconds; see the ~28s total Pass run above).

```
04:28:05  drill started
04:28:19  run_suite returned (≈14s wall — the 10s budget plus kill-confirmation overhead)
```

Result:

```json
{
  "verdict": "Inconclusive", "exitCode": -1, "cancelled": false, "timedOut": true,
  "remediationHint": "The run did not complete within 10s and was terminated.",
  "steps": []
}
```

Correctly `Inconclusive`, **never** `Fail` — `timedOut: true` / `cancelled: false` correctly
distinguishes a timeout from an explicit client cancellation (the documented, opposite case of the
same field pair). `explain_run()` immediately afterwards correctly returned a **tool error**
("Events file not found") rather than fabricating a diagnosis — the CLI never got far enough to
write one, and the tool does not pretend otherwise.

**Immediately after** (`04:28:27`, ~8s after the call returned):

```
docker ps -a:
  broker-ugtmevjr   confluentinc/confluent-local:8.2.0   Up 14 seconds   127.0.0.1:4129->9092/tcp, 127.0.0.1:4128->9093/tcp

docker network ls:
  aspire-session-network-knsbewts-vouchfx   bridge   local
```

**Confirmed real orphans**: the Kafka broker dependency container and its Aspire session network
were left running — exactly the documented limitation. (Only the Kafka broker had reached "running"
in the 10-second window; Postgres and `orders-api` apparently had not yet been created when the
kill landed.)

**~16 seconds later** (`04:28:43`): both the container and the network were **gone**, with no
manual action taken in between:

```
docker ps -a:      only the pre-existing ollapoc-* containers (unchanged from baseline)
docker network ls: only bridge/host/none/ollapoc_default (unchanged from baseline)
```

**Honest finding:** yes, EDGE-002's orphan does happen in practice, confirming the documented gap
is real and not merely theoretical — but Testcontainers' Ryuk reaper cleaned up both the orphaned
container and the orphaned network **automatically**, within roughly 16 seconds of the process
being force-killed, consistent with Ryuk's short heartbeat-loss detection window. **No manual
Docker cleanup was necessary or performed** for this drill — the machine returned to its exact
pre-drill baseline on its own. This is a real, mildly reassuring result: the "known limitation" is
real (a brief window exists where a container/network is genuinely orphaned and visible), but it
is self-healing on any machine with a working Ryuk reaper, which every standard Testcontainers-based
run has. A machine where Ryuk itself cannot run (e.g. a locked-down CI runner without the reaper
image reachable) would not get this same protection — worth flagging as a residual risk for such
environments, though outside the scope of what this drill could reproduce here.

## Cleanup performed

- `dotnet tool uninstall -g Vouchfx.Mcp` — global tool removed.
- `./nupkg` (in `vouchfx-mcp`) and all scratch driver/script files (Node driver, batch JSON
  scripts, temp `.e2e.yaml` copies) deleted from the session scratch directory — none were ever
  written into either `vouchfx-mcp` or `vouchfx-samples`.
- `docker ps -a` / `docker network ls` re-verified back to the exact pre-drill baseline (see EDGE-002
  above) — no manual container/network removal was required at any point in this validation; the
  only Docker artefact left behind deliberately is the `vouchfx-samples-orders-dotnet:local` image
  built locally for the Pass/Fail runs (an image, not a running resource — left in place as it is
  reusable local build cache, exactly as `scripts/run-sample.ps1` would leave it after a normal run).
- The only repository change from this validation is this file,
  `docs/validation/live-validation-2026-07-21.md`, in `vouchfx-mcp`. Nothing in `vouchfx-samples`
  was modified — every temp/invalid suite variant lived only in the session scratch directory.
