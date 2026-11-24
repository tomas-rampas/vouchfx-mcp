# Changelog

Versioned sections marked `Unreleased` are release preparations, not published packages.
Public availability requires a published GitHub Release and the package's availability on NuGet.org.

## Unreleased

### Changed

- Correct the troubleshooting guide's account of MCP Tasks: the server does serve protocol revision
  2026-07-28 (through `server/discover`), the old signal could never fire, and the guide now records what
  was measured on SDK 2.4.0 (22 November 2026) and the two things that stand in the way. The guide
  also says now that Tasks first appeared in the 2025-11-25 revision of the specification and became
  an extension in the 2026-07-28 revision. Polling and the server's behaviour are unchanged.
- Correct the runtime requirement of the pinned `vouchfx` CLI in the installation guide, the
  troubleshooting guide, two error pages, the README and the package README: the CLI needs the
  ASP.NET Core 10 shared framework as well as the .NET 10 runtime, as the MCP server does.
- Update dependencies: the exact-pinned `Microsoft.Extensions.Hosting`,
  `Microsoft.Extensions.FileSystemGlobbing`, `System.Diagnostics.DiagnosticSource` and
  `System.Text.Encoding.CodePages` to 10.0.15 (from 10.0.14), `JsonSchema.Net` to 9.7.0 (from 9.6.0),
  and the test-only `coverlet.collector` and `Microsoft.NET.Test.Sdk`. `JsonSchema.Net` 9.7.0 brings
  `JsonPointer.Net` 7.1.0 and `Json.More.Net` 3.1.0 (from 7.0.4 and 3.0.1). The 11.0.0 releases of the
  four .NET packages are not taken: on `net10.0` they would add 33 assemblies to the tool, so
  Dependabot now skips their major updates.

## [1.4.0](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.4.0) — 2026-11-21

This release jumps from 1.1.0 to 1.4.0 because its version is aligned with the engine release it
pins, `vouchfx` 1.4.0, the first built for .NET 10.

### Changed

- **Breaking: the runtime requirement changes.** MCP users now need the .NET 10 runtime and the
  ASP.NET Core 10 shared framework; .NET 8 and 9 are no longer supported. Replace an existing
  `vouchfx` CLI install with the pinned one: run `dotnet tool uninstall --global vouchfx`, then
  `dotnet tool install --global vouchfx --version 1.4.0`. No `--allow-roll-forward` is needed. A
  `net8.0` CLI such as 1.2.0 cannot start without .NET 8 and is not the pinned version, so the
  CLI-backed tools report `VFX-E-1401` or a version mismatch until it is replaced.
- Pin the engine to 1.4.0, the first engine release that targets .NET 10. The vendored composed
  schemas (v1 and v2), language reference and recipes are all refreshed from that engine commit:
  among other changes, the v1 schema's description of `metadata.schemaVersion` now says that omitting it
  means v1, the `http.rest` step's `body` description (also in the language reference) documents how
  inline YAML bodies are serialised, and the recipes' Vault example is corrected (`VAULT_ADDR`,
  `VAULT_TOKEN`, `vault/<kvPath>#<field>` references). All four vendored files differ from their 1.2.0
  copies; the resync was checked only by the vendored-drift script, not by a before-and-after comparison
  of validation verdicts.
- The pinned engine 1.4.0 carries its own upgrade notes, which reach anyone who runs suites through
  `run_suite`: `security.caCert` no longer accepts a PKCS#12 (`.pfx`) bundle, and `http.rest` structured
  request bodies now follow YAML's meaning, so some suites send different bytes. See the engine's
  1.4.0 changelog; neither change was measured through this server.
- The tool now targets `net10.0`; the repository SDK pin, CI and release workflow move to .NET 10 with
  it. Because the pinned CLI is itself a `net10.0` build, the CI, release and compatibility lanes install it
  without `--allow-roll-forward`, and the server still never sets `DOTNET_ROLL_FORWARD` for the CLI.
  The CLI fix-it messages (pin verifier, `plan_coverage`, `scaffold_suite`) now say the pinned CLI
  targets .NET 10 and needs the .NET 10 runtime, and give no roll-forward flag; the `VFX-E-1401` and
  `VFX-D-1106` pages and the install, troubleshooting and package guides say the same. The "not found or
  could not start" handling is unchanged.
- Update dependencies since 1.1.0: `ModelContextProtocol` and `ModelContextProtocol.AspNetCore`
  to 2.4.0 (from 2.2.0), `JsonSchema.Net` to 9.6.0 (from 9.4.1), and the exact-pinned
  `Microsoft.Extensions.*` packages to 10.0.14 (from 10.0.13). Humanizer.Core, reached through
  JsonSchema.Net's JsonPointer.Net, resolves to its 3.x line (3.0.10; it was 2.14.1 in 1.1.0).

## [1.1.0](https://github.com/tomas-rampas/vouchfx-mcp/releases/tag/v1.1.0) — 2026-10-25

### Changed

- Serve byte-exact v1 and v2 language schemas from pinned Core 1.2.0. Validation selects v2
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

### Fixed

- Preserve multi-suite invalid-suite and omitted-finding counts when local size guards or CLI
  compile validation reject suites. Malformed reports, including overlong reported paths, fail
  closed without exposing arbitrary compiler/provider messages or host paths.
- Keep CLI output draining within the invocation's timeout and cancellation budget even when a
  descendant retains an output handle after the CLI process exits.


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
