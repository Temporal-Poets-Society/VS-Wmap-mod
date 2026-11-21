# Ore heat maps

Ore heat maps colour the Creative map by where the world's ore deposits are, for checking or debugging ore generation.
They ship **switched off**: they are a tool for checking ore generation, not part of normal play. A server owner can
turn them on.

## Turning them on

In the server's `wmap.json` set `"OreHeatMaps": true` and restart. They are offered only on the Creative map: to players
in creative mode, players with the `wmap` privilege, and everyone when the server sets `AllowAllPlayers`. They never show
on the game chart.

## Using them

Choose an ore in the **Ore heat** list above the atlas:

- **One ore:** about how many blocks of that ore a chunk column holds there (32 × 32 blocks, from the surface down),
  from 1 (blue) to 10,000 (red) on a logarithmic scale, and what the prospecting pick's density reading would say there
  (its word and factor). The pick lists an ore only above a factor of 0.025; smaller amounts it calls "miniscule".
- **Ores the pick lists:** how many ores a density reading would list there (grey: none). Offered only where every ore
  comes from Earth's regional ores, whose generators answer the pick's own question anywhere.

The map follows every zoom: zoomed in, each cell is a chunk; zoomed out, the cells grow so they stay readable. The server
works the map out in tiles, every ore at once, and keeps them with the world's map tiles, so a place is worked out once
and switching ores is instant. With Earth's regional ores, choosing an ore the first time also stores the coarse levels of the
whole world in the background. A tile that is not there yet is drawn from a coarser one until it arrives. The legend sits
at the top right, and the line under the map says what the cell under the cursor holds.

## Checking against the pick

`/wmapore` (needs the full map and `OreHeatMaps` on) prints, for the chunk column you stand in, the heat map's number
beside the prospecting pick's own reading there, for every ore; `/wmapore ORE` for one ore. See
[Commands and keys](Commands-and-keys).

## How exact it is

The numbers come from the world's own ore generators, for the same deposits the prospecting pick asks about. With Earth's
regional ores they answer anywhere on the planet, as they answer the pick. Where Earth keeps the game's own rock layers
in a column, a cell stays unknown until that chunk is loaded. From Earth 2.0.1 the game's own rock layers are Earth's
default (**Terrain · Rocks and ground** on Earth's tab of the world-creation screen: "Vanilla rock layers"), so on such a
world the map shows ore only around loaded chunks; with "Ground columns" or "Regional rock regions only" it covers the
whole planet (only under ice sheets do cells wait). With the game's own
generators (Earth's default ores, or worlds without Earth) the map asks the pick's own question, host rock included, but
only where the server has that chunk loaded; elsewhere a cell is unknown.
