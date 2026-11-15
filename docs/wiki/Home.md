# World Map

World Map (Wmap) replaces Vintage Story's map with an **atlas**: a nautical chart or a shaded terrain map that you can zoom
from the whole world down to a harbour without loading screens. In survival it fills in as you explore; in creative it
shows the whole world. It also keeps your HUD minimap in the same style.

With the other mods of the suite it does more:

- **Earth:** the whole planet is charted from Earth's terrain, with its rivers, lakes and climate colours, and the world
  on the horizon is drawn past your view distance, out to 8 km (the **far view**).
- **Cylinder World:** the map wraps east to west across the seam, without a jump.
- **Sea:** a **Jonas barometer** shows the winds across the whole world, now and month by month; with Earth it also
  reads the air pressure.
- **Astra Terra for Earth:** the coordinates box shows the local solar time its sky follows.

You can also hand **paper maps** of what you have discovered to other players, mark berries, ores and traders
automatically, and read latitude, longitude and the time of day in a small box in the corner.

This wiki describes **World Map 0.5.4**.

## What it needs

- Vintage Story **1.22.0** or later. It is built and tested on **1.22.7**.
- No other mod is required. Earth, Cylinder World, Sea and Astra Terra for Earth are optional (see
  [Compatibility](Compatibility)).
- In multiplayer, the server and **every** player need World Map, and the **same version** of it.

## Install

1. Quit the game.
2. Copy `wmap_0.5.4.zip` into the `Mods` folder of your Vintage Story data folder (on Windows
   `%APPDATA%\VintagestoryData\Mods`). Replace any older World Map zip. Leave the zip as it is; don't unpack it.
3. On a server, put the same zip in the server's `Mods` folder.
4. **Start a new world.** World Map 0.5.4 does not support worlds made with earlier versions of World Map.

## Quick start

- Press **M** (or whatever your world-map key is) or type `.wmap` to open the atlas. **Esc** closes it.
- Drag to pan, use the mouse wheel to zoom, press **Space** to follow yourself and **Home** to see the whole world.
- **Chart / Terrain** switches between the nautical chart and the terrain map.
- In survival the atlas shows only the land you have seen. In creative mode the **Creative / Game** button switches
  between the whole world and your own chart.
- Right-click on the atlas to add a waypoint.
- **F6** shows or hides the minimap, **Ctrl+F6** moves it to another corner.
- **Ctrl+Shift+V** shows or hides the coordinates box.

The first time a world with Earth or Cylinder World starts, the server charts the whole world once in the background.
That takes a minute or two. On the Creative map the line under the atlas shows the progress; in survival the map simply
fills in when it is done. Later starts reuse it.

## Pages

- [The atlas and minimap](The-atlas-and-minimap): styles, controls, waypoints, automatic markers
- [Exploration](Exploration): what you see in survival and in creative
- [The far view](The-far-view): the world on the horizon
- [Coordinates and clocks](Coordinates-and-clocks)
- [Barometer and winds](Barometer-and-winds)
- [Paper maps](Paper-maps)
- [Ore heat maps](Ore-heat-maps): for server owners and creative players, off unless the server turns them on
- [Settings](Settings): the world option and every setting in `wmap.json`
- [Commands and keys](Commands-and-keys)
- [Compatibility](Compatibility): other mods, saves, multiplayer
- [FAQ](FAQ)

## Versions and problems

World Map is in development (0.x). What changed in each version is in the `CHANGELOG.md` inside the zip and in this
repository. Report problems on World Map's page on the Vintage Story Mod DB, or in this repository's issues if they are
open. A screenshot, the World Map version, your other mods and the lines with `[wmap]` from the game's logs help most.
