# Wmap validation

Updated 2026-10-16. Test environment: Windows 11, Vintage Story 1.22.7, .NET SDK 8.0.419 and runtime 10.0, AMD Radeon RX 6600, 16-core/32-thread CPU.

## 0.5.2: grid labels in the readout's format, installed

12 November 2026, same worktree. The author's screenshot (St Lawrence Island, 1:50 world, 0.5 blocks per pixel, decimal degrees chosen): "map longitude numbers does not match what hud coordinates are showing". Measured on the screenshot, the player's arrow lies 2.9′ west of the 169°30′W line, at about 169.452° W; the readouts said 169.4513° W, so the lines and readouts agreed. The labels were always in degrees and minutes (169°45′W, 169°30′W, 169°15′W), whatever the ° / °′ button said, and 169°45′W read like the readout's 169.45.

* Fix: `GeoFormat.GridLabel` labels the atlas's meridians and parallels in the readout's format: 169.75°W, 169.5°W, 169.25°W with decimal degrees, 169°45′W with minutes, whole degrees alike in both. The labels are worked out again when the button changes.
* Core suite: **108773 assertions PASS** (5 new label checks: minutes, decimals, trimmed decimals, whole degrees and the equator, minute carry).
* Release build: 0 warnings, 0 errors. `release/wmap_0.5.2.zip` rebuilt (SHA-256 `db7468c9fe2114cd79371ea0531bf4d316566beae70495e2455747b1cc31decb`); **installed** on 12 November at 09:59 Europe/Riga at the author's OK ("Yes, install"), with the game closed; the installed file's hash was checked. The previous installed build (`147bb55b…`, below) is in `VintagestoryData/Backups/wmap-before-0.5.2-gridlabels-20261112-095919`.
* Not checked: in game; the smoke run (it does not read the labels).

## 0.5.2: ore heat maps in stored tiles (WM-D48), installed

6 November 2026, same worktree. The author reported that the map "only renders once selected in given zoom, then it does not update" and wanted maps worked out ahead and stored. [WM-D48](decisions.md#wm-d48-ore-heat-maps-in-stored-tiles-at-every-zoom-052-6-november-2026) has the design.

* Release build: 0 warnings, 0 errors. `release/wmap_0.5.2.zip` is rebuilt (SHA-256 `147bb55b95d6c80f71ddc5a44dca8a057adcbd9366505c0288befa42936d3767`) and was **installed** on 6 November at 12:43 Europe/Riga, after the author's OK and once the game had closed; the installed file's hash was checked. The earlier joined build (`84b0488d…`) is kept in `VintagestoryData/Backups/wmap-before-0.5.2-tiles-20261106-124313`. The smoke mod builds.
* Core suite: **108768 assertions PASS**. The grid checks were replaced by tile checks: level choice, the view's tiles on every copy of the ring, cell lookup and samples at chunk centres, no samples past the ring's end or the south edge, the store-ahead levels and budget, stand-ins from 1 to 7 levels up, and the stored tile's round trip (half floats, NaN kept, counts capped, the listed count, damaged or foreign files refused).
* Isolated dedicated servers (VS 1.22.7, below-normal priority, beside the running game), with a scratch mod running `OreHeatChecks`, `DialogChecks`, timings and the ore audit's sites:
  * Earth 1.0.3-tall.9 at 1:10 on Earth's built-in heights (43 ores by reading): **PASS**. `OreHeatChecks` covers: a tile of chunk cells answered everywhere, stored and read back by a new server without being worked out again; the whole world in one tile; listed = one by one; refusal; a newer request replacing the tiles of an older one; and the store-ahead running. `DialogChecks` covers: one request after the view settles, a coarse tile first, then the view's six; the hover line; a missing tile drawn from the coarse one; no request on a small pan; a new request on zooming out (level 3 to 7) and on switching ores.
  * Earth timings: a tile with every ore 47–61 ms for levels 3 to 9 (176 ms for the whole-world tile); two more ores of a tile already worked out, 0 ms. The author's Europe view (20 W to 35 E, 35 to 62 N; 309 blocks per pixel in this world) needs 18 tiles of 4,096-block cells: 1.2 s in all.
  * Store-ahead: **667 tiles (levels 15 to 10) in 53 s** on two threads, 23.4 MiB with the other tiles of the run (838 files).
  * Vanilla 1,024,000 × 256 (31 ores by ore map): **PASS**. No tile is stored where a region is missing, and nothing is stored ahead.
  * At the 152 Earth and 160 vanilla audit sites, the values equal the earlier grid build's within half-float rounding (largest 0.8 % on a factor of 0.012). The listed counts are identical.
* Not checked: in game (the overlay, the stand-ins as drawn, the status line); multiplayer; an Earth world on the GLO-90 store, which the author's world may use (a tile then samples GLO-90 heights, and its time is not known). Not committed.

## 0.5.2: joined test build (climate raster, rivers, ore heat maps), installed

5 November 2026, `claude/climate-builtin-heights`. The author asked to join this branch's work with `claude/ore-heatmaps` (0.5.2-ore.1) and install one version. Both branches started from 0.5.1 (`f60ce3d`) with uncommitted work only, so the ore worktree's changes were brought in file by file, three-way against 0.5.1. Nothing is committed; the ore worktree is unchanged.

* Conflicts were limited to the version (now 0.5.2 for both), the changelog, these two documents and the core suite's closing lines; all were kept from both sides. In code, only `AtlasServer` was changed by both branches, in separate places, and it merged without conflict.
* `tools/Run-PlaytestChecks.ps1 -EarthRoot 'C:\VS mods\Earth\.claude\worktrees\mountain-height' -Output .local/playtest-merged`: **PASS**. Release build 0 warnings, 0 errors, `release/wmap_0.5.2.zip`. Core suite **108250 assertions**: 0.5.1's 107404, plus 9 for the climate raster and 837 for the ore heat maps, as the two branches counted separately. Surface rules, recorded-precedence, river-channel and zoom-detail GPU checks all pass. Log: `.local/playtest-merged.txt`.
* `tests/SmokeMod` builds (7 known CA1416 warnings). Its new ore checks were not rerun on the joined build; they passed on isolated dedicated servers on the ore branch (below).
* **Installed** into `%APPDATA%\VintagestoryData\Mods` as `wmap_0.5.2.zip` (identical to the build) with the game closed. `wmap_0.5.1.zip` was moved to `ModsBackup`. The ore heat maps change the network protocol, so a server or client still on 0.5.1 does not match.
* Not checked in game yet.

## 0.5.2: detail tiles draw rivers from their own water

5 November 2026, same branch. [WM-D46](decisions.md#wm-d46-detail-tiles-draw-rivers-from-their-own-water-052-5-november-2026) has the cause and the evidence. The trigger was the author's Niagara screenshot at 1:10.

* `tools/Run-PlaytestChecks.ps1 -EarthRoot 'C:\VS mods\Earth\.claude\worktrees\mountain-height' -Output .local/playtest-river-channel`: **PASS**. That covers the Release build (0 warnings, 0 errors; `release/wmap_0.5.2.zip` rebuilt with the fix), the core suite (107413 assertions), surface rules, the recorded-precedence and zoom-detail GPU renders, and the new **river channel** GPU check. Log: `.local/playtest-river-channel.txt`. Game references were copied into this worktree's `.local/game` from `codex-world-options`.
* River channel check: a 30-block river runs through a column of 512-block chart texel centres, drawn with 2- and 4-block tiles at 3.7 blocks per pixel. River-blue land pixels more than 25 blocks out:

  | Shader | Chart style | Terrain style |
  |---|---|---|
  | 0.5.2 before this change (`--chart-wind`, baseline mode) | 20,499 | 27,000 |
  | fixed | 0 | 0 |

  The limit is 50. Renders are in `.local/river-channel-before` and `.local/river-channel-after`.
* Offline renders of the reported Niagara view used Earth 1.0.3-tall.8 at the author's geometry and the production shader; the harness is in the session scratchpad. Only the lens round the lower Niagara changed (about 16,000 px in each style). Renders of the chart alone are identical, and the Lake Erie patch is the chart where tiles were still pending.
* Not run: the in-game smoke profiles, including the one-block `river-terrain` GPU check. Not checked in game; not installed or committed.

## 0.5.2: climate raster on Earth's built-in heights

5 November 2026, `claude/climate-builtin-heights` (from `release/0.5.0` at 0.5.1). [WM-D45](decisions.md#wm-d45-the-climate-raster-on-earths-built-in-heights-052-5-november-2026) has the cause, the decision and the timing table.

* Release build: 0 warnings, 0 errors; `release/wmap_0.5.2.zip` (references from the release worktree's `.local/game`).
* Core suite: **107413 assertions PASS** (0.5.1: 107404), with the new switch checks against a stand-in Earth type.
* Offline timing on Earth 1.0.3-tall.8 with the whole GLO-90 store, 1024 x 512, 16 threads, below-normal priority beside the running game: 6.8 s warm and 22.1 s cold with the switch, 0 tiles; 87.9 s and 70,817 tiles without it. Forest differs in 1.5 % of texels, temperature and rain nowhere.
* Not run: smoke profiles and GPU checks (no rendering or protocol change). Not checked on a dedicated server or in game; not installed.

## 0.5.2-ore.1: ore heat maps

5 November 2026, `claude/ore-heatmaps` from 0.5.1. [WM-D47](decisions.md#wm-d47-ore-heat-maps-on-the-creative-map-052-ore1-5-november-2026) records the design and the numbers.

* Release build: 0 warnings, 0 errors; `release/wmap_0.5.2-ore.1.zip`. Smoke mod: 7 CA1416 warnings.
* Core suite: **108241 assertions PASS** (ore heat grids, request validation, colours and the pick's words added).
* New smoke checks, `OreHeatChecks` (server) and `DialogChecks.CheckOreHeat` (menu, settling, hover). They ran on isolated dedicated servers through a scratch mod, not through `Run-Smoke.ps1`: Earth 1.0.3-tall.9 at 5,009,920 × 1024 with regional ores, and vanilla at 1,024,000 × 256. Both PASS, with heat values compared at 152 and 160 sites against block-by-block ore counts.
* Not run: the full `Run-Smoke.ps1` profiles (Earth 1.0.3 needs data packages the runner does not copy). Not checked in game: no client was launched.

## 0.5.0-pre.13: zoom detail over recorded ground (#36)

31 October 2026, `claude/wmap-zoom-detail-caa73b`. [WM-D41](decisions.md#wm-d41-play-test-36-zoom-detail-over-recorded-ground-050-pre13-31-october-2026) records the cause and evidence.

* Release build: 0 warnings, 0 errors; `release/wmap_0.5.0-pre.13.zip`. Smoke mod: 7 CA1416 warnings.
* Core suite: **107199 assertions PASS**.
* `tools/Run-PlaytestChecks.ps1 -EarthRoot <Earth codex-playtest-issues, 0.17.0-pre.11> -Output .local/playtest-36`: PASS, including the new St Lawrence Island zoom renders at 0.3/1/2 blocks per pixel (recorded pixels unlike the one-block prediction, terrain style: pre.12 order 53.4/43.3/27.3 %, pre.13 0.0/0.0/0.4 %; contrary recorded lake kept 100 %) and #22c's recorded-precedence renders through the new path. Log `.local/playtest-36.txt`.
* Full dedicated Earth smoke, `Run-Smoke.ps1 -Profile earth -OverviewWidth 8192 -PolarMode earth -MapHeight 512 -VerticalBudget 110 -Wait` with Earth 0.17.0-pre.11, Cylinder World 0.11.15-playtest.5, Hydrology 0.1.12 (explicit zips), Sea 0.4.6 and AstraTerra 0.15.0-dev.1-earthtest (runner defaults): **PASS, 0 FAIL** (`.local/smoke-earth-20261031-141957`). The first run failed only on the rewritten `DetailZoomChecks` assertion, which expected incoming one-block predictions that the check never uploads; the assertion was corrected (`.local/smoke-earth-20261031-141720`).
* Not checked in game: no client was launched.

## 0.5.0-pre.12: play-test columns and glacier surfaces

30 October 2026, `codex/playtest-issues-2026-10-30`. [WM-D40](decisions.md#wm-d40-play-test-19-and-22-surface-recording-050-pre12-30-october-2026) records causes and before/after evidence. Latest assembly-free core suite: **107193 assertions PASS**. #22c now has a reproduced Wmap cause and fix: finer predictions, zoom fades and overview selection could hide existing observations. Recorded dry land/water/ice now overrides predictions at every zoom, including cached fallbacks. See `.local/recorded-precedence-before.txt` (fails) and `.local/recorded-precedence-after.txt` (passes).

Earlier in this task, adapter/recorder/box-worker and offscreen chart/Terrain checks passed against pre.8 data in both ring geometries, and Release built without warnings. Those logs/renders predate the final precedence changes. **The final source still needs the coordinator's Release build, new GPU precedence renders and dedicated smoke checks.** The user instructed this sandbox not to access game assemblies or Git metadata for writes; only the assembly-free core suite was run after that instruction. The old ZIP is not a final build. The coordinator should run the following sequentially with access to the usual references:

With game references prepared in `.local/game`, reproduce from this worktree:

```powershell
& tools/Run-PlaytestChecks.ps1 -EarthRoot 'C:\VS mods\Earth\.claude\worktrees\mountain-height' -Output .local/playtest-after
& tools/Run-PlaytestChecks.ps1 -EarthRoot 'C:\VS mods\Earth\.claude\worktrees\mountain-height' -Output .local/playtest-ring409600 -RingWidth 409600
```

`EarthRoot` is read-only: only its Core sources/data are read; its project is never built. The runner keeps SDK state inside this worktree and uses no package feeds or game client. `-BaselineAssembly <pre.11 Wmap.dll>` runs the same diagnostic renders against the earlier production assembly, reports the expected failures and restores the current test assembly afterward. The baseline DLL used here was built from the initial clean branch before edits and retained at `.local/baseline/Wmap.dll`.

The final runner also invokes `MapGpu.RecordedPrecedence`, generating `recorded-river-*.png` for old dry chunks versus a new predicted small river, actual lake/glacier over predictions, requested/cached observations, zoom fades and the untouched wide eastern river. The full smoke's detail-zoom check verifies the actual atlas fallback path. These new GPU checks are added but pending coordinator execution; do not describe the earlier renders as a pass for them. Commit message for the uncommitted changes: `.local/commit-message.txt`.

The smoke mod also builds (seven CA1416 platform warnings). A full dedicated Earth smoke attempt with explicit Earth 0.17.0-pre.8, Hydrology 0.1.11, Cworld playtest.4, Sea pre.7 and AstraTerra dev.21 packages stopped at **sandbox access denied launching VintagestoryServer.exe**. Its prepared, isolated data folder is `.local/smoke-earth-20261030-194258`; `.local/smoke-attempt.txt` records the failure. No server or client started. Generated chunks, far-render handoffs and the full smoke matrix remain pending. `Run-Smoke.ps1` now supports worktrees through `-SuiteRoot`, explicit package paths, world dimensions/vertical budget, and `-Wait -TimeoutSeconds 900`; the smoke mod's shutdown callback and the runner's timeout bound its lifetime.

## 0.5.0-pre.11: heights over the whole world, the far sea off steep coasts

Updated 2026-10-27. Branch `fix/height-range-2026-10-27`. **Core**: 107,167 assertions pass (new: the range for 256-, 0- and 512-block worlds; a 512-block world's chart, prediction tile and line-of-sight grid keep a plateau at 300 and a peak at 380 above the sea, which the old range cut at 256; a standard world's bytes unchanged). **Build**: Release, 0 warnings. **GPU**: Sea 0.4.7-pre.7's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: ALL OK. **Smoke, Earth profile** (`-OverviewWidth 8192 -PolarMode earth` equivalent, run from this worktree; Sea 0.4.7-pre.7, AstraTerra 0.15.0-dev.21, Cylinder World 0.11.15-playtest.4 since playtest.3's package is gone; the user's game was running during the final pair):
- **512-block world, sea 110, Earth Map 0.17.0-pre.3** (`earthVerticalBudget` 110; Everest at y=480; range 403): [PASS](test-results/earth512-0.5.0-pre.11.txt). The chart reaches 354 blocks above the sea, 1,891,338 texels above the old 256 cut. Far sea 119,143 pixels, the highest 2.9 blocks; Baikal at its surface; the torch at 2900 blocks in sight over the curve (the sight line clears the curved sea by 5 blocks; 128/255, as on a flat planet).
- **384-block world, Earth Map 0.16.14-rivers.2** (sea 165, range 256): [PASS](test-results/earth-0.5.0-pre.11.txt). Far sea 119,432 pixels, the highest 2.9; the torch at 2900 hidden behind the curve (0/255; 110/255 on a flat planet).
- **Before the shore fix** the 512-block world failed the shore check: the far sea climbed the coast in 11 of 119,157 pixels, up to 8.7 blocks; pre.10 [fails it the same way](test-results/earth512-0.5.0-pre.10.txt) (9 of 119,161, up to 7.9). The pixels lay on land 10 to 24 blocks above the sea by Earth's own samples, next to the filtered coastline. Before the smoke's own far frames took the range, Baikal read 16 to 56 blocks under its surface there; with the old fixed check, the torch at 2900 failed (128/255) though the geometry leaves it in sight.
- **Tile client allocation** (limit 16,384 bytes a tick): 4,441 and 8,422 in the final pair.

## 0.5.0-pre.10: river lines on their valley floor, the terraces' edge closed

Updated 2026-10-27. Branch `fix/far-sea-coast-2026-10-26`. **Core**: 107,160 assertions pass. **Build**: Release, 0 warnings. **GPU**: Sea 0.4.7-pre.7's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: ALL OK. **Smoke, Earth profile** (`-OverviewWidth 8192 -PolarMode earth` equivalent, run from this worktree with the sibling mods named explicitly; Earth Map 0.16.14-rivers.2, Cylinder World 0.11.15-playtest.3, Sea 0.4.7-pre.7, AstraTerra 0.15.0-dev.21): [PASS](test-results/earth-0.5.0-pre.10.txt), with no other game or server running.
- **Near band, water at its level** (changed check): the far sea 119,434 pixels, the highest 2.9 blocks above the sea; 6 river-line pixels more than 3 blocks up, at most 5.9 blocks above the valley floor. pre.9's shader [fails](test-results/shore-before-0.5.0-pre.10.txt): 135 of 143 river-line pixels more than 6 blocks above the valley floor, up to 57.3 (and, judged as sea before, 145 of 119,580 up to 99.5).
- **Mid-distance view, sky after a view-distance change**: 0 undrawn sea pixels at every step (pre.9: 2 of 43,249 at 256, at the terraces' 768-block edge; also present at 512 but between the check's sampled pixels).
- **Tile client allocation under the recorder** (limit 16,384 bytes a tick): timing-dependent, not from this change, which does not touch the tile client. The check measures the calling thread while snapshots go to the save worker; a snapshot not yet back takes a new tile copy. Mid-distance-only runs: pre.9 628 and 3,814; this branch 19,797 and 45,048 (the user's game and a Hansa test server running), then 4,580, 2,982 (nothing else running) and 13,704 in the full run. Recorded, not fixed here.

## 0.5.0-pre.8: the far sea's glitter path

Updated 2026-10-26. **Core**: 107,160 assertions pass (the change is in the far shaders). **GPU**: Sea 0.4.7-pre.7's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: `farterrain` and `farcells` compile and link (low and high settings) with `seaGlint`, `seaSun`, `seaSunColor` and `seaWeather` live; `wmapSeaGlints`, taken from this branch's include, equals Sea's `seaGlints` for the same rays, roughness and uniforms (largest difference 0.000000). **Not run**: the smoke profiles. Not seen in game: the glitter path across the near/far hand-off and on to the horizon.

## 0.5.0-pre.9: pre.8 (far glint) and pre.7-seam.1 (seam graticule) merged

Updated 2026-10-26. Branch `playtest/2026-10-26`. **Core**: 107,160 assertions pass. **Build**: Release, 0 warnings. **GPU**: Sea 0.4.7-pre.7's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: ALL OK. **Smoke, Earth profile** (`-OverviewWidth 8192 -PolarMode earth` equivalent, run from this project's `.local`; Earth Map 0.16.14-rivers.2, Cylinder World 0.11.15-playtest.3, Sea 0.4.7-pre.7, AstraTerra 0.15.0-dev.21): every section up to the far-terrain GPU checks passes, including the new `SeamGraticule` check (renders `gpu-seam-graticule*.png`); then **FAIL** "Near band: the far sea climbs the coast: 145 of 119580 sea pixels are drawn more than 3 blocks above the sea (up to 99.5)". **Not caused by this merge:** pre.7 with its own smoke mod and the same mods fails identically (145 of 119580); pre.9 with Earth 0.16.14-playtest.1 (the gutter layout) fails the same way (140 of 119253); pre.5 now stops earlier, at the mid-distance view's view-distance gap (2 of 43249 sea pixels; pre.7's horizon skirt removed that). The pixels are a few water-coloured specks inside the coastal hills and along the shoreline in `far-band-shore.png` (inland or shoreline water drawn above sea level), not at the seam. Open for a follow-up. Not seen in game.

## 0.5.0-pre.7: pre.6 with the horizon-gap fix

Updated 2026-10-26. Branch `playtest/2026-10-26` (pre.6 + `fix/far-land-horizon-gap`). **Core**: 107,160 assertions pass. **Build**: Release, 0 warnings. **GPU**: Sea 0.4.7-pre.6's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: ALL OK (`farterrain` and `farcells` compile and link with the skirt and the pre.6 uniforms). **Not run**: the smoke profiles (`Run-Smoke.ps1` finds its sibling mods through `..\`, which a worktree breaks). Not seen in game: the row of sky under distant coasts (issue 6), and pre.6's items.

## 0.5.0-pre.6: sunset glow and the near/far wave scale

Updated 2026-10-25. **Core**: 107,160 assertions pass (unchanged; the change is in the far shaders). **GPU**: Sea 0.4.7-pre.5's `tests/ShaderCheck` with `WMAP_ROOT` on this branch: `farterrain` and `farcells` compile and link (low and high settings) with `seaNightFloor`, `seaSkyReflect`, `seaWaveCount`, `seaWave`, `seaWaveFactor` and `seaSunset` live. **Not run**: the Earth, Cylinder World and plain smoke profiles (the mid-distance check's far-sea colour-step test was not repeated). Not seen in game: the seam with Sea's near water, the glow's look, and far land's new shadow term and lighter haze against the game's own terrain where far terrain takes over.

## 0.5.0-pre.5: play-test build (pre.4 with the review fixes)

**Core**: 107,160 assertions pass. **Smoke**: [Earth](test-results/earth-0.5.0-pre.5.txt) (`-OverviewWidth 8192 -PolarMode earth`; Earth Map 0.16.10, Cylinder World 0.11.15-playtest.1, Sea 0.4.7-pre.2) passes except the known Antarctic-peninsula column (predicted ice y 290, generated 280, as in pre.4). The first run of the merge failed the mid-distance check at 29,899 bytes per tile-client tick: the review's lazy colour array moved each new recording's 67 KB colour allocation to the main thread. With the worker making a new recording's colours again it passes at 628 bytes a tick (pre.4: 1,594). Not run: the Cylinder-only and plain profiles.

## 0.5.0-pre.4: second review round and the play profile

Updated 2026-10-24 ([what and why](mid-distance-view.md#review-of-050-pre3-and-its-play-profile-and-050-pre4)). **Core suite**: **PASS, 107,148 assertions**; new: the column mask is made again at once for a column's move, a seam jump of one lap and a change of start, and after a teleport only a new mask fills the missing column; a raw colour in a marked tile (a rollback) gives no colour and no light, and over all 256 top bytes only 0x80 to 0x9F count. With 0.5.0-pre.3's `RecordedColor.Has` put back the suite fails at the rollback check.

**Smoke**: [Earth](test-results/earth-0.5.0-pre.4.txt) (`-OverviewWidth 8192 -PolarMode earth`; Earth Map 0.16.8, Cylinder World 0.11.14, Sea 0.4.6) passes except the known Antarctic-peninsula column (predicted ice y 290, generated 280, as in pre.3); [Cylinder World only](test-results/cylinder-0.5.0-pre.4.txt) and [plain](test-results/plain-0.5.0-pre.4.txt) pass. New in the mid-distance check: the texture units of each program as the game numbers them (farterrain 4 game and 10 Wmap samplers, farcells 4 and 12, farcellmap 0 and 9, farlights none: no unit shared; with every game sampler and one unknown added none shared, where 0.5.0-pre.3's units share unit 4 between liquidDepth and the column mask in farterrain and farcells, and unit 15 or 14 with the unknown one); a new recording every frame for 2.3 s works the windows out 3 times (pre.3: 150); rebuilding the boxes and lights allocates 0 bytes; a walking frame 1,552 bytes, standing 160. GPU at 600 blocks 0.64 ms, 0.67 with Sea's waves (unchanged). With 0.5.0-pre.3's behaviour put back, mid-distance check only, one change set per run: the five known names pinned and Wmap's units fixed fails at farterrain's units (unit 4: wmapLoaded and liquidDepth; unit 15: wmapChart and the unknown sampler); a new box and light array each rebuild fails at 784 bytes per rebuild (36 boxes, 1 light in the test scene); every new recording reworking the windows fails at 150 reselections in 2.4 s (at most 4).

**Play profile of 0.5.0-pre.3** (the user's, Devkit, against 0.4.11 in the same place): `FarTerrain.OnRenderFrame` 71.7 µs mean (p99 0.215 ms, max 27.88 ms), 6,500 B per call (0.4.11: 20.1 µs, 2,947 B); Wmap's client tick listener 577.7 µs mean (p99 2.435 ms, max 23.52 ms), 135,132 B per call (0.4.11: 376.2 µs, 124,870 B); one 623 ms frame in Sea's shader compile. 0.5.0-pre.4 has not been profiled in play yet.

## 0.5.0-pre.3: review fixes

Updated 2026-10-24 ([what and why](mid-distance-view.md#review-of-050-pre2-and-050-pre3)). **Core suite**: **PASS, 107,138 assertions**; new: older recordings' colours (0xFF in the top byte) give no light and no colour, marking clears them and keeps the terrain, the mark survives the codec as an unknown cell, and paper maps keep each tile's colour format in both directions.

**Smoke**: [Earth](test-results/earth-0.5.0-pre.3.txt) (`-OverviewWidth 8192 -PolarMode earth`; Earth Map 0.16.5, Cylinder World 0.11.14, Sea 0.4.6) passes except the known Antarctic-peninsula column; [Cylinder World only](test-results/cylinder-0.5.0-pre.3.txt) and [plain](test-results/plain-0.5.0-pre.3.txt) pass. The mid-distance check adds six review checks (one missing column filled and nothing else covered; a recording appearing with the camera still; at most 4 copies a frame among 60 changing recordings; the cell map under a bound unpack buffer; the status line without allocations; a recorded pillar across Cylinder World's seam from both laps). With 0.5.0-pre.2's behaviour put back (one build, four changes) the four that compare against it fail: the window stays at 0 tiles, 60 copies in one frame, an incomplete framebuffer, 272,000 bytes over 1000 frames; the missing-column check measures pre.2's single radius at 17,970 pixels over loaded columns. The view-distance check now counts a gap only where far terrain should draw by the column mask.

## 0.5.0-pre.2: after the first in-game test

Updated 2026-10-23 ([findings and fixes](mid-distance-view.md#in-game-test-of-050-pre1-and-050-pre2)). **Core suite**: **PASS, 107,131 assertions**; new: far terrain's start (0.75 of the view distance, at least 48, or the first chunk column not loaded; columns off the world count as loaded).

**Smoke**: [Earth](test-results/earth-0.5.0-pre.2.txt) (`-OverviewWidth 8192 -PolarMode earth`; Earth Map 0.16.5, Cylinder World 0.11.14, Sea 0.4.6) passes except the known Antarctic-peninsula column; [Cylinder World only](test-results/cylinder-0.5.0-pre.2.txt) and [plain](test-results/plain-0.5.0-pre.2.txt) pass. The far-terrain GPU timing now draws 60 times before it measures (0.35 ms with the band, 0.14 chart only, 0.12 for 0.3.10's mesh). The mid-distance check adds:
- **Sea's far water**: `seaNightFloor`, `seaSkyReflect` and `seaWaveCount` are live in `farterrain` and `farcells`; Sea 0.4.6's own text patch, called by reflection in the smoke world, leaves the shader alone. With its reflection on, the far sea toward open water has no step in colour between rows from the real water's end to 1400 blocks, across the terraces' edge (at most 18 of about 70,000 pairs over 8/255, at the coast's edge).
- **Changing the view distance**: 512 → 700 (chunk columns loaded to 400 blocks) → 512 → 256 → 512 over open sea, by the production start rule and the cell map's redraw decision: no undrawn sea pixels between where the real water ends and 760 blocks, and the last 512 equals the first. With 0.5.0-pre.1's rule the 700 step leaves 6,113 of 13,527 undrawn (tried).
- **Allocations** on the main thread: 0 bytes per recorded sample (the recorder's classification and light on the generated chunks), 824 per far terrain frame, 0.8 to 2.4 KB per tile client tick under a stream of new recordings ([mid-distance only, full chart](test-results/mid-distance-0.5.0-pre.2.txt)). Limits in the check: 1 KB a frame, 16 KB a tick.
- **GPU** with Sea's reflection and 24 wave trains: 0.66 ms for all far terrain in the timing view.

**Sea** (branch `wmap-far-waves`, 0.4.7-pre.1): `tests/SeaTests` ALL OK; `tests/ShaderCheck` and `tests/SkyIntegrationCheck` ALL OK against Wmap 0.4.11 (main) and this branch (`WMAP_ROOT`); `SkyIntegrationCheck --vanilla` OK.

Not verified here: the far sea's colour against Sea's real water in game (calibrated from two screenshots of one place by day), the waves on the far sea in motion, the view filling in while columns load in a running client, and the tick listener's time and worst calls in a new profile.

## 0.5.0-pre.1: mid-distance view prototype

Updated 2026-10-23 (WM-P06, branch `claude/mid-distance-view-41ba01`, not merged; [design and results](mid-distance-view.md#implemented-prototype)).

**Core suite**: **PASS, 107,127 assertions**. New: the colour and light packed into a recording's colour field, and which changes count (a first colour, a light coming on or going out; not a colour alone); recorded texels (top face for land, surface for water, coverage, colour known, neighbours filled without coverage); which height the smooth mesh and the terraces take (two-block recording, then the prediction where the band predicts land, then the four-block recording, then the prediction at a whole block); terraces closed: for 20 random height fields over an 8 × 8 patch every edge between cells, in one-block pieces, is used an even number of times, and every vertex is at a whole block; lights merged per chunk (a cluster at its light-weighted centre, at most 4 per chunk, none across a chunk's edge, dim light left out) and placed from a tile's cells; window slots at levels 1 and 2 keep every resident tile while the camera moves and across the seam and back on a ring with a partial seam tile. Checked against broken code: with the south wall's neighbour taken wrong the closure check fails (190 open edges); with 64-block instead of 32-block chunks the light check fails.

**Smoke**: [Earth](test-results/earth-0.5.0-pre.1.txt) (`-OverviewWidth 8192 -PolarMode earth`; Earth Map 0.16.5, Cylinder World 0.11.14, Sea 0.4.6) passes except the known Antarctic-peninsula column (predicted ice y 290, generated 280), as on 0.4.7 and 0.4.8; [Cylinder World only](test-results/cylinder-0.5.0-pre.1.txt) and [plain](test-results/plain-0.5.0-pre.1.txt) pass. The far-terrain checks pass unchanged with the restructured shaders (Baikal, the far sea at the coast, night, twilight, curve, band heights, stitching, seam). New check, **mid-distance view** (every profile: the four programs link in all 24 settings; Earth: a recorded tower and torch at Nice through the production classes, its heights, the light and the curve, GPU time and GL state); numbers in the [design note](mid-distance-view.md#measured). Its renders: [600 blocks](test-results/mid-tower-600-0.5.0-pre.1.png), [1200 blocks](test-results/mid-tower-1200-0.5.0-pre.1.png), [the light at night](test-results/mid-light-1200-0.5.0-pre.1.png), [hidden by the curve](test-results/mid-light-2900-planet-0.5.0-pre.1.png), [shown on a flat planet](test-results/mid-light-2900-flat-0.5.0-pre.1.png).

Found by the checks while building it: the first Earth render showed single sky pixels where two terrace walls meet at a corner (fixed: walls overlap the corners); the test's first recording missed the tower (the cells a build changes are recorded block by block, as the game reports them; the check does that now); the first terraces took four-block recordings' heights and cost 0.71 ms (now the predicted height there and the colour worked out per cell: 0.24 ms). The existing far-terrain GPU timing gave 0.31 to 1.24 ms for the same draw in different runs: it timed at whatever clocks the GPU had; it now draws 60 times first (0.33 ms with the band, 0.13 chart only, 0.11 for 0.3.10's mesh). The smoke runner takes `-MidOnly` for working on the check alone (the full run is the verification).

Not verified here (needs the game): the recorder in a running client (every loaded chunk, the cost per frame, colours from `Block.GetColor`), the window filling while travelling and after a teleport, the GPU time in game (`.wmap mid`), the join with real chunks, the look of terraces and structures, lights from real lamps and a Voyage lighthouse, the `.wmap mid` switches, paper map sizes with colours, and GL state against the game's own renderers (the smoke check covers texture units, VAO, framebuffer, viewport, blend and depth mask).

## 0.4.8: paper maps

Updated 2026-10-22 (WM-P05, [design](paper-maps.md)). **Core suite**: a map keeps its description, charted area, terrain and markers through writing and reading; a reader gets only the parts they tick; a cut map, data after it, a charted area of another world's size, terrain outside the world, a tile twice, a damaged tile, a waypoint at NaN and an overlong title are refused; a stored map reads without decoding its tiles; duplicates (same title and icon within 2 blocks, readings within a block) and the merge (the reader's own cell stays, unknown cells are filled, a second merge changes nothing). The merge check fails when the rule is changed to overwrite (tried).

**Smoke** (the check uses its own small world, so it does not depend on the profile): plain and Cylinder World profiles pass; the full Earth profile (`-OverviewWidth 8192 -PolarMode earth`, Earth Map 0.16.3, Cylinder World 0.11.14) passes except the known Antarctic-peninsula column (predicted ice y 290, generated 280), its generated-check section identical to 0.4.7's own run. the real items and both recipes load. Player A (a fake server player) has a waypoint, an automatic marker, a temporary waypoint and a prospecting reading in the game's own layers, a charted patch and two recorded tiles on disk (one with colours that do not compress). A's `PaperMapClient` packs them, the upload goes through `PaperMapServer` in 2 pieces, and A's blank map becomes a drawn map describing 0.1 km², 2 tiles, 1 waypoint, 1 automatic marker, 1 reading (81 KiB in the save). The item goes to player B, who already has a waypoint of the same title where A's is and one recorded cell of A's tile. B reads it (2 pieces down): B has one Base waypoint (not two), the automatic marker with its tag and a new id, no temporary waypoint, the reading with a new id and its ore, A's charted area and nothing elsewhere, B's own cell at 150 with A's cells around it at 130, and A's block-by-block tile. A second read adds nothing; reading without holding the map and a damaged upload are refused, and the blank map stays blank. At the Earth profile's default 2048 chart the far-terrain Baikal lake check fails (25,567 of 397,969 pixels off the surface), identically on a clean 0.4.6 build; it passes at 8192.

Not checked here: the dialogs and right-clicks in a game client, the item's look in hand and on the ground, the chat lines, real network pacing with large maps, and two real players.

## 0.4.7: small lakes

Updated 2026-10-22. Reported in game: small lakes on the chart drew as stippled, stepped blobs without a shore. Diagnosed offline from the user's own recordings and prediction cache (that world's `observed` and `client` folders): the lakes were recorded alone (WM-D29), and the encoder took the unrecorded cells around them for water. A scratch harness encoded those tiles with the real Core code and drew them through `map.frag` in detail-tile mode, before and after: after, the shore is inked all round, no stipple, no pale rims.

**Core suite**: **PASS, 96,470 assertions**. New: a lake recorded alone reaches into the unknown without its prediction (the old behaviour, still the fallback) and has its coast at its edge (+0.5 texel) over predicted land; unknown cells keep no coverage and take the prediction's height; water against land is a shore, a height difference alone is not. The lake check fails with the prediction dropped from the encoder.

**Smoke, Earth** (`-OverviewWidth 8192 -PolarMode earth`; Earth 0.16.3, Cylinder 0.11.14, Sea 0.4.6): every check passes except the generated-polar Antarctic-peninsula column (predicted ice y=290, generated 280), which fails the same way without this change. GPU map (production shader, OpenGL 3.3 core) and water bed pass. Shore growth in the recorder (WM-D33) has no smoke check; it is play-checked.

## 0.4.4: review fixes

Updated 2026-10-21. A code review (Claude, then Codex; a second Codex pass over the fixes found four more gaps, fixed here, and a third found none). Final runs: [Earth](test-results/earth-0.4.4.txt) (`-OverviewWidth 8192 -PolarMode earth`), [Cylinder World only](test-results/cylinder-0.4.4.txt) and [plain](test-results/plain-0.4.4.txt), all PASS. Core suite: **PASS, 96,331 assertions**.

Each new check was also run against the old behaviour and fails there:

- **Recording levels** (core): every cell of levels 3 to 7 keeps exactly one four-block sample for each of the ring's 16 offsets against the samples' lattice, and a cell cut by the world's east and south edges (4 to 200 blocks inside) keeps one too. 0.4.3's rule fails at once (level 3, offset 0: none kept); centring on the whole cell fails on cut cells (a level-4 cell cut to 4 blocks: none). Smoke (client pipeline): a four-block sample reaches the eight-block level and a second one in that cell does not change it.
- **Samples while recordings load** (smoke, client pipeline): a sample for a recording still loading is reported as not kept and is absent once the tile has loaded; taken again, it is kept. Not checked here: `ObservedTerrain`'s retry of the chunk in a running client.
- **Write order across reconnects** (smoke): a session closed while a previous session's final writes are still running waits for them. The old flush fails ("did not wait").
- **Water bed** (smoke, every profile, real blocks in the test save): the generated bed while untouched, a raised bed, a dug-out bed and a platform with water under it. The old rule fails (232 for all three edits).
- **Exploration mask geometry** (core): on a 205,000-block ring the texels span the ring exactly, a cell is the texel the shader samples, and a disc across the seam is charted by the true distance on both sides (worst 0 to 2/255). The old geometry fails (96/255).
- **Line of sight** (core): the cells collected on the worker are the distinct cells of every visible sample, far fewer than the samples. The Earth smoke's Adriatic sight render passes unchanged.
- **Map layers on every ring copy** (core): the view of each copy, the copies a view shows across the seam and three laps away, the copy under the cursor, a bounded map. Not checked here: other mods' layers drawn in a game client.
- **Tile views** (smoke, GPU): a still view selects nothing in 200 frames, a moved one selects again, and so does one whose wanted tiles another view replaced; its draw list is the atlas's.
- **Far tiles without biome tiles** (smoke, GPU): the second session takes all 49 near-band tiles from disk and asks the server for none. The old disk rule fails (49 asked again).
- **Busy server** (smoke, Cylinder and Earth): with the job queue full, a request is answered busy at once, to the player who asked.

Not verified in game (play-testing 0.4.4 from 2026-10-21): the recorder's retry of a chunk in a running client (the smoke check calls the tile client directly, not `ObservedTerrain`), the refresh of cells recorded block by block, other mods' map layers on every copy of the ring (and the pinned-waypoint copy at the edge), far terrain turning off when the server stops sharing its chart, shader reload, the frame-time effect of the caches, and the mask's once-a-second mipmaps.

Test gaps: the tile-view check covers a still view, a moved one and replaced wanted tiles, not a recording appearing or a tile's content changing under a still view; the busy reply is checked against the server alone, not a 0.4.3 client's handling of it. Known limitations and smaller open items are listed in the README.

## 0.4.3: rivers, teleport menu, polar snow, twilight, lakes, chart depth, day and night

Updated 2026-10-20. The final runs ([Earth](test-results/earth-0.4.3.txt), [Cylinder World only](test-results/cylinder-0.4.3.txt), [plain](test-results/plain-0.4.3.txt), all PASS) used the sibling releases Earth Map 0.14.7, Cylinder World 0.11.6 (its 22:51 commit) and Sea 0.3.8-pre.3. Core suite: **PASS, 96,088 assertions** (new: teleport columns across the seam, at the poles, past a bounded map's ends, non-numbers; the day grid's cell centres, rows, clamping).

**Charting by line of sight.** Core suite: on a synthetic world, open sea is seen out to the reach and not beyond, a ridge's face is seen and the land behind it is not (until the eye is high enough to see over it), sight crosses the seam, and the height grid keeps each cell's highest point with water at the sea. Smoke (Earth), on the real world chart: from 3 blocks above the middle of the Adriatic (43.0° N 15.6° E), the open Adriatic and Italy's Adriatic coast (Pescara, Ancona) are seen, the Tyrrhenian Sea behind the Apennines is not, though it lies within the 4096-block reach; from 600 blocks up it is (so it was hidden by the mountains, not missing). Height grid 4096 × 2048 in about 70 ms once per chart, a cast in about 19 ms on a background thread. The first render showed a checkered pattern of uncharted texels on the open sea (one sample per 50-block height cell against 32-block mask texels); samples are now half a mask texel apart and the check requires no isolated uncharted texel on open sea ([render](test-results/sight-adriatic-0.4.3.png); sea behind the Croatian islands stays hidden, in patches). Not verified in game: casting while sailing, and the switch between the circle and sight.

**Climate stripes (Earth Map data).** Reported in game: horizontal green lines and bands across Africa and Siberia on the Terrain map. They are in Earth Map 0.15's climate grid (earthdata/climate.bin, 0.25 degrees): some rows differ regionally from the rows on both sides, e.g. 48.6-48.9 N from Kazakhstan to Mongolia (rain 66 to 90 to 56 at 60 E, the warmest month up to 3 C warmer) and about 11.9 N at 20 E (warmest month 36.7 to 30.8 C, rain 63 to 96), probed through Earth's own SampleClimate. A parallel climate bake equals a single-threaded one (new GPU check), so Wmap does not make them. Cause (found by the Earth weather session, 2026-10-21): the UDel precipitation source lacks one month in about 15 % of land cells, in runs along rows; Earth's bake treated those cells as having no UDel data, so its coarse ocean data took over on the 0.25 degree rows around them. Fixed for weather.bin in Earth 0.15.1; climate.bin, which world generation reads, waits for the user's save-policy decision, since new chunks would meet old ones with seams along the old stripes. Until then Wmap's Terrain colours show the stripes as the world has them.**Cross-blended tints.** Reported in game: most of Europe one green. The Terrain style now blends a humid and an arid height ramp by dryness (Patterson and Jenny's cross-blended hypsometric tints), with forest and a tundra ramp; tuned in the browser preview over Europe and the world. A first version painted Siberia and northern Canada white (snow below -17 C mean); snow now comes from height and the ice data only. The low-desert check still passes ([world](test-results/terrain-climate-0.4.3.png), [Algeria](test-results/algeria-climate-0.4.3.png)).

**Terrain colours by climate.** Reported in game: Algeria's low desert drawn lush green. GPU check (Earth): the climate raster baked from Earth's biome source (1024 × 512 texels in 4.4 s on 8 threads), Terrain style over the whole world: three low deserts (Sirte, the Grand Erg Oriental, the Western Desert) that are green by height must turn sand-coloured, and the Congo must stay green; it passes ([world](test-results/terrain-climate-0.4.3.png), [Algeria](test-results/algeria-climate-0.4.3.png)). The first version of the check used a Sahara point on high ground, already tan by height, and failed on itself; it now uses low places. The real server bakes, caches and announces the raster in the smoke world (its log: 64 × 32 texels at the smoke's small chart size). Not checked: the download to a game client and the colours against the game's own ground.

**The map follows the game mode.** Server check (every profile): a survival player gets the game chart and is refused predictions with `ShareWorldChart` off; the same player in creative gets the full map and predictions at once. Not checked here: the client switching modes and rebuilding the window when the game mode changes (needs a game client).

**Close zoom over recordings (recorder of WM-D29).** Reported in game: changed terrain at a story location vanished close up; then the first fix (every block) was too costly, drew treetops and rewrote the map all the time. Core suite: the one-block prediction draws over four-block recordings and one-block recordings draw over it; the rule for recording a cell block by block (land against water, more than 3 blocks plus 3 % of the height; the sea compares its bed, a lake its surface). Smoke (every profile, client pipeline): a four-block sample stays out of the one-block level, a block of a detailed cell reaches it, and a colour-only change is no change. Smoke (Earth, generated chunks): in a central German forest the recorder finds the generated ground under all 32 tree columns sampled (within a block). Not checked here: the comparison with the near band and the change events in a running client, and the cost while walking (by design at most 256 samples and 1 ms per tick, and nothing between changes).

**Far terrain at twilight.** Reported in game in Antarctica: far snow glowing red-brown beyond grey-blue real snow. New GPU check (every profile) at twilight daylight (0.375): a white and a deep red sun colour must give the same far terrain, and a dim ambient a different one. The old shader [fails](test-results/twilight-before-0.4.3.txt) (26/255 between the suns); 0.4.3 gives 0/255, and 52–87/255 between ambients. Not verified in game; far terrain at night is now as bright as the game's own terrain under the open sky (about a third of full light), brighter than 0.4.2 drew it.

**Lakes in far terrain.** Reported in game at Lake Baikal: far terrain under the real lake water. New GPU check (Earth): looking along Baikal (53.3° N 107.9° E) from 40 blocks above its surface with the far water drawn pure red, at most 1 % of red pixels may lie more than 4 below or 3 above the lake surface. The old water rules [fail](test-results/lake-before-0.4.3.txt) (34,255 of 36,396 pixels off, up to 11 blocks low: the lake sank like land near the start); 0.4.3 passes with 391,505 water pixels ([render](test-results/lake-baikal-0.4.3.png)); the few outliers reach 17.5 blocks above it, on shore cells. The existing far checks (sea edge, far sea on the coast above Nice, band heights against their oracle) pass unchanged.

**Chart depth and height.** Iterated in the browser shader preview over the Earth world chart, then rendered by the GPU check ([region chart](test-results/chart-depth-0.4.3.png)). No numeric check: it is a style.

**Day and night.** Reported in game: a strange dark circle (the daylight strength is near zero only in deep night). The overlay now shades by the sun's elevation in day, civil, nautical and astronomical twilight and night. Server check (every profile): the calendar's sun over the 256 × 128 grid in about 7 ms; in the test worlds -15 to +33 degrees at hour 8, varying with latitude and not with longitude (no mod moves the sun by longitude there). GPU check: the day half keeps its brightness, the night half darkens (525 to 298) and the horizon line is drawn ([render](test-results/daynight-0.4.3.png)). Real calendar (2026-10-21): the earth profile loads AstraTerra 0.15.0-dev.1-earthtest (dist\, by name; LongitudeAwareSun true), whose sun follows Earth Map's meridians (Earth gives it each X's longitude, its decision 0010; Earth alone does not move the sun). Both binding lines are required in the server log. The calendar is moved to an equinox and both solstices and the sun sampled into the grid ([equinox](test-results/daynight-equinox-0.4.3.png), [June](test-results/daynight-june-0.4.3.png), [December](test-results/daynight-december-0.4.3.png)). At hour 0.98: local noon at 165.3 E, the highest sun in that column (89.2 degrees at the equinox, 67.3 in June, 65.9 in December); the sunset line at -104.6 E on every row from 60 S to 60 N at the equinox (straight); in June at the equator -104.4, 45 N -79.5, 45 S -129.8, 60 N -56.3, 60 S -153.0 (curved), December mirrored; polar day north of 68 N and polar night south of 68 S in June, reversed in December; twilight bands 5.9/6.0/6.1 degrees of longitude wide at the equinox and 6.5 at the solstices ([Earth](test-results/earth-0.4.3.txt)). The Cylinder World and plain profiles (no AstraTerra) show latitude bands only: every row one elevation ([Cylinder](test-results/cylinder-0.4.3.txt), [plain](test-results/plain-0.4.3.txt)). A first version of the noon check failed on the grid's -32..+31.75 degree clamp (a plateau of +31.75 around noon); it uses the elevations before the clamp. Not verified in game: the overlay with AstraTerra in a game client.

**Rivers at full zoom.** Reported in game on the Guadalquivir: a second, darker blue band beside the river. New GPU smoke check (Earth), **river-terrain**: the production shader at 2 pixels per block over the Guadalquivir (37.25° N 6.07° W) with level-0 tiles; pixels in the world chart's river-channel colour must stay below 50. 0.4.2's shader [fails](test-results/river-before-0.4.3.txt) (7,192 such pixels beside 104,099 of the tile's water; [render](test-results/river-before-0.4.3.png), the reported artifact); 0.4.3 passes with 0 ([render](test-results/river-0.4.3.png)). Coarser tiles still draw the chart's river lines; not rechecked at intermediate zooms beyond the existing views.

**Wind check and Sea 0.4.** Sea 0.4.0 (another session, 2026-10-21) removed its own wind belts and seasons; winds now come from a provider (Earth Map 0.15). The wind check required Sea's old seasonal shift at 30 N (0.1) and failed; it now requires the prevailing wind to change at all between January and July (0.035 there), which shows Wmap reads the month. The trade-wind and westerly checks pass unchanged. **Teleport menu:** dark ink on chart paper (white text was hard to read; not checked visually here, as the GUI check has no GL).

**Teleport.** Smoke (every profile): a map position east of the ring's end lands on the ring's own lap; the column is loaded (generated in the test save) and the arrival point lies just above its top block or water, then again at once for the loaded column (Earth: x 230407.3 → column 25607, onto salt water). Protocol: an old server's manifest offers no teleporting, the served one does, the request survives the wire format. [Earth](test-results/earth-0.4.3.txt), [Cylinder World only](test-results/cylinder-0.4.3.txt) and [plain](test-results/plain-0.4.3.txt) pass; the plain run first failed on the check itself, which wanted air where the rain height passes tall grass (a passable block is enough now). Client (every profile, in the GUI check with the real AtlasDialog and Cairo): with a creative player a right-click opens the menu inside the map without teleporting, a click elsewhere and Esc close it, a left click on **Teleport here** sends one request for the right-clicked point (the world's centre after Whole world); a survival player gets no menu. Not checked here: the menu's drawing (no GL in the GUI check) and the server's creative/privilege refusals, which need a real player.

**Seam band check and Cylinder 0.11.6.** Cylinder 0.11.6 keeps Earth's real seabed in the seam band of new Earth-mode worlds (`EarthSeabed`; `SeamGround` returns the natural ground). Wmap's prediction follows by itself, since it asks `SeamGround` per column, but the check required shaped columns and failed ("no seam columns compared"). It now requires none for such a world (59,424 columns follow the natural ground) and still checks the v10 and v9 band on copies without the flag.

**Snow on polar ice.** With Earth Map 0.14.6 and Cylinder World 0.11.6 (its earlier 22:23 build). Reported in game (Earth 0.14.5, Cylinder 0.11.5) near the North Pole by the seam: green islands with chunk-shaped edges along the player's track in Development · Terrain. The player's recordings held about 4,000 polar cells as land 1–7 blocks above the sea (snow drifts on Cylinder's sea ice) and 558 as land on the Wall's top. The Earth profile's generated-polar check (polar mode `earth`) now runs the recorder's classification (`ObservedTerrain.Classify`) over every generated column the prediction calls sea ice or Wall and requires the same kind, with snow-covered columns among them. 0.4.3 [passes](test-results/earth-0.4.3.txt) with the rest of the Earth profile: 256 sea-ice and Wall columns agree, 192 of them under snow. The recorder without the snow rule [fails](test-results/snow-before-0.4.3.txt) at the first location (the Wall's top under `snowlayer-1`: land instead of Wall, 64 of 64). That run also showed the report header saying PASS above the failing generated check; the header now says FAIL when a generated check fails. Not verified in game yet.

## 0.4.2: far terrain at night

Updated 2026-10-17. Reported in game: looking north at night, distant land glowed brown, about (11, 7, 5) against a sky of (4, 2, 1). New GPU smoke check in every profile, **night**: the coast view at the game's night daylight (0.0625) under a black and a brown fog colour, with fog itself off; far terrain must not change by more than 6/255. 0.4.1 [fails](test-results/night-before-0.4.2.txt) (41/255); 0.4.2 passes (5/255). At noon a reddish sky texture in place of the stand-in must still change it by at least 15/255 (Earth 129, Cylinder 180, plain 107). [Earth](test-results/earth-0.4.2.txt), [Cylinder World only](test-results/cylinder-0.4.2.txt) and [plain](test-results/plain-0.4.2.txt) pass. Not verified in game yet; the night brightness of far land itself (its lighting floor) is unchanged and only compared against one screenshot.

## 0.4.1: the far sea stays off the coasts

Updated 2026-10-17. Reported in game: at view distance 512, distant coasts looked flooded compared with the same view at 1536. New GPU smoke check, **far sea stays at the sea**: the far sea drawn pure red toward the coast above Nice; each red pixel's height from the depth buffer must be within 3 blocks of the sea. 0.4.0 [fails](test-results/shore-before-0.4.1.txt) (5,132 sea pixels drawn more than 3 blocks up, up to 75); 0.4.1 passes (119,010 sea pixels, the highest 0.9 blocks up), with the rest of the [Earth](test-results/earth-0.4.1.txt) profile; [Cylinder World only](test-results/cylinder-0.4.1.txt) and [plain](test-results/plain-0.4.1.txt) pass too. GPU time per draw 0.36 ms with the band. Checked in game by the user on 2026-10-17 at the reported place: coasts look right and the near band works.

## 0.4.0: far terrain near band and climate colours

Updated 2026-10-17, same mods as 0.3.9. The plan and the implementation went through adversarial Codex reviews ([design note](far-terrain-near-band.md#implemented-040)); the findings changed the design and added the checks marked *(review)*.

**Core suite** (`dotnet run --project tests/WmapTests -c Release`): **PASS, 96,060 assertions**. New: the toroidal tile window at 45 camera positions per world, for circumferences that are and are not multiples of a tile, levels 2 and 6 (distinct slots, also across the seam; nearest first; canonical keys; tile index and position give back the point; texel centres where `SemanticTile.Sample` samples); a partial seam tile counts against the slots; bounded worlds. Biome tiles: exact at their samples, sampling every 16 blocks (level 2) or every second texel (level 6), neighbours equal where they overlap, not readable as semantic tiles. The near band's finer mesh tiles and stitches like the others and keeps the normal snap. Shader sources are ASCII (non-ASCII comments broke compilation on AMD, found by the smoke run).

**Smoke, far tile stream** (all profiles): old and new messages read each other in both directions, and an old manifest offers no far tiles. *(review)* The manifest actually sent to a player, serialised, turns the band and climate on (Earth) or off (Cylinder alone). Atlas and far requests do not cancel each other. Only allowed stream/content/level combinations are queued. At most 8 of 20 far requests are open per player. *(review)* Two players with 8 requests each alternate (ABABABAB) beside four players whose queues ran empty. A player who leaves gets nothing more, and their old work does not change their new count. Earth's biome: Germany forest 249, Sahara 0 and dry, the equator warmer than Lapland. Cold costs: semantic tile 41 ms, band biome 2 ms, climate biome 18 ms.

**Smoke, near band on the GPU** (Earth): through the real `FarTiles`, fed from Earth's samplers, and transform feedback of the displaced vertices:
- 1024 stitched vertices lie exactly on their coarse edges (0.0000 blocks), with blend, canopy and push-down; also at the seam and *(review)* across a 300-block partial seam tile in a synthetic 10,540-block world.
- 5,646 vertices in the band's core equal a CPU oracle of the tiles (bilinear on the stored bytes, decoded).
- On land above Nice, 900–1180 blocks out: band median 0.38, 95 % 1.07 blocks from Earth's terrain; the chart alone 1.75 and 9.02.
- *(review)* Heights move by at most 0.005 blocks when a tile's weight goes from 0 to 0.0001 (continuous fade-in).
- *(review)* With every tile half faded in, the step across the partial seam tile's edge is 2.72 blocks, like an ordinary tile edge (3.61); the old formula gave 10.16 ([fails](test-results/partial-seam-before-0.4.0.txt)).
- *(review)* Probe points every quarter block (a probe mesh, captured the same way): at most 0.45 blocks per step beside a missing diagonal tile (the per-edge formula: 46.22, [fails](test-results/corner-before-0.4.0.txt)), 0.26 between tiles faded to 0.3 and 0.8, 0.16 across a 30-block seam tile.
- *(review)* Crossing the seam in a world with 21 tiles per lap keeps all 56 loaded tiles in their slots and faded in (0 uploads), and heights still equal the tiles after it.
- *(review)* GL state: the draw keeps the active unit, texture bindings, VAO and unpack alignment; tile uploads and texture creation keep them with a pixel-unpack buffer bound and odd unpack settings.
- The water tint changes the far sea's colour (G/B 1.09 against 0.83 without).
- GPU time per draw: 0.31 ms with the band, 0.11 ms for the same mesh from the chart alone, 0.10 ms for 0.3.10's mesh (median of 9).

Results: [Earth/Cylinder/Sea](test-results/earth-0.4.0.txt), [Cylinder World only](test-results/cylinder-0.4.0.txt), [plain](test-results/plain-0.4.0.txt). The plain profile's 0.3.10 sea-edge check now skips the synthetic chart, which has no sea there (it failed there since 0.3.10; that profile had not been run for 0.3.10).

Not verified here: the look in a game client (the game's lighting, translucent water, fog and sky are not in the harness), the colours against real chunks, view distances other than 512, multiplayer over a real network, and how the server copes with many players filling bands at once.

## 0.3.10: far terrain meets the real sea

Updated 2026-10-17, same mods as 0.3.9. Reported in game at a 512-block view distance: a pale band along the far edge of the real sea, with far coasts visible through the water. Cause: the game fades water out between 0.75 and 0.80 of the view distance (`chunkliquid.vsh`) and land between 0.80 and 0.85 (`chunkopaque.vsh`), while far terrain started at the view distance − 24 and sat 12 blocks low there, so the fading water showed far terrain kilometres away.

New GPU smoke check, **sea edge**: the far-terrain shader looks south over the Mediterranean from 3 blocks above the water at a 512-block view distance. Every pixel whose ray meets the water surface where the game's water fades (384–410 blocks) must show far terrain, the far sea must not lie in front of the water surface, and must be at most 1.5× the water's distance. Restoring 0.3.9's start distance, push-down and sea level [fails](test-results/sea-edge-before-0.3.10.txt) (142/142 pixels, up to 2.2×); 0.3.10 [passes](test-results/earth-0.3.10.txt) (142/142 sea pixels, at most 1.27×), with the rest of the Earth profile.

Not verified here: the look in a game client (the game's own water, fog and sky are not in the harness), other view distances, and the far sea's colour against the game's climate-tinted water.

## 0.3.9: Cylinder World's natural seam band

Updated 2026-10-17, with Cylinder World 0.11.5 (layout v10), Earth Map 0.14.1-pre.3 and Sea 0.3.5. The core suite (42,146 assertions) is unchanged and passes.

New smoke check in the Cylinder and Earth profiles, **seam band against Cylinder's `SeamGround`**: `CylinderSurface` over synthetic natural land (sea level + 30) and deep sea (sea level − 70), on both sides of the seam, at three latitudes away from the poles, every 5.5 blocks from the world edge to 96 blocks past `SeamWorkReach`. Height, depth and kind must equal what the installed Cylinder's `SeamGround` gives. It runs for the world's own layout and for a copy of its parameters set to layout v9. Layout v10: 59,424 columns, 57,493 shaped, 221 of them past the half-width H = 26,272 (reach 27,136), identical-part seabed 100–140. Layout v9: 0 columns changed past H, flat seabed 125 (= `SeabedY`). The 0.3.8 code [fails it](test-results/seam-band-before-0.3.9.txt) at the first v10 column.

The Earth profile's generated-chunk comparison now also runs without Earth polar mode and adds three seam locations at the equator over the Pacific gutter: the identical part next to the jump line, inside the band 100 blocks from H, and 96 blocks past H.

[Cylinder World only](test-results/cylinder-0.3.9.txt) and [Earth/Cylinder/Sea](test-results/earth-0.3.9.txt) (Earth polar mode, 8192 × 4096 chart, 73 s bake, 768 generated columns at 12 locations with 0 mismatches and 0 height error) pass. The plain profile was not rerun: the change only applies with Cylinder World.

Not verified here: the v10 band in a game client's map; Earth's default polar mode (its seam locations run in the same code); the v9 fallback path against a real pre-0.11.5 Cylinder build (it is the 0.3.8 code, unchanged).

## 0.3.8: review fixes and other terrain generators

The core suite (42,146 assertions) adds loading saved exploration (a locked file is a failed read, a truncated one is damage) and view selection with recordings and predictions together: recordings are selected with shared predictions and drawn over those of their level, close zoom keeps the four-block recordings, flat predictions start at level 2 so recordings always cover them, and at regional zoom recordings are drawn over a flat mask but not over a prediction with relief.

New smoke checks, in every profile:

- **Chart download pacing:** 1,000 requests for one chunk plus every offset twice send 8 chunks in the first tick and 32 distinct chunks in all; a client with the normal 16-request window receives all 16; three players who keep requesting more than the budget for 30 ticks receive 160 chunks each.
- **Locked recording:** a recording file held open exclusively by another process is not loaded as empty or edited, and loads intact once released. A truncated recording loads as empty, so it can be recorded again.
- **Cache identity:** two starts give the same prediction cache; changing Cylinder's sea-ice date does not change it, `FlattenSeabed: false` does; `PredictTerrain: false` drops the predictions and keeps the topology and recording folder.
- **Bounded map edge** (GPU): a non-wrapping chart with land only along its west edge, viewed from the east edge where the camera clamps. The map's east must lie west of the camera and no west coast beyond it. Restoring 0.3.7's wrapped camera coordinate [fails](test-results/bounded-edge-before-0.3.8.txt); 0.3.8 passes.

[Plain](test-results/plain-0.3.8.txt), [Cylinder World only](test-results/cylinder-0.3.8.txt) and [Earth/Cylinder/Sea](test-results/earth-0.3.8.txt) (Earth polar mode, 8192 × 4096 chart, 73 s bake, 576 generated polar columns with 0 mismatches) pass with Cylinder World 0.11.4, Earth Map 0.14.1-pre.2 and Sea 0.3.5.

Not covered by a check: the per-player packet admission in `WmapSystem`, which needs the game's network layer, and reconnecting while the previous session's exploration write takes longer than 2 s. Not verified here: Terra Prety (not installed; see [terrain-generators.md](terrain-generators.md)), recordings drawn over predictions and recorded water depth in the game client, and far terrain staying off for a Cylinder-only world in the client. The recorder's bed scan reads real chunks and runs only in the client.

## 0.3.7: Jonas barometer, layer depth, stains

The core suite (42,135 assertions) adds the barometer's local field: square sampling round the player, wrapping over the seam, calm beyond the poles, rows running south; plus knots and compass bearings.

New GPU regressions in the real GUI element path, with the game's projection and a depth buffer: a stand-in for Magelan's ship plan drawn at depth 55 after Wmap's arrow (58) and wind layer (57) must show in the arrow quad's empty corner and under the wind arrows, and must stay hidden under the arrow itself. Restoring 0.3.6's depth writes [fails both](test-results/layer-depth-before-0.3.7.txt); 0.3.7 passes. A synthetic barometer field must draw arrows inside its reach and no ink beyond its dashed circle. A new server check loads the barometer item as the game does: the wind-instrument attribute, its shape and all four game textures it names, the recipe with all seven ingredients resolved, and the English names.

[Plain-world](test-results/plain-0.3.7.txt) and [Earth/Cylinder/Sea](test-results/earth-0.3.7.txt) integration pass. The Earth run samples a real barometer field in the North Atlantic from Sea's weather (96 × 96 in 5 ms) and renders it on the gameplay chart ([preview](previews/barometer-gameplay-chart.png)). The gameplay renders show the parchment without stains. The plain profile's far-terrain colour check is limited to real charts, because its synthetic test island is one flat level; it had not been run since 0.3.6.

Not verified here: the item's look in the hand and on the ground, crafting in the GUI, and the barometer's view in the game client. Magelan's binnacle side doesn't exist yet.

## 0.3.6: far terrain

Design: [far-terrain.md](far-terrain.md). The core suite (42,129 assertions) now builds the far-terrain mesh at four distance/detail settings and proves that its rings tile the square exactly (summed triangle area equals the square's) and that every vertex on a fine ring's outer edge, displaced as the shader does (heights depending on the ring's mip level), lies on the coarser ring's edge: no cracks.

The [Earth/Cylinder integration](test-results/earth-0.3.6.txt) compiles and links the production far-terrain shader with the game's own shader includes and #define prefix for 24 settings combinations (shadow quality 0–2, SSAO off/on, godrays off/on, 0 or 10 dynamic lights) and renders it from the real world chart in the hidden OpenGL context: a view north over the Riviera coast (every sampled pixel below the horizon drawn, none high in the sky, 30 colour tones), a view straight down (nothing drawn inside the start distance), and the same westward view across the seam from two positions one circumference apart (161,638 pixels drawn, 0 differ). Default mesh: 3 rings, 41,985 vertices, 81,920 triangles, 1.6 MiB. The smoke sky and glow textures are stand-ins, so the colours of the renders are not the in-game colours.

Not verified here: the game's own render pipeline (its shader registry, sky textures, fog values, camera far plane and the boundary with real chunks). That needs an in-game check.

## 0.3.3: relief, zoom cache and player heading

The user confirmed that 0.3.2 draws in the game client, but reported that local relief softened after the world chart finished, zoom changed detail in patches, and the native minimap footprint disagreed with the atlas arrow.

The detail shader replaced the tile's local height gradient with the world chart's regional gradient whenever the chart was available. It now keeps local relief and uses the chart only for river courses and coast distance beyond the tile margin. The new synthetic hill regression renders exactly the same loaded detail before and after a flat coarse chart arrives, and compares the resulting pixels. Briefly restoring the old slope replacement [fails the regression](test-results/relief-before-0.3.3.txt) with a mean channel difference of 28.65; the fixed shader passes.

The GPU atlas previously drew only the requested level and discarded an uploaded tile when its decoded CPU entry was evicted. It now retains its separately bounded GPU copy, and draws cached parents or one neighbouring finer level underneath newly requested tiles. Fallbacks coarser than the finished chart are excluded. A SHA-256 fingerprint per slot keeps unchanged disk reloads from invalidating the surviving image. Checks cover zoom in and out, finer-level promotion, exclusion of useless coarse fallbacks, identical rendering after CPU eviction and disk reloading, and invalidation after an actual recorded terrain edit.

Both Wmap views now use the same arrow and yaw conversion. Tests invoke the atlas's actual player drawing method and the minimap's marker drawing helper at four cardinal directions, checking the front and rear pixels against `EntityPos.GetViewVector`. The HUD registry replaces the native player layer with a subclass that keeps native player tracking and hover text; vanilla full-map rendering delegates to the original layer. Remote player entitlement colors and native waypoint markers remain available.

The 31,840 core assertions, [plain-world integration](test-results/plain-0.3.3.txt), and [Earth/Cylinder integration](test-results/earth-0.3.3.txt) pass. Earth renders the full 8192 × 4096 chart, 240 local detail tiles, seam/gameplay views and both heading paths, and matches all 576 generated polar columns. The final plain run covers the additional identical-disk-reload and changed-content checks. Interactive verification of 0.3.3 remains pending; GPU tests use a hidden context with simulated client services.

## 0.3.2: shared camera matrices and HUD minimap

The interactive 0.3.1 retry downloaded and cached the world chart, but showed only the brown dialog background and GUI-drawn geographic labels. Neither the map shader nor its compass was visible.

The installed `ClientMain.CurrentProjectionMatrix` and `CurrentModelViewMatrix` both return `tmpMatrix`, overwriting it on every getter call. Wmap passed both getters as method arguments, so both shader uniforms received the modelview matrix. The previous test returned independent arrays and did not model this engine behavior. The corrected regression reproduces `GUI map blank with shared matrices` on the unmodified 0.3.1 DLL. 0.3.2 copies each matrix immediately and uses that shared drawing entry point for both atlas and minimap.

New minimap checks draw through `WmapMinimapLayer` with the same shared matrix getters. They verify visible terrain, shared Chart/Terrain switching, gameplay exploration and HUD bounds. Registration places the chart after native terrain and before native markers, before the game's map worker starts. Atlas and minimap share world-chart/mask textures; the HUD has one detail page. The atlas owns detail requests while its window is open. The vanilla full-map fallback is retained.

The 31,840 core assertions pass. [Plain-world integration](test-results/plain-0.3.2.txt) and the final [Earth/Cylinder integration](test-results/earth-0.3.2.txt) pass, including the corrected matrix getters, minimap drawing and registration order. The Earth run uses an 8192 × 4096 chart (69.0-second bake, 12.5 MiB packed), renders 240 local detail tiles, checks seam/gameplay views and matches all 576 generated polar columns. The [0.3.1 failure report](test-results/shared-matrices-0.3.1-before.txt) preserves the regression before the fix.

The user subsequently confirmed that the 0.3.2 interactive retry renders the map. The follow-up relief, zoom and heading issues are covered by 0.3.3 above.

## 0.3.1: chart download repair

The first interactive 0.3.0 report showed an empty map with **“Charting the world… 100%”**. The live server log confirmed a finished 8183 × 4100 chart (12.4 MiB, 74.2 seconds), but the client had no cached world chart. The ready manifest was suppressed because sampling had already announced 100% before compression and publication completed.

The regression test failed on 0.3.0 with `Finished world chart was never announced after bake progress reached 100%`. It now verifies that readiness produces exactly one new manifest, and that a player still loading during publication receives the chart upon entering play. The server only publishes a chart ID when the packed bytes are available.

A new GPU check invokes the real `AtlasElement.Draw` path with the game's GUI projection and modelview translation. It reproduced blank output with back-face culling enabled and with a strict `LESS` depth test over the composer's depth-50 background. The map now temporarily disables culling and draws its world/detail layers above that background; the test checks visible pixels and restored culling. These are inherited-state compatibility checks; the game's normal GUI starts with culling disabled and `LEQUAL` depth testing.

The **31,840 core assertions pass**. The final release passes isolated server integration with:

| Profile | 0.3.1 result |
| --- | --- |
| Earth Map 0.13.0 + Cylinder World 0.11.1, Earth polar mode | [PASS](test-results/earth-0.3.1.txt): 8192 × 4096 chart; 74.5-second bake; world, regional and local rendering with 240 detail tiles; seam and gameplay fog; new GUI/state and readiness regressions; all 576 generated polar columns match |
| Plain world | [PASS](test-results/plain-0.3.1.txt): recorded terrain/client pipeline, permissions, new readiness regressions, GUI composition and GPU rendering with a synthetic chart |

The subsequent 0.3.1 interactive retry confirmed the download fix but exposed the remaining camera-matrix failure above. The hidden GPU test simulated client services and returned independent matrices, so its earlier rendering pass did not cover that failure.

## 0.3.0 baseline

The results below were recorded with Earth Map 0.12.2 and Cylinder World 0.11.0.

**Not yet done: an interactive playtest in the game client.** Everything below runs inside a real game server process with the installed game assemblies. GPU rendering uses a hidden OpenGL 3.3 core context, not the game's window and GUI frame. The first in-game check list at the end covers what remains.

## Core suite

`dotnet run --project tests/WmapTests -c Release`: **PASS, 31,840 assertions.**

- **Distance transform:** exact Felzenszwalb transform compared cell by cell with brute force, bounded and periodic. Coast distance has the right sign on each side and crosses zero between the last land and first water cell. It continues across the cylinder seam, and unknown cells never create a false coast.
- **Encodings and codecs:** square-root encodings keep sign and precision. Raster and tile codecs round-trip exactly and reject damage, version mismatch, oversize input and decompression bombs.
- **World chart bake:** an island straddling the seam stays continuous. Heights and sea bed are relative to the sea surface, the river channel is negative inside a river, the world raster round-trips, and texel budgets and square texels hold for Earth and very large worlds.
- **Detail tiles:** the sampling margin makes coast distance identical across tile edges. Bounded worlds outside their edge are unknown, sea ice counts as sea while glaciers count as land, and recorded coverage encodes correctly.
- **Exploration:** the default radius is 1536 blocks for every player, it is bounded, nothing is charted beyond it, and charting wraps across the seam.
- **Detail selection:** none at whole-world zoom, only levels finer than the chart, the tile budget is respected, recordings are used when predictions are not shared, and draw order is correct.
- Topology, camera, partial edge tiles, geographic formatting and the observation codec, kept from earlier versions.

## Server integration (`tools/Run-Smoke.ps1`)

Each profile starts an isolated server with its own data directory, a new save and loopback port 42439, then runs the checks below and shuts down. Raw reports: [Earth](test-results/earth-0.3.0.txt), [Cylinder](test-results/cylinder-0.3.0.txt), [plain](test-results/plain-0.3.0.txt).

| Profile | World chart | Result |
| --- | --- | --- |
| Earth Map + Cylinder World, Earth polar mode, 256,000 × 102,400 × 384 | 8192 × 4096 from Earth's generator and 826,332 river segments: bake 71.5 s, pack 1.0 s → 12.5 MiB, unpack 0.2 s | PASS |
| Cylinder World only | 4096 × 2048 approximate continents: bake 10.0 s → 0.6 MiB | PASS |
| Neither (plain 256,000 × 102,400) | none; synthetic chart for the GPU check | PASS |

Bake times are one-off per world and run on 16 below-normal-priority threads. Earth's terrain sampling is serialised by a lock in Earth Map's tile cache (see the README follow-ups); most of the 71 s is spent there.

Checks in every profile:

- **Client pipeline** (real `TileClient` and `BaseMapClient`, simulated channel): semantic network tiles, request reuse during pan and zoom, rejection of stale-world data, warm disk cache and corruption recovery. Recorded terrain is encoded and persisted across an immediate reconnect, and survives prediction-cache revisions and quota trimming. Unshared predictions are never requested. The world chart transfer is chunked and verified by SHA-256; a damaged transfer is fetched again once, and the cached chart is reused next session without a download.
- **Server permissions:** ordinary players get gameplay. Predicted detail is shared only with `ShareWorldChart`, development only with the privilege or `AllowAllPlayers`, and manifests are isolated per player.
- **GUI composition:** the real `AtlasDialog` with Cairo at four window and GUI-scale combinations, mode switching, repeated open and close, recompose, disposal and opening-failure containment.
- **Map key:** atlas dispatch, preserved remapping, original-map fallback and cleanup.
- **GPU map:** the production shader through `MapRenderer` and `TileAtlas` in an OpenGL 3.3 core, forward-compatible context.
  - The 8192 × 4096 chart uploads in 6 row strips with a full mipmap chain (299 ms).
  - At whole-world minification the rendered sea share equals the chart's (67% vs 67%). This check caught an incomplete mip chain during development; it rendered the whole world as sea.
  - Views rendered: world, region and close zoom, the close zoom with 240 detail tiles loaded through the real client cache and atlas. Also the cylinder seam, which is checked for a column discontinuity, and gameplay fog, checked to be blank parchment outside the explored path.
- **Polar adapter (Earth):** the generated chunks at 9 locations (576 columns: both walls, both borders, Arctic ice outside the old strip, the Atlantic margin, Greenland, the Antarctic plateau and peninsula) match Wmap's prediction with **0 mismatches and 0 height error**. Cylinder 0.11 places sea ice in the fluid layer; the comparison now reads it, and so does Wmap's terrain recorder.

## Rendered previews

These are production-shader output from the Earth run, rendered in the hidden context, not screenshots of the game GUI. The graticule labels, scale bar and dialog chrome are drawn by the game's GUI and do not appear here.

- [World, chart](previews/world-chart.png) and [world, terrain](previews/world-terrain.png)
- [Western Europe, chart](previews/region-chart.png) and [terrain](previews/region-terrain.png)
- [English Channel at 3 blocks/px with detail tiles](previews/local-chart.png)
- [The seam through the Bering Strait](previews/seam-chart.png)
- [Gameplay: only a voyage corridor charted](previews/gameplay-chart.png)

## First in-game check

1. Replace previous Wmap ZIPs with `release/wmap_0.3.3.zip` and restart the game. Start the Earth + Cylinder world and watch `server-main.log` for `[wmap] Charting the whole world once` and then `World chart ready`. On a first bake, verify the status advances past 100% into receiving/preparing the chart without leaving the world.
2. Press M. Check whole-world rendering and smooth, cursor-anchored zoom. At close zoom new areas still need detail tiles. Revisit a loaded area at several nearby zoom levels: cached detail should remain beneath the new level as it loads, and local relief should stay detailed after the world chart arrives.
3. Pan east and west through the seam repeatedly, with and without Follow.
4. Switch Chart and Terrain, then Dev and Gameplay. Gameplay should show blank parchment except near where you have been.
5. Check the graticule labels, scale bar, compass rose and heading marker. Compare the cursor's latitude and longitude with `/earth`.
6. Leave and rejoin: the chart should open immediately from the local copy and exploration should persist.
7. Check the HUD minimap: its terrain uses the selected Wmap style, explored areas match Gameplay, and the Wmap player arrow and native waypoint markers stay visible. Face and walk north, east, south and west: both Wmap arrows should point along your movement. Check another player's arrow/color and hover text in multiplayer. F6 toggles the minimap and Ctrl+F6 moves it. Open `.wmap vanilla` and check that the full map retains native terrain and icons. Optionally disable `ReplaceMinimap` and restart to check vanilla HUD terrain and icons.
8. Optionally, on a dedicated server with a second client: the chart should download once (about 12.5 MiB) and not again on reconnect.
