# Far terrain: design note

Prepared 2026-10-12 for Wmap 0.3.6. The section "Implemented" at the end records what was built and what was verified.

## Goal

Past the game's view distance, draw a low-detail 3D silhouette of the world out to a configurable distance (default 8 km), so coasts, mountain ranges and islands are visible on the horizon while sailing or standing on a peak. Real chunks remain the only geometry within view distance.

## What Farseer does (for comparison)

From its public mod page ([Farseer](https://mods.vintagestory.at/show/mod/22371), MIT licensed, by badgerson): the server builds 2D heightmaps for regions around each player, generating unexplored chunks to read heights ("chunk peeking", not saved), stores them in a server database and sends them to clients. The client builds one mesh per region, draws it opaque in the opaque pass, raises the camera's far plane, colours it with the game's sky colour so it reads as hazy silhouettes, sinks it near the boundary of real chunks, and optionally bends it down with distance ("globe effect"). Known complaints: generation load and lag spikes on servers, an ugly ring where real chunks start, misplaced terrain with some worldgen mods.

Wmap's situation is different: **the whole world is already sampled.** With Earth Map or Cylinder World, the server bakes one world chart (8192 × 4096 texels of height, coast distance, ice and river distance) once, and every client already downloads it and keeps it on the GPU as a mipmapped, east–west wrapping texture. Far terrain therefore needs **no new server work, no new network traffic and no chunk generation**. Only Farseer's general rendering approach was reviewed (render stage, far plane); no code is copied.

## Approach

One static mesh, displaced on the GPU from the world chart texture that the atlas already uses.

1. **Mesh**: nested square rings around the camera (a clipmap). Ring 0 is a full grid; every further ring doubles both its cell size and its outer radius. The layout never changes during play, so it is built once, off the main thread, and uploaded as one vertex and one index buffer.
2. **Placement**: each frame the whole mesh is placed at the camera, snapped to the coarsest ring's cell size. Every vertex therefore lies on a fixed world grid point of its own ring and samples the same height every frame: no swimming or shimmering while walking. When the snap point moves, ring boundaries step outward or inward by one coarse cell, far out in the haze.
3. **Heights**: the vertex shader reads the world chart's height channel at the vertex's world position, using the mip level that matches the ring's cell size (prefiltered, so distant rings show ranges, not aliased peaks). Water is held at sea level, so **the sea is a flat plane at the real water surface** by construction. Land keeps its sampled height above sea.
4. **Cracks between rings**: a fine ring's outer edge has twice as many vertices as the coarse ring's inner edge. Those in-between vertices carry a "stitch" offset and take the average of their two neighbours' heights, which lies exactly on the coarse edge. The level of detail is a function of position only, so shared vertices agree in both rings. No skirts are needed.
5. **Colour**: the fragment shader samples the world chart again per pixel and uses the atlas's Terrain palette: elevation tints, sea shaded by depth, ice, and Earth Map's rivers as thin water lines from the river distance field. Shading uses the game's sun direction, sun colour and daylight.
6. **Fog and sky**: the colour is blended toward the game's own sky colour along the view ray (the same `getSkyColorAt` the sky and chunk shaders use), increasingly with distance, so the far edge disappears into the horizon at any time of day. Land starts 30 % blended, matching the game's own land fading into the sky there; since 0.3.10 the sea starts unblended, next to the game's water. Since 0.4.2 it takes the sky colour by at most the daylight, as the game's chunks do: at night the sky colour function returns the fog colour, not the dark sky. The game's weather fog (`fogMin`, `fogDensity`, flat fog) is applied on top, so fog and rain hide far terrain together with real terrain.
7. **Boundary with real chunks**: the game fades its own chunks out well inside the view distance `V` (1.22: water from 0.75 `V` to 0.80 `V` in `chunkliquid.vsh`, land from 0.80 `V` to 0.85 `V` in `chunkopaque.vsh`, further for terrain above the eye). Since 0.3.10 far terrain starts at 0.75 `V`, where water begins to fade, and fragments nearer than that are discarded. From there the far terrain sits low, fading to its true height over 512 blocks: land by up to 12 blocks but never more than a block below the sea, and the sea one block under the real water surface (a sea-level water block's surface is at `SeaLevel` − 1/8). Real terrain reaching further covers it instead of z-fighting, and fading real water shows the far sea right behind it. Until 0.3.9 far terrain started at `V` − 24 and sat 12 blocks low there, so fading water showed far terrain kilometres away (a pale band at the edge of the real sea). Unloaded chunks beyond 0.75 `V` show far terrain; nearer ones show the sky, as without Wmap.
8. **Far plane**: the game's camera far plane is 3000 blocks. Wmap raises it to the far terrain distance plus a margin while far terrain is on, and restores it when the world is left.
9. **Seam**: texture coordinates repeat east–west, so the mesh crosses Cylinder World's seam without special cases. Beyond the north and south edges of the world nothing is drawn.
10. **Sea colour**: by default the atlas's Terrain sea colours. If the Sea mod's `SeaSystem` exposes a member named `FarWaterColor` (RGB as `float[]`, `Vec3f`/`Vec4f`, or an `int` 0xRRGGBB), Wmap uses it instead, read by reflection every few seconds. Sea does not expose one yet; this is an optional hook, not a dependency.

## Mesh resolution per band

The detail setting picks ring 0's cell size; each ring holds 64 cells from its inner to its outer edge on every side (128 × 128 for ring 0).

| Detail | Ring 0 (cell / radius) | Ring 1 | Ring 2 | Ring 3 | Ring 4 |
| --- | --- | --- | --- | --- | --- |
| 0 low | 64 / 4096 | 128 / 8192 | 256 / 16384 | | |
| 1 normal (default) | 32 / 2048 | 64 / 4096 | 128 / 8192 | 256 / 16384 | |
| 2 high | 16 / 1024 | 32 / 2048 | 64 / 4096 | 128 / 8192 | 256 / 16384 |

Rings beyond the chosen distance are not built. The world chart has the ring's length / 8192 blocks per texel: about 25 on a 204,800-block ring (Earth at 1:200), 49 on Earth 2.0's default 1:100 ring (400,384 blocks) and 489 at 1:10 (WM-0024), so cells finer than that only interpolate; "high" mainly helps smaller worlds and makes the first kilometre smoother.

## Data source and transfer

Only the world chart, which the client already has. No new packets. Consequences:

- Worlds without Earth Map or Cylinder World have no world chart, so they get no far terrain (the atlas draws them from recorded terrain only). Drawing recorded detail tiles as far terrain is a possible follow-up. Drawing predicted detail tiles near the player, out to 1536 blocks, is implemented since 0.4.0; see [far-terrain-near-band.md](far-terrain-near-band.md).
- Since 0.3.8, Cylinder World without Earth Map gets no far terrain either: its chart is a land/sea mask with invented heights (`AtlasInfo.FlatPrediction`), which drew flat plates of land on the horizon, and with another terrain generator (Terra Prety) could put them where the world has sea or mountains. See [decisions.md](decisions.md) WM-D12.
- Far terrain shows the predicted world, not structures or player edits. With the full map it shows whatever is on the horizon. A player without it (survival, from 0.5.4, WM-D63) gets the prediction only of land they have charted or have in their line of sight out to 8192 blocks (`ExplorationServer.FarSight`), which the server casts; the rest of their chart is empty, and far terrain draws only the known texels (`wmapKnown`).

## Cost

At the default (8192 blocks, normal detail): 3 rings, about 42,000 vertices and 82,000 triangles, drawn with **one draw call** per frame. Buffers: about 0.7 MB vertices + 1.0 MB indices on the GPU; no new textures (the world chart is shared with the atlas). CPU: building the mesh once takes a few milliseconds on a worker thread; per frame only uniforms are set. GPU: each vertex reads the chart 7 times (height, 4 for the normal, 2 for stitched edges); each covered pixel reads it 2 times. On the developer's RX 6600 this is far below 1 ms; on integrated graphics it is comparable to drawing a few hundred chunk faces. Low detail at 16 km is about the same size.

## Risks

- **Raising the far plane** reduces depth precision: at 0.1 near plane and 24-bit depth, depth steps are about 1.3 blocks at 1.5 km and 40 blocks at 8 km. Real chunks are unaffected in practice; distant ridges may flicker against each other at 12–16 km, where they are mostly haze. Other mods that read `zFar` (SSAO-like effects) see the larger value, as with Farseer.
- **Farseer installed too**: both would draw far terrain. Wmap detects Farseer and stays off, with a log line.
- **Coarse heights near the boundary**: chart texels (25 to 489 blocks by the world's scale) smooth cliffs and narrow valleys; a coarse hill may rise slightly above a real valley at the boundary. The push-down and the sky blend hide most of it.
- **Lakes** sit at their bed height (the chart stores the bed, not the lake surface), so high lakes look slightly sunk.
- **The game's shader includes** are used for sky colour and fog. If a future game version renames them, the far-terrain shader fails to compile; Wmap logs it and leaves far terrain off, the atlas is unaffected.
- **Heights above 256 blocks over sea** are clamped by the chart's encoding.

## Implemented

As designed above, in `src/Core/FarMesh.cs` (layout), `src/Client/FarTerrainDraw.cs` (GPU buffers and uniforms, one draw call), `src/Client/FarTerrain.cs` (game renderer at opaque order 0.36, far plane, Farseer detection, Sea colour hook) and `assets/wmap/shaders/farterrain.vsh/.fsh`. The default mesh measured 3 rings, 41,985 vertices, 81,920 triangles and 1.6 MiB. Unit tests prove the rings tile without gaps or cracks; the GPU smoke check compiles the shader with the game's includes for 24 settings combinations and renders coast, downward and seam views. The in-game pipeline is not yet verified; see [testing](testing.md).

Since 0.4.0 the near band and climate tiles extend this: see [far-terrain-near-band.md](far-terrain-near-band.md#implemented-040).

When the camera crosses Cylinder World's jump line, the mesh keeps its world grid only if the ring circumference is a multiple of the coarsest cell (256 blocks by default; true for the standard 204,800-block ring); otherwise the far terrain shifts slightly once at the teleport.

## Since 0.5.3 (WM-D54 to WM-D61)

The first steps of the [far view plan](../../overview/proposals/2026-11-13-world-map-far-view-plan.md) (suite D70: World Map is the suite's only far terrain). Far land is lit with the game's own numbers for its chunks: the face's shade from the game's light with chunkopaque.vsh's floor, the shadow map's strength and clear-day boost (`shadowIntensity`), and full sunlight at the world's sun-light level (0.866 in vanilla); before, far land was about 6 % too dark on clear days with shadows on and up to 15 % too bright under cloud. In the near band each pixel's slope comes from the band's four-block heights with the forest canopy, so relief between the mesh's vertices and forest edges are shaded. Where Earth's climate is known, high ground is coloured by its climate (scree, rock, snow) instead of the atlas's height palette. Recorded forests stand at the canopy height the recorder saw (see [mid-distance-view.md](mid-distance-view.md)). `.wmap fardebug albedo|lit|off` shows the colour alone or lit without haze, to compare with the game's ground on screen.

0.5.3 (WM-D55): far grass and leaves are coloured as the game colours them: the climate map, the season map at the game's `seasonRel` and colormap.vsh's weight between them, with each place's grass season map and wet season from Earth (`GrassSeasonAt`; biome tiles format 102 carry them below the biome). From 0.8 of the view distance far terrain takes the depth of the point at 0.8 along its ray and so covers the game's blocks where the game fades them into the sky (out to 0.95); nearer than that it draws only over columns not loaded, its push-down is gone by 0.8, and land haze starts at none.

0.5.3 (WM-D56): far terrain is drawn in three passes before the game's chunks: depth only; colour where that left the surface nearest (no depth writes); the distant lights; then the cover, the depth of the point at 0.8 of the view distance along every ray to far land past it and below the clouds' underside. The game's blocks from there on lie behind it at any distance. Grassy soil is recorded as the game draws its top.
