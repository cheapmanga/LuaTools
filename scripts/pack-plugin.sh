#!/usr/bin/env bash
# Rebuild the embedded store-page plugin from its source folder.
#
# plugin/ is the source of truth for the Steam store-page frontend (luatools.js, themes, icon, the
# lua backend bridge). It is zipped here into src/LuaToolsGui/Assets/plugin.zip, which the csproj embeds
# as "bundled-plugin.zip" and PluginInstallerService installs at runtime. Run this after editing anything
# under plugin/, then build/publish as usual. Deterministic (-X strips extra attrs) so the zip only
# changes when the content does.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/src/LuaToolsGui/Assets/plugin.zip"
rm -f "$out"
( cd "$root/plugin" && zip -r -q -X "$out" . -x '.*' )
printf 'packed %s\n' "$out"
ls -l "$out" | awk '{printf "  %.0f KiB\n", $5/1024}'
