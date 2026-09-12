# LuaTools store-page plugin

Source of the frontend injected into the Steam store page: the UI (`public/luatools.js`), the themes
(`public/themes/`), the crescent icon fallback (`public/luatools-icon.png`), the webkit CSS, and the lua
backend bridge (`backend/`, used only under the Millennium loader).

This folder is the single source of truth. `scripts/pack-plugin.sh` zips it into
`src/LuaToolsGui/Assets/plugin.zip`, which the app embeds and installs at runtime.

## Editing the UI

1. Edit files here (mostly `public/luatools.js` and `public/themes/`).
2. `bash scripts/pack-plugin.sh` — rebuilds the embedded zip.
3. Build/publish the app as usual to ship it.

## Testing a change WITHOUT rebuilding the 76 MB app

LuaTools reads `%AppData%\LuaToolsGui\plugin` at startup, so you can overlay this folder onto an
already-installed app and just restart LuaTools:

1. Launch LuaTools once (creates the plugin folder).
2. Run `apply-dev.ps1` from this folder inside the sandbox/VM.
3. Reload the Steam store page.

Only the embedded crescent logo (served over the app's `/icon` route) needs a full app build; everything
else — layout, colours, themes, text — applies instantly this way.
