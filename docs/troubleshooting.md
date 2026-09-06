# Troubleshooting

## Server exits at startup

Three conditions are startup-fatal: the server writes one sanitised line to stderr and exits with a
non-zero code before it ever speaks MCP. Each is a packaging problem, never something you configure —
the fix in every case is to reinstall the `Vouchfx.Mcp` tool package, not to change your MCP client
setup. Grep your client's captured stderr for these exact prefixes to tell them apart:

- **`vouchfx-mcp: could not read ENGINE_PIN:`** — the `ENGINE_PIN` file that ships beside the built
  executable is missing or malformed. Without it the server has no engine version to gate the CLI
  handshake against, so it cannot proceed at all.
- **`vouchfx-mcp: could not derive the result provenance stamp:`** — the embedded, vendored composed
  schema's own version marker (the source of every result's `meta.schemaVersion`) is missing or
  corrupt. This is a packaging fault in the shipped assembly, not a bad local schema file — there is
  no local schema file to fix.
- **`vouchfx-mcp: could not load the diagnostic catalogue:`** — one of the embedded diagnostic
  catalogue pages (what `explain_diagnostic` and the `vouchfx-docs:///errors/{code}` resource family
  both serve, one page per catalogued code) is missing or malformed. A single bad page is forced to fail at startup rather than
  poisoning every code's lookup later, on whichever call happens to touch it first.

In every case, reinstalling the tool package (`dotnet tool update --global Vouchfx.Mcp --prerelease`,
or reinstall from a fresh `dotnet pack` if you build from source) replaces the corrupt embedded
artefact. None of these three are user-configuration problems — if a reinstall does not clear one,
that is a bug in the shipped package worth reporting.

## CLI pin / version mismatch

`run_suite` performs a handshake against the installed `vouchfx` CLI's own `--version` output before
every run, comparing it against this build's `ENGINE_PIN`. Three distinct outcomes, each returned as a
structured tool error rather than a crash:

- **CLI not found on PATH** — the server reports the pinned version it expected and the exact command
  to install it:

  ```bash
  dotnet tool install --global vouchfx --version 1.0.0-rc.4
  ```

- **Version mismatch** — the installed CLI's version does not match `ENGINE_PIN`. The reported fix is
  an update, not a fresh install:

  ```bash
  dotnet tool update --global vouchfx --version 1.0.0-rc.4
  ```

- **Unparseable version output** — the CLI reported something this server did not recognise as a
  version string at all (a corrupted install, or a `vouchfx` build old enough to predate the
  `--version` flag's current shape). The reported fix is to reinstall:

  ```bash
  dotnet tool install --global vouchfx --version 1.0.0-rc.4
  ```

None of these ever spawn the CLI further to try to "fix itself" — a mismatch is always surfaced as a
structured result, never a silent behavioural drift. `run_suite`, `list_step_types`,
`describe_step_type`, `plan_coverage`, and `scaffold_suite` perform this handshake. `validate_suite`
and `search_docs` work from this server's own embedded artefacts and remain available when the CLI is
missing.

## Live catalogue requires Spec A (shape-level `list --json`)

`list_step_types` and `describe_step_type` call `vouchfx list --json` on the pinned CLI and require
every entry to carry `requiredFields`, `optionalFields`, `captureSupported`, and `familyIntent`. If
the installed engine only returns thin type/family/provider keys (pre–Spec A), the tool returns an
error naming that gap and the minimum engine capability — it does **not** invent field lists or
silently degrade to type keys alone (EDGE-004).

Fix: install a vouchfx build that includes Spec A (`engine-schema-and-catalogue-export`), matching
`ENGINE_PIN` once that version is pinned, then retry. Confirm with:

```bash
vouchfx list --json
```

and check that entries include the four bar-B fields above.

## Docker daemon unavailable

A suite the engine cannot even start because Docker is unreachable is reported by `run_suite` as
`verdict: "EnvironmentError"` — **never** `"Fail"`. This distinction is deliberate and load-bearing: an
environment error means the *infrastructure* could not be stood up (an unreachable Docker daemon, a
failed image pull, a container that never became healthy), not that any test assertion was actually
evaluated and found wanting. Conflating the two would report "your system is broken" when the honest
answer is "the test environment itself never came up".

When `verdict` is `"EnvironmentError"`, `remediationHint` names the likely cause — e.g. that Docker
appears to be unavailable and to check the daemon is running and reachable — built from the run's own
`environment-error` events where the engine reported one, or from the CLI's stderr output as a
fallback when the run failed before any such event was even recorded. `explain_run`'s
`categoryMeaning` field explains the same distinction in plain language for any run you diagnose after
the fact, and its `environmentErrors` list carries the same evidence (`errorKind`, `resourceName`,
`detail`) for every environment error the run recorded.

## Timeouts and cancellation

`run_suite` accepts an optional `timeoutSeconds` (1–3600, default 300) and always reports a run that
did not finish in time — or one whose MCP request was itself cancelled by the calling client — as
`verdict: "Inconclusive"`, **never** `"Fail"`: the suite's actual result was never determined, which is
a different thing from a determined failure.

Two boolean fields distinguish *why* the run ended early:

- `timedOut: true` — the `timeoutSeconds` budget elapsed before the run completed.
- `cancelled: true` — the calling MCP client's own request cancellation fired first, **or** a
  `cancel_run` call asked this run to stop. Both take the identical stop path described below; only a
  `cancel_run`-driven stop additionally records the run's registry `status` as `cancelled` rather than
  `completed`, since it is a deliberate lifecycle action a host then polls the result of.

Exactly one of the two is `true` for an aborted run; `steps` is empty in either case, since no step
outcomes were recorded before the abort. If a run is legitimately slow rather than stuck, raise
`timeoutSeconds` on the next attempt (up to the 3600-second ceiling) rather than retrying blindly.

**What actually happens to the Docker topology when a run is aborted:** `run_suite` requests a
*graceful* engine stop first, not an immediate kill — it closes the CLI child process's stdin, the
signal a `vouchfx` started with `--shutdown-on-stdin-eof` (available from `v1.0.0-alpha.10`) uses to
cancel its own internal token and run its normal container/network teardown to completion, up to its
own internal budget. Only if the CLI is still running once that grace period elapses does `run_suite`
fall back to a hard, whole-process-tree kill. In the ordinary case, then, an aborted run's containers
and Aspire session network are torn down by the engine itself, the same as a normal completion — no
orphaned container is left behind, and no external cleanup is needed. The hard-kill fallback is rare
(reached only if the engine's own teardown genuinely hangs past its internal budget); in that specific
case, Docker containers are not reachable through the killed process tree at all, so their cleanup
falls back to Testcontainers' own Ryuk reaper, which independently reaps orphaned containers within
roughly the reaper's usual detection window regardless of how the parent process ended. If you ever
see a leftover `vouchfx`-related container or `aspire-session-network-*` network after a cancelled or
timed-out `run_suite` call, it should be self-cleaning shortly afterwards; if it persists, that is
worth reporting rather than assuming it is expected.

## Run already in progress

A `run_suite` call is rejected with error code `VFX-E-1501 RunInProgress` when another run is already
active. The scope of this rejection depends on whether a workspace is configured:

- **With `--workspace`**: the claim is enforced across all server processes accessing the same workspace,
  via an OS-level file lock on `<root>/.vouchfx/runs/.lock`. Two separate host connections, editor
  windows, or restarted servers pointing at the same workspace all contend for the claim; the second to
  ask is refused.
- **Without `--workspace`**: the claim is process-local only. Overlapping calls to the *same* server
  see this code, but separate server instances do not contend.

The error is marked retryable (`retryable: true`), and the identical call will succeed once the active
run finishes. The error's `details` carries the active run's `runId` when available, helping you
correlate the refusal with the run you are waiting on (for example, to call `explain_run` once it
finishes). A lock whose holder no longer exists is not a problem you have to resolve manually: the
claim is an operating-system-held file handle rather than a pid written into a file, so a server that
crashes or is force-quit releases its claim automatically.

**Do not delete `<root>/.vouchfx/runs/.lock` by hand.** On Windows the operating system denies the
delete while a run holds the file, so the attempt is useless. On Linux and macOS the claim is an
advisory `flock` record on the file's *inode*, so the delete succeeds even mid-run and silently
breaks mutual exclusion — the next `run_suite` call creates a fresh file, gets a new inode with no
lock on it, and starts a second concurrent run against the same workspace. Relatedly, on every
platform the `.lock` file is expected to persist between runs: it is never read and never blocks
anything, so a leftover one in a quiet workspace is normal and should be left alone. See
[VFX-E-1501](errors/VFX-E-1501.md) for the full per-platform detail.

### The escape hatch: `cancel_run`

If you do not want to wait for the active run, `cancel_run` is the supported way out — never a manual
kill, and never deleting the lock file. Take the `runId` from the rejection's `details` (or from
`list_runs`) and call `cancel_run` with it. The run is asked to stop through exactly the graceful
sequence described above — stdin closed first, hard kill only if the engine's own teardown overruns —
so the topology is torn down properly and the claim is released as part of the run ending. The
cancelled run is recorded with `status: "cancelled"` and outcome `Inconclusive`; it is not lost, and
`explain_run`/`get_run_events` still read whatever it produced before it stopped.

Two answers mean the run cannot be stopped from here, and both are worth reading rather than retrying
blindly:

- **[`VFX-E-1507`](errors/VFX-E-1507.md)** — the entry says `running` and this server is not the one
  holding it. **Read the message**: the workspace lock answers per *workspace*, not per *run*, so it
  cannot on its own prove another process has *your* run. The message names which of three it
  established — a *different* server process is running it (there is no cross-process cancel: the lock
  is a bare file handle with no channel through it, and this server will not invent a competing
  side-channel, so cancel from the process that started the run); the run has already finished here
  and its completing record was lost or is being written this instant; or **this** server is busy with
  another run whose lock masks the probe (call `list_runs`, and cancel that `runId` if that is the one
  you meant). The code is retryable precisely because the condition clears when whatever holds the
  workspace finishes.
- **[`VFX-E-1508`](errors/VFX-E-1508.md)** — the entry says `running` but the lock is free, so nothing
  is running it. This is the phantom entry a hard-killed server leaves behind — or one whose
  completing registry write failed, which the server announced on stderr at the time — and `cancel_run`
  is how you identify one: `get_run_status` and `list_runs` deliberately report such an entry as `running`,
  because establishing liveness means acquiring the lock and a read-only tool must never be able to
  make a concurrent `run_suite` call fail. Nothing is blocking you — start the run again — and remove
  `<root>/.vouchfx/runs/<runId>/` if you want the stale entry gone.

## `get_step_timeline` refuses your `specPath` or `stepId`

Both refusals are about **this run's own record**, not about your filesystem or your suite, and neither
is retryable — the identical call reports the identical thing until you change the argument.

- **[`VFX-E-1509`](errors/VFX-E-1509.md)** — the `specPath` names a suite this run did not cover. A
  run's suite set is fixed when it starts and never rewritten; `get_run_status` reports it as
  `specPaths`. The comparison is generous about spelling (a relative path is resolved against the
  workspace root, both sides are normalised, and matching follows the platform's own file-name rules)
  and strict about membership, so a path that resolves to a file this run never touched is refused
  rather than answered. Note that the file existing is irrelevant in both directions: a suite deleted
  since the run is still a suite that run covered, and still works here.
- **[`VFX-E-1510`](errors/VFX-E-1510.md)** — the run's event stream records no step with that id.
  Matched exactly and ordinally; there is no fuzzy suggestion. Before assuming a typo, check whether
  the step ever *ran*: a run cut short by a cancellation, a timeout or an environment error never
  reaches the steps after the one that stopped it, and a suite refused by pre-flight validation ran
  nothing at all. `explain_run` will say where it stopped, and `get_run_events` with
  `types: ["step-completed"]` lists every step that did finish.

A step that ran and recorded **no individual attempts** is a different thing entirely and is not an
error: it comes back as a successful result with an empty `attempts` array, a `null` `verifyMode`, and
a `conclusion` saying so. That is the ordinary shape of a step left on the default `IMMEDIATE` verify
mode, which emits a completion event and no attempt events.

## `list_runs` says `truncated: true`

The registry examines at most 10,000 run directories per read, in the filesystem's own enumeration
order — before the newest-first sort — so a workspace holding more than that is listed from an
arbitrary slice, and successive pages need not come from the same one. `truncated: true` is the
registry reporting that its scan stopped at that bound; it is a fact from the scan itself, not a guess
from the row count (10,000 rows back is otherwise indistinguishable from a workspace holding exactly
10,000 runs).

Read it **alongside** `nextCursor`, not instead of it: `nextCursor` says more matching runs remain
within what was scanned, `truncated` says what was scanned is not everything. The combination to watch
for is `truncated: true` with no `nextCursor` — the walk ended at this server's bound rather than at the
end of the registry.

There is no retention sweep and no reaper in this release, so the flag will not clear on its own.
Reaching those numbers means `<root>/.vouchfx/runs/` needs pruning: remove the run directories you no
longer need (each is self-contained — its `run.json` and its `events.jsonl`), and note that a full walk
of 10,000 runs takes around 70 seconds because every page re-scans the whole directory.

## `get_run_artifacts` returns `partial: true` and an empty section

That is the expected answer today, not a fault, and the result tells you which part is missing rather
than leaving you to infer it: read the `gaps` array, where each entry names the `field`, a one-line
`reason`, and the upstream ask (`awaits: "U4"`) that would close it.

Three specific shapes surprise people:

- **`logs` is always `[]`.** This server has no container log access at all — no engine flag exposes it
  and the server never talks to a container runtime — so it returns an empty list rather than an error
  or an invented line. `container` and `tailLines` are accepted and validated but select and bound
  nothing yet; they exist so the tool's contract does not change again when log access lands. For what
  the run itself recorded, use `get_run_events` or `explain_run`.
- **`environment.services` and `environment.dependencies` are always `[]`, and `resources` may be too.**
  The only environment identifier in the v1 event stream is the resource an `environment-error` event
  names, and that event does not say which of the two it is — so identifiers appear under `resources`
  with `role: "unclassified"` and a `health` of `null` (**not observed**, never "unhealthy"). A run in
  which nothing went wrong named no resource at all, so all three arrays are legitimately empty.
- **`reports` has no `html` or `junit` property.** They are omitted rather than `null` on purpose: the
  engine owns where it writes its reports and this server is never told the paths, so a `null` would
  wrongly suggest it looked. `reports.events` is the artefact it does have.

If `reports.events.available` is `false`, the run's own event stream has been deleted or cannot be read
— usually a cleaned output directory. The run's metadata outlives its events file, so the other sections
still answer; `explain_run`, `get_run_events` and `get_step_timeline` will refuse the same run with
`VFX-E-1004`, which is the same fact reported by tools for which that file is the whole answer.

## Suite validation timeout

`validate_suite` (and the same pre-flight check `run_suite` performs before spawning anything) runs the
actual YAML/schema evaluation inside an isolated child process bounded by a 10-second wall-clock
timeout. A suite that does not validate within that window is reported as a **tool error** carrying
code `VFX-E-1150` — the suite's actual validity was never determined, and the worker process is killed
(its exit confirmed, not merely assumed) rather than left running. In practice a genuine `.e2e.yaml`
suite validates in well under a second; although `VFX-E-1150` is marked `retryable: true` (a wall-clock
kill can be caused by transient load on your machine rather than by the suite), one on an
ordinary-sized suite is worth reporting as a possible bug rather than retried indefinitely. A separate
code, `VFX-E-1901`, covers the case where the worker process itself could not be started, crashed, or
produced output this server could not parse — distinct from a timeout, but likewise meaning validity
was never actually determined.

Note the distinction these two codes sit on: they are `VFX-E-…` **errors** (`isError` true) precisely
because no verdict was reached. A suite that genuinely fails validation is the opposite case — a
successful call carrying `VFX-D-…` diagnostics. If you are branching on `isError`, a merely-invalid
suite will never take the error branch.

## Diagnostic logging and secret material

All of this server's own logging goes to **stderr** (stdout is the JSON-RPC channel and carries
nothing else), and at its default `Information` level it logs neither tool arguments nor tool results.

**Do not raise the log level to `Trace` while working with suites that carry secret material.** At
`Trace` the MCP SDK logs entire JSON-RPC frames, and since `validate_suite` accepts a suite inline via
its `yaml` argument, a frame now contains the **full suite body** — including any `${secret:…}`
references and any literal credentials the draft happens to hold. Those frames land in whatever your
client captures stderr into. This is a property of frame-level tracing, not of any particular tool:
the server never resolves a secret reference and never echoes one into a result, but it cannot
un-log a request it was asked to trace. Use `Debug` or lower for routine diagnosis, and reserve
`Trace` for reproducing a protocol-level problem with a suite you would be happy to paste into a bug
report.

## Where to look next

For anything not covered above, `run_suite`'s returned `eventsFilePath` and `explain_run` /
`diagnose_run` are the fastest way to get an evidence-backed answer rather than guessing: both read
that same file (or the most recent finished run in the registry when you omit the path) and name the
exact failing or inconclusive step, its RETRY attempt timeline, and any environment errors recorded
— all without re-running anything. `diagnose_run` additionally returns Fail-only review patch
proposals (never auto-applied; EnvironmentError yields infrastructure guidance only). See
[explain_run](tools-and-resources.md#explain_run) and [diagnose_run](tools-and-resources.md#diagnose_run)
for their full result shapes.
