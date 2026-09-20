#!/usr/bin/env bash
# Reproduce CI from a shell, with one command:
#
#   tools/ci/local.sh [stage...]      default: test build
#
# It runs the same tools/ci/run.sh stages CI runs. Where it runs them depends on
# the machine: this workstation runs the game and has little free memory, so when
# an Incus remote holding the build container is reachable the stages run *there*
# and the output is streamed back. Nothing is installed into the game, and no
# artifact is copied back: build output stays in the container (the local
# equivalent is `tools/ci/run.sh build` by hand).
#
# Configured only by the environment:
#   CI_LOCAL_REMOTE   0 forces a local run even when the remote is reachable
#   INCUS_REMOTE      which Incus remote to use; unset = a local run when
#                     `incus remote list` has it, otherwise a local run
#   INCUS             the incus binary, default `incus`
#   CI_CONTAINER      the container on that remote, default xiv-mcp-build
#   CI_REMOTE_DIR     where the checkout lands in it, default /build/xiv-mcp
#   DALAMUD_LIB_PATH, UMBRA_LIB_PATH, SKIP_PLUGIN_TESTS, CI_TRX
#                     passed through to tools/ci/run.sh for a local run only.
#                     The container has no game installation, so a remote run
#                     skips the Dalamud projects and the plugin tests exactly as
#                     a GitHub-hosted runner does; run those stages locally (or
#                     on the gaming PC's runner) to cover them.
#
# See docs/CI.md.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

INCUS="${INCUS:-incus}"
CONTAINER="${CI_CONTAINER:-xiv-mcp-build}"
D="${CI_REMOTE_DIR:-/build/xiv-mcp}"

log() { printf '== local: %s\n' "$*"; }
die() { printf 'local: error: %s\n' "$*" >&2; exit 1; }

usage() { sed -n '2,27p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

stages=()
for a in "$@"; do
  case "$a" in
    -h | --help) usage; exit 0 ;;
    *) stages+=("$a") ;;
  esac
done
[[ ${#stages[@]} -gt 0 ]] || stages=(test build)

# Which Incus remote, if any. CI_LOCAL_REMOTE=0 skips the whole question.
pick_remote() {
  [[ "${CI_LOCAL_REMOTE:-1}" == 0 ]] && return 1
  if [[ -n "${INCUS_REMOTE:-}" ]]; then return 0; fi
  command -v "$INCUS" >/dev/null || return 1
  if "$INCUS" remote list --format csv 2>/dev/null | cut -d, -f1 | grep -qx "${INCUS_REMOTE:-__none__}"; then
    INCUS_REMOTE="${INCUS_REMOTE:-}"
    return 0
  fi
  return 1
}

run_local() {
  log "running locally: tools/ci/run.sh ${stages[*]}"
  exec "$ROOT/tools/ci/run.sh" "${stages[@]}"
}

# Everything git considers part of this worktree: tracked files plus untracked
# ones .gitignore does not exclude. artifacts/ is ignored, so build output in the
# container is left alone between runs.
push_source() { # push_source <remote:container>
  local c="$1" list
  log "sending the checkout to $c:$D"
  list="$(mktemp)"
  git -C "$ROOT" ls-files -z --cached --others --exclude-standard >"$list"
  "$INCUS" exec "$c" -- bash -c "mkdir -p $D && find $D -mindepth 1 -maxdepth 1 ! -name artifacts -exec rm -rf {} +"
  tar -C "$ROOT" --null -T "$list" -czf - | "$INCUS" exec "$c" -- tar -xzf - -C "$D"
  rm -f "$list"
  "$INCUS" exec "$c" -- bash -c "chmod +x $D/tools/ci/*.sh $D/tools/*.sh 2>/dev/null || true"
}

run_remote() {
  local c="$INCUS_REMOTE:$CONTAINER"
  "$INCUS" info "$c" >/dev/null 2>&1 || die "no container $c (create it, or set CI_LOCAL_REMOTE=0 to run here)"
  if [[ "$("$INCUS" info "$c" | awk '/^Status:/{print tolower($2)}')" != running ]]; then
    log "starting $c"
    "$INCUS" start "$c"
  fi
  push_source "$c"
  log "tools/ci/run.sh ${stages[*]} in $c"
  # HOME is set so run.sh finds ~/.dotnet and the NuGet cache in the container
  "$INCUS" exec "$c" --env HOME=/root -- bash -lc "cd $D && tools/ci/run.sh ${stages[*]}"
}

if pick_remote; then
  run_remote
else
  run_local
fi
