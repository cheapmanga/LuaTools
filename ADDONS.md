# Addons

An addon is a folder in `%AppData%\LuaToolsGui\addons\`, holding an `addon.json`. The folder name **is**
the addon id — a manifest whose `id` disagrees with its folder is refused, so one addon cannot pose as
another by editing a file.

**Nothing needs restarting.** Drop a folder in and open the Addons page (or hit **Refresh**) — its
sources are live from the next fetch, and enabling or disabling one takes effect the same way.

**An addon is data, and only data.** It can say *where* manifests are fetched from; it cannot supply
code, a binary, or a fetch routine of its own. It names one of the shapes below and the app does the
fetching, so installing an addon from a stranger cannot execute anything.

## An addon

A source the Add page can fetch from:

`%AppData%\LuaToolsGui\addons\example.freesource\addon.json`:

```json
{
  "schema": 1,
  "id": "example.freesource",
  "name": "Example free source",
  "version": "1.0.0",
  "author": "you",
  "description": "The smallest useful addon: one manifest source, no code.",
  "homepage": "https://github.com/cheapmanga/LuaTools",

  "sources": [
    {
      "name": "example-zip",
      "displayName": "Example (free)",
      "kind": "manifestZip",
      "url": "https://raw.githubusercontent.com/someone/some-repo/main/{appid}.zip",
      "mirrors": [
        "https://cdn.jsdelivr.net/gh/someone/some-repo@main/{appid}.zip"
      ],
      "order": 150,
      "badge": "Free",
      "free": true
    }
  ]
}
```

This is what the format exists for. Free manifest sources rot: the upstream ManifestHub repo has been
frozen since January 2026, and the app had to ship a whole new build just to point at fresher community
forks. An addon turns that into editing one line.

### Source kinds

| `kind` | What it points at | `{appid}` in the url |
|---|---|---|
| `manifestZip` | one `<appid>.zip` per game, holding the lua **and** its `.manifest` files | required |
| `luaFile` | one `<appid>.lua` per game, entitlements and depot keys only | required |
| `depotKeyDatabase` | a single flat JSON map of `depot id → key` for every game at once | not used |

Every url must be `https`. Source names that belong to the app (`manifesthub`, `sushi`, `luatools`,
`hubcap`, `sadie`) are refused rather than silently shadowed.

## When something doesn't load

The Addons page lists every folder it found and, for each, either what it contributed or why it was
refused. A broken addon never stops the app from starting.
