# Mid-distance view: design note

Prepared 2026-10-23 as proposal WM-P06, from a discussion with the user. A **prototype is built** on its own branch (0.5.0-pre.1; see [Implemented (prototype)](#implemented-prototype) at the end for what was built, what changed from this design and what was measured) and is **not merged into main until it has been tested in game** (the user's condition). The sections before it are the design as proposed; their figures are estimates.

## Question

Far terrain (0.3.6) and its near band (0.4.0) show the predicted world: right at the horizon, smooth and blobby at mid distance, and without player structures, worldgen structures, trees or lights. Real chunks show everything, but their cost grows with the square of the view distance (see [far-terrain-near-band.md](far-terrain-near-band.md#why-a-1536-view-distance-is-expensive-in-vanilla)). Can the space between the real chunks and about 1500 blocks look like the real world, structures and lights included, for much less than real chunks cost, so the real view distance can be smaller?

## The idea in three parts

1. **Recorded terrain drawn in 3D ("fake chunks").** Once a chunk has existed, Wmap's recorder already knows its surface: a sample every 4 blocks (height, water depth, kind, game colour) and every block where the world differs from the prediction (structures, edits, missed shores; WM-D33). Today that only feeds the 2D atlas. Draw it as far terrain, over the prediction, out to the near band's 1536 blocks.
2. **Distant lights.** Block light lives in chunk light data, so lights vanish with their chunks. Voyage's harbour beacon is ordinary block light, which [does not provide](../../Voyage/docs/lighthouse-beacon.md#performance-and-engine-limits) a distant sprite or visibility beyond unloaded chunks. Draw lights beyond the real chunks as glow sprites, so a lighthouse can be seen and recognised at sea without GPS.
3. **Separate what is seen from what is generated.** Seeing far needs no generated chunks; generating is only needed where the player can be. With parts 1 and 2 the real view distance can drop (512 → 256 means 4 × fewer columns generated, sent, stored and meshed), and the space beyond is drawn from prediction, recordings and lights.

### Not proposed: surface-only generation

Vintage Story generates whole columns: every `ChunkColumnGeneration` handler receives the full column (Earth's own `BareRock` indexes `request.Chunks[y / 32]`), passes wait for neighbouring columns, sunlight is spread down the whole column, and caves, ore hints and underground structures cross the surface. Generating the top now and the rest later would mean patching the engine's chunk supply, teaching every worldgen mod about half-built columns and saving half-built columns. Part 3 gets most of the saving with vanilla saves and no engine patch. Underground stays full and vanilla, generated where the player goes.

## Bands

| Distance | Drawn from | Detail |
| --- | --- | --- |
| 0 – about 0.8 `V` | Real chunks | Full |
| 0.75 `V` – about 768 | **Terraces**: recorded cells as flat tops at whole-block heights with vertical walls; prediction where nothing is recorded | 1–4 blocks, finest where structures were recorded block by block |
| about 768 – 1536 | The near band's smooth mesh, with recorded heights and colours over the prediction | 4 blocks per texel |
| 1536 – horizon | World chart (unchanged) | Chart |
| 0.75 `V` – horizon | **Distant lights** over all of the above, hidden by them | Points |

One block spans about 1.5 pixels at 500 blocks and 0.5 at 1500 (1080p, 70° vertical field of view), so steps matter only near the inner edge; past about 800 blocks terraces and smooth relief look alike. A 10-block house at 1000 blocks is about 7 px wide: a coloured lump at the right height.

## Part 1: recorded terrain in far terrain

### Data

- **Source, prototype:** the client's own recordings (`ObservedTerrain`, layer 1 tiles). `TileData` keeps 16-bit heights and depths, kind and the game's RGBA colour per sample, so whole-block heights and real colours are available. The near band's semantic texture is not enough for this: its heights are 8-bit on a square-root curve (steps of about 3–4 blocks at 200 blocks above sea).
- **Coverage:** the recorder covers `ObservedRadiusChunks` (4, at most 8) around the player's path. For the view it must record **every loaded chunk**, out to the view distance, within its existing per-frame budget. Coverage is then the player's own past: places they have been near. On a first visit the prediction is shown, which is exact for terrain on Earth worlds; there are no player structures in never-generated land anyway.
- **Source, later: WM-P01** (server-side generated-terrain layer). The server records columns as they finish generating and as blocks change, and sends them like prediction tiles. It covers everything ever generated within reach of any player, other players' builds included. Not in the prototype.
- **Trees:** the recorder skips trees and plants for heights. Far terrain already raises an 8-block canopy from Earth's forest potential; recorded cells keep that canopy where the prediction has forest, until a recorded canopy height exists. A recorded canopy channel is optional for the prototype.
- **Freshness:** recordings follow the world where the player passes (0.4.4: removed structures disappear, edited water beds count). Other players' changes appear only after the player passes again (or with WM-P01).

### Rendering

- **Smooth band (768–1536):** recorded height and colour go into band textures next to the predicted ones, with a coverage channel, and the shader takes recorded over predicted where covered. Same mesh, same single draw call.
- **Terraces (inner edge to about 768):** a cell is a flat top at a whole-block height with vertical walls to lower neighbours (near-band Option B). Built on the GPU from the textures (flat cells with walls between them), so moving needs no CPU meshing; only new data needs uploads.
- **Colour:** the recorded game colour, lit by the game's sun and daylight as far terrain is now; water keeps far terrain's water colours (and Sea's `FarWaterColor`).
- **Joins:** unchanged inner edge (starts at 0.75 `V`, pushed down a little). With exact recorded heights the push-down can be small on recorded land.

### Limits

One height per cell: no overhangs, bridges, arches or cave mouths. Thin things (masts, poles, fences) vanish or become lumps. Flat map colours, not textures. Ships, entities and dropped items are not recorded.

### Cost (estimate)

Terraces at 2-block cells from 384 to 512 and 4-block cells to 768: about 150,000 cells, up to about 1 million triangles with walls. To be measured; budget **1 ms** GPU on the RX 6600 for all far terrain. Recorded textures at 1 block per texel near and 4 beyond: tens of MiB at most. Recording every loaded chunk: within the recorder's existing 1 ms frame budget, spread over more frames.

## Part 2: distant lights

- **What:** a glow sprite at each light's position, with a minimum on-screen size, a soft halo and brightness falling with distance. Depth-tested against real chunks, terraces and far terrain, so land hides it. The game's weather fog applies: lights vanish in fog and rain. The planet's curve (WM-D39) applies: a light sinks below the horizon like the ground. On a 256,000-block ring, Lisbon's beacon 42 blocks above the sea is visible from a deck 5 blocks up to about 640 + 1,850 ≈ 2,500 blocks (√(2Rh) each), beyond the fake-chunk band.
- **Ordinary lights (prototype):** recorded with the terrain. Where a recorded sample's surface (or the block above it) has block light above a threshold, the cell is lit; lit cells merge into a few points per chunk with a brightness. Towns read as clusters of glints at night. Lights deep inside buildings are not seen, as from outside.
- **Beacons (later, cross-mod):** a recorded light has no rhythm and is only known where the player has been, which is no use for finding an unknown coast. Beacons need a small **server-side registry** (position, height, port, mode) sent to clients within range, and the client animates the rhythm from the pattern and the shared server clock, as Voyage's beacon already computes its phase. Voyage owns the beacons; Wmap would read the registry through an optional hook, as it reads Sea's `FarWaterColor`. **This interface is a proposal and needs agreement with Voyage**, whose runtime boundary is itself still a proposal.
- **Daylight:** lights fade out by day and grow at dusk.
- **Cost (estimate):** hundreds to a few thousand points in one instanced draw: negligible.

## Part 3: smaller real view distance

No code: a setting and a test. With parts 1 and 2 in place, play at `V` 256 and 384 and compare with 512 and 1024 without them (frame time, memory, how quickly the view fills after a teleport, and the look).

Other parts of the world that are known without generating could later be drawn as silhouettes too: Earth Ruins' authored landmarks (positions and footprints are authored, the Lisbon lighthouse included). A cross-mod proposal, not part of the prototype.

## Prototype

Built on its own branch in its own worktree, **not merged into main until it has been tested in game**:

1. The recorder records every loaded chunk (not only `ObservedRadiusChunks`), and the light level at its surface samples.
2. Recorded heights, colours and coverage reach far terrain's textures in the near band's window; the shader draws them over the prediction.
3. GPU terraces from the inner edge to about 768.
4. Distant lights from recordings: sprites, depth, fog, curve, daylight.
5. Settings to switch each part on and off, so the in-game test can compare.

Out of the prototype: WM-P01 (server recording), the beacon registry and rhythm, landmark silhouettes, any change to generation.

## Verification plan

**Core suite (no game):** recorded-over-predicted choice per cell; terrace heights are whole blocks and walls close (no gaps between cells); light merging per chunk; the window keeps recorded tiles in their slots while moving and across the seam.

**Smoke, Earth profile:** record a built-up place (a story location or a placed structure), render it from 600 and 1200 blocks with the GPU harness, and compare depth-read heights with the generated chunks' rain heights (within 1 block on recorded cells); a lit structure at night shows a light point that is hidden behind a hill and below the horizon at the curve's distance; draw time with the existing `wmap-far-terrain` profiler entry.

**In game (by hand, before any merge):** the same save at `V` 256, 384 and 512 with the mid-distance view, and 512 and 1024 without it. Frame time, process and graphics memory, view fill time after a teleport; screenshots at a town, a coast, a forest edge, and at night with lights; a lighthouse seen from a ship at sea.

## Implemented (prototype)

Built 2026-10-23 on its own branch (`claude/mid-distance-view-41ba01`, version **0.5.0-pre.1**), by a Claude Code session the user's main session started. **Not merged into main**: the user tests it in game first. Everything below is the prototype's state; the sections above are the design as proposed.

### What was built

**Recorder** (`ObservedTerrain`). With `MidDistanceRecorded` it records every loaded chunk out to the view distance (at most 48 chunks), not only `ObservedRadiusChunks`: the chunks are scanned nearest first, a slice per frame within the existing 1 ms and 256-sample budget, resuming where it stopped and resting a second when nothing is due. Each sample also keeps the game's colour (`Block.GetColor`, as the game's map takes it) and the block light at the column's top (the brighter of the top block and the one above, `EnumLightLevelType.OnlyBlockLight`). Both go into the tile's existing colour field (`RecordedColor`: 24 bits of colour, a flag and 5 bits of light), so the tile format and the protocol are unchanged and 0.4.11 reads these tiles and paper maps. A colour alone is still no change (WM-D29); a first colour, or a light coming on, going out or changing by a step of 4, is.

**Recorded tiles on the GPU** (`RecordedTiles`, `Core/MidDistance.cs`). The player's recordings around the camera in two 8 × 8-slot atlases side by side (2080 × 1040 texels): four-block tiles (level 2) out to `NearBandDistance` and two-block tiles (level 1) out to `TerraceDistance`, addressed like the near band (`FarTileWindow`, a lap counter kept across the seam, a 16 × 8 slot table texture instead of uniform arrays). Per texel: RGBA16F (top in world y, coverage, colour known, water) and RGBA8 (the game's colour). A recording in the tile client's memory is taken from there (it is newer than its file) and checked again every half second; others are read from disk on a worker. Tiles fade in over 1.5 s. From the same tiles the worker builds a box per two-block recording (structures, out to the band's edge) and merged lights per four-block tile (`LightPoints`: at most 4 per chunk, lit samples within 8 blocks of the brightest merged, out to `LightDistance`).

**What far terrain draws** (`farterrain.vsh/.fsh`, `wmapfarheight.ash`, `wmapfarshade.ash`, `wmapfaralbedo.ash`):

| Distance | Drawn | From |
| --- | --- | --- |
| 0.75 `V` to `TerraceDistance` (768) | **Terraces**: two-block cells, flat tops at whole-block heights, walls on their east and south edges to the neighbour's top | The cell map (below) |
| `TerraceDistance` to 1536 | The near band's smooth mesh, with the four-block recordings' land, water and colour over the prediction; **recorded structures** as boxes standing on it | Recorded tiles, near band, chart |
| 1536 to the horizon | The world chart, as before | Chart |
| 0.75 `V` to `LightDistance` (3072) | **Distant lights**, after the game's chunks | Merged lights |

- **Which height a cell takes** (`TerracePatch.CellTop`, mirrored in `farcellmap.fsh` and `wmapWithRecorded`): the two-block recording where there is one (the recorder records a cell block by block exactly where the world differs from the prediction by more than 3 blocks, or where blocks were changed: structures, edits, missed shores); otherwise, where the near band predicts land and the four-block recording is land, the **predicted** height; otherwise (recorded water, land where water is predicted, no band) the four-block recording; otherwise the prediction. The first build took the four-block recording's height wherever it existed, and on Nice's slopes only 84 % of the ground lay within a block of the generated terrain (one sample stands for a 4 × 4 cell); with the prediction there, 94 %. This follows the recorder's own rule (WM-D29, WM-D33: a cell not recorded block by block is as predicted). The recordings still decide land or water and the colour. The sea keeps the prediction's bed for its depth colour, a lake its recorded surface.
- **Terraces** are built on the GPU. Each frame, if anything changed (the map moved by a 64-block patch, far terrain's origin, a tile arrived or is fading in), `farcellmap.fsh` works out every cell's top and finished colour into an 832 × 832 cell map (R32F + RGBA8, ±832 blocks around the camera, 64 blocks of margin on both sides of the ring so the camera can move meanwhile). `farcells.vsh` then draws instanced 32 × 32-cell patches (`TerracePatch`: 12 vertices and 6 triangles per cell, walls of equal tops have no area), only the patches that reach into the ring and into the view (frustum test on the CPU). Walls reach 0.02 cells past the corners: the first render showed single sky pixels where two walls meet at a corner with different heights (their edges are not shared). Terrace fragments take their cell's colour, so they only light, haze and fog it. Push-down near the start: land 2 blocks fading over 256 (far terrain alone: 12 over 512), water 1, gone before the smooth mesh begins at 768.
- **Forest**: unrecorded and four-block cells keep the predicted canopy (8 blocks × forest, whole blocks); cells recorded block by block get none.
- **Structures past the terraces**: one box per two-block recording that stands at least half a block above the smooth ground, from its top down into the ground (no bottom face), drawn from `TerraceDistance` (or far terrain's start when terraces are off) to the band's edge.
- **Lights** (`farlights.vsh/.fsh`, a second renderer at Opaque 0.9, after the game's chunks): one quad per light, at least 2.5 px in radius (more for brighter lights), a core and a soft halo added to the colour and the glow buffer (bloom), depth-tested and not written. Pulled 2 blocks + 0.4 % of the distance toward the camera so the light's own building does not hide it. They sink with the planet's curve and are dropped behind the sea's bulge like far terrain's ground (WM-D39), fade in the game's fog (`getFogLevel`) and out by day (daylight 0.35 to 0.75).
- **GL state**: every draw gives back the texture units, VAO, framebuffer, viewport, blend function and equation, depth mask and the enables it found. The game binds its own textures (shadow maps, sky, glow) to units numbered by sampler order; with more Wmap samplers they would have landed on Wmap's units, so they are pinned to units 0 to 4 after each compile (`FarTerrain.PinGameSamplers`, reflection on `ShaderProgramBase.textureLocations`). Wmap uses units 5 to 15.
- **Settings and command**: `MidDistanceRecorded`, `MidDistanceTerraces`, `MidDistanceLights` (on), `TerraceDistance` (768, 256 to 768), `LightDistance` (3072, 1024 to 3584). `.wmap mid` reports what is drawn and the GPU time of far terrain and of the lights (timestamp queries, averaged, never waited for); `.wmap mid recorded|terraces|lights [on|off]` switches a part live.

### Measured

From the smoke runs (Earth Map 0.16.5, Cylinder World 0.11.14, Sea 0.4.6, Vintage Story 1.22.7, RX 6600, 1280 × 720; [Earth](test-results/earth-0.5.0-pre.1.txt), [the mid-distance check alone, full chart](test-results/mid-distance-0.5.0-pre.1.txt)). A 6 × 6 granite tower 28 blocks high with a lit torch on top was built at Nice in the generated chunks, the 3 × 3 chunks around it recorded with the recorder's own code (`Classify`, `BlockLight`; cells changed by the build, as the game reports them, and cells differing from the prediction recorded block by block), and far terrain drawn from them through the production classes and shaders at view distance 512:

| | With the mid-distance view | Prediction alone (0.4.11's far terrain) |
| --- | --- | --- |
| Tower's top (generated top face y 220) drawn at 600 blocks (terraces) | 219.10 ([render](test-results/mid-tower-600-0.5.0-pre.1.png)) | 192.7 (no tower) |
| … at 1200 blocks (smooth band and structures) | 219.56 ([render](test-results/mid-tower-1200-0.5.0-pre.1.png)) | 193.7 (no tower) |
| Ground pixels in the recorded area within a block of the generated rain heights, 600 blocks | 93.9 % of 2993 (worst 4.2 blocks beyond) | 51.9 % |
| … 1200 blocks | 24.1 % of 652 (worst 18.8) | 23.8 % |
| Torch (block light read 14) at night from 1200 blocks out at sea, 5 blocks up | 172/255 brighter ([render](test-results/mid-light-1200-0.5.0-pre.1.png)) | none |
| … from 2900 blocks, behind the planet's curve | 0/255 ([render](test-results/mid-light-2900-planet-0.5.0-pre.1.png)) | |
| … from 2900 blocks on a flat planet (the light is in reach; the curve hides it) | 113/255 ([render](test-results/mid-light-2900-flat-0.5.0-pre.1.png)) | |
| GPU time of all far terrain, 600 blocks out looking along the coast, view distance 512 (median of 9 after 60 draws) | 0.60 ms: cell map 0.10, smooth mesh 0.28, terraces 0.24 (129 patches), structures 0.01 | 0.31 ms |
| … view distance 256 (terraces from 192) | 0.68 and 0.69 ms in two runs (155 patches); one full run measured 2.55 ms, not reproduced | 0.36 ms |
| The smooth mesh alone against 0.4.11's shaders, same view and data | 0.31 ms (0.33 with recordings; 0.12 chart only) | 0.31 ms (0.11 chart only); an earlier run 0.25 |
| Video memory | about 30 MiB more: recorded tiles 24.8 MiB, cell map 5.3 MiB, buffers under 1 MiB | |

The cell map is timed as if drawn every frame; in game it is drawn again only when it moved or a tile arrived or is fading in, so its 0.10 ms is mostly saved. The timing view is one view: a wider view over land, or many structures, costs more, and the budget (1 ms for all far terrain) has to be confirmed in game with `.wmap mid`. All 4 programs link in all 24 combinations of the game's shadow, SSAO, godray and dynamic-light settings in every profile; the Cylinder World and plain profiles pass unchanged (no Earth prediction, so nothing new is drawn there).

Not measured here: the recorder's cost per frame in a running client (by design within the existing 1 ms per tick), how fast the window fills after a teleport, process memory, and the frame time in game.

### Limits of the prototype

- Coverage is the player's own past: every chunk the game loaded while they were there. Other players' building appears only after passing again, and never-visited land shows the prediction (WM-P01 would change both).
- One height per cell; no overhangs, bridges or arches. Structures come from cells recorded block by block: the recorder records those where a block was changed (anything a player builds while it is loaded) or where the cell's four-block sample differs from the prediction by more than 3 blocks. A worldgen building whose cells' centre samples miss it stays as predicted, in the terraces and beyond. Past 768 blocks structures are two-block boxes.
- Ground heights beyond the terraces follow the smooth mesh (16- and 32-block cells from 512 to 2048): at 1200 blocks only about a quarter of ground pixels lie within a block of the generated rain heights, the same as without recordings. Structures there are exact.
- Colours are the game map's colours (`Block.GetColor`), not textures; recordings made before 0.5 have no colour (far terrain's own colours there) until the recorder passes again (within 5 minutes of the chunk loading).
- Lights have no colour of their own (a warm white) and no rhythm; a light is only known where the player has been. Beacons need the Voyage registry (later, cross-mod).
- Recording every loaded chunk writes more recordings to disk and makes paper maps larger (colours compress less); not measured.
- Far terrain needs Earth Map, so the mid-distance view does too: on plain and Cylinder-only worlds it records loaded chunks (with colours and light) but draws nothing new.

### To check in game

Following the verification plan above, with `.wmap mid` for the GPU time and the switches:

1. The same save at view distance 256, 384 and 512 with the mid-distance view, and at 512 and 1024 without it (`.wmap mid recorded off`, `.wmap mid terraces off`, `.wmap mid lights off`): frame time (F3 and the game's frame profiler entries `wmap-far-terrain` and `wmap-far-lights`), process and graphics memory, how long the view takes to fill after a teleport, and `.wmap mid`'s GPU times.
2. Screenshots at a town (your own or a story location), a coast, a forest edge (canopy and terrace steps at the join with real chunks), and at night with lights; a lighthouse (Voyage's harbour beacon) seen from a ship at sea.
3. The join with real chunks at 0.75–0.85 `V`: no terraces poking through real terrain, no gaps or flicker; walls without sky showing through.
4. `client-main.log` for `[wmap] Mid-distance view: …` and no shader errors; GL state (no broken GUI, minimap or shadows after looking at far terrain).

### In-game test of 0.5.0-pre.1, and 0.5.0-pre.2

The user played 0.5.0-pre.1 on 23 October 2026 (Earth, Cylinder World and Sea; Santorini): it "looks fantastic and much better than before", the rocks and islands especially. Two problems and a play profile (Devkit, about 23 minutes) came back; 0.5.0-pre.2 answers them.

**The far sea ended Sea's near water in a ring** (screenshots at view distance 512 and 256; worse at 256). Cause: Sea 0.4.x adds its sky reflection and night floor to Wmap's far sea by text-patching the program named `farterrain` when it loads. That still worked (the game expands `#include` before Sea's hook sees the code, and the anchors were still there), but from 0.75 × view distance to 768 blocks the far sea is now drawn by the terraces' program `farcells`, which Sea never patched: a flat teal (84, 119, 114) all the way out, against Sea's sea darkening to (50, 76, 85) at its edge and brightening toward the horizon.
- **Far water by contract (0.5.0-pre.2).** Wmap declares `seaNightFloor` (vec3), `seaSkyReflect` (float), `seaWaveCount` (int) and `seaWave[24]` (vec4: kx, kz, phase at the camera, amplitude) in `wmapfaralbedo.ash`, and does the shading itself in every far program: the night floor on the light, the sky reflected by Fresnel along the ray the waves reflect (the game's own `getSkyColorAt`, rgb × alpha at night as Sea 0.4.6 does), and wave normals from Sea's own slope formula, each wave train fading out where it gets shorter than about four pixels so the far sea does not shimmer. A water mod sets them when the game uses a far program. Sea already does that for any program declaring `seaNightFloor` (its `Use` hook, since 0.4.3), and its text patch skips a shader that declares it, so **Sea 0.4.6 works as it is** (reflection and night floor on both far programs; checked in the smoke world by calling its patch on the shader). The waves need Sea 0.4.7-pre.1 (branch `wmap-far-waves` in Sea's repository), which also sends its wave trains to Wmap's programs. Without Sea everything is zero and the far sea is as before.
- **The far sea's colour with Sea.** Sea's near water across distance fits one model closely: a dark body seen looking down plus the sky by Fresnel. From the screenshots, at its edge at 256 (about 200 blocks out) it is (0.20, 0.31, 0.33); at 512 (about 380 out) (0.36, 0.43, 0.47); with the horizon sky at (0.73, 0.76, 0.80) both fit a body of about (0.165, 0.265, 0.305) and a Fresnel weight that comes out the same (0.33 to 0.35) in all three channels at 380 blocks. With Sea's reflection on, the far sea's body is now 0.25 × the climate's water tint + (0.13, 0.15, 0.20) (lighter in shallow water), the sky added by Fresnel as above. Without Sea it keeps 0.4.0's look. The terraces' cells carry the tint and depth instead of a finished colour, so their water is shaded by the same function. Calibrated from two screenshots of one place in daylight: the match has to be checked in game, at other places, at dusk and at night.

**A band of sky after changing the view distance** (screenshot at 700, staying when switching back and forth). Reproduced in the smoke harness: switching 512 → 700 → 512 → 256 → 512 rebuilds all of Wmap's state (the last 512 render equals the first, 0 pixels differ; the cell map is drawn again whenever its signature changes). The band is chunk columns not loaded yet: the game fades its chunks by its view-distance setting, but after a change (and after a teleport, and while travelling fast) columns between the old and the new distance load over seconds to minutes, and the game shows the sky where they are missing. The real water in that screenshot ended nearer than at 512, and the "wedges" were single columns arriving. **0.5.0-pre.2 starts far terrain at the nearest chunk column that is not loaded** (map chunk and sea-level chunk present), when that is nearer than 0.75 × view distance (`FarStart`, checked every 250 ms; at most 0.75 × view distance / 32 columns around), so far terrain fills in until they arrive. Check: at 700 with columns loaded to 400 blocks, 0 of 13,527 sea pixels between the real water's end and 760 blocks are undrawn; 0.5.0-pre.1's rule leaves 6,113 undrawn (tried).

**Play profile** (Devkit, 0.4.11 against 0.5.0-pre.1). Wmap's client tick listener 3.4 → 10.6 ms/s, 207 → 464 µs and 112 → 262 KB per call; `FarTerrain.OnRenderFrame` 1.2 → 8.3 KB per frame, worst 31 ms. Found and fixed in 0.5.0-pre.2:
- Every tick the tile client copied up to two dirty recordings for saving (150 KB each), and the recorder now keeps many tiles dirty. A recording is now saved at most every 5 s and re-encoded for a view at most once a second (the first time at once), and the copies the worker hands back are reused. Its eviction and per-tick loops no longer use LINQ (the wanted tiles are sorted once per change).
- The recorder made new block positions per sample (and in `Classify`, `Bed`, `BlockLight`): reused now.
- Recorded tiles the recorder keeps changing were copied and derived again every half second (about 600 KB each): now at most every 10 s, with pooled copies, texels and scratch.
- Far terrain's status line was a new string every frame; the frustum and the lights' uniforms allocated per frame; the recorded tiles' atlases were created by uploading 25 MiB of zeros (the 31 ms first frame).
- Measured in the smoke check (main thread only; the workers' allocations not counted): the recorder's classification and light 0 bytes per sample; far terrain 824 bytes per frame (all its updates and draws); the tile client under a stream of 256 four-block samples a tick across new ground 0.8 to 2.4 KB per tick in three runs (135 KB before the reuse of copies). The tick listener's time and the worst calls need the profile again in game.

**GPU** (RX 6600, the smoke's timing view at 600 blocks over the coast, median of 9 after 60 draws; [results](test-results/mid-distance-0.5.0-pre.2.txt)): all far terrain 0.63 ms at view distance 512 and 0.62 at 256; 0.66 ms with Sea's reflection and 24 wave trains on; far terrain alone 0.32 and 0.33 ms. A view with more sea on screen costs more for the waves (24 cosines per far-sea pixel): check `.wmap mid` at sea in game.

**To check in game (0.5.0-pre.2)**: the sea at 256, 512 and 700 with Sea 0.4.6 and with Sea 0.4.7-pre.1 (the waves): no ring, no shimmer far out, the colour across the join by day, at dusk and at night; changing the view distance and teleporting (far terrain fills in while columns load); the Devkit profile again (tick listener, allocations, worst calls).

### Review of 0.5.0-pre.2, and 0.5.0-pre.3

A review round (Codex adversarial review and the main session's read, 23 October 2026) found six problems; 0.5.0-pre.3 fixes them, each with a check that fails on 0.5.0-pre.2's behaviour (tried for every one: the pre.2 behaviour put back, the check run).

1. **Far terrain covered loaded ground for one missing column.** 0.5.0-pre.2 pulled far terrain's start in all round to the nearest unloaded column, so terraces and predicted ground (or an old recorded roof) covered loaded real terrain in every other direction, popping when the column arrived. Now the start stays at 0.75 × view distance, and nearer than that far terrain draws only over columns not loaded: a 75 × 75 mask of the columns around the camera (`FarStart.Scan`, every 250 ms) that the far fragment shader reads (`FarStart.Draws` mirrors it). Check: one column missing 242 blocks east at view distance 512: it is filled (204 pixels), 33 pixels near its edges are drawn over loaded columns (the check's sea-plane estimate of each pixel's column), against 17,970 with pre.2's single radius.
2. **Older recordings' colours read as bright lights.** Before 0.4.4 the recorder stored `Block.GetColor` raw, alpha 0xFF in the top byte, which pre.2 read as "colour, light 31": the user's older recordings would light up at night in false colours. A tile now says which format its colours are: a mark in its north-west padding corner, which no sample (the recorder copies samples into the padding along the edges only) and no paper-map merge (known cells only) ever writes, so it is 0 in every older tile. Only a marked tile's colours count (`RecordedColor.Of`); the recorder marks a tile when it first writes a colour into it, clearing what an older recorder left. Paper maps: a marked map marks the reader's tile; an older map's colours are not copied into a marked tile. The tile format is unchanged (0.4.11 reads the mark as one unknown padding cell). Recordings made by 0.5.0-pre.1 and pre.2 are in unmarked tiles too, so their colours and lights are dropped until the recorder passes again (it revisits loaded chunks every 5 minutes). Core checks: a 0xFF336699 colour gives no light and no colour; marking keeps the terrain; the mark survives the codec; both merge directions.
3. **New recordings stayed out of the window until the camera moved 16 blocks.** The windows are worked out again whenever a recording tile appears (`TileClient.ObservationVersion`). Check: a tile appearing with the camera still enters the window (pre.2: 0 then 0).
4. **All stale recordings copied in one frame** (up to 15 MiB with a hundred; it fits the profile's GC spikes). At most 4 copies a frame (`RecordedTiles.CopiesPerFrame`), the rest the next frames; a copy that cannot be queued goes back to the pool. Check: 60 changing recordings, at most 4 copied a frame (pre.2: 60).
5. **The cell map could not be allocated under another renderer's pixel-unpack buffer**, which switched the mid-distance view off. Allocated under `GlUnpack` now. Check: with a 64-byte unpack buffer bound, the cell map is created and the buffer stays bound (pre.2: incomplete framebuffer).
6. **The status line was built every frame** after all ("on" set every frame defeated pre.2's cache). Check: 0 bytes over 1000 frames (pre.2: 272,000).

Also checked in this round (not raised by the review): Sea's `seaWave` uniforms without Sea (`seaWaveCount` starts at 0; unused uniforms are dropped and a water mod's writes to them are ignored) and with Sea 0.4.6, which sets only the reflection and night floor; GLSL 3.30 portability (no implicit int/float conversions in the new shaders; uniform arrays indexed by a loop variable, which 3.30 allows; the four programs link in all 24 settings on the RX 6600, not tried on NVIDIA or Intel); precision at large coordinates (wave phases and terrace positions are relative to the camera; box positions are whole blocks, exact in floats to 16 million; lights are within 0.03 blocks at x = 512,000); and Cylinder World's seam for the recorded windows: a recorded pillar 100 blocks past the seam is drawn at its height seen from the lap west of the seam (64 pixels at its top) and from the east (32).

### Review of 0.5.0-pre.3 and its play profile, and 0.5.0-pre.4

A second review round (24 October 2026) raised four problems, and the user's profile of 0.5.0-pre.3 in play (Devkit, against 0.4.11 in the same place) two allocation findings. 0.5.0-pre.4 fixes them; each fix has a check that fails with 0.5.0-pre.3's behaviour.

| Profile (per call) | 0.4.11 | 0.5.0-pre.3 |
|---|---|---|
| `FarTerrain.OnRenderFrame` | 20.1 µs mean, 2,947 B | 71.7 µs mean (p99 0.215 ms, max 27.88 ms), 6,500 B |
| Wmap's client tick listener | 376.2 µs mean, 124,870 B | 577.7 µs mean (p99 2.435 ms, max 23.52 ms), 135,132 B |

A. **Texture units could clash.** The game gives each sampler of a program a unit by the order they are declared in, and binds its own textures (shadow maps, sky, glow, the liquid depth) to those units. 0.5.0-pre.3 pinned the five names it knew to units 0, 1, ... and put Wmap's twelve at fixed units 4 to 15: with all five present the fifth landed on the loaded-columns mask's unit 4, and a sampler the game might add under another name kept its declaration-order unit among Wmap's. Now every sampler that is not Wmap's is pinned to 0, 1, ... and Wmap's are planned per program from right above them (at 4 when there are four or fewer, as before), checked against the GPU's units (`FarTerrainDraw.PinGameSamplers`, `PlanUnits`). Check: each of the four programs, expanded as the game expands it, its samplers numbered by the game's own expression (`ShaderProgram.collectUniformNames`), pinned and planned by the production code: no unit holds two samplers, and every sampler GL finds active is among them; then with all five game samplers and one unknown added, the plan still shares no unit while 0.5.0-pre.3's units clash (liquidDepth and the mask on unit 4).

B. **The loaded-columns mask went stale.** It was made every 250 ms around where the camera then was. After a teleport or a jump across Cylinder World's seam (a whole lap in x: the game's chunks there are other chunks), up to 250 ms of frames read the columns of the old place. Now it is made again at once when the camera has moved a column or more, or far terrain's start changed (`FarStart.ScanDue`), and every 250 ms otherwise. Cylinder World keeps real chunks on both sides of its jump line at their own coordinates, so the mask stays in the game's chunk coordinates; it needs no wrapped offset, only a new scan after the jump. Core checks: a column's move, a seam jump of one lap and a change of start each scan at once; after a teleport the missing column under the new place is filled only with a new mask.

C. **New recordings reworked every window.** Each new recording tile (`TileClient.ObservationVersion`) made all recorded windows be worked out again, and the recorder makes tiles all the time while the player walks. Now a new tile does so at most once a second (`RecordedTiles.VersionReselectMs`; a camera move still does at once), with the lists reused and without LINQ. Check: a new recording every frame for 2.3 seconds: the windows worked out 3 times (pre.3: 150, once a frame).

D. **A rollback could still bring false lights.** A recorder from before 0.4.4 run on a tile 0.5 had marked (going back to an old Wmap with the same world) writes raw colours, top byte 0xFF, into it; the mark stays, and 0.5.0-pre.3 read each as light 31. A colour of this format now needs its top byte in 0x80 to 0x9F, which is what the recorder writes (`RecordedColor.Has`). Core checks: a raw colour in a marked tile gives no colour and no light, this format's colours at light 0 and 31 do, and over all 256 top bytes only 0x80 to 0x9F count. Remaining: a raw colour whose alpha was 0x80 to 0x9F would pass (the game's block colours are opaque).

**Far terrain's allocations in play.** The smoke frame (0.8 KB) stood still and never took a recording again; the game walks and the recorder keeps changing nearby tiles. Each time one was taken again (every few seconds) the boxes and lights were put together into a new list and array the size of all of them (up to 2.6 MB), and walking reworked the windows with LINQ every 16 blocks. Now the boxes and lights go into the same arrays (grown when needed, uploaded by count), and the layers' uniform names are made once (four strings a draw before). Checks: rebuilding the boxes and lights allocates nothing (pre.3: the size of all boxes each time); a walking frame (half a block a frame for 400 frames) 1,552 bytes; standing 160 (pre.3's smoke frame: 824).

**The tick listener's 125 KB per call** (in 0.4.11 too) was mostly the exploration save: every 30 seconds it cloned the whole explored mask on the main thread, about 27 MB at 8192 texels wide (a large-object allocation, which fits the 23 ms maximum). The copy now goes into the last save's buffer once its write is done (a new one only while the last is still being written). The day and night layer also made a new position for each of its 4096 samples a tick while it builds; it reuses one now (the game's `GetSunPosition` still returns a new vector each, which Wmap cannot avoid without bypassing mods that move the sun). Not measured in the game yet: the next profile should show it.

**Shader compile time.** The 623 ms frame in the profile was in Sea's compile path. Wmap compiles its four far programs when the game loads shaders (and on a shader reload); Sea 0.4.7-pre.1 no longer patches Wmap 0.5's far terrain (it declares the far-water uniforms itself), so Sea only reads its code. Their compile time was not measured in game; if it matters, farterrain and farcells (the largest, with the game's fog and sky includes) are the ones to look at.
