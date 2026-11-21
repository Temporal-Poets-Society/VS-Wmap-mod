# Commands and keys

## Keys

| Key | What it does |
| --- | --- |
| **M** | Open or close the atlas. This is the game's own world-map key ("Wmap: world atlas" in the key settings), so a key you set for the world map works too |
| **Ctrl+Shift+V** | Show or hide the coordinates box ("Wmap: latitude and longitude") |
| **Ctrl+Shift+F** | Switch the far view off or on ("Wmap: far view on/off"); the choice is saved |
| **F6** / **Ctrl+F6** | The game's keys: show or hide the minimap / move it to another corner |

In the atlas:

| Key | What it does |
| --- | --- |
| **Space** | Centre on yourself and follow |
| **Home** | Fit the whole world |
| **Esc** | Close the right-click menu, or the atlas |

## Commands for everyone

Commands that start with a dot run in your own game; those with a slash run on the server.

| Command | What it does |
| --- | --- |
| `.wmap` | Open the atlas |
| `.wmap vanilla` | Open the game's own map |
| `.wmap far`, `.wmap far on`, `.wmap far off` | The far view: whether it is drawn now (and, while the world is being charted, how far charting has come), or switch it on or off. Off frees its video memory; the choice is saved in your `wmap.json` |
| `.wmap mid` | The mid-distance view (a prototype): what it draws and what it costs the graphics card |
| `.wmap mid recorded`, `.wmap mid terraces`, `.wmap mid lights` | Switch that part of the mid-distance view on or off; add `on` or `off` to choose. The choice is saved in your `wmap.json` |
| `.wmap fardebug albedo` | Show the far view's colours alone, without light or haze (for comparing colours on screen); `.wmap fardebug lit` shows it lit, without haze or fog; `.wmap fardebug off` goes back to normal |

Examples:

```
.wmap mid terraces off
.wmap mid lights on
.wmap fardebug off
```

## Commands for players with the whole map

| Command | What it does |
| --- | --- |
| `/wmapore` | For the chunk column you stand in: each ore's heat map number beside the prospecting pick's own reading there |
| `/wmapore ORE` | The same for one ore, by its code, for example `/wmapore nativecopper` |

It needs creative mode, the `wmap` privilege or the server's `AllowAllPlayers`, and `OreHeatMaps` on in the server's
`wmap.json` (see [Ore heat maps](Ore-heat-maps)). An unknown ore code lists the ores that have maps.

## Admin commands of the game that World Map uses

| Command | What it does |
| --- | --- |
| `/player NAME privilege grant wmap` | Give a player the whole map; `revoke` takes it back. Needs the game's `grantrevoke` privilege |
| `/worldconfig wmapExploreBySight false` | Switch exploration by sight off for this world (`true` on); takes effect when the world restarts. Needs the server's admin privilege (`controlserver`) |
