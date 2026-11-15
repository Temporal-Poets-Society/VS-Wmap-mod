# The atlas and minimap

## Opening the atlas

Press your world-map key (**M** unless you changed it) or type `.wmap`. World Map takes over the game's world-map key;
`.wmap vanilla` still opens the game's own map. The atlas opens in the **Chart** style, centred on you and following
you. A style you switch to stays until you leave the world or your game mode changes; panning and zoom stay as you leave
them while the window exists.

The line under the map shows your position and the position under the cursor: latitude and longitude on worlds with
geography (Earth, or Cylinder World's planet layout), block coordinates on other worlds. The ° / °′ button switches
between decimal degrees and degrees and minutes.

## Two styles

- **Chart:** a nautical chart drawn from the terrain: parchment with darkened edges, ink coastlines with waterlines
  offshore, stippled shallows, tinted shelves and depth contours, land contours every 10 blocks, hatching on highland
  slopes, rivers, sea ice, a grid of latitude and longitude with the equator, tropics and polar circles, a compass rose
  and a scale bar in sea miles.
- **Terrain:** a topographic map with hill shading and sea depths. With Earth, land is coloured by its climate (desert
  sand, steppe, grassland, forest, tundra, snow, with rock and snow on high ground); otherwise by height.

**Chart / Terrain** switches between them instantly. Lines keep their thickness on screen at every zoom.

## The game chart and the Creative map

- **Game:** the chart shows only what you have seen; the rest is blank parchment with the grid. This is what every
  survival player sees. See [Exploration](Exploration).
- **Creative:** the whole predicted world, for building and checking worlds.

The atlas follows your game mode: in creative mode it opens on the Creative map, in survival on the game chart. The
**Creative / Game** button switches between the two. It is there in creative mode, for players with the server's `wmap`
privilege, and for everyone when the server sets `AllowAllPlayers`. Being a server admin, or the host of a single-player
world, is not enough on its own: switch to creative or grant yourself `wmap` (see [Commands and keys](Commands-and-keys)).

## Controls

| Control | What it does |
| --- | --- |
| Left drag | Pan (stops following) |
| Mouse wheel | Zoom around the cursor |
| **Space** | Centre on yourself and follow |
| **Follow** | Switch following on or off |
| **Home** or **Whole world** | Fit the whole world |
| Right-click | Add a waypoint there with the game's own dialog (icon, colour, title). On a waypoint: edit it. On a prospecting reading: delete it, as on the game's map |
| Right-click, creative mode | A small menu with the position, **Add waypoint…** and **Teleport here** (onto the top block or the water; needs the `tp` privilege, and `RightClickTeleport` on in your own `wmap.json`) |
| **Show** | What the atlas draws: your waypoints, automatic markers, prospecting readings, other mods' layers (ships, creatures) |
| **Ores** | Show only the prospecting readings that mention one ore, coloured by its strength |
| **Auto-mark** | When and what to mark automatically (below) |
| **Day / Night** | Shade the night side of the world, with the line where day turns to night |
| **Winds** | With Sea: the winds now or month by month (see [Barometer and winds](Barometer-and-winds)) |
| **Ore heat** | Creative map only, when the server allows it (see [Ore heat maps](Ore-heat-maps)) |
| **Esc** | Close the atlas, or the right-click menu |

Waypoints are the game's own: the same as on the minimap. Your own waypoints stay and shrink
as you zoom out; automatic markers and prospecting readings fade out when you zoom far out (a pinned one stays).

## Automatic markers

World Map can make waypoints for you. In the **Auto-mark** list choose when:

- **When harvesting:** when you start to pick or break something (a mushroom, a berry bush, surface ore);
- **When sneaking:** when you look at something while sneaking;

and which kinds: berries, mushrooms, surface ore, wild beehives, resin, fruit trees, clay, translocators and traders.
The server makes the marker, unless one with the same name is already near. It knows the game's own blocks and creatures
only: berries or ores added by other mods are not marked. A server can switch this off (`AutoMarks`) and limits how many
automatic markers a player may have (`AutoMarkLimit`, 1000).

## The HUD minimap

The game's minimap draws in World Map's style, with the same exploration, and keeps the game's own player icons,
markers and hover text. **F6** shows or hides it and **Ctrl+F6** moves it to another corner, as in the game. Set
`ReplaceMinimap` to `false` (and restart) to keep the game's own minimap. The atlas's Show list and zoom fading do not
apply to the minimap, which the game draws.

## How the map is made

When a world with Earth or Cylinder World first starts, the server **charts the whole world once** into one large image,
the world chart. That takes about a minute or two on a fast computer; on the Creative map the line under the atlas shows the progress,
and close-up detail works meanwhile. A game with the whole map downloads the chart once and keeps it (about 12 MiB on a
large world). A survival game receives only the parts of land you have seen, again each session, and keeps none of it on
disk.

The chart has a limited resolution (one texel is 25 to 489 blocks, depending on the world's size). Zoomed in further,
the server makes **detail tiles** on demand, down to a block; the server keeps them on disk, and so does a game with the
whole map. Where you have been,
the terrain you recorded is drawn over the prediction, so story locations, your buildings and your digging show as they
are.

Worlds without Earth or Cylinder World have no world chart: the atlas draws only the terrain recorded around you.

## Limits

- The prediction does not show structures, ruins or what players have built until you have been there.
- The atlas, exploration, the coordinates box, the far view and teleporting from the map work on the world's surface
  only, not in other dimensions.
- Physical charts, sextant fixes, surveying and routes are planned, not built.
