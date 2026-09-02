# vouchfx.ai MCP Surface — Implementation Plan for `vouchfx-mcp`

**Derived from:** `docs/vouchfx-ai-mcp-spec.md` (v0.1)
**Scope of this plan:** the `vouchfx-mcp` repository only, plus precisely-specified upstream asks against the engine.
**Status:** proposal — awaiting approval before execution.

---

## Context

The spec asks for an AI/agent layer over vouchfx: a complete MCP tool surface plus three agents
(Author, Healer, Impact) on Microsoft Agent Framework.

**The spec was written for a repository that is not this one.** It assumes the `vouchfx` engine
monorepo — `Vouchfx.Core`, `Vouchfx.Providers.*`, `Vouchfx.Cli`, an in-process Roslyn compiler, a
reflective provider registry, a collectible `AssemblyLoadContext`, and a memory-leak regression gate.

This repository is the **MCP satellite**. It:

- never builds or embeds the engine — it spawns the *published* `vouchfx` dotnet tool as a subprocess;
- is pinned to `ENGINE_PIN` = **v1.0.0-rc.4** (`be12ebd1…`) with a fail-closed version handshake;
- vendors byte-exact, CI-drift-gated copies of the engine's schema and docs (`vendored/`);
- is governed by one invariant: **CLI and MCP must not drift — never invent engine logic in-process**;
- is strictly **read-only** — it never writes, modifies or deletes a suite file;
- **never hosts a model** (REQ-010);
- targets .NET 8, `TreatWarningsAsErrors`, MCP SDK `ModelContextProtocol` **1.4.1**;
- ships **nine tools** and **two resources**, feature-complete for its current scope;
- is packaged as the `Vouchfx.Mcp` dotnet tool at `0.1.0-alpha.1` and **has never been published to
  NuGet.org**.

That last fact is the single most valuable scheduling lever in this plan: **every breaking contract
change is free right now and expensive after the first tagged release.** Contract-shaping work is
therefore sequenced first.

Spec ground rule 1 (*"the codebase wins on naming"*) and ground rule 5 (*"ask when the repo
contradicts the spec semantically"*) are applied throughout.

### Intended outcome

An MCP host (Claude Code, Copilot, Cursor, VS Code agent mode) can author, validate, run, inspect and
maintain vouchfx suites through this server alone, with a stable versioned contract, a real error
taxonomy, an addressable run lifecycle, and prompts that encode the method — while the server stays
model-free, read-only and pin-honest.

---

## Locked decisions

| # | Decision | Consequence |
|---|----------|-------------|
| **D1** | **Scope = this repo + upstream asks.** | The MCP surface (§4–§7) is built here. Topology, Impact and compile land as *engine CLI* capabilities this server relays. `Vouchfx.Ai` is planned as a separate repo, not built here. |
| **D2** | **Healer = superset.** | `diagnose_run` keeps Fail-only *review* proposals ("here is the defect — do not rewrite the assertion") **and gains** actionable spec-edit proposals for `EnvironmentError` / `Inconclusive`. Still never auto-applied. Satisfies spec P5's intent without regressing shipped behaviour. |
| **D3** | **No `write_spec`.** | The read-only invariant stands. Spec §5.14 is replaced by **`normalize_suite`**, which returns canonical YAML + validation; the host writes the file with its own tools. The Author loop still closes. |
| **D4** | **Repo tool names win.** | No `vouchfx.` prefix, no `*_spec` / `*_scenario` renames. New tools follow the existing verb/noun style. Spec semantics adopted; spec identifiers discarded. |

---

## 1. Inventory — spec concept vs. repository reality

This table *is* the spec's M0 deliverable, discharged. "Verdict" drives the rest of the plan.

| Spec | Exists here today | Verdict |
|------|-------------------|---------|
| §5.1 `get_schema` | `Validation/LiveSchemaDocument.cs` is **fully implemented but never constructed** by `VouchfxMcpServerRegistration` — a finished `vouchfx schema` loader awaiting a consumer; `vendored/composed-schema.v1.json` is embedded (`Vouchfx.Mcp.Vendored.composed-schema.v1.json`) | **NEW TOOL — cheapest item in the plan.** Both the live and offline loaders exist; only the tool that calls them is missing. |
| §5.2 `list_providers` | `list_step_types` + `describe_step_type` (live `vouchfx list --json`) | **EXTEND, don't merge** (D4). Missing: `tier`, `vouched`, `requiredResources`, `supportsVerifyMode`, `example`, `docsUrl`. Most need **U5**. |
| §5.3 `get_topology` | nothing | **UPSTREAM (U1) + relay tool.** No source-scanning exists; building it here would violate the no-drift invariant and duplicate what spec M2 itself calls a `vouchfx topology` CLI command. |
| §5.4 `suggest_scenarios` | `plan_coverage` (engine `plan --json`) + `scaffold_suite` (engine `scaffold --intent`) | **DROP as a distinct tool — already satisfied, better.** Coverage-gap-driven beats flow-skeleton-driven, and both are engine-delegated. Enrich `plan_coverage` findings once **U1** lands. |
| §5.5 `validate_spec` | `validate_suite` — schema only, `path` only, process-isolated worker | **REFACTOR (largest single item).** Add inline `yaml`, `level`, semantic rules, `normalizedYaml`, `summary`. |
| §5.6 `compile_spec` | nothing; **no `compile` subcommand on the pinned CLI** | **BLOCKED → deferred.** Requires **U3**. The spec's in-proc Roslyn + `Unload()` requirement is structurally impossible here. |
| §5.7 `run_scenario` | `run_suite` — blocking, single path, in-proc single-flight | **REFACTOR.** Add multi-path/glob, `labels`, `keepEnvironment`, `wait:false`, a durable `runId`. Async needs **U4**. |
| §5.8 `get_run_status` / `cancel_run` / `list_runs` | `Run/ILastRunTracker.cs` — session-scoped last run only | **NEW.** Needs a persisted run registry under `outputDir`. |
| §5.9 `get_verdict` | `explain_run` (verdict + `categoryMeaning` + notable steps) | **REFACTOR.** Add `reason.kind` and `classificationHints` (spec §8.3 rule table). |
| §5.10 `get_step_timeline` | attempts nested in `explain_run.notableSteps[].attempts` | **EXTRACT** into a focused tool — avoids the 64 KB tiering swallowing the timeline. |
| §5.11 `get_run_events` | `Run/EventsFileReader.cs` (internal, 50 MB cap) | **NEW.** Expose paged raw events with a cursor. |
| §5.12 `get_run_artifacts` | nothing | **PARTIAL + UPSTREAM (U4).** Reports/logs directories are engine-owned. |
| §5.13 `impact_of_contract_change` | nothing | **UPSTREAM (U2) + relay tool.** Same reasoning as §5.3; spec M2 also expects `vouchfx impact --base`. |
| §5.14 `write_spec` | **nothing — read-only by design** | **DROPPED (D3)** → `normalize_suite`. |
| §5.15 `explain_diagnostic` | nothing; error kinds are ad-hoc strings (`file-not-found`, `suite-invalid`, …) | **NEW + catalogue.** Depends on the error taxonomy (§2.2). |
| §6 Resources | 2 static: `vouchfx-docs:///language-reference`, `vouchfx-docs:///recipes` | **EXTEND.** Add schema, errors, examples, workspace, run resources + **templates** (`resources/templates/list` is not advertised today). |
| §7 Prompts | **none** | **NEW.** Highest value-per-line in the whole plan. |
| §8.1 / §8.2 Analysis libs | none | **UPSTREAM** (U1 / U2). |
| §8.3 `Analysis.Verdicts` | **already here** — `Run/SuiteEventParser.cs`, `Diagnosis/*` | **EXTEND** with the hint rule table. This is a *reader* of a schema-versioned stream, not engine re-implementation — no drift risk. |
| §8.4 `Analysis.Authoring` | partial — secret-hygiene tests exist; no semantic rules, no normalizer | **BUILD HERE.** New capability with no engine counterpart ⇒ no drift risk. Keep semantic diagnostics in a **separate result channel** from schema errors so the measured engine-agreement contract for schema errors stays untouched. |
| §9 `Vouchfx.Ai` | none | **OUT OF SCOPE (D1)** — separate repo. |
| §9.6 GitHub Action | none | **OUT OF SCOPE (D1).** |

---

## 2. Cross-cutting refactors (do these first — they touch all nine tools)

### 2.1 Workspace model — spec §4.2

Today every tool takes an absolute-or-relative path guarded by `Validation/PathSafetyGuard.cs`
(which rejects UNC paths). There is no workspace concept.

- Add `Workspace { root, specsDir, outputDir, configPath }`, resolved at server start from
  `--workspace <path>` with a per-call override; defaults `specsDir = <root>/e2e`,
  `outputDir = <root>/.vouchfx/runs`.
- Extend `PathSafetyGuard` with canonicalised containment (symlink-resolved, no `..` escape) →
  `VFX-E-1001 PathOutsideWorkspace`. Keep the existing UNC rejection (its threat model is credential
  leakage: on Windows, reading `\\host\share\file` triggers an outbound SMB + NTLM auth).
- ⚠ **This changes a deliberate decision.** `PathSafetyGuard` today **allows local `../` traversal on
  purpose** — the containment rule is new policy, not a bug fix. Gate it on workspace-configured-ness
  so existing absolute-path, single-file usage keeps working, and land it as an explicit,
  separately-reviewed behaviour change.

### 2.2 Error & diagnostic taxonomy — spec §4.4

Adopt `VfxError { code, message, details?, docsUrl?, retryable }` with the spec's reserved ranges,
and `Diagnostic { code, severity, message, location?, path?, fix?, docsUrl? }`.

- Map every existing ad-hoc `kind` string onto a `VFX-E-####` / `VFX-D-####` code. This is the
  breaking change that must land pre-release.
- Preserve the spec's most important rule: **diagnostics are data, not errors.** Two existing
  precedents already encode it and must not regress: `plan_coverage` never passes `--fail-on-gap`
  (a gap is the product), and `run_suite` returns `RunSuiteInvalidPayload { kind: "suite-invalid" }`
  through `StructuredToolResult.Success`, *not* `Error` — clients keying off `isError` do not see it.
- ⚠ **Two verdict vocabularies exist and must not be conflated.** The engine's wire tokens are
  `PASS` / `FAIL` / `ENV_ERROR` / `INCONCLUSIVE` (`Run/RunVerdict.cs`, `ParseWireToken`); the strings
  this server puts on MCP responses are the enum names `Pass` / `Fail` / `EnvironmentError` /
  `Inconclusive`. Elevation rank is `Pass < Inconclusive < Fail < EnvironmentError`. New tools must
  emit the response vocabulary and parse the wire vocabulary tolerantly (unknown token → `null`,
  never throw).
- New `docs/errors/VFX-*.md` catalogue, embedded as resources, powering `explain_diagnostic`,
  `docsUrl` everywhere, and a `vouchfx-docs:///errors/{code}` resource.

### 2.3 `ToolMeta` on every result — spec §5 shared types

`{ schemaVersion, serverVersion, workspaceRoot }` on all nine existing and all new tools.
`ServerIdentity.cs` and `EnginePin.cs` already hold the inputs.

### 2.4 Limits & pagination — spec §4.5

The repo has caps (64 KB response envelope, 50 MB reads, capped `search_docs` matches) but no
`limit` / `cursor` / `nextCursor`. Add opaque cursors to every list-returning tool. Keep the existing
degradation tiers — they are more sophisticated than the spec's flat truncation — and add the
`resourceUri` escape hatch the spec requires.

⚠ **The payload budget is 32 KB, not 64 KB.** `Tools/StructuredToolResult.Success` serialises every
payload **twice** — once as a `TextContentBlock` and once as `StructuredContent` — so
`ExplainRunOrchestrator` budgets against `EffectiveDiagnosisBudgetBytes = 64 KB / 2`. Every field
added anywhere in this plan costs double on the wire. Concretely:

- `ExplainRunOrchestrator` has **three fixed degradation tiers** `(notableSteps, stepObsChars,
  attempts, attemptObsChars)` = `(10, 2000, 10, 500)` → `(5, 300, 5, 100)` → `(3, 0, 0, 0)`, the last
  a guaranteed floor carrying no evidence text at all.
- `DiagnoseRunOrchestrator` adds **four progressive shrink stages** on top (full → patches elided →
  rationales elided → proposals and guidance dropped entirely).

P4's `classificationHints` and the D2 Healer proposals must extend both ladders, and every tier must
stay *measured by serialising it*, never assumed to fit. This is also the strongest argument for
extracting `get_step_timeline` (P3) rather than growing `explain_run`: today a long RETRY timeline is
the first thing the tiers throw away.

### 2.5 Concurrency — spec §4.6

`run_suite` already single-flights and rejects rather than queues. Change: return
`VFX-E-1501 RunInProgress` **including the active `runId`** with `retryable: true`. A cross-process
file lock at `<outputDir>/.lock` only becomes meaningful alongside the run registry (P3).

### 2.6 Observability — spec §4.9

No OpenTelemetry package is referenced today and `Log.cs` carries exactly one message. Add OTel spans
`vouchfx.mcp.tool/<name>` with `workspace.hash`, `runId`, duration, outcome — **no spec contents or
logs in span attributes**. Additive; deferred to P7.

### 2.7 Invariants that must survive every refactor

Non-negotiable, each already tested — treat a regression as a blocker:

1. **ENGINE_PIN fail-closed handshake** on every CLI-backed tool. Only `Ok` is cached; all three
   failure modes are re-probed each call so a user can install or upgrade without restarting.
2. **Process-isolated validation worker** (10 s wall clock, 50 MB stream caps, whole-tree kill with
   *confirmed* exit). The reason is not tidiness: a tiny well-formed YAML input can drive YamlDotNet's
   `Scanner` into an uninterruptible ~100 %-CPU spin that no `CancellationToken` can recover from.
3. **YAML-bomb defences** (5 MB size / depth 64 / 10 anchors / 10 aliases) evaluated *before* any
   recursive parse — a native `StackOverflowException` is uncatchable.
4. **Secret hygiene, enforced by a fail-closed source guard.** `SecretHygieneSourceGuardTests` regex-
   scans `src/` for `.Environment[` / `.EnvironmentVariables` mutation **and derives the set of
   process-spawn sites from source, asserting it equals a hard-coded guarded list**. ⇒ **Any new
   process spawn site — including a topology or impact relay (P6) — fails CI until it is explicitly
   registered.** Children must inherit the environment implicitly and unmodified; this server never
   builds, mutates, or even filters it, and is a relay, never a redaction authority.
5. **Read-only:** no suite file is ever written (D3).
6. **No model hosted in this server** (REQ-010).
7. **Graceful teardown.** `run` is launched with `--shutdown-on-stdin-eof` and is the *one* process
   boundary that deliberately keeps the child's stdin **open**, so cancellation can request a clean
   engine stop by closing it; force-kill is only the backstop after the ~35 s grace. Paired with the
   live drill (`docs/validation/graceful-teardown-drill.md`).
8. **Vendored artefact drift gate** (`scripts/sync-vendored.ps1 -Verify`, byte-exact SHA-256).
   `-Update` is the only supported refresh path — regenerating the schema from `vouchfx schema`
   produces CRLF and trips the gate despite identical text.
9. **Taxonomy fidelity** — the four verdicts are never conflated; cancelled or timed-out is
   `Inconclusive`, never `Fail`; an unrecognised CLI exit code classifies as `EnvironmentError`,
   never `Fail`.
10. **Packaging:** the CI `pack` job asserts `ENGINE_PIN` ships beside the entry DLL — a globally
    installed tool resolves `AppContext.BaseDirectory` to the tool store, so a missing pin makes
    every real install fail fatally at startup.
11. **stdout is the JSON-RPC channel exclusively** — all logging goes to stderr, proven against the
    real built binary by `RealServerProcessTests` (byte-empty stdout), which the in-memory harness
    cannot check.

---

## 3. Work packages

### P0 — Foundations (no user-visible change)

Publish this inventory as `docs/IMPLEMENTATION-MAP.md`; add the `VFX-*` code catalogue skeleton;
introduce `ToolMeta`, `VfxError`, `Diagnostic` records with source-generated JSON contexts.

**Acceptance:** all nine tools carry `meta`; every existing error kind maps to a code; suite green.

### P1 — Error taxonomy + `explain_diagnostic` *(breaking — must precede the first release)*

Migrate all nine tools to `VfxError` / `Diagnostic`; write `docs/errors/VFX-*.md`; add
`explain_diagnostic`; serve `vouchfx-docs:///errors/{code}`.

**Acceptance:** golden contract test per tool for both success and error shapes; every emitted code
has a catalogue entry, test-enforced in both directions (no orphan codes, no orphan docs).

### P2 — Authoring quality *(the spec's own "invest here")*

- **`get_schema`** — `section` + `format: json-schema|summary`. Summary generated from schema
  annotations; offline from `vendored/`, live-verified against `vouchfx schema`.
- **`validate_suite` v2** — inline `yaml` input, `level: schema|semantic|full`, a `summary` block,
  and semantic diagnostics **VFX-D-1201…1211** in a channel separate from schema errors.
  VFX-D-1201 (unknown step type) already exists as the `unknown-step-type` kind — map it, do not
  duplicate. VFX-D-1210 (topology cross-check) is gated on **U1**; ship it disabled by default.
  ⚠ `Validation/SuiteValidator.cs` is ~1 450 lines, most of it five hard-won **noise-suppression
  passes** (if/then discriminator noise, composite roll-ups, `unevaluatedProperties` cascade after
  rc.4 closed `$defs/step`, forbidden containers, const-vs-enum). Semantic rules must be an
  **additional pass after** that pipeline, never woven into it — and the single-parse discipline
  must hold (a re-parse per error was measured at 31.9 s on a 2 000-error suite).
- **`normalize_suite`** (replaces `write_spec`, D3) — canonical YAML plus validation; the host writes
  the file. Comment preservation is spec open decision #2: prefer a comment-preserving
  implementation, or — if impractical on YamlDotNet 18.1 — document the loss and default it off.
- **Catalogue enrichment** — add the §5.2 fields; those needing engine data are **U5**.

**Acceptance:** one test per diagnostic code; normalizer idempotence
(`normalize(normalize(x)) == normalize(x)`); secret-literal corpus; and the measured schema-error
agreement with the engine's 55-fixture rejected corpus **unchanged**.

### P3 — Run lifecycle

A persisted run registry under `<outputDir>`, replacing session-only `ILastRunTracker`. Then
`run_suite` gains multi-path/glob, `labels`, `keepEnvironment` and `wait`; and new `get_run_status`,
`cancel_run`, `list_runs`, `get_run_events`, `get_step_timeline`, `get_run_artifacts`.
Async (`wait:false`) and artifacts depend on **U4** — ship blocking-only with a durable `runId` first
so the registry lands independently of the engine.

**Acceptance:** registry survives a server restart; the concurrency rejection carries the active
`runId`; cursor pagination over a large events fixture; a cancelled run still reports `Inconclusive`.

### P4 — Verdict intelligence + Healer superset (D2)

Add the §8.3 `classificationHints` rule table and a structured `reason.kind` to `explain_run`; extend
`diagnose_run` with spec-edit proposals for `EnvironmentError` / `Inconclusive` scoped to environment
declarations, timeouts, match keys and capture paths — alongside the existing Fail-only review
proposals. Never auto-applied.

**Acceptance:** an event-stream fixture per `reason.kind`; hint snapshot tests; explicit tests that a
`Fail` never yields an assertion-weakening proposal and that `EnvironmentError` never yields a YAML
rewrite outside the allowed scopes.

### P5 — Resources, prompts, and the host-facing method

- Advertise resource templates via `resources/templates/list`; add `vouchfx://schema/{version}`,
  `…/docs/errors/{code}`, `…/examples/{name}`, `…/workspace/specs`, `…/runs/{runId}/…`.
  Keep the existing `vouchfx-docs:///` URIs working (D4).
- **MCP prompts** — `author_scenario`, `heal_run`, `review_spec`, `explain_failure` as
  `Prompts/*.md` with front-matter arguments, unit-tested to render.
- `docs/dsl-guide-for-agents.md` (≤ 20 KB, written for a model reader) and `SKILL.md` for Claude Code.

**Acceptance:** the spec's M4 test — *Claude Code authors a passing scenario for a sample app using
only this server* — executed and recorded.

### P6 — Topology & Impact relays *(gated on U1 / U2)*

Thin, drift-free relay tools `get_topology` and `impact_of_change`, mirroring exactly how
`plan_coverage` relays `vouchfx plan --json`: fail closed on a CLI lacking the subcommand, never
compute locally. Enable VFX-D-1210 and enrich `plan_coverage` once live.

### P7 — Hardening

OTel spans; structured JSON server logs correlated with the run stream; HTTP transport behind a flag
with bearer auth; the MCP Tasks extension behind a flag *if* SDK 1.4.1 supports it (verify — do not
assume); first tagged release and NuGet publication.

---

## 4. Upstream asks (engine repo — blockers, not this repo's work)

Each lands as an engine CLI subcommand with `--json`, then an `ENGINE_PIN` bump here, a
`sync-vendored.ps1 -Update` resync, and a **mandatory** graceful-teardown drill.

| # | Ask | Unblocks |
|---|-----|----------|
| **U1** | `vouchfx topology [--sources …] [--json]` — spec §8.1 | `get_topology`, VFX-D-1210, richer `plan_coverage` |
| **U2** | `vouchfx impact --base <ref> [--head] [--json]` — spec §8.2 | `impact_of_change` |
| **U3** | `vouchfx compile <path> [--emit-csx] --json` — compile without running, honouring compile-once / `Unload()` | `compile_spec` |
| **U4** | Run lifecycle: stable run ids, detached run plus status/cancel, an artifacts directory (JUnit/HTML/logs), container log access | async `run_suite`, `get_run_artifacts` |
| **U5** | `list --json` adds `tier`, `vouched`, `requiredResources`, `supportsVerifyMode`, `example`, `docsUrl` | catalogue enrichment |
| **U6** | `x-vouchfx-doc` annotations in the composed schema | `get_schema(format:"summary")` quality |

**U3 and U4 are the only hard blockers on the spec's Author loop.** Everything else degrades
gracefully.

---

## 5. Explicitly dropped from the spec (for this repo)

| Dropped | Why |
|---|---|
| §3 package layout (`Vouchfx.Core`, `Vouchfx.Providers.*`, `Vouchfx.Cli`, `Vouchfx.Analysis.*` as core-NuGet siblings) | Wrong repository. |
| §5.6 `compile_spec` as in-proc Roslyn + `AssemblyLoadContext.Unload()` | This server never loads engine code. Reachable only via **U3**, as a CLI relay. |
| §11 memory-leak regression gate row | An engine-repo gate; nothing here loads assemblies. |
| §5.14 `write_spec` | **D3** — the read-only invariant; hosts have their own file tools. |
| §5.4 `suggest_scenarios` | Superseded by `plan_coverage` + `scaffold_suite`, which are engine-delegated and coverage-driven. |
| Merging `list_step_types` + `describe_step_type` into one `list_providers` | **D4** — the codebase wins; two tools is the better model contract (cheap list, expensive detail). |
| The `vouchfx.` tool prefix and all `*_spec` / `*_scenario` renames | **D4**. |
| §9 `Vouchfx.Ai` — Author/Healer/Impact agents, Agent Framework, budget middleware, `agent.*` audit events, `vouchfx.ai.json` | **D1** — separate repo. This server stays model-free (REQ-010); its job is to make that repo's job easy, via the P5 prompts. |
| §9.3 `vouchfx ai author|heal|impact` CLI | Engine CLI surface, not this repo. |
| §9.6 GitHub Action | Belongs with the agent package. |
| §4.1 Streamable HTTP transport | Deferred to P7 behind a flag; stdio is the shipped contract. |
| Hosted `vouchfx-mcp.vouchfx.io` full tool surface | Spec open decision #6 — read-only until HTTP is hardened. |

---

## 6. Documentation surface (updated per work package, never batched)

`README.md` (the "nine tools" count changes) · `docs/overview.md` (same, plus the workflow sections) ·
`docs/tools-and-resources.md` (the field-level contract — largest churn) · `docs/install.md`
(a new engine-minimum section per upstream ask) · `docs/troubleshooting.md` (new error codes) ·
new `docs/errors/VFX-*.md`, `docs/dsl-guide-for-agents.md`, `docs/IMPLEMENTATION-MAP.md`, `SKILL.md` ·
the `ENGINE_PIN` history block on every pin bump · `vendored/README.md` ·
`scripts/build_site.py` and `site/` (the Pages workflow builds the public site from `docs/`).

---

## 7. Testing strategy delta

Existing conventions to follow, all of which extend to every new tool:

- **Mirror-namespace unit tests** (`Cli/`, `Validation/`, `Run/`, `Docs/`, `Diagnosis/`, `Planning/`,
  `Scaffold/`) — one `<Type>Tests.cs` per production type, called directly.
- **`Real*McpTests`** drive the real server end-to-end through
  `Support/McpTestHarness.cs`, which calls the *production* `AddVouchfxMcpServer` — only transport and
  logging differ. `Support/FakeVouchfxCli.cs` + `FakeSuiteRunner` keep CI green with **no `vouchfx`
  CLI and no Docker on the runner**; `McpTestHarness.DefaultTestPin` is deliberately *not* the real
  `ENGINE_PIN`.
- **`Real*ProcessTests`** spawn the real built binary (stdout cleanliness, `--validate-worker`).
- **`Real*AgainstPinnedCliTests`** are the two executable "CLI and MCP must not drift" gates; they
  **self-skip cleanly** when no pinned CLI is installed. Keep that pattern — plus the two guards that
  stop a skip from passing vacuously (`Assert.NotEmpty` on the CLI's own findings, and `Assert.NotEqual`
  on the licensed-wording-gap set so closing a gap forces promotion).
- `ListTools_ReturnsExactlyTheNineAdvertisedTools` **locks the tool count** — every new tool updates it.

New coverage: golden request/response per tool (success **and** error); code-catalogue completeness;
one test per VFX-D-12xx rule; normalizer idempotence; a secret-literal corpus; cursor pagination;
run-registry restart survival; hint snapshots per `reason.kind`; Healer scope-refusal tests;
prompt-render tests; path-containment escape tests.

**Regression guard:** the measured `validate_suite` ↔ `vouchfx validate` schema-error agreement
(33 byte-identical / 13 same-findings-less-enriched / 0 differing) must be re-measured and unchanged
after P2.

---

## 8. Risks

1. **Semantic-validation drift** — VFX-D-12xx has no engine counterpart today; if the engine later
   ships semantic validation, two implementations diverge. *Mitigation:* a separate result channel,
   and propose the rules upstream as a seventh ask once proven.
2. **Contract churn after release** — P1 must land before the first NuGet tag, or the taxonomy
   migration becomes a breaking change for real users.
3. **Upstream latency** — U3/U4 gate the full Author loop. P2/P4/P5 are deliberately sequenced to
   deliver value without them.
4. **Response-size pressure — the sharpest technical constraint in the plan.** `ToolMeta` on every
   result, `classificationHints`, richer diagnostics and Healer proposals all compete for a **32 KB
   effective** payload budget (64 KB envelope ÷ 2 for double serialisation). The existing three tiers
   and four shrink stages must be extended and re-measured, never bypassed. If the budget cannot
   hold, the answer is `resourceUri` hand-off (§2.4), not a raised cap.
5. **Pin bumps are expensive** — each upstream ask costs a vendored resync plus a mandatory live
   teardown drill. Batch U1/U2/U5/U6 into a single bump where possible.

---

## 9. Verification

- Per work package: `dotnet build Vouchfx.Mcp.sln -c Release -warnaserror` (0 warnings),
  `dotnet format Vouchfx.Mcp.sln --verify-no-changes`, `dotnet test Vouchfx.Mcp.sln`,
  `pwsh ./scripts/sync-vendored.ps1 -Verify`.
- End-to-end: register the built tool with Claude Code per `docs/install.md`, then run the P5
  acceptance — author a passing suite for a sample app using only this server's tools and prompts.
- Live: the graceful-teardown drill after any `ENGINE_PIN` change.
- Review gates per `/delegate`: `code-review-gatekeeper` + `security-specialist` per todo,
  `spec-compliance-reviewer` scoped to the items each todo delivers, and `peer-review-critic` as the
  final gate before done.
