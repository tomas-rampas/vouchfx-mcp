# M4 acceptance drill — Claude Code authors a passing suite unaided

**Status: executed and passed, 2026-09-07.** Two runs: the first surfaced one gap in the
`author_scenario` prompt's narrated procedure, the gap was fixed, and the second run passed with no
workaround. Recorded per the same live-drill convention as the
[graceful-teardown drill](graceful-teardown-drill.md).

## What the drill proves

The plan's M4 acceptance criterion: an MCP host (Claude Code) with **only this server registered** —
no other vouchfx context of any kind — can author a passing `.e2e.yaml` suite for a sample
application by following the `author_scenario` prompt, using the server's tools, prompts, and
resources alone.

## Setup

- **Engine**: the real pinned CLI, `1.0.0-rc.4+be12ebd126fdf03dcea9eade7bcec3afbcba001b` (exact
  `ENGINE_PIN` match, verified before each run), Docker 29.6.1 running.
- **Sample app**: `traefik/whoami:latest` — a containerised HTTP echo service on in-container
  port 80. Its surface (`GET /`, `/api`, `/health`, `POST /`) was probed directly beforehand so the
  drill agent's task statement contained measured application facts and nothing else. Not from the
  `vouchfx-samples` set.
- **Isolation**: the drill agent worked in a scratch workspace outside the repository, reaching the
  real built server (`Vouchfx.Mcp.dll --workspace …`, stdio JSON-RPC) through a thin driver script
  offering `initialize` / `list-tools` / `list-prompts` / `list-resources` / `get-prompt` /
  `call-tool` / `read-resource`. One server process per call; the run registry persists across
  processes via `--workspace`, so the `runId` from `run_suite` resolved in later calls.
- **Contamination controls**: the instruction file named exactly one server-side identifier
  (`author_scenario`, which the acceptance criterion itself mandates) and contained zero DSL
  vocabulary — no file-format name, field names, step types, or outcome words (verified by
  independent review and a keyword sweep before each run). The agent was forbidden to read this
  repository or any other documentation on the machine. Each run used a fresh agent with no prior
  vouchfx context; the first run's working files were archived out of reach before the second.

## Run 1 — Pass, with one procedure gap

The agent followed the prompt end to end and produced a passing suite (15 traced calls, valid on
validation iteration 1 of the permitted 5; `run_suite` verdict **Pass**, runId
`run-ac403547fe8c434093b2879e004e8321`, 4/4 steps, one attempt each; `explain_run` confirmed).

**Gap found (the drill's purpose):** the procedure's step 2 mandates `plan_coverage`, which by
design refuses an empty suite directory (`VFX-E-1006` — zero suites discovered is a configuration
error, not a finding). The procedure said nothing about the greenfield case, and the agent had to
improvise ("took the gap to be the entire flow"). Per the story's acceptance criterion — the drill
is not marked done on a first attempt that required an undocumented workaround — the gap was fixed
and the drill re-run.

**Fix:** `author_scenario` step 2 now carries the greenfield branch (a `VFX-E-1006` refusal on a
workspace with no suites yet is the expected answer, the coverage gap is the whole flow, go straight
to `scaffold_suite`), pinned by a render test. `review_spec` was measured **not** to share the hole
(its `plan_coverage` call is scoped to a suite that exists by precondition) and instead gained a
reading rule for the softer defect found there: with no run history every finding is
never-run/never-exercised — a fact about the history, not the suite. `heal_run` needs neither; its
universe is non-empty by construction.

## Run 2 — Pass, no workaround

Fresh agent, reset workspace (verified through the server: `workspaceConfigured: true`, `specs: 0`,
zero runs). 17 traced calls:

```
initialize → list-prompts → get-prompt author_scenario → read vouchfx://docs/dsl-guide
→ list-tools → plan_coverage (VFX-E-1006, anticipated verbatim by the amended prompt)
→ list_step_types → describe_step_type http.rest → read vouchfx://examples/http-smoke
→ get_schema step:http.rest → get_schema steps → get_schema environment
→ scaffold_suite (4 http.rest steps + service whoami) → validate_suite (valid, iteration 1)
→ normalize_suite (normalize: true; wrote its normalizedYaml with the host's own tools)
→ run_suite → explain_run
```

Result: `run_suite` verdict **Pass**, runId `run-7f09053637b9445491bf1ef17aa87a3a`, exitCode 0,
4/4 steps Pass in one attempt each (8–17 ms; observed status 200 = expected on every step);
`explain_run`: "Pass: all assertions held — the system under test behaved as expected." The suite
threads a captured `$.hostname` from `GET /api` into the `POST /` step as a `{placeholder}`. The
greenfield refusal was handled exactly as the amended prompt narrates — no workaround, no repo
reads, no format knowledge from memory.

## Caveats recorded by the drill agents (limits honestly served, not procedure gaps)

1. **HTTP response-body assertions are not expressible.** `step:http.rest`'s `expect` is
   `{status}` with `additionalProperties: false` — served exactly so by `get_schema`, and recorded
   by the agent rather than papered over with invented fields. An engine-side expressiveness limit;
   body content is unproven by the suite.
2. **Capture-miss semantics are undocumented.** Nothing served states whether a capture whose
   JSONPath matches nothing fails the step, so a capture cannot be claimed as a JSON-shape
   assertion. Follow-up filed against the DSL guide.
3. **`describe_step_type` serves field names but null per-field types/descriptions** — the known
   U5-gated catalogue enrichment; `get_schema section: step:<type>` covers the exact contract.
4. **`VFX-D-1106` fired on every `get_schema` call on this host** — the schema cross-check against
   the installed CLI's export (transcoding divergence), self-explaining and informational; the
   vendored copy is what validates.
5. **The guide's negative-path advice is unimplementable against an echo service** that answers 200
   to everything, given status-only assertions. A context mismatch, not a defect.

## Verdict

M4 holds: the server's own prompts, tools, and resources are sufficient for an unaided host to
author a valid, passing suite — first validation iteration, first run, twice. The one procedure gap
the first run surfaced is precisely what the drill exists to catch; it was fixed and re-verified in
the scenario that exposed it.
