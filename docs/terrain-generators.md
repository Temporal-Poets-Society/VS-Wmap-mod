# Terrain generators other than Earth Map

Status as of 0.3.8 (16 October 2026); compatibility work deferred 17 October 2026, see [Known compatibility issues](#known-compatibility-issues). The Terra Prety facts come from its source on GitHub (`TerraPrety/src/Patch.cs`, read 16 October 2026) and its [mod page](https://mods.vintagestory.at/terraprety). Terra Prety has **not** been installed or run with Wmap. The Cylinder World side is analysed in [Cworld's terrain-mod compatibility](../../Cworld/docs/terrain-mod-compatibility.md). Decisions are recorded in [decisions.md](decisions.md).

## Known compatibility issues

**Status: recorded, deferred** (17 October 2026). Terra Prety support is not being worked on and will not be tested for now. The list below is what a player or server owner would meet; take it up if compatibility becomes a goal. Issues apply to Terra Prety with Cylinder World and without Earth Map, unless stated.

| ID | Issue | Effect in play | Workaround now | Fix if pursued |
| --- | --- | --- | --- | --- |
| TG-1 | **No relief.** Wmap's prediction has invented heights: land at sea level + 10, sea 40 blocks deep | Terrain style shows flat land; the chart has no slope hatching, no 200 m line and one sea depth. Recorded areas show true relief | None needed; it is cosmetic | WM-P01 (record generated columns) |
| TG-2 | **Approximate coastlines.** The mask is Terra Prety's ocean layer at Wmap's vanilla threshold (127), at ocean-map resolution. Where the real coast lands has not been measured | Coasts and islands can be misplaced or missing on unexplored and unrecorded parts of the chart, in Development mode and in gameplay's explored band | `PredictTerrain: false` | WM-P03 (measure), then WM-P01 |
| TG-3 | **Recorded corrections are narrow.** Recordings cover `ObservedRadiusChunks` (4 chunks) around each player's path; gameplay reveals `ExploreRadius` (1536 blocks) | Within the explored band, only a strip about 130–160 blocks either side of the path is true; the rest shows the mask | Raise `ObservedRadiusChunks` (up to 8) | WM-P01 |
| TG-4 | **No far terrain.** It is off for flat predictions | Nothing on the horizon past the view distance | None | WM-P01 or WM-P02 (a source with relief) |
| TG-5 | **The seam coast differs.** If Terra Prety's coast ignores Cylinder's ocean forcing, the real world has a ramp into the seam band; Wmap draws the mask's coast | A straight artificial coast near the seam in the world, not shown on the chart | None | Cworld CW-P02 (changes the world; Wmap follows) |
| TG-6 | **Unverified sampler side effects.** Wmap's cylinder sampler calls the patched `GetOceanMapGen` once per worker thread to build its own ocean layer | Not observed; Terra Prety's prefix only constructs a layer from its config and `requireLandAt` | `PredictTerrain: false` | Confirm in a run with Terra Prety |
| TG-7 | **Terra Prety + Earth Map** is unknown: two mods would shape the same terrain | Unknown | Don't combine them | Out of scope |
| TG-8 | **Terra Prety alone** (no Cylinder World) gives Wmap no prediction | The chart shows recorded terrain only, like any vanilla world. Works as designed | — | WM-P01 would add a shared chart |

## What Wmap reads

| Source | What Wmap gets | Accuracy |
| --- | --- | --- |
| Earth Map (`TerrainModel.GetColumn`) | Ground height, water surface, ice and river flags per column; river centrelines | Exact for Earth's base terrain. Cylinder's polar finishing matched 576 generated columns with 0 mismatches |
| Cylinder World without Earth | Whatever `GenMaps.GetOceanMapGen` returns, thresholded at 127 and combined with Cylinder's `OceanForce`; then Cylinder's finishing passes (seam band, walls, polar ice) | **A land/sea mask only.** Heights are invented: land at sea level + 10, water 40 blocks deep. Resolution is one ocean-map pixel (`RegionSize / noiseSizeOcean` blocks) |
| Recorded terrain (any generator) | Top block, water depth, material and colour from loaded chunks within `ObservedRadiusChunks` (4, so 9 × 9 chunks) of the player, every 4 blocks | Exact for what was loaded, including structures and edits. Coverage is only where players have been |

Wmap never generates chunks for the chart. A prediction is only as good as the provider's model of the generator.

## Terra Prety with Cylinder World (no Earth Map)

Terra Prety patches vanilla world generation with Harmony:

- `GenMaps.GetOceanMapGen` (prefix, replaces it) returns its own `MapLayerOceansSmooth`: Voronoi continents, ignoring landcover.
- `GenMaps.GetLandformMapGen` (prefix, replaces it) returns height-based landforms.
- `GenMaps.ForceRandomLandArea` (prefix, replaces it) and `ForceLandformAt` (prefix).
- `GenMaps.OnMapRegionGen` (transpiler): wider upheaval padding and its `ContinentalUpheavalHandler`.
- `GenBlockLayers.OnChunkColumnGeneration` (transpiler): disables the sea-level-rise term.

It exposes no public height or continent function.

What that means for Wmap:

1. **Coastlines are Terra Prety's, approximately.** Wmap's cylinder sampler calls the patched factory, so its land/sea mask is Terra Prety's ocean layer plus Cylinder's seam forcing. The real coast is decided later, where the patched generator turns the ocean map, landforms and upheaval into blocks. Wmap's vanilla threshold of 127 is an assumption that has not been compared with generated chunks.
2. **There is no relief.** Mountains, valleys, shelves and depths are absent. The Terrain style shows flat land, and the chart has no slope hatching and a uniform 40-block sea.
3. **Far terrain stays off** (0.3.8). The flat mask would have raised flat plates of land on the horizon, including where Terra Prety put sea or mountains.
4. **Recorded terrain corrects the chart** (0.3.8). Where players have been, the recorded terrain draws over the flat prediction at every zoom, so local coasts are true even when the mask is wrong. Its coverage is the recording radius around each player's path (`ObservedRadiusChunks`), much narrower than the explored area that gameplay reveals (`ExploreRadius`): between them the mask is shown.
5. **The chart is rebuilt when Terra Prety changes.** The cache key includes every mod's version, Terra Prety's settings object and Cylinder's `FlattenSeabed`.
6. **The seam band is right.** Cylinder World flattens the band on finished blocks whatever generated them, and Wmap applies the same projection. Where Terra Prety's continents run into the band, the real world has the ramp described in Cworld's analysis; Wmap shows the mask's coast instead.
7. **The server log says so.** With Terra Prety loaded, Wmap names the source "Approximate continents from Terra Prety's ocean map" and logs a warning.

Terra Prety without Cylinder World gives Wmap no prediction provider. Wmap then charts recorded terrain only, as in any vanilla world. Terra Prety together with Earth Map would put two mods in charge of the same terrain; that combination is untested and outside this note.

Set `PredictTerrain: false` in `wmap.json` on the server when the mask is misleading. The world keeps its topology, latitude/longitude, exploration and recordings; only predictions, the world chart and far terrain go.

## Other kinds of terrain mods

| Kind of mod | Wmap with Cylinder World, without Earth |
| --- | --- |
| Landform content packs (`landforms.json`) | Mask follows vanilla's ocean map, so coasts are about right. Heights are invented as always |
| Replaces `GenMaps.GetOceanMapGen` (Terra Prety) | Mask follows the replacement. How the generator turns it into a coast decides the error |
| Rewrites `OceanMap` in its own region handler, or ignores it | Mask is wrong. Use `PredictTerrain: false` |
| Replaces the world type (not `standard`) | Cylinder World adds no terrain; the prediction is wrong. Use `PredictTerrain: false` |

## What full compatibility would need

In order of value. Step 1 is implemented; the rest are proposals, **deferred** with the issues above.

1. **Honest fallback (0.3.8, implemented).** Flat predictions are flagged and far terrain is off for them. Recorded terrain draws over predictions. `PredictTerrain` switches predictions off. The cache key covers what shapes terrain.
2. **Measure (proposal WM-P03, deferred).** Add a Terra Prety + Cylinder smoke profile. Generate chunks along a few coasts, as `GeneratedPolarChecks` does at the poles, and report the share of columns where the mask's land/sea differs from the generated blocks. That number decides whether step 3 or 4 is worth it. It needs a Terra Prety release zip next to the project.
3. **Chart what the generator actually built (proposal WM-P01, deferred).** Generator-independent and exact. On the server, record each chunk column as it finishes generating (its `WorldGenTerrainHeightMap` and the water above it) into a shared "generated" tile layer, drawn over predictions. Pregenerated or travelled areas then show true relief and coasts for every player, and no extra chunks are generated. Costs: a ChunkColumnGeneration handler ordered after every terrain pass, server disk space for the recorded layer, and a policy for gameplay visibility (exploration still decides what a player sees).
4. **A terrain provider contract (proposal WM-P02, deferred).** Wmap finds providers by type name today: Earth's `TerrainModel.GetColumn` and Cylinder's `Params`. A small documented contract would let any generator offer a column sampler: ground, water surface, ice, river, plus capabilities (relief exact or approximate, thread-safe, a revision string for the cache). By the suite's ownership rule, generic terrain APIs belong to Earth, so the contract would be defined there and Earth's model would be its first implementation. Terra Prety would still need an adapter, because it exposes no height function and its relief comes out of the patched `GenTerra`. Reproducing that per column is close to running the generator.
5. **Cylinder World's generator-independent coast (Cworld CW-P02).** This changes the world, not the map. Wmap's projection of the seam band follows automatically while the blend stays inside `SeamParams.SeamGround`, which Wmap calls since 0.3.9.
