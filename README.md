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

## .NET compatibility

Current source builds and runs on .NET 8, 9 and 10 while retaining a `net8.0` binary
baseline. Rebuilt tools use major-version runtime roll-forward when .NET 8 is absent;
the [compatibility matrix](https://github.com/tomas-rampas/vouchfx-mcp/blob/main/.github/workflows/dotnet-compatibility.yml) checks each
SDK/runtime in isolation, including MCP worker processes and an installed tool.
Previously published MCP and pinned engine packages retain their original runtime
requirements; consult the selected release's installation guide.

## Status

> **Status: implemented server.** The server exposes eighteen tools,
> four MCP prompts and eleven MCP resources — four concrete
> ones (the two vendored documents, the workspace suite index and the DSL guide for agents) and seven URI templates covering six families,
> with error pages served under two schemes. The server serves over
> **stdio by default, with an optional bearer-authenticated HTTP transport** behind
> `--transport http`; every tool call emits **one bounded `ActivitySource` span**, and all of this server's own stderr
> output is **one JSON object per line**. Packaging and an OIDC release pipeline exist.
> **[Vouchfx.Mcp 1.0.0](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.0.0) is published on [NuGet.org](https://www.nuget.org/packages/Vouchfx.Mcp/1.0.0).** Install it with `dotnet tool install --global Vouchfx.Mcp --version 1.0.0`.
> Verify the installed tool, pinned CLI execution and cancellation behaviour for the selected release. Environment support follows the engine version in [`ENGINE_PIN`](ENGINE_PIN):
> inline services/dependencies and user-supplied Docker Compose are supported. Select Compose with
> `environment.source.driver: compose` and keep its configuration beside the suite. CLI-free schema
> validation does not read the driver configuration or prove its bindings; validate the saved suite
> with the pinned CLI before running it. Other formats require a registered engine driver.

> **Workspace history retention:** maintenance automatically prunes eligible terminal metadata and
> event evidence with defaults of **1,000 runs and 30 days**. Copy evidence you need to keep;
> `--keep-run-history` disables pruning. See [retention settings and recovery limits](docs/install.md#run-history-retention-and-recovery).
> A documentation site, in the
> same fleet design as the other vouchfx satellites, covers all of the below in more depth and is live at
> [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/) (built from `scripts/build_site.py`). `validate_suite`
> (accepts file paths or inline YAML, three validation levels, process-isolated, twelve semantic rules (thirteen codes,
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
> (decoding redirected CLI output as UTF-8, so a divergence reported as a diagnostic on the still-successful result is
> real drift, never a transcoding artefact).
> `list_step_types` and `describe_step_type` load the **live** shape-level catalogue from the pinned engine via
> `vouchfx list --json` (required/optional fields, capture support, family intent — Spec A — plus the engine's
> own tier, RETRY support and language-reference link, and, from `describe_step_type`, a minimal example suite);
> they fail fast if the CLI is missing, mismatched, or returns only thin type keys without field metadata. `plan_coverage` runs the
> engine's deterministic, read-only coverage-and-gap analysis over a declared suite set, an optional event history,
> and the live step catalogue via the pinned CLI `plan --json` (Spec D M3 Planner) — a call that finds gaps is a
> successful result, never an error, and every gap finding carries a suggested step type/id that feeds
> `scaffold_suite` unchanged.
> Its **response** is size-budgeted; the analysis is not. On a repository
> large enough to produce hundreds of findings, a measured ladder caps the reply at 64 KB of wire
> envelope and keeps the most actionable findings first, never returning more than 150.
> Everything left out is counted — `omittedFindingCount` plus the inventory's own `omitted*` counters,
> with `responseTruncated` set. `maxFindings` asks for fewer still. `ENGINE_PIN` (currently v1.2.0) is Planner-capable; the CLI presence/version
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
> Capture-miss hints name the declared JSONPath when the same event proves a unique unmatched capture;
> declaring-step proposals require that proof. Legacy or ambiguous evidence keeps conservative guidance.
> Derived event readers also expose optional `engineCorrelation: { runIds, partial }`: `explain_run`,
> `diagnose_run`'s diagnosis, `get_step_timeline`, and environment/all `get_run_artifacts` results.
> These are exact engine event identifiers, separate from the server's `run-…` registry identity.
> One server run can contain several engine identifiers; the field does not attribute a step to a suite.
> See the [correlation bounds and absence rules](docs/tools-and-resources.md#engine-event-correlation).
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
> `completed`/`cancelled`), verdict, timestamps, the suites it covered, its events file, its labels, and (since
> vouchfx-mcp#114) `run_suite`'s own `remediationHint` for it, persisted verbatim — which is
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
> from the run-evidenced `verifyMode` a host may already key on, since the two answer different questions — how
> the step was declared (`IMMEDIATE`, the engine's default, when the suite named none) versus what this run
> evidenced. `specPath` is validated against the run's own suite set (`VFX-E-1509`
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
> Install published `Vouchfx.Mcp` **1.0.3** with CLI **1.0.7**, following the
> [paired installation instructions](docs/install.md#published-package-mcp-103-with-cli-107). See
> [Implementation map](docs/implementation-map.md) for how the wider vouchfx.ai proposal maps onto
> what ships here today.

## Engine pin

This repository runs the packaged `vouchfx` CLI as a subprocess; it does not build the engine. The source pin is
**v1.2.0**, with the full engine commit recorded in [`ENGINE_PIN`](ENGINE_PIN). That file explains what each field
pins, how vendored artefacts stay drift-gated against it, and how to advance it. This pin applies to a package built from this source checkout; published MCP 1.0.3 instead pins CLI 1.0.7. Follow the [installation guide](docs/install.md) for the selected route and verify the CLI’s full version and commit. Environment execution follows
this pin; `plan_coverage` reports external-source
suites as unanalysable, and `scaffold_suite` currently accepts inline environment outlines only.

From v1.2.0 the CLI can also load trusted external providers from `VOUCHFX_PROVIDERS` and
`~/.vouchfx/providers`. The CLI-backed tools see them, because every CLI this server starts inherits
its environment. `run_suite` compile-validates every selected suite with the pinned CLI before
execution, using that same provider discovery. `validate_suite`, `normalize_suite` and `get_schema`
remain offline authoring tools for the pinned engine's bundled step types. See
[External providers](docs/tools-and-resources.md#external-providers-engine-v120-and-later).

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

## Build and test

Use the .NET SDK pinned in `global.json`:

```bash
dotnet restore Vouchfx.Mcp.sln --locked-mode
dotnet build Vouchfx.Mcp.sln -c Release --no-restore
dotnet test Vouchfx.Mcp.sln -c Release --no-build --no-restore
```

Native path-containment probes use real local file/directory links. Unsupported local hosts report
explicit xUnit skips; the CI unit-test step sets `VOUCHFX_MCP_REQUIRED_LINK_PROBES=1`, so a missing
link prerequisite fails instead. Run just that native evidence set after building:

```powershell
$env:VOUCHFX_MCP_REQUIRED_LINK_PROBES = '1'
dotnet test tests/Vouchfx.Mcp.Tests/Vouchfx.Mcp.Tests.csproj -c Release --no-build --no-restore --filter 'containment=link'
Remove-Item Env:VOUCHFX_MCP_REQUIRED_LINK_PROBES
```

On Bash, prefix the test command with `VOUCHFX_MCP_REQUIRED_LINK_PROBES=1`. Report the executed,
failed and skipped counts in a contribution's validation notes. A required run must have zero skips.
The link-target refusal fixtures read local link metadata; they never connect to a network share.
These probes do not require the engine or Docker. See
[workspace containment](docs/install.md#optional-workspace-containment) for the path-check limits.

Most other tests use fake engine processes. The two real step-attempt envelope probes are explicitly
skipped by default. Release validation must install the exact engine version from `ENGINE_PIN`,
provide working Aspire DCP and Docker with Linux containers, then require those probes:

```powershell
$env:VOUCHFX_MCP_REQUIRED_REAL_PROBES = '1'
dotnet test tests/Vouchfx.Mcp.Tests/Vouchfx.Mcp.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~RealStepAttemptEnvelopeAgainstPinnedCliTests' --logger 'console;verbosity=normal'
Remove-Item Env:VOUCHFX_MCP_REQUIRED_REAL_PROBES
```

CI and release publication also require the `Required real CLI probes` lane. It installs the public CLI and checks the exact full version from `ENGINE_PIN`, checks out that immutable engine source for parity assertions, and uses the bundled Linux DCP with Docker. The lane runs six methods: the dependency-env refusal, the reserved-environment table, both StepAttempt probes, and both engine-generated storage scaffold facts. Its TRX/log gate requires six passes, zero skipped results, evidence for fourteen CLI invocations, and both storage generations. Missing prerequisites fail the lane; GitHub/NuGet publication depends on it. Raw TRX and console diagnostics are uploaded even on failure.

In required mode, missing prerequisites, failed startup or missing execution evidence fail the
tests. A passing release validation run must execute both probes with zero skipped tests. The suite deliberately
times out its RETRY assertion: each attempt is `FAIL`, but the final step and scenario are
`INCONCLUSIVE`, with `observation.reason=retry-timeout` on the step. The probe requires CLI exit code
`4` (`--fail-on-inconclusive`) and the expected event evidence; an environment failure cannot pass.

Required release evidence also includes the complete reserved-environment source table and every
type/key refusal against the pinned CLI. Set `VOUCHFX_ENGINE_REPO` to an engine checkout containing
the full commit in `ENGINE_PIN`; the probe reads that immutable source object, not working files.
It requires `VOUCHFX_MCP_REQUIRED_REAL_PROBES=1` and runs before Docker startup:

```powershell
$env:VOUCHFX_ENGINE_REPO = '/absolute/path/to/vouchfx'
$env:VOUCHFX_MCP_REQUIRED_REAL_PROBES = '1'
dotnet test tests/Vouchfx.Mcp.Tests/Vouchfx.Mcp.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~RealReservedEnvironmentAgainstPinnedCliTests' --logger 'console;verbosity=normal'
Remove-Item Env:VOUCHFX_MCP_REQUIRED_REAL_PROBES
Remove-Item Env:VOUCHFX_ENGINE_REPO
```

The ordinary `Build & unit tests` and `Build, test & pack` steps require native link probes and
assert the installed CLI's full version/SHA. Their unit-test steps leave
`VOUCHFX_MCP_REQUIRED_REAL_PROBES` unset. The separate blocking `required-real-probes.yml` lane
is called by both hosted workflows, provides the immutable `ENGINE_PIN` source checkout, and runs
all four required real-CLI facts with `VOUCHFX_MCP_REQUIRED_REAL_PROBES=1`. These checks do not replace
the [graceful-teardown drill](docs/validation/graceful-teardown-drill.md).

### Publishing the tool

Publish a GitHub Release for the reviewed version tag to start publication. Pushing a tag alone
does not publish. A `workflow_dispatch` smoke test builds, tests and packs without attaching release
assets or publishing to NuGet.org. Publication requires both the build/test/pack job and the dedicated real-CLI
probe lane. A retry reuses an existing package asset only after its size and SHA-256 match the
package being published; ambiguous assets, mismatches and lookup/download failures stop publication.
Assets are never overwritten. An incorrect published package needs a new patch version.

## Documentation validation

Install the Python dependencies and immutable `vouchfx-site-tools` revision from
[the documentation workflow](.github/workflows/docs.yml), then run:

```sh
python scripts/build_site.py _site
python -m vouchfx_site_tools.check_private_identifiers _site
python -m vouchfx_site_tools.check_social_card _site https://vouchfx-mcp.vouchfx.io/
```

The read-only documentation job builds and checks the site on every pull request.
Pages runs both checks before uploading its artefact. The private identifier check
scans static rendered text, text-bearing HTML metadata and published JSON,
including markup embedded in JSON strings. Its fixed, bounded diagnostics omit
matched text and input paths. It does not fetch frames or execute JavaScript or CSS.

The separate social-card check requires both image URLs to resolve inside the
built site on its configured origin and base path, with valid PNG headers,
`1200x630` dimensions and a maximum size of 307,200 bytes. It validates
metadata and headers; it does not decode pixels or check the accent colour.
A refusal returns exit code `1` and blocks publication.

## Related

- [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/) — this server's documentation site.
- [vouchfx](https://github.com/tomas-rampas/vouchfx) — the engine this server wraps.
- [vouchfx.io](https://vouchfx.io) — documentation, roadmap, and provider catalogue.
