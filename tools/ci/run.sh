#!/usr/bin/env bash
# The one CI entry point: GitHub Actions, a self-hosted runner and a local shell
# all run this. Nothing else knows how to build or test this repository.
#
#   tools/ci/run.sh <stage>...       stages: deps test build package all
#
#   deps     locate the .NET 10 SDK and the Dalamud reference assemblies, then
#            `dotnet restore XivMcp.slnx`
#   test     dotnet test tests/XivMcp.Core.Tests -c Release, then
#            tests/XivMcp.Plugin.Tests (which needs the Dalamud reference
#            assemblies; without them it is skipped, not failed)
#   build    dotnet build XivMcp.slnx -c Release. Without Dalamud (and Umbra)
#            reference assemblies the Dalamud projects cannot compile at all, so
#            the stage then builds the rest of the solution and says which
#            projects it left out
#   package  this repository has no packaging step yet (tools/install-dev.sh
#            stages a dev plugin into the build directory for the local game; it
#            is not a release artifact). The stage says so and does nothing
#   all      test, build, package
#
# Configured only by the environment (see docs/CI.md):
#   DOTNET             dotnet binary; unset = `dotnet` on PATH, else ~/.dotnet/dotnet
#   DALAMUD_LIB_PATH   directory holding Dalamud.dll (XIVLauncher's dev hooks,
#                      ~/.xlcore/dalamud/Hooks/dev by default). Unset or without
#                      Dalamud.dll: SKIP_PLUGIN_TESTS=1 semantics, i.e. the
#                      plugin tests and the Dalamud projects are skipped with a
#                      message
#   UMBRA_LIB_PATH     Umbra reference assemblies for src/XivMcp.Umbra; unset =
#                      the newest ~/.xlcore/installedPlugins/Umbra/<version>.
#                      Umbra is a Dalamud plugin, not a package: a runner that has
#                      no installed copy cannot build src/XivMcp.Umbra at all, so
#                      the build stage leaves that one project out with a message
#   SKIP_PLUGIN_TESTS  1 skips tests/XivMcp.Plugin.Tests even when Dalamud is there
#   CI_CACHE_DIR       cache root, default ${XDG_CACHE_HOME:-~/.cache}/xiv-mcp-ci;
#                      the NuGet package cache lives in it
#   XIVMCP_ARTIFACTS   MSBuild artifacts path, default <repo>/artifacts (inside
#                      the checkout and git-ignored, so CI can upload it)
#   CI_TRX             0 turns off the .trx test reports (a plain-text summary is
#                      written either way)
#
# Nothing here needs a secret, a token or network access beyond NuGet.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

CACHE="${CI_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/xiv-mcp-ci}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$CACHE/nuget}"
export XIVMCP_ARTIFACTS="${XIVMCP_ARTIFACTS:-$ROOT/artifacts}"
# a stable culture in the logs, and no first-run banners
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_UI_LANGUAGE=en
RESULTS="$XIVMCP_ARTIFACTS/test-results"

# the solution's Dalamud-dependent projects: they reference Dalamud.dll (and,
# for Umbra, the installed Umbra assemblies) by path, so without those files
# they cannot be compiled at all
DALAMUD_PROJECTS=(src/XivMcp.Plugin src/XivMcp.Umbra tests/XivMcp.Plugin.Tests)
# everything else in XivMcp.slnx, built when the Dalamud assemblies are missing
PORTABLE_PROJECTS=(src/XivMcp.Core src/XivMcp.DevHost tools/catalog tests/XivMcp.Core.Tests)

log() { printf '== ci: %s\n' "$*"; }
die() { printf 'ci: error: %s\n' "$*" >&2; exit 1; }

usage() { sed -n '2,38p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

# DOTNET: the caller's, else dotnet on PATH, else the SDK installed in ~/.dotnet.
# `dotnet --version` in the repository root is the honest test: it fails when no
# installed SDK satisfies global.json (10.0.401, rollForward latestFeature, so
# any 10.0.4xx or newer feature band of 10.0 does, but 10.0.1xx does not).
pins_ok() { ( cd "$ROOT" && "$1" --version ) >/dev/null 2>&1; }

ensure_dotnet() {
  local candidates=() c
  if [[ -n "${DOTNET:-}" ]]; then
    candidates=("$DOTNET")
  else
    command -v dotnet >/dev/null && candidates+=("$(command -v dotnet)")
    [[ -x "$HOME/.dotnet/dotnet" ]] && candidates+=("$HOME/.dotnet/dotnet")
    [[ ${#candidates[@]} -gt 0 ]] || die "the .NET 10 SDK is required (set DOTNET=/path/to/dotnet)"
  fi
  DOTNET="${candidates[0]}"
  for c in "${candidates[@]}"; do
    if pins_ok "$c"; then DOTNET="$c"; break; fi
  done
  pins_ok "$DOTNET" || die "no installed SDK satisfies global.json ($(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$ROOT/global.json")): $(for c in "${candidates[@]}"; do "$c" --list-sdks 2>/dev/null | tr '\n' ' '; done)"
  export DOTNET
  log "dotnet $( cd "$ROOT" && "$DOTNET" --version ) ($DOTNET)"
}

# HAVE_DALAMUD=1 when there are reference assemblies to build the Dalamud
# projects and run the plugin tests against. This is a skip, never a failure:
# a hosted runner has no game installation and is still expected to be green.
HAVE_DALAMUD=0
ensure_dalamud() {
  local dir="${DALAMUD_LIB_PATH:-$HOME/.xlcore/dalamud/Hooks/dev}"
  if [[ -f "$dir/Dalamud.dll" ]]; then
    DALAMUD_LIB_PATH="$dir"
    export DALAMUD_LIB_PATH
    HAVE_DALAMUD=1
    log "dalamud reference assemblies in $DALAMUD_LIB_PATH"
  else
    HAVE_DALAMUD=0
    log "no Dalamud.dll in ${DALAMUD_LIB_PATH:-$dir}: the Dalamud projects and the plugin tests are skipped (SKIP_PLUGIN_TESTS=1)"
  fi
}

# HAVE_UMBRA=1 when there are Umbra reference assemblies for src/XivMcp.Umbra.
# Umbra ships as a Dalamud plugin, so a hosted runner has no way to fetch it; like
# the Dalamud check above this is a skip, never a failure.
HAVE_UMBRA=0
ensure_umbra() {
  local dir="${UMBRA_LIB_PATH:-}"
  if [[ -z "$dir" ]]; then
    local root="${XLCORE_DIR:-$HOME/.xlcore}/installedPlugins/Umbra"
    if [[ -d "$root" ]]; then
      dir="$(find "$root" -mindepth 1 -maxdepth 1 -type d | LC_ALL=C sort -V | tail -1)"
    fi
  fi
  if [[ -n "$dir" && -f "${dir%/}/Umbra.dll" ]]; then
    UMBRA_LIB_PATH="${dir%/}"
    export UMBRA_LIB_PATH
    HAVE_UMBRA=1
    log "umbra reference assemblies in $UMBRA_LIB_PATH"
  else
    HAVE_UMBRA=0
    log "no Umbra.dll${dir:+ in $dir}: src/XivMcp.Umbra is left out of the build (set UMBRA_LIB_PATH)"
  fi
}

# The csprojs take DalamudLibPath/UmbraLibPath as MSBuild properties and want a
# trailing separator; UMBRA_LIB_PATH is only passed when the caller set it.
dotnet_props() {
  local p=()
  if [[ "$HAVE_DALAMUD" == 1 ]]; then p+=("-p:DalamudLibPath=${DALAMUD_LIB_PATH%/}/"); fi
  if [[ -n "${UMBRA_LIB_PATH:-}" ]]; then p+=("-p:UmbraLibPath=${UMBRA_LIB_PATH%/}/"); fi
  if [[ ${#p[@]} -gt 0 ]]; then printf '%s\n' "${p[@]}"; fi
}

stage_deps() {
  ensure_dotnet
  ensure_dalamud
  ensure_umbra
  mkdir -p "$XIVMCP_ARTIFACTS" "$NUGET_PACKAGES"
  log "dotnet restore XivMcp.slnx (packages in $NUGET_PACKAGES)"
  local props=()
  mapfile -t props < <(dotnet_props)
  "$DOTNET" restore XivMcp.slnx "${props[@]}"
}

# One test project. The trx logger is best effort: the summary file below is
# written from the run's own output, so a report always exists.
run_test_project() { # run_test_project <project>
  local proj="$1" name log_file rc=0
  name="$(basename "$proj")"
  log_file="$RESULTS/$name.log"
  mkdir -p "$RESULTS"
  local args=(test "$proj" -c Release)
  local props=()
  mapfile -t props < <(dotnet_props)
  args+=("${props[@]}")
  [[ "${CI_TRX:-1}" == 0 ]] || args+=(--logger "trx;LogFileName=$name.trx" --results-directory "$RESULTS")
  log "dotnet ${args[*]}"
  set +e
  "$DOTNET" "${args[@]}" 2>&1 | tee "$log_file"
  rc=${PIPESTATUS[0]}
  set -e
  # the one line every runner prints; kept as the plain-text summary
  grep -Ei 'failed!|passed!|Passed:|Failed:|Test summary' "$log_file" | tail -5 >>"$RESULTS/summary.txt" || true
  [[ $rc -eq 0 ]] || die "$name failed (exit $rc, log $log_file)"
}

stage_test() {
  ensure_dotnet
  ensure_dalamud
  mkdir -p "$RESULTS"
  : >"$RESULTS/summary.txt"
  run_test_project tests/XivMcp.Core.Tests
  if [[ "${SKIP_PLUGIN_TESTS:-0}" == 1 ]]; then
    log "SKIP_PLUGIN_TESTS=1: tests/XivMcp.Plugin.Tests skipped"
    echo "tests/XivMcp.Plugin.Tests: skipped (SKIP_PLUGIN_TESTS=1)" >>"$RESULTS/summary.txt"
  elif [[ "$HAVE_DALAMUD" != 1 ]]; then
    log "tests/XivMcp.Plugin.Tests skipped: no Dalamud reference assemblies (set DALAMUD_LIB_PATH)"
    echo "tests/XivMcp.Plugin.Tests: skipped (no Dalamud reference assemblies)" >>"$RESULTS/summary.txt"
  else
    run_test_project tests/XivMcp.Plugin.Tests
  fi
  log "test summary ($RESULTS/summary.txt):"
  cat "$RESULTS/summary.txt"
}

stage_build() {
  ensure_dotnet
  ensure_dalamud
  ensure_umbra
  local props=() projects=() p
  mapfile -t props < <(dotnet_props)
  if [[ "$HAVE_DALAMUD" == 1 && "$HAVE_UMBRA" == 1 ]]; then
    log "dotnet build XivMcp.slnx -c Release"
    "$DOTNET" build XivMcp.slnx -c Release "${props[@]}"
    log "build output under $XIVMCP_ARTIFACTS"
    return 0
  fi
  # Not a reduced-for-speed build: without those reference assemblies on disk the
  # projects have unresolvable <Reference HintPath>s and the solution build fails.
  projects=("${PORTABLE_PROJECTS[@]}")
  if [[ "$HAVE_DALAMUD" == 1 ]]; then
    projects+=(src/XivMcp.Plugin tests/XivMcp.Plugin.Tests)
    log "no Umbra reference assemblies: building without src/XivMcp.Umbra"
  else
    log "no Dalamud reference assemblies: building without ${DALAMUD_PROJECTS[*]}"
  fi
  for p in "${projects[@]}"; do
    log "dotnet build $p -c Release"
    "$DOTNET" build "$p" -c Release "${props[@]}"
  done
  log "build output under $XIVMCP_ARTIFACTS"
}

stage_package() {
  # There is no release packaging in this repository yet: the plugin is
  # installed as a Dalamud dev plugin by tools/install-dev.sh, which copies the
  # Release output into ~/xiv-mcp-build/devplugin on the developer's machine.
  # When a tools/package.sh appears, this stage runs it.
  if [[ -x "$ROOT/tools/package.sh" ]]; then
    log "tools/package.sh"
    "$ROOT/tools/package.sh"
    return 0
  fi
  log "no packaging in this checkout (no tools/package.sh): nothing to do"
}

[[ $# -gt 0 ]] || { usage >&2; exit 2; }
for s in "$@"; do
  case "$s" in
    deps | test | build | package | all) ;;
    -h | --help) usage; exit 0 ;;
    *) usage >&2; printf 'ci: error: unknown stage: %s\n' "$s" >&2; exit 2 ;;
  esac
done
for s in "$@"; do
  case "$s" in
    deps) stage_deps ;;
    test) stage_test ;;
    build) stage_build ;;
    package) stage_package ;;
    all) stage_test; stage_build; stage_package ;;
  esac
done
log "done: $*"
