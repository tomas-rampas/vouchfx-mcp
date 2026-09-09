# vouchfx `.e2e.yaml` — a guide for agents

You are writing a vouchfx end-to-end suite. Read this once; it is everything you need to write a
correct file.

Rules first, then examples. Every YAML block below is a complete, schema-valid document — copy one
and edit it. Do not copy fragments from memory of another testing tool; this language is its own.

## The shape of a file

Four top-level blocks. Only `steps` is required.

- `metadata` — `name`, `owner`, `tags`, `description`. Write it. `tags` is what CI selects on.
- `environment` — `services` (the system under test) and `dependencies` (databases, brokers, caches
  the engine provisions). Map keys are LOGICAL NAMES; every step's `target` refers to one.
- `variables` — constants pre-loaded into the shared context, readable as `{placeholder}`.
- `steps` — an ordered list. Every step needs `id` and `type`. `type` is `family.provider`
  (`http.rest`, `db-assert.postgres`). A bare family is rejected.

```yaml
metadata:
  name: orders-api-smoke
  owner: team-orders
  tags: [smoke]
  description: The service starts and serves its orders collection.

environment:
  services:
    orders-api:
      image: myco/orders-service:latest
      httpPort: 8080

variables:
  expectedStatus: "open"

steps:
  - id: check-health
    type: http.rest
    description: The service answers its health probe.
    target: orders-api
    method: GET
    path: /health
    expect:
      status: 200

  - id: list-open-orders
    type: http.rest
    target: orders-api
    method: GET
    path: /orders?status={expectedStatus}
    expect:
      status: 200
```

Never write a host or a port in a step. `target` names a service; the engine resolves the address.
`path` must be a rooted relative path (`/orders`); an absolute URL is rejected as an SSRF guard.

## State threading: `capture` + `{placeholder}`

A step that produces an id must hand it to the next step. Use `capture` to pull the value out, and
`{name}` to put it back in.

- `capture` maps a variable name to a JSONPath over this step's result: `orderId: "$.id"`.
- For XML, use the mapping form: `orderId: { xpath: "//id" }`.
- A captured value is visible to every LATER step.
- In SQL, bind it as a parameter — never concatenate it into the query text.

**Never hard-code an id.** A literal `order-123` passes once and fails on the next run against fresh
state. If you are tempted to write one, you need a `capture`.

```yaml
metadata:
  name: order-placement-threads-its-id
  tags: [integration]

environment:
  services:
    orders-api:
      image: myco/orders-service:latest
      httpPort: 8080
  dependencies:
    orders-db:
      type: postgres

steps:
  - id: place-order
    type: http.rest
    target: orders-api
    method: POST
    path: /orders
    body:
      sku: WIDGET-1
      quantity: 3
    expect:
      status: 201
    capture:
      orderId: "$.id"

  - id: read-back-order
    type: http.rest
    target: orders-api
    method: GET
    path: /orders/{orderId}
    expect:
      status: 200
```

## RETRY: how you say "eventually"

Anything that asserts on an effect ANOTHER process produces is asynchronous. A worker's database row,
a broker's message, a projection's cache entry.

- Set `verifyMode: RETRY`. The engine polls with bounded exponential backoff.
- Always pair it with an explicit `timeout` (`30s`, `60s`). This is the window you are willing to call
  "eventually": too short is flaky, too long delays a real regression.
- The default is `IMMEDIATE`. An `IMMEDIATE` step that exceeds its `timeout` is **Inconclusive**,
  never Fail — a slow machine is not a product defect.

**Never sleep. Never loop by hand. Never widen an assertion to make a slow answer arrive.**

```yaml
metadata:
  name: settlement-is-asynchronous
  tags: [integration, async]

environment:
  services:
    orders-api:
      image: myco/orders-service:latest
      httpPort: 8080
  dependencies:
    orders-db:
      type: postgres

steps:
  - id: place-order
    type: http.rest
    target: orders-api
    method: POST
    path: /orders
    body:
      sku: WIDGET-1
    expect:
      status: 201
    capture:
      orderId: "$.id"

  - id: await-settlement-row
    type: db-assert.postgres
    target: orders-db
    verifyMode: RETRY
    timeout: 60s
    query: >-
      SELECT status FROM orders WHERE id = @order_id
    parameters:
      order_id: "{orderId}"
    expect:
      rowCount: 1
      row:
        status: settled
```

## Secrets: references only

Write `${secret:source/path}`. Never a literal credential.

- `${secret:env/ORDERS_API_TOKEN}` reads that environment variable at STEP-EXECUTION time.
- The engine redacts it from logs, reports and the event stream. Only the reference path appears.
- A literal password is checked into source control, printed in CI, and flagged as `VFX-D-1207`.

```yaml
metadata:
  name: authenticated-call
  tags: [integration, secrets]

environment:
  services:
    orders-api:
      image: myco/orders-service:latest
      httpPort: 8080

steps:
  - id: submit-with-token
    type: http.rest
    target: orders-api
    method: POST
    path: /orders
    headers:
      Authorization: "Bearer ${secret:env/ORDERS_API_TOKEN}"
    body:
      sku: WIDGET-1
    expect:
      status: 201
```

## The four outcomes

A run ends in exactly one of these. They are not severities; they mean different things and demand
different actions.

| Outcome | Meaning | What to do |
| --- | --- | --- |
| `Pass` | The system did what the suite asserted. | Done. Summarize what it covers. |
| `Fail` | The system did NOT do what the suite asserted. | A product defect. Report expected vs actual. **Never weaken the assertion to make it pass.** |
| `EnvironmentError` | The infrastructure never came up. Nothing about the product was tested. | Fix environment declarations only. |
| `Inconclusive` | No verdict was reached — a timeout, a cancellation. | Inspect the timeline. Adjust `timeout`/`match` only if the observed data shows the assertion is wrong. |

Use these four words. The engine's raw event stream spells them differently; that spelling is for
machines reading events, not for you.

## Do / don't

**Do**

- Start from `vouchfx://examples/http-smoke` or a `scaffold_suite` skeleton, never a blank file.
- Call `describe_step_type` and copy parameter names from the registry output.
- Give every step an `id` that says what it checks (`await-settlement-row`, not `step3`).
- Assert something that can fail. An `expect` that any result satisfies proves nothing.
- Use `verifyMode: RETRY` with an explicit `timeout` for every asynchronous assertion.
- Thread ids with `capture` + `{placeholder}`.
- Write `${secret:...}` references.
- Run `validate_suite` at `level: full` and fix both channels before running anything.

**Don't**

- Don't hard-code ids, hosts, ports or URLs.
- Don't write a literal credential.
- Don't sleep, poll by hand, or retry in your own code.
- Don't assert only the happy path — prove the system rejects what it should.
- Don't change an assertion to turn a `Fail` into a `Pass`. That deletes the finding, not the bug.
- Don't invent step types or fields. If it is not in `list_step_types` / `describe_step_type`, it does
  not exist.

## Where to look next

- `vouchfx://schema/latest` — the composed JSON Schema, the exact contract you are validated against.
- `vouchfx-docs:///language-reference` — every step type's required and optional fields.
- `vouchfx-docs:///recipes` — worked patterns for queues, caches, mail, seeding and CI.
- `vouchfx://examples/{name}` — three complete, commented suites.
- `explain_diagnostic` — what any `VFX-D-####` or `VFX-E-####` code means and how to fix it.
