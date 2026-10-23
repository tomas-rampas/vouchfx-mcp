# Changelog

## Unreleased

- Adopt published Core 1.1.0 with byte-exact v1 and v2 language schemas. Validation selects v2
  only for explicit `metadata.schemaVersion: v2`; the omitted version remains v1.
- `get_schema` accepts optional `version: v1|v2`, and `vouchfx://schema/v2` serves v2 offline.
  Live optional cross-verification remains v1-only. Validation and schema tool metadata report
  the selected language version. Event wire versions remain independent.
- `scaffold_suite` continues to generate v1 suites. For per-file telemetry opt-out, explicitly
  select v2 and set `metadata.telemetry: false`. Unknown language versions are refused.
- Pin the engine to Core 1.2.0. Its vendored schema and documents are unchanged from 1.1.0. Its CLI
  can also load trusted external providers from `VOUCHFX_PROVIDERS` and `~/.vouchfx/providers`. The
  CLI-backed tools see them. `run_suite` now uses the pinned CLI's compile validation before
  execution, including trusted external providers. Offline `validate_suite`, `normalize_suite` and
  `get_schema` remain scoped to bundled step types. Local path/access/size guards precede any CLI
  invocation; the single-flight claim covers pin verification, pre-flight and execution. Invalid
  CLI reports keep the `VFX-D-1100` envelope with fixed stage diagnostics; arbitrary compiler/provider
  messages and paths are withheld. Unusable CLI reports fail closed with `VFX-E-1901`, and the
  30-second per-suite CLI budget reports `VFX-E-1150`; the whole-call budget still applies.
  The semantic `VFX-D-1201` finding no longer suggests renaming external types, and `VFX-D-1106`
  names external providers among its causes.
- The install and troubleshooting guides' CLI commands named 1.0.7 after the 1.1.0 pin; they now
  name the pinned version, and a test keeps them in step with `ENGINE_PIN`.


## [1.0.3](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.0.3) — 2026-10-17

### Changed

- Pin the engine source and vendored-resource drift check to Core v1.0.7. Raw event relay
  preserves its additive `security-confirmation` records; summary parsing leaves their
  probe level and replay status independent of scenario verdicts.
- The [release workflow](https://github.com/tomas-rampas/vouchfx-mcp/actions/runs/39056256209)
  passed all three jobs, including six required real-CLI methods with zero skips. The full
  suite reported 3,182 passed and five optional skips; the required methods measured fourteen
  CLI invocations and two storage generations.

## [1.0.2](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.0.2) — 2026-10-17

### Changed

- Pin the engine source and vendored-resource drift check to Core v1.0.6. Its active stdin-EOF
  cancellation path exits with code 4 while preserving the `INCONCLUSIVE` run result.

## [1.0.1](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.0.1) — 2026-10-16

### Changed

- Adopt stable Core v1.0.5 and align vendored recipes and installation, validation, and
  troubleshooting guidance with that pin.

## [1.0.0](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.0.0) — 2026-10-15

### Added

- First stable `Vouchfx.Mcp` dotnet tool release on NuGet.org, with eighteen tools, four prompts,
  MCP resources, stdio and optional bearer-authenticated HTTP transport. Uses the separately
  installed vouchfx CLI pinned to `1.0.2` in `ENGINE_PIN`.
- The [release workflow](https://github.com/tomas-rampas/vouchfx-mcp/actions/runs/38800401731) completed its build/test/pack,
  required real-CLI probes and publication jobs.

### Fixed

- Stdio client disconnection now starts host shutdown while `run_suite` is active. The existing
  cancellation path waits for CLI teardown before recording the aborted suite as `Inconclusive`;
  the aggregate preserves any earlier `Fail` or `EnvironmentError` verdict. The host
  allows 50 seconds for graceful teardown, kill confirmation, output draining and persistence;
  this is a shutdown budget, not a promise that every environment stops successfully.
