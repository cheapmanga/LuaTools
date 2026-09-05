#!/usr/bin/env bash
#
# Builds the fork's release asset: a standalone LuaTools that runs on a clean Windows 10/11 with
# nothing installed.
#
# Why this exists. Official builds ship through a Velopack setup that installs the .NET 8 Desktop
# Runtime for the user. The fork ships a plain zip instead - no installer, no auto-update - so
# nothing is left to install that runtime, and a framework-dependent build simply refuses to start
# on a machine that lacks it. Publishing self-contained puts the runtime in the executable.
#
# Two flags are deliberate:
#   PublishSingleFile + IncludeNativeLibrariesForSelfExtract  - without the second one, WPF's native
#     libraries (wpfgfx_cor3, PresentationNative_cor3, ...) are left beside the exe and "single file"
#     is a folder of six files.
#   No PublishTrimmed - WPF resolves types named in XAML by reflection, and the trimmer cannot see
#     those references. A trimmed build compiles, then throws at the first window.
#
# Needs only the .NET 8 SDK; runs from Linux as well as Windows.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/publish/standalone}"

rm -rf "$out"

# Stamp the build's git commit so StandaloneUpdateService can tell this build from the one the release tag
# points at. MUST be the commit the moving tag will be re-pointed to at publish (i.e. current HEAD).
#
# PUBLISH INVARIANT (or the self-updater re-installs forever): the zip built here must come from the SAME
# commit the moving tag is re-pointed to, and to avoid a window where the tag is ahead of the asset, upload
# THIS zip first, THEN move the v1.1.3-fork tag ref to this commit.
commit="$(git -C "$root" rev-parse --short HEAD 2>/dev/null || true)"

dotnet publish "$root/src/LuaToolsGui/LuaToolsGui.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableWindowsTargeting=true \
    -p:DebugType=none \
    -p:BuildCommit="$commit" \
    -o "$out"

# LuaTools.SamHost.exe stays a separate file: it is x86/net48 and cannot live inside an x64/net8.0
# bundle. The csproj copies it here; the two files ship together and must stay together.
test -f "$out/LuaTools.exe" && test -f "$out/LuaTools.SamHost.exe"

# Ship them inside a folder, so unzipping into a Downloads folder doesn't scatter them.
stage="$(mktemp -d)"
mkdir "$stage/LuaTools"
cp "$out/LuaTools.exe" "$out/LuaTools.SamHost.exe" "$stage/LuaTools/"
zip="$out/LuaTools-win-x64.zip"
rm -f "$zip"
(cd "$stage" && zip -9 -qr "$zip" LuaTools)
rm -rf "$stage"

printf '\n%s\n' "$zip"
ls -l "$zip" | awk '{printf "  %.1f MiB\n", $5/1048576}'
