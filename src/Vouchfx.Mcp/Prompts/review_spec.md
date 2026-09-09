---
name: review_spec
title: Review a vouchfx suite before running it
description: >-
  A pre-flight review of an existing .e2e.yaml suite: run the mechanical checks first, then apply
  judgment where the machine cannot — tautological assertions, hard-coded ids, missing negative-path
  coverage and coverage gaps — and produce a checklist with a severity and a concrete edit per
  finding. Read-only; it recommends changes and never makes them.
arguments:
  - name: path
    required: true
    description: >-
      The suite to review, e.g. e2e/provision-customer.e2e.yaml. Absolute or workspace-relative.
---
You are reviewing the vouchfx suite at `{{path}}` before it runs. Produce a checklist, not prose.

## 1. Let the machine go first

Call `validate_suite` with `path: {{path}}` and `level: full`. Read BOTH channels it returns:

- `errors` — the engine's own schema verdict.
- `semanticDiagnostics` — this server's advice, each with a `VFX-D-####` code.

Three of the seven categories below are already decided there, mechanically. Do not re-derive them;
**report what the tool found**, and spend your own judgment on the four it cannot check:

| Category | Already checked by |
| --- | --- |
| Unused captures | `VFX-D-1204` |
| Secret literals | `VFX-D-1207` |
| Async steps missing RETRY | `VFX-D-1209` (see also `VFX-D-1206` on RETRY without a timeout) |

Call `explain_diagnostic` on any code you are unsure about rather than guessing what it means.

## 2. Find the coverage gaps

Call `plan_coverage` with `path: {{path}}` (pass its optional `eventsPath` too if you have run
history — the gap analysis is sharper with it). Its findings are the coverage-gap source: which flows
this suite set does not touch, and which are stale or fragile.

With no run history, every finding comes back as never-run or never-exercised. That is a fact about
the history you gave it, not a defect in the suite — report it as "not yet exercised", and do not
raise it as a review finding against the author.

## 3. Review by hand for what no tool can decide

Go step by step through the suite and look for each of these:

- **Tautological assertions** — an assertion that cannot fail. An `expect` block that asserts nothing,
  a `rowCount` bound that any result satisfies, a match criterion so loose it would accept the wrong
  answer. A step that cannot fail is a step that proves nothing; this is the most common way a suite
  becomes decorative.
- **Missing `verifyMode: RETRY` on async steps** — anything asserting on an effect that another
  process produces (a worker's row, a broker's message, a projection's cache entry) must poll, with an
  explicit `timeout`. Confirm the tool's finding and check for the case it cannot see: a step that
  LOOKS synchronous but depends on something asynchronous upstream.
- **Hard-coded ids** — a literal order id, customer id or correlation id that a previous step should
  have produced. These pass once and fail on the next run against fresh state. The fix is `capture`
  plus a `{placeholder}`.
- **Missing negative-path coverage** — the suite proves the happy path works; does anything prove the
  system rejects what it should? An unauthorised call, an invalid payload, a duplicate submission.
- **Coverage gaps** — from `plan_coverage` above, in this suite's own terms.
- **Unused captures** — confirm the tool's finding, and ask the question it cannot: was the capture
  unused because the assertion that should have consumed it is MISSING?
- **Secret literals** — confirm the tool's finding. Every credential must be a `${secret:...}`
  reference.

## 4. Produce the checklist

One row per finding, with:

- **Severity** — `error` (the suite is wrong or will not run), `warning` (it will run but proves less
  than it appears to), or `info` (a readability or maintenance point).
- **Where** — the step `id`, or the block, the finding applies to.
- **What** — one sentence.
- **A concrete edit** — the YAML to change, written out. "Consider adding a timeout" is not a concrete
  edit; `timeout: 30s` on step `await-settlement-row` is.

Order the checklist by severity, errors first. If a category has no findings, say so explicitly — a
reviewer who stays silent about negative-path coverage is indistinguishable from one who forgot to
look.

This server never writes a suite file, and this review changes nothing: it recommends. Apply anything
you agree with using your own file-editing tools afterwards.
