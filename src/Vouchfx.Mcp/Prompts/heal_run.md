---
name: heal_run
title: Heal a vouchfx run
description: >-
  Walks you through diagnosing a run that ended in EnvironmentError or Inconclusive and applying the
  smallest scoped fix, using only this server's tools. It forbids acting on a Fail outcome except to
  explain it, so a genuine product defect is never "fixed" by weakening the test that found it.
arguments:
  - name: runId
    required: true
    description: >-
      The run to heal, as run_suite returned it (and as list_runs reports it) — e.g. run-42.
  - name: allowedScopes
    required: false
    default:
      - environment
      - timeouts
      - match
      - capture
    description: >-
      Which proposal scopes you may actually apply, comma-separated. Defaults to every scope this
      server's Healer can emit. Narrow it to restrict what may change — e.g. "environment" to permit
      only infrastructure declarations.
---
You are diagnosing vouchfx run `{{runId}}`.

## The rule that comes before the procedure

**You must NOT act on a `Fail` outcome except to explain it.** A `Fail` means the system under test
did not do what the suite asserted — that is a defect in the product, not in the suite. Report it
with expected and actual values and stop; **do not weaken an assertion to force a pass**, do not
widen a match, do not lengthen a timeout to make a wrong answer arrive "eventually".

You are here to heal `EnvironmentError` and `Inconclusive` outcomes. Those two mean the run could not
reach a verdict — the infrastructure did not come up, or the evidence was inconclusive — and they
imply nothing about whether the product is correct.

{{#allowedScopes}}
**Scopes you may apply in this session:** {{allowedScopes}}
{{/allowedScopes}}
{{^allowedScopes}}
**Scopes you may apply in this session: NONE.** You were given an empty scope list, which is an
explicit instruction, not an omission: **report every proposal and apply none of them.** Do the full
diagnosis, quote the evidence, and describe the edits you would have made — then stop without
changing any file.
{{/allowedScopes}}

## 1. Resolve the run

Call `get_run_status` with `runId: {{runId}}`. It returns the registry's own record for that run,
including its `eventsFilePath` and its `outcome`.

You need that path because the diagnosis tools are events-file-native, not run-id-native: `explain_run`
and `diagnose_run` both take an `eventsPath`, and neither accepts a run id. (The
`vouchfx://runs/{runId}/verdict` resource resolves the id for you in one step if you would rather
read it than call a tool.)

If the outcome is `Pass`, there is nothing to heal — say so and stop.

## 2. Diagnose

Call `diagnose_run` with the `eventsPath` from step 1. It returns everything `explain_run` does — the
verdict, the notable steps, their attempt timelines — plus the Healer's own proposals.

For each non-Pass step, read its `reason.kind` and the run-level `classificationHints`. `reason.kind`
is a stable value to branch on (`pull`, `unhealthy`, `seed`, `timeout`, `capture_unmet`, `partition`,
`assertion`); the hint sentence beside it is for you to read, not to pattern-match.

## 3. Read the proposals — and filter them by scope

For the `EnvironmentError` and `Inconclusive` steps, read `diagnose_run`'s `specEditProposals`. Each
carries a `scope`, a `rationale` and a `suggestedEdit` (a YAML fragment to adapt, never a diff).

{{#allowedScopes}}
**Apply a proposal ONLY when its `scope` is one of: {{allowedScopes}}.** A proposal outside that set
is reported, never applied — say what it suggested and why you did not act on it.
{{/allowedScopes}}
{{^allowedScopes}}
**Apply nothing.** Your scope list is empty, so every proposal here is out of scope: report each one
— its `scope`, its `rationale` and the edit it suggests — and apply none of them.
{{/allowedScopes}}

Note what the Healer will never propose: there is no scope for an assertion. If the only way to make
a step pass is to change what it asserts, that is the `Fail` case above, and the answer is a report.

## 4. Apply the edit yourself

This server never writes, modifies or deletes a suite file.
**Apply the change using your own file-editing tools**, at the suite path `get_run_status` reported
under `specPaths`.

Make the SMALLEST change the proposal supports. If a proposal's `suggestedEdit` does not fit the
suite as written, adapt it and say that you did.

## 5. Re-run — once

Call `run_suite` with `wait: true` on that suite path, and do it **once**. `wait: true` is what makes
the call return the finished verdict, which is what you need in order to compare; an asynchronous run
would leave you comparing nothing.

One re-run, not a loop. If the outcome did not improve, report that — repeating the cycle burns a
container environment chasing something the evidence has not explained.

## 6. Compare and report

Compare the new outcome to the old one. Then produce a report with four parts:

- **Root cause** — what actually went wrong, in one or two sentences.
- **Evidence** — quote the observation or log lines you relied on. Get them from `get_run_artifacts`
  for the run's artefact inventory and environment error records, or from `get_run_events` to read
  the raw event stream directly. Quote them; do not paraphrase.
- **Change made or recommended** — the edit you applied, or the one you are recommending and why you
  did not apply it (out of scope, or the evidence did not support it).
- **Confidence** — how sure you are, and what would raise it.
