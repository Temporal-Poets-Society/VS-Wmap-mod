# Compatibility

## The game

Vintage Story 1.22.0 or later; built and tested on 1.22.7. Other game versions have not been checked.

## Multiplayer

The server and every player need World Map, and the **same version**: with a 0.5.3 game on a 0.5.4 server (or the
other way round) the atlas does not open, and says why when you press M (not yet checked in a real game). The server
charts the world and decides what each survival player may see; each player's game draws it.

Not checked yet: two or more players on one server together, and graphics cards other than AMD.

## The suite's mods

All of these are optional. World Map was checked with the versions in brackets.

| Mod | What it adds to World Map |
| --- | --- |
| **Earth** (2.0.0) | The whole planet charted from Earth's terrain, with its rivers and lakes; latitude and longitude; the far view; climate colours on the Terrain style and the far view; the air pressure on the Jonas barometer; Earth's winds through Sea |
| **Cylinder World** (1.1.1) | The map wraps east to west across the seam without a jump; the seam band, the polar ice and the walls. Without Earth: an approximate map of continents and oceans with invented heights, and no far view |
| **Sea** (0.6.1) | The winds on the atlas and minimap, through the Jonas barometer; Sea can shade the far sea |
| **Hydrology** (0.6.2) | No direct link: its rivers and water show on the map where you have been, as recorded terrain |
| **Astra Terra for Earth** | The local solar time in the coordinates box follows its sky |

## Other mods

- **Farseer:** if it is installed, World Map leaves the far view to it.
- **Fast Map:** not needed; using both together has not been tested.
- **Other terrain generators** (Terra Prety and the like) are not predicted. With Cylinder World, World Map still reads
  the installed ocean map, so such continents appear roughly, without heights. If the map misleads, set `PredictTerrain`
  to `false` in the server's `wmap.json`: the map then shows only what players record.
- **Ship mods** can let players aboard see the winds from a barometer mounted on the ship, if they support it.
- **Other mods' map layers** (ships, creatures and the like that draw on the game's map) are drawn on the atlas too
  (the **Show** list).
- **Automatic markers** know the game's own blocks and creatures only.

## Saves

- **World Map 0.5.4 does not support worlds made with earlier versions of World Map.** Start a new world.
- What each player has charted, and the paper maps drawn, are kept in the world save.
- The world chart and detail tiles are kept under the game's data folder, in `Wmap/server/...` (on the server) and
  `Wmap/client/...` (in each player's game). They are caches: deleting them only makes World Map chart and download
  them again.
- Your recorded terrain is kept in `Wmap/observed/...` in your game's data folder. Back it up if it matters to you.
- When the installed mods change version, the server charts the world again once. Your recorded terrain is kept: it
  belongs to the world, not to the mod versions.
