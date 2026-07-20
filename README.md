# vouchfx-mcp

A local stdio [Model Context Protocol](https://modelcontextprotocol.io/) server for AI coding agents, wrapping
the packaged [`vouchfx`](https://github.com/tomas-rampas/vouchfx) CLI. It will let an agent validate
`.e2e.yaml` suites against the JSON Schema, look up the step catalogue and documentation for a given
`<family>.<provider>` type, run suites and follow their live progress, and diagnose a suite's JSON Lines event
stream — all without the agent having to shell out to `vouchfx` and parse its output by hand.

## Status

> **Under construction.** This repository is being built spec-first: features land against approved specs in a
> spec → build → review loop, one requirement at a time. The MCP server itself has not been implemented yet —
> this is the initial repository scaffold — and the packaged `Vouchfx.Mcp` dotnet tool is **not yet published**.
> There is nothing here to install or run today.

## Engine pin

This repository wraps the published `vouchfx` dotnet tool rather than building the engine from source. It is
currently pinned to **v1.0.0-alpha.9** (commit `8c579ab4`) — see [`ENGINE_PIN`](ENGINE_PIN) for exactly what
that pins, how vendored artefacts stay drift-gated against it, and how to advance it.

## Related

- [vouchfx](https://github.com/tomas-rampas/vouchfx) — the engine this server wraps.
- [vouchfx.io](https://vouchfx.io) — documentation, roadmap, and provider catalogue.
