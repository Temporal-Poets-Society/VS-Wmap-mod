# World Map 0.5.3-rc.3 — release candidate

> **In development.** This is a release candidate for 0.5.3; the last full release is 0.5.1. The mid-distance renderer remains experimental; client and multiplayer testing continues. Changes per version: [CHANGELOG](CHANGELOG.md); the first release: [release details and verification](docs/release-0.5.0.md).

A standalone Vintage Story map for cylindrical worlds, Earth maps and ships. Zoom from the whole world down to a harbour without loading screens, pan east or west across Cylinder World's seam without a jump, and read latitude and longitude anywhere. Fast Map is not required.

Two styles, switched instantly:

- **Chart**: a nautical chart drawn automatically from the terrain. Parchment with darkened edges, sepia ink coastlines, offshore waterlines, stippled shallows, tinted shelves and depth contours (dense to 20 blocks, dotted beyond), land contours every 10 blocks, pen hatching on highland slopes, rivers from Earth Map's own courses, sea ice, a graticule with equator, tropics and polar circles, edge labels, a compass rose and a scale bar in sea miles.
- **Terrain**: a topographic map with hill shading and bathymetry, land coloured by climate with Earth Map (desert sand, steppe, grassland, forest, tundra, snow; rock and snow on high ground), by elevation otherwise.

Two modes:

- **Game**: the chart fills in only where you have been. Everything else is blank parchment with the graticule, ready for later surveying mechanics.
- **Creative**: the whole predicted world, for building and checking worlds. The map follows your game mode: creative mode opens it, survival returns to the game chart. The **Creative / Game** button switches between them in creative mode, or anywhere with the server's `wmap` privilege or its `AllowAllPlayers` setting. Being the server's admin (or a single-player host) is not enough: switch to creative, or grant yourself `wmap`. (Called Development and Gameplay before 0.4.3.)

With Earth Map, **far terrain** shows the world on the horizon past your view distance, out to 8 km.

**Paper maps** carry what you have discovered to another player (see [Paper maps](#paper-maps)).

With the Sea mod, a **Jonas barometer** shows worldwide weather and monthly prevailing winds on the gameplay chart and minimap (see [Winds](#winds)).

Sextant fixes, surveying, the captain's chart table and course following are later milestones; see the [architecture roadmap](docs/architecture.md). Decisions so far are recorded in [docs/decisions.md](docs/decisions.md).

## Install and open

1. Quit the game completely, then copy `wmap_0.5.3-rc.3.zip` into your Vintage Story data directory's `Mods` folder, replacing any previous Wmap ZIP, then restart the game. On Windows this is `%APPDATA%\VintagestoryData\Mods`. Keep the ZIP intact.
2. For multiplayer, install the same ZIP on the server and every client. Client and server need the same World Map version: the network format changed in 0.5.2.
3. Enter a world and press **M** (or your remapped world-map key), or type **`.wmap`**. **`.wmap vanilla`** opens the original map.

The atlas starts in **Chart**, centered on and following the player at **2 blocks per GUI pixel**. Panning, zooming and style changes remain in effect while that atlas window exists; a new session starts in this view again.

The HUD minimap uses the same Wmap style, exploration and player heading arrow. Native player tracking, hover text and waypoint markers remain available. F6 toggles it and Ctrl+F6 changes its corner as usual. Set `ReplaceMinimap` to `false` and restart to keep vanilla terrain and player icons there. Gameplay hides unexplored terrain; use Follow and zoom in to see the area around you, or the Creative map to see the whole world.

The first time a world with Earth Map or Cylinder World starts, the server charts the whole world once in the background. On a 204,800 × 102,400 ring this took about 70 seconds on a 16-thread desktop. The status line shows progress, and close-up detail works meanwhile. The result is cached; later starts reuse it immediately. Each client downloads it once (about 12.5 MiB for that ring) and keeps its own copy.

Requires Vintage Story 1.22.0 or later; built and tested against **Vintage Story 1.22.7**. Companion versions and the exact test scopes are recorded in [testing](docs/testing.md) and [this release](docs/release-0.5.0.md). Other versions and mod combinations are unverified. Coexistence with Fast Map has not been playtested.

## Controls

| Control | Action |
| --- | --- |
| M / `.wmap` | Open or close Wmap |
| F6 / Ctrl+F6 | Toggle HUD minimap / change its corner |
| Ctrl+Shift+V | Latitude, longitude and local time, then heading and the world's UTC time (`17:36 local`, `16:00 UTC`), in the top left corner on the background of the game's own coordinates box; on worlds with one clock the hour once, and the time alone on worlds without geography. The game's block coordinates stay on Ctrl+V |
| `/wmapore [ore]` | Server, players with the full map: the ore heat map at your column beside the prospecting pick's own reading there, for one ore or all |
| `.wmap mid` | The mid-distance view (prototype): what it draws and its GPU time; `.wmap mid recorded\|terraces\|lights [on\|off]` switches a part live |
| Left drag | Pan; stops following |
| Right-click | Add a waypoint there with the game's own dialog (icon, colour, title; shared with the minimap and your group). On a waypoint: edit it; on a prospecting reading: delete it, as on the game's map. Creative mode: a small menu with the position, **Add waypoint…** and **Teleport here** (onto the top block or water; needs the `tp` privilege; client and server 0.4.3). A click elsewhere, the wheel or Esc closes it |
| Mouse wheel | Smooth zoom around the cursor. Your own waypoints stay and shrink as you zoom out; automatic markers and prospecting readings fade out between 4 and 16 blocks a pixel (a pinned one stays) |
| Show | What the atlas draws: your waypoints, automatic markers, prospecting readings, other mods' layers (ships, creatures) |
| Ores | Show only the prospecting readings that mention one ore, coloured by its strength |
| Auto-mark | Make waypoints automatically **when harvesting** (starting to break or pick: a mushroom, a berry bush, surface ore) and/or **when sneaking** while looking at something; and which kinds: berries, mushrooms, surface ore, wild beehives, resin, fruit trees, clay, translocators, traders. The server makes the marker unless one of the same name is near (client and server 0.4.6) |
| Home / Whole world | Fit the world |
| Space / Follow | Center on yourself and follow |
| Chart / Terrain | Switch style |
| Creative / Game | Switch between the whole predicted world and the game chart (shown in creative mode or with permission) |
| Ore heat | Creative map only: colour the map by how much of one ore the world's deposits hold, or by how many ores the prospecting pick would list (see [Ore heat maps](#ore-heat-maps-creative)) |
| Day / Night | Shade the night side of the world from the game's daylight, with the line where day turns to night |
| Winds | With the Sea mod. Worldwide weather now, prevailing winds now, or prevailing winds for January through December. Gameplay requires a Jonas barometer; Off hides winds |
| ° / °′ | Decimal degrees or degrees and minutes |
| Esc | Close |

Your position and the cursor position are shown below the map. Earth Map supplies its geographic transform, including the Pacific seam and its sea gutter; Cylinder World's planetary layout supplies a linear one otherwise; ordinary worlds show X/Z.

## Winds

With the [Sea](../Sea) mod installed, the map can show its winds.

### In gameplay: the Jonas barometer

A **Jonas barometer** is a clockwork weather glass with glowing cups that turn to winds nobody has felt yet. For now, carrying one in your hotbar or backpack lets the atlas and HUD minimap show winds across the **whole world**, over explored and unexplored terrain. Choose **Weather now**, **Prevailing now**, or **January through December** in the atlas Winds list to see the dominant winds throughout the year. The position line reads the selected winds at your location and at any cursor position in knots. On an Earth world the barometer also reads the **air pressure** where you stand, from Earth's weather, and whether it rose or fell over the last three hours ("Barometer: 1004 hPa, falling"); the weather-now reading at the cursor adds the pressure there. A falling glass comes before Earth's storms and fronts. Off hides them; without a barometer or another mod's wind-sight grant, gameplay hides them too. `BarometerRange` is retained for compatibility and no longer clips the display.

Craft it from 2 brass ingots, plain glass, a Jonas oscillator, a Jonas valve, a Jonas spring and a temporal gear:

| | | |
| --- | --- | --- |
| brass ingot | plain glass | brass ingot |
| oscillator (Jonas frame) | valve (Jonas part) | spring (Jonas frame) |
| | temporal gear | |

A ship mod can grant a mounted barometer's wind sight to everyone aboard (integration contract; see [docs/magelan-barometer.md](docs/magelan-barometer.md)). Another mod grants wind sight by setting the player entity's watched string attribute `wmapWindSight` on the server to a short description of the source, such as "the Santa Maria's binnacle", and removing it when it no longer applies. Wmap clears it when a player joins. An item counts as a wind instrument when its attributes set `wmapWindInstrument: true`.

### Wind layers

Pick a layer from the **Winds** list above the map:

- **January … December**: the prevailing wind of that month from the provider registered with Sea (Earth supplies its climate and weather on Earth worlds). Dashed lines mark changes between easterly and westerly belts; exactly calm regions show calm circles without striped fills.
- **Prevailing now**: the same for today's date.
- **Weather now**: what is actually blowing, from Sea's wind provider (gusts left out). It refreshes every quarter of a game hour.

Arrows fly with the wind, like on a pilot chart. They grow longer and heavier with strength, and carry one feather pair per step: light (from 0.1, vanilla units), moderate (0.25), strong (0.5); a small circle marks a calm. They are spaced evenly on screen at every zoom and stay put while you pan. With the cursor over the map, the position line reads the wind there, its Beaufort name and belt.

Wmap samples a 512 × 256 grid over the world in the background through Sea, without extra wind network traffic. Earth or another registered provider owns wind and weather (Sea 0.4.0 or later); without one, prevailing winds are calm. The readout identifies when the model is off.

## Far terrain

Past the game's view distance, Wmap draws the world as a low, hazy 3D landscape out to `FarTerrainDistance` (8192 blocks): coasts, islands and mountain ranges on the horizon, the sea as a flat plane at the water's surface. It is lit by the game's sun and daylight and fades into the game's own sky colour and weather fog, so it disappears into the horizon at any time of day and in rain or fog. The game fades its own chunks out at 75–85 % of your view distance; far terrain starts there, slightly lower, so real terrain covers it where both exist. It wraps across Cylinder World's seam. Past your view distance it follows the planet's curve (0.4.11, the server's `PlanetScale`, worlds with geography; flat until then, like the game's own chunks fading into it): coasts sink below the horizon with distance, so from Athens you see the islands and Crete's mountains, not Africa, and from mid-Atlantic only the sea.

**Near band** (0.4.0, Earth Map): out to `NearBandDistance` (1536 blocks) far terrain is drawn from 4-block detail tiles instead of the 25-block world chart, so hills, cliffs, valleys and coasts nearest the real chunks have their real shape and height (on land, half the points within 0.4 blocks of Earth's terrain, 95 % within 1.1; the chart alone: 1.8 and 9). Beyond it the chart takes over, blending over the last 336 blocks. **Climate colours** (0.4.0, Earth Map): land and sea take the game's own climate tints for grass, leaves and water, and forests stand about 8 blocks above the ground, from Earth's forest and climate. Without them far terrain keeps the atlas's Terrain colours. The tiles load nearest first, about 2 seconds for the whole band on a fresh world, and fade in.

It is the same world chart the atlas already downloaded, displaced on the GPU: one draw call, about 2.6 MiB of GPU buffers with the near band (1.6 MiB without). The near band and climate tiles cost the server about 43 ms per tile once (cached on disk, shared by players), about 0.4 MiB of downloads for a full band, 12.4 MiB of textures, and on an RX 6600 about 0.3 ms per frame (0.1 ms without). It needs a world chart with relief, so it needs Earth Map (Cylinder World alone predicts only coastlines, with invented heights), and players without the `wmap` privilege on a server with `ShareWorldChart: false` do not get it. It shows predicted terrain, not structures or player edits, and like real sight it is not limited by exploration. Detail is limited by the chart: about 25 blocks per texel on a 204,800-block ring.

Wmap raises the game's camera far plane (normally 3000 blocks) while far terrain is drawn. If Farseer is installed, Wmap leaves far terrain to it. The [design note](docs/far-terrain.md) covers the mesh, costs and risks.

## Mid-distance view (prototype)

Experimental. Far terrain's near band (to 1536 blocks) shows what you recorded where you have been, over the prediction, so a town, a harbour or your own base is there past your chunks, and lights show at night:

- **Recorded terrain** (`MidDistanceRecorded`): the recorder records every chunk the game has loaded, out to your view distance, with the game's colours and block light, within its usual time budget. Far terrain takes those heights and colours, and draws recorded structures past the terraces as blocks at their height (one per two blocks).
- **Terraces** (`MidDistanceTerraces`): from where your chunks fade out (0.75 × view distance) to `TerraceDistance` (768 blocks), two-block cells at whole-block heights with walls, worked out on the graphics card each frame. With a view distance of 1024 or more there are none: your chunks reach past them.
- **Distant lights** (`MidDistanceLights`): recorded lit places (block light 8 or more at the top of a column: torches, lanterns, lighthouses) as glows at night, a few per chunk, out to `LightDistance` (3072 blocks), hidden by what stands in front, below the horizon with the planet's curve, in fog, and fading out by day.

`.wmap mid` reports what is drawn and the GPU time of far terrain and of the lights (timestamp queries, averaged); `.wmap mid terraces off` and so on switch a part without restarting, to compare. On first entering a world the window fills as the recorder works through the loaded chunks (a few hundred a second at most). Where you have never been, the prediction is shown as before. Needs far terrain (Earth Map); client only.

**The far sea and Sea**: Wmap's far programs declare the far-water uniforms `seaNightFloor`, `seaSkyReflect`, `seaWaveCount` and `seaWave[24]` and shade the far sea with them (night floor, sky reflected by Fresnel, wave normals fading out with distance). A water mod sets them when the game uses those programs: Sea 0.4.6 sets the first two (its far sea's reflection and night floor), Sea 0.4.7-pre.1 also its waves. Without one they stay zero and the far sea looks as before. While chunk columns within 0.75 × view distance are not loaded yet (after changing the view distance or teleporting), far terrain fills those columns, and only those, so no band of sky shows and loaded ground is never covered.

What it cannot show: overhangs, bridges and arches (one height per cell), thin things (masts, poles, fences), ships and creatures, other players' building since you last passed, and textures (each cell has one colour). Lights have no rhythm: a lighthouse's flashes (Voyage) would need a beacon registry, a later cross-mod proposal.

## How it draws

The server samples the world's terrain generator once into an **8192 × 4096 world chart** (about 25 blocks per texel on the ring above). Each texel stores *meaning*, not color: distance to the coast, height relative to the sea, ice, and distance to the nearest river. The client uploads it as one OpenGL texture with a full mipmap chain and wrap-around addressing, and a shader evaluates the chosen style for every screen pixel. That is why:

- zooming within the world chart's resolution requires no new terrain data, and ink lines retain their screen thickness;
- the seam is continuous; the texture simply repeats;
- switching style, or later adding styles, never re-renders tiles.

The image has a finite resolution: about 25 blocks per texel on a 204,800-block ring, or 62 on a 510,976-block ring. Magnification cannot recover smaller features from it. Below half that spacing in blocks per screen pixel, **detail tiles** (128 × 128 texels, at progressively finer block spacing) are sampled on demand by the server and saved on both sides. These are cached terrain images at several resolutions, drawn by the same shader. The whole set of fine levels is not baked in advance: each doubling of world-image resolution would quadruple its pixel count.

Uploaded detail from neighbouring levels fills gaps while the new zoom loads. Its GPU copy survives eviction of its decoded CPU copy, within the separate GPU cache limit. Returning to an uploaded view can therefore draw immediately; reloading identical disk data preserves that image, while changed terrain replaces it. An evicted GPU image reloads its saved tile from disk. Receiving the world chart no longer replaces local hill shading with coarse regional shading. New areas still require their first detail generation, and drawing the style per pixel adjusts its ink patterns during zoom. Without Earth Map or Cylinder World there is no world chart; terrain recorded around you is drawn through the same styles instead.

Wmap creates its textures directly instead of through the game's helpers, which cap mipmaps at the player's mipmap setting and use nearest-mipmap filtering: unsuitable for a map viewed across a 100× zoom range.

## Terrain sources

| Installed world mods | World chart |
| --- | --- |
| Earth Map, optionally with Cylinder World | Earth's terrain model and river courses, plus Cylinder's seam, polar ice and Wall when installed |
| Cylinder World without Earth Map | Approximate continent/ocean mask with invented heights (no relief, no far terrain), plus Cylinder's seam band, polar ice and walls |
| Neither, or `PredictTerrain: false` | None; recorded terrain around the player only |

Cylinder World's seam band is drawn with Cylinder's own rule (`SeamParams.SeamGround`, Cylinder World 0.11.5 and later): in worlds created with 0.11.5 its seabed has relief, its edge is wavy and its ramp can reach up to 864 blocks past the band's half-width. Older Cylinder builds are drawn with the earlier flat-band formula.

Recorded terrain draws over the prediction wherever you have been, so a wrong prediction is corrected locally. Predictions do not generate chunks. They cannot reproduce every later generation pass, structure or player edit, and they are not a source of navigable depth for ships. Sea ice follows Cylinder's static Arctic rule; Cylinder 0.11's seasonal sea ice, which changes with the in-game calendar, is not modelled yet.

Other terrain generators (Terra Prety, …) are not predicted. With Cylinder World, Wmap still reads the installed ocean-map generator for its mask, so Terra Prety's continents appear approximately, without heights. Set `PredictTerrain: false` if the mask misleads. Details and what full support would need: [terrain generators](docs/terrain-generators.md).

## Exploration

In gameplay the chart reveals what you have seen. By default (`ExploreBySight`, on from 0.4.5) that is what lies in your line of sight, described below; with it off, what lies within `ExploreRadius` (1536 blocks, the game's maximum view distance) of where you have been. It is the same for every player whatever their own view distance, because the chart is drawn from the world chart rather than from chunks your client happens to have loaded. Exploration is stored per world and player in `Wmap/observed/<hash>/explored.wmx`. On first start with 0.3.0 it is seeded from terrain recorded by earlier Wmap versions. With the server setting `ExploreBySight` the game chart is charted by line of sight: a small circle around you (`SightCircle`, 64 blocks) and whatever your eye can see over the ground, all around you, out to `SightDistance` (2048 blocks). Small hidden patches that what you see encloses, such as the back of a hill or a hollow, are charted with it, up to the area of a `SightHoleSize` square (256 blocks); a patch reaching the edge of your sight stays hidden. Mountains hide what lies behind them, so sailing the Adriatic charts Italy's coast but not the Tyrrhenian side. On a world with geography the planet is round (0.4.11): ground sinks below the horizon by distance² / (2 × radius), the radius being the world's east-west circumference / 2π (`PlanetScale`). On a 256,000-block ring, from a deck 5 blocks above the sea, the sea's horizon is about 640 blocks off, a low coast shows to about 1,300 blocks, a 50-block hill to about 2,700 and a 150-block mountain to about 4,100; a smaller world has a nearer horizon. It looks over the world chart's heights (no forests, buildings, fog or night) and needs the shared world chart; without it the circle applies. Nearby loaded terrain is recorded every four blocks, and block by block where it differs from the prediction (story locations, your digging and building), ignoring trees and plants. It draws over predictions where you have been, draws the whole map in worlds without predictions, and later surveying features will build on it.

With `ShareWorldChart` (the default), every player receives the world chart and predicted detail, and gameplay hides what they have not explored. A modified client could show it anyway. Set it to `false` on a server where that matters: ordinary players then see only their own recorded terrain.

## Ore heat maps (Creative)

On the Creative map (creative mode, the `wmap` privilege or `AllowAllPlayers`), **Ore heat** colours the map by one ore, for checking or debugging ore generation (client and server). The map follows every zoom: zoomed in, each cell is one chunk; zoomed out, cells grow so they stay about 10 pixels or more. The server works the map out in tiles, every ore at once, and stores them with the world's map tiles, so a place is worked out once and switching ores is instant. On an Earth world, choosing an ore the first time also stores the coarse levels of the whole world in the background. A tile not there yet is drawn from a coarser one, in its larger cells, until it arrives; the status line counts the tiles still being worked out, the legend sits at the top right and the line under the map says what the cell under the cursor holds.

- **One ore**: about how many blocks of that ore a chunk column there holds (32 × 32 blocks, surface to bottom), from 1 (blue) to 10,000 (red) on a logarithmic scale, and what the prospecting pick's density reading would say there (its word and factor; it lists an ore only above a factor of 0.025, smaller amounts are "miniscule").
- **Ores the pick lists**: how many ores a density reading there would list (grey: none). Offered only where the generators answer the pick's own reading (Earth); vanilla's ore maps would over-count.

The numbers come from the world's own deposit generators through the game's API, for the same deposit the prospecting pick asks about each ore. With Earth's regional ores the generators answer anywhere on the planet, exactly as they answer the pick. Where Earth keeps vanilla's rock strata in a column (under ice sheets, for example), the pick hands Earth vanilla's simulated rock column; the map does the same where that chunk is loaded, and elsewhere leaves such a cell unknown until it is, rather than reading 0. `/wmapore` compares a cell with the pick where you stand. With vanilla's generators the map uses the region's ore map, so it shows only regions the server has loaded, and it is an upper bound: vanilla places ore only in its host rock, which the map does not count. Never shown on the game chart. Design and checks: [WM-D47](docs/decisions.md#wm-d47-ore-heat-maps-on-the-creative-map-052-ore1-5-november-2026) and [WM-D48](docs/decisions.md#wm-d48-ore-heat-maps-in-stored-tiles-at-every-zoom-052-6-november-2026) (stored tiles).

## Paper maps

A **blank map** is crafted from eight parchments around an ink and quill in the center (the quill is kept). Hold it and right-click: give the map a title and tick what to draw: your **charted area**, your **recorded terrain**, your **waypoints**, your **automatic markers** and your **prospecting readings**. The map holds everything you have discovered of those, as it is when you draw it; it does not change as you explore further. Hand it to another player. Holding it, they right-click and tick what to copy into their own atlas. Waypoints and readings become theirs (the server makes them, as the game's own), and those they already have are not copied twice; the charted area and terrain fill in only where they have recorded nothing themselves, so what they saw wins over the copy. A drawn map with a blank map in the crafting grid makes a copy; the drawn one is kept. Copies share one snapshot.

The map's content is kept in the world save (`wmap-map-<id>`); the item carries only its id, title, who drew it and what it holds. A map is at most `PaperMapMaxMiB` (16 MiB); the most travelled test worlds need 2–3 MiB. Needs client and server 0.4.8. Design: [docs/paper-maps.md](docs/paper-maps.md).

## Configuration

`ModConfig/wmap.json` is created on first load. Restart after editing it.

The world settings under **Customize > Earth** offer **Wmap: shared world chart** (`wmapSharedChart`) and **Wmap: explore by sight** (`wmapExploreBySight`). Both default to on for new worlds. Each saved world value overrides its corresponding `ShareWorldChart` / `ExploreBySight` server config setting; a missing key keeps the existing mod-config value, independently for each switch. Older worlds are not migrated or given new keys. Both switches may be changed after creation; restart the world to apply changes. The server sends effective exploration and chart access through the existing atlas sync. Global `wmap.json` defaults are not overwritten by world choices. Disabling sharing keeps the existing creative/`wmap` privilege exceptions; sight exploration still requires access to predicted terrain.

| Setting | Default | Meaning |
| --- | --- | --- |
| `AllowAllPlayers` | `false` | Server: development mode for everyone |
| `BarometerRange` | `3000` | Legacy local reach (500–12000), retained for compatibility; no longer limits the worldwide display |
| `ShareWorldChart` | `true` | Server: send the world chart and predicted detail to every player |
| `PredictTerrain` | `true` | Server: predict terrain from Earth Map or Cylinder World. `false` charts recorded terrain only, for terrain generators Wmap cannot predict |
| `WorldChartSize` | `8192` | Server: largest side of the world chart (512–8192). 4096 uses a quarter of the memory |
| `BakeThreads` | `0` | Server: chart workers; 0 uses half the processors. They run at below-normal priority |
| `DiskCacheMiB` | `512` | Detail-tile cache per world (64–4096) |
| `CacheRevision` | `1` | Server: increment to rebuild all predictions |
| `DevelopmentMode` | `true` | Client: the Creative map is on; set from the game mode whenever it changes |
| `DefaultStyle` | `0` | Client: 0 chart, 1 terrain |
| `DecimalDegrees` | `false` | Client: coordinate format |
| `ExploreRadius` | `1536` | Client: blocks charted around you in gameplay, independent of view distance (32–4096) |
| `ExploreBySight` | `true` | Server: chart the game map by line of sight instead of the circle; applies to every player. A `wmap.json` written before 0.4.5 keeps `false` until edited |
| `SightDistance` | `2048` | Server: blocks line of sight reaches at most (512–16384); 4096 before 0.4.9. The planet's curve usually ends it sooner |
| `PlanetScale` | `1` | Server: the planet's radius for line of sight and far terrain, as a multiple of the world's circumference / 2π (0–16; 0.4.11). Larger shows farther; `0` is flat, as before 0.4.11 |
| `SightHoleSize` | `256` | Server: enclosed hidden patches up to the area of a square this wide are charted too (0–1024; 0 off) |
| `SightCircle` | `64` | Server: blocks always charted around a player with line of sight (16–1536) |
| `ResidentTiles` | `384` | Client: detail tiles kept in memory (128–768) |
| `AtlasPages` | `2` | Client: 2048² detail pages on the GPU, 16 MiB each (1–4) |
| `DetailBias` | `1` | Client: 1 draws detail up to one texel per pixel, 0 halves detail work |
| `ObservedRadiusChunks` | `4` | Client: loaded-terrain recording radius (1–8) |
| `RecordObservedTerrain` | `true` | Client: record nearby loaded terrain |
| `ReplaceMinimap` | `true` | Client: draw Wmap terrain in the HUD minimap, with its native controls and markers |
| `RightClickTeleport` | `true` | Client: in creative mode, a right-click on the atlas offers Teleport here |
| `AutoMarks` | `true` | Server: make automatic markers for players who ask (0.4.6) |
| `AutoMarkLimit` | `1000` | Server: automatic markers a player may have (10–10000); none are made past it until some are deleted |
| `PaperMaps` | `true` | Server: players draw and read paper maps (0.4.8) |
| `PaperMapMaxMiB` | `16` | Server: largest paper map (1–256 MiB); the drawer is told to leave out recorded terrain above it |
| `AutoMarkOnUse` / `AutoMarkOnSneak` | `true` | Client: mark when you start harvesting or breaking / when you look at something while sneaking (the Auto-mark list) |
| `AutoMarkKinds` | all | Client: kinds marked automatically (the Auto-mark list) |
| `ShowWaypoints`, `ShowAutoMarks`, `ShowProspecting`, `ShowOtherLayers` | `true` | Client: the Show list |
| `ProspectingOre` | `""` | Client: the ore filter; empty shows every reading |
| `CoordinatesHud` | `true` | Client: latitude, longitude, local and UTC time and heading in the top left corner (Ctrl+Shift+V) |
| `CoordinatesHudTime` | `true` | Client: the clocks (HH:MM) in that box. Local: Astra Terra's local solar time where it is installed, else the solar time at Earth's or the map's longitude, else the game's hour. UTC: the world's universal clock, the solar time at longitude 0 (Astra Terra's universal time, the game's hour of day); shown only where it differs from the local clock |
| `DayNightOverlay` | `false` | Client: shade the night side of the world on the atlas (the Day / Night button) |
| `WindLayer` | `1` | Client: remembered worldwide layer; 0 off, 1 weather now, 2 prevailing now, 3–14 January–December. Gameplay requires a barometer or wind-sight grant |
| `FarTerrain` | `true` | Client: draw far terrain past the view distance |
| `FarTerrainDistance` | `8192` | Client: blocks to the far terrain's horizon (2048–16384) |
| `FarTerrainDetail` | `1` | Client: far terrain mesh, 0 low, 1 normal, 2 high (finer near rings; little gain on large worlds; the near band uses its own finer rings) |
| `FarTerrainNearBand` | `true` | Client: draw far terrain from 4-block detail tiles out to `NearBandDistance` (Earth Map; client and server 0.4.0) |
| `NearBandDistance` | `1536` | Client: blocks to the near band's outer edge (768–1536) |
| `FarTerrainClimate` | `true` | Client: the game's climate colours and forest canopy on far terrain (Earth Map; client and server 0.4.0) |
| `MidDistanceRecorded` | `true` | Client (prototype): record every loaded chunk out to the view distance with colours and light, and draw recorded terrain and structures in far terrain |
| `MidDistanceTerraces` | `true` | Client (prototype): terraces from far terrain's start to `TerraceDistance` |
| `MidDistanceLights` | `true` | Client (prototype): recorded lights as glows at night out to `LightDistance` |
| `TerraceDistance` | `768` | Client (prototype): blocks to the terraces' outer edge (256–768) |
| `LightDistance` | `3072` | Client (prototype): blocks within which recorded lights are drawn (1024–3584) |

## Memory and storage

The mid-distance view (prototype) adds about 30 MiB of video memory: 24.8 MiB for the recorded tiles' two atlases and 5.3 MiB for the terraces' cell map. The world chart is 128 MiB of video memory at 8192 × 4096, plus 43 MiB of mipmaps; `WorldChartSize: 4096` needs a quarter of that. Detail pages add 16 MiB each, including one HUD minimap page; far terrain adds about 1.6 MiB of mesh buffers (2.6 MiB with the near band) and shares the world chart, and the near band and climate tiles 12.4 MiB of textures; the exploration mask is about 27 MiB with mipmaps, and the latitude/longitude lookup 8 MiB. The atlas and minimap share the world chart and exploration textures. Charting needs roughly 300 MiB of temporary server memory once.

Under the game's `Wmap` data folder: `server/<hash>` and `client/<hash>` hold the world chart and detail tiles, which are disposable caches. `observed/<hash>` holds your exploration and recorded terrain; back it up if it matters.

## Known limitations and follow-ups

- 0.3.1 fixed the chart download but its interactive retry still showed a blank GUI. 0.3.2 fixes the game's shared camera-matrix buffer and adds the HUD minimap. The exact matrix failure is reproduced in a GPU regression; the 0.3.2 interactive retry remains pending. See [testing](docs/testing.md).
- Charting an Earth world takes about 70 s once. Most of that is lock contention in Earth Map's terrain tile cache (`EarthData.GetTile` takes a global lock and reorders an LRU list for each of the roughly 20 grid reads per column). A lock-free read path there would speed up this bake and probably Earth's own multithreaded world generation.
- Cylinder 0.11's seasonal sea ice is not shown by season.
- Graticule labels sample the geography along the map edges; very close to Earth's Pacific shear they can sit a few pixels off their lines.
- Recordings made before 0.3.8 stored the water surface as the seabed, so recorded seas show no depth until you pass there again.
- Other terrain generators are not predicted. Terra Prety's known issues (flat, approximate chart, no far terrain) are listed in [terrain generators](docs/terrain-generators.md#known-compatibility-issues); support is deferred and untested.
- Physical charts, sextant fixes, routes and the captain's table are not implemented yet; waypoints are the game's own (0.4.6). The atlas's Show filters and zoom fading do not apply to the HUD minimap, which the game draws. Automatic markers know the game's own blocks only: other mods' berries or ores are not marked. The exploration mask is where survey results will be written.
- A paper map that is burned or lost leaves its content in the world save: the server cannot know no copy is left in a chest or on the ground. Maps cannot yet be previewed on the atlas before copying, cut to an area, or copied waypoint by waypoint. The charted area and terrain on a map come from the drawer's client and are checked for format only, as a player's own atlas is: a modified client could draw a false map.
- Any change to the installed mods' versions charts the world again: the chart's cache key holds every mod, since Wmap cannot tell which ones shape terrain.
- A 0.4.3 client on a 0.4.4 server reads its "busy" answer (all tile workers busy) as an error and waits 10 s before asking again; 0.4.4 clients ask again after 1.5 s.
- A strip of the world under 4 blocks wide at its east or south edge (a ring that is not a multiple of 4 blocks) gets no four-block recording at the coarser levels.
- Smaller open items from the 0.4.4 review: the disk cache trims the oldest-written tiles, not the least used; the client accepts world and climate chart sizes up to 512 MiB each from the server; a server with another protocol version stays silent, so the client keeps showing "requesting atlas access"; temporary files left by a crash during a cache write are not cleaned up; the HUD player arrows allocate a little per frame; the atlas's helper map element for other mods' layers is not disposed.

## Build and test

Windows, .NET 8 SDK and .NET 10 runtime, Vintage Story 1.22.7:

```powershell
.\tools\Prepare-References.ps1 -GamePath "$env:APPDATA\Vintagestory"
dotnet build Wmap.csproj -c Release
dotnet run --project tests/WmapTests/WmapTests.csproj -c Release
dotnet build tests/SmokeMod/SmokeMod.csproj -c Release
.\tools\Run-Smoke.ps1 -Profile earth -OverviewWidth 8192 -PolarMode earth
```

The core suite checks the distance transform against brute force, the seam, encodings, codecs, the bake, rivers and detail selection. The smoke runner starts an isolated local server with the sibling Earth Map and Cylinder World releases, bakes the real world chart, and runs client, permission, GUI and polar checks. It also renders the production shader in a hidden OpenGL context and saves the images. Never install the smoke-test mod in a normal world: it shuts its server down when done. Results are in [testing](docs/testing.md).

To design or tune a style without starting the game, run the browser preview against a world raster from an Earth smoke run. It uses the newest Earth run in `.local` unless you pass a raster path. It loads `src/Client/Shaders/map.frag` directly; press R in the page after editing it. S switches style, D switches mode, and the URL keeps the view.

```powershell
python tools\shader-preview\serve.py
```
