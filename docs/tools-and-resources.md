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
below and omitted from each "Result shape" line to avoid repeating it eighteen times:

```jsonc
"meta": {
  "schemaVersion": "v1",        // the vouchfx language schema version this server validates against,
                                // read from the vendored composed schema's own version marker
  "serverVersion": "0.1.0",     // the same value as serverInfo.version in the MCP initialize handshake
  "workspaceRoot": "/path/..."  // the configured workspace root, or the installed tool's base directory
}
```

It lets a host identify which schema version and server version produced a result without a separate
handshake call. `workspaceRoot` reports the workspace root when a `--workspace` flag was supplied at
server startup (see [Install & registration](install.md#optional-workspace-containment)), or the
resolved base directory of the installed tool when no workspace is configured.

> **Privacy note on `workspaceRoot`.** This is a local filesystem path, and it is attached to every
> successful result. When no workspace is configured, it resolves under the invoking user's profile
> (e.g. `C:\Users\<username>\.dotnet\tools\...` or `~/.dotnet/tools/...`), so it commonly reveals the
> **OS username** and the local install layout. When a workspace is configured, it reports the workspace
> root you supplied. MCP hosts routinely forward tool output to a third-party model backend, so treat
> this value as leaving the machine. It is not covered by the engine's secret redaction — a path is not
> a `${secret:...}` reference.

## Tools

### validate_suite

Validates an `.e2e.yaml` suite — either a file on disk or YAML text supplied inline — against the
engine's JSON Schema and reports every structural error found, without running the suite. Also
returns a structured digest of what the suite contains (step count, step types, service and
dependency names, capture variable names, and interpolation tokens used) computed from the single
parse, and a separate `semanticDiagnostics` channel carrying the VFX-D-12xx semantic findings
(unknown step types, dangling references, secret literals, and the rest — see the semantic-codes
table below). Uses the **embedded vendored** composed schema
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
  - `path` (string, optional) — absolute or workspace-relative path to the suite file. A relative
    path resolves against the workspace root when the server was started with `--workspace`, and
    against the server process's current directory otherwise. Supply this OR `yaml`, never both.
  - `yaml` (string, optional) — the suite's YAML text, validated directly without reading or
    writing any file, for a draft not yet written to disk. Supply this OR `path`, never both.
  - `level` (string, optional, default `"full"`) — which passes to run: `"schema"` for the JSON
    Schema pass only, `"semantic"` for the semantic-rules pass only, or `"full"`
    for both. Case-sensitive.

    > **`level: "semantic"` does not run the schema pass, so `valid` reflects only the passes that
    > ran — do not read it as "the engine will accept this suite".** `valid` reports exactly one
    > thing: that the `errors` array is empty. At `"semantic"` that array is empty because nothing
    > looked, not because the document conforms. The result echoes the effective `level` back
    > precisely so this is distinguishable; if you need the engine's verdict, ask for `"schema"` or
    > `"full"`.
- **Result shape**: `{ valid: bool, errors: [{ code, instancePath, message, line, column }],
  semanticDiagnostics: [{ code, severity, message, location, path, fix, docsUrl }],
  semanticDiagnosticsTruncated: bool, summary: { steps, stepTypes, services, dependencies, captures,
  placeholders, truncated } | null, level, meta }`. `valid` is `true` when `errors` is empty and no
  semantic finding has severity `error` — see the caveat under `level` above.
  `semanticDiagnosticsTruncated` is `true` when the semantic pass produced more than 1 000 findings
  and the array therefore does not carry all of them. `level` echoes the level the call actually ran at: the `level`
  argument you sent, or `"full"` when you sent none. `summary` is **`null` whenever no document was
  built** — see below. The `summary` object contains:
  - `steps`: count of steps in the suite.
  - `stepTypes`: distinct `type` values those steps declare, in first-appearance order.
  - `services`: logical names under `environment.services`.
  - `dependencies`: logical names under `environment.dependencies`.
  - `captures`: distinct capture variable names any step's `capture` map declares.
  - `placeholders`: distinct `{name}` interpolation tokens used in string values, including the
    reserved-prefix forms `{svc::…}` and `{conn::…}` (never `${secret:…}` references).
  - `truncated`: `true` when this digest is no longer a complete, exact representation of the
    document — so **do not treat these lists as an exact inventory**. Two alterations raise it: a
    list that hit the **1 000-entry** cap and dropped a name it would otherwise have carried (a list
    cut), or a single entry longer than **128 characters** that was clipped to at most that length (a
    length clip — the trailing `…` marks it; a clipped entry is 128 characters, or 127 in the rare
    case where clipping one character shorter avoids splitting a surrogate pair). Real names are short
    — a step type is ~15 characters, a
    capture/service/dependency name ~20–30 — so the length clip only ever bites a pathological
    entry (e.g. an alias-amplified multi-MB scalar), and clipping keeps the entry's presence visible
    on the wire rather than echoing the whole string.

  Every list in `summary` is capped at **1 000 entries** and every entry at **128 characters**, and
  `truncated` tells you when either cap actually bit, so you never have to infer incompleteness from
  a list length or an entry length. A summary is a digest for orientation, not an inventory: a suite
  with more than a thousand distinct step types, service names, capture variables, or placeholder
  tokens is past the point where reading a flat list helps.
  `truncated` is **not** raised by the secret-hygiene filter: no `summary` field ever carries a
  `${secret:…}` reference, whether it appeared as a value or as a name (a capture, service,
  dependency, or step type named after one is omitted from the list rather than echoed), and that
  omission is a permanent property of every summary rather than a sign of truncation.

  **`summary` is `null` when the document could not be parsed** — a successful call, with the reason
  in `errors`. That is every case where no document was ever built: `VFX-D-1102` (unparseable YAML),
  `VFX-D-1103` (over the size cap), `VFX-D-1104` (nested too deep), `VFX-D-1105` (too many
  anchors/aliases), and `VFX-D-1107` (a line exceeds the length cap), plus the file-level errors below for a `path` that could not be read. Unparseable
  YAML is the likeliest outcome of validating a draft mid-edit, so handle the null rather than
  assuming a summary accompanies every non-error result. **`valid: false` is not a proxy for it**: a
  schema violation is reported on a document that parsed perfectly well and therefore comes *with* a
  summary.

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
  | `VFX-D-1107` | A single line exceeds the length cap. |
  | `VFX-D-1201` | A step's `type` matches no step type the engine defines. |

  `VFX-D-1201` appears in **both** channels, from one detector — see the note under **Semantic
  codes** below.

  `VFX-D-1103`/`1104`/`1105`/`1107` are the YAML-bomb defences (size, nesting, anchor/alias, and per-line-length caps), applied
  before any recursive parse. **These defences run at every `level`** — a level that could switch
  them off would be a bypass with a friendly name.

- **Semantic codes** in `semanticDiagnostics[].code` — findings that require the step-type vocabulary,
  not just the JSON Schema's shape. All are `VFX-D-…` diagnostics (returned on a successful call). Severity
  controls whether a finding flips `valid` to false: only a `VFX-D-1207` finding of severity `error` makes
  `valid: false`; every warning and info finding leaves it true. Note: `VFX-D-1201` emits to *both* `errors`
  (with engine-parity wording) and `semanticDiagnostics` (with a Levenshtein closest-match suggestion) from
  the same detector — the channels never merge.

  **These codes only ever appear at `level` `"semantic"` or `"full"`.** At `"schema"` the rules pass
  does not run at all, so `semanticDiagnostics` is empty for the trivial reason that nothing looked —
  an absent finding there is not evidence the suite is free of it. Read `level` back off the result
  before drawing a conclusion from an empty array, exactly as you must before reading `valid`.

  **Severity is a property of the finding, not of the code**, so read it off each entry rather than
  inferring it from the table below: `VFX-D-1207` reports at `error` for its three structural shapes
  (a private-key PEM header, an `AKIA`/`ASIA` key id, an inline `Password=`) and at `warning` for its
  high-entropy-token inference, which never changes `valid`. The table's Severity column gives the
  level each code reports at today; where a code has more than one, both are listed.

  | Code | What it flags | Severity | Notes |
  | --- | --- | --- | --- |
  | [`VFX-D-1201`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1201.html) | Unknown step type | warning | Dual-channel enrichment: schema entry + suggestion. |
  | [`VFX-D-1202`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1202.html) | Dangling `target` reference | warning | |
  | [`VFX-D-1203`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1203.html) | Placeholder used before definition | warning | Order-aware; `{svc::…}`/`{conn::…}` tokens count as always-defined. |
  | [`VFX-D-1204`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1204.html) | Unused capture | warning | |
  | [`VFX-D-1205`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1205.html) | Undeclared dependency type | warning | Suppressed when step's `target` names a declared service. |
  | [`VFX-D-1206`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1206.html) | RETRY without timeout or timeout above 300 s | warning | Advisory max is server-owned, not an engine limit. |
  | [`VFX-D-1207`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1207.html) | Literal secret material detected | **error** (structural shapes) / warning (entropy inference) | Only the `error` form makes `valid: false`. Correct practice is `${secret:…}` reference. |
  | [`VFX-D-1208`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1208.html) | Duplicate step id | warning | Reported at each repeat occurrence. |
  | [`VFX-D-1209`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1209.html) | Async step without RETRY | warning | Carries machine-applicable `fix` (sets `verifyMode: RETRY`). |
  | [`VFX-D-1210`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1210.html) | Topology cross-check | warning | **Disabled** — catalogued and tested but never emitted; awaits upstream capability. |
  | [`VFX-D-1211`](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-D-1211.html) | Missing `metadata.owner`/`tags` | info | |

- **Error codes** — returned as a **tool error** (`isError` true), carrying a single
  `{ code, message, docsUrl, retryable }` object instead of a validation result, because in each of
  these cases the suite's validity was never determined:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `path` is a UNC/network location, or (when a workspace is configured) resolves outside its root. | false |
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
- **The `semanticDiagnostics` channel — now populated with ten semantic rules (eleven codes, one
  reserved).** This channel carries findings that require the step-type vocabulary, not just the JSON
  Schema's shape: unknown step types, unused captures, undefined placeholders, dangling references,
  missing metadata, and more — but only when `level` is `"semantic"` or `"full"`. The `errors` and
  `semanticDiagnostics` channels remain forever separate: moving a finding from one channel to another
  would be a breaking change; the names will not change. See the **Semantic codes** section above for
  the full rule set and catalogue links.

  The channel is **capped at 1 000 findings**, and `semanticDiagnosticsTruncated` is `true` when that
  cap dropped one — the same cap-plus-flag shape `summary.truncated` uses, so you never have to infer
  incompleteness from a list length. A finding is per-node for some rules, so nothing about a *valid*
  document bounds how many it can produce.
- Never throws. A suite that is merely invalid — including malformed YAML, schema violations, or
  semantic findings — is a successful call carrying diagnostics; a missing file, a rejected path,
  an input-validation failure, or a worker timeout is a structured tool error carrying a single
  `VFX-E-…` object. Both are structured results; neither is an exception.

### normalize_suite

Returns a suite's canonical text and its full validation result to the HOST. The server never writes the file — the canonical text comes back to you as a string, and your file system is untouched. This is the read-only replacement for the spec's dropped `write_spec` capability: the server produces the bytes and validation, and the host — which already has file access, already shows the user a diff, and is already the thing the user authorised to edit their repository — decides whether and where to write them.

Normalization is **opt-in**: the `normalize` parameter defaults to false because the measured comment loss is permanent (see below). Without `normalize: true`, the tool returns the suite's full validation result exactly as `validate_suite` would at `level: "full"`, wrapped alongside a `null` `normalizedYaml` field. A caller that has not said "I accept losing my comments" gets validation data, not a rewrite of their file.

**Comments discarded.** The YAML library this server is pinned to (`YamlDotNet` 18.1.0) cannot carry comment events through re-serialisation — the stream loader does not consume them, and the emitter corrupts documents on inline comments (a mapping value can be swallowed into a comment). Comment-to-node association is guesswork under reordering. This server evaluated comment-preserving normalization against the pinned library, found it failed, and chose the honest default: normalization drops all `#` comments. **Do not set `normalize: true` without the user's explicit agreement on a commented suite**, and diff before writing the result to disk.

**Canonical form.**

- **Key order** is taken from the vendored schema's own property declarations, ranked with schema ancestors (outermost first) — but only for mappings the schema actually describes. **A mapping of your own data is left in the order you wrote it**, even when one of its keys happens to share a name with a schema field. Two rules make that true: a mapping reached through a key the schema declares as a free-form container (`headers`, `body`, `variables`, `services`, `dependencies`, `capture`, `labels`, `env`, `parameters` and the rest — the list is derived from the schema, not hand-written) is never reordered at all, and any other mapping is reordered only when a strong majority of its keys belong to the matching schema shape. Measured before those rules existed: `headers: {zebra, id, alpha, type, name}` came back as `{id, type, zebra, alpha, name}`, and a `services` map was reordered because one service was named `image`.
- **Sequences are never reordered.** Step order is the suite's meaning.
- **Quoting** follows single→double conventions only; the quoted↔plain boundary is never crossed, because that boundary carries resolved type information (`'yes'` unquoted is a boolean; `"007"` unquoted is the integer 7). Two exceptions are forced by the emitter rather than chosen: an empty scalar carrying an explicit tag (`!!str`) and a plain scalar the emitter will not write plain (measured: text outside the Basic Multilingual Plane, such as emoji, comes back double-quoted with `\U…` escapes). **The value is identical in both cases — only its spelling changes.**
- **Layout**: every non-empty mapping and sequence is written in block style, two-space indented, with block sequences indented under their key. Empty collections keep their compact `{}` / `[]` form — the only thing block style can render them as. Long scalars are never folded onto a second line.
- **Anchors and aliases are preserved, never expanded.** An anchor belongs to its NODE, so when reordering moves the aliased node's first occurrence, the `&name` definition moves with it and the `*name` reference follows. The graph is identical; the line the anchor sits on may not be.
- **Idempotent**: `normalize(normalize(x)) == normalize(x)`, byte-identically, LF line endings and exactly one trailing newline on every platform.
- **Read-only.** No suite file is ever written, modified, or deleted; the canonical text is returned to you as a string only. Enforced by `ReadOnlySourceGuardTests` at source level and by on-disk byte, timestamp and sibling-file proofs.

**The canonical text is proved before it is returned.** After rendering it, the server parses it back and compares the result with an untouched parse of your input; if it does not re-parse, or re-parses to a different document, you get `normalizedYaml: null` and a `normalizationRefused` reason instead of text. There is one known shape that triggers this — an alias used as a mapping **key** (`*anchor : value`), which YamlDotNet's emitter writes as `*anchor:` and cannot read back. A refusal says nothing about your suite: the validation result is complete and unaffected. **Never write a file from a response whose `normalizationRefused` is non-null** — there is nothing to write.

**Practical ceiling.** The 2 MB input cap is set deliberately within the validation worker's 10-second budget: an admitted suite is expected to complete validation. Measured on a reference host at `level: "full"` over uniform `http.rest` suites, a worst-case-shape suite at the 2 MB cap validates in ~6.5 s (median) and ~7 s with normalization (slowest run ~7.2 s), leaving ~3 s margin before timeout. Measured curve: 0.5 MB takes ~2.1–2.6 s (validate/normalize); 1.0 MB ~4.0–4.5 s; 1.5 MB ~5.2–5.7 s; 2.0 MB (cap) ~6.5–7.0 s. **Normalization is a ~10–15% surcharge.** The timeout is deliberately not relaxed for this tool: it exists to bound uninterruptible parser spins. `VFX-E-1150` is now reachable essentially only through transient host load or CPU contention rather than suite size — the cap no longer admits a suite that overruns the budget on the measured shape, though a pathological-but-legal YAML that is slow to parse while staying under every hard cap can still hit it.

**Always validates at `ValidationLevel.Full`.** There is no `level` argument. A caller could otherwise ask for schema-only validation and receive canonical text for a suite whose embedded AWS key the semantic pass never looked for — silently turning off the `VFX-D-1207` secret-literal check on the one result a host is invited to write back to disk. That gate is structural here, not a rule to remember: the diagnostic appears because the full pass ran, and nothing in this tool can arrange for it not to. Every result carries the full validation outcome (valid, errors, semanticDiagnostics, semanticDiagnosticsTruncated, summary, level) and carries the same meanings documented in `validate_suite`.

**Belongs to the CLI-free class.** Like `validate_suite`, `search_docs`, and `explain_diagnostic`, this tool works entirely offline: no engine install, no network, no Docker. The suite is parsed inside the spawned `--validate-worker` child, under the same wall-clock timeout (10 seconds) and whole-tree process kill as `validate_suite`, whether the suite arrived as a file path or as inline YAML.

- **Parameters**:
  - `path` (string, optional) — absolute or workspace-relative path to the `.e2e.yaml` suite file. A relative path resolves against the workspace root when the server was started with `--workspace`, and against the server process's current directory otherwise. Supply this OR `yaml`, never both.
  - `yaml` (string, optional) — the suite's YAML text, normalized directly without reading or writing any file. Supply this OR `path`, never both.
  - `normalize` (boolean, optional, default `false`) — set `true` to receive the canonical YAML in `normalizedYaml`. Default `false`, because normalization **discards all comments** in the suite. Left at `false`, `normalizedYaml` is `null` and only the validation result comes back. Do not set it to `true` without the user's agreement on a commented suite, and always diff before writing.

- **Result shape**: `{ normalizedYaml: string | null, commentsDropped: boolean, normalizationRefused: string | null, validation: { valid, errors, semanticDiagnostics, semanticDiagnosticsTruncated, summary, level }, meta }`.
  - `commentsDropped` is `true` on exactly the responses that carry canonical text — on the pinned YAML library, producing it and discarding every `#` comment are the same act, so the loss is stated on the payload and not only in this page and the tool description. It is `false` whenever `normalizedYaml` is `null`, because nothing was produced and nothing was lost.
  - `normalizationRefused` is `null` in every ordinary outcome. It is non-`null` only when canonical text was rendered and then rejected by the re-parse gate above, and it carries one of two fixed, content-free tokens: `canonical-text-did-not-re-parse` or `canonical-text-changed-the-document`. It is deliberately **not** a `VFX-E-####` code: the taxonomy describes what is wrong with your input, and a gate refusal says only that this server's emitter could not render a fine document faithfully.
  - Together the three fields tell the three reasons `normalizedYaml` can be `null` apart: normalization was not requested (`commentsDropped: false`, `normalizationRefused: null`), there was no document to canonicalise (same, with the reason in `validation`), or the emission was refused (`normalizationRefused` names which half of the gate failed).
  - The `validation` object is the complete `validate_suite` payload — same field meanings, same structure, same codes in both channels. A suite that is merely invalid still has a canonical form; you get both the errors and the normalized text on a successful call.

- **Input validation**:
  - Exactly one of `path` or `yaml` must be supplied; both or neither is an error `VFX-E-1152`.
  - A `path` of exactly `--yaml-stdin` is refused with the same error `VFX-E-1152` (internal marker collision).

- **Error codes** — returned as a **tool error** (`isError` true), carrying a single `{ code, message, docsUrl, retryable }` object, because in each of these cases validity was never determined:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `path` is a UNC/network location, or (when a workspace is configured) resolves outside its root. | false |
  | `VFX-E-1002` | The suite file does not exist. | false |
  | `VFX-E-1003` | The suite file exists but could not be read. | false |
  | `VFX-E-1150` | The isolated validation worker exceeded its wall-clock budget and was killed. | true |
  | `VFX-E-1152` | Exactly one of `path` or `yaml` must be supplied; both, neither, or a `path` of exactly `--yaml-stdin` is an error. | false |
  | `VFX-E-1901` | The validation worker could not be started, crashed, or produced unusable output. | true |

- **Diagnostic codes** in `validation.errors[]` and `validation.semanticDiagnostics[]` — identical to `validate_suite`'s, returned as data on a **successful** call (the tool worked, the suite validity was determined). See `validate_suite`'s tables above for the full catalogue and meanings. Of particular note for this tool: **`VFX-D-1207` (literal secret detected)** always appears at severity `error` when its structural shapes are found, because `validate_suite` always runs at `level: "full"`. Never returns canonical YAML without surfacing any detected VFX-D-1207 in the validation result.

- **Notable behaviour — process-isolated worker, file or inline.** Identical to `validate_suite`: the YAML parse, schema evaluation, and normalization all run inside a disposable child process bounded by a 10-second wall-clock timeout, with the same whole-tree kill on timeout and the same stdout/stderr caps. Inline YAML is transported over stdin, never written to disk. A suite that does not parse is a successful call with `summary: null`, `normalizedYaml: null`, and error diagnostics; a missing/unreadable file or worker failure is a tool error.

- **Notable behaviour — a `level` argument is ignored, not honoured.** There is no `level` parameter; a host that sends one anyway still gets `validation.level: "full"`. That is what makes the `VFX-D-1207` gate structural rather than a convention.

- Never throws. Read-only always. A suite that is invalid is a successful call with diagnostics; a missing file, an unparseable worker response, or an input-validation failure is a structured tool error.

### list_step_types

Lists every step type the **pinned engine** supports, in dotted `<family>.<provider>` form, grouped by
family — loaded from the live CLI export `vouchfx list --json`, not from a hand-maintained or
vendored-only catalogue (REQ-010).

- **Parameters**: none.
- **Result shape**: `{ families: [{ family, familyIntent, types: [{ type, provider, description,
  captureSupported, familyIntent, requiredResources }] }] }`, families ordered alphabetically, types ordered
  alphabetically within each family. `requiredResources` is a string array of the dependency kinds a step
  of that type needs declared in `environment.dependencies` — an empty array means "none, derived"; the field is omitted
  entirely for a step type this server cannot derive it for (e.g. a type the vendored schema does not define).

  > **Deliberately absent from every entry, never defaulted or guessed:** `tier`, `vouched`,
  > `supportsVerifyMode`, `example`, `docsUrl` (the spec's §5.2 `ProviderInfo` record lists these, but the
  > pinned engine's `vouchfx list --json` does not emit them). They are pending upstream ask U5.
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
`list_step_types`: required and optional field **names**, capture support, family intent, and required resources.

- **Parameters**: `type` (string, required) — the dotted `<family>.<provider>` type name exactly as
  `list_step_types` reports it, e.g. `db-assert.postgres`.
- **Result shape**: `{ type, family, provider, description, fields: [{ name, type, description,
  required }], requiredOneOf, requiredFields, optionalFields, captureSupported, familyIntent, requiredResources }`.
  `fields` is derived from `requiredFields` / `optionalFields` (type/description may be null for
  live-export entries). `requiredResources` is a string array of the dependency kinds a step of this
  type needs declared in `environment.dependencies` — an empty array means "none, derived"; the field is omitted
  entirely for a step type this server cannot derive it for. Excludes the common step envelope fields every step type shares (`id`,
  `type`, `description`, `capture`, `verifyMode`, `timeout`, `continueOnFailure`).

  > **Deliberately absent from every result, never defaulted or guessed:** `tier`, `vouched`,
  > `supportsVerifyMode`, `example`, `docsUrl` (the spec's §5.2 `ProviderInfo` record lists these, but the
  > pinned engine's `vouchfx list --json` does not emit them). They are pending upstream ask U5.
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
  - `path` (string, required) — absolute or workspace-relative directory to search recursively for
    `*.e2e.yaml` suites, or a single suite file — the declared universe to analyse.
  - `eventsPath` (string, optional) — absolute or workspace-relative path to a JSON Lines event
    history file, or a directory of `*.jsonl` files. Omit for no history: every declared suite/step
    is reported never-run (a valid, successful analysis).
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
  | `VFX-E-1001` | `path` or `eventsPath` named a network/UNC location (always refused), or resolved outside the configured workspace root. | false |
  | `VFX-E-1006` | An argument was rejected — a bad or missing suite path, an empty suite folder, or an out-of-range threshold. | false |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, not launchable, or lacks the M3 Planner. | false |
  | `VFX-E-1603` | The Planner ran but produced no analysis — it failed, timed out, overran its output cap, or returned unreadable output. | false |

  `VFX-E-1603` covers several causes, two of which (a timeout, an output-cap overrun) would be
  transient in isolation. It is nonetheless `retryable: false`, because its other causes are not, and
  in both transient cases the message tells you the genuinely useful thing — narrow `path` or
  `eventsPath` and retry, which is a *different* call.
- **Path safety.** Both path arguments go through the same guard every other path-taking tool uses,
  and the resolved string is what reaches the engine's command line: a network/UNC location is
  refused in **both** workspace modes, and with `--workspace` configured a relative path is resolved
  against the root and the result must be inside it. Refusal happens before the CLI is spawned at
  all. The UNC half is a behaviour change — until [issue #76](https://github.com/tomas-rampas/vouchfx-mcp/issues/76)
  this tool passed both arguments to `vouchfx plan` unchecked.
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

Runs one or more `.e2e.yaml` suites through the packaged `vouchfx` CLI and reports the verdict once
the run(s) complete. Supply either `path` (one suite file) or `paths` (an array of files and/or
workspace-relative globs) — exactly one, never both.

- **Parameters**: `path` (string, optional) or `paths` (string array, optional) — exactly one must be
  supplied. `path` is a single suite file (glob syntax not expanded). `paths` is an array of files
  and/or workspace-relative glob patterns (e.g. `e2e/checkout/**` expands to the `*.e2e.yaml` files it
  matches under that directory); patterns with `..` segments or absolute paths are refused. Globs that
  match no files are refused with `VFX-E-1002` (no suite found). At most 50 entries, expanding to at
  most 100 suites; exceeding either is refused, never silently truncated. Any entry containing `*` or
  `?` is read as a pattern, with no escape syntax — on Windows those characters cannot occur in a file
  name at all, but on Linux they can, so a suite literally named `what?.e2e.yaml` must be passed as
  `path` (which is never glob-expanded) rather than in `paths`. `tags` (string array, optional) —
  restrict all suites to steps/scenarios matching one or more tags. `labels` (object, optional) —
  free-form key/value metadata recorded with the run for later correlation (max 20 keys, key/value
  length bounds 64/256 chars; recorded in the run registry but not yet in the engine's JSON Lines
  event envelope — awaits engine support). `timeoutSeconds` (integer, optional, 1–3600, default `300`)
  — abort the run if it has not completed in time; this is the WHOLE-call budget, not per-suite.
  `keepEnvironment` (boolean, optional) — only `false` (default) is implemented; `true` is refused
  with `VFX-E-1504`. `wait` (boolean, optional) — only `true` (default) is implemented; `false` is
  refused with `VFX-E-1504`.
- **Result shape** (on `Completed`, in serialised field order): `{ runId, verdict, exitCode, cancelled,
  timedOut, remediationHint, steps: [{ stepId, verdict, durationMs, attemptCount, observation }],
  eventsFilePath, specs: [{ path, outcome, steps: […] }], eventsTruncated }`.
  - `runId`: the id this run was registered under — pass it to `get_run_events` to read the run's raw
    event stream. `null` in exactly the case `eventsFilePath` is empty (the budget expired before any
    run was registered, so no id was ever minted). Note this is *this server's* id; the `runId` field
    inside the engine's own events is a different, bare-hex value.
  - `exitCode`: the CLI's own process exit code when this run covered exactly one suite; `null` for
    multi-suite runs (each suite's own outcome is in `specs[]`).
  - `steps`: the concatenation of every suite's steps, kept at the top level for backward compatibility;
    a caller that only reads this field is unaffected by multi-suite runs.
  - `eventsFilePath`: ONE file per run (not per suite), holding the complete JSON Lines event stream
    across all suites. Empty only when the call's budget expired before any run was registered (see the
    `timeoutSeconds` note below).
  - `specs`: an entry per suite this run covered, in run order. Each entry carries the suite's path (as
    resolved), its outcome (Pass/Fail/EnvironmentError/Inconclusive, or `null` for un-run suites when
    an earlier suite's cancellation or timeout halted the whole run), and that suite's own step list.
    A suite that **fails** (or hits an environment error) does **not** stop the run — every later
    suite still executes and reports its own outcome, with the overall verdict elevated across all of
    them; only cancellation or the whole-call timeout halts the sequence. There is deliberately no
    fail-fast option today; a host that wants to stop early cancels the call.
  - `eventsTruncated`: `true` when what you can read of `eventsFilePath` is not the whole stream —
    because it exceeded the reader's byte cap, because a multi-suite run's later parts were dropped once
    the stream reached that cap, or because appending one part failed. The verdicts in `specs[]` are
    computed before any merge and are unaffected.
- **The four taxonomy verdicts, never conflated**: `Pass`, `Fail`, `EnvironmentError`, `Inconclusive`.
  A cancelled or timed-out run is always reported as `Inconclusive`, distinguished via `cancelled` vs.
  `timedOut` — never as `Fail`. The overall verdict is the worst of every suite's verdict (Pass <
  Inconclusive < Fail < EnvironmentError). `remediationHint` is populated whenever `verdict` is
  `EnvironmentError` (e.g. naming the Docker daemon when that looks like the cause) and is `null`
  otherwise.
- **Gate ordering, cheapest first — nothing is spawned unless every earlier gate passes**: gated
  options (`wait: false` or `keepEnvironment: true` are refused with `VFX-E-1504`) → exactly one of
  `path`/`paths` (both or neither is `VFX-E-1503`) → argument safety (a `path`/`tag` beginning with
  `-`, out-of-range `timeoutSeconds`, or label bounds — all rejected with `VFX-E-1006`) → the same
  pre-flight validation `validate_suite` performs on every suite (an invalid suite is returned as a
  `{ code: "VFX-D-1100", path, validation }` payload with `isError` **false**, since an invalid suite is
  data, not a tool failure — the CLI is never spawned; a missing/unreadable file is the same
  `VFX-E-100…` tool error `validate_suite` returns for it, with the suite's path prefixed onto the
  message), **all-or-nothing per run** (one invalid suite refuses the whole call and runs nothing —
  `path` is what tells you which of a glob's suites it was) → single-flight concurrency (at most one run per workspace at a time,
  enforced across separate server processes when `--workspace` is configured; a concurrent call is
  rejected immediately with retryable `VFX-E-1501`, never queued; a lock file that cannot be opened at
  all — planted link, directory in its place, permissions problem — is `VFX-E-1502` instead, which is
  retryable too but for a different reason: not "wait for the other run" but "fix the directory, then
  the same call works") → CLI presence + version handshake (missing/mismatched returns `VFX-E-1401`) →
  the run itself.
- **`timeoutSeconds` bounds the whole call, starting at the first gate that touches the filesystem**:
  path expansion, the per-suite pre-flight, the CLI handshake and the run itself all spend from the one
  budget. If it expires before any suite starts, the call returns the ordinary timed-out **result** —
  `verdict: "Inconclusive"`, `timedOut: true`, every resolved suite in `specs[]` with `outcome: null`
  ("not run") — rather than an error, because a timeout is Inconclusive in the taxonomy and never an
  infrastructure failure. In that one case `eventsFilePath` is an empty string and `runId` is `null`:
  no run was registered, so no events file was ever created and no id was ever minted. (One thing is
  not interruptible: a single glob walk, because the
  matcher exposes no cancellation. A `**` over a very large tree can therefore overrun the budget by
  the length of that walk; anchor the pattern with a literal prefix.)
- **Error codes** — note that a failing *suite* is not among them: a run that fails is a
  **successful** call reporting `verdict: "Fail"`.

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `path` or glob is a UNC/network location, or (when a workspace is configured) resolves outside its root. | false |
  | `VFX-E-1002` | The suite file does not exist; or a glob pattern matched no `*.e2e.yaml` files. | false |
  | `VFX-E-1003` | The suite file exists but could not be read. | false |
  | `VFX-E-1006` | An argument was rejected before anything was spawned — a `path`/`tag` beginning with `-`, an out-of-range `timeoutSeconds`, a label past its count/key/value bounds, a glob that is absolute or contains a `..` segment, or a path list past its caps (at most 50 entries, expanding to at most 100 suites totalling at most 24,000 characters). | false |
  | `VFX-E-1150` | The pre-flight validation worker exceeded its wall-clock budget and was killed. | true |
  | `VFX-E-1401` | The pinned `vouchfx` CLI is missing, version-mismatched, or not launchable. | false |
  | `VFX-E-1501` | Another run is already active per workspace (or on this server, if no `--workspace` is configured). | true |
  | `VFX-E-1502` | The run could not be recorded — the run lock or registry write failed, or the run's own metadata was too large to store. Nothing was run. | true |
  | `VFX-E-1503` | Both `path` and `paths` were supplied, or neither was. Supply exactly one. | false |
  | `VFX-E-1504` | `wait: false` or `keepEnvironment: true` were requested; only the defaults (true/false) are implemented today. | false |
  | `VFX-E-1901` | The pre-flight validation worker could not be started, crashed, or produced unusable output. | true |

  The path/validation codes (`VFX-E-1001`, `-1002`, `-1003`, `-1150`, `-1901`) are shared with
  `validate_suite` by design: both tools run the same pre-flight check through the same classifier, so
  they can never give different answers about one file. `run_suite` adds one thing to those messages
  that `validate_suite` does not need — the suite's own path, prefixed — because its pre-flight covers
  every suite in the call and the guard that writes the message names none. (`VFX-E-1006` is shared in
  name only: each tool rejects its own arguments, and the list above is `run_suite`'s.) A suite that is
  merely **invalid** is the case that is *not* an error here — see the gate ordering above.
- **Serialisation & events.** Every attempted run writes its own JSON Lines event stream (path returned
  as `eventsFilePath`); reading that file is bounded at 50 MB, with `eventsTruncated: true` when the
  file exceeded that and had to be read only up to the cap. A multi-suite run concatenates each suite's
  per-suite events into the single stream; per-suite attribution is tracked in `specs[]`, computed
  before concatenation. `explain_run` is designed to read this same file afterwards.
- **Progress.** Reports best-effort progress as the run proceeds (start, each relayed CLI output line,
  a closing summary) when the calling client requests MCP progress notifications.

### explain_run

Diagnoses a completed suite run in plain language, purely by reading and parsing its JSON Lines event
stream. Never re-runs anything — no CLI spawn, no validation worker, no container.

- **Parameters**: `eventsPath` (string, optional) — path to the run's events file; when omitted, the
  most recent finished run in the run registry is used automatically (spans server restarts when
  launched with `--workspace`; session-scoped otherwise).
- **Result shape**: `{ verdict, categoryMeaning, summary, totalStepCount, passedStepCount, notableSteps:
  [{ stepId, verdict, durationMs, attemptCount, observation, attempts: [{ attempt, tMs, outcome,
  observation }], omittedAttemptCount, reason: { kind, hint } }], omittedNotableStepCount,
  environmentErrors: [{ errorKind, resourceName, detail, reason: { kind, hint } }],
  omittedEnvironmentErrorCount, eventsFilePath, eventsTruncated, responseTruncated,
  classificationHints: [string] }`. (`classificationHints` serialises last — it was added to the
  shape after the other fields.)
- `categoryMeaning` always accompanies `verdict` — a short, fixed explanation of what that CATEGORY
  means (e.g. that `EnvironmentError` is an infrastructure problem and explicitly **not** a test
  defect), so an agent never has to infer the taxonomy's meaning itself.
- `notableSteps` names every step whose own verdict is not `Pass` — a passing step is never "notable" —
  together with its full RETRY attempt timeline (`attempts`) and observation/diff evidence. Each step
  also carries an optional `reason: { kind, hint }` when the verdict classifier could assign one:
  - `kind` is one of `pull`, `unhealthy`, `seed`, `timeout`, `capture_unmet`, `partition`, or
    `assertion` (the only kind ever assigned to a `Fail` step); `kind` may be `null` for an
    unrecognised `errorKind` on an environment error, a fail-closed default rather than a guess.
    An eighth value, `compile`, completes the vocabulary but is reserved for a future engine
    capability — no rule in this build ever emits it.
  - `hint` is a bounded (≤ 300 character), never-empty plain-text explanation, with a visible `…`
    truncation marker when clipped. Carries only engine-derived text (an image reference, a resource
    name, a timeout, an observation sentence) — never `${secret:...}` references, never re-redacted.
  - For any notable step — `Fail`, `EnvironmentError`, or `Inconclusive` alike — `reason` is `null`
    when the rule table did not classify it; a step's own verdict never guarantees a reason.
    Environment-error **records** (`environmentErrors` below) are the surface that always carries one.
- `environmentErrors` carries every `environment-error` event the run recorded. Each record carries a
  `reason: { kind, hint }` structured the same way as step reasons, always present (never `null`),
  with the same bounded, deterministic hint text.
- `classificationHints` is a flat digest of every hint from the `reason` field on items this response
  actually includes (one per classified step, one per environment-error record), deduplicated by
  exact string with first-occurrence order preserved. Ten steps failing the same assertion yield one
  hint. Sourced from the tier-capped lists, not the omitted-count totals, so this field's size stays
  bounded by the response budget. Empty when no items carry a classification. This array is a
  display/summary digest only — it can include the hint of an environment-error record whose `kind`
  is `null`, and the flat string shape carries no kind. To branch programmatically, read
  `notableSteps[].reason.kind` and `environmentErrors[].reason.kind`, never this array.
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
- **No run to explain**: if `eventsPath` is omitted and the run registry contains no finished run,
  returns an MCP tool error saying so, rather than fabricating a diagnosis.
- **Path safety**: a UNC/network `eventsPath` is rejected before any filesystem call is made against
  it, for the same forced-authentication reason `validate_suite`/`run_suite` reject one for their own
  `path` argument. When a workspace is configured, a relative `eventsPath` resolves against the
  workspace root and the result is checked for containment within it, using the same rules that apply
  to suite paths. **No exemptions**: containment applies uniformly to a caller-supplied `eventsPath`
  and to the default (omitted) one alike. Handing back the `eventsFilePath` `run_suite` returned still
  always works, and now for the ordinary reason — a workspace-configured server writes run artefacts
  under the workspace's own output directory, so that path is inside the root and passes containment
  on its merits. Without a workspace, containment does not apply at all.
- **Error codes** — identical to `diagnose_run`'s, since both tools read the same events file through
  the same guards and fail for the same five reasons. Note that a run whose verdict is `Fail` or
  `EnvironmentError` is a **successful** call: these codes are only about being unable to produce a
  diagnosis at all.

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The `eventsPath` is a UNC/network location, or (when a workspace is configured) resolves outside its root. | false |
  | `VFX-E-1004` | The events file does not exist. | false |
  | `VFX-E-1005` | The events file exists but could not be read. | false |
  | `VFX-E-1601` | `eventsPath` was omitted and the run registry contains no finished run. | false |
  | `VFX-E-1602` | The events file was read but contained no recognisable vouchfx event. | false |

### diagnose_run

Healer (M2 / Spec C): the same taxonomy-faithful diagnosis as `explain_run`, plus **two proposal
kinds** grounded in the event stream. Deterministic templates only — no LLM inside this server, no
auto-apply, no writes to the customer's suite file, no engine `healer-suggestion` events.

**Workflow:** `run_suite` → events file → `explain_run` / `diagnose_run` → human (or host under
human review) applies any accepted patch → `validate_suite` → `run_suite` again. Free text belongs
only in the host conversation, not as a tool parameter.

- **Parameters**: `eventsPath` (string, optional) — path to the run's events file; when omitted, the
  most recent finished run in the run registry is used (spans server restarts when launched with
  `--workspace`; session-scoped otherwise). Suite path is **not required** for v1; proposals are
  evidence-based from observations when suite YAML is absent.
- **Result shape**: `{ diagnosis: { …same fields as explain_run…, including classificationHints and
  per-step/per-error reason }, proposals: [{ stepId, rationale, patch }], environmentGuidance:
  [string], specEditProposals: [{ stepId, scope, rationale, suggestedEdit }] }`.
- **`proposals`** (review-only, Fail-only): non-empty only for step-level **Fail** with
  non-empty observation/diff evidence. Each proposal has `stepId`, a short `rationale` grounded in
  that evidence, and a `patch` (unified-diff style review comment / YAML fragment placeholders).
  Empty for **Pass**, pure **EnvironmentError**, and **Inconclusive**. Structure and semantics are
  unchanged from earlier versions — `EnvironmentError` and `Inconclusive` outcomes never produce a
  proposal from this list.
- **`specEditProposals`** (scoped, EnvironmentError/Inconclusive): a second, distinct proposal list
  for outcomes where editing the suite is appropriate. Non-empty only for step-level
  **EnvironmentError**/**Inconclusive** or environment-error records where the reason classifier
  (US-S4-01) assigned a structured `reason.kind`. Empty for **Pass** and for every **Fail** step
  (an assertion is never weakened). Each proposal carries:
  - `stepId`: the affected step's id, or **`null`** when the proposal concerns an environment-error
    **record** rather than a step (always `null` for the `environment` scope, which addresses image
    tags, dependency versions, and seed targets).
  - `scope`: one of exactly four values (a closed set): `"environment"` (image tag, dependency
    version, seed target); `"timeouts"` (raise timeout, adjust verifyMode); `"match"` (the key
    or headers a step polls on); `"capture"` (the extractor JSONPath that produced nothing). No
    other scope is ever emitted.
  - `rationale`: short text grounded in the classified reason (at most 500 characters — the measured
    worst case is 396 — and never empty; there is no enforced minimum length).
    The classifier's own hint may be embedded and may end in a visible `…` truncation marker if it
    was capped at 300 characters; that original bound is documented in this payload and not
    recapped. **Exception — the response-size ladder** (below): when the budget forces bodies to be
    elided, rationales are cut to 120 characters, and one stage further down every rationale is
    replaced by a fixed ~60-character truncation notice. Those two shapes are the only ones outside
    the range above.
  - `suggestedEdit`: a YAML fragment (never a unified diff — the same review-only framing Fail
    proposals use, because this server was never given a file path to diff against). Opens with a
    comment block explaining it is a suggestion only. Vocabulary comes from the vendored schema
    (real field names, never invented keys). Never auto-applied, never written to disk.
  - **Response trimming never changes which proposals you get.** A `timeout` step yields a `match`
    proposal exactly when the run actually observed values that did not match, and that fact is
    established by the classifier from the run's *untrimmed* attempt data — so the same run always
    produces the same proposals, however heavily the response itself was trimmed for size.
- **`environmentGuidance`**: infrastructure checklist when environment-error evidence is present
  (image pull, health, provision, Docker). **Never** accompanied by YAML rewrite patches for those
  failures. Inconclusive may include non-patch guidance only. Structure and usage are unchanged.
- **Never auto-apply**: proposals of both kinds are returned in the tool result only — the tool is
  read-only and does not invoke git or write suite files.
- **Same path/error behaviour as `explain_run`**: registry-based default (omitted `eventsPath` uses
  the most recent finished run), UNC rejection, workspace containment (uniformly applied, with no
  exemptions, and the same workspace-relative resolution — it shares `explain_run`'s whole path-intake seam),
  missing/unreadable file, no recognisable events — structured tool errors, no hang. Response size
  aligned with `explain_run`'s 32 KB diagnosis budget — and with the same caveat about the larger
  wire envelope documented there; full detail remains in the events file path inside `diagnosis`.
  Both proposal lists shed detail at the same points as the budget tightens: first the bodies go
  (a `FailProposal`'s `patch` and a `SpecEditProposal`'s `suggestedEdit` are replaced by a short
  "omitted" comment, with a shortened rationale kept), then the rationales — at which stage
  `environmentGuidance` also collapses to a single truncation notice and spec edits that have become
  identical (same `stepId` and `scope`) are deduplicated — and finally `proposals`,
  `specEditProposals`, and `environmentGuidance` are dropped together, never one while the others
  survive. In the rarest case, where even that emptied shape plus the response wrapper will not fit,
  `diagnosis` itself falls back to `explain_run`'s minimal shape: `notableSteps`,
  `environmentErrors` and `classificationHints` all empty, `responseTruncated: true`, the counts and
  the events file path retained, and the summary saying the detail could not be returned.
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

### get_run_events

Returns one page of a completed run's **raw** JSON Lines events, exactly as the engine wrote them —
no summarising, no interpretation, no re-running. Use it to build your own timeline or dashboard over
a run instead of consuming `explain_run`'s summarised diagnosis, or to inspect an event type this
server does not model. Never spawns the engine CLI, and never takes the run lock, so it is safe to
call while a run is in flight.

- **Parameters**:
  - `runId` (string, **required**) — the run to read, as returned on `run_suite`'s own result. Resolved
    through the run registry, which spans server restarts when the server was launched with
    `--workspace` and is session-scoped otherwise. This is **this server's** id (`run-`-prefixed); the
    `runId` field *inside* the relayed events is the engine's own bare-hex run identifier, a different
    value, passed through untouched like every other engine field. Do not expect the two to match.
  - `types` (string array, optional) — only return events whose `type` is one of these (e.g.
    `["step-attempt", "step-completed"]`). Matched exactly against the token the engine wrote. Omit,
    or send an empty array, for every type.
  - `stepId` (string, optional) — only return events belonging to this step id. Matched exactly.
    Omit for every step.
  - `limit` (integer, optional) — maximum events to return; **default 200, maximum 2000** (spec
    §4.5). An out-of-range value is **refused** (`VFX-E-1006`), never silently clamped, so a short
    page is never mistaken for the end of the stream.
  - `cursor` (string, optional) — a `nextCursor` from a previous call, passed back unchanged.
- **Result shape**: `{ eventSchemaVersion, events: object[], nextCursor?, truncated }` (plus the shared
  `meta` object every successful result carries).
  - `truncated`: `true` when this page is not everything the stream held — the events file exceeded
    the 50 MB read cap, the scan hit its 2,000,000-line backstop, an over-long line was passed over
    with its type unreadable (on a filtered page only), or a mid-file line was not parseable as a JSON
    object at all. All four are described below. **Read it together with `nextCursor`, never instead
    of it**: `nextCursor` says more *matching* events remain within what was read; `truncated` says
    what was read is not all there was. The combination to watch for is `truncated: true` with no
    `nextCursor` — the walk has ended at this server's bound, not at the end of the run.
- **Filters are applied before paging.** `limit` bounds the number of **matching** events returned,
  not the number of lines scanned. A run of 5000 events with 40 matches and `limit: 10` returns ten
  matching events, four pages in total.
- **Wire vocabulary, not response strings.** Events carry the engine's own tokens —
  `PASS` / `FAIL` / `ENV_ERROR` / `INCONCLUSIVE` — never the
  `Pass` / `Fail` / `EnvironmentError` / `Inconclusive` strings that `run_suite`, `explain_run` and
  `diagnose_run` put on *their* results. This is the raw wire boundary; the two vocabularies name the
  same four-way taxonomy and must not be conflated when you consume both tools.
- **Unknown event types and unknown fields pass through untouched.** The v1 event contract is
  additive-frozen, and a raw-event tool that dropped what it did not recognise would be strictly less
  useful than reading the file yourself.
- **Every relayed value is sanitised, so the text is not byte-identical to the file.** String values
  and property names, at every depth, are rendered through the same control-character escaping
  `explain_run` applies: every character outside printable ASCII (`0x20`–`0x7E`) comes back as a
  literal six-character `\uXXXX` escape, so `é` in the file reads as `é` in the result. Nothing is
  re-redacted or re-resolved — the engine is the sole redaction authority and these bytes have already
  passed through it.
- **`eventSchemaVersion`** is read from the stream's own version marker when it declares one, and
  otherwise reports the vendored composed schema's version. Measured against the currently pinned
  engine (`v1.0.0-rc.4`), every event carries a `"v":1,"schemaVersion":"v1"` prefix, so the marker
  path is what fires in practice and `v1` is what you receive. The vendored-version fallback covers a
  stream that declares nothing — an older engine's file, or one whose first 50 lines are all
  unparseable — and at the currently pinned engine it happens to produce the same string `v1`, so the
  value alone does not tell you which path ran. Either way it is identical on every page of one run.
- **`nextCursor` is opaque and single-purpose.** Pass it back unchanged as `cursor`; do not construct,
  parse, or edit it, and do not carry it between tools or between runs. It is bound to the `runId`,
  `types` and `stepId` it was issued under and is refused (`VFX-E-1506`) if any of those change —
  `limit` may change freely between pages. **It is absent, not null, on the last page**: the server
  looks one matching event ahead before minting it, so its presence always means at least one further
  matching event exists, and you never learn the walk is over by fetching an empty page.
- **A page may be shorter than `limit`.** Beyond the count, a page is bounded by a 32 KB serialised
  payload budget, and for realistic events that budget binds first. Measured against a `step-attempt`
  carrying a step id, an attempt number, a `tMs` and a small observation object: **146.5 bytes per
  event**, so `limit: 2000` actually returns **224 events in 32,827 bytes**; the full 2000 would have
  been about 293,000 bytes, roughly 9x the budget. When the budget stops a page early you
  still get a `nextCursor` — check that, not the event count, to decide whether the walk is over. (As
  with `explain_run`, the 32 KB figure bounds the payload rather than the wire envelope, which is
  larger because every result is carried twice and the text copy is escaped.)
- **A single oversized event is replaced, not trimmed.** An event whose relayed form would exceed
  4 KB comes back as a small marker object carrying its `type` and `stepId` plus
  `"_vfxTruncated": true` and `"_vfxOriginalBytes": <n>`. The underscore prefix marks these as this
  server's own fields, never the engine's. Trimming the event field by field instead would produce
  something that looks like the engine's event but silently is not. The same marker is what you get
  for every other "cannot reproduce this faithfully" case: an event with more than 256 properties or
  array elements at one level, one nested deeper than 24 levels, one whose line exceeds 1 MB (refused
  before it is parsed at all — with one exception: on a `types`/`stepId`-filtered page an over-long
  line is passed over with no marker, because its type was never readable and asserting it matched
  your filter would corrupt the timeline you narrowed on purpose; **that page reports
  `truncated: true`**, since the line was dropped without ever establishing whether it matched), and
  one carrying an unpaired `\uD800`–`\uDFFF` surrogate escape — in a value *or* in a property name,
  neither of which can be decoded to text.
- **Every other bound is marked too — nothing is shortened silently.** A single string value or
  property name is kept to **2000 characters**; when any string in an event was cut, that event
  carries `"_vfxStringsCapped": true` alongside the engine's own fields. (The cap is applied before
  escaping, so a capped string can still be longer than 2000 characters in the result.)
- **Malformed lines are skipped, not fatal — and a mid-file one sets `truncated`.** A line that is not
  valid JSON, or is JSON but not an object, is passed over — the same tolerance `explain_run` applies,
  so one forward-incompatible line never makes an otherwise-good run's events unreadable. An empty
  events file is an empty page, not an error. If such a line has **further lines behind it**, the page
  reports `truncated: true`, because the scan completed but silently omitted something: it is a hole in
  the middle of the stream, not the partial last line a byte-capped read routinely ends on (that case
  is already reported by `truncated` for its own reason, and is not double-counted). Note the
  deliberate asymmetry with the previous bullet: a line this server cannot *parse* is skipped and
  flagged (nothing about it is knowable — not its type, not its step, not whether it is one event or
  half of one — so minting a marker for it would fabricate an event), while an event it can parse but
  cannot *reproduce* becomes a visible marker rather than a hole.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1001` | The run's events path is a UNC/network location, or (when a workspace is configured) resolves outside its root. | false |
  | `VFX-E-1004` | The run is in the registry but its events file no longer exists. | false |
  | `VFX-E-1005` | The events file exists but could not be read. | false |
  | `VFX-E-1006` | An argument failed validation — a missing `runId`, an out-of-range `limit`, an over-long or over-numerous filter value. | false |
  | `VFX-E-1505` | No run with that `runId` is in the run registry. | false |
  | `VFX-E-1506` | The `cursor` could not be verified — malformed, from a different build or tool, or issued under different filters. | false |

### get_run_status

Returns one run's current lifecycle state from the persisted run registry. Use it to poll a run you
started, to re-find a run after your own process restarted, or to turn a `runId` into the
`eventsFilePath` the diagnosis tools read. Never spawns the engine CLI, and never takes the run lock,
so it is safe to call while a run is in flight.

- **Parameters**:
  - `runId` (string, **required**) — the run to report on, as returned on `run_suite`'s own result or
    by `list_runs`. Resolved through the run registry, which spans server restarts when the server was
    launched with `--workspace` and is session-scoped otherwise.
- **Result shape**: `{ run }` (plus the shared `meta` object every successful result carries), where
  `run` is the registry's own record:
  - `runId` — this server's id for the run (`run-` followed by 32 hex characters). Note this is **this
    server's** id; the `runId` field *inside* the engine's own events is a different, bare-hex value.
  - `status` — `running`, `completed`, or `cancelled`. `cancelled` appears only for a run stopped by
    `cancel_run`; a run stopped by its own `timeoutSeconds` budget or by the MCP caller cancelling the
    call is `completed` with an `Inconclusive` outcome, which is what those have always reported.
  - `outcome` — the run's overall verdict (`Pass` / `Fail` / `EnvironmentError` / `Inconclusive`), or
    `null` while it is still running. These are the **response** strings, not the engine's wire tokens
    that `get_run_events` relays.
  - `startedAt` / `finishedAt` — UTC timestamps; `finishedAt` is `null` until the run reaches a
    terminal status.
  - `specPaths` — every suite this run covered, in the order they ran (a multi-path or glob
    `run_suite` call is **one** run with several entries here).
  - `eventsFilePath` — the single JSON Lines event stream for the whole run. Hand it to `explain_run`,
    or hand the `runId` to `get_run_events`.
  - `labels` — the labels `run_suite` recorded, verbatim; `{}` when none were sent. This is the only
    place a run's labels are readable, and what `list_runs`' `label` filter matches against.
- **This is the registry's record, not a second status model.** `explain_run`, `diagnose_run` and
  `get_run_events` resolve a `runId` through the same entry, so this tool can never disagree with them
  about a run's state or about where its events live.
- **Per-step detail is deliberately not here.** Steps, attempts, observations and environment detail
  live in the events file, because the registry stores run *metadata* only and never copies event
  content into persistent storage. Call `explain_run` for a diagnosis, or `get_run_events` for the raw
  stream.
- **A `running` status is the last recorded state, not a liveness check.** A server killed outright
  (`SIGKILL`, `TerminateProcess`, lost power) never writes its completing update, so its entry reads
  `running` permanently and there is no reaper. This tool reports it as the registry has it —
  deliberately, because establishing liveness means acquiring the workspace run lock, and a read-only
  tool that took that lock could make a concurrent `run_suite` call fail. **`cancel_run` is what
  settles the question**: it answers `already_finished`, `cancelled`, `VFX-E-1507` (this server is not
  holding it and the lock did not prove it residue — the message says which of the three possible
  reasons applies) or `VFX-E-1508` (nothing is running it — the entry is residue).
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | `runId` was missing, blank, or over 2000 characters. | false |
  | `VFX-E-1505` | No run with that `runId` is in the run registry. | false |

### cancel_run

Asks a run that is still in flight to stop, and reports what could be done about it. Unlike every
other tool in this section, `cancel_run` is **not read-only** — it exists to change a run's lifecycle
— though it deletes nothing and overwrites nothing. (It is not quite "writes nothing" either: its
liveness probe goes through the workspace run lock, which creates the output directory and an empty
`<outputDir>/.lock` if they do not exist yet. Both are this server's own inert artefacts — the lock
file carries no payload by construction — and the next `run_suite` call would have created them
anyway. Nothing of yours is touched.)

- **Parameters**:
  - `runId` (string, **required**) — the run to stop.
  - `reason` (string, optional) — free-form context for why. Held in this server's memory for the
    operator's benefit only: it is **never persisted, never echoed back, and never appears in any
    other tool's result**. Bounded at 2000 characters.
- **Result shape**: `{ runId, status }` (plus the shared `meta` object), where `status` is one of:
  - `"cancelled"` — the run was in flight in this server process and has been **asked** to stop. This
    call does not wait for the stop to complete: the engine is given its full grace period first (see
    below), so blocking here would hold the MCP request open for tens of seconds. To observe the
    terminal state, poll `get_run_status` until the status is **terminal** — `completed` **or**
    `cancelled` — never until it reads `cancelled` specifically. Two windows make the narrower
    condition non-terminating: a cancellation delivered in the instant the run was already composing
    its completing record is signalled honestly and still ends the run as `completed`; and a
    completing write that fails outright (announced on the server's stderr) leaves the entry at
    `running` with no reaper to clear it.
  - `"already_finished"` — the run had already reached a terminal status. **`isError` is `false`** —
    this is a normal answer, not a failure. A polling host loses this race routinely.
- **The stop is the same one `run_suite` performs, not a second path.** Cancellation fires the very
  cancellation token the run is already executing under, so what happens next is exactly what happens
  when a `run_suite` call is cancelled: the engine's stdin is closed (its `--shutdown-on-stdin-eof`
  cue to shut down cleanly and tear its containers down), it is given ~35 s of grace, and only then is
  the process tree killed. There is deliberately no separate kill path here to drift from that one.
- **Cancelling a test is never reported as a test failure.** The four-verdict taxonomy is preserved.
  What changes is the run's **status** (`cancelled` rather than
  `completed`); its **outcome** remains whatever the run genuinely reached. For the ordinary case — a
  run cancelled before any suite failed — that is `Inconclusive`. For a multi-suite run where an
  earlier suite had already produced a real `Fail`, the outcome stays `Fail`: overwriting it would
  discard a verdict the engine demonstrably produced, and the `cancelled` status already records that
  the run was cut short.
- **Cancellation is same-process only, and says so rather than pretending.** A run held by a
  *different* server process against the same workspace cannot be signalled from here: runs are
  serialized by a `FileShare.None` file handle at `<outputDir>/.lock`, and the exclusivity that makes
  it a lock is the same thing that stops one process sending anything through it. Rather than
  inventing a side-channel — a second cancellation path competing with the graceful stop above — the
  call is refused by name with `VFX-E-1507`. You are never told a run was cancelled when nothing was
  signalled.
- **`VFX-E-1507`'s message is specific about what it actually established.** The lock answers per
  *workspace*, not per *run*, so "the lock is held" never on its own proves another process is
  running the run you named. The message distinguishes three cases: another server process is running
  it (cancel it from there); the run has already finished here and its completing record was lost or
  is being written right now (ask again); or **this** server is busy with a *different* run, whose
  lock masks the probe entirely (`list_runs`, then cancel that `runId` — or wait and ask again).
- **It is also how you identify a phantom `running` entry.** When the entry says `running` but that
  lock is **free**, no process is running it and the entry is residue — from a server killed mid-run,
  or from a run whose completing registry write failed; that is `VFX-E-1508`. Reading the lock means briefly acquiring it, which is why only this
  (non-read-only) tool does it — and why, for the microseconds it holds the probe, a `run_suite` call
  starting at exactly that instant can be rejected with retryable `VFX-E-1501`. The probe is reached
  only when a `running` entry exists that this process is not holding, so it never runs during an
  ordinary run.
- **Error codes** — note that `already_finished` is **not** among them:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | `runId` was missing, blank, or over 2000 characters; or `reason` was over 2000 characters. | false |
  | `VFX-E-1505` | No run with that `runId` is in the run registry. | false |
  | `VFX-E-1507` | The entry says `running` but this server is not holding it, and the run lock did not prove it residue — another process has it, its completing record was lost, or this server's own different run masks the probe. | true |
  | `VFX-E-1508` | The entry says `running` but the workspace's run lock is free: nothing is running it, and the entry is residue. | false |

### list_runs

Lists the runs in the run registry, newest first, one page at a time. Use it to find a `runId` you no
longer hold, to see what has run recently, or to correlate runs by the labels `run_suite` recorded.
Never spawns the engine CLI, and never takes the run lock.

- **Parameters**:
  - `limit` (integer, optional) — maximum runs to return; **default 200, maximum 2000** (spec §4.5).
    An out-of-range value is **refused** (`VFX-E-1006`), never silently clamped.
  - `cursor` (string, optional) — a `nextCursor` from a previous call, passed back unchanged.
  - `label` (string, optional) — `key=value` matches runs carrying that key with exactly that value;
    a bare `key` matches runs carrying that key whatever its value. Both halves are matched **exactly**
    — there is no wildcard, prefix or substring matching, because a label is correlated by a machine
    rather than searched by a human.
  - `since` (string, optional) — an ISO-8601 timestamp; only runs whose `startedAt` is at or after it.
    A value carrying no offset is read as **UTC**, never as the server's local zone.
- **Result shape**: `{ runs, nextCursor?, truncated }` (plus the shared `meta` object). Each entry
  carries exactly five fields — `runId`, `status`, `outcome`, `startedAt`, `finishedAt` — with the same
  meanings `get_run_status` documents above. Spec paths, the events file and labels are deliberately
  **not** here: call `get_run_status` for one run's full record.
  - `truncated`: `true` when the registry scan behind this page stopped at its 10,000-run bound, so the
    workspace may hold runs this walk can never reach (see below). **Read it together with
    `nextCursor`, never instead of it** — the same rule `get_run_events` states for its own identically
    named field: `nextCursor` says more matching runs remain within what was scanned, `truncated` says
    what was scanned is not everything. It describes the *scan*, not the page, so every page of a
    capped walk reports `true`. Always `false` without `--workspace`, where the registry is in memory
    and has no scan to cap.
- **Paging is a snapshot, and its position is a timestamp.** The cursor resumes at "the first run
  started strictly before the last one you were given", not at an index — so runs started *during* a
  page walk (which always land at the head of a newest-first list) can never shift the page under you
  or cause a row to be served twice. The trade is stated rather than hidden: those newer runs are not
  inserted into the walk in progress. Start a fresh walk to see them.
- **`nextCursor` is opaque and single-purpose**, exactly as `get_run_events`' is. Pass it back
  unchanged; do not construct, parse, or edit it, and do not pass a `get_run_events` cursor here or
  vice versa (that is refused, not misread). It is bound to the `label` and `since` it was issued
  under and is refused (`VFX-E-1506`) if either changes — `limit` may change freely between pages. **It
  is absent, not null, on the last page**: the server looks one matching run ahead before minting it.
- **A very large workspace is listed from a bounded slice, and paging one is slow.** The registry
  examines at most 10,000 run directories per read, in the filesystem's own enumeration order — i.e.
  before the newest-first sort — so a workspace holding more than that many runs is paged over an
  arbitrary 10,000-run subset, and successive pages need not come from the same subset. Every page
  re-scans the whole registry, so a walk costs pages × scan. Measured (Windows 11, .NET 8, warm cache):
  1,000 runs page in 132 ms and walk fully in **0.7 s**; 10,000 runs page in 1.4 s and walk fully in
  **70 s**; at 12,000 runs the read still returns 10,000 and the walk still yields 10,000 rows, so
  2,000 runs are unreachable — but **not silently**: every page of such a walk reports
  `truncated: true`, sourced from the registry's own scan rather than guessed from the row count
  (10,000 rows back is otherwise indistinguishable from a workspace holding exactly 10,000 runs).
  There is no retention sweep in this release; reaching those numbers means the output directory needs
  one.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | `limit` was out of range, `since` was not an ISO-8601 timestamp, or `label` was empty-keyed or over-long. | false |
  | `VFX-E-1506` | The `cursor` could not be verified — malformed, from a different build or tool, or issued under different filters. | false |

### get_step_timeline

Returns **one step's complete attempt timeline** from a finished run — every RETRY poll the engine
recorded for it, with what each attempt observed. Use it when `explain_run` showed a step that retried
and you need the whole history rather than its first few attempts. Never spawns the engine CLI, never
re-runs anything, and never takes the run lock, so it is safe to call while a run is in flight.

- **Parameters** (all three are required):
  - `runId` (string) — the run to read, as `run_suite` returned it or `list_runs` reported it.
    Resolved through the run registry, which spans server restarts when the server was launched with
    `--workspace` and is session-scoped otherwise.
  - `specPath` (string) — the suite the step belongs to. Must be one of the paths this run covered;
    `get_run_status` lists them under `specPaths`. A relative path is resolved against the workspace
    root first, both sides are then normalised to full paths, and the comparison follows the platform's
    own file-name rules — so you do not have to reproduce the registry's exact string. See
    "`specPath` is validated, and only sometimes a filter" below for what it does and does not do.
  - `stepId` (string) — the step whose timeline you want, matched **exactly** and ordinally. Take it
    from `explain_run`'s `notableSteps[].stepId`, or from `get_run_events` with
    `types: ["step-completed"]`.
- **Result shape**: `{ specPath, stepId, verifyMode, timeoutMs, attempts, conclusion, truncated,
  omittedAttemptCount, observedCapped, specPathAttributed }` (plus the shared `meta` object). Each
  entry in `attempts` is `{ n, at, delayMs, tMs, outcome, observed?, error? }`.
- **This tool is immune to `explain_run`'s truncation, and that is why it exists.** `explain_run`
  budgets its whole-run diagnosis in three fixed tiers, and the attempt arrays inside
  `notableSteps[]` are what those tiers shrink first: ten attempts per step at the largest tier, five
  at the next, none at the smallest. A forty-attempt poll loop is therefore cut to its first ten
  before you ever see it. `get_step_timeline` inverts that order — under budget pressure it shortens
  per-attempt **evidence text** and keeps every attempt, saying so with `observedCapped`. Both tools
  read the same parsed event stream, so they can never disagree about what happened; they differ only
  in what they are prepared to drop.
- **`outcome` is this tool's own three-value vocabulary, and it is not the verdict taxonomy.** An
  attempt is:

  | `outcome` | Meaning |
  | --- | --- |
  | `matched` | This poll found what the step was waiting for. |
  | `unmatched` | It did not. **This is the ordinary state of every poll before the last one under `verifyMode: RETRY`, and not a failure** — the engine expects unmatched attempts and keeps polling. |
  | `error` | No match/no-match determination was reached at all: infrastructure prevented the assertion from being evaluated, the engine reported an indeterminate result, no outcome was recorded for the attempt, or the outcome token is one this build does not recognise. The attempt's `error` field says which. |

  These are **not** the `Pass`/`Fail`/`EnvironmentError`/`Inconclusive` verdicts that `run_suite`,
  `explain_run` and `diagnose_run` put on *their* results, and **not** the `PASS`/`FAIL`/`ENV_ERROR`/
  `INCONCLUSIVE` wire tokens `get_run_events` relays. The step's own verdict appears in `conclusion`,
  which is the one field on this payload that legitimately names the four-way taxonomy.
- **`attempts[].at` is populated — but it is not a per-attempt instant.** Every event the engine writes
  carries a `ts` property (a 33-character ISO-8601 instant with offset), `step-attempt` included, and it
  is relayed verbatim. What it is not is a stamp of *when the attempt happened*: the engine buffers its
  event stream and stamps `ts` as it writes the report, so every event in one run tends to carry the
  same handful of values. **Do not difference two `at` values to time anything.**

  **Use `attempts[].tMs` to order and time the timeline.** It is the engine's own figure for how long
  *that attempt* took, in milliseconds, relayed verbatim and named exactly as the engine names it; it is
  additive to spec §5.10's field list for that reason.
- **Two fields are always `null`.** They are reported as explicit nulls rather than omitted, and nothing
  is synthesised to fill them:
  - `attempts[].delayMs` — the backoff before the attempt. The stream carries no inter-attempt delay on
    any event, and consecutive `tMs` values cannot substitute: `tMs` is each attempt's own duration, not
    a running elapsed figure, so differencing two of them is not a backoff.
  - `timeoutMs` — a step's `timeout` **is** on the wire, on the `step-started` event, which this server's
    event parser does not read (it handles `step-attempt`, `step-completed`, `scenario-completed` and
    `environment-error`). So the `null` describes this build rather than the engine's contract. Nothing
    is derived in the meantime: the largest `tMs` observed is the time the step actually took, which
    would be actively misleading under this name. Read the declared timeout from the suite with
    `validate_suite`, or read the raw `step-started` event with `get_run_events`, which relays every
    event type untouched.
- **`verifyMode` describes what this run evidenced, not what the suite declared.** `RETRY` when more
  than one attempt was recorded — only engine-owned polling produces that, so it is a fact. `ONCE` when
  exactly one was: the honest name for "this step was verified once in this run", and deliberately
  **not** a claim that the suite declared `verifyMode: IMMEDIATE`, since a RETRY step that matched on
  its first poll is indistinguishable here. `null` when the stream recorded no attempt at all for the
  step, in which case `attempts` is empty and `conclusion` says so.

  **`null` is the ordinary shape for a step that did not retry, not an edge case.** Measured against the
  pinned engine: an `IMMEDIATE` step emits a `step-started` and a `step-completed` event and **no**
  `step-attempt` event at all, so it arrives here with an empty timeline and a `null` `verifyMode` — a
  normal, successful result. `ONCE` is therefore reported for the narrower population of steps that
  really did record exactly one attempt event, which in practice means a RETRY step that matched on its
  first poll. Read `RETRY` as "this step retried" and treat `ONCE` and `null` alike as "it did not".

  Note that `ONCE` is spec §5.10's token, not the suite language's, whose own values are `IMMEDIATE` and
  `RETRY` — **do not copy it into a suite**.
- **`specPath` is validated, and only sometimes a filter.** A path the run never covered is refused
  (`VFX-E-1509`), so naming the wrong suite is caught rather than answered. What it cannot always do is
  narrow the timeline: a multi-suite `run_suite` call concatenates each suite's stream into the run's
  single events file, and **no line carries a suite attribution**. So:
  - the run covered **one** suite ⇒ every event came from it, `specPathAttributed` is `true`, and the
    attribution is a certainty;
  - the run covered **several** ⇒ `specPathAttributed` is `false`, `conclusion` says so in words, and
    what comes back is the run-wide timeline for that step id. If two of the run's suites declare a
    step with the same id, their attempts are interleaved and cannot be separated.
- **An unknown `stepId` is an error, not an empty timeline** (`VFX-E-1510`). "This step ran and
  recorded no individual attempts" is a real and different state, returned as a **successful** result
  with an empty `attempts` array — if a typo produced the same shape, the two would be
  indistinguishable.
- **Bounds, and all of them are visible.** `observedCapped` is `true` when at least one attempt's
  `observed` text was shortened or dropped to fit the response budget; the attempt itself is still
  present with its `n`, `tMs` and `outcome` intact. `truncated` carries the same meaning it does on
  `get_run_events` — what you got may not be all there is — and is set when the events file exceeded
  the 50 MB read cap, or when the response budget dropped attempts from the end of the list, in which
  case `omittedAttemptCount` says how many. That second path is unreachable for any realistic RETRY
  timeline, and the numbers are measured rather than estimated (Windows 11, .NET 8): forty attempts
  each carrying a 1,000-character observation come back **whole**, at 12,114 bytes against a 32,768-byte
  budget, with the evidence text cut to 200 characters per attempt; carrying no evidence text, **469
  attempts** fit — 47× `explain_run`'s largest tier of ten. An exponential-backoff poll loop against a
  declared step timeout produces attempts in the tens. The bound exists because an events file is
  untrusted input, not because a real one is expected to reach it.
- **Evidence text is relayed, never re-redacted.** `observed` is the engine's own `observation` field
  for that attempt, already redacted at its source (the engine is the sole redaction authority), then
  bounded and control-character-escaped by this server exactly as `explain_run` does it. No
  `${secret:…}` reference is ever resolved.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | `runId`, `specPath` or `stepId` was missing, blank, or over its length bound. | false |
  | `VFX-E-1505` | No run with that `runId` is in the run registry. | false |
  | `VFX-E-1509` | The `specPath` names a suite this run did not cover. | false |
  | `VFX-E-1510` | The run's event stream records no step with that `stepId`. | false |
  | `VFX-E-1001` | The run's recorded events path is a UNC location, or resolves outside the configured workspace. | false |
  | `VFX-E-1004` | The run is in the registry but its events file no longer exists. | false |
  | `VFX-E-1005` | The events file exists but could not be read. | true |

### get_run_artifacts

Reports **what a finished run left behind** — its event-stream artefact, and whatever environment
resources the run's own events named. It reads only the run registry and that run's JSON Lines event
stream: it never re-runs anything, never spawns the engine CLI, and never takes the run lock, so it is
safe to call while another run is in flight.

**This tool is deliberately partial today, and says so on every result.** The engine owns its artifacts
directory and this server is not told where it is; there is no container log access at all. Rather than
return an empty section and leave you to guess, every result carries `partial: true` and a `gaps` array
naming each field this build cannot populate, why, and the upstream ask that would close it
(**U4** — stable run ids, a detached run lifecycle, an artifacts directory, container log access).

- **Parameters**:
  - `runId` (string, **required**) — the run to inspect, as `run_suite` returned it or `list_runs`
    reported it. Resolved through the run registry, which spans server restarts when the server was
    launched with `--workspace` and is session-scoped otherwise.
  - `kind` (string, optional) — which section to return: `"reports"`, `"logs"`, `"environment"`, or
    `"all"`. Omit (or send a blank) for `"all"`. Matched case-insensitively and echoed back in its
    canonical lower-case spelling; any other value is refused with `VFX-E-1006`. **A section you did not
    select is omitted from the result entirely**, not returned empty — which is what lets you tell "I did
    not ask for logs" from "there are no logs".
  - `container` (string, optional, ≤ 256 characters) — which container's logs to tail. **Accepted and
    validated, and it selects nothing today**; it is echoed back on the result, sanitised, so you can
    confirm the server read it.
  - `tailLines` (number, optional, 1–5000, default 200) — how many log lines to tail. **Accepted and
    validated, and it bounds nothing today** (`logs` is always empty). A value outside the range is
    refused with `VFX-E-1006` naming the maximum, **not clamped** — so you never believe you received
    more lines than you did, and the bound you code against now is the bound that will apply once log
    access lands.
- **Result shape**: `{ runId, kind, partial, reports?, logs?, environment?, container, tailLines, gaps }`
  (plus the shared `meta` object).
  - `reports`: `{ events: { path, available, resourceUri } }`. `html` and `junit` are **omitted**, not
    null. `path` is the registry's recorded path *sanitised for display*: identical to
    `get_run_status`'s raw `eventsFilePath` for an ASCII path, and for one containing any other
    character a strictly narrower rendering of it (each becomes a literal `\uXXXX` escape, capped at
    1,000 characters). It is therefore **not openable verbatim** when the path is non-ASCII — use
    `get_run_status` if you need to open the file.
  - `logs`: an array of `{ container, lines, truncated, resourceUri }`. **Always empty** in this build.
  - `environment`: `{ services, dependencies, resources, truncated, omittedResourceCount }`. Each entry
    in `resources` is `{ id, role, health, errorKind, detail?, occurrences, source }`. `id` is `null`
    exactly when the event named no resource, and `source` then reads
    `"environment-error-unnamed"` instead of `"environment-error"` — the failure is still reported, with
    its `errorKind` and `occurrences`, but no identity is invented for it.
  - `gaps`: an array of `{ field, reason, awaits? }`, where `field` is the dotted path of the missing
    field within this payload and `awaits` is the upstream ask (`"U4"`) when one applies.
- **What is actually derivable, and what is not.** The honest inventory, from the two sources this tool
  has:

  | Field | Today | Why |
  | --- | --- | --- |
  | `reports.events` | **Populated** | The run registry mints and records the events file's path. `available` says whether that file still exists. |
  | `reports.html`, `reports.junit` | Omitted | The engine writes its reports where its own flags direct; this server neither passes those flags nor is told the resulting paths. Awaits **U4**. |
  | `logs` | Always `[]` | There is no container log access in this build: no engine flag exposes it and this server never talks to a container runtime. Never an error, and never a fabricated line. Awaits **U4**. |
  | `environment.resources` | Populated **when the run had environment errors** | An `environment-error` event names the resource that failed. That is the only environment identifier the v1 event stream carries. |
  | `environment.services`, `environment.dependencies` | Always `[]` | The event names a resource without saying whether it is a service or a dependency — see below. Awaits **U4**. |
  | `resources[].health` | Always `null` | Live health needs a probe against a running environment, which this server has no channel to make and would not make after the fact. `null` means **not observed**, never "unhealthy". Awaits **U4**. |

- **A healthy run reports no environment resources at all, and that is a correct answer.** Identifiers
  reach this server only on `environment-error` events, so a run in which nothing went wrong has none to
  report. An empty `environment.resources` is not a failure of the tool.
- **Nothing is classified as a service or a dependency, on purpose.** The suite language has both
  (`environment.services` and `environment.dependencies`), and an `environment-error` event carries a
  bare resource name that could be either. Rather than guess, every identifier is reported under the
  additive `resources` array with `role: "unclassified"`, and both spec arrays stay empty. The one
  derivation that *would* classify them — reading the suite file's own `environment:` block — is refused
  for the same reason `get_step_timeline` refuses to read a suite for `verifyMode`: the file on disk today
  is not necessarily the file that ran, so the answer would be an assertion about the run sourced from
  something that is not the run.
- **A resource that failed repeatedly is one entry.** Entries are deduplicated by the id the event
  carried — before any display cap, so two long ids sharing a prefix stay two entries — in
  first-appearance order, keep the **first** event's `errorKind` and `detail` (the failure that started
  the trouble), and carry an `occurrences` count. For the individual events, call `get_run_events` with
  `types: ["environment-error"]`, which relays them raw and paged.
- **A swept events file is reported, not refused.** If the run's stream has since been deleted or cannot
  be read, `reports.events.available` comes back `false` with a matching `gaps` entry, the environment
  section comes back empty with its own, and the other sections still answer. This differs from
  `get_step_timeline` and `get_run_events`, which return `VFX-E-1004`/`VFX-E-1005` for the same
  condition — for those two the file *is* the answer, while here it is one input of three.
- **Bounds, and all of them are visible.** At most 25 distinct environment resources come back per
  result, with each `id`, `errorKind` and `detail` individually capped — and then the **assembled
  payload is measured**, with resources shed from the end until it fits a 32 KB budget. The measurement
  is what guarantees the bound: character caps cannot see the wire encoder, which expands `+`, `<`, `>`,
  `&` and `"` sixfold, so a stream made of those characters is over budget at 25 capped entries (measured:
  64,830 B, shed to 12 entries at 31,747 B). `environment.truncated` and
  `environment.omittedResourceCount` say when the resource list, the shed, or the 50 MB events-file read
  cap cut something short, and the omission count is against every distinct resource the parse saw.
  Nothing is dropped silently.
- **Relayed text is never re-redacted.** A resource's `id`, `errorKind` and `detail` are the engine's own
  event fields, already redacted at their source, then bounded and control-character-escaped by this
  server. No `${secret:…}` reference is ever resolved, and the `environment` section means the **run's**
  environment as its events described it — never this server process's.
- **Error codes**:

  | Code | Meaning | `retryable` |
  | --- | --- | --- |
  | `VFX-E-1006` | `runId` was missing or over its length bound, `kind` was not one of the four accepted values, `container` was over 256 characters, or `tailLines` was outside 1–5000. | false |
  | `VFX-E-1505` | No run with that `runId` is in the run registry. | false |
  | `VFX-E-1001` | The run's recorded events path is a UNC location, or resolves outside the configured workspace. | false |

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
