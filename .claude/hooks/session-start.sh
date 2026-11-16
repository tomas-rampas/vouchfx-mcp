#!/usr/bin/env bash
# .claude/hooks/session-start.sh: SessionStart bootstrap for Claude Code on the web.
#
# Registered in .claude/settings.json. It runs only in a Claude Code on the web
# container ($CLAUDE_CODE_REMOTE=true); a local machine keeps whatever SDK its owner
# installed and is never touched.
#
# Trust model. This file comes from the checked-out branch, and the SessionStart hook
# runs it at session start (as root in a web container) before anyone has read the
# diff. So .claude/ is treated like .github/workflows/: it is code-owned in
# .github/CODEOWNERS (as is global.json, which this hook feeds to the dotnet host), and
# web sessions should be opened only on branches you trust.
# The alternative is to move this bootstrap into the Claude Code environment's own
# setup script, which no branch controls, and delete this file and its registration
# in .claude/settings.json.
#
# What it guarantees, idempotently:
#   1. A .NET 10 SDK that satisfies global.json (10.0.100, rollForward latestFeature).
#      The web sandbox's egress proxy blocks builds.dotnet.microsoft.com (where
#      dotnet-install.sh downloads from), and Microsoft's Ubuntu 22.04 (jammy) apt feed,
#      which an earlier version of this hook used for .NET 8, publishes no
#      dotnet-sdk-10.0. Ubuntu 24.04's own archive (noble-updates/noble-security)
#      carries dotnet-sdk-10.0 in the 10.0.1xx band, which satisfies global.json, so
#      that is the source used. It installs under /usr/lib/dotnet.
#      A container in which that earlier version ran carries Microsoft-feed .NET
#      packages (SDK, runtimes, dotnet-host, hostfxr, targeting packs), the
#      microsoft-prod apt list and its keyring, and the apt preference
#      /etc/apt/preferences.d/dotnet-microsoft. Left in place they make a mixed-vendor
#      install: Ubuntu's dotnet-host-10.0 conflicts with Microsoft's dotnet-host, so apt
#      removes the muxer and cross-grades hostfxr, stranding the old SDK and runtimes
#      in /usr/share/dotnet with no host. So, whenever it runs as root, the hook acts on
#      that footprint, and only on it. Its marker is the preference file this hook family
#      wrote (/etc/apt/preferences.d/dotnet-microsoft), or a non-Ubuntu dotnet-host
#      package (the package that conflicts). The Microsoft list and keyring are deleted
#      only when the preference file was present: a container that merely has Microsoft's
#      feed for another purpose keeps it. Installed dotnet*/aspnetcore*/netstandard*
#      packages whose version is not an Ubuntu build are purged only with that marker,
#      and the purge is simulated first: if the simulation fails, or prints any action
#      other than a purge of exactly those packages, the hook stops and purges nothing.
#      A fresh container skips all of that. Also whenever it runs as root, it (re)writes
#      an apt preference that pins those packages to Ubuntu's <codename>-updates and
#      <codename>-security suites only; a missing or malformed VERSION_CODENAME stops
#      the hook before the pin is written. The install runs with --no-remove, so it
#      fails rather than removing any package.
#   2. /usr/bin/dotnet, and DOTNET_ROOT/PATH in /etc/profile.d/dotnet.sh and in
#      $CLAUDE_ENV_FILE, so login shells and the session's own tool calls agree.
#   3. The published `vouchfx` global tool at exactly the version ENGINE_PIN names, so
#      the *AgainstPinnedCliTests classes run for real instead of self-gating into their
#      quiet-pass branch (the same reason .github/workflows/build.yml installs it).
#      That CLI is a net8.0 framework-dependent tool with no roll-forward in its own
#      runtimeconfig, and this container carries only .NET 10. So it is installed with
#      --allow-roll-forward, which writes rollForward=Major into the tool's own
#      runtimeconfig. Its version is probed first WITHOUT DOTNET_ROLL_FORWARD; only when
#      that fails is it probed once with DOTNET_ROLL_FORWARD=Major, and then the summary
#      says "runs only with DOTNET_ROLL_FORWARD=Major". Roll-forward is never exported
#      for the session: the server this repo ships never sets it for the CLI either.
#      A copy that starts only when forced is NOT accepted, whatever version it reports:
#      the decision to keep it rests on the plain probe alone. A pinned-version copy that
#      cannot start without the variable (what an earlier version of this hook installed,
#      without roll-forward) is replaced by the same version installed with
#      --allow-roll-forward. A copy of a different version was already replaced by the
#      pinned one, and still is. Without this, the *AgainstPinnedCliTests would skip in
#      that container unless someone exported the variable.
#      This hook is AUTHORITATIVE for the global tool: a different installed version is
#      replaced, and a copy of the pinned version that still names another commit
#      afterwards is removed and the hook exits non-zero (see the end of this file).
#      When several fleet repos share one session, the vouchfx-samples and
#      vouchfx-providers hooks only install into an empty slot and never replace, so
#      this repo's pin wins however the hooks are ordered, and its parity tests cannot
#      silently skip against another repo's CLI.
#
# Two guards on the dotnet host itself. The .NET 10 host honours `sdk.paths` in
# global.json, which would load an SDK from a repository-relative path, as root. So
# before ANY dotnet call that has a repository directory as its working directory, the
# hook refuses (exit 1) a global.json whose `sdk` object holds any key other than
# version, rollForward and allowPrerelease, and anything it cannot classify (it also
# refuses anything that is not a regular file, and one over 64 KiB). The search covers
# the physical path (REPO_DIR is resolved with cd -P) and every parent directory. Every
# other dotnet call (tool list/install/uninstall, the summary --version) runs from a
# scratch directory that carries its own `{}` global.json: the host stops its upward
# search at the nearest global.json (measured on SDK 10.0.115), so a global.json in
# /tmp or any other ancestor of the scratch directory is never reached. The scratch
# directory is created under /run or ~/.cache when that is a real directory only its
# owner can write to, else under the default temporary directory; the `{}` is the
# guarantee and the parent is defence in depth. /tmp ancestors are deliberately NOT
# added to global_json_safe: refusing a session over an unrelated /tmp/global.json
# would be an availability problem, not a security gain.
#
# Fleet note. The "own footprint" marker /etc/apt/preferences.d/dotnet-microsoft is also
# written by any checkout whose own session hook still installs the .NET 8 SDK from
# Microsoft's feed, so a web session must not combine this repository with such a
# checkout until that checkout's hook has moved to .NET 10: the two hooks would undo each
# other's apt state on every start.
#
# Synchronous by design: the session starts only once the SDK is present, so nothing
# races a half-installed toolchain. On a container that already has the SDK (the web
# environment caches container state after this hook completes) it finishes in well
# under a second. Progress goes to stderr; the single summary line on stdout is what
# the session sees, so it also reports how many packages a purge removed.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR=/usr/lib/dotnet
DOTNET_PREFS=/etc/apt/preferences.d/dotnet-ubuntu
# Written by the .NET 8 version of this hook: Microsoft's jammy feed, its signing key, and
# the preference that pinned dotnet packages to it.
LEGACY_MS_LIST=/etc/apt/sources.list.d/microsoft-prod.list
LEGACY_MS_KEYRING=/usr/share/keyrings/microsoft-prod.gpg
LEGACY_MS_PREFS=/etc/apt/preferences.d/dotnet-microsoft
# The first line of the environment block, which doubles as its once-per-file marker. It
# names the dotnet root, so a block an earlier version of this hook wrote (another root)
# does not count as already present and the corrected one is appended after it.
ENV_MARKER="# Written by .claude/hooks/session-start.sh (Claude Code on the web; dotnet root ${DOTNET_DIR})."
# Set by remove_microsoft_feed to the number of packages it purged, for the summary line.
PURGED_COUNT=0

log() { printf '[session-start] %s\n' "$*" >&2; }
# Physical path (cd -P / pwd -P): the dotnet host searches the physical directory chain
# for global.json, so that is the chain global_json_safe must walk.
REPO_DIR="$(cd -P -- "${CLAUDE_PROJECT_DIR:-$(dirname "${BASH_SOURCE[0]}")/../..}" 2>/dev/null && pwd -P)" || {
  log "ERROR: cannot resolve the repository directory."
  exit 1
}

VOUCHFX_TOOL="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools/vouchfx"

# True when every global.json the dotnet host could pick up for a working directory of
# $1 (the nearest one, searching upward, is the one it uses; all of them are checked,
# so none can be swapped in by a missing nearer file) is a plain version pin. The file
# is flattened (whitespace removed) and must match the whole of
#   {"sdk":{<members>}}
# where each member is exactly one of "version":"<chars>", "rollForward":"<letters>"
# or "allowPrerelease":true|false, each at most once. Anything else is refused:
# "paths", "errorMessage", any other key, comments, a byte-order mark, malformed JSON,
# a symlink, a FIFO, a directory or any other non-regular file, a file over 64 KiB
# (measured in bytes), an unreadable file. A missing global.json is fine: there is
# nothing for the host to honour. Runs under LC_ALL=C, so the size is bytes and the match
# is bytewise whatever the session's locale. Pure bash and coreutils, so it needs
# nothing beyond the container. It prints nothing to stdout: SessionStart stdout reaches
# the session only on exit 0, and a refusal exits 1, so the reason goes to stderr.
global_json_safe() {
  local -x LC_ALL=C
  local dir="$1" file raw count key size
  while :; do
    file="$dir/global.json"
    if [ -L "$file" ]; then
      log "ERROR: ${file} is a symlink; refusing it."
      return 1
    fi
    if [ -e "$file" ]; then
      if [ ! -f "$file" ]; then
        log "ERROR: ${file} is not a regular file; refusing it."
        return 1
      fi
      size="$(wc -c <"$file")" || { log "ERROR: cannot read ${file}."; return 1; }
      size="${size//[[:space:]]/}"
      if ! [[ "$size" =~ ^[0-9]+$ ]] || [ "$size" -gt 65536 ]; then
        log "ERROR: ${file} is larger than 64 KiB; refusing it."
        return 1
      fi
      raw="$(head -c 65537 "$file" | tr -d ' \t\r\n')" || { log "ERROR: cannot read ${file}."; return 1; }
      # One regex over the flattened text.
      if ! [[ "$raw" =~ ^\{\"sdk\":\{\"(version\":\"[0-9A-Za-z.+-]{1,64}\"|rollForward\":\"[A-Za-z]{1,32}\"|allowPrerelease\":(true|false))(,\"(version\":\"[0-9A-Za-z.+-]{1,64}\"|rollForward\":\"[A-Za-z]{1,32}\"|allowPrerelease\":(true|false)))*\}\}$ ]]; then
        log "ERROR: ${file} is not a plain {\"sdk\":{version, rollForward, allowPrerelease}} pin (the .NET 10 host honours sdk.paths, which would load an SDK from the repository); refusing it."
        return 1
      fi
      for key in version rollForward allowPrerelease; do
        # grep exits 1 on no match, which pipefail would turn into a failure. The caller's
        # `|| exit 1` switches errexit off in here; the || true keeps the function safe if
        # it is ever called with errexit active.
        count="$(grep -o "\"${key}\":" <<<"$raw" | wc -l || true)"
        if [ "$count" -gt 1 ]; then
          log "ERROR: ${file} repeats the key ${key}; refusing it."
          return 1
        fi
      done
    fi
    [ "$dir" = "/" ] && break
    dir="$(dirname "$dir")"
  done
  return 0
}

# A directory no repository file can reach, for every dotnet call that does not need the
# repository as its working directory. Removed on exit. Two layers: it carries its own
# `{}` global.json (the dotnet host stops its upward search at the nearest one, so no
# global.json in an ancestor such as /tmp can steer a call run from here), and it is
# created under a parent only its owner can write to when one exists (/run, then
# ~/.cache: an existing real directory, owned by the caller, with no group or other write
# bit), falling back to the default temporary directory. A missing preferred parent is
# never an error.
make_scratch() {
  local parent mode d
  for parent in /run "${HOME:-/nonexistent}/.cache"; do
    [ -d "$parent" ] && [ ! -L "$parent" ] || continue
    [ "$(stat -c %u "$parent" 2>/dev/null)" = "$(id -u)" ] || continue
    mode="$(stat -c %a "$parent" 2>/dev/null)" || continue
    [[ "$mode" =~ ^[0-7]{3,4}$ ]] || continue
    (( (8#$mode & 8#022) == 0 )) || continue
    if d="$(mktemp -d "$parent/vouchfx-hook.XXXXXX" 2>/dev/null)"; then
      printf '%s' "$d"
      return 0
    fi
  done
  mktemp -d
}
SCRATCH="$(make_scratch)" || exit 1
trap 'rm -rf "$SCRATCH"' EXIT
printf '{}' >"$SCRATCH/global.json" || exit 1
scratch_dotnet() { (cd "$SCRATCH" && "$DOTNET_DIR/dotnet" "$@"); }

# A NuGet config that holds nuget.org and nothing else, for the one install below.
# --add-source only ADDS nuget.org beside every feed already configured (machine, user,
# and any nuget.config above the working directory), any of which could serve a
# same-version `vouchfx`. (SDK 10 also has --source, which replaces the sources; a config
# file is used for parity with the CI lanes.) Measured on SDK 8.0.425, where the install
# command had no --source: a fake 1.0.0-rc.5 packed into a folder feed that a local
# nuget.config named was installed instead of nuget.org's under --add-source, and
# nuget.org's was installed under --configfile with this file. The version check after
# the install proves only the commit a package claims, so it cannot stand in for choosing
# the source. The directory is made inside $SCRATCH, so it sits under that directory's `{}`.
nuget_org_only_config() {
  local dir
  dir="$(mktemp -d "$SCRATCH/nuget.XXXXXX")" || return 1
  printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' \
    '    <clear />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' \
    '  </packageSources>' '</configuration>' >"$dir/nuget.config"
  printf '%s' "$dir"
}

# Installs the vouchfx global tool at exactly $1 (a NuGet version, no leading "v").
# The package comes from nuget.org alone (nuget_org_only_config, above).
# Called only when the installed CLI's informational version differs from
# the pin's, or when the pinned build does not start without DOTNET_ROLL_FORWARD,
# so an installed copy is always replaced: a different version, and also the
# right version from another build (a locally packed copy, say), since nuget.org never
# republishes a version and its package is the pinned build. It is uninstalled first,
# because `dotnet tool update` refuses to move to a lower version, and --no-cache stops
# the reinstall reusing a cached copy of that other build. The caller then checks what
# is installed, and removes a copy that still is not the pinned build.
# --allow-roll-forward: the published CLI is a net8.0 build with no roll-forward policy
# of its own, and this host carries only .NET 10 (see the header).
# dotnet is called by absolute path: a non-root run never links /usr/bin/dotnet, and
# the PATH this hook writes only reaches later processes, not this one.
install_cli() {
  local want="$1" have
  have="$(scratch_dotnet tool list -g 2>/dev/null | awk 'tolower($1)=="vouchfx" {print $2}')"
  if [ -n "$have" ]; then
    log "Replacing vouchfx ${have} with ${want} from nuget.org."
    scratch_dotnet tool uninstall -g vouchfx >/dev/null
  else
    log "Installing vouchfx ${want}."
  fi
  local cfg status=0
  cfg="$(nuget_org_only_config)" || return 1
  scratch_dotnet tool install -g vouchfx --version "$want" --configfile "$cfg/nuget.config" --no-cache --allow-roll-forward >/dev/null || status=$?
  rm -rf "$cfg"
  return "$status"
}

# The installed CLI's informational version ("<version>+<commit-sha>") when it starts in
# mode $1, or empty. "plain" is the environment as it is, with any DOTNET_ROLL_FORWARD
# removed so an inherited value cannot make the probe pass; "forced" adds
# DOTNET_ROLL_FORWARD=Major for this one process only. Run from $SCRATCH, and an exit
# status other than 0 or an output that is not a version counts as "does not start".
probe_cli() {
  local mode="$1" out
  if [ "$mode" = forced ]; then
    out="$(cd "$SCRATCH" && env DOTNET_ROLL_FORWARD=Major "$VOUCHFX_TOOL" --version 2>/dev/null | head -n1 | tr -d '\r')" || out=""
  else
    out="$(cd "$SCRATCH" && env -u DOTNET_ROLL_FORWARD "$VOUCHFX_TOOL" --version 2>/dev/null | head -n1 | tr -d '\r')" || out=""
  fi
  [[ "$out" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] || out=""
  printf '%s' "$out"
}

# Plain probe first; the forced probe only when that fails and a tool file is there.
cli_version() {
  local v
  v="$(probe_cli plain)"
  if [ -z "$v" ] && [ -x "$VOUCHFX_TOOL" ]; then
    v="$(probe_cli forced)"
  fi
  printf '%s' "$v"
}

# The system-wide steps (apt, /usr/bin/dotnet, /etc/profile.d) run only as root, which
# is what a Claude Code on the web container is. This hook NEVER escalates: there is no
# sudo, because a checked-in hook that elevated itself would hand any branch a privileged
# path. Not root and no SDK is a loud refusal; not root with an SDK present skips only
# the system-wide writes.
is_root() { [ "$(id -u)" -eq 0 ]; }

# True when an SDK under $DOTNET_DIR resolves this repository's global.json. The dotnet
# host applies the pinned version and its rollForward policy itself, and exits non-zero
# (145) when no installed SDK satisfies them, so this check cannot drift from global.json
# the way a hard-coded version band could. This is the one dotnet call with the
# repository as its working directory, so global_json_safe has already approved the file
# (see the main block below).
sdk_ok() { (cd "$REPO_DIR" && "$DOTNET_DIR/dotnet" --version) >/dev/null 2>&1; }

# Installed .NET packages that are not Ubuntu archive builds, one name per line. Every
# Ubuntu build of these source packages carries "ubuntu" in its version
# (10.0.115-0ubuntu1~24.04.1); Microsoft's feed versions do not (8.0.427-1, 9.0.4-1).
foreign_dotnet_packages() {
  dpkg-query -W -f='${Status}\t${Package}\t${Version}\n' 'dotnet*' 'aspnetcore*' 'netstandard*' 2>/dev/null |
    awk -F'\t' '$1 ~ / installed$/ && $3 !~ /ubuntu/ { print $2 }' || true
}

# True when the first argument, without any :arch suffix, names one of the remaining
# arguments, also compared without their :arch suffix. Exact string comparison only.
in_package_set() {
  local want="${1%%:*}" pkg
  shift
  for pkg in "$@"; do
    [ "${pkg%%:*}" = "$want" ] && return 0
  done
  return 1
}

remove_microsoft_feed() {
  local -a foreign refused
  local line verb name prefs_present=false
  # The marker of the earlier hook's footprint is its apt preference file. Whether it is
  # present is recorded before anything is deleted. When it is, the preference, the
  # Microsoft list and its keyring all go first, so no apt operation below can see the
  # Microsoft feed or its priority-1001 pin. Without it, the list and keyring are left
  # alone: they were not written by this hook family.
  if [ -e "$LEGACY_MS_PREFS" ]; then
    prefs_present=true
    local file
    for file in "$LEGACY_MS_LIST" "$LEGACY_MS_KEYRING" "$LEGACY_MS_PREFS"; do
      if [ -e "$file" ]; then
        log "Removing ${file}."
        rm -f "$file"
      fi
    done
  fi

  mapfile -t foreign < <(foreign_dotnet_packages)
  if [ "${#foreign[@]}" -eq 0 ]; then
    return 0
  fi
  # Purge only when the earlier hook's footprint is here: its preference file was present
  # at the start of this run, or a non-Ubuntu dotnet-host is installed (the package
  # that conflicts with Ubuntu's dotnet-host-10.0). Without either, these packages came
  # from somewhere this hook did not touch, and they are left in place.
  if [ "$prefs_present" != true ] && ! in_package_set dotnet-host "${foreign[@]}"; then
    log "Leaving ${#foreign[@]} non-Ubuntu .NET package(s) in place: ${LEGACY_MS_PREFS} was not present and no non-Ubuntu dotnet-host is installed."
    return 0
  fi
  # Purge exactly these, and nothing else. The simulation runs first; if it fails the
  # hook stops and purges nothing. An action line is one that starts with Inst, Remv,
  # Purg or Conf followed by a space or the end of the line. Every action line must be
  # a Purg of a package in the set; any other action, or a Purg of any other package,
  # refuses the purge rather than carrying it out. Lines that do not start with one of
  # those four verbs (headings, "Need to get ...") are not actions.
  sim="$(apt-get purge -s "${foreign[@]}")" || {
    log "ERROR: the simulated purge of ${foreign[*]} failed; nothing was purged."
    exit 1
  }
  refused=()
  while IFS= read -r line; do
    [[ "$line" =~ ^(Inst|Remv|Purg|Conf)( |$) ]] || continue
    read -r verb name _ <<<"$line"
    if [ "$verb" != "Purg" ] || [ -z "${name:-}" ] || ! in_package_set "$name" "${foreign[@]}"; then
      refused+=("$line")
    fi
  done <<<"$sim"
  if [ "${#refused[@]}" -gt 0 ]; then
    log "ERROR: purging the Microsoft-feed .NET packages (${foreign[*]}) would also do: ${refused[*]}. Nothing was purged; remove them yourself."
    exit 1
  fi
  log "Purging Microsoft-feed .NET packages: ${foreign[*]}."
  apt-get purge -y -qq "${foreign[@]}" >/dev/null
  PURGED_COUNT="${#foreign[@]}"
}

# The Ubuntu codename from /etc/os-release; stops the hook when there is none, or when
# it is anything but one plain lower-case word (it is written into the apt preference).
# Callers run it in a command substitution, so its exit only ends that subshell: each
# caller must stop on its failure explicitly.
os_codename() {
  local codename
  codename="$(awk -F= '$1 == "VERSION_CODENAME" { gsub(/"/, "", $2); print $2 }' /etc/os-release 2>/dev/null || true)"
  if [ -z "$codename" ]; then
    log "ERROR: /etc/os-release names no VERSION_CODENAME, so the Ubuntu suites to pin are unknown."
    exit 1
  fi
  if ! [[ "$codename" =~ ^[a-z][a-z0-9]*$ ]]; then
    log "ERROR: VERSION_CODENAME in /etc/os-release is not a single lower-case word, so it is not written into the apt preference."
    exit 1
  fi
  printf '%s\n' "$codename"
}

# Pins dotnet*, aspnetcore* and netstandard* to Ubuntu's <codename>-updates and
# <codename>-security suites. Written whenever the hook runs as root, not only before an
# install, so a container where an earlier version of this hook wrote a wider pin gets
# this one. Rewriting it is idempotent.
write_dotnet_pin() {
  local codename
  codename="$(os_codename)" || exit 1

  # Above 1000 a pin also permits downgrades, so it names only the two suites that publish
  # dotnet-sdk-10.0, not every Ubuntu suite (backports and proposed stay at their defaults).
  printf 'Package: dotnet* aspnetcore* netstandard*\nPin: release o=Ubuntu,a=%s\nPin-Priority: 1001\n\n' \
    "${codename}-updates" "${codename}-security" >"$DOTNET_PREFS"
}

# Reached only as root, after the main block below has written the pin, so it does not
# write the pin again.
install_sdk() {
  export DEBIAN_FRONTEND=noninteractive
  local codename
  codename="$(os_codename)" || exit 1

  # dotnet-sdk-10.0 is published in <codename>-updates and -security, so the image's own
  # Ubuntu lists need a refresh; the package archive is reachable through the proxy.
  log "Installing dotnet-sdk-10.0 from Ubuntu's ${codename}-updates/${codename}-security archive."
  apt-get update -qq
  apt-get install -y -qq --no-remove dotnet-sdk-10.0 >/dev/null
}

write_profile() {
  local profile=/etc/profile.d/dotnet.sh want
  want="${ENV_MARKER}
export DOTNET_ROOT=${DOTNET_DIR}
"'case ":$PATH:" in *":'"${DOTNET_DIR}"':"*) ;; *) PATH="'"${DOTNET_DIR}"':$PATH" ;; esac
case ":$PATH:" in *":${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools:"*) ;; *) PATH="$PATH:${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools" ;; esac
export PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1'
  if is_root && [ "$(cat "$profile" 2>/dev/null)" != "$want" ]; then
    printf '%s\n' "$want" >"$profile"
  fi
  # Once per file: a session can fire SessionStart more than once (resume, clear, compact),
  # and the block's first line (which names the dotnet root) is the marker that says it is
  # already there.
  if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -qxF "$ENV_MARKER" "$CLAUDE_ENV_FILE" 2>/dev/null; then
    printf '%s\n' "$want" >>"$CLAUDE_ENV_FILE"
  fi
}

# Nothing below runs a dotnet host with the repository as its working directory until the
# global.json the host would read has been approved.
global_json_safe "$REPO_DIR" || exit 1

# Runs whenever root, not only before a fresh install: a container that an earlier version
# of this hook already moved to the Ubuntu SDK may still hold the stranded Microsoft-feed
# packages and the wider pin. On a clean container the purge finds nothing and changes
# nothing, and the pin is rewritten with the same content. install_sdk relies on the pin
# written here.
if is_root; then
  export DEBIAN_FRONTEND=noninteractive
  remove_microsoft_feed
  write_dotnet_pin
fi
if ! sdk_ok; then
  if ! is_root; then
    log "ERROR: no SDK under ${DOTNET_DIR} satisfies global.json, and installing one needs root. This hook never escalates; install the SDK yourself."
    exit 1
  fi
  install_sdk
  sdk_ok || { log "ERROR: dotnet-sdk-10.0 installed, but no SDK under ${DOTNET_DIR} satisfies global.json."; exit 1; }
fi
if is_root; then
  [ "$(readlink -f /usr/bin/dotnet 2>/dev/null)" = "$DOTNET_DIR/dotnet" ] || ln -sf "$DOTNET_DIR/dotnet" /usr/bin/dotnet
else
  log "Not root: /usr/bin/dotnet and /etc/profile.d/dotnet.sh are left as they are; the session environment is still written."
fi
write_profile

export DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
purge_note=""
if [ "$PURGED_COUNT" -gt 0 ]; then
  purge_note="; purged ${PURGED_COUNT} Microsoft-feed .NET package(s)"
fi
# ---- the pinned engine CLI ----
hook_status=0
summary_cli="no usable vouchfx (see stderr)"
read -r pin_ver pin_sha _ <"$REPO_DIR/ENGINE_PIN" || true
if [[ "${pin_ver:-}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ && "${pin_sha:-}" =~ ^[0-9a-f]{40}$ ]]; then
  cli_want="${pin_ver#v}"
  expected="${cli_want}+${pin_sha}"
  # Keep the installed copy only when it starts WITHOUT roll-forward and is the pinned
  # build. One that starts only when forced is replaced (same version reinstalled with
  # --allow-roll-forward; a different version was always replaced).
  plain_before="$(probe_cli plain)"
  if [ "$plain_before" != "$expected" ]; then
    if [ -z "$plain_before" ] && [ -x "$VOUCHFX_TOOL" ]; then
      forced_before="$(probe_cli forced)"
      if [ "${forced_before%%+*}" = "$cli_want" ]; then
        log "vouchfx ${forced_before} is the pinned version but does not start without DOTNET_ROLL_FORWARD; reinstalling it with --allow-roll-forward."
      fi
    fi
    install_cli "$cli_want" || log "WARNING: could not install vouchfx ${cli_want}; the pinned-CLI parity tests will self-skip."
  fi
  actual="$(cli_version)"
  if [ "$actual" = "$expected" ]; then
    summary_cli="vouchfx ${actual} matches ENGINE_PIN"
  elif [ "${actual%%+*}" = "$cli_want" ]; then
    # Fail closed. CliPinVerifier compares the version alone, so the parity tests would RUN
    # against this build rather than self-skip. Removing it makes them skip, as they do
    # with no CLI at all. Reaching here means nuget.org's package for the pinned version
    # names another commit (so ENGINE_PIN's sha is wrong), or the replacement above could
    # not remove this copy. Either needs a person, so the hook exits non-zero.
    hook_status=1
    if scratch_dotnet tool uninstall -g vouchfx >/dev/null 2>&1 && [ -z "$(cli_version)" ]; then
      log "ERROR: vouchfx --version reported '${actual}', the pinned version from another build; ENGINE_PIN expects '${expected}'. Removed it, so the pinned-CLI parity tests will self-skip."
      summary_cli="removed vouchfx ${actual}, which is not the build ENGINE_PIN names (see stderr)"
    else
      log "ERROR: vouchfx --version reports '${actual}', the pinned version from another build; ENGINE_PIN expects '${expected}'. It could not be removed, so the pinned-CLI parity tests will run against it."
      summary_cli="vouchfx ${actual} is not the build ENGINE_PIN names (see stderr)"
    fi
  else
    log "WARNING: vouchfx --version reports '${actual:-<none>}'; ENGINE_PIN expects '${expected}'. The pinned-CLI parity tests will self-skip."
  fi
  # A CLI that starts only under DOTNET_ROLL_FORWARD=Major (an older install without
  # --allow-roll-forward) still serves the tests when the tests' own environment sets it,
  # but the session does not: say so rather than export it.
  if [ -n "$actual" ] && [ -z "$(probe_cli plain)" ]; then
    summary_cli="${summary_cli} (runs only with DOTNET_ROLL_FORWARD=Major)"
  fi
else
  log "WARNING: ENGINE_PIN's first line is not '<vX.Y.Z[-pre]> <40-char sha>'; skipping the CLI install."
fi
echo "session-start: .NET SDK $(scratch_dotnet --version) ready${purge_note}; ${summary_cli}."
exit "$hook_status"
