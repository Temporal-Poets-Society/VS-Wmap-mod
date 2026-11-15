# Paper map sharing (WM-P05)

Blank-map recipe from **0.5.0-pre.15**: eight `game:paper-parchment` in the outer crafting slots, with a `game:inkandquill` tool in the center, makes one blank map. The writing tool is retained and takes its normal tool wear. Copying still uses one existing drawn map and one blank map.

Status: **agreed with the user on 22 October 2026 and implemented in 0.4.8** (decisions WM-D34..WM-D36). Builds on 0.4.6
(server-made automatic markers, waypoints and readings drawn by the atlas).

The idea is the user's: a player draws what they choose onto a paper map, hands the map to another player, and the
receiver copies what they want from it into their own atlas. A map is a snapshot, not a live link.

## 1. What a map holds

**Everything the drawer has discovered** of the parts they tick; there is no area to choose (the user's answer). The
parts:

| Part | Where it lives | On the map |
| --- | --- | --- |
| Charted area | The server's record of what the drawer has charted (from 0.5.4, WM-D63; before, the drawer's client, `explored.wmx`), one byte per mask texel, 32+ blocks a texel | The whole mask, deflated |
| Recorded terrain | The drawer's client, `observed/<id>/1-<level>-x-z.wmt` | Every recorded tile, at every level |
| Waypoints | The server (`WaypointMapLayer`) | The drawer's own waypoints, not temporary ones |
| Automatic markers | The server: waypoints whose `Text` starts with `wmap-auto:` | As waypoints, ticked separately |
| Prospecting readings | The server (`OreMapLayer`, save key `oreMapMarkers-<uid>`) | The drawer's own readings |

Sizes, measured on the user's own test worlds: the most recorded one holds 1.7–2.6 MiB of tiles at all levels, and a
mask is up to about 650 KiB. The server refuses a map over 16 MiB (`PaperMapMaxMiB`) and says why; the drawer can untick
recorded terrain, the largest part.

## 2. Storage: a document id on the item, the content in the save

The architecture (section 6) says an item carries a document id, not a terrain blob. That holds here:

- **Item attributes are the wrong place for the content.** A stack's attributes travel with every inventory update, with
  a held item to every player who sees it, with chests and dropped items, and are saved with each. Megabytes per stack
  would be sent over and over.
- **The content goes in the world save**, `SaveGame.StoreData("wmap-map-<id>")`, as the game keeps prospecting readings.
  It travels and is backed up with the world. The id is a new GUID.
- **The item carries** the id plus small fields for its tooltip and the reading dialog: title, drawer's name, the game
  date it was drawn, and the counts of each part.
- **A map is immutable.** Copies share the id, so a copy costs nothing in the save.
- **Known limit:** a map burned or lost leaves its document in the save; the server cannot see every chest or dropped
  item to know none is left. A cleanup command can come later if saves grow.

## 3. Drawing (export)

1. The player holds a **blank map** and right-clicks. A dialog asks for a title and shows a tick box for each part with
   its count; *Draw* starts.
2. The client packs what only it has (charted area, recorded terrain; recordings not yet written are flushed first) and
   uploads it in 64 KiB pieces, as the world chart travels the other way (`BaseChunk`), with the ticked parts.
3. The server checks: the player still holds a blank map; the upload fits the size cap; the mask has this world's size;
   every tile decodes (`TileCodec`'s checksum and value checks) and has a valid key for this world. It then adds
   **waypoints and readings from its own lists**, not from the client, so a map cannot carry markers the drawer never
   had.
4. It stores the document, takes one blank map from the held stack, and gives the drawn map (into the slot when the
   stack was one, else into the inventory, else dropped at the player's feet).

Recorded terrain comes from the drawer's client, as it does for the drawer's own atlas: a modified client could draw false
terrain, which reveals nothing it did not invent. The charted area comes from the server's record of what the drawer has
charted (0.5.4, WM-D63), so a map cannot chart land its drawer has not seen; reading one charts the reader's area on the
server, which then sends the reader's client the prediction there. Worlds from before 0.5.4 are not supported (WM-D63); a
map without the server's record of its chart (one marked `serverCharted`) gives its terrain, waypoints and readings, not its
chart.

## 4. Reading (import)

1. The receiver right-clicks with the drawn map in hand. A dialog shows the title, who drew it and when, and a tick box
   for each part the map holds (all ticked), with a *Copy into my atlas* button.
2. The client asks the server with the map's id and the ticked parts. The server checks the player holds a map with that
   id (the item is the right to read the document) and loads it.
3. **Waypoints and readings** become the receiver's own, made by the server: new GUIDs, `OwningPlayerUid` the receiver,
   added through the game's `WaypointMapLayer.AddWaypoint` / `OreMapLayer.AddWaypoint`, so the minimap, groups and other
   mods see them as any other. Duplicates are skipped: a waypoint with the same title and icon within 2 blocks of one
   the receiver has, a reading within 1 block of one they have. Automatic markers keep their `wmap-auto:` tag and count
   against the server's automatic marker limit. Reading the same map twice adds nothing the second time.
4. **Charted area and terrain** are sent to the client in pieces. The client merges the charted area by the larger value
   per texel (`ExploredMask.Merge`), and each recorded tile cell by cell **only where the receiver has no recording of
   their own** (the user's choice: what you saw yourself wins over a copy). Tiles merge as the receiver's own recordings
   load, a few a frame.
5. Chat says what was copied.

## 5. Items and recipes

- `wmap:papermap-blank`: **eight parchments around ink and quill** (the quill is a tool and is not used up). Stacks to 16.
- `wmap:papermap-drawn`: made only by drawing or copying. Stacks to 1. Tooltip: title, drawn by, contents.
- **Copying** (agreed): a drawn map (kept) + a blank map → a second drawn map with the same id, like the game's schematic
  copy recipe (`copyAttributesFrom`).
- Shape: the game's parchment shape with Wmap textures.

Server settings (`wmap.json`): `PaperMaps` (default on), `PaperMapMaxMiB` (16).

## 6. Messages and modules

- New messages on the existing channel, appended so older peers keep their numbers: `MapDraw` (upload id, title, ticked
  parts, total size), `MapPiece` (both directions, 64 KiB) and `MapRead` (map id, ticked parts). Results are told in
  chat. `AtlasInfo.PaperMaps` tells a client the server takes them. A drawing's pieces are put together on arrival, off
  the main thread (there may be hundreds; the per-player main-thread cap of WM-D19 would drop them).
- Core (tested in `tests/WmapTests`): the `PaperMap` document (versioned binary: parts, mask, tiles, waypoints,
  readings), its checks, the duplicate rules and the cell merge.
- Server: `PaperMapServer` (checks, storage, item swap, making waypoints and readings).
- Client: `PaperMapClient` (packing from `ExploredMask` and `TileClient`, the upload, the merge queue), `PaperMapDialog`,
  and the item class `PaperMapItem` (right-click opens the dialog; the tooltip shows the description).

## 7. Checks

- Core: document round trip; refuses damaged, oversized or invalid content; duplicate rules; the merge keeps the
  receiver's cells and fills only unknown ones.
- Smoke (server, with a second player as other server checks fake one): player A with waypoints, an automatic marker, a
  reading, a mask and recorded tiles draws a map; the item goes to player B; B reads it; B owns the waypoints and the
  reading under new GUIDs, B's mask and recordings hold A's, B's own cells are kept, and a second read adds nothing.

## Not in this version

- Previewing a map on the atlas before copying it, picking single waypoints, and limiting a map to an area.
- Physical charts (the atlas only opened through a held chart) and shared crew charts: architecture sections 6 and 9.
