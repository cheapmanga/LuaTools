<p align="center">
  <img height="336" alt="luatools" src="https://github.com/user-attachments/assets/54702ada-93a8-439b-ab3e-5cd73747ed46" />
</p>

# LuaTools
<p>
  <img align="right" height="250" src="https://github.com/user-attachments/assets/df083fb0-9be7-4690-9f0f-c8b0a73da881" />

  [Discord](https://discord.gg/luatools) • [Website](https://lua.tools) • [Git Mirror](https://git.lua.tools/luatools)
  
  A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 8.
    
  LuaTools browses and installs manifest sources, edits `stplug-in` lua files (depot pinning,
  per-depot enable/disable), manages unlocker modes, and injects a companion plugin into Steam's
  store pages.

  **This fork** adds two things: an **Achievements** page — pick a game from your Steam library and
  unlock or lock its achievements without leaving LuaTools — and a banner on the **Add** page that
  offers a game's fixes right after a Fetch. See [Achievements](#achievements) and
  [Fixes after a Fetch](#fixes-after-a-fetch) below.
  
  It ships fully translated in 29 languages and auto-updates via Velopack.
  <br><sub>Found a translation error? Tell us about it over on [Discord](https://discord.gg/luatools)</sub>
</p>

## Achievements

Pick a game in the grid, toggle achievements, and hit **Save to Steam**. Nothing leaves the machine
until you save, so you can flip a dozen rows and still back out with **Revert**. Server-awarded
achievements are shown read-only, because Steam ignores any attempt to change them.

Requirements: **Steam running and signed in**, and the game's achievements cached by Steam (they are
as soon as the game has been launched once on this machine).

Under the hood this is [gibbed's Steam Achievement Manager](https://github.com/gibbed/SteamAchievementManager)
interop, running in a small helper process (`LuaTools.SamHost.exe`) that ships next to `LuaTools.exe`.
It has to be a separate process: `steamclient.dll` is 32-bit and cannot be loaded into a 64-bit app,
and Steam binds one app id per connection. The helper targets .NET Framework 4.8, which is part of
Windows, so there is nothing extra to install.

## Fixes after a Fetch

Fetching a game on the **Add** page also checks whether it has published fixes. When it does, a banner
names them — *"This game has 1 fix(es) available: Online Fix."* — and its button opens the Fixes page
with that game already unfolded.

The kinds come from the listing's own tags, so an Online Fix is not announced as something else. The
lookup never holds up the Fetch, and when the listing cannot be reached there is simply no banner.

## Validator

The **Validator** page checks a game's Denuvo activation and repairs what it can. Pick the game from
your installed library rather than looking up an appid, tick *Lock the version* if you want the game
pinned to its installed build, and the run streams the script's own output into the page with a
progress bar. The D-Report code it ends on is one click from your clipboard.

The repair itself is `Devuvo.ps1`, this project's script, downloaded on every run — so fixes to the
checks arrive without an app update, and nothing here is frozen at build time.

It asks before it runs, and says why: the script uploads a machine report — MachineGuid, disk serial,
MAC addresses, public IP, hardware, the game folder's contents — to a paste service, and the D-Report
code **is that page's public address**. Anyone given the code can read the report. That was true of
the standalone tool too; here it is on screen before you start.

## Tokeer

The **Tokeer** page redeems a shared activation code: paste it, and the tickets it stands for are
written into Steam's credential store, including the owner SteamID that a stale value would otherwise
outrank. Then launch the game from Steam as usual. The only things that leave the machine are the code
and this machine's Windows MachineGuid, which is what binds a code to one PC.

Generating one works the other way round: pick a game you own, and the tickets Steam cached the last
time you launched it are sent to the code store, which verifies Valve's signature on the ownership
ticket before minting anything. Codes are single use.

Two things follow from reading the tickets out of Steam's own store rather than asking the live
session for fresh ones, as their app does with a helper published without source. A game that has
never been launched on this PC has nothing cached yet, and the page says so instead of failing
vaguely. And the account signed into Steam is checked against the ticket's owner: after redeeming
someone else's code that owner's ticket sits in your registry, and without that check anyone could
launder one shared code into many.

## Statistics
<div>
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Setup.exe?displayAssetName=true&style=for-the-badge" />
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Portable.zip?displayAssetName=true&style=for-the-badge" />
</div>

<a href="https://www.star-history.com/?repos=madoiscool%2Fluatools&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&theme=dark&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
 </picture>
</a>

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the released installer bundles a
  check for the .NET 8 **Desktop Runtime** and installs it if missing; [building from source](https://github.com/madoiscool/LuaTools/blob/main/CONTRIBUTING.md#building-from-source--developing) needs
  the full SDK

## Installation
You can find release builds on the [luatools website](https://lua.tools/app) or in the [releases](https://github.com/madoiscool/LuaTools/releases/latest) tab. 

## Credits / Adjacent software

- [Millennium](https://steambrew.app/): the Steam plugin framework whose injection API this app
  polyfills when Millennium isn't installed
- [Velopack](https://velopack.io/): installer and auto-update framework
- [Steam Achievement Manager](https://github.com/gibbed/SteamAchievementManager) by Rick (gibbed):
  the Steam achievement interop the Achievements page is built on. Its sources are vendored under
  `src/LuaTools.SamHost/Vendor/` under the zlib licence, unmodified; see the
  [notice](src/LuaTools.SamHost/Vendor/NOTICE.md) there
- [DepotDownloaderMod](https://github.com/SteamAutoCracks/DepotDownloaderMod): downloads depot content
  from Steam's CDN, powering the Depots page's Download action. A fork of
  [DepotDownloader](https://github.com/SteamRE/DepotDownloader), fetched and run as a standalone tool
- [SteamAutoCrack](https://github.com/SteamAutoCracks/Steam-auto-crack): fetched and launched from the
  Downloads page
- [TokeerDRM](https://github.com/Tesla697/TokeerDRM-App) by Tesla697: the activation-code scheme the
  Tokeer page redeems and generates against. The page talks to the same public code store their app
  does and reads and writes the same Steam registry values; it carries none of their code
- [LuaToolsValidator](https://github.com/Tesla697/LuaToolsValidator) by Tesla697: the front end whose
  job the Validator page does. Both run the same `Devuvo.ps1`, published by this project and fetched
  at run time, so neither one owns the repair logic
- [Steamless](https://github.com/atom0s/Steamless): removes SteamStub from game executables
- [CloudRedirect](https://github.com/Selectively11/CloudRedirect): Steam Cloud revival project, can be turned on via the mode page

## Licence

MIT. See [LICENSE](LICENSE).

The vendored Steam Achievement Manager sources under `src/LuaTools.SamHost/Vendor/` stay under their
own zlib licence, `src/LuaTools.SamHost/Vendor/LICENSE.txt`.
