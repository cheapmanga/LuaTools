# Addons

An addon is a folder in `%AppData%\LuaToolsGui\addons\`, holding an `addon.json`. The folder name **is**
the addon id — a manifest whose `id` disagrees with its folder is refused, so one addon cannot pose as
another by editing a file.

**A data addon needs no restart.** Drop it in and open the Addons page (or hit **Refresh**) — its
sources are live from the next fetch, and enabling or disabling one takes effect the same way. Only an
addon that carries an **assembly** needs LuaTools restarted: it registers services into a container that
is built once at startup, and its types can never be unloaded afterwards.

## The smallest useful addon

No code, nothing compiled, nothing executed — a source the Add page can fetch from:

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

This is the case the format exists for. Free manifest sources rot: the upstream ManifestHub repo has
been frozen since January 2026, and the app had to ship a whole new build just to point at fresher
community forks. A data addon turns that into editing one line.

### Source kinds

| `kind` | What it points at | `{appid}` in the url |
|---|---|---|
| `manifestZip` | one `<appid>.zip` per game, holding the lua **and** its `.manifest` files | required |
| `luaFile` | one `<appid>.lua` per game, entitlements and depot keys only | required |
| `depotKeyDatabase` | a single flat JSON map of `depot id → key` for every game at once | not used |

Every url must be `https`. Source names that belong to the app (`manifesthub`, `sushi`, `luatools`,
`hubcap`, `sadie`) are refused rather than silently shadowed.

## An addon with code

Reference `LuaTools.Addons` (**`Private=false`** — do not ship your own copy of it, or the host cannot
cast your addon to its own interface), implement `ILuaToolsAddon`, and name the built dll in the
manifest with its sha256:

```json
{
  "schema": 1,
  "id": "you.mypage",
  "name": "My page",
  "version": "1.0.0",
  "assembly": "MyAddon.dll",
  "assemblySha256": "…",
  "minHostVersion": "1.1.3"
}
```

```csharp
public sealed class MyAddon : ILuaToolsAddon
{
    public void Configure(IAddonContext ctx)
    {
        ctx.Services.AddSingleton<MyPageView>();
        ctx.AddPage(new AddonPage { Title = "My page", ViewType = typeof(MyPageView), Icon = "Star24" });
    }
}
```

`Configure` runs on the UI thread during startup, before the window exists, and every addon's runs
before the app is usable. Register and return — anything that can block belongs in the service your
page resolves, on first use.

### The sha256 is not a trust decision

A hash sitting next to the file it describes proves nothing against someone who can rewrite both. It is
there so a published addon, its catalog entry and the bytes on your disk can be shown to be the same
thing — which is what makes "it's open source" checkable rather than merely true. Read the source of
anything you install; that is the actual protection, and it is the same one LuaTools itself relies on.

## When something doesn't load

The Addons page lists every folder it found and, for each, either what it contributed or why it was
refused. A broken addon never stops the app from starting.
