# Install & registration

## Prerequisites

- The **.NET 8 SDK**, to install and run the `vouchfx-mcp` tool itself.
- The **ASP.NET Core 8 shared runtime**. This applies to *every* user, not just those enabling the
  HTTP transport: the built application declares a framework reference to `Microsoft.AspNetCore.App`,
  so the runtime must be present for the process to start at all. **A documented install already
  satisfies this** — `dotnet tool install` requires the .NET SDK, and the SDK always carries the
  ASP.NET Core shared runtime, so there is normally nothing extra to do. It is listed here because a
  stripped-down runtime-only host is the one environment where it bites. See
  [Runtime requirement (measured)](#runtime-requirement-measured) for the evidence and the fix.
- For `run_suite`, `list_step_types`, `describe_step_type`, `plan_coverage`, and `scaffold_suite`: the
  [`vouchfx`](https://www.nuget.org/packages/vouchfx) CLI installed and on `PATH`, at the exact
  version this server is pinned to (see [the engine pin](overview.md#the-engine-pin)). Catalogue tools
  need an engine with Spec A rich `list --json` (see [minimum engine](overview.md#minimum-engine-for-the-live-catalogue)).
  `plan_coverage` needs the M3 Planner (`vouchfx plan` — see [minimum engine for plan_coverage](overview.md#minimum-engine-for-plan_coverage-planner)).
  `scaffold_suite` needs Spec B (`vouchfx scaffold` — see [minimum engine for scaffold](overview.md#minimum-engine-for-scaffold-generator)).
  For `run_suite` container topologies: a running Docker engine. The pinned engine
  bundles matching DCP tools on supported platforms (see [DCP tools](#dcp-tools-for-the-pinned-engine)).
  `validate_suite`, `search_docs`, and `explain_diagnostic` work without the CLI.

## Install the vouchfx-mcp tool

The published package and the current source checkout have different engine pins. Choose one
route and keep its MCP package and CLI together; installing the newest CLI independently does not
make an older MCP package compatible.

### Published package: MCP 1.0.3 with CLI 1.0.7

Install [`Vouchfx.Mcp` 1.0.3](https://www.nuget.org/packages/Vouchfx.Mcp/1.0.3) and its matching
[`vouchfx` 1.0.7](https://www.nuget.org/packages/vouchfx/1.0.7) CLI:

```bash
dotnet tool install --global Vouchfx.Mcp --version 1.0.3
dotnet tool install --global vouchfx --version 1.0.7
vouchfx --version
```

If either global tool is already installed at a different version, uninstall that tool with
`dotnet tool uninstall --global Vouchfx.Mcp` or `dotnet tool uninstall --global vouchfx`,
then install the selected version above. This also handles switching from a newer CLI.

The CLI output must be `1.0.7+20d837a2f735c1c785db6593b58ebe10b3dd9f05`, matching the
[ENGINE_PIN shipped with MCP 1.0.3](https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.0.3/ENGINE_PIN).
MCP 1.0.0 instead requires CLI `1.0.2+7fda208c965d0f61c84902aca852e37d490e1c76`;
see its [release pin](https://github.com/tomas-rampas/vouchfx-mcp/blob/v1.0.0/ENGINE_PIN).
Neither published pairing accepts CLI 1.2.0. Compatibility checks compare the normalised version;
compare the full commit manually as well. Continue with client registration after installing this pair.

### Current source or private-feed package

The remainder of this installation section describes building the selected source checkout, whose
`ENGINE_PIN` currently names CLI **1.2.0**. It does not describe the published MCP 1.0.3 package.

> **Optional source/private-feed installation.** For contributor validation, use matching source or a private package
> feed. Set `MCP_SOURCE_COMMIT` to the exact MCP commit selected in your release metadata.
> Use a fresh checkout and new cache, package and tool directories. MCP restores third-party
> dependencies from NuGet.org; it does not reference `Vouchfx.Sdk` or `Vouchfx.Sdk.Testing`.

```bash
git clone https://github.com/tomas-rampas/vouchfx-mcp.git
cd vouchfx-mcp
git checkout --detach "$MCP_SOURCE_COMMIT"
dotnet restore src/Vouchfx.Mcp -p:PackageVersion=1.0.0 --packages ./nuget-cache --source https://api.nuget.org/v3/index.json
dotnet pack src/Vouchfx.Mcp -c Release -p:PackageVersion=1.0.0 --no-restore -o ./nupkg
cat > ./NuGet.Tool.Config <<'XML'
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="./nupkg" />
  </packageSources>
</configuration>
XML
dotnet tool install --tool-path ./mcp-tools --configfile ./NuGet.Tool.Config Vouchfx.Mcp --version 1.0.0
```

The tool installation above uses only the new local feed, so an existing public package
cannot replace the selected source package. The package version `1.0.0` does not change
`serverInfo.version` (`0.1.0`), which is separate from the negotiated MCP `protocolVersion`.
Add the selected tool directory to the MCP client process's `PATH`.

**Optional private Core feed for provider projects.** For contributor validation against a private
Core build, a project that references `Vouchfx.Sdk`, `Vouchfx.Sdk.Testing` or other Core packages can
use the following configuration. Replace the private feed path, save it as `NuGet.Core.Config`,
and restore that project with `--configfile ./NuGet.Core.Config --packages ./provider-cache`, using
a new cache directory. The exact Core package IDs select the private feed; community providers and other dependencies
use NuGet.org. This mapping is unnecessary for MCP's third-party-only dependency graph.

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="core" value="/absolute/path/to/sdk-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="core">
      <package pattern="Vouchfx.Sdk" />
      <package pattern="Vouchfx.Sdk.Testing" />
      <package pattern="Vouchfx.Engine.Abstractions" />
      <package pattern="Vouchfx.Engine.Authoring" />
      <package pattern="Vouchfx.Engine.Compilation" />
      <package pattern="Vouchfx.Engine.Planning" />
      <package pattern="vouchfx" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

## Install the CLI for the selected source checkout

For the source/private-feed route above, this checkout targets **vouchfx 1.2.0**, with the full engine commit recorded in
[`ENGINE_PIN`](https://github.com/tomas-rampas/vouchfx-mcp/blob/main/ENGINE_PIN).
Install the matching CLI from NuGet.org, or update an existing installation:

```bash
dotnet tool install --global vouchfx --version 1.2.0
# If vouchfx is already installed:
dotnet tool update --global vouchfx --version 1.2.0
vouchfx --version
```

The full output must be `1.2.0+<full commit SHA from ENGINE_PIN>`. Verify both fields before
starting the MCP server; an older public engine, including `1.0.0-rc.6`, does not satisfy this pin.

### Optional source/private-feed CLI installation

For contributor validation, set `CORE_SOURCE_COMMIT` to the full commit in `ENGINE_PIN`, clone
the [engine source](https://github.com/tomas-rampas/vouchfx), and check out that exact commit.
Use the .NET 8 SDK selected by its `global.json` to prepare a new local package feed:

```bash
git clone https://github.com/tomas-rampas/vouchfx.git vouchfx
cd vouchfx
git checkout --detach "$CORE_SOURCE_COMMIT"
dotnet pack src/Cli/Vouchfx.Cli -c Release -p:Version=1.2.0 -p:PackageVersion=1.2.0 -o ./nupkg
```

Install into a new local tool directory. Save a configuration containing only the selected
private feed, replacing the feed path with the absolute directory containing that package:

```bash
cat > ./NuGet.Cli.Tool.Config <<'XML'
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="/absolute/path/to/nupkg" />
  </packageSources>
</configuration>
XML
dotnet tool install --tool-path ./vouchfx-tools --configfile ./NuGet.Cli.Tool.Config vouchfx --version 1.2.0
./vouchfx-tools/vouchfx --version
```

Manually compare the full `--version` output with
`1.2.0+<full commit SHA from ENGINE_PIN>` to verify both the version and commit SHA.
On Windows, invoke `./vouchfx-tools/vouchfx.exe`. Add the absolute tool directory to the MCP
server process's `PATH`; a client launched from another shell may need it in its server
environment configuration. `run_suite`, `list_step_types`, `describe_step_type`,
`plan_coverage`, and `scaffold_suite` automatically check the normalized version field and
refuse a version mismatch. That handshake does not compare the commit SHA; see
[Troubleshooting](troubleshooting.md#cli-pin-version-mismatch).

### DCP tools for the pinned engine

The CLI versions in both installation routes above include their version-matched DCP tools for Windows x64, Linux x64,
and macOS x64/arm64. A supported installation needs Docker, but no separate Aspire restore or
`ASPIRE_DCP_PATH` override. Catalogue, validation and existing-event inspection do not start a
topology and need no DCP tools. Keep an override only when deliberately testing alternate tools.

### Historical published fallback: rc.6

The published engine remains available for **older MCP checkouts whose `ENGINE_PIN` matches rc.6**:

```bash
dotnet tool install --global vouchfx --version 1.0.0-rc.6
```

Published `1.0.0-rc.6` does not bundle its matching Aspire DCP tools. On a clean machine,
installing the CLI and starting Docker is insufficient for `run_suite` to start a container
topology. Provision the exact Aspire **13.4.2** tools for your platform and set
`ASPIRE_DCP_PATH` to the directory containing `dcp`/`dcp.exe`, before starting the MCP server:

```powershell
# Example after the matching Windows x64 tools have been provisioned:
$env:ASPIRE_DCP_PATH = "$env:USERPROFILE/.nuget/packages/aspire.hosting.orchestration.win-x64/13.4.2/tools"
```

```bash
# Replace with the existing, matching native-tools directory for this host:
export ASPIRE_DCP_PATH='/path/to/aspire-13.4.2/tools'
```

Setting this variable does not install the tools. The server passes it to the CLI subprocess;
an MCP client that does not inherit your shell environment must supply it in the server's
environment configuration. Follow the engine's
[DCP portability guide](https://vouchfx.io/kb/dcp-orchestrator-portability/) and
[provisioning/troubleshooting instructions](https://vouchfx.io/troubleshooting/#the-aspire-orchestration-component-is-not-installed-could-not-be-located).
An AppHost restore at Aspire 13.4.2 can provision the legacy tools; the retired
`dotnet workload install aspire` command cannot. An rc.6 run with this explicit override
is a provisioned legacy installation, not evidence that the package works standalone.

These rc.6 provisioning instructions apply only to the historical matching pin. They are not
prerequisites for the current bundled-DCP engine.

For full field metadata on catalogue tools, the installed CLI must implement Spec A
(`vouchfx list --json` with `requiredFields` / `optionalFields` / `captureSupported` /
`familyIntent`). `ENGINE_PIN` implements Spec A; a locally installed CLI that predates it still fails
fast with an upgrade message rather than returning incomplete type keys.

For the Planner path (`plan_coverage`), the installed CLI must also implement the M3 Planner
(`vouchfx plan --json`). `ENGINE_PIN` implements it; a locally installed CLI that predates it fails
closed with an explicit error rather than inventing a coverage-and-gap report in-process.

For the Generator path (`scaffold_suite`), the installed CLI must also implement Spec B
(`vouchfx scaffold --intent`). `ENGINE_PIN` implements it; a locally installed CLI that predates it
fails closed with an explicit error rather than inventing YAML in-process.

## Register with an MCP client

Add an entry to your client's `.mcp.json` (Claude Code, Claude Desktop, and most other MCP-aware agents
use this shape):

```json
{
  "mcpServers": {
    "vouchfx": {
      "command": "vouchfx-mcp"
    }
  }
}
```

The server speaks MCP over stdio by default (see the optional HTTP transport below) and locates its own `ENGINE_PIN` file and vendored documentation
relative to the installed tool's own location — wherever `dotnet tool install` placed it. No arguments
or environment variables are required for basic operation.

Closing the server's stdin disconnects the stdio session and starts host shutdown, including when
`run_suite` is active. The runner requests CLI shutdown and waits for teardown before the server
records the aborted suite as `Inconclusive`; any earlier `Fail` or `EnvironmentError` verdict remains
in the aggregate outcome. Keep draining stdout and stderr while waiting for exit; a tool
response is not guaranteed after disconnection. The host's shutdown budget is 50 seconds, allowing
the runner's 35-second graceful period, force-kill confirmation, output draining and persistence.

### Optional: workspace containment

Optionally pass the `--workspace <path>` flag to configure path containment:

```json
{
  "mcpServers": {
    "vouchfx": {
      "command": "vouchfx-mcp",
      "args": ["--workspace", "/path/to/workspace"]
    }
  }
}
```

When this flag is supplied, the server resolves a workspace with the following directories:

- **Root** (canonicalised and absolute from `<path>`)
- **Specs directory** — `<root>/e2e`, where suites are expected to live
- **Output directory** — `<root>/.vouchfx/runs`, where the run registry and events files are persisted:
  one JSON document per run stores metadata, and one JSON Lines stream stores the events.
  A lock file (`<root>/.vouchfx/runs/.lock`) is held for the duration of each run, enforcing
  single-flight concurrency across server processes. It is created once and then persists on every
  platform — the claim is the operating-system handle, never the file's existence, so the file is
  inert between runs: it is never read and never blocks a future run. Do not delete it by hand. On
  Windows the operating system denies the delete while a run holds it anyway; on Linux and macOS the
  claim is an advisory lock on the file's inode, so deleting it mid-run breaks mutual exclusion
  rather than tidying up.
- **Config file** — `<root>/vouchfx.config.json`, if present

The root must already exist as a local directory. A missing path, regular file, unresolved link,
link cycle or exhausted path-resolution budget refuses startup with one bounded JSON error on
stderr and exit code `1`; the server creates no directory to repair a mistyped root. A direct
network/UNC root (`--workspace \\host\share`) is rejected before filesystem inspection. Local links
are inspected one hop at a time, and network-shaped targets are rejected before they are followed.
An optional config file linked outside the workspace is not treated as present.

**Startup banner.** When the server starts, it prints a message to stderr stating whether a workspace
is configured and, if so, the root path. This helps confirm the server is operating in the mode you
intended. A typo in the flag name (e.g. `--workspce` or any flag starting `--worksp` but not
`--workspace`) is a startup-fatal error with a "did-you-mean" suggestion; misspelled flags are
caught immediately rather than silently ignored.

**Behaviour change with `--workspace`:** every path parameter passed to `validate_suite`,
`normalize_suite`, `run_suite`, `plan_coverage`, `explain_run`, and `diagnose_run` is canonicalised
(symlinks resolved segment by segment, iterated until nothing more resolves) and must resolve inside
the workspace root. Paths that try to escape the root — via `../` traversal or symlink target
resolution — are rejected with error `VFX-E-1001 PathOutsideWorkspace`.

**Relative paths resolve against the workspace root** when one is configured, which is what makes
`nested/suite.e2e.yaml` mean what a caller expects rather than depending on whichever directory your
MCP client happened to launch the server from. Without `--workspace`, a relative path still resolves
against the server process's current directory, exactly as it always has.

**Nothing is exempt from containment.** Every events path is checked the same way, whether a caller
named it or the server chose it — including `explain_run`/`diagnose_run`'s default (the events file
of the most recent finished run in the run registry). The documented `run_suite` → `explain_run`
round trip works because run artefacts live *inside* the workspace: with `--workspace`, `run_suite`
writes its events file under `<root>/.vouchfx/runs/`, so handing the returned `eventsFilePath`
straight back to `explain_run` passes containment on its merits. Without `--workspace`, artefacts
live in the OS temp directory, the registry is session-scoped (in-memory only), and containment is
off entirely; network-target checks still apply.

**`plan_coverage` is guarded like everything else, and its UNC rejection changed behaviour for
*every* host.** Its `path` and `eventsPath` used to bypass the guard entirely; both now go through
it — including the relative-path rebase onto the workspace root when one is configured, the same
second behaviour change every other guarded tool carries. The containment half is workspace-gated
like the rest, but the network/UNC half is not — so a UNC path that this one tool used to hand
straight to `vouchfx plan` is now refused **even with no `--workspace` flag**, where nothing
refused it before. That is the intended fix: the engine subprocess was performing the outbound
SMB/NTLM handshake the guard exists to prevent, one process removed from the server. One limit,
stated plainly: `plan_coverage`'s `path` names the root of a directory walk the *engine* performs —
containment binds that analysed root, and the engine's own discovery beneath it is not re-checked,
so a link inside a contained root can still lead the walk outside it. Copy suites locally when you
need local-only storage.

**Limits of path checks:** each path check shares one 256-step budget across link resolution and
unusable-name ancestor recovery, including suffix trimming. Exhaustion, cycles and paths it cannot
verify are refused. It rejects network-shaped path strings and stored symbolic-link/junction
targets in both workspace modes. It does **not** detect every network-mounted filesystem or mapped
drive. Checks describe the filesystem at check time: a writer can replace a path before the later
read (TOCTOU), and hard links inside the root are accepted. Treat a workspace as a local developer
boundary, not a sandbox for files writable by an adversary; restart the server after moving its root.

### Run history retention and recovery

Workspace runs store server metadata in `<root>/.vouchfx/runs/<runId>/run.json` and engine evidence
in `events.jsonl`. **The server now deletes eligible terminal evidence automatically**: the defaults
retain the newest **1,000 eligible terminal runs** and those finished within **30 days**. A run
must satisfy both limits. This replaces the previous promise that workspace evidence was never deleted.
Copy evidence you need to retain elsewhere; add `.vouchfx/` to your repository's `.gitignore`.

Set positive startup limits, on either transport:

```bash
vouchfx-mcp --workspace /path/to/repo --run-history-max-terminal-runs 2000 --run-history-max-age-days 60
vouchfx-mcp --workspace /path/to/repo --keep-run-history
```

The maximum accepted values are 100,000 runs and 36,500 days. Repeated, invalid or missing values
refuse startup. `--keep-run-history` disables pruning, including evidence deletion, but retains orphan
reconciliation; it cannot be combined with pruning limits. These flags do not read a configuration file.
Without a workspace the registry remains in memory and no history-maintenance directory is created.
The existing best-effort 24-hour OS-temp-file sweep is unchanged.

Maintenance runs at startup when the output directory already exists, and before each run. It must
acquire or reuse the same exclusive workspace claim; a held or unavailable claim prevents maintenance.
Validated abandoned nonterminal records become `completed` / `Inconclusive`, with a fixed
`remediationHint`. **Their `finishedAt` is reconciliation time, not measured engine completion. Engine
cleanup remains unconfirmed**: this pass never stops an engine child or touches containers.

Each pass examines at most 10,000 run directories and 100,000 directories in total, and performs at
most 100 filesystem mutations. Stderr reports bounded examined/reconciled/pruned/skipped/mutation
counts and `incomplete`. Corrupt or unfamiliar documents, untrustworthy finish timestamps, links and
foreign contents are preserved. Only fixed owned `run.json`/`events.jsonl` files and an empty run
directory are deleted; stored paths are never deletion targets. Partial scans prune by count only
when examined records prove the required number of newer eligible runs. Limits and preserved entries
can prevent convergence; these settings do not guarantee a global disk quota or a complete sweep.
Path checks run immediately before mutations, but retain the documented filesystem TOCTOU limit.
Mutation counts include attempts; reconciled/pruned counts cover completed record operations.
Cancellation or a storage failure during pruning can leave metadata without its events file, or an
empty directory. Readers then report their existing missing-event or not-found states.

Readers stay lock-free and observe current files. A pruned run ID returns `VFX-E-1505`; copy required
evidence before retention removes it. Paging still reparses examined metadata on every page; no index
or cross-page snapshot cache is introduced.




**Without `--workspace`:** arbitrary local paths, including relative `../` traversal, remain
allowed. Network paths and local links with network-shaped targets are still refused, as are paths
whose local nature cannot be verified within the bounded walk.

For details on both UNC-path rejection (which applies in both modes) and containment behaviour, see
the [VFX-E-1001 error documentation](https://vouchfx-mcp.vouchfx.io/docs/errors/VFX-E-1001.html).

## What each tool needs at runtime

| Requirement | `validate_suite`, `normalize_suite`, `search_docs`, `explain_diagnostic` | `get_schema` | `list_step_types`, `describe_step_type` | `plan_coverage` | `scaffold_suite` | `run_suite` | `explain_run`, `diagnose_run` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `vouchfx` CLI on PATH | Not needed | Optional — cross-verifies the embedded schema against `vouchfx schema` when present | **Required**, version-checked, Spec A rich `list --json` | **Required**, version-checked, M3 Planner `plan --json` | **Required**, version-checked, Spec B `scaffold` | **Required**, version-checked | Not needed |
| Docker engine running | Not needed | Not needed | Not needed | Not needed | Not needed | Required for container topologies | Not needed |
| Matching Aspire DCP tools | Not needed | Not needed | Not needed | Not needed | Not needed | Required for container topologies; published rc.6 needs explicit provisioning as above | Not needed |
| Reads a local events file | No | No | No | Optional (`eventsPath`) | No | Writes one, then reads it back | **Required** — its whole job |

`validate_suite`, `normalize_suite`, `search_docs`, and `explain_diagnostic` work entirely from this
server's embedded vendored schema/docs/catalogue even without a CLI. `get_schema` is **CLI-optional**:
it serves the embedded schema offline and, when the pinned CLI is present, cross-verifies it against
`vouchfx schema` and reports any divergence as diagnostic `VFX-D-1106`. The matching pinned engine writes redirected text as UTF-8 without a BOM; the server decodes
schema, scaffold and live run output with that contract. No console-code-page workaround is needed.
A mismatch reports schema divergence or an unparseable export. Catalogue tools
always prefer the live engine export and fail closed when it is unavailable or too thin.

## Optional: serving over HTTP instead of stdio

**You almost certainly do not need this.** Every MCP client listed above launches this server as a
local subprocess and speaks to it over **stdio**, which is the default, needs no configuration, and is
the shipped contract. The HTTP transport exists for the cases stdio cannot serve — a CI runner or a
shared instance reaching the server across a process or machine boundary.

### Enabling it

Two things are required, and the server refuses to start without both:

```bash
export VOUCHFX_MCP_HTTP_TOKEN='<a long, random, high-entropy secret>'
vouchfx-mcp --transport http --urls http://127.0.0.1:5090
```

MCP is then served at `/mcp` on that address, and **every** request must carry
`Authorization: Bearer <token>`.

> **A blocking `run_suite` can outlive an idle timeout.** `run_suite` holds its HTTP request open for
> the whole run, which for a real suite is minutes. Default idle timeouts in reverse proxies and HTTP
> clients are often shorter, and the connection dropping does not stop the run — it just loses you the
> result. Either raise the timeout on both, or start the run and follow it with the polling approach in
> [long-running runs](troubleshooting.md#long-running-runs-poll-because-mcp-tasks-is-not-available).

### The bearer token

- The token is read **only** from the `VOUCHFX_MCP_HTTP_TOKEN` environment variable. There is no
  command-line option for it, and passing one is refused rather than honoured: a process's arguments
  are readable by every user on the host (`/proc/<pid>/cmdline`, `ps`, Task Manager) and leak into
  shell history and container manifests.
- **Starting without a token is a startup failure**, not a warning — the server reports
  [`VFX-E-1007`](errors/VFX-E-1007.md) on stderr, exits non-zero, and opens no listener at all. It
  never falls back to serving unauthenticated.
- Every tool is behind the token. The gate runs before MCP request dispatch, so an unauthenticated
  caller gets a bare `401` with `WWW-Authenticate: Bearer` and no body — no tool runs, and nothing is
  revealed about how the server is configured.
- The token is never written to a log record, and it is stripped from the environment of every child
  process this server spawns, so the `vouchfx` engine never sees it.
- The token must be at least 16 characters of printable ASCII. Both are enforced at startup: a
  shorter token, one with leading or trailing whitespace, or one carrying non-ASCII characters is
  refused with `VFX-E-1007` rather than accepted. Padding is refused rather than trimmed so the
  credential this server accepts is exactly the one you configured, and non-ASCII is refused because
  how a client encodes it in an HTTP header is not well defined — it would authenticate from some
  clients and not others.
- Generate one with real entropy, e.g. `openssl rand -base64 48`, and treat it as a credential:
  anyone holding it can run suites on this machine. The 16-character floor is a guard against the
  worst configurations, not a substitute for entropy.

### Bind address, and the cleartext warning

**With no `--urls` flag the server binds `http://127.0.0.1:5090` — loopback only.** That default is
enforced in code, and the address you pass is authoritative: it is parsed at startup and configured
on the listener explicitly, so no `appsettings.json` in the working directory and no ambient
`ASPNETCORE_URLS` can move it. A repeated `--urls` flag is refused rather than silently resolved.

Three `--urls` shapes are refused at startup with `VFX-E-1007`, all deliberately:

- **Anything other than `http://`.** `https://127.0.0.1:5090` is refused rather than silently served
  as cleartext — this server terminates no TLS, and quietly downgrading a scheme an operator asked
  for is exactly the kind of assumption that gets a credential onto a network. Put TLS in a reverse
  proxy in front instead.
- **A host NAME rather than a literal IP.** `http://localhost:5090` is refused; write
  `http://127.0.0.1:5090`. Resolving a name at start time would make the interface the server binds
  depend on DNS and `hosts`-file state at that moment — precisely the late-binding this server
  removes everywhere else in its bind path.
- **Anything beyond the address and the port** — a path, a query string, a fragment, or user
  information. `http://127.0.0.1:5090/other` is refused, and so is `http://127.0.0.1:5090/mcp`: a
  bind endpoint is an address and a port, so the extra component would simply be discarded and the
  server would serve at `/mcp` on that port anyway, having reported success for a configuration you
  did not write. The MCP path is fixed and not configurable — append it to the URL your *client*
  calls, never to the one the server binds. Credentials belong in the `Authorization` header, never
  in the URL. A bare trailing slash (`http://127.0.0.1:5090/`) is the same value as no path and is
  accepted.

**This server speaks cleartext HTTP. It has no TLS support at all.** The consequence is direct: the
bearer token travels in an `Authorization` header on *every request*, in the clear. On loopback that
is acceptable — the traffic never leaves the machine, and anyone able to read loopback traffic can
already read the process's memory. **Off loopback it is not acceptable on its own.** Binding
`0.0.0.0` puts the token on the wire across your network and exposes every tool that spawns the
engine CLI to anyone who can reach the port.

If you need non-loopback access, **terminate TLS in front of this server** — a reverse proxy on the
same host, forwarding to `127.0.0.1:5090` — rather than binding the server to a public interface.

### DNS rebinding, and why the token mostly covers it

A browser-based attacker can make a victim's browser resolve a hostname to `127.0.0.1` and then issue
requests to a loopback service. This server does not implement an `Origin` allowlist, and the reason
is that the bearer token already blocks the practical attack: a cross-origin request cannot set an
`Authorization` header without a CORS preflight, and this server grants no CORS permissions, so the
rebound request arrives unauthenticated and gets a `401`. Script in a page therefore cannot reach the
tools.

What that does *not* cover is a non-browser attacker who already has the token, which is a different
problem solved by not leaking the token. If you are running this somewhere a browser-origin check
would add real value, put it in the reverse proxy along with TLS.

### Runtime requirement (measured)

The HTTP transport is built on the MCP SDK's ASP.NET Core integration, which means **the built
application declares a framework reference to `Microsoft.AspNetCore.App`** — verified in the packed
tool's `runtimeconfig.json`, which lists both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`.

What that means in practice:

- **The ASP.NET Core shared runtime must be installed to start the process at all** — the framework
  reference is a property of the application, not of the transport you chose, so it applies to stdio
  users too.
- **In practice this is already satisfied wherever this tool is installed.** `dotnet tool install`
  requires the .NET SDK, and the SDK always carries the ASP.NET Core shared runtime. If you install
  the tool the documented way, there is nothing extra to do.
- **Nothing else changes for stdio users.** No listener is created, no port is bound, no token is
  required, and no HTTP code runs. The tool package structure is unchanged — same tool manifest, same
  entry assembly, same `ENGINE_PIN` beside it.
- If you hit a startup error naming `Microsoft.AspNetCore.App`, install the ASP.NET Core runtime (or
  the .NET SDK) for your platform and retry.

## Verifying the install

Once registered, ask your agent to call `list_step_types` (no arguments) — with the pinned `vouchfx`
CLI on `PATH` (Spec A rich catalogue), a working install returns Core provider step types grouped by
family with `familyIntent` and `captureSupported`. Without that CLI, catalogue tools return a clear
tool error rather than inventing field metadata. If instead your MCP client reports it could not
start the `vouchfx-mcp` process, confirm `vouchfx-mcp` resolves on `PATH`
(`dotnet tool list --global` should list `Vouchfx.Mcp`) and that the `.NET 8` runtime is installed.
If the process starts but exits immediately, see
[Server exits at startup](troubleshooting.md#server-exits-at-startup) for the three fatal-at-startup
conditions and their exact stderr prefixes — these indicate a broken install, not a `PATH` problem.
