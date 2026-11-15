# Settings

World Map has one option on the world-creation screen and many in its config file, `wmap.json`.

## The world option

On the world-creation screen, under **Customize**, on the **Earth** tab:

| Option | Values | Default | What it does |
| --- | --- | --- | --- |
| **Wmap: explore by sight** (`wmapExploreBySight`) | On, Off | On | On: the map charts what is visible over the ground, with a small circle around you. Off: it charts a plain circle around where you have been (`ExploreRadius`) |

It can be changed after the world is made, with `/worldconfig wmapExploreBySight false` (or `true`), and takes effect
when the world restarts. Without predicted terrain (no Earth or Cylinder World, or `PredictTerrain` off) the circle
applies anyway. The world's value overrides `ExploreBySight` in the server's `wmap.json`; it never writes to that file.

The option "Wmap: shared world chart" of earlier versions is gone: from 0.5.4 the unexplored map is for creative only.

## The config file

`ModConfig/wmap.json` in the game's data folder (on Windows `%APPDATA%\VintagestoryData\ModConfig\wmap.json`; on a
server, the server's data folder). It is made the first time World Map loads. Restart the game or server after editing
it; values out of range are brought into range when it loads.

The **server** reads the server settings from its own file; **your game** reads the client settings from yours. In
single player both come from the same file. The atlas's buttons and lists (degrees, day and night, winds, Show, Ores,
Auto-mark) and Ctrl+Shift+V save their choices to your file as you use them.

### Server: access and exploration

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `AllowAllPlayers` | `false` | | Everyone gets the whole map (the Creative map), as creative players do |
| `ExploreBySight` | `true` | | Chart by line of sight (see the world option above, which overrides it) |
| `SightDistance` | `2048` | 512–16384 | How far line of sight charts, in blocks; the planet's curve usually ends it sooner |
| `SightCircle` | `64` | 16–1536 | Blocks always charted around a player with line of sight |
| `SightHoleSize` | `256` | 0–1024 | Small hidden patches that what a player sees encloses are charted too, up to the area of a square this wide (0: off) |
| `ExploreRadius` | `1536` | 32–4096 | Without line of sight: blocks charted around a player |
| `PlanetScale` | `1` | 0–16 | How round the planet is, for line of sight and the far view: larger shows farther, 0 is flat |
| `ShareWorldChart` | `true` | | No longer used (0.5.4) |

### Server: the world chart

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `PredictTerrain` | `true` | | Chart the world from Earth or Cylinder World. `false` charts only the terrain players record, for terrain generators World Map cannot predict |
| `WorldChartSize` | `8192` | 512–8192 | Largest side of the world chart, in texels. `4096` uses a quarter of the memory, with less detail when zoomed out |
| `BakeThreads` | `0` | 0–64 | Threads that chart the world; 0 uses half the processors. They run at below-normal priority |
| `DiskCacheMiB` | `512` | 64–4096 | Disk space for detail tiles, per world (your game uses the same setting for its own copy) |
| `CacheRevision` | `1` | | Raise it to make the server chart the world again from scratch |

### Server: markers, paper maps, ore heat maps

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `AutoMarks` | `true` | | Make automatic markers for players who ask for them |
| `AutoMarkLimit` | `1000` | 10–10000 | Automatic markers a player may have; none are made past it until some are deleted |
| `PaperMaps` | `true` | | Players may draw and read paper maps |
| `PaperMapMaxMiB` | `16` | 1–256 | The largest paper map, in MiB |
| `OreHeatMaps` | `false` | | Ore heat maps on the Creative map and the `/wmapore` command |
| `BarometerRange` | `3000` | 500–12000 | Kept for older configs; no longer limits anything |

### Your game: the atlas and minimap

| Setting | Default | What it does |
| --- | --- | --- |
| `DefaultStyle` | `0` | Not for editing: the atlas starts in Chart every session (0 Chart, 1 Terrain) |
| `DevelopmentMode` | `true` | Not for editing: set from your game mode (the Creative map in creative mode) |
| `DecimalDegrees` | `false` | Decimal degrees instead of degrees and minutes |
| `DayNightOverlay` | `false` | Shade the night side of the world (Day / Night) |
| `ReplaceMinimap` | `true` | Draw the HUD minimap in World Map's style; `false` keeps the game's own minimap |
| `RightClickTeleport` | `true` | In creative mode, offer **Teleport here** on right-click (needs the `tp` privilege) |
| `ShowWaypoints`, `ShowAutoMarks`, `ShowProspecting`, `ShowOtherLayers` | `true` | The Show list |
| `ProspectingOre` | `""` | The Ores list; empty shows every reading |
| `AutoMarkOnUse`, `AutoMarkOnSneak` | `true` | Mark things when you start harvesting or breaking them / when you look at them while sneaking |
| `AutoMarkKinds` | all | The kinds marked: `berries`, `mushrooms`, `ores`, `beehives`, `resin`, `fruittrees`, `clay`, `translocators`, `traders` |
| `WindLayer` | `1` | The Winds list: 0 off, 1 weather now, 2 prevailing now, 3–14 January–December |
| `CoordinatesHud` | `true` | The coordinates box (Ctrl+Shift+V) |
| `CoordinatesHudTime` | `true` | The clocks in the coordinates box |

### Your game: recording and memory

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `RecordObservedTerrain` | `true` | | Record the land your game loads, so the map shows it as it is |
| `ObservedRadiusChunks` | `4` | 1–8 | How far around you, in chunks, the land is recorded when `MidDistanceRecorded` is off; with it on (the default) every chunk your game loads is recorded |
| `ResidentTiles` | `384` | 128–768 | Detail tiles kept in memory |
| `AtlasPages` | `2` | 1–4 | Detail pages on the graphics card, 16 MiB each |
| `DetailBias` | `1` | 0–1 | 1 draws detail up to one texel per screen pixel; 0 halves the detail work |

### Your game: the far view

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `FarTerrain` | `true` | | Draw the far view |
| `FarTerrainDistance` | `8192` | 2048–16384 | Blocks to its horizon |
| `FarTerrainDetail` | `1` | 0–2 | Mesh detail: 0 low, 1 normal, 2 high |
| `FarTerrainNearBand` | `true` | | Fine detail out to `NearBandDistance` |
| `NearBandDistance` | `1536` | 768–1536 | Blocks to the near band's outer edge |
| `FarTerrainClimate` | `true` | | The game's climate colours and forests on the far view |
| `MidDistanceRecorded` | `true` | | Prototype: your recorded terrain and structures in the far view |
| `MidDistanceTerraces` | `true` | | Prototype: terraces past your chunks |
| `MidDistanceLights` | `true` | | Prototype: recorded lights as glows at night |
| `TerraceDistance` | `768` | 256–768 | Blocks to the terraces' outer edge |
| `LightDistance` | `3072` | 1024–3584 | Blocks within which recorded lights show |

See [The far view](The-far-view) for what these do.

## Privileges

- `wmap`: the whole map (the Creative map and its **Creative / Game** button) in any game mode, and `/wmapore`. Grant it
  with `/player NAME privilege grant wmap` (which needs the game's `grantrevoke` privilege). Server admins do not get it
  automatically. It sends that player the whole predicted world, so it lifts the survival protection for them; so does
  `AllowAllPlayers` for everyone.
- `tp`: the game's teleport privilege, needed for **Teleport here** on the atlas.
