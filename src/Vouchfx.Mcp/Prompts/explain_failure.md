---
name: explain_failure
title: Explain one step's outcome in plain language
description: >-
  Explains what a single step of a vouchfx run attempted, what it observed, and what its outcome
  means by taxonomy — in 200 words or fewer, for a developer who has never used vouchfx.
arguments:
  - name: runId
    required: true
    description: >-
      The run the step belongs to, as run_suite returned it and list_runs reports it — e.g. run-42.
  - name: stepId
    required: true
    description: >-
      The step to explain, matched exactly — e.g. check-balance. Take it from explain_run's
      notableSteps[].stepId if you do not already have it.
---
Explain step `{{stepId}}` of vouchfx run `{{runId}}` to a developer who is **unfamiliar with
vouchfx**. Your whole answer must be **200 words or fewer**.

## Gather the evidence

1. Call `get_run_status` with `runId: {{runId}}`. Keep three things from it: the run's `outcome`, its
   `specPaths`, and its `eventsFilePath`.
2. Call `get_step_timeline` with `runId: {{runId}}`, `stepId: {{stepId}}`, and a `specPath` taken from
   the `specPaths` in step 1. **All three arguments are required** — the tool needs the suite as well
   as the run, and the path must be one the run actually covered or it is refused with `VFX-E-1509`.
   It returns every attempt the step made, with what each one observed.
3. If you want the run-level picture too, call `explain_run` with `eventsPath` set to the
   `eventsFilePath` from step 1 — that tool takes an events PATH, not a run id. It carries the same
   step under `notableSteps`, with a `reason` and the run's `classificationHints`.

If `{{stepId}}` is not a step the run recorded, `get_step_timeline` refuses it with `VFX-E-1510`
rather than returning an empty timeline. That refusal means the id is wrong, not that the step did
nothing — check the spelling against `explain_run`'s `notableSteps`.

## Write the explanation

Cover exactly three things, in this order:

1. **What the step attempted** — in ordinary words. Not "an `http.rest` step with `expect.status`
   201", but "it asked the orders service to create an order and expected it to accept".
2. **What it observed** — the actual values from the timeline. Quote them. If the step retried, say
   how many times and for how long, and what each attempt saw. A per-attempt `outcome` there is
   `matched`, `unmatched` or `error` — that is the ATTEMPT's own vocabulary, and it is not the run's
   verdict.
3. **What the outcome means** — by taxonomy. Use exactly these four words, and explain the one that
   applies:
   - **Pass** — the system did what the suite asserted.
   - **Fail** — the system did NOT do what the suite asserted. This is a defect in the product. It is
     the only outcome that should stop a release.
   - **EnvironmentError** — the infrastructure never came up, so nothing about the product was
     tested. It implies no defect.
   - **Inconclusive** — the run could not reach a verdict at all: it timed out or was cancelled. Also
     not a defect, and not a pass either.

Do not use the engine's internal wire spellings for these; the four words above are the ones a host
sees and the ones your reader should learn.

Finish with one sentence saying what the reader should do next. No preamble, no restatement of the
question, and stay inside the 200 words.
