# Paper maps

A paper map carries what you have discovered to another player.

## Making a blank map

Craft a **blank map** from eight parchments around an ink and quill (the ink and quill is kept):

| | | |
| --- | --- | --- |
| parchment | parchment | parchment |
| parchment | ink and quill | parchment |
| parchment | parchment | parchment |

## Drawing

Hold the blank map in your active hotbar slot and right-click. Give it a title and tick what to draw:

- your **charted area**: what the server knows you have charted;
- your **recorded terrain**: the land as you saw it where you have been;
- your **waypoints**;
- your **automatic markers**;
- your **prospecting readings**.

The map holds those as they are when you draw it; it does not change when you explore further. A drawn map says who drew
it and what it holds. The server makes you wait 5 seconds between drawings and 3 seconds between reads.

## Reading

Hand the map to another player. Holding it, they right-click and tick what to copy into their own atlas:

- Waypoints, automatic markers and prospecting readings become their own (the server makes them, as the game's own);
  those they already have are not copied twice. Automatic markers past the server's limit (`AutoMarkLimit`) are left
  out, and the reader is told.
- The charted area is added to what they have charted, on the server, and their atlas fills in there.
- Recorded terrain fills in only where they have recorded nothing themselves: what they saw wins over the copy.

## Copies

A drawn map and a blank map in the crafting grid make a copy; the drawn one is kept. Copies share the same content.

## Server settings

| Setting | Default | What it does |
| --- | --- | --- |
| `PaperMaps` | `true` | Players may draw and read paper maps |
| `PaperMapMaxMiB` | `16` | The largest map, in MiB (1 to 256). A drawer over it is told to leave out recorded terrain. The most travelled test worlds needed 2 to 3 MiB |

## Limits

- A map's content is kept in the world save, not in the item. A map that is burned or lost leaves its content there: the
  server cannot know that no copy is left in a chest or on the ground.
- You cannot yet preview a map on the atlas before copying, cut it to an area, or copy single waypoints.
- The recorded terrain on a map comes from the drawer's game. A modified game could draw false terrain, as it could on
  its own atlas. The charted area comes from the server, so a map cannot chart land its drawer has not seen.
- Not yet checked with two players in multiplayer; the server side is tested.
