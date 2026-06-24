# CardForge fork notes

This repository is an altered source version of Rick "gibbed" Gibbed's Steam
Achievement Manager. The original project and zlib license notice are preserved.

## Direction

CardForge keeps the existing SAM behavior available while turning the picker into
a more comfortable Steam library and card-drop dashboard.

Current changes:

- dark CardForge styling for the picker and game manager;
- app icon replacement for picker and game windows;
- cached Steam capsule images;
- library search by name, AppID, and type;
- tiles, list, and sortable table view modes;
- local playtime and achievement-count display where Steam cache data exists;
- embedded Steam badge scanner for remaining card drops;
- cards-only filter and launch/close helpers for card-drop games;
- tray controls for the app and opened game windows;
- game hub actions for SAM, store, card pages, Steam pages, and AppID copy.

Privacy notes:

- WebView2 profile folders are runtime state and must never be committed or
  included in release archives.
- `LogoCache` contains generated game images only; it is also treated as runtime
  cache and is not part of source control.
- Release archives should be made from a clean build output after deleting any
  `*.WebView2` folders.

Ideas for later:

- friendlier list mode layout;
- better card-drop scan filters for unfinished badge pages only;
- saved view presets for card farming, unfinished achievements, and high playtime
  games;
- a compact single-window tab manager for opened SAM.Game instances.
