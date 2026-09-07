---
name: author_scenario
title: Author a vouchfx scenario
description: >-
  Walks you through authoring a passing vouchfx .e2e.yaml scenario using only this server's tools:
  ground yourself in the schema, find the coverage gap, scaffold, write, validate, normalize, write
  the file with your own tools, run, and interpret the verdict by taxonomy. Every step names a tool
  or resource this server actually serves.
arguments:
  - name: flowDescription
    required: true
    description: >-
      The user-facing flow to cover, in plain language — e.g. "provision a customer" or "checkout
      with a saved card". This is the scenario's subject; everything else is optional context.
  - name: flowId
    required: false
    description: >-
      An existing flow or scenario identifier to align with, if the team already has one. Omit it and
      you will be asked to derive a stable id from the description.
  - name: specPath
    required: false
    description: >-
      Where the finished suite should be written, e.g. e2e/provision-customer.e2e.yaml. Omit it and
      you will be told how to choose a path under the workspace's specs directory.
  - name: constraints
    required: false
    description: >-
      Any limits to respect — e.g. "max 8 steps", "HTTP and Postgres only", "no message queues".
---
You are authoring a vouchfx `.e2e.yaml` scenario. Follow this procedure exactly and do not skip
steps.

**Flow to cover:** {{flowDescription}}
{{#flowId}}
**Align with existing flow id:** `{{flowId}}`
{{/flowId}}
{{#specPath}}
**Target path:** `{{specPath}}`
{{/specPath}}
{{^specPath}}
**Target path:** not supplied. Choose one under the workspace's `specsDir` (`<workspace root>/e2e`),
named after the flow — for example `e2e/provision-customer.e2e.yaml`. Read the
`vouchfx://workspace/specs` resource first: if a suite there already covers this flow, extend it
instead of adding a second one, and say so.
{{/specPath}}
{{#constraints}}
**Constraints you must respect:** {{constraints}}
{{/constraints}}

## 1. Ground yourself in the language

Read `vouchfx://docs/dsl-guide` first. It is written for exactly this reader — short, imperative and
example-dense — and covers the whole language in one read: the file's four blocks, state threading,
RETRY, secrets, and the four outcomes.

Then, as you need them: `get_schema` (`format: summary` for the digest, or a `section` when you need
one step type's exact contract), the `vouchfx-docs:///language-reference` resource for every step
type's fields, `vouchfx-docs:///recipes` for worked patterns, and `vouchfx://examples/http-smoke` for
the smallest complete document to start from.

Do not write YAML from memory of another testing tool. This language has its own contract and this
server serves it to you.

## 2. Find the gap, then scaffold — never start from a blank file

Call `plan_coverage` to find the coverage gap this flow sits in, then `scaffold_suite` to generate a
flow skeleton from it. Start from that skeleton.

**Greenfield:** if `plan_coverage` refuses with `VFX-E-1006` because the path discovers zero suites,
that is the expected answer for a workspace with no suites yet, not an error to work around. There is
nothing to analyse, so the coverage gap is the whole flow: go straight to `scaffold_suite` and say
that you did.

If you do not have enough of the system's contract to describe the flow — you cannot tell which
service serves it, which dependency holds the state it changes, or what the response looks like —
**say so and ask for the missing contract instead of guessing**. A scenario built on a guessed
interface tests the guess.

## 3. Copy the step contracts from the registry

Call `list_step_types` for the family you need, then `describe_step_type` for each step type you
intend to use. Copy parameter names from the registry output, never from memory.

## 4. Write the YAML

Use `capture` plus `{placeholder}` to thread ids between steps. Every asynchronous assertion uses
`verifyMode: RETRY` with an explicit `timeout`. Secrets are `${secret:...}` references only — never a
literal credential, which is both a leak and a validation error.

## 5. Validate, and loop until it is clean

Call `validate_suite` with `level: full` on the YAML you have written. It answers in two separate
channels and you must read both:

- `errors` — the engine's own schema verdict. Fix **every** one.
- `semanticDiagnostics` — this server's advice. Address every one; a `severity: error` finding makes
  the suite invalid just as a schema error does.

Loop until it is valid. **Maximum 5 iterations; if it is still failing, stop and report the
diagnostics verbatim** rather than continuing to guess.

## 6. Normalize, then write the file yourself

First, normalize. Call `normalize_suite` with `normalize: true` — the flag is required, because it
defaults to false and without it `normalizedYaml` comes back null and you have nothing to write. The
`normalizedYaml` it returns is the canonical form and is what you write.

Two cases where you write your own text instead, and say so:

- `normalizedYaml` is null with a `normalizationRefused` reason — normalization declined. Write the
  validated YAML you already have in hand.
- Your suite carries comments you want to keep. Normalization drops them (`commentsDropped`), so
  write your own text and say that you kept the comments deliberately.

Then write it. This server never writes, modifies or deletes a suite file.
**Write the file yourself, using your own file-editing tools**, at the target path above.

## 7. Run it

Call `run_suite` with the path you wrote. Then call `explain_run` to interpret the verdict.

## 8. Interpret the outcome by taxonomy

The outcome is one of exactly four values, and each one means something different about what you
should do next.

- **Pass** — done. Summarize what the scenario covers.
- **EnvironmentError** — the infrastructure did not come up; this implies no defect in the system
  under test. Read `explain_run`'s `classificationHints` and each step's `reason.kind`, and call
  `get_run_artifacts` for what the run left behind. Fix environment declarations only.
- **Inconclusive** — the run could not reach a verdict (a timeout, a cancellation). Inspect
  `get_step_timeline` for the step's full attempt history. Adjust `timeout` or `match` **only if the
  observed data shows the assertion is wrong**; otherwise report it and stop.
- **Fail** — the system under test did not do what the scenario asserted. This is a defect, and
  **do not change the assertion to make it pass**. Report it with expected and actual values.

## 9. Final answer

Report: the path written, the outcome, what the scenario proves, what it does not cover, and any
open questions.
