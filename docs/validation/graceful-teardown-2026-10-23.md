# Graceful-teardown measurement — 2026-10-23

Measured result: the v1.2.0 pin passed an uncancelled control and both observed cancellation
phases of the [graceful-teardown drill](graceful-teardown-drill.md). This completes the current-pin
obligation; it does not retrospectively establish a pass at rc.6 or any other intervening pin.

## Scope and method

- Published engine: `1.2.0+0af21de5bd0a4e91ca002e03e6c256bc4e8ca4e8`.
- Sample source: `tomas-rampas/vouchfx-samples` at
  `0fc7426bb28be793192593c7ccb7179442210f12`, orders-dotnet inline topology.
- Host: Windows, Docker Desktop Linux containers; Docker server `29.6.1 linux/amd64`.
- Method: documented Option B, direct CLI with `--shutdown-on-stdin-eof`. The observer closed
  each abort's recorded child stdin while that same process and its topology were running.
  No MCP server or actual MCP tool timeout was invoked.
- Both child stdout and stderr were drained to files. Ryuk was disabled, no external providers
  were configured, and no forced kill or manual Docker cleanup was used.
- This sample has no TLS security declaration. The results establish this one host and inline
  orders topology, not Compose, TLS, every Docker platform, or end-to-end MCP timeout behaviour.

## Accepted measurements

All durations are measured elapsed seconds. Removal bounds are relative to stdin EOF and cover
all observed owned containers, the owned network and owned volumes.

| Measurement | Child PID | Integer exit / outcome evidence | Exit after EOF | Owned resources observed | All owned resources observed absent by |
| --- | --- | --- | --- | --- | --- |
| Uncancelled control | 29752 | 0 / PASS archive | Not applicable; total runtime 31.922 s | 3 containers, 1 network, 3 volumes | Normal teardown; baseline restored |
| Mid-stand-up abort | 37768 | 4 / fixed early Inconclusive stdout notice; no JSON archive | 10.141 s | 2 containers, 1 network, 3 volumes | 13.985 s |
| Ready, execution in flight | 45160 | 4 / INCONCLUSIVE scenario-completed archive | 5.875 s | 3 containers, 1 network, 3 volumes | 4.860 s |

Mid-stand-up EOF was recorded at `2026-10-23T08:05:57.603647Z`, after two running owned
containers and the owned network were observed, before any step started. The CLI printed:

> vouchfx run: the run was shut down because stdin reached EOF or could not be read, which --shutdown-on-stdin-eof treats alike, before it reached a verdict.  Stopping on either is what that flag arranges; it is not an engine or provider defect - there is nothing to report.  Reported as Inconclusive (section 12.1).

This early catch produced no JSON archive: the notice and exit 4 are the outcome evidence,
not an invented scenario event. The ready abort recorded EOF at `2026-10-23T08:07:51.986786Z`,
after all three owned containers were running and `step-started` was present; its archive
contains the INCONCLUSIVE scenario-completed event.

The cleanup values are conservative observation upper bounds, not exact deletion instants.
Maximum observed teardown sampling gaps were 0.828 s for mid-stand-up and 1.079 s for the ready
abort. The latter exceeds the one-second observation target by 0.079 s; that target is unchanged.
Neither gap changes the measured result against the unchanged 35-second grace: both child exit
and confirmed owned-resource absence occurred inside it. Resources can disappear before process
exit, as observed in the ready case.

## Baseline and observer checks

The host baseline contained zero containers, three networks and eleven pre-existing volumes.
After each accepted measurement, a 30-second settle check confirmed exact equality of global
before/after resource IDs and states, with no extra resources. The whole-session baseline also
matched exactly. Every recorded owned resource was absent, no new Ryuk container appeared,
and no unrelated baseline resource was removed or changed.

Two earlier observer attempts are explicitly excluded from the passes above: one cancelled
before the ownership network was observed and lacked phase evidence; the other failed with a
`ValueError` during a Docker removal race. The corrected observer passed three mocked removal-race
probes (exit 0). The control and the two accepted abort measurements establish the live result;
the mocked probes validate observer handling, not engine cleanup.

## Evidence fingerprints

Raw captures remain private: they contain host-specific data and are not publication artefacts.
These SHA-256 fingerprints identify the retained raw reports without publishing their contents.

| Report | SHA-256 |
| --- | --- |
| Control | `0ea4c836e9d0427fc62a0ab97008b930fc7c87fbcacdb7a2a35689f5d09ddba1` |
| Mid-stand-up | `e6f73169cf8bc1cb0752d554a0079b111079b5ccdb92520fe9da6f9972b3d261` |
| Ready/in-flight | `603a126c0735412df7f56f95d03993c66f7c731380606b22dcec36029b9c77b7` |
