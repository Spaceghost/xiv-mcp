#!/usr/bin/env bash
# Build XivMcp (Release) and print the path to add under
#   Dalamud Settings -> Experimental -> Dev Plugin Locations
# This script never edits Dalamud's configuration and never copies into ~/.xlcore;
# it stages the build in ../xiv-mcp-build/devplugin (XIVMCP_STAGE overrides).
#
# Environment:
#   DOTNET            dotnet executable (default: ~/.dotnet/dotnet, then dotnet on PATH)
#   XIVMCP_ARTIFACTS  build output root (default: ../xiv-mcp-build/artifacts next to the repo)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
project="$repo/src/XivMcp.Plugin/XivMcp.Plugin.csproj"

dotnet="${DOTNET:-}"
if [[ -z "$dotnet" ]]; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet="$HOME/.dotnet/dotnet"
  elif command -v dotnet >/dev/null 2>&1; then
    dotnet="$(command -v dotnet)"
  else
    echo "install-dev: dotnet SDK not found (set DOTNET=/path/to/dotnet)" >&2
    exit 1
  fi
fi

artifacts="${XIVMCP_ARTIFACTS:-$(cd "$repo/.." && pwd -P)/xiv-mcp-build/artifacts}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 XIVMCP_ARTIFACTS="$artifacts"

echo "install-dev: building $project (Release) into $artifacts" >&2
"$dotnet" build "$project" -c Release -nologo -v quiet >&2

dll="$artifacts/bin/XivMcp.Plugin/release/XivMcp.dll"
if [[ ! -f "$dll" ]]; then
  echo "install-dev: build finished but $dll is missing" >&2
  exit 1
fi

# Stage into a fixed directory so Dalamud (auto-reload) only ever sees complete
# builds, never a half-written bin/ from an in-progress compile.
stage="${XIVMCP_STAGE:-$(cd "$repo/.." && pwd -P)/xiv-mcp-build/devplugin}"
mkdir -p "$stage"
src_dir="$(dirname "$dll")"
for f in XivMcp.json XivMcp.Core.dll XivMcp.Core.pdb XivMcp.deps.json XivMcp.pdb XivMcp.dll; do
  [[ -f "$src_dir/$f" ]] || continue
  cp "$src_dir/$f" "$stage/.$f.tmp" && mv -f "$stage/.$f.tmp" "$stage/$f"
done
dll="$(readlink -f "$stage/XivMcp.dll")"

# Wine maps the host root to drive Z:, so /a/b/c becomes Z:\a\b\c.
windows_path="Z:${dll//\//\\}"

cat >&2 <<EOF

Built: $dll

In game: /xlsettings -> Experimental -> Dev Plugin Locations, add the path below,
save, then enable "XivMcp" under Dev Tools -> Installed Dev Plugins (/xlplugins).
After rebuilding, reload the dev plugin from /xlplugins (or enable its auto-reload option).
EOF
printf '%s\n' "$windows_path"
