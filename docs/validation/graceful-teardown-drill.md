# Graceful-teardown live drill procedure

**Purpose:** Verify that the MCP's `GracefulShutdownGrace` (35 seconds) remains safe against the current engine pin — that is, the engine's own teardown budget stays at ~30 seconds or less, so the engine self-exits (or hits its own `ShutdownBackstop` force-exit) before the MCP's 35-second grace deadline expires.

**When to run this drill:** Every time `ENGINE_PIN` is advanced. This is a MANDATORY gate before merging a pin bump (see `ENGINE_PIN`'s "How to advance it" / "Steps:" section).

**What will be validated:** The engine's graceful teardown works end-to-end on a real Docker topology: a cancelled/timed-out `run_suite` call removes containers and the Aspire session network via the engine's own shutdown mechanism, NOT via Ryuk or manual cleanup.

## Prerequisites

- The `vouchfx` CLI at **exactly** the current `ENGINE_PIN` version is installed (currently v1.0.0-speca.1).
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

Before running the drill, confirm no leftover containers or Aspire networks exist:

```bash
docker ps -a              # Should show NO running/exited vouchfx, orders-api, postgres, or kafka containers
docker network ls         # Should show NO aspire-session-network-* networks
```

Record the baseline container and network count for comparison after the drill.

### Step 3: Build the sample application image

Navigate to the `vouchfx-samples` repository and build the local image:

```bash
cd samples/orders-dotnet/app
docker build -t vouchfx-samples-orders-dotnet:local .
cd ../../../                  # Back to vouchfx-samples root
```

### Step 4: Run the drill

Execute a `run_suite` call against the orders suite with a SHORT `timeoutSeconds` value (e.g. 20 seconds) to force an abort mid-topology-stand-up:

```bash
vouchfx run samples/orders-dotnet/tests/orders.e2e.yaml --events /tmp/orders-drill.jsonl --timeout-seconds 20
```

On Windows:
```powershell
vouchfx run samples/orders-dotnet/tests/orders.e2e.yaml --events $env:TEMP\orders-drill.jsonl --timeout-seconds 20
```

**Expected outcome:** The command returns with `exit code 4` (Inconclusive / timeout), taking approximately 20–28 seconds wall clock (the topology stand-up plus the grace period for the engine's own teardown).

### Step 5: Verify graceful teardown

Immediately after `run_suite` returns (within a few seconds), check Docker:

```bash
docker ps -a
docker network ls
```

**Expected result:** Both containers AND the `aspire-session-network-*` network should be GONE — removed by the engine's own graceful teardown, NOT waiting for Ryuk.

**Acceptable timeline:** If you observe the containers/network for a few seconds and see them disappearing (a few seconds after the command returns), that is the engine's graceful teardown running to completion. If they persist for many seconds and eventually disappear, Ryuk is likely doing the cleanup (the engine gave up and you have exceeded the grace period — **this is a FAILURE**, indicating the engine's teardown budget has diverged).

### Step 6: Settle and verify no orphans

Wait 30 seconds (to be thorough) and re-check Docker:

```bash
docker ps -a
docker network ls
```

**Expected result:** Exactly the same as Step 2 (your recorded baseline). Zero vouchfx/orders/postgres/kafka containers and zero `aspire-session-network-*` networks.

### Step 7: Document the result

Record that the drill PASSED:
- The engine removed containers and the Aspire session network within the graceful-shutdown grace period.
- No manual Docker cleanup was required.
- Optionally, note the exact timestamp/duration for reference in the PR.

## Troubleshooting

**Containers/network still present after ~30 seconds:**
- **The drill has FAILED.** The engine's teardown budget has likely exceeded the MCP's 35-second grace, or the graceful-shutdown mechanism has regressed. Do NOT merge the ENGINE_PIN bump.
- Check the released engine version's `ShutdownBackstop`/teardown budget (see `HeadlessTopology.DisposeAsync` in the engine source at `ENGINE_PIN`'s commit).
- File an issue linking this drill's failure, the engine version, and the engine source reference.

**Docker containers were removed but the network persists:**
- Check whether the network removal is still happening a few seconds later (the engine's teardown may be staggered).
- If the network persists beyond the full ~28 second window, this is also a FAILURE — the engine's graceful phase did not complete within the grace period.

**Command hangs indefinitely:**
- The CLI process may be stuck. Manually kill it (`pkill vouchfx` on Unix, `Stop-Process -Name dotnet` on Windows).
- Verify the installed CLI is actually the pinned version: `vouchfx --version` should match `ENGINE_PIN`.
- Verify Docker is reachable and working (try `docker ps`).

## Drill timing reference

For context, a **PASS** on a healthy system typically looks like:

| Elapsed | Event |
|---------|-------|
| ~0s | Drill starts; topology stand-up begins |
| ~6s | Postgres container created |
| ~18s | Orders API container created |
| ~20s | Timeout fires; `run_suite` cancellation signals engine shutdown |
| ~26s | Containers removed by engine's graceful teardown |
| ~28s | Aspire session network removed; `run_suite` returns (exit code 4) |
| ~30s+ | Settle check confirms zero leftover resources |

## Related documentation

- `VouchfxCliSuiteRunnerTests.GracefulShutdownGrace_Is35Seconds` — the unit test that pins the 35-second value and documents why the drill is the enforcing gate.
- `VouchfxCliSuiteRunner.GracefulShutdownGrace` — the constant definition with full remarks on the relationship to the engine's ~30-second budget.
- `docs/validation/live-validation-2026-07-21.md` — the original live validation drill performed before the graceful-shutdown feature was merged; contains historical evidence of the old force-kill-then-Ryuk path and detailed environment/baseline info.
- `ENGINE_PIN` — the version pin file; "How to advance it" section lists the drill as a mandatory step.
