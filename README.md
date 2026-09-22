# vouchfx-mcp

A local, stdio-first [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents — with an optional, flag-gated HTTP transport — wrapping
the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) CLI. It advertises eighteen tools to validate
`.e2e.yaml` suites against the JSON Schema, look up the step catalogue and documentation for a given
`<family>.<provider>` type, serve the composed schema as a JSON Schema document or markdown digest, plan a declared
suite set's coverage and gap findings (Planner), scaffold a machine-drafted suite skeleton from structured step
types (Generator), run suites with best-effort progress updates and a taxonomy-faithful verdict, poll, list and
gracefully cancel those runs, diagnose a suite's JSON Lines event stream, return Healer patch proposals
(assertion-level for Fail, scoped spec edits for EnvironmentError/Inconclusive), and explain any of this
server's own diagnostic/error codes — all without the agent having to shell out to
`vouchfx` and parse its output by hand.

## Status

> **Under construction.** This repository is being built spec-first: features land against approved specs in a
> spec → build → review loop, one requirement at a time. All eighteen tools, four MCP prompts, eleven MCP resources — four concrete
> ones (the two vendored documents, the workspace suite index and the DSL guide for agents) and seven URI templates covering six families,
> with error pages served under two schemes — and their embedded content are fully
> functional. The server serves over **stdio by default, with an optional bearer-authenticated HTTP transport** behind
> `--transport http`; every tool call emits **one bounded `ActivitySource` span**, and all of this server's own stderr
> output is **one JSON object per line**. It is feature-complete and packaged as the Vouchfx.Mcp dotnet tool with an OIDC release
> pipeline; what remains are the first tagged release and publication to NuGet.org. A documentation site, in the
> same fleet design as the other vouchfx satellites, covers all of the below in more depth and is live at
> [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/) (built from `scripts/build_site.py`). `validate_suite`
> (accepts file paths or inline YAML, three validation levels, process-isolated, ten semantic rules (eleven codes,
> one reserved) including secret-literal detection, returns suite summary and semantic-diagnostics channel),
> `search_docs` (vendored language reference and recipes), and `explain_diagnostic` (embedded per-code catalogue
> pages) are CLI-free. `normalize_suite` is CLI-free too: it returns a suite's canonical text — key order taken
> from the engine's own JSON Schema, one consistent quoting and block-layout style, with mappings of the author's
> own data (headers, JSON bodies, variables, services, captures) deliberately left in the order they were written
> — alongside the same full `validate_suite` result, always at level `full` so the secret-literal check cannot be
> turned off on text a host may write back. **This server never writes the file**: normalization is opt-in
> (`normalize: true`) because it discards all `#` comments on the pinned YAML library, the result says so per
> document on the payload (`commentsDropped` is true only when your suite really did carry a comment), and the
> canonical text is proved to re-parse to the same document before it is returned — on the rare shape the emitter
> cannot render faithfully you get `normalizedYaml: null` and a `normalizationRefused` reason instead of
> corruption. `get_schema` serves the composed JSON Schema — the whole document or one addressable section — as a
> schema document or markdown digest; it works offline from the embedded schema this server vendors at its pinned
> engine commit, and optionally cross-verifies that schema against the installed CLI's `vouchfx schema` export
> (modelling the console code page, so a divergence reported as a diagnostic on the still-successful result is
> real drift, never a transcoding artefact).
> `list_step_types` and `describe_step_type` load the **live** shape-level catalogue from the pinned engine via
> `vouchfx list --json` (required/optional fields, capture support, family intent — Spec A); they fail fast if the
> CLI is missing, mismatched, or returns only thin type keys without field metadata. `plan_coverage` runs the
> engine's deterministic, read-only coverage-and-gap analysis over a declared suite set, an optional event history,
> and the live step catalogue via the pinned CLI `plan --json` (Spec D M3 Planner) — a call that finds gaps is a
> successful result, never an error, and every gap finding carries a suggested step type/id that feeds
> `scaffold_suite` unchanged.
> Its **response** is size-budgeted; the analysis is not. On a repository
> large enough to produce hundreds of findings, a measured ladder caps the reply at 64 KB of wire
> envelope and keeps the most actionable findings first, never returning more than 150.
> Everything left out is counted — `omittedFindingCount` plus the inventory's own `omitted*` counters,
> with `responseTruncated` set. `maxFindings` asks for fewer still. `ENGINE_PIN` (currently v1.0.0-rc.5) is Planner-capable; the CLI presence/version
> handshake still fails closed if a locally installed CLI is missing or does not match the pin. `scaffold_suite`
> generates a machine-drafted, schema-valid `.e2e.yaml` skeleton from structured step types, ids, and an environment
> outline via the pinned CLI `scaffold --intent` (Spec B Generator) — free text is host-LLM only; this server never
> hosts a model. `ENGINE_PIN` is scaffold-capable; the same handshake fails closed on a missing/mismatched local
> CLI. `run_suite` executes one or more suites through the packaged `vouchfx` CLI: takes `path` (single file) or
> `paths` (array of files/workspace-relative globs) and runs them sequentially under one run ID; verifies the CLI is
> on PATH and matches [`ENGINE_PIN`](ENGINE_PIN) and that every suite validates before spawning anything; reports
> best-effort progress as the run proceeds; returns the taxonomy-faithful verdict (pass / fail / environment error /
> inconclusive) at the run level (the worst of every suite's verdict) and per-suite outcomes in `specs[]`. A
> missing/mismatched CLI or an invalid suite returns a structured result explaining why — naming **every** invalid
> suite it found, bounded and with the omitted counts visible, since one bad file refuses the whole call and a glob's
> caller should not need one round trip per broken file — without attempting to run anything, and a Docker-unavailable or
> timed-out/cancelled run is always reported as an environment error or inconclusive, never as a failure.
> `timeoutSeconds` bounds the whole call from its first filesystem access: glob expansion, per-suite pre-flight, the
> CLI handshake and the run all spend from the one budget. `explain_run` diagnoses a run purely by reading and
> parsing its JSON Lines event stream — never re-running anything — defaulting to the most recent finished run in the
> run registry when no path is given (persists across server restarts when launched with `--workspace`): it reports
> the verdict together with what that category means, names the failing or inconclusive step(s) with their RETRY
> attempt timeline and observation/diff evidence, and always keeps an environment error distinct from a genuine test
> defect. `diagnose_run` (Healer / Spec C) adds two proposal kinds: Fail steps get review-only patch proposals
> (existing); EnvironmentError/Inconclusive outcomes get scoped spec-edit proposals (stepId or null, scope one of
> environment/timeouts/match/capture, YAML fragment never a diff, never auto-applied); both kinds stay within the
> same 32 KB response budget as `explain_run`; infrastructure guidance for EnvironmentError remains unchanged.
> `explain_diagnostic` looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns its title, explanation,
> common causes, and fixes — the same content served by the templated error-page resources, so a host can hand a
> code straight from any `VfxError`/`Diagnostic` to either access path. `get_run_events` hands a host the raw JSON
> Lines events for a run, exactly as the engine wrote them: it takes the `runId` `run_suite` returns on its result,
> filters by event `types` and/or `stepId` **before** paging (so `limit` bounds matching events, not lines scanned),
> and returns a page plus an opaque `nextCursor` to continue with. `limit` defaults to 200 and caps at 2000, and a
> page is additionally bounded by a 32 KB payload budget, so `nextCursor` — not the event count — is what says
> whether the walk is over; `truncated` says separately whether this server saw the whole stream at all. Events use
> the engine's **wire** vocabulary (`PASS`/`FAIL`/`ENV_ERROR`/`INCONCLUSIVE`), never the `Pass`/`Fail`/
> `EnvironmentError`/`Inconclusive` strings other tools' results carry, and unknown event types and fields pass
> through untouched; text is not byte-identical, though — every relayed string is control-character-sanitised
> exactly as `explain_run` sanitises (non-ASCII comes back as a literal `\uXXXX`), and any bound that did apply is
> marked in the event rather than applied silently. It never spawns the CLI and never takes the run lock, so it is
> safe to call while a run is in flight. `get_run_status`, `list_runs` and `cancel_run` are the run-lifecycle trio.
> `get_run_status` returns one run's record straight from the persisted run registry — status (`running`/
> `completed`/`cancelled`), verdict, timestamps, the suites it covered, its events file and its labels — which is
> the same record `explain_run` and `get_run_events` resolve a `runId` through, so the three can never disagree.
> `list_runs` pages that registry newest first, filtered by `label` (`key=value`, or a bare `key` for any value)
> and/or `since`, reusing `get_run_events`' opaque cursor verbatim under its own scope; its position is a `startedAt`
> boundary rather than an index, so runs started mid-walk cannot shift the page under a caller. Both are read-only
> and never take the run lock. `cancel_run` asks an in-flight run to stop through **exactly** the mechanism
> `run_suite` already uses — the engine's stdin is closed for a graceful shutdown and the process tree is killed
> only after the grace period — so there is no second cancellation path; a cancelled run's status becomes `cancelled`
> and its outcome is `Inconclusive`, never `Fail`. Cancelling an already-finished run answers `already_finished` with
> `isError: false`. Cancellation is **same-process only**, and says so rather than pretending: a run held by another
> server process against the same workspace is refused with `VFX-E-1507` (there is no IPC channel through a
> `FileShare.None` lock), and a `running` entry sitting beside a free lock — the residue a hard-killed server leaves
> — is identified as such with `VFX-E-1508`, which is also how a host tells a phantom `running` entry from a real
> one. `get_step_timeline` returns one step's **complete** RETRY attempt timeline from a finished run, extracted from
> the same parsed event stream `explain_run` reads rather than duplicated beside it. It exists because `explain_run`'s
> response-size tiers shrink its own `attempts` arrays first (ten per step, then five, then none), so a long poll loop
> was the first evidence discarded; this tool inverts that order, keeping every attempt and dropping per-attempt
> evidence text instead (`observedCapped` says when it did). Each attempt's `outcome` is the tool's own three-value
> vocabulary — `matched` / `unmatched` / `error` — which is neither the four-way verdict taxonomy nor the engine's
> wire tokens; an `unmatched` attempt under `verifyMode: RETRY` is the ordinary state of every poll before the last
> one, not a failure. Each attempt's `at` is the engine's own `ts`, relayed verbatim — but the engine stamps it when
> it writes its buffered report, not when the attempt ran, so `tMs` (that attempt's own duration) is what orders and
> times the timeline. `delayMs` (per-attempt) comes back as an explicit null rather than a value synthesised
> from other numbers — nothing on the wire carries it. `timeoutMs` (per-step) and the additive `declaredVerifyMode`
> field are sourced from the step's own `step-started` event (null when that event was not captured for the step,
> and `timeoutMs` alone null when the suite declared no explicit timeout); `declaredVerifyMode` is kept separate
> from the run-evidenced `verifyMode` a host may already key on, since the two answer different questions — what
> the suite authored versus what this run evidenced. `specPath` is validated against the run's own suite set (`VFX-E-1509`
> otherwise), but for a multi-suite run it cannot filter — the engine's events carry no per-suite attribution — and
> `specPathAttributed` comes back false to say so. Read-only and lock-free, like the rest of the events-file readers.
> `get_run_artifacts` reports what a finished run left behind, and is **honestly partial**: every result carries
> `partial: true` plus a `gaps` array naming each field this build cannot populate, why, and the upstream ask that
> would close it. What it really has is the run's own JSON Lines event stream (`reports.events`) and the
> environment resources that stream's own `environment-error` events named. On `reports.events`, `available` says
> whether the file still exists, and `resourceUri` names the advertised `vouchfx://runs/<runId>/events` resource
> that serves it. That URI is always present: it names a resource, while `available` reports the bytes.
> What it does not have: `logs` is always an empty array (there is no container log access at all — never a
> fabricated line and never an error), the engine's HTML and JUnit report paths are omitted rather than nulled (the
> engine owns where it writes them and this server is never told), and `environment.services`/`dependencies` stay
> empty because an `environment-error` event names a resource without saying which of the two it is — every identifier
> therefore lands under `resources` with `role: "unclassified"` and a `health` of `null`, meaning **not observed**,
> never "unhealthy". A run in which nothing went wrong reports no environment resources at all, which is a correct
> answer rather than a failure. `container` and `tailLines` are accepted and validated but select and bound nothing
> yet — `tailLines` outside 1–5000 is refused rather than clamped — so the contract does not change again when the
> engine's artifacts directory lands.
>
> **Prompts**: four MCP prompts, the complete set. `author_scenario` encodes the authoring procedure a host follows to write a passing suite
> using only this server's tools — grounded in `get_schema` and the vendored docs, gap-found with `plan_coverage`,
> scaffolded, validated at `level: full`, normalized, written by the HOST's own file tools, run, and interpreted by
> taxonomy. `heal_run` encodes the healing procedure for a run that ended in `EnvironmentError` or
> `Inconclusive` — resolve the run with `get_run_status`, diagnose with `diagnose_run`, filter its
> `specEditProposals` by an `allowedScopes` permission list, let the HOST apply the edit, re-run once
> and report — and forbids acting on a `Fail` except to explain it.
> `review_spec` is a pre-flight checklist over an existing suite — mechanical checks via
> `validate_suite` first, then judgment on tautological assertions, hard-coded ids, missing
> negative-path coverage and `plan_coverage` gaps — and `explain_failure` explains one step to a
> developer new to vouchfx in 200 words, by taxonomy.
>
> **Resources** are four concrete URIs on `resources/list` — the two vendored engine documents
> (`vouchfx-docs:///language-reference`, `vouchfx-docs:///recipes`), an index of workspace suites
> (`vouchfx://workspace/specs`), and a DSL guide written for a model reader
> (`vouchfx://docs/dsl-guide` — short, imperative, and every YAML example a complete document that
> this repository's own tests validate against the vendored schema) — plus seven URI templates on `resources/templates/list`: schema versions
> (`vouchfx://schema/{version}`, with a `latest` alias), error pages under BOTH schemes
> (`vouchfx-docs:///errors/{code}` and `vouchfx://docs/errors/{code}` — two templates, identical bytes), three
> complete sample suites under `vouchfx://examples/{name}` teaching patterns from basic HTTP smoke tests to
> secrets and message queues, and run resources (`vouchfx://runs/{runId}/verdict`, `.../events`,
> `.../logs/{container}`). See
> [Tools & Resources](docs/tools-and-resources.md#resources) for all four concrete resources and all seven templates.
>
> **[`SKILL.md`](SKILL.md)** at the repository root is a Claude Code skill: with this server registered,
> a session asked to write, review, run or fix a `.e2e.yaml` suite picks up the right procedure with no
> extra installation. It is a ROUTER, not a copy of the procedures — it points at the four prompts and
> at `vouchfx://docs/dsl-guide`, and restates only the two rules that must never be inferred away: never
> weaken an assertion to make a `Fail` pass, and this server never writes a suite file (the host's own
> file tools do).
>
> The packaged `Vouchfx.Mcp` dotnet tool
> is **not yet published**. See
> [Implementation map](docs/implementation-map.md) for how the wider vouchfx.ai proposal maps onto
> what ships here today.

## Engine pin

This repository wraps the published `vouchfx` dotnet tool rather than building the engine from source. It is
currently pinned to **v1.0.0-rc.5** (commit `cc5e8efa9c84f59e1135568456f7c156261f6263`) — see [`ENGINE_PIN`](ENGINE_PIN) for exactly what
that pins, how vendored artefacts stay drift-gated against it, and how to advance it.

## Secret hygiene

This server never resolves `${secret:...}` references and never reads or echoes its own process environment into
a tool result, progress notification, or resource. The vouchfx engine is the sole redaction authority (see its
`SecretString`, §17): the `--events` JSON Lines fields `run_suite`, `explain_run`, and `diagnose_run` relay are already redacted at
source, and this server passes them through untouched — bounded and control-character-sanitised for display, never
re-redacted, never re-resolved. The `vouchfx` CLI child process inherits this server's environment **except for this
server's own HTTP bearer token** (`VOUCHFX_MCP_HTTP_TOKEN`), which is stripped from every child's environment
unconditionally. Everything else is passed through unmodified, which is what lets a suite's own
`${secret:env/...}` reference resolve inside the engine; this server never builds or reads that environment for any
other purpose. The one removal is a narrowing, never an injection: the token is a credential this server owns and no
suite has business reading, so a suite writing `${secret:env/VOUCHFX_MCP_HTTP_TOKEN}` deliberately fails to resolve.

## Related

- [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/) — this server's documentation site.
- [vouchfx](https://github.com/tomas-rampas/vouchfx) — the engine this server wraps.
- [vouchfx.io](https://vouchfx.io) — documentation, roadmap, and provider catalogue.
