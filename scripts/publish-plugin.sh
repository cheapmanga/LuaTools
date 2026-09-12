#!/usr/bin/env bash
# Publish a plugin-only update: repack plugin/ and upload plugin.zip to the fixed "plugin" release on
# cheapmanga/LuaTools. Installed apps detect the new plugin.zip by its digest and update the store-page
# plugin on their own - NO new app build, NO 76 MB re-download.
#
# The loader winmm.dll on that release is stable and is NOT touched here; pass --with-dll <path> only if it
# ever needs replacing.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo="cheapmanga/LuaTools"
tag="plugin"

# 1) Rebuild the zip from the source folder (also refreshes the app's embedded fallback copy).
bash "$root/scripts/pack-plugin.sh"
zip="$root/src/LuaToolsGui/Assets/plugin.zip"

# 2) Upload it to the plugin channel (clobber the existing asset; the tag does not move).
gh release upload "$tag" "$zip" --clobber --repo "$repo"

echo "Published plugin.zip to $repo@$tag"
gh release view "$tag" --repo "$repo" --json assets -q '.assets[] | "  \(.name)  \(.size)  \(.digest)"'
echo "Installed apps will pick it up on their next plugin check."
