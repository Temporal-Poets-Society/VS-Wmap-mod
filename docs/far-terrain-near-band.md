# Far terrain near band: design note

Prepared 2026-10-17 after 0.3.10 as proposal WM-P04. **Phases 1 and 2 are implemented in 0.4.0**; see [Implemented (0.4.0)](#implemented-040) at the end for what was built, what changed from this design, and what was measured. Phase 3 stays open. The sections before it are the original design; measured numbers are marked as measured, the rest are estimates.

## Question

Can a player see out to about 1536 blocks for much less than the cost of a 1536-block view distance? The idea: real chunks only to about 512 blocks, then something that reads as real terrain to about 1536, then Wmap's existing far terrain to the horizon.

## Where things stand

- **Real chunks.** At view distance `V` the game draws water to 0.75–0.80 `V` and land to 0.80–0.85 `V`, where it fades into the sky (see [far-terrain.md](far-terrain.md) item 7). At `V` = 512, land therefore ends at about 410–435 blocks.
- **Far terrain** (0.3.6, joined to the sea in 0.3.10) starts at 0.75 `V`. It is drawn from the world chart: about 25 blocks per texel on Earth's 204,800-block ring. Hills nearer than about 2 km look smooth and blobby, coasts are rounded, and valleys narrower than about 50 blocks vanish. The `FarTerrainDetail` setting only makes the mesh finer. Below 25 blocks it just interpolates the same chart.
- **A larger world chart is not the answer.** At 16,384 × 8,192 the download grows from 12.5 MiB to about 50 MiB, and graphics memory from about 170 MiB to about 680 MiB with mipmaps, for every player, everywhere. Detail is needed only near the player.

## Why a 1536 view distance is expensive in vanilla

Chunks are loaded in a disc. Going from 512 to 1536 raises the radius from 16 to 48 chunks, and the number of columns from about 800 to about 7,200: **9×**. Each column is 12 chunks tall on a 384-block world. Nearly every cost grows with that area:

| Cost | Why it grows 9× |
| --- | --- |
| Server generation | Each new column runs full worldgen: Earth terrain, rivers, Cylinder's seam, decorators, trees, structures. Earth's terrain sampler runs one thread at a time behind a lock (below), so this queues. |
| Network | Every chunk is sent compressed, including the parts that are never visible from far away. |
| Client memory | Block, fluid and light data for every loaded chunk, plus mesh copies in the pools (`modelDataPoolMaxVertexSize` 500,000 vertices per pool). |
| Tessellation | The client's CPU meshes every chunk, and meshes it again when a neighbour or the light changes. |
| Graphics memory and frame time | Chunks are drawn at full detail: every visible block face is a quad. The game's level of detail only drops or swaps small decoration (`Lod0`, beyond `min(640, V) × lodBias`, 0.33 by default). It never simplifies terrain. Trees and leaves dominate on land. Shadow passes (`shadowMapQuality` 2 renders near and far shadow maps) draw the geometry again. |

Even then, the game's own fade means a 1536 view shows real land only to about 1230–1300 blocks.

Absolute numbers (GB of RAM, frame time) were not measured here. The plan below measures them in the game.

## What a near band needs, and what Wmap already has

**Heights at about 4 blocks per sample, over a 3072 × 3072 square around the player, ready within a few seconds and kept up to date while travelling.**

### Data sources

| Source | Covers the band? | Notes |
| --- | --- | --- |
| **Earth predicted detail tiles** (`SemanticTile`, layer 0) | Yes, anywhere on Earth worlds | The server path already exists (`TileRequest` → `AtlasServer.Work` → `SemanticTile.Sample`, disk cache, `ShareWorldChart` permission). Earth's prediction is the worldgen terrain height: the smoke run compares generated chunks with it and finds 0 height error (768 columns, 12 places). It holds ground and water, not trees, structures or player edits. |
| Recorded terrain (layer 1) | Only where the player has been | The recorder samples a 4-block grid within 4 chunks of the player (`ObservedRadiusChunks`). It could record every loaded chunk out to `V`, but that still ends inside the band. Useful later for worlds without a prediction; not a source for the band. |
| Cylinder World without Earth | No | Its chart is a land/sea mask with invented heights (WM-D12): no relief to show. |
| Farseer-style chunk peeking | Yes | The server generates the chunks to read heights: the very cost the band avoids. Rejected. |

### Measured: Earth sampling cost

From a throwaway probe in the Earth smoke profile (RX 6600 machine, 2026-10-17, [raw output](test-results/near-band-probe-0.3.10.txt)). Each figure is the time to sample and pack the predicted tiles that cover 3072 × 3072 blocks around three places:

| Place | Level 2 (4 blocks/texel): 49 tiles | Level 3 (8 blocks/texel): 16 tiles |
| --- | --- | --- |
| Nice coast | 2.1 s, 42.5 ms/tile | 0.69 s, 43.0 ms/tile |
| Alps | 2.1 s, 43.7 ms/tile | 0.69 s, 43.3 ms/tile |
| Open Atlantic | 1.7 s, 34.9 ms/tile | 0.57 s, 35.7 ms/tile |

- **Four threads are no faster than one** (2.2–2.3 s for level 2). Earth's `EarthData.GetTile` lock serialises sampling, as found in the 0.3.0 bake. Throughput is about 0.7–0.9 million samples per second.
- Each 128 × 128 tile samples 178 × 178 points: a 24-texel margin keeps coast distance exact across tile edges. That is 1.9× the samples a tile strictly needs. Heights alone would not need the margin.
- **Packed tile size**: 5–8 KiB on average (from the same run's cache). The whole level-2 band is about 0.4 MiB per player, and the server's disk cache serves later requests.
- **While travelling**: crossing one 512-block tile brings a new row of 7 level-2 tiles, about 0.3 s of server work per 512 blocks. At 10 blocks/s, for example, that is once every 50 s.

For comparison: if Earth's worldgen samples terrain at the same rate (not measured), the 6,400 extra columns of a vanilla 1536 view need about 6.6 million terrain samples, about 9 s of the same serialised work, before any other worldgen.

## Rendering options

The band covers about 384 to 1536 blocks. One block spans about 1.5 pixels at 500 blocks and about 0.5 pixels at 1500 (1080p, 70° vertical field of view), so block-level detail matters only near the inner edge.

### Option A: finer clipmap rings sampling a detail texture (recommended)

- **Data on the GPU**: one 1024 × 1024 RGBA8 texture covering 4096 × 4096 blocks at 4 blocks per texel: 8 × 8 slots of 128-texel tiles, addressed toroidally, so moving only uploads new tiles. 4 MiB, 5.3 MiB with mipmaps. Same channels as the chart (coast distance, height, ice, coverage), so the existing colouring applies.
- **Mesh**: the existing `FarMesh` with a finer first ring: 8-block cells to 512, 16 to 1024, 32 to 2048, then today's rings to 8192. Five rings: about 67,000 vertices and 131,000 triangles in one draw call, about 2.6 MiB. Today's default mesh has 42,000 vertices and 82,000 triangles.
- **Shader**: the vertex shader reads the detail texture where it covers the vertex and blends to the chart height by distance and coverage, between about 1200 and 1536. The blend depends on position only, so vertices shared by two rings agree, as for today's stitching. The fragment shader takes coast distance and height from the same place.
- **Cost (estimate)**: two extra texture reads per vertex and per pixel. Far below a millisecond on an RX 6600, to be measured with the existing `wmap-far-terrain` profiler entry. CPU: decoding and uploading one 64 KiB tile now and then.
- **Look**: smooth, sun-shaded 4-block relief. Cliffs, valleys and coasts are in the right places at the right heights. Not blocky.

### Option B: blocky terraced columns (Distant Horizons style)

Draw each cell as a flat top at a whole-block height, with vertical sides where a neighbour is lower: instanced cells, or tile meshes with flat areas merged.

- **Cost (estimate)**: 4-block cells to 768 and 8-block cells to 1536 make about 258,000 cells. At up to 10 triangles each, that is up to about 2.6 million triangles before flat areas are merged. That is the same order as vanilla's own terrain at 512. Mesh memory is tens of MiB unless built on the GPU.
- **Look**: matches voxel chunks at the join. Beyond about 800 blocks the steps are under a pixel and look like Option A.
- **Verdict**: Option A first. Terraces only near the inner edge, if the join still reads wrong in game.

## The joins

- **Inner edge**, unchanged from 0.3.10: start at 0.75 `V`, with the sea one block under the real water. With heights exact to 4 blocks, the land push-down (12 blocks today) can drop to about 4.
- **Two problems remain that the band does not solve on its own:**
  1. **Trees.** Predicted heights are bare ground. Forests in real chunks stand 5–15 blocks higher, so a forest edge steps down at the join. Earth's worldgen uses `TerrainModel.ForestPotential(x, z)` (called in `EarthWorldGen.cs`). Sampling it into the tile (a spare channel or a second texture) could raise a soft canopy and tint it dark green.
  2. **Colours.** Far terrain uses the atlas's height palette. Real chunks use climate-tinted grass, leaves and water. Earth's worldgen fills the game's climate map from `TerrainModel.ClimatePixel` (temperature and rain), and the game tints plants and water from that through its colormaps (`planttint`, `watertint`). Sampling climate into the tile would let the band, and the far sea, use the game's own tints.
- **Land fading into the sky.** Real land between 0.80 and 0.85 `V` is mixed toward the sky colour by `chunkopaque.fsh` (`haxyFade`), so a pale rim stays between real land and far land. Removing it means patching the game's chunk shader as it loads, as Sea already does for `chunkliquid` (`Sea/src/Water/WaterShader.cs`). Possible, but it touches every chunk and may clash with other shader mods. Last, and optional.
- **Outer edge**: the band fades into chart heights over about 1200–1536. The ring layout moves in 256-block steps, so the blend zone can jump slightly. That happens about 1.3 km out, in haze.

## Server and protocol work

- **A separate request stream.** `AtlasServer.Request` keeps one `View` counter per player and drops older requests as obsolete. Near-band requests through the same counter would cancel the atlas's requests, and the other way round. The band needs its own view number (a new `TileRequest` field, or a separate packet), its own per-player cap (today 12 outstanding, a 64-job queue shared by everyone), and ordering nearest first.
- **Priority under load.** Sampling is serialised by Earth's lock, so a full band for several new players at once queues for seconds. Order requests nearest first, and let the disk cache serve players near each other.
- **Optional: heights-only tiles.** Dropping the coast-distance margin halves sampling, but coast distance then has to come from the chart or be computed on the client. Measure first.
- **Permissions.** As for far terrain today: only with the world chart shared (`ShareWorldChart`) or the `wmap` privilege. It shows the predicted world and ignores exploration, like sight.

## Recommendation

1. **Phase 1, Option A on Earth worlds.** A dedicated near-band request stream for level-2 predicted tiles, nearest first. A toroidal 4 MiB detail texture. A finer first ring in `FarMesh` and a detail-or-chart height choice in the far-terrain shaders. Reduce the land push-down inside the band. Off without a prediction with relief, as far terrain is today.
2. **Phase 2.** Forest canopy and climate colours from Earth's `ForestPotential` and `ClimatePixel`, sampled into the tiles. Climate also gives the far sea the game's water tint. This needs a tile format change (version bump, cache rebuild).
3. **Phase 3, only if the join still reads wrong in game.** Terraces near the inner edge (Option B), and an optional patch of the game's land fade into far terrain instead of the sky.

**Not recommended:** a vanilla 1536 view distance on Earth worlds, Farseer-style chunk peeking, or a bigger world chart.

**Practical setting today:** view distance 512 with far terrain on. Raising `FarTerrainDetail` above 1 does not add detail: the chart, not the mesh, is the limit.

## Verification plan

**Core suite (no game)**
- The near-band ring layout tiles without gaps or cracks, extending the `FarMesh` tests to an 8-block first ring.
- The toroidal tile placement keeps each tile in one slot while the player moves and wraps across Cylinder's seam.
- The blend weight depends on position only.

**Smoke, Earth profile**
- **Heights**: render the band with the GPU harness, read depth back to world heights at cell centres between 384 and 1536, and compare with Earth's sampler: within 2 blocks on land and coast.
- **Against generated chunks**: compare the band with generated chunks' rain-height maps at a coast, a mountain and an island, like the polar and seam column checks.
- **Joins**: keep the 0.3.10 sea-edge check. Add a land-edge check: the far land is under real land between 0.75 and 0.85 `V`. Add an outer-edge check: no undrawn pixels below the horizon, and depth rises steadily across 1200–1536.
- **Server**: band requests do not make atlas requests obsolete, or the other way round. Caps hold under a flood. Three players are served fairly, nearest tiles first. The disk cache is reused.
- **Cost**: per-tile sampling time (measured 35–47 ms here), first-fill time for one and three players, and draw time with a GPU timer query.

**In game (by hand)**
- Same save and place, at 512 with the near band and at vanilla 1024 and 1536. Note process memory and dedicated graphics memory (Task Manager), frame time, and how long the view takes to fill after a teleport. Screenshots at a coast, a mountain range and a forest edge.

## Implemented (0.4.0)

Built 2026-10-17: phases 1 and 2. An adversarial Codex review of the implementation plan (12 findings) changed several points before coding, and two review rounds of the implementation found seven defects, all fixed with checks that fail on the earlier code where it could be shown; those points are marked *(review)*. Decisions WM-D22..WM-D26 in [decisions.md](decisions.md).

**Server** (`AtlasServer`). Far requests are `TileRequest.Stream` 1 with `Content` 1 (semantic and biome tiles, level 2: the band) or 2 (a biome tile, any level: climate). They have their own view and a cap of 8 per player. *(review)* Instead of sharing the atlas's queue with priority, one dedicated worker takes them, players in turn, so the atlas's workers are never busy with far terrain and one player's band cannot hold up another's. Two requests for the same tile, from either stream or any player, share one sampling. Work for a connection that has left is neither sent nor counted. Only allowed stream, content and level combinations are queued. A client sends far requests only to a server that announces `AtlasInfo.FarTiles`; old peers read the new messages as before.

**Biome tiles** (`BiomeTile`, `WorldSource.EarthBiome`). R forest (Earth's `ForestPotential`), G and B the stored temperature and rain of Earth's climate-map pixel (`ClimatePixel`, with the world's `globalTemperature` and `globalPrecipitation`, as Earth's worldgen reads them). Sampled every 16 blocks in the band and every second texel (128 blocks) at level 6, then interpolated; neighbouring tiles agree exactly where they overlap. *(review)* Cached as `b-<identity>-…`, the identity hashing the format and those settings. Measured cold cost: 2 ms per band tile, 18 ms per climate tile; the semantic tile is still 41–49 ms.

**Server manifest.** *(review)* The manifest a player receives carries `FarTiles`, `Biome` and `BiomeId`; the first implementation built it without them, so a real client never turned the feature on while every check passed. A smoke check now enables far terrain's tiles from the served, serialised manifest.

**Client** (`FarTiles`, `FarTileWindow`). Two windows of tiles around the camera, nearest first, in 8 × 8-slot texture atlases (1040 × 1040 RGBA8 each: band semantic, band biome, climate biome; 12.4 MiB): the band at level 2 to `NearBandDistance` (at most 1536, so its window always fits the slots, also with a partial seam tile), the climate at level 6 to the far terrain's distance. Slots are toroidal by unwrapped tile id. *(review)* Laps are counted from one the client keeps across seam crossings (`OriginLap`, also sent to the shader), so crossing the seam leaves loaded tiles in their slots even when tiles per lap is not a multiple of 8. Tiles wholly inside the real terrain are not requested. Decoding and the disk cache (the atlas's folder) run on a worker; the render thread uploads two tiles per frame. *(review)* Texture creation, uploads and the tint textures keep the GL state they found (texture binding, unpack buffer, alignment, row length, skips). New tiles fade in over 1.5 s.

**Rendering** (`farterrain.vsh/.fsh`, `shaderincludes/wmapfartiles.ash`). With the band on, ring 0 has 8-block cells: 8/512, 16/1024, then the normal rings from 32/2048 (5 rings to 8192, 67,329 vertices). *(review)* The vertex shader computes one final height per position (chart and band blended by the band's weight, forest canopy, push-down) and stitched vertices average exactly that at their coarse edge's ends. The band's weight is its coverage × readiness × the fade toward the band's edge (from 1200 to 1536 blocks). *(review)* Readiness is one continuous field: every tile within 48 blocks of a point scales it by `mix(smoothstep(0, 48, distance to the tile), 1, the tile's readiness)`, its own tile at distance 0. Per-edge factors, the first version, stepped by 46 blocks beside a missing diagonal tile. Band and climate biomes blend premultiplied by their own readiness, continuous as either goes to zero. *(review)* Canopy (8 blocks × forest) grows only on land at least 2–10 blocks from the coast and off ice, so lakes and coasts stay bare. The fragment shader works out the band, its weight and the biome again per pixel. Since 0.4.1 water is drawn only where the drawn surface lies at the sea (a mesh cell running up a cliff is a ramp; its part above the water is shore), and steep faces are bare rock. Colours: grass `0.6 × planttint`, canopy `0.4 × planttint`, rock and snow above as the atlas draws them; water `0.55 × watertint + 0.09`, slightly darker over deep water; tint coordinates by vanilla's `GetAdjustedTemperature` and `GetRainFall` at the ground's height (not the canopy). Sea's `FarWaterColor` still wins. Push-down near the start is unchanged (12 blocks of land, 1 of sea).

**Measured** (Earth smoke profile, RX 6600; [results](test-results/earth-0.4.0.txt)):

| | 0.3.10 (chart) | 0.4.0 (band) |
| --- | --- | --- |
| Land heights against Earth's terrain, 900–1180 blocks from the camera above Nice: median / 95 % | 1.75 / 9.02 blocks | 0.38 / 1.07 blocks |
| GPU time per draw | 0.10 ms (0.3.10 mesh) | 0.31 ms (0.11 ms for the finer mesh from the chart alone) |
| A full band and its climate tiles from cold | — | 49 + 13 tiles, 43–49 ms each on the server |

**Not built, or limits that remain:**
- Phase 3: terraces near the inner edge, and patching the game's land fade into the sky. Decide after play-testing.
- Forest is Earth's potential, not the trees actually generated (Earth adds vanilla's forest noise); canopy is a smooth 8-block layer, evergreen-dark, without seasons.
- Lakes and rivers inherit the chart's convention: the semantic height of water is its bed, so high lakes look sunk.
- Colours are an approximation of the game's lighting and translucent water, tuned against one screenshot; the in-game look is not verified.
- Earth worlds only. Cylinder World alone (a flat land/sea mask) and plain worlds keep 0.3.10's behaviour.

**To check in game:** the screenshot's place at view distance 512 (the far sea against the real water), a mountain coast (shape of the hills just past the real chunks, no steps or cracks), a forest edge (canopy height and colour), the seam, and `client-main.log` for `[wmap] Far terrain tiles: near band on, climate on`.
