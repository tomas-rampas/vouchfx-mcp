# vouchfx-mcp

A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents, wrapping
the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) CLI. It advertises twelve tools to validate
`.e2e.yaml` suites against the JSON Schema, look up the step catalogue and documentation for a given
`<family>.<provider>` type, serve the composed schema as a JSON Schema document or markdown digest, plan a declared
suite set's coverage and gap findings (Planner), scaffold a machine-drafted suite skeleton from structured step
types (Generator), run suites with best-effort progress updates and a taxonomy-faithful verdict, diagnose a
suite's JSON Lines event stream, return Fail-only Healer patch proposals, and explain any of this server's own
diagnostic/error codes — all without the agent having to shell out to `vouchfx` and parse its output by hand.

## Status

> **Under construction.** This repository is being built spec-first: features land against approved specs in a
> spec → build → review loop, one requirement at a time. All twelve tools, both vendored-document MCP resources,
> and the diagnostic-catalogue resource are fully functional — the server is feature-complete and packaged as the Vouchfx.Mcp dotnet tool with an OIDC release pipeline; what remains are the first tagged release and publication to NuGet.org. A
> documentation site, in the same fleet design as the other vouchfx satellites, covers all of the below in more
> depth and is live at [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/)
> (built from `scripts/build_site.py`). `validate_suite` (accepts file paths or inline YAML, three validation levels, process-isolated,
> ten semantic rules (eleven codes, one reserved) including secret-literal detection, returns suite summary and semantic-diagnostics channel),
> `search_docs` (vendored language reference and recipes), and `explain_diagnostic` (embedded per-code
> catalogue pages) are CLI-free. `normalize_suite` is CLI-free too: it returns a suite's canonical text — key
> order taken from the engine's own JSON Schema, one consistent quoting and block-layout style, with mappings of
> the author's own data (headers, JSON bodies, variables, services, captures) deliberately left in the order they
> were written — alongside the same full `validate_suite` result, always at level `full` so the secret-literal
> check cannot be turned off on text a host may write back. **This server never writes the file**: normalization
> is opt-in (`normalize: true`) because it discards all `#` comments on the pinned YAML library, the result says
> so on the payload (`commentsDropped`), and the canonical text is proved to re-parse to the same document before
> it is returned — on the rare shape the emitter cannot render faithfully you get `normalizedYaml: null` and a
> `normalizationRefused` reason instead of corruption. `get_schema` serves the composed JSON Schema — the whole document or one
> addressable section — as a schema document or markdown digest; it works offline from the embedded schema this
> server vendors at its pinned engine commit, and optionally cross-verifies that schema against the installed
> CLI's `vouchfx schema` export (reporting any divergence as a diagnostic on the still-successful result).
> `list_step_types` and `describe_step_type` load the **live** shape-level catalogue from the pinned engine via
> `vouchfx list --json` (required/optional fields, capture support, family intent — Spec A); they fail fast
> if the CLI is missing, mismatched, or returns only thin type keys without field metadata.
> `plan_coverage` runs the engine's deterministic, read-only coverage-and-gap analysis over a declared suite
> set, an optional event history, and the live step catalogue via the pinned CLI `plan --json` (Spec D M3
> Planner) — a call that finds gaps is a successful result, never an error, and every gap finding carries a
> suggested step type/id that feeds `scaffold_suite` unchanged. `ENGINE_PIN` (currently v1.0.0-rc.4) is
> Planner-capable; the CLI presence/version handshake still fails closed if a locally installed CLI is
> missing or does not match the pin.
> `scaffold_suite` generates a machine-drafted, schema-valid `.e2e.yaml` skeleton from structured step types,
> ids, and an environment outline via the pinned CLI `scaffold --intent` (Spec B Generator) — free text is
> host-LLM only; this server never hosts a model. `ENGINE_PIN` is scaffold-capable; the same handshake fails
> closed on a missing/mismatched local CLI.
> `run_suite` executes a suite through the packaged `vouchfx` CLI: it verifies the CLI is on PATH and matches
> [`ENGINE_PIN`](ENGINE_PIN) and that the suite itself validates before spawning anything, reports best-effort
> progress as the run proceeds, and returns the taxonomy-faithful verdict (pass / fail / environment error /
> inconclusive) together with each step's outcome once the run completes — a missing/mismatched CLI or an invalid
> suite returns a structured result explaining why, without attempting to run anything, and a Docker-unavailable
> or timed-out/cancelled run is always reported as an environment error or inconclusive, never as a failure.
> `explain_run` diagnoses a run purely by reading and parsing its JSON Lines event stream — never re-running
> anything — defaulting to the most recent finished run in the run registry when no path is given (persists
> across server restarts when launched with `--workspace`): it reports the verdict together with what that
> category means, names the failing or inconclusive step(s) with their RETRY attempt timeline and
> observation/diff evidence, and always keeps an environment error distinct from a genuine test defect.
> `diagnose_run` (Healer / Spec C) adds Fail-only review patch proposals from that same events file;
> EnvironmentError gets infrastructure guidance only (never YAML rewrites); proposals are never auto-applied.
> `explain_diagnostic` looks up one catalogued `VFX-D-####`/`VFX-E-####` code and returns
> its title, explanation, common causes, and fixes — the same content served by the templated
> `vouchfx-docs:///errors/{code}` resource, so a host can hand a code straight from any `VfxError`/
> `Diagnostic` to either access path. The packaged `Vouchfx.Mcp` dotnet tool is **not yet published**. See
> [Implementation map](docs/implementation-map.md) for how the wider vouchfx.ai proposal maps onto
> what ships here today.

## Engine pin

This repository wraps the published `vouchfx` dotnet tool rather than building the engine from source. It is
currently pinned to **v1.0.0-rc.4** (commit `be12ebd126fdf03dcea9eade7bcec3afbcba001b`) — see [`ENGINE_PIN`](ENGINE_PIN) for exactly what
that pins, how vendored artefacts stay drift-gated against it, and how to advance it.

## Secret hygiene

This server never resolves `${secret:...}` references and never reads or echoes its own process environment into
a tool result, progress notification, or resource. The vouchfx engine is the sole redaction authority (see its
`SecretString`, §17): the `--events` JSON Lines fields `run_suite`, `explain_run`, and `diagnose_run` relay are already redacted at
source, and this server passes them through untouched — bounded and control-character-sanitised for display, never
re-redacted, never re-resolved. The `vouchfx` CLI child process inherits this server's environment unmodified,
which is what lets a suite's own `${secret:env/...}` reference resolve inside the engine; this server never builds
or reads that environment for any other purpose.

## Related

- [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/) — this server's documentation site.
- [vouchfx](https://github.com/tomas-rampas/vouchfx) — the engine this server wraps.
- [vouchfx.io](https://vouchfx.io) — documentation, roadmap, and provider catalogue.
