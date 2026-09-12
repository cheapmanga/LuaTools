# Apply this plugin folder to an already-installed LuaTools, without rebuilding or re-downloading the app.
#
# LuaTools reads the store-page frontend from %AppData%\LuaToolsGui\plugin at startup. This script copies
# the files from THIS folder over that install, then restarts LuaTools so the change is re-injected. Use it
# to iterate on the UI (luatools.js, themes) in seconds instead of pulling the 76 MB app each time.
#
# Run inside the sandbox/VM AFTER LuaTools has been launched once (so the plugin folder exists):
#   powershell -ExecutionPolicy Bypass -File apply-dev.ps1
# Then reload the Steam store page (or restart Steam) to see it.
#
# Note: the crescent logo is embedded in LuaTools.exe (served over /icon), so a NEW logo still needs a new
# app build. Everything else - layout, colours, themes, text - lives here and applies instantly.

$ErrorActionPreference = 'Stop'
$src  = $PSScriptRoot
$dest = Join-Path $env:APPDATA 'LuaToolsGui\plugin'

if (-not (Test-Path $dest)) {
    Write-Host "Plugin folder not found at $dest" -ForegroundColor Yellow
    Write-Host "Launch LuaTools once so it installs the plugin, then run this again." -ForegroundColor Yellow
    exit 1
}

Write-Host "Copying plugin -> $dest"
Copy-Item (Join-Path $src 'public')      $dest -Recurse -Force
Copy-Item (Join-Path $src 'backend')     $dest -Recurse -Force
Copy-Item (Join-Path $src 'plugin.json') $dest -Force

# Restart LuaTools so CefInjectorService re-reads luatools.js and re-injects it.
$exe = Join-Path $env:LOCALAPPDATA 'LuaTools\current\LuaTools.exe'
Get-Process LuaTools -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path $exe) {
    Start-Process $exe
    Write-Host "LuaTools restarted. Reload the Steam store page to see the change." -ForegroundColor Green
} else {
    Write-Host "Copied. LuaTools.exe not found at the default path - start it yourself, then reload the store page." -ForegroundColor Green
}
