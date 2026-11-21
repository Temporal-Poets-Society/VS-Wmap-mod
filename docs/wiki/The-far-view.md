# The far view

With **Earth**, World Map draws the world past your view distance as a low, hazy landscape out to 8 km: coasts,
islands and mountain ranges on the horizon, and the sea as a flat surface. It is lit by the game's sun, takes the
game's sky colour and weather fog, and fades into the horizon by day, at night and in rain or fog. The game's own chunks
fade out at three quarters of your view distance; the far view starts just inside that, so the real terrain covers it
where both exist.

- **The planet is round:** past your view distance, land sinks below the horizon with distance. From a ship you see
  the islands and mountains near you, not the coast of another continent.
- **Near band:** out to 1536 blocks the far view is drawn from fine detail (one point every four blocks), so the hills,
  cliffs, valleys and coasts nearest the real chunks have their real shape and height. Beyond that it uses the coarser
  world chart.
- **Climate colours:** grass, leaves and water take the game's own climate tints, through the seasons. Forests stand
  at their height; high ground turns to scree, rock and snow by its climate.
- **Where you have been,** the land is drawn as you saw it, at its recorded height and colour.

It is drawn from the **world chart**, which the server makes once for each world when it first starts (a minute or two
on a 1:10 world, longer while the new world's first chunks are being made; again after an update of Earth, Cylinder
World or World Map). Until the chart is ready there is no far view: `.wmap far` says how far charting has come.

Switch the far view off and on with **Ctrl+Shift+F**, the atlas's **Far view** button or `.wmap far off` / `.wmap far on`.

It needs Earth: Cylinder World alone predicts only coastlines with invented heights, so the far view stays off there.
It is drawn on the world's surface only, and not at all when your view distance comes within 64 blocks of
`FarTerrainDistance`.
It shows the predicted terrain, not structures, ruins or what players have built (except, nearby, what you recorded;
see the mid-distance view below).

## In survival

A survival player's far view shows only **land they have seen**: what is in their line of sight out to 8192 blocks, and
what they have charted before. Land hidden behind a ridge, or land you arrive over for the first time, appears a few
seconds after you see it. Creative players, players with the `wmap` privilege and everyone under the server's
`AllowAllPlayers` see everything to the horizon. See [Exploration](Exploration).

## Settings

These are in your own `wmap.json` (see [Settings](Settings)); restart the game after changing them.

| Setting | Default | What it does |
| --- | --- | --- |
| `FarTerrain` | `true` | Draw the far view at all |
| `FarTerrainDistance` | `8192` | Blocks to its horizon (2048 to 16384). In survival nothing past 8192 blocks is in sight; only land you charted before is drawn there |
| `FarTerrainDetail` | `1` | The mesh: 0 low, 1 normal, 2 high (finer near you; little gain on large worlds) |
| `FarTerrainNearBand` | `true` | Draw the near band from fine detail |
| `NearBandDistance` | `1536` | Blocks to the near band's outer edge (768 to 1536) |
| `FarTerrainClimate` | `true` | The game's climate colours and forests on the far view |

The server's `PlanetScale` (default `1`) sets how round the planet is: larger shows farther, `0` is flat.

## The mid-distance view (a prototype)

Between your chunks and 1536 blocks, World Map can also draw what **you recorded**, so a town, a harbour or your own
base shows past your chunks, and their lights at night:

- **Recorded terrain** (`MidDistanceRecorded`): the chunks your game has loaded, with their colours and light; built
  structures past the terraces as blocks at their height.
- **Terraces** (`MidDistanceTerraces`): from where your chunks fade out to `TerraceDistance` (768 blocks), two-block
  cells at whole-block heights. With a view distance of 1024 or more there are none: your chunks reach past them.
- **Distant lights** (`MidDistanceLights`): torches, lanterns and lighthouses you have seen, as glows at night, out to
  `LightDistance` (3072 blocks).

It is experimental. It cannot show overhangs, bridges, thin things like masts and fences, ships and creatures, or what
other players built since you last passed. `.wmap mid` shows what it draws and what it costs; `.wmap mid terraces off`
and so on switch a part off without restarting (see [Commands and keys](Commands-and-keys)).

## Performance

The far view reuses the world chart the atlas already has. On a mid-range graphics card (an AMD RX 6600 at 1920 x 1080,
view distance 384, a 1:10 Earth world) it takes about 1.3 ms of the graphics card a frame with the near band and the
mid-distance view (2.2 ms before 0.5.5), half a millisecond of it the terraces. To make it cheaper, switch off the
terraces (`.wmap mid terraces off`), lower `FarTerrainDistance`, set `FarTerrainDetail` to `0`, or switch off
`FarTerrainNearBand` or the other mid-distance parts; Ctrl+Shift+F switches the far view off altogether. It
uses roughly 30 MiB of extra video memory for the mid-distance view and about 12 MiB for the near band, besides the world
chart itself (about 170 MiB at full size; the server's `WorldChartSize: 4096` needs a quarter of that).

## Other far-view mods

If **Farseer** is installed, World Map leaves the far view to it. Other far-view or level-of-detail mods have not been
tested together with World Map.
