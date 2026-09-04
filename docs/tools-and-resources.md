# Tool & resource reference

Every tool below returns a **structured result** — success or failure — rather than throwing for an
ordinary bad input; where a tool can also return an MCP *tool error* (as opposed to a structured
success payload), that is called out explicitly. Field names shown are the JSON property names the MCP
result actually carries.

**Stable codes on every error and diagnostic.** Two shapes carry them, and the prefix tells you which:

- `VFX-D-…` is a **diagnostic** — a finding about your suite or run, returned as data on a
  **successful** call (`isError` is false). An invalid suite is not a tool failure.
- `VFX-E-…` is an **error** — the call itself could not be performed, returned as a tool error
  (`isError` true) whose entire body is one object: `{ code, message, docsUrl, retryable }`.

`retryable` means "retrying this same call, unchanged, might succeed" — it is false for anything you
must fix first. Every `VFX-E-…` error object carries a `docsUrl` of the form
`https://vouchfx-mcp.vouchfx.io/docs/errors/<code>.html`. A `VFX-D-…` diagnostic entry (returned as
data on `SuiteValidationError`/`RunSuiteInvalidPayload`, not as an error object) carries no `docsUrl`
field of its own yet — until the `Diagnostic` record's Sprint-2 adoption adds one, look up the same
catalogue page directly by code, using the URL pattern documented in the [Resources](#resources)
section below.

One code is omitted from the per-tool tables below because it is not specific to any of them:
**`VFX-E-1902`** (`retryable` false) means this server produced an outcome it could not render — a bug
in this server, not in your input. Any tool whose handler dispatches over multiple outcome variants
(an "outcome switch") may in principle return it; none is expected to. `search_docs` has no such
switch — see its own "no error shape at all" note below.

**Every successful result also carries a `meta` object**, alongside the per-tool fields documented
below and omitted from each "Result shape" line to avoid repeating it eleven times:

```jsonc
"meta": {
  "schemaVersion": "v1",        // the vouchfx language schema version this server validates against,
                                // read from the vendored composed schema's own version marker
  "serverVersion": "0.1.0",     // the same value as serverInfo.version in the MCP initialize handshake
  "workspaceRoot": "/path/..."  // PROVISIONAL — currently the server process's resolved base directory
}
```

It lets a host identify which schema version and server version produced a result without a separate
handshake call. `workspaceRoot` is provisional: it becomes a real workspace root when the workspace
model lands, without changing its name, shape, or position.

> **Privacy note on `workspaceRoot`.** This is a local filesystem path, and it is attached to every
> successful result. On a `dotnet tool` install it resolves under the invoking user's profile (e.g.
> `C:\Users\<username>\.dotnet\tools\...` or `~/.dotnet/tools/...`), so it commonly reveals the **OS
> username** and the local install layout. MCP hosts routinely forward tool output to a third-party
> model backend, so treat this value as leaving the machine. It is not covered by the engine's secret
> redaction — a path is not a `${secret:...}` reference — and it is accepted for now only because the
> field is provisional. Reducing what it discloses (reporting the workspace's own root, which the host
> already knows, rather than the tool's install location) is an explicit design input for the
> workspace model.

## Tools

### validate_suite

Validates an `.e2e.yaml` suite — either a file on disk or YAML text supplied inline — against the
engine's JSON Schema and reports every structural error found, without running the suite. Also
returns a structured digest of what the suite contains (step count, step types, service and
dependency names, capture variable names, and interpolation tokens used) computed from the single
parse, and a separate `semanticDiagnostics` channel for semantic findings (currently empty; the
VFX-D-12xx semantic rule set arrives next). Uses the **embedded vendored** composed schema
(drift-gated to `ENGINE_PIN` via `scripts/sync-vendored.ps1`, which is also the only supported way
to refresh it — see `vendored/README.md`). Offline-capable: does not require the CLI.

> **Relationship to `vouchfx validate`.** This tool evaluates the same schema the engine does, but
> it is a separate implementation rather than a wrapper, so the two are held to a specific and
> deliberately-limited contract: they aim to agree on **which** errors exist and **where**, and the
> CLI is authoritative for **wording**. Measured at the `v1.0.0-rc.4` pin over the engine's own
> 55-fixture rejected corpus: 33 byte-identical, 13 reporting the same findings at the same
> locations with less enriched text, **0 where the set of findings differs**, and 9 where the CLI
> short-circuits before schema validation and the two are not comparable. If a message here is
> terser than you expected, run `vouchfx validate` for the fuller explanation — the verdict will not
> change.
>
> That agreement is verified, not guaranteed by construction. Treat `vouchfx validate` as the
> authority if the two ever disagree, and please report it — a divergence is a bug in this tool.

- **Parameters**:
  - `path` (string, optional) — absolute or workspace-relative path to the suite file. Supply this
    OR `yaml`, never both.
  - `yaml` (string, optional) — the suite's YAML text, validated directly without reading or
    writing any file, for a draft not yet written to disk. Supply this OR `path`, never both.
  - `level` (string, optional, default `"full"`) — which passes to run: `"schema"` for the JSON
    Schema pass only, `"semantic"` for the semantic-rules pass only (currently empty), or `"full"`
    for both. Case-sensitive.

    > **`level: "semantic"` does not run the schema pass, so `valid` reflects only the passes that
    > ran — do not read it as "the engine will accept this suite".** `valid` reports exactly one
    > thing: that the `errors` array is empty. At `"semantic"` that array is empty because nothing
    > looked, not because the document conforms. The result echoes the effective `level` back
    > precisely so this is distinguishable; if you need the engine's verdict, ask for `"schema"` or
    > `"full"`.
- **Result shape**: `{ valid: bool, errors: [{ code, instancePath, message, line, column }],
  semanticDiagnostics: [], summary: { steps, stepTypes, services, dependencies, captures,
  placeholders }, level, meta }`. `valid` is `true` only when `errors` is empty — see the caveat
  under `level` above. `level` echoes the level the call actually ran at: the `level` argument you
  sent, or `"full"` when you sent none. The `summary` object contains:
  - `steps`: count of steps in the suite.
  - `stepTypes`: distinct `type` values those steps declare, in first-appearance order.
  - `services`: logical names under `environment.services`.
  - `dependencies`: logical names under `environment.dependencies`.
  - `captures`: distinct capture variable names any step's `capture` map declares.
  - `placeholders`: distinct `{name}` interpolation tokens used in string values, including the
    reserved-prefix forms `{svc::…}` and `{conn::…}` (never `${secret:…}` references).

  Every list in `summary` is capped at **1 000 entries and truncated silently** — there is no
  "truncated" flag. A summary is a digest for orientation, not an inventory: treat a list of exactly
  1 000 entries as possibly incomplete. No `summary` field ever carries a `${secret:…}` reference,
  whether it appeared as a value or as a name (a capture, service, dependency, or step type named
  after one is omitted from the list rather than echoed).

  The `errors` and `semanticDiagnostics` channels are permanently separate and never merged.
- **Input validation**:
  - Exactly one of `path` or `yaml` must be supplied; both or neither is an error `VFX-E-1152`.
  - A `path` of exactly `--yaml-stdin` is refused with the same error `VFX-E-1152`: that literal
    name collides with the internal marker the server uses to tell its isolated worker "the suite
    text is on stdin", so the file would never be opened. Pass it in a qualified form
    (`./--yaml-stdin`), or send its text as `yaml`.
  - An invalid `level` token (not one of the three case-sensitive values) is an error `VFX-E-1006`.
- **Diagnostic codes** in `errors[].code` — all of them `VFX-D-…`, all of them returned on a
  **successful** call (`isError` is false) because a finding about your suite is data, not a tool
  failure:

  | Code | Meaning |
  | --- | --- |
  | `VFX-D-1101` | A JSON Schema violation. |
  | `VFX-D-1102` | The YAML could not be parsed. |
  | `VFX-D-1103` | The file exceeds the size cap. |
  | `VFX-D-1104` | The YAML nests deeper than the cap allows. |
  | `VFX-D-1105` | More anchors/aliases than the cap allows ("billion laughs" defence). |
  | `VFX-D-1201` | A step's `type` matches no step type the engine defines. |

  `VFX-D-1103`/`1104`/`1105` are the YAML-bomb defences (size, nesting, and anchor/alias caps), applied
  before any recursive parse. **These defences run at every `level`** — a level that could switch
  them off would be a bypass with a friendly name.
- **Error codes** — returned as a **tool error** (`isError` true), carrying a single
  `{ code, message, docsUrl, retryable }` object instead of a validation result, because in each of
  these cases the suite's validity was never determined:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `path` is a UNC/network location, rejected before any filesystem call. | false |
  | `VFX-E-1002` | The suite file does not exist. | false |
  | `VFX-E-1003` | The suite file exists but could not be read. | false |
  | `VFX-E-1006` | An argument was rejected — invalid `level` token. Valid values: `schema`, `semantic`, `full`. | false |
  | `VFX-E-1150` | The isolated validation worker exceeded its wall-clock budget and was killed. | true |
  | `VFX-E-1152` | Exactly one of `path` or `yaml` must be supplied; both, neither, or a `path` of exactly `--yaml-stdin` is an error. | false |
  | `VFX-E-1901` | The validation worker could not be started, crashed, or produced unusable output. | true |

  Every code carries a `docsUrl` of the form `https://vouchfx-mcp.vouchfx.io/docs/errors/<code>.html`.
- **Notable behaviour — process-isolated worker, file or inline.** The actual YAML/schema evaluation
  runs inside a disposable child process (the same `vouchfx-mcp` executable, re-invoked in a hidden
  worker mode), bounded by a 10-second wall-clock timeout. Inline YAML is transported over stdin
  (UTF-8, never written to disk) and crosses the same process-isolation boundary as file content —
  the same timeout, the same whole-tree kill on timeout. A tiny, well-formed-looking YAML input can
  drive a YAML scanner into an uninterruptible, ~100%-CPU spin that no in-process `CancellationToken`
  can recover from — only OS-level process termination can — which is exactly why this tool never
  parses untrusted YAML directly inside the long-lived server process, whether from a file or
  inline. Worker stdout/stderr are each capped at 50 MB before being treated as misbehaving. A
  worker that does not finish in time is killed (its exit is confirmed, not assumed) and reported
  as `VFX-E-1150`, never left running. The read-only guarantee holds for both paths: no suite file
  is ever written, modified, or deleted; inline YAML is never persisted.
- **The `semanticDiagnostics` channel — currently empty; in place for future use.** In this release
  the semantic rules are not yet implemented, so `semanticDiagnostics` is present and empty at every
  `level`. Once semantic rules ship, they will populate this channel with findings that are not
  schema violations — e.g. unused capture variables, undefined placeholder tokens. The `errors` and
  `semanticDiagnostics` channels will remain forever separate: moving a finding from one channel to
  another is a breaking change; the names will not change.
- Never throws. A suite that is merely invalid — including malformed YAML, schema violations, or
  semantic findings — is a successful call carrying diagnostics; a missing file, a rejected path,
  an input-validation failure, or a worker timeout is a structured tool error carrying a single
  `VFX-E-…` object. Both are structured results; neither is an exception.

### list_step_types

Lists every step type the **pinned engine** supports, in dotted `<family>.<provider>` form, grouped by
family — loaded from the live CLI export `vouchfx list --json`, not from a hand-maintained or
vendored-only catalogue (REQ-010).

- **Parameters**: none.
- **Result shape**: `{ families: [{ family, familyIntent, types: [{ type, provider, description,
  captureSupported, familyIntent }] }] }`, families ordered alphabetically, types ordered
  alphabetically within each family.
- **Requires** the `vouchfx` CLI on `PATH` at `ENGINE_PIN`, with Spec A rich catalogue fields
  (`requiredFields`, `optionalFields`, `captureSupported`, `familyIntent` on every entry). A missing
  CLI, pin mismatch, or thin pre-Spec-A list is a **tool error** (fail-fast; EDGE-004) — never a
  silent list of type keys without field metadata.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, unparseable, not launchable, or its catalogue is too thin. | false |

  This tool takes no arguments, so it has no argument-error code.
- Call `describe_step_type` for the full required/optional field contract of any one type this returns.

### describe_step_type

Describes one step type's full contract from the same live engine catalogue export as
`list_step_types`: required and optional field **names**, capture support, and family intent.

- **Parameters**: `type` (string, required) — the dotted `<family>.<provider>` type name exactly as
  `list_step_types` reports it, e.g. `db-assert.postgres`.
- **Result shape**: `{ type, family, provider, description, fields: [{ name, type, description,
  required }], requiredOneOf, requiredFields, optionalFields, captureSupported, familyIntent }`.
  `fields` is derived from `requiredFields` / `optionalFields` (type/description may be null for
  live-export entries). Excludes the common step envelope fields every step type shares (`id`,
  `type`, `description`, `capture`, `verifyMode`, `timeout`, `continueOnFailure`).
- **Requires** the same pinned Spec A CLI as `list_step_types`. Thin catalogues fail fast (EDGE-004).
- **Unknown type**: returns an MCP tool error listing every valid type, rather than crashing.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1250` | The requested `type` is not in the live engine catalogue. The message lists every valid type. | false |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, unparseable, not launchable, or its catalogue is too thin. | false |

  Note `VFX-E-1250` rather than the `VFX-D-1201` a *suite* referencing an unknown type produces:
  asking about a type that does not exist is a call that cannot be performed, not a finding about a
  suite.

### search_docs

Free-text search over the two vendored engine documents (the generated language reference and the
recipes library) for a query, returning the most relevant sections.

- **Parameters**: `query` (string, required) — free text, e.g. *"how does verifyMode RETRY work"*.
- **Result shape**: `{ query, matches: [{ source, headingPath, snippet, url }] }`. `matches` is ordered
  most relevant first and capped at a fixed maximum; `snippet` is the section's body text, truncated
  with an ellipsis when long. For a long section where **some** searched term occurs only beyond that
  truncation point, the snippet is a window around that term's first occurrence instead of the
  section's opening — marked with a leading ellipsis — so the snippet always shows at least one of
  the terms that matched, rather than text mentioning none of them. With a multi-word query the
  window may therefore omit terms that *were* visible in the opening. `url` is a deep link to the
  matching section on
  [vouchfx.io](https://vouchfx.io) (the document's published page, plus a `#`-anchor for the section).
- **Notable behaviour.** Scoring is presence-based (which sections contain the query's terms), not raw
  occurrence-count based — a section that mentions every term once outranks one that repeats a single
  term many times. Never throws for a search outcome: a query with no matches returns an **empty**
  `matches` list, never an error; only an actual request cancellation is surfaced as cancellation.
- **Error codes**: none. This is the only tool with no error shape at all — by design, not by
  omission. Every query, including one with no matches or an over-long one, is a successful call.

### plan_coverage

Runs the engine's deterministic, **read-only** coverage-and-gap analysis over a declared `.e2e.yaml`
suite set (a directory searched recursively, or a single suite file), an optional JSON Lines event
history, and the pinned engine's live step catalogue. Invokes the pinned CLI `vouchfx plan --json` so
CLI and MCP cannot drift (Spec D / M3 Planner). A call that finds gaps is a **successful** result —
gaps are the data this tool exists to surface, never an error condition.

**Planner path:** `plan_coverage` → pick a gap finding → `scaffold_suite` (hints feed it unchanged) →
fill semantics → `validate_suite` → `run_suite`.

- **Parameters**:
  - `path` (string, required) — directory to search recursively for `*.e2e.yaml` suites, or a single
    suite file — the declared universe to analyse.
  - `eventsPath` (string, optional) — path to a JSON Lines event history file, or a directory of
    `*.jsonl` files. Omit for no history: every declared suite/step is reported never-run (a valid,
    successful analysis).
  - `staleDays`, `flakyMinRuns`, `fragileMinEnvErrors`, `inconclusiveMin` (integer, optional) — override
    the engine's history-health thresholds (defaults `30` / `2` / `2` / `2`).
- **Result shape**: `{ schemaVersion, engineVersion, thresholds, inventory: { suites, services,
  dependencies, stepTypes, runCount, firstEventTs, lastEventTs, skippedEventLines,
  unmatchedObservations, unanalysableSuites, unmappableDependencies }, findings: [{ kind, suite,
  stepId, target, targetKind, suggestedTypes, suggestedStepId, ambiguous, ambiguityReason, history,
  detail, relatedSuites }] }` — the schema-versioned report document relayed verbatim from the pinned
  engine.
- **Finding kinds**: `suite-never-run`, `step-never-exercised`, `dependency-not-asserted`,
  `dependency-missing-step-type`, `service-missing-http-step` (coverage/vocabulary **gaps** — every one
  carries a `suggestedTypes`/`suggestedStepId` hand-off hint feeding `scaffold_suite` unchanged, except
  `suite-never-run` and an unmappable-dependency gap, which name no single step a scaffold call could
  act on); `step-stale`, `step-flaky`, `step-fragile`, `step-inconclusive-prone` (history-health, never
  gaps); `suite-identity-ambiguous` (a scenario-id collision or a since-renamed file, never a gap).
- **Requires** the `vouchfx` CLI on `PATH` at `ENGINE_PIN` with the M3 Planner (`vouchfx plan`). A
  missing/mismatched CLI, an invalid suite path, or an out-of-range threshold is an MCP **tool error**
  — never a hang.
- **Error codes** — note that **finding gaps is never one of them**; gaps are the data this tool
  exists to return:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | An argument was rejected — a bad or missing suite path, an empty suite folder, or an out-of-range threshold. | false |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, not launchable, or lacks the M3 Planner. | false |
  | `VFX-E-1603` | The Planner ran but produced no analysis — it failed, timed out, overran its output cap, or returned unreadable output. | false |

  `VFX-E-1603` covers several causes, two of which (a timeout, an output-cap overrun) would be
  transient in isolation. It is nonetheless `retryable: false`, because its other causes are not, and
  in both transient cases the message tells you the genuinely useful thing — narrow `path` or
  `eventsPath` and retry, which is a *different* call.
- **Not** a free-text parameter surface: no `prompt` / `goal` / natural-language field. Structured only.
- Never writes, modifies, or deletes a suite file; never calls a model; never invokes git (REQ-013).

### scaffold_suite

Generates a machine-drafted, catalogue-grounded, **schema-valid** `.e2e.yaml` suite skeleton from
**structured arguments only** — never free text. Invokes the pinned engine CLI
`vouchfx scaffold --intent <temp-file>` so CLI and MCP cannot drift (Spec B / REQ-007). Free-text
goals belong in the host LLM only; the host chooses step types via `list_step_types` first.

**Generator path (REQ-008):** free-text goal (host LLM) → choose types/ids → `scaffold_suite` → fill
semantics → `validate_suite` → `run_suite`. This server does not host an LLM (REQ-010).

- **Parameters**:
  - `steps` (array, required) — ordered list of `{ id, type, label? }`. `type` is a dotted
    `<family>.<provider>` key from the live catalogue (e.g. `http.rest`, `db-assert.postgres`).
  - `services` (array, optional) — `{ name, image? }` for `environment.services`.
  - `dependencies` (array, optional) — `{ name, type }` for `environment.dependencies` (e.g.
    `type: postgres`).
- **Result shape** (success): `{ yaml: string }` — full document text, including a provenance comment
  block (machine-drafted / human review required). Credential-shaped fields use `${secret:…}`
  references only.
- **Requires** the `vouchfx` CLI on `PATH` at `ENGINE_PIN` with Spec B scaffold. Pin handshake matches
  `run_suite` / catalogue tools. A missing/mismatched CLI, unknown step type, empty steps, or other
  scaffold validation failure is an MCP **tool error** (message names the problem, e.g. `nope.fake`)
  — never a hang.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | An argument was rejected before anything was spawned — e.g. `steps` was empty. | false |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, not launchable, or lacks Spec B scaffold support. | false |
  | `VFX-E-1301` | The scaffold produced no suite — the engine rejected the intent (an unknown step type or dependency kind), or the CLI timed out, overran its output cap, or produced nothing. | false |

  As with `VFX-E-1603` above, `VFX-E-1301` unions several causes and stays `retryable: false`: its
  dominant cause is an intent the engine rejects, which an identical retry cannot fix.
- **Not** a free-text parameter surface: no `prompt` / `goal` / natural-language field. Structured
  only.
- Scaffold alone is not guaranteed run-green without further fill; it is guaranteed schema-valid
  placeholders for registered Core types.

### run_suite

Runs an `.e2e.yaml` suite through the packaged `vouchfx` CLI and reports its verdict once the run
completes.

- **Parameters**: `path` (string, required); `tags` (string array, optional) — restrict the run to
  steps/scenarios matching one or more tags; `timeoutSeconds` (integer, optional, 1–3600, default
  `300`) — abort the run if it has not completed in time.
- **Result shape** (on `Completed`): `{ verdict, exitCode, cancelled, timedOut, remediationHint, steps:
  [{ stepId, verdict, durationMs, attemptCount, observation }], eventsFilePath, eventsTruncated }`.
- **The four taxonomy verdicts, never conflated**: `Pass`, `Fail`, `EnvironmentError`, `Inconclusive`.
  A cancelled or timed-out run is always reported as `Inconclusive`, distinguished via `cancelled` vs.
  `timedOut` — never as `Fail`. `remediationHint` is populated whenever `verdict` is
  `EnvironmentError` (e.g. naming the Docker daemon when that looks like the cause) and is `null`
  otherwise.
- **Gate ordering, cheapest first — nothing is spawned unless every earlier gate passes**: argument
  safety (a `path`/tag beginning with `-` is rejected outright, since it would otherwise be misread as a
  CLI option — tool error `VFX-E-1006`) → the same pre-flight validation `validate_suite` performs (an
  invalid suite is returned as a `{ code: "VFX-D-1100", validation }` payload with `isError` **false**,
  since an invalid suite is data, not a tool failure — the CLI is never spawned; a missing or unreadable
  file, by contrast, is the same `VFX-E-100…` tool error `validate_suite` returns for it) → the CLI
  presence + version handshake against `ENGINE_PIN` (a missing/mismatched CLI returns tool error
  `VFX-E-1401` explaining exactly why, without spawning anything) → single-flight concurrency (only one
  `run_suite` call may be active on this server at a time; a concurrent call is rejected immediately
  with the retryable tool error `VFX-E-1501`, never queued) → the run itself.
- **Error codes** — note that a failing *suite* is not among them: a run that fails is a
  **successful** call reporting `verdict: "Fail"`.

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `path` is a UNC/network location, rejected before any filesystem call. | false |
  | `VFX-E-1002` | The suite file does not exist. | false |
  | `VFX-E-1003` | The suite file exists but could not be read. | false |
  | `VFX-E-1006` | An argument was rejected — a `path`/tag beginning with `-`, or an out-of-range `timeoutSeconds`. | false |
  | `VFX-E-1150` | The pre-flight validation worker exceeded its wall-clock budget and was killed. | true |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, or not launchable. | false |
  | `VFX-E-1501` | Another `run_suite` call is already active on this server. | true |
  | `VFX-E-1901` | The pre-flight validation worker could not be started, crashed, or produced unusable output. | true |

  The five path/validation codes are shared with `validate_suite` by design: both tools run the same
  pre-flight check through the same classifier, so they can never give different answers about one
  file. A suite that is merely **invalid** is the case that is *not* an error here — see the gate
  ordering above.
- **Serialisation & events.** Every attempted run writes its own JSON Lines event stream to a temp file
  (path returned as `eventsFilePath`); reading that file is bounded at 50 MB, with `eventsTruncated:
  true` when the file exceeded that and had to be read only up to the cap. `explain_run` is designed to
  read this same file afterwards.
- **Progress.** Reports best-effort progress as the run proceeds (start, each relayed CLI output line,
  a closing summary) when the calling client requests MCP progress notifications.

### explain_run

Diagnoses a completed suite run in plain language, purely by reading and parsing its JSON Lines event
stream. Never re-runs anything — no CLI spawn, no validation worker, no container.

- **Parameters**: `eventsPath` (string, optional) — path to the run's events file; when omitted, the
  most recent `run_suite` call **this session** is used automatically.
- **Result shape**: `{ verdict, categoryMeaning, summary, totalStepCount, passedStepCount, notableSteps:
  [{ stepId, verdict, durationMs, attemptCount, observation, attempts: [{ attempt, tMs, outcome,
  observation }], omittedAttemptCount }], omittedNotableStepCount, environmentErrors: [{ errorKind,
  resourceName, detail }], omittedEnvironmentErrorCount, eventsFilePath, eventsTruncated,
  responseTruncated }`.
- `categoryMeaning` always accompanies `verdict` — a short, fixed explanation of what that CATEGORY
  means (e.g. that `EnvironmentError` is an infrastructure problem and explicitly **not** a test
  defect), so an agent never has to infer the taxonomy's meaning itself.
- `notableSteps` names every step whose own verdict is not `Pass` — a passing step is never "notable" —
  together with its full RETRY attempt timeline (`attempts`) and observation/diff evidence.
- **The 32 KB diagnosis budget.** The diagnosis payload is trimmed to fit 32 KB of serialised JSON,
  enforced through three fixed, deterministic detail tiers (rich → compact → minimal), each actually
  measured by serialising it rather than assumed to fit. `responseTruncated: true` marks that evidence
  was trimmed to fit; the full detail always still exists in the events file itself, whose path
  (`eventsFilePath`) is included regardless. `eventsTruncated: true` instead marks that the *source*
  events file itself exceeded the 50 MB read cap before any trimming even began.
- **The full MCP response is larger than that budget, and can exceed 64 KB.** The 32 KB figure bounds
  the diagnosis payload, not the wire envelope. Every result is carried twice — once as
  `structuredContent` and once as a text content block — and the text copy is a JSON-escaped *string*,
  so it costs more than the structured copy. Measured against the largest diagnosis the tiers accept:
  a 32,229-byte payload produces a **71,335-byte** response, a **2.213×** multiplier rather than the
  2× the halved budget assumes. The shared `meta` object contributes **384 bytes** of that — 6.6% —
  so it is not the cause; the escaping is. Budget for a worst-case `explain_run` or `diagnose_run`
  response of roughly 70 KB today. Reducing it (via an on-demand resource hand-off for large evidence,
  rather than a raised cap) is planned work, not a current guarantee.
- **No run to explain**: if `eventsPath` is omitted and no `run_suite` call has completed yet this
  session, returns an MCP tool error saying so, rather than fabricating a diagnosis.
- **Path safety**: a UNC/network `eventsPath` is rejected before any filesystem call is made against
  it, for the same forced-authentication reason `validate_suite`/`run_suite` reject one for their own
  `path` argument.
- **Error codes** — identical to `diagnose_run`'s, since both tools read the same events file through
  the same guards and fail for the same five reasons. Note that a run whose verdict is `Fail` or
  `EnvironmentError` is a **successful** call: these codes are only about being unable to produce a
  diagnosis at all.

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `eventsPath` is a UNC/network location, rejected before any filesystem call. | false |
  | `VFX-E-1004` | The events file does not exist. | false |
  | `VFX-E-1005` | The events file exists but could not be read. | false |
  | `VFX-E-1601` | `eventsPath` was omitted and no run has completed in this session. | false |
  | `VFX-E-1602` | The events file was read but contained no recognisable vouchfx event. | false |

### diagnose_run

Healer (M2 / Spec C): the same taxonomy-faithful diagnosis as `explain_run`, plus **Fail-only**
review patch proposals grounded in the event stream. Deterministic templates only — no LLM inside
this server, no auto-apply, no writes to the customer's suite file, no engine `healer-suggestion`
events.

**Workflow:** `run_suite` → events file → `explain_run` / `diagnose_run` → human (or host under
human review) applies any accepted patch → `validate_suite` → `run_suite` again. Free text belongs
only in the host conversation, not as a tool parameter.

- **Parameters**: `eventsPath` (string, optional) — path to the run's events file; when omitted, the
  most recent `run_suite` call **this session** is used. Suite path is **not required** for v1;
  proposals are evidence-based from observations when suite YAML is absent.
- **Result shape**: `{ diagnosis: { …same fields as explain_run… }, proposals: [{ stepId, rationale,
  patch }], environmentGuidance: [string] }`.
- **`proposals`**: non-empty only for step-level **Fail** with non-empty observation/diff evidence.
  Each proposal has `stepId`, a short `rationale` grounded in that evidence, and a `patch`
  (unified-diff style review comment / YAML fragment placeholders). Empty for **Pass**, pure
  **EnvironmentError**, and **Inconclusive** (no suite-rewrite patches).
- **`environmentGuidance`**: infrastructure checklist when environment-error evidence is present
  (image pull, health, provision, Docker). **Never** accompanied by YAML rewrite patches for those
  failures. Inconclusive may include non-patch guidance only.
- **Never auto-apply**: proposals are returned in the tool result only — the tool is read-only and
  does not invoke git or write suite files.
- **Same path/error behaviour as `explain_run`**: last-run default, UNC rejection, missing/unreadable
  file, no recognisable events — structured tool errors, no hang. Response size aligned with
  `explain_run`'s 32 KB diagnosis budget — and with the same caveat about the larger wire envelope
  documented there; full detail remains in the events file path inside `diagnosis`.
- **Error codes**: exactly the five in `explain_run`'s table above — `VFX-E-1001`, `VFX-E-1004`,
  `VFX-E-1005`, `VFX-E-1601`, `VFX-E-1602`, all `retryable: false`. Deliberately the same codes, not
  merely similar ones: a host that has learned `explain_run`'s error handling already knows
  `diagnose_run`'s.

### explain_diagnostic

Looks up one catalogued diagnostic/error code and returns its plain-language explanation — the same
content served by the [`errors` resource family](#errors) below, addressable directly from a `code`
seen on any `VfxError`/`Diagnostic` this server returns. Never spawns the engine CLI; works fully
offline.

- **Parameters**: `code` (string, required) — a `VFX-D-####`/`VFX-E-####` code exactly as seen in a
  result's `code` field, e.g. `VFX-E-1002`.
- **Result shape**: `{ code, title, explanation, commonCauses: string[], fixes: string[], docsUrl }`.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1903` | The `code` argument does not name a catalogued code. | false |

- Never throws for a bad `code` — an unrecognised value is a structured tool error, not a crash, and
  the server keeps advertising every tool afterwards.

### get_schema

Returns the composed JSON Schema — the whole document, one major section (`metadata`, `environment`,
`variables`, `steps`), or one step type's own definition — formatted as a JSON Schema document or as a
markdown digest built from the schema's field descriptions only. Works offline from the embedded
composed schema this server vendors at its pinned engine commit; when a matching `vouchfx` CLI is
installed, cross-verifies the embedded schema against that engine's own `vouchfx schema` export.

- **Parameters**:
  - `section` (string, optional, default `"full"`) — which part of the schema to return: `"full"`,
    `"metadata"`, `"environment"`, `"variables"`, `"steps"`, or `"step:<family>.<provider>"` for a
    single step type's definition (e.g. `"step:http.rest"`). Case-sensitive. Unknown sections (e.g.
    a `step:` for a family/provider not in the schema) are rejected with a tool error.
    **Mind the cost of the default**: the `full` document is ~105 KB of JSON, ~220 KB on the wire
    (the payload is carried twice, as structured content and as text), so prefer a specific section —
    or `format: "summary"` — unless you genuinely need the whole contract in one call. The same
    advisory rides the tool's own description and the `VFX-E-1151` catalogue page.
  - `format` (string, optional, default `"json-schema"`) — the output format: `"json-schema"` for
    the schema subtree itself, or `"summary"` for a markdown digest of the section's field
    descriptions, capped at 8&#160;KB. Case-sensitive.
- **Result shape**: `{ schemaVersion, section, jsonSchema?, summary?, diagnostics? }` — `jsonSchema`
  and `summary` are mutually exclusive, determined by the `format` parameter. `diagnostics` appears
  only when the optional live cross-verification detects a divergence.
- **Dependency class**: **CLI-optional** — a third posture alongside the existing CLI-free and
  pinned-CLI-backed classes. Offline mode (no pinned CLI installed, or probe fails): serves the
  embedded composed schema and succeeds. Live mode (pinned CLI present and version matches
  `ENGINE_PIN`): cross-verifies the vendored schema against `vouchfx schema` output; a divergence is
  surfaced as a diagnostic, never as a tool failure.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | The `format` argument is invalid. Valid values are: `json-schema`, `summary`. | false |
  | `VFX-E-1151` | The `section` argument is not a valid schema section. Unknown step types (e.g. `step:fake.provider`) are rejected here. | false |

- **Diagnostic codes**:

  | Code | Meaning |
  | --- | --- |
  | `VFX-D-1106` | The pinned CLI's live `vouchfx schema` export disagrees with the embedded vendored schema. The embedded (validated, byte-pinned) schema is still returned. |

- **Notable behaviour — summary size budget.** When `format: "summary"` is requested, the markdown
  digest is generated only from the schema's own `description` field annotations and is capped at 8 KB
  of rendered Markdown. Fields without descriptions are omitted, never placeholder-filled. At the
  currently pinned engine every section fits with room to spare — measured across all of them, the
  largest digest is 2,141 bytes, for `step:mq-publish.kafka` (about a quarter of the budget), and the
  `full` section's is 773 bytes — so truncation is not something you will see today; the cap is a
  postcondition of the renderer (guard-tested against synthetic oversized input) so that a future pin
  bringing a much larger section still cannot overrun it. Whether or not truncation occurs, the result
  payload always fits within the budget.
- **Notable behaviour — live cross-verification.** The optional probe to `vouchfx schema` runs
  regardless of which `section` or `format` is requested, because it is a statement about the
  document this server is serving from (the vendored schema), not about the particular fragment the
  caller addressed. A host that only ever asks for summaries deserves to hear about drift just as
  much as one asking for full schemas. The verification fails silently (no CLI, a version-mismatched
  CLI, or a probe timeout) and reports nothing — absent a CLI is not a finding, and absent a CLI-dependent
  result is not a failure of this server's contract (the schema is still returned).
- Never throws; read-only in the strongest sense: no suite file is touched, the optional CLI probe
  never writes, and nothing outside this server's embedded manifest resources is read for content.

## Resources

Two static (non-templated) MCP resources, each the vendored document's full, verbatim Markdown text,
served with MIME type `text/markdown` — plus one templated resource family covering every catalogued
diagnostic/error code.

### Language reference

- **URI**: `vouchfx-docs:///language-reference`
- **Name**: vouchfx Language Reference
- The generated `.e2e.yaml` language reference: every common step field (`id`, `type`, `verifyMode`,
  `timeout`, …) and every registered step type's required/optional fields. Byte-identical to the pinned
  engine's `docs/language-reference.md`.

### Recipes

- **URI**: `vouchfx-docs:///recipes`
- **Name**: vouchfx Recipes: Common Patterns and Examples
- Task-oriented recipes for common integration-testing scenarios — seeding fixtures, test doubles,
  secrets, engine-owned `verifyMode: RETRY` polling, message-queue verification, CI integration, and
  more — each a runnable `.e2e.yaml` example with explanation. Byte-identical to the pinned engine's
  `docs/recipes.md`.

Both documents are also what `search_docs` searches; reach the same content either as a resource your
client reads directly, or as search results with deep links back to
[vouchfx.io](https://vouchfx.io).

### errors

- **URI template**: `vouchfx-docs:///errors/{code}` (a TEMPLATED resource — advertised via
  `resources/templates/list`, not `resources/list`; the two static resources above are unaffected by
  this one existing alongside them).
- One page per catalogued `VFX-D-####`/`VFX-E-####` code — title, explanation, common causes, and
  fixes, in Markdown — served from the exact same embedded bytes `explain_diagnostic` parses (single
  source of truth: one file, two access paths). Read `vouchfx-docs:///errors/VFX-E-1002`, for example,
  for that code's page directly.
- An unrecognised code returns an MCP protocol-level error, not a crash; the server keeps advertising
  every tool and resource afterwards.
