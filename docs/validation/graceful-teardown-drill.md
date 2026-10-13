# Graceful-teardown live drill procedure

**Purpose:** Verify that the MCP's `GracefulShutdownGrace` (35 seconds) remains safe against the current engine pin — that is, the engine's own teardown budget stays at ~30 seconds or less, so the engine self-exits (or hits its own `ShutdownBackstop` force-exit) before the MCP's 35-second grace deadline expires.

**When to run this drill:** Every time `ENGINE_PIN` is advanced. This is a MANDATORY gate before merging a pin bump (see `ENGINE_PIN`'s "How to advance it" / "Steps:" section).

**What will be validated:** The engine's graceful teardown works end-to-end on a real Docker topology: a cancelled/timed-out `run_suite` call removes containers and the Aspire session network via the engine's own shutdown mechanism, NOT via Ryuk or manual cleanup.

## Prerequisites

- The `vouchfx` CLI at **exactly** the current `ENGINE_PIN` version is installed (currently v1.0.1).
  This gate proves the MCP's grace is safe against the *pinned* build, so validating a newer CLI than
  the pin would not establish that — install the pinned version, not merely "or later".
- Docker is running and reachable.
- Clone or pull `vouchfx-samples`: you will use the `samples/orders-dotnet` suite.
- Testcontainers is configured and working (reachable Docker daemon).

## Procedure

### Step 1: Prepare the environment

```bash
# Set Ryuk to disabled (the drill must exercise the engine's own teardown, not the Ryuk reaper).
export TESTCONTAINERS_RYUK_DISABLED=true
```

On Windows (PowerShell):
```powershell
$env:TESTCONTAINERS_RYUK_DISABLED = 'true'
```

### Step 2: Verify the baseline

Record the existing container and network IDs before launching the drill:

```bash
docker ps -a --no-trunc
docker network ls --no-trunc
```

Use a uniquely named suite or Compose project and record the child PID. Identify only the new
resources owned by this run using their labels, names and the before/after ID difference. Existing
containers and networks may remain running; never prune or stop unrelated resources.

### Step 3: Build the sample application image

Navigate to the `vouchfx-samples` repository and build the local image:

```bash
cd samples/orders-dotnet/app
docker build -t vouchfx-samples-orders-dotnet:local .
cd ../../../                  # Back to vouchfx-samples root
```

### Step 4: Run the drill

Make a `run_suite` call against the orders suite with a SHORT `timeoutSeconds` value, calibrated per the note below, to force an abort mid-topology-stand-up (t≈8s on the warm 2026-09 host; 20s on the colder 2026-08 hosts these timings were first measured on — the samples below still show 20).

`vouchfx run` itself has **no timeout flag at all** — verify with `vouchfx run --help`; there is no
`--timeout-seconds` option, and passing one exits 2 ("Unrecognized command or argument"). The
`run_suite` MCP tool's `timeoutSeconds` parameter (`RunSuiteTool.cs`) is enforced entirely on the
**MCP side**, not by the CLI: `VouchfxCliSuiteRunner` always starts the CLI with
`--shutdown-on-stdin-eof` and keeps the child's own redirected stdin pipe open for the run's
duration; when the caller's `timeoutSeconds` elapses, it closes that pipe as the graceful-stop
SIGNAL, then waits up to the 35-second `GracefulShutdownGrace` for the engine to tear down on its
own before force-killing (see `VouchfxCliSuiteRunner.RunAgainstProcessAsync` /
`StopGracefullyThenForceKillAsync`). This step reproduces that same mechanism — closing the
child's stdin after a delay — rather than a CLI flag that does not exist.

Two equivalent ways to trigger it; use whichever is available to you:

**Option A — through the `run_suite` MCP tool itself (closest to production):** with an MCP client
at hand (Claude Desktop, an MCP inspector, or a short script against the `ModelContextProtocol` .NET
or TypeScript SDK), call:

```json
{ "path": "samples/orders-dotnet/tests/orders.e2e.yaml", "timeoutSeconds": 20 }
```

The server performs exactly the stdin-close sequence described above internally. Skip to Step 5
once the tool call returns.

> **Pick the close delay from the host's measured stand-up, not from this document.** Measured
> 2026-09-12 at `v1.0.0-rc.5` on a warm-cache host: the whole orders suite completes in **under 18
> seconds**, so a t=20s close lands *after* completion and measures an ordinary end-of-run teardown
> — the same trap the fixed-40s note below describes, one notch earlier. On that host the
> mid-stand-up state needed **t≈8s** (containers appear ~6–9s in). Calibrate by running the suite
> once to completion first and timing it.
>
> **Capture discipline for scripted drills.** If a harness redirects the child's stdout, it MUST
> drain it concurrently or redirect to a file. Measured 2026-09-12: an undrained stdout pipe fills
> at 4096 bytes (Windows anonymous pipe), the engine wedges on a console write mid-stand-up, and
> the drill then measures the engine's own ~30s backstop force-exit instead of graceful teardown —
> a constant ~30.06s stdin-close→exit across five runs was the tell. (Silver lining, also measured:
> even wedged, the engine self-exited inside the 35s grace and DCP reaped all containers ≤5s after
> exit — nothing orphaned.) The Bash pipe idiom above with `> out.txt 2> err.txt` is safe; a
> PowerShell `Process` with `RedirectStandardOutput = $true` and no async reader is not.

**Option B — direct-CLI reproduction (no MCP client needed):** start `vouchfx run` with
`--shutdown-on-stdin-eof`, with its stdin piped from something that itself exits after N seconds —
the moment that upstream command exits, its end of the pipe closes, delivering EOF to `vouchfx`'s
stdin at exactly that point: the same signal the MCP sends by calling
`process.StandardInput.Close()` once `timeoutSeconds` elapses.

```bash
# The "{ sleep 20; }" subshell produces no output; its side of the pipe closes the instant it
# exits (~20s), delivering EOF to vouchfx's stdin at that point — reproducing the MCP's own
# stdin-close signal without needing an MCP client. Replace 20 with your calibrated close
# delay (see the note above).
{ sleep 20; } | vouchfx run samples/orders-dotnet/tests/orders.e2e.yaml \
  --events /tmp/orders-drill.jsonl \
  --shutdown-on-stdin-eof > /tmp/orders-drill.out 2> /tmp/orders-drill.err
echo "Exit code: $?"
tail -3 /tmp/orders-drill.out   # the verdict line lands in the redirected file
```

On Windows (PowerShell) — there is no equivalent pipe-EOF idiom for an external process's stdin in
PowerShell, so this drives .NET's `Process` API directly, mirroring exactly what
`VouchfxCliSuiteRunner` itself does (redirect stdin, wait, then close it):

```powershell
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "vouchfx"
foreach ($a in @(
    "run", "samples/orders-dotnet/tests/orders.e2e.yaml",
    "--events", "$env:TEMP\orders-drill.jsonl",
    "--shutdown-on-stdin-eof")) { $psi.ArgumentList.Add($a) }
$psi.RedirectStandardInput = $true
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Seconds 20       # replace with your calibrated close delay (see the note above)
$proc.StandardInput.Close()   # the graceful-stop signal — identical to VouchfxCliSuiteRunner's own call
$proc.WaitForExit()
Write-Host "Exit code: $($proc.ExitCode)"
```

**Expected outcome** depends on where the calibrated close lands (both measured 2026-09-12 at
`v1.0.0-rc.5`): a close mid-stand-up prints `topology failed to start`; a close after execution
has begun prints `Scenario '<name>': INCONCLUSIVE (pass=0 fail=0 envError=0 inconclusive=1)`.
Either is a valid abort for this drill — what matters is the teardown timing and the Docker
state, not which of the two the timing race produced. Wall clock tracks host warmth: roughly
25–40 seconds on the colder 2026-08 hosts, under 25 on the warm 2026-09 host. Both options above
deliver EOF at the chosen close delay — whichever abort phase it lands in, per the calibration
note.
A close that lands after the run already finished prints `PASS` — that run measured an ordinary
end-of-run teardown, **not** an abort, and is a false PASS for this drill: lower the close delay
and re-run.

**Current pin:** an explicit `--shutdown-on-stdin-eof` cancellation is cooperative shutdown;
require the same child PID to exit **4**, an INCONCLUSIVE outcome, and removal of its owned Docker
resources within the 35-second grace. Close stdin while that child and its topology are observed
running; closing it after completion does not exercise cancellation. Record the integer exit,
events and close-to-exit duration separately from the MCP tool's timeout result.

Native CTRL_C/SIGTERM is a separate caller/framework path with measured exit **1** at the earlier
`03b9fd643413059cfb3a5843d06770d8a7accae9` candidate; the final pin changed only .NET comments
on that path. Do not use that native exit as the expectation for this explicit stdin-EOF drill.

### Historical exit measurements

The older measurements below apply to their named prerelease pins, not the current EOF contract.

> **Historical process exit code: 0 rather than 4.** Measured 2026-08-10 on `v1.0.0-rc.3` and `v1.0.0-rc.4`:
> a cancelled run that prints `INCONCLUSIVE`, and one whose topology never finished starting, both
> exit **0** on both versions. This document asserted exit code 4; that is wrong for at least those
> two pins (earlier pins were not re-tested). Do not treat a 0 here as evidence the drill went
> wrong, and do NOT gate the drill on the exit code — judge it on the printed verdict and on the
> Docker state. (That the engine exits 0 for a non-Pass verdict is an engine-side defect, not an
> MCP one; it is out of scope for a pin bump and is recorded in `ENGINE_PIN`'s pin history.)
>
> Measured 2026-09-12 on `v1.0.0-rc.5`: one of thirteen aborts exited **1** with NO verdict line
> and no events file (teardown still clean; not reproduced in three retries). When an abort prints
> no verdict at all, judge that run on the Docker state alone and re-run the abort. The engine-side
> fix for the exits-0 behaviour landed upstream after the rc.5 tag and ships in `v1.0.0-rc.6`, so
> cancellation exit codes are expected to CHANGE at that pin. This caveat has **not yet been
> re-measured at rc.6** — until it is, keep judging the drill on the printed verdict and the Docker
> state, never on the exit code (the tracking reference lives in `ENGINE_PIN`'s pin history).

### Step 4b: the heavier scenario — abort after the topology is fully stood up

Trigger this one on **observed readiness**, not on a longer fixed delay: poll `docker ps` until all
of the suite's containers are running, then close stdin immediately (optionally after a couple of
seconds, so the engine's health gate passes and the first step starts — that is the genuinely
heaviest state, with every resource healthy and a run in flight).

> A fixed 40-second delay — which earlier revisions of this document suggested — no longer reaches
> that state at all. Measured 2026-08-10: the whole `orders-dotnet` suite completes in ~31 seconds
> wall on a warm-cache host, so a 40-second close lands after the run has already finished and
> measures an ordinary end-of-run teardown rather than a cancelled one. Readiness polling hits the
> intended state directly and does not need re-tuning as hosts get faster or slower.

### Step 5: Verify graceful teardown

Immediately after `run_suite` returns (within a few seconds), check Docker:

```bash
docker ps -a
docker network ls
```

**Expected result:** This run's owned containers and network are gone through engine teardown. For an inline topology, inspect its Aspire session network; for Compose, inspect the unique project's labelled resources. Every pre-existing unrelated resource remains unchanged.

**Acceptable timeline:** Poll every second rather than eyeballing it — "gone at exit" and "gone twelve seconds later" are different facts and a single 30-second settle check collapses them into one. Measured against `v1.0.0-rc.4` (five runs): everything was gone 0.22–1.05 s after the child exited.

Brief post-exit persistence is not by itself a failure. What *is* a failure is resources still present once the 35-second grace has elapsed. Note that with `TESTCONTAINERS_RYUK_DISABLED=true` set as Step 1 requires, "Ryuk cleaned up instead" is not an available explanation for late cleanup — check that this run created no Ryuk container. A pre-existing unrelated Ryuk container is not evidence about the drill.

### Step 6: Settle and verify no orphans

Wait 30 seconds (to be thorough) and re-check Docker:

```bash
docker ps -a
docker network ls
```

**Expected result:** All recorded resources owned by the drill are absent, and the unrelated baseline IDs and states are preserved. Do not require an otherwise empty Docker host.

### Step 7: Document the result

Record PASS only after the observations establish it:
- Record the exact engine version/full SHA, child PID, stdin-close time, integer exit and event verdict.
- Record close-to-exit time and removal times for each owned container/network, within the 35-second grace.
- Preserve the baseline IDs/states and confirm no manual cleanup was needed. A failed or unobserved check is not a pass.

## Troubleshooting

**Containers/network still present after ~30 seconds:**
- **The drill has FAILED.** The engine's teardown budget has likely exceeded the MCP's 35-second grace, or the graceful-shutdown mechanism has regressed. Do NOT merge the ENGINE_PIN bump.
- Check the released engine version's `ShutdownBackstop`/teardown budget (see `HeadlessTopology.DisposeAsync` in the engine source at `ENGINE_PIN`'s commit).
- File an issue linking this drill's failure, the engine version, and the engine source reference.

**Docker containers were removed but the network persists:**
- Check whether the network removal is still happening a few seconds later (the engine's teardown may be staggered).
- If an owned network persists beyond the 35-second grace, the drill fails. Record its ID and measured timing.

**Command hangs indefinitely:**
- The CLI process may be stuck. Record a failed drill; if cleanup is necessary, terminate only the recorded child PID (`kill <pid>` on Unix, `Stop-Process -Id <pid>` on Windows), then inspect only its recorded owned resources.
- Verify the installed CLI is actually the pinned version: `vouchfx --version` should match `ENGINE_PIN`.
- Verify Docker is reachable and working (try `docker ps`).

## Drill timing reference

Measured on a warm-cache Windows host against `v1.0.0-rc.4`, 2026-08-10 — the mid-stand-up
scenario (stdin closed at a fixed t=20s):

| Elapsed | Event |
|---------|-------|
| ~0s | Drill starts; topology stand-up begins |
| ~9s | Postgres and Kafka containers created |
| ~20s | Stdin closed; the graceful-stop signal reaches the engine |
| ~25s | Child process exits (teardown 4.6–10.1s across three runs) |
| ~25–26s | Containers and the `aspire-session-network-*` network gone (0.2–1.1s after exit) |
| ~55s | Settle check confirms zero leftover resources |

The heavier scenario (Step 4b, readiness-triggered) measured a **5.79s and 5.80s** teardown across
two runs, against the MCP's 35-second `GracefulShutdownGrace` — 29.2s of headroom. The worst
teardown observed in any of the five runs was 10.12s (24.9s of headroom). Full detail, including
a like-for-like `v1.0.0-rc.3` control run, is in `ENGINE_PIN`'s pin history.

These are single-host numbers on cached images. Treat them as the shape of a PASS, not as a
threshold: the gate is the 35-second grace, and the thing to watch across pins is the trend.

## Related documentation

- `VouchfxCliSuiteRunnerTests.GracefulShutdownGrace_Is35Seconds` — the unit test that pins the 35-second value and documents why the drill is the enforcing gate.
- `VouchfxCliSuiteRunner.GracefulShutdownGrace` — the constant definition with full remarks on the relationship to the engine's ~30-second budget.
- `docs/validation/live-validation-2026-07-21.md` — the original live validation drill performed before the graceful-shutdown feature was merged; contains historical evidence of the old force-kill-then-Ryuk path and detailed environment/baseline info.
- `ENGINE_PIN` — the version pin file; "How to advance it" section lists the drill as a mandatory step.
