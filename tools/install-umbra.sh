#!/usr/bin/env bash
# Build the Umbra.XivMcp custom Umbra plugin (toolbar widgets for the XivMcp MCP server).
#
#   tools/install-umbra.sh              build Release, print the DLL path and the Z:\ path for Umbra
#   tools/install-umbra.sh --install    also copy Umbra.XivMcp.dll (+ .pdb) into $UMBRA_PLUGIN_DIR
#
# Environment:
#   DOTNET            dotnet binary              (default: ~/.dotnet/dotnet, then dotnet on PATH)
#   XIVMCP_ARTIFACTS  bin/obj root for this build (default: ~/xiv-mcp-build/artifacts via Directory.Build.props)
#   UMBRA_PLUGIN_DIR  install target             (default: ~/umbra-plugins)
#   UMBRA_LIB_PATH    Umbra assemblies directory (default: newest ~/.xlcore/installedPlugins/Umbra/<version>/)
#   DALAMUD_LIB_PATH  Dalamud dev assemblies     (default: ~/.xlcore/dalamud/Hooks/dev/)
#
# Installing only copies files. It never edits Umbra's or Dalamud's configuration: adding the
# plugin in Umbra Settings -> Plugins and restarting Umbra are manual steps (see docs/UMBRA.md).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/XivMcp.Umbra/XivMcp.Umbra.csproj"
INSTALL=0

for arg in "$@"; do
  case "$arg" in
    --install) INSTALL=1 ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg (try --help)" >&2; exit 2 ;;
  esac
done

if [[ -n "${DOTNET:-}" ]]; then :;
elif [[ -x "$HOME/.dotnet/dotnet" ]]; then DOTNET="$HOME/.dotnet/dotnet";
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)";
else echo "dotnet SDK not found (set DOTNET=/path/to/dotnet)" >&2; exit 127; fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[[ -x "$HOME/.dotnet/dotnet" && "$DOTNET" == "$HOME/.dotnet/dotnet" ]] && export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

props=()
[[ -n "${UMBRA_LIB_PATH:-}" ]] && props+=("-p:UmbraLibPath=${UMBRA_LIB_PATH%/}/")
[[ -n "${DALAMUD_LIB_PATH:-}" ]] && props+=("-p:DalamudLibPath=${DALAMUD_LIB_PATH%/}/")

ARTIFACTS="${XIVMCP_ARTIFACTS:-$ROOT/../xiv-mcp-build/artifacts}"
export XIVMCP_ARTIFACTS="$ARTIFACTS"

echo "== building Umbra.XivMcp (Release)"
"$DOTNET" build "$PROJECT" -c Release -nologo "${props[@]}"

OUT="$(cd "$ARTIFACTS" && pwd)/bin/XivMcp.Umbra/release"
DLL="$OUT/Umbra.XivMcp.dll"
[[ -f "$DLL" ]] || { echo "build finished but $DLL is missing" >&2; exit 1; }

# Wine maps the Linux root to Z:, which is how Umbra (running under Wine) sees host paths.
winpath() { local p="${1//\//\\}"; printf 'Z:%s' "$p"; }

echo
echo "built: $DLL"
if [[ "$INSTALL" != 1 ]]; then
  echo "Windows path (for Umbra Settings -> Plugins, if you load it from the build dir):"
  echo "  $(winpath "$DLL")"
  echo
  echo "Not installed. Re-run with --install to copy it to ${UMBRA_PLUGIN_DIR:-$HOME/umbra-plugins}/."
  exit 0
fi

DEST="${UMBRA_PLUGIN_DIR:-$HOME/umbra-plugins}"
mkdir -p "$DEST"
cp -f "$DLL" "$DEST/Umbra.XivMcp.dll"
if [[ -f "$OUT/Umbra.XivMcp.pdb" ]]; then cp -f "$OUT/Umbra.XivMcp.pdb" "$DEST/Umbra.XivMcp.pdb"; fi
echo "installed: $DEST/Umbra.XivMcp.dll"
echo "Add this path in Umbra Settings -> Plugins (once), then restart Umbra:"
echo "  $(winpath "$DEST/Umbra.XivMcp.dll")"
