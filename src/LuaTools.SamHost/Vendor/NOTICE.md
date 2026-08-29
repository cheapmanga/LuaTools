# Vendored third-party source

Everything under `Vendor/` comes from **Steam Achievement Manager** by Rick (gibbed),
<https://github.com/gibbed/SteamAchievementManager>, and is used under the **zlib license**
(the full text is in `LICENSE.txt`, and every file keeps its original copyright header).

LuaTools did not write this code and does not claim to have written it.

## What was taken

| Path | Upstream origin |
| --- | --- |
| `SAM.API/**` | `SAM.API/**` — the Steam client interop layer (`steamclient.dll` loading, `ISteamClient018`, `ISteamUserStats013`, callbacks) |
| `SAM.Game/KeyValue.cs`, `KeyValueType.cs`, `StreamHelpers.cs` | `SAM.Game/**` — Valve binary KeyValues parser, used to read `appcache\stats\UserGameStatsSchema_<appid>.bin` |

## Modifications

The files are **unmodified** copies of upstream. What LuaTools adds lives outside `Vendor/`
(`Program.cs`, `SteamSession.cs`, `SchemaReader.cs`, `Json.cs`): a headless stdin/stdout protocol
replacing SAM's WinForms UI, so the picker and the achievement list can be rendered by LuaTools itself.

Files deliberately left behind: `SAM.Picker` and `SAM.Game`'s WinForms UI (LuaTools has its own),
`GlobalSuppressions.cs` (ReSharper metadata), and `SAM.Game/Stats/**` (replaced by this project's own
model types).

If these files are ever edited, say so here and mark the edit in the file itself — the zlib license
requires altered versions to be plainly marked.
