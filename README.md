# vouchfx-mcp

A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents, wrapping
the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) CLI. It advertises six tools to validate
`.e2e.yaml` suites against the JSON Schema, look up the step catalogue and documentation for a given
`<family>.<provider>` type, run suites with best-effort progress updates and a taxonomy-faithful verdict, and
diagnose a suite's JSON Lines event stream — all without the agent having to shell out to `vouchfx` and parse its
output by hand.

## Status

> **Under construction.** This repository is being built spec-first: features land against approved specs in a
> spec → build → review loop, one requirement at a time. All six tools and both vendored-document MCP resources
> are fully functional — the server is feature-complete and packaged as the Vouchfx.Mcp dotnet tool with an OIDC release pipeline; what remains are the first tagged release and publication to NuGet.org. A
> documentation site, in the same fleet design as the other vouchfx satellites, covers all of the below in more
> depth — once deployed at [tomas-rampas.github.io/vouchfx-mcp](https://tomas-rampas.github.io/vouchfx-mcp/)
> (built from `scripts/build_site.py`), pending Pages publication. `validate_suite` (validates `.e2e.yaml` files against the vendored engine schema with
> structured errors and unknown-step-type detection, isolated in a killable child process so a hostile suite can
> never hang the server), `list_step_types` (enumerates all 25 core provider types), `describe_step_type` (returns
> per-type field schemas), and `search_docs` (searches the vendored language reference and recipes for a free-text
> query, returning the matching sections with deep links to [vouchfx.io](https://vouchfx.io)) are CLI-free.
> `run_suite` executes a suite through the packaged `vouchfx` CLI: it verifies the CLI is on PATH and matches
> [`ENGINE_PIN`](ENGINE_PIN) and that the suite itself validates before spawning anything, reports best-effort
> progress as the run proceeds, and returns the taxonomy-faithful verdict (pass / fail / environment error /
> inconclusive) together with each step's outcome once the run completes — a missing/mismatched CLI or an invalid
> suite returns a structured result explaining why, without attempting to run anything, and a Docker-unavailable
> or timed-out/cancelled run is always reported as an environment error or inconclusive, never as a failure.
> `explain_run` diagnoses a run purely by reading and parsing its JSON Lines event stream — never re-running
> anything — defaulting to the most recent `run_suite` call this session when no path is given: it reports the
> verdict together with what that category means, names the failing or inconclusive step(s) with their RETRY
> attempt timeline and observation/diff evidence, and always keeps an environment error distinct from a genuine
> test defect. The packaged `Vouchfx.Mcp` dotnet tool is **not yet published**.

## Engine pin

This repository wraps the published `vouchfx` dotnet tool rather than building the engine from source. It is
currently pinned to **v1.0.0-rc.1** (commit `645021bc`) — see [`ENGINE_PIN`](ENGINE_PIN) for exactly what
that pins, how vendored artefacts stay drift-gated against it, and how to advance it.

## Secret hygiene

This server never resolves `${secret:...}` references and never reads or echoes its own process environment into
a tool result, progress notification, or resource. The vouchfx engine is the sole redaction authority (see its
`SecretString`, §17): the `--events` JSON Lines fields `run_suite` and `explain_run` relay are already redacted at
source, and this server passes them through untouched — bounded and control-character-sanitised for display, never
re-redacted, never re-resolved. The `vouchfx` CLI child process inherits this server's environment unmodified,
which is what lets a suite's own `${secret:env/...}` reference resolve inside the engine; this server never builds
or reads that environment for any other purpose.

## Related

- [vouchfx](https://github.com/tomas-rampas/vouchfx) — the engine this server wraps.
- [vouchfx.io](https://vouchfx.io) — documentation, roadmap, and provider catalogue.
