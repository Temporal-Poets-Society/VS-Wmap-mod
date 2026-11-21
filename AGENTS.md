# World Map (`wmap`)

An atlas and minimap for cylindrical and Earth worlds: a nautical Chart or a Terrain style from the whole world to a
harbour, revealed by exploration, with far terrain on the horizon, winds through a Jonas barometer and shared paper
maps. Suite rules: `C:\VS mods\AGENTS.md`. Current state: the card `C:\VS mods\overview\mods\wmap.md`. Open issues:
`tools\Update-Backlog.ps1 -Mod wmap` from `C:\VS mods` (prefix WM). Decisions: `docs\decisions.md` (WM-Dnn, proposals
WM-Pnn); test results: `docs\testing.md`.

## Repository
- Trunk `main`, tracking the private `origin` (`andrisberzins-1/VS-Wmap-mod`). The public
  `Temporal-Poets-Society/VS-Wmap-mod` (remote `tps`) holds clean release snapshots only. Published versions are tags
  (`v0.5.0`, `v0.5.1`, `v0.5.3`, `v0.5.4`, `v0.5.5`); old lines are `archive/*` tags.
- 0.5.5 is released (tag `v0.5.5`, 21 November 2026: GitHub release on `tps`, installed in the author's game).
  Publishing, tags and pushes are the author's, through the release coordination.
- The GitHub wiki's source is `docs/wiki/` (one file per page, `_Sidebar.md`): edit it there; the coordination publishes it.

## Build
- First `.\tools\Prepare-References.ps1 -GamePath "$env:APPDATA\Vintagestory"`: it copies the game DLLs into
  `.local\game` (git-ignored), which the project references (`-p:GameReferences=<folder>` to override). Run it again
  after a game update.
- `dotnet build Wmap.csproj -c Release` gives `release\wmap_<version>.zip` (underscore) and **overwrites** a zip of
  the same version. The 0.4.x-0.5.1 zips there (some with `.sha256`, git-ignored) are the only local copies.

## Test
- `dotnet run --project tests\WmapTests\WmapTests.csproj -c Release` (core assertions; 108,876 at 0.5.4; a run lists every failed check with its line).
- `.\tools\Run-PlaytestChecks.ps1 -EarthRoot "C:\VS mods\Earth" -Output .local\playtest-after` (with
  `tests\PlaytestTests`).
- Smoke: `dotnet build tests\SmokeMod\SmokeMod.csproj -c Release`, then
  `.\tools\Run-Smoke.ps1 -Profile earth|cylinder|plain` (an isolated server with sibling mods from `..\`;
  `-SuiteRoot` from a worktree). It takes the newest `earth-*`, `hydrology-*`, `cylinderworld-*`, `sea-*` and
  `astraearth-*` zips from the mods' `release\` folders (AstraTerra's from `forks\AstraTerra\dist`) or the player's Mods
  folder (WM-0008, WM-0013); `-EarthZip`, `-HydrologyZip`, `-AstraZip` and the like pick others. Never install the
  smoke mod in a normal world.
- Far terrain's GPU time, offscreen (no game, no screen): build the mod, then `dotnet run --project tests\FarGpuBench -c
  Release -- --baseline <an assets\wmap folder of another version>` (`git archive <tag> assets/wmap` gives one): every
  pass timed at 1920 x 1080, view distance 384, and each way of drawing compared with the baseline's picture and depth
  pixel by pixel (WM-0054). Run it with no game running.
- In game: the AgentBridge bench; for Earth load the Earth, Cylinder World and Hydrology zips and an Earth play
  style. Seen on the bench with Earth 2.0 worlds (0.5.3 looks, WM-0020 to WM-0026); not verified yet: multiplayer, NVIDIA
  or Intel GPUs.

## Release
- Bump `version` in `modinfo.json`; the README's title line; the entry at the top of `CHANGELOG.md`; the release
  record in `docs\decisions.md` (and `docs\release-<version>.md` for a big one). Build, test, commit, tag `vX.Y.Z`.

## Gotchas
- Network protocol: channel `wmap-v2` (`src\Protocol.cs`). Append new message types and ProtoMember fields, never
  renumber; a protocol change means client and server need the same version (0.5.2 changed it).
- Predictions never generate chunks (WM-D03). Integrations with Earth, Cylinder World, Sea and ship mods are by
  reflection (WM-D04); Earth's terrain columns are read by reflection, not through a public API. A renamed geometry
  member stops the atlas with an error naming it; an unreadable terrain model falls back to observed terrain with an
  error in the server log (WM-0033).
- Ore heat maps hand Earth vanilla's simulated rock column where Earth keeps vanilla strata (WM-D51); `/wmapore`
  compares a cell with the pick where you stand.
- Recorded terrain wins over predictions (WM-D11, D29, D41).
- Survival clients get only the prediction of land they have seen (WM-D63): the server gates every tile and chart block
  (`RevealGate`, `ExplorationServer`). Anything new sent to clients must go through it, distances included (a value that
  says where something else lies). On the bench, `/bridge fly` switches the player to creative: check the survival rule
  as a spectator (it can fly and is not creative).
