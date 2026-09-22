#!/usr/bin/env bash
# .claude/hooks/session-start.sh: SessionStart bootstrap for Claude Code on the web.
#
# Registered in .claude/settings.json. It runs only in a Claude Code on the web
# container ($CLAUDE_CODE_REMOTE=true); a local machine keeps whatever SDK its owner
# installed and is never touched.
#
# What it guarantees, idempotently:
#   1. A .NET 8 SDK that satisfies global.json (8.0.400, rollForward latestFeature).
#      The web sandbox's egress proxy blocks builds.dotnet.microsoft.com (where
#      dotnet-install.sh downloads from), and Ubuntu 24.04's own dotnet-sdk-8.0 is an
#      8.0.1xx build, below global.json's floor. Microsoft's Ubuntu 22.04 (jammy) apt
#      feed carries the 8.0.4xx band and installs cleanly on noble, so that is the
#      source used. An apt preference pins every dotnet package to that feed, so apt
#      never mixes Ubuntu's own host/runtime packages into the Microsoft SDK.
#   2. /usr/bin/dotnet, and DOTNET_ROOT/PATH in /etc/profile.d/dotnet.sh and in
#      $CLAUDE_ENV_FILE, so login shells and the session's own tool calls agree.
#   3. The published `vouchfx` global tool at exactly the version ENGINE_PIN names, so
#      the *AgainstPinnedCliTests classes run for real instead of self-gating into their
#      quiet-pass branch (the same reason .github/workflows/build.yml installs it).
#      This hook is AUTHORITATIVE for the global tool: a different installed version is
#      replaced. When several fleet repos share one session, the vouchfx-samples and
#      vouchfx-providers hooks only install into an empty slot and never replace, so
#      this repo's pin wins however the hooks are ordered, and its parity tests cannot
#      silently skip against another repo's CLI.
#
# Synchronous by design: the session starts only once the SDK is present, so nothing
# races a half-installed toolchain. On a container that already has the SDK (the web
# environment caches container state after this hook completes) it finishes in well
# under a second. Progress goes to stderr; the single summary line on stdout is what
# the session sees.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR=/usr/share/dotnet
MS_KEYRING=/usr/share/keyrings/microsoft-prod.gpg
MS_LIST=/etc/apt/sources.list.d/microsoft-prod.list
MS_PREFS=/etc/apt/preferences.d/dotnet-microsoft

log() { printf '[session-start] %s\n' "$*" >&2; }
REPO_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

VOUCHFX_TOOL="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools/vouchfx"

# Installs the vouchfx global tool at exactly $1 (a NuGet version, no leading "v").
# nuget.org is ADDED as a source, never replacing configured ones, as the CI install
# step does. A different installed version is uninstalled first, because
# `dotnet tool update` refuses to move to a lower version.
install_cli() {
  local want="$1" have
  have="$(dotnet tool list -g 2>/dev/null | awk 'tolower($1)=="vouchfx" {print $2}')"
  [ "$have" = "$want" ] && return 0
  if [ -n "$have" ]; then
    log "Replacing vouchfx ${have} with ${want}."
    dotnet tool uninstall -g vouchfx >/dev/null
  else
    log "Installing vouchfx ${want}."
  fi
  dotnet tool install -g vouchfx --version "$want" --add-source https://api.nuget.org/v3/index.json >/dev/null
}

# The installed CLI's informational version ("<version>+<commit-sha>"), or empty.
cli_version() { "$VOUCHFX_TOOL" --version 2>/dev/null | head -n1 | tr -d '\r' || true; }

# The system-wide steps (apt, /usr/bin/dotnet, /etc/profile.d) run only as root, which
# is what a Claude Code on the web container is. This hook NEVER escalates: there is no
# sudo, because a checked-in hook that elevated itself would hand any branch a privileged
# path. Not root and no SDK is a loud refusal; not root with an SDK present skips only
# the system-wide writes.
is_root() { [ "$(id -u)" -eq 0 ]; }

# True when an SDK in the 8.0.4xx band or later is installed. Captured before grep so
# `grep -q` exiting early cannot SIGPIPE `dotnet` into a pipefail.
sdk_ok() {
  local sdks
  sdks="$("$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null || true)"
  grep -Eq '^8\.0\.[4-9][0-9]{2} ' <<<"$sdks"
}

install_sdk() {
  export DEBIAN_FRONTEND=noninteractive
  local arch line
  arch="$(dpkg --print-architecture)"
  line="deb [arch=${arch} signed-by=${MS_KEYRING}] https://packages.microsoft.com/ubuntu/22.04/prod jammy main"

  if [ ! -s "$MS_KEYRING" ]; then
    log "Adding Microsoft's package signing key."
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor --yes -o "$MS_KEYRING"
  fi
  if [ "$(cat "$MS_LIST" 2>/dev/null)" != "$line" ]; then
    printf '%s\n' "$line" >"$MS_LIST"
  fi
  printf 'Package: dotnet* aspnetcore* netstandard*\nPin: origin "packages.microsoft.com"\nPin-Priority: 1001\n' \
    >"$MS_PREFS"

  # Refresh only the Microsoft list (seconds), keeping the image's other lists as they
  # are; fall back to a full refresh if a dependency then cannot be resolved.
  log "Installing dotnet-sdk-8.0 from Microsoft's jammy feed."
  apt-get update -qq -o Dir::Etc::sourcelist=sources.list.d/microsoft-prod.list \
    -o Dir::Etc::sourceparts=- -o APT::Get::List-Cleanup=0
  if ! apt-get install -y -qq dotnet-sdk-8.0 >/dev/null; then
    log "Retrying after a full apt refresh."
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-8.0 >/dev/null
  fi
}

write_profile() {
  local profile=/etc/profile.d/dotnet.sh want
  want='# Written by .claude/hooks/session-start.sh (Claude Code on the web).
export DOTNET_ROOT=/usr/share/dotnet
case ":$PATH:" in *":/usr/share/dotnet:"*) ;; *) PATH="/usr/share/dotnet:$PATH" ;; esac
case ":$PATH:" in *":${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:"*) ;; *) PATH="$PATH:${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools" ;; esac
export PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1'
  if is_root && [ "$(cat "$profile" 2>/dev/null)" != "$want" ]; then
    printf '%s\n' "$want" >"$profile"
  fi
  # Once per file: a session can fire SessionStart more than once (resume, clear, compact),
  # and the block's first line is the marker that says it is already there.
  if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -qxF "# Written by .claude/hooks/session-start.sh (Claude Code on the web)." "$CLAUDE_ENV_FILE" 2>/dev/null; then
    printf '%s\n' "$want" >>"$CLAUDE_ENV_FILE"
  fi
}

if ! sdk_ok; then
  if ! is_root; then
    log "ERROR: no .NET 8.0.4xx SDK under ${DOTNET_DIR}, and installing one needs root. This hook never escalates; install the SDK yourself."
    exit 1
  fi
  install_sdk
  sdk_ok || { log "ERROR: dotnet-sdk-8.0 installed, but no 8.0.4xx SDK is visible under ${DOTNET_DIR}."; exit 1; }
fi
if is_root; then
  [ "$(readlink -f /usr/bin/dotnet 2>/dev/null)" = "$DOTNET_DIR/dotnet" ] || ln -sf "$DOTNET_DIR/dotnet" /usr/bin/dotnet
else
  log "Not root: /usr/bin/dotnet and /etc/profile.d/dotnet.sh are left as they are; the session environment is still written."
fi
write_profile

export DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# ---- the pinned engine CLI ----
summary_cli="no usable vouchfx (see stderr)"
read -r pin_ver pin_sha _ <"$REPO_DIR/ENGINE_PIN" || true
if [[ "${pin_ver:-}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ && "${pin_sha:-}" =~ ^[0-9a-f]{40}$ ]]; then
  cli_want="${pin_ver#v}"
  expected="${cli_want}+${pin_sha}"
  if [ "$(cli_version)" != "$expected" ]; then
    install_cli "$cli_want" || log "WARNING: could not install vouchfx ${cli_want}; the pinned-CLI parity tests will self-skip."
  fi
  actual="$(cli_version)"
  if [ "$actual" = "$expected" ]; then
    summary_cli="vouchfx ${actual} matches ENGINE_PIN"
  else
    log "WARNING: vouchfx --version reports '${actual:-<none>}'; ENGINE_PIN expects '${expected}'. The pinned-CLI parity tests will self-skip."
  fi
else
  log "WARNING: ENGINE_PIN's first line is not '<vX.Y.Z[-pre]> <40-char sha>'; skipping the CLI install."
fi
echo "session-start: .NET SDK $("$DOTNET_DIR/dotnet" --version) ready; ${summary_cli}."
