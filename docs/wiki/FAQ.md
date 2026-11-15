# FAQ

**The atlas is blank parchment.**
In survival the atlas shows only land you have seen; explore, and it fills in. Press **Space** to centre on yourself.
In creative mode, press **Creative / Game** for the whole world. On a new world with Earth or Cylinder World, wait for
the server to finish charting the world, a minute or two (on the Creative map the line under the atlas shows the
progress).

**How do I see the whole map?**
Switch to creative mode, or have an admin grant you the `wmap` privilege (`/player NAME privilege grant wmap`), or set
`AllowAllPlayers` to `true` in the server's `wmap.json`. Being the server's admin, or the host of a single-player world,
is not enough on its own.

**I am the only player. Why can't I see the whole map in survival?**
From 0.5.4 a single-player survival world follows the same rule as a server: the unexplored map is for creative only.

**I updated World Map and my old world's map is wrong.**
World Map 0.5.4 does not support worlds made with earlier versions. Start a new world.

**The map shows land I have never walked to.**
In survival the map charts what you can **see**, not only where you walk: from a hilltop or a ship you chart the land in
sight out to 2048 blocks (`SightDistance`). The far view on the horizon shows even farther, out to 8192 blocks, but only
what is in your line of sight; to put that land on your map, get closer. The map also fills in by squares (up to
about 490 blocks on a 1:10 Earth), so its edge runs a little past what you saw.

**There is no far view.**
It needs Earth. It is off if Farseer is installed, until the server has charted the world, and if `FarTerrain` is `false`
in your `wmap.json`. In survival it shows only land you have seen; land you arrive over for the first time appears a few
seconds later. The client log says why it is not drawn, on lines starting `[wmap] Far terrain:`.

**The winds don't show.**
Winds need the Sea mod. On the game chart you also need a Jonas barometer in your hotbar or backpack. Check that the
**Winds** list is not set to **Off**.

**The coordinates box is gone.**
Press **Ctrl+Shift+V**. It shows latitude and longitude only on worlds with geography (Earth, or Cylinder World's planet
layout); elsewhere it shows the time alone.

**"Teleport here" is missing or doesn't work.**
It is offered in creative mode only, needs the `tp` privilege, and `RightClickTeleport` on in your `wmap.json`.

**A paper map is too big to draw.**
Leave out the recorded terrain, or ask the server to raise `PaperMapMaxMiB`.

**The game runs slower with World Map.**
Lower `FarTerrainDistance`, set `FarTerrainDetail` to `0`, or switch off `FarTerrainNearBand` or the mid-distance view
(`.wmap mid terraces off` and so on). On the server, `WorldChartSize: 4096` uses a quarter of the memory for the world
chart (and in each player's game).

**The first start of a world takes long.**
The server charts the whole world once, in the background, at below-normal priority: about a minute or two on a fast
computer. You can play meanwhile. The next start reuses it, unless the installed mods changed version.

**Another terrain generator draws a wrong map.**
World Map predicts the world only from Earth or Cylinder World. Set `PredictTerrain` to `false` in the server's
`wmap.json`; the map then shows only the land players record.

**Where are World Map's files?**
`ModConfig/wmap.json` (settings), and the `Wmap` folder in the game's data folder: `server/` and `client/` (caches you
may delete), and `observed/` (your recorded terrain). What each player has charted, and paper maps, are in the world save.
