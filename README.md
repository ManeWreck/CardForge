# CardForge

CardForge is an altered fork of Rick "gibbed" Gibbed's
[Steam Achievement Manager](https://github.com/gibbed/SteamAchievementManager).
It keeps the original SAM workflow available, but rebuilds the picker around a
more practical Steam library interface with playtime, achievement, and card-drop
helpers.

The goal is simple: keep SAM lightweight and portable, while making day-to-day
library management less awkward. CardForge adds a darker interface, faster image
loading, searchable views, card-drop scanning, and tray controls for opened game
windows.

CardForge is not affiliated with Valve, Steam, or the original SAM author. It is
an altered source version and preserves the original zlib license notice.

## Preview

The screenshots below use mock library data. They do not show a real Steam
account, cookies, session data, or a personal game library.

![CardForge library overview](docs/images/cardforge-library-overview.png)

![CardForge card table view](docs/images/cardforge-card-table.png)

## Highlights

- Modern dark CardForge styling for both the picker and game manager windows.
- Larger Steam library picker with a wider search box.
- Search by game name, AppID, app type, Steam store URL, or `steam://run/` URL.
- Tiles, list, and sortable table library modes.
- Game Hub panel with quick actions for SAM, store pages, card pages, Steam
  pages, and AppID copying.
- Local playtime display from Steam's local user cache when available.
- Local achievement total display from Steam's cached stats schema when
  available.
- Embedded Steam badge scanner for remaining card drops.
- Cards Only filter for games with remaining card drops.
- Favorite games filter and local favorites list.
- Scheduler for timed launch, close, restart, queue rotation, and card-aware
  relaunching.
- Bulk launch and close helpers for games that still have card drops.
- Tray menu for hiding CardForge, refreshing data, and managing opened game
  windows.
- Cached Steam capsule images for faster startup after the first load.
- `CardForge.exe` is generated beside `SAM.Picker.exe` for a cleaner launch
  name.

## Card Drops

CardForge can open an embedded Steam badge viewer and scan the visible badge
pages for remaining card drops. This keeps the scan tied to the Steam account
that is currently signed in inside the embedded viewer.

The scanner is intentionally local and user-driven:

- it reads Steam community badge pages that the signed-in user can already see;
- it stores browser state only in the local `%LOCALAPPDATA%\CardForge\WebView2`
  profile so the Steam login can survive app restarts;
- WebView2 profile folders are ignored by Git and are not included in releases;
- the release archive does not include cookies, local storage, sessions, or
  account-specific cache.

## Scheduler

The Scheduler window can build a timed run list from selected games, favorites,
or games with card drops remaining.

Supported behavior:

- launch one or more games for a chosen number of minutes;
- close and relaunch the same batch;
- rotate through a queue such as A, B, C;
- limit the maximum number of simultaneously opened games;
- refresh card-drop data between cycles;
- in Card-aware mode, keep games that still need cards and skip games that no
  longer have drops remaining.

Example setups:

- A every 15 minutes: add A, set 15 minutes, max 1, Repeat on, Rotate off.
- A+B+C every 30 minutes: add A, B, C, set 30 minutes, max 3, Repeat on.
- Rotate A then B then C hourly: add A, B, C, set 60 minutes, max 1, Rotate on.
- Card farming queue: add favorites or Cards Only games, enable Card-aware and
  Refresh drops each cycle, then set the maximum simultaneous games.

## Current Status

CardForge is a preview fork. The main workflow works, but there are still UI
rough edges and unfinished areas. Known areas that need more polish:

- list mode layout;
- better filtering before scanning card pages;
- a cleaner single-window/tabbed manager for many opened SAM.Game windows;
- more complete metadata refresh behavior.

## Download

Use the latest preview release from this repository:

[Download CardForge preview](https://github.com/ManeWreck/CardForge/releases/latest)

The release archive is portable. Extract it anywhere and run `CardForge.exe`.
`SAM.Picker.exe` remains available for compatibility. Steam must be running, and
you must be logged in to Steam.

## Build

Requirements:

- Windows;
- Steam client installed;
- .NET SDK with .NET Framework 4.8 targeting support;
- WebView2 Runtime for the embedded Steam badge viewer.

Build x86:

```powershell
dotnet build SAM.sln -p:Platform=x86
```

Build release package output:

```powershell
dotnet build SAM.sln -c Release -p:Platform=x86
```

Debug output is written to `bin/`. Release output is written to `upload/`.

## Privacy Notes

Do not commit or publish runtime state. The repository ignores these files by
default:

- `bin/`;
- `upload/`;
- `obj/`;
- `*.WebView2/`;
- `EBWebView/`;
- generated `CardForge-*.zip` archives.

Do not publish local WebView2 profile folders created while signing in to Steam
through the embedded viewer. They are intentionally kept on the user's machine
so the embedded Steam account does not reset on every CardForge restart.

## Original Project

The original Steam Achievement Manager code was released by Rick Gibbed and is
available at [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager).

The closed-source version originally released in 2008, had its last major
release in 2011, and received a hotfix in 2013. The open-source SAM 7.0.x code
included general maintenance, Fugue icon replacements, and version updates.

## Attribution

Most original SAM icons are from the
[Fugue Icons](https://p.yusukekamiyamane.com/) set.

CardForge-specific changes are maintained in this fork. The original license
notice remains in [LICENSE.txt](LICENSE.txt).
