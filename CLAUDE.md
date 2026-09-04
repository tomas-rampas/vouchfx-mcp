# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

A local stdio MCP (Model Context Protocol) server, written in C# / .NET 8, that wraps the **published `vouchfx` dotnet tool** as a subprocess — it never builds the engine from source and never re-implements engine logic in-process ("CLI and MCP must not drift" is the governing invariant). It ships twelve tools, two vendored-document resources, and a templated `vouchfx-docs:///errors/{code}` error-catalogue resource family, packaged as the `Vouchfx.Mcp` dotnet tool (command `vouchfx-mcp`), not yet published to NuGet.org. Sprint 1 of the vouchfx.ai plan (contract foundations: `ToolMeta`, the `VFX-E-####`/`VFX-D-####` taxonomy, `explain_diagnostic`) has landed on this codebase.

## Commands

```bash
dotnet build Vouchfx.Mcp.sln -c Release -warnaserror   # 0-warning gate (TreatWarningsAsErrors is on anyway)
dotnet test Vouchfx.Mcp.sln -c Release --no-build      # build first: Real* tests spawn the built Vouchfx.Mcp.dll at the same -c
dotnet test tests/Vouchfx.Mcp.Tests/Vouchfx.Mcp.Tests.csproj --filter "FullyQualifiedName~SuiteValidatorTests"   # one class
dotnet test tests/Vouchfx.Mcp.Tests/Vouchfx.Mcp.Tests.csproj --filter "FullyQualifiedName~ClassName.MethodName"  # one test
dotnet format Vouchfx.Mcp.sln --verify-no-changes      # CI format gate; run plain `dotnet format` to fix
dotnet pack src/Vouchfx.Mcp/Vouchfx.Mcp.csproj -c Release -o artifacts/nuget   # tool nupkg (default 0.1.0-alpha.1)
pwsh ./scripts/sync-vendored.ps1 -Verify               # vendored/ drift gate (CI runs this); -Update is the ONLY way to refresh vendored/
python scripts/build_site.py                           # docs site → _site/ (needs markdown, pygments, vouchfx-site-tools)
```

SDK: .NET 8 (`global.json`: 8.0.400, rollForward latestFeature). Shared MSBuild properties live in the root `Directory.Build.props` (net8.0, nullable, warnings-as-errors, `IsPackable=false` default); per-project files deliberately do not repeat them, and `tests/Directory.Build.props` must keep its explicit import of the root file.

CI (`.github/workflows/build.yml`) runs three parallel jobs: build+format+test, the vendored-drift gate, and a pack job that unzips the nupkg and asserts the tool manifest, `ENGINE_PIN`, and entry DLL are present. `release.yml` does the real tag-versioned pack (`-p:PackageVersion=<semver-from-tag>`).

## Engine pin and vendored artefacts

- **`ENGINE_PIN`** (repo root): first line is `<version> <full-40-char-commit-sha>`. The *version* gates the runtime CLI handshake (`CliPinVerifier` — fail-closed for CLI-dependent tools); the *SHA* is what `vendored/` is drift-gated against in CI. The file ships beside the built exe (a `Content` item); `EnginePin.Load` at startup is fatal if it is missing or malformed. The file's own comment block documents how to advance it — including a real gotcha about annotated vs lightweight tags (use `gh api repos/tomas-rampas/vouchfx/commits/<tag> --jq .sha`).
- **`vendored/`** (composed schema, language reference, recipes): byte-exact copies from the engine repo at the pinned SHA, embedded into the assembly as manifest resources with explicit `LogicalName`s. **Never hand-edit them, and never regenerate the schema via `vouchfx schema`** (CRLF/trailing-newline differences fail the SHA-256 drift gate) — `pwsh ./scripts/sync-vendored.ps1 -Update` is the only supported refresh path. See `vendored/README.md`.

## Architecture

`Program.cs` has two modes: normal stdio MCP server, and a hidden one-shot `--validate-worker <source> [--level=<level>]` mode checked before anything else (validate_suite's process-isolation boundary; `<source>` is a suite path or `--yaml-stdin`, in which case the suite text arrives on the worker's stdin). **stdout is the JSON-RPC channel and nothing else may ever write to it** — all logging goes to stderr; tests assert stdout cleanliness.

`VouchfxMcpServerRegistration.AddVouchfxMcpServer` is the single DI configuration used by both production startup and the test harness — there is no second copy to drift. `Tools/ToolRegistry` aggregates the twelve tools (append-only ordering — the tool-count lock test updates with every addition); each tool's name/description/input schema is owned by that tool's own `Create()` factory in `Tools/`. Error and diagnostic emissions flow through `Contracts/VfxCodeCatalogue` (the single kind→code registry) — never mint a `VFX-*` code outside it; the bidirectional catalogue completeness gate fails CI on any code without a `docs/errors/` page or page without an emitting site.

The twelve tools split into four dependency classes (`explain_diagnostic` joins the CLI-free class — it serves the embedded error catalogue and never probes the engine):

- **CLI-free** (work offline, no engine install): `validate_suite` — `Validation/` pipeline (YamlDotNet parse with `YamlSafetyGuard`/`PathSafetyGuard` hardening, YAML→JSON, JsonSchema.Net against the vendored schema), executed in a spawned worker process (`ValidationWorkerClient` — wall-clock timeout, process-tree kill). Takes either a `path` or inline `yaml` (never both) and a `level` selector (`schema`/`semantic`/`full`, default `full`) that gates the passes but never the safety guards; returns two permanently separate channels — `errors` (schema) and `semanticDiagnostics` (this server's own advice) — plus a `summary` digest of the document, all from one parse. `Validation/Semantics/` is the rule seam US-S2-03 fills: a rule gets the parsed document, the caller-facing digest, and `SuiteFacts` (the complete, uncapped, unfiltered name sets — the set-membership authority, never serialised). `normalize_suite` — `Normalization/`, US-S2-04's read-only replacement for the spec's dropped `write_spec`: same worker isolation and same exactly-one-of-`path`/`yaml` rule (VFX-E-1152) as `validate_suite`, always at level `full`, returning `{normalizedYaml, validation}` where `validation` is the whole `SuiteAnalysis` shape. Canonical key order is derived from the vendored schema's own `properties` declarations (`CanonicalKeyOrder`); normalization **drops comments** on the pinned YamlDotNet (measured — see `SuiteNormalizer`'s remarks) and is therefore opt-in behind `normalize: true`. The server never writes the file — the host does. `search_docs` — `Docs/` search over the vendored markdown.
- **Pinned-CLI-backed** (fail closed if `vouchfx` is missing or its version mismatches `ENGINE_PIN`): `list_step_types`/`describe_step_type` (live catalogue via `vouchfx list --json`, `Validation/LiveStepCatalogue`), `plan_coverage` (`Planning/`, `vouchfx plan --json`), `scaffold_suite` (`Scaffold/`, `vouchfx scaffold --intent`), `run_suite` (`Run/`, validates the suite first, spawns the CLI, streams best-effort progress, returns the taxonomy-faithful verdict). The `Cli/` directory holds the shared subprocess plumbing (`VouchfxCliProcessRunner`, `CliPinVerifier`, path resolution, version normalisation).
- **CLI-optional** (never fails for want of an engine, but uses one when present): `get_schema` (`Schema/`) serves the vendored composed schema — whole document or one addressable section, as JSON Schema or an 8 KB markdown digest — entirely offline; when the pinned CLI *is* present (`CliPinVerifier` via `Validation/LiveSchemaDocument`), the embedded copy is cross-verified against that engine's `vouchfx schema` export and any divergence is surfaced as a `VFX-D-1106` diagnostic on the still-successful result. The CLI contributes a check, never the content: the vendored copy is always what gets served, so `get_schema` and `validate_suite` can never disagree.
- **Events-file readers** (never re-run anything): `explain_run` and `diagnose_run` (`Diagnosis/`) parse a run's JSON Lines event stream; `explain_run` defaults to the most recent `run_suite` this session (`ILastRunTracker`). `diagnose_run` (Healer) adds patch proposals **only for Fail verdicts**; EnvironmentError gets infrastructure guidance, never YAML rewrites; proposals are never auto-applied.

Hard invariants (all have guard tests):

- **Verdict taxonomy is preserved faithfully**: pass / fail / environment error / inconclusive. Docker-unavailable, timeout, or cancellation is an environment error or inconclusive — never reported as a test failure.
- **Read-only**: the server never writes, modifies, or deletes a suite file — `ReadOnlySourceGuardTests` holds this structurally, scanning `src/` for filesystem-mutation APIs with a fail-closed exact-equality list (only `RunSuiteOrchestrator` and `ScaffoldSuiteOrchestrator` may touch temp artefacts they created themselves; `normalize_suite`'s whole pipeline may not write at all). It never hosts a model — free-text intent is host-LLM territory; `scaffold_suite` takes structured input only.
- **Secret hygiene**: never resolve `${secret:...}`, never read or echo this process's environment into any tool result/progress/resource. The engine is the sole redaction authority; already-redacted event fields are relayed bounded and control-character-sanitised (`TextSanitiser`, `BoundedStreamReader`), never re-redacted or re-resolved. Enforced by `SecretHygieneSourceGuardTests` and `RealSecretHygieneMcpTests`.

## Testing conventions

- **No test depends on the real `vouchfx` CLI or Docker.** `McpTestHarness` hosts the real server over in-memory paired streams with a `FakeVouchfxCli` and `FakeSuiteRunner` by default. The `Real*` test-name prefix means "real MCP wire protocol" or "real spawned *vouchfx-mcp* process" (`RepoLayout` derives the built DLL paths — hence build-before-test at the same configuration), **not** the real engine CLI.
- The three exceptions, `RealPlanCoverageAgainstPinnedCliTests`, `RealValidateAgainstPinnedCliTests`, and `RealGetSchemaAgainstPinnedCliTests`, self-gate by running the production `CliPinVerifier` against the real PATH and silently pass when the pinned CLI is not installed — do not invent a new skip mechanism for CLI-dependent tests; reuse that pattern.
- `tests/StdinEofChildFixture/` is a tiny child-process fixture for exercising `VouchfxCliSuiteRunner`'s graceful-stop-then-force-kill against a real OS process.
- `McpServerSkeletonTests` deliberately hardcodes `"0.1.0"` to catch edits to `<Version>`.

## Versioning and dependency discipline

- `<Version>` (0.1.0) in `Vouchfx.Mcp.csproj` is the single source of truth for `serverInfo.version` in the MCP handshake and is **deliberately decoupled** from `<PackageVersion>` (0.1.0-alpha.1 default, overridden per tag by release.yml). Never let one change as a side effect of the other.
- Package versions are exact-pinned, no wildcards. YamlDotNet is pinned to the same version the engine repo pins (fleet consistency — validation verdicts must not diverge on YAML scalar resolution); JsonSchema.Net is currently deliberately ahead of the engine's pin (see the csproj comment before touching either).

## Working style in this codebase

- The codebase carries unusually dense rationale comments — files, csproj items, and workflows explain *why* each decision was made and what breaks otherwise. When editing, read the surrounding comment block first (it usually names the constraint you are about to violate) and keep it truthful; several comments encode measured facts ("Verified by re-packing…", dated gotchas).
- Development is spec-first: features land against approved specs in a spec → build → review loop. `specs/` is gitignored (maintainer-local, mirrors the engine repo's convention) and holds the working spec material — currently including the fleet-level vouchfx.ai spec/plan proposals (`specs/vouchfx-ai-mcp-spec.md`, `specs/vouchfx-ai-mcp-plan.md`). Requirement IDs (REQ-NNN/EDGE-NNN) appear throughout code comments and test names. Never move internal material under `docs/` — everything tracked there is auto-published to the public site (see `scripts/build_site.py`).
- `docs/*.md` (overview, install, tools-and-resources, troubleshooting, implementation-map) and `docs/errors/*.md` (the diagnostic catalogue pages) are the source for the published site at vouchfx-mcp.vouchfx.io; the README's "Status" block is kept current with tool-level detail.
