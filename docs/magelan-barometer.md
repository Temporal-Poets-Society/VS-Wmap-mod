# Magelan: the Jonas barometer in the binnacle, and a course line

Instructions for the Magelan side of features that Wmap 0.3.7 starts. Wmap has no compile-time dependency on Magelan and Magelan needs none on Wmap: they meet through an item attribute and an entity attribute.

## What Wmap provides (updated in 0.5.0-pre.15)

- Adds the item `wmap:jonasbarometer` (a Jonas weather glass). Its attributes set `wmapWindInstrument: true`; test for that attribute rather than the code, so other instruments can count too.
- A player who carries one (hotbar or backpack) sees the weather now on the gameplay atlas and the HUD minimap. Since 0.5.0-pre.15, this grants worldwide weather and prevailing winds for any month, selected in the atlas Winds list, plus readouts at the player and cursor. `BarometerRange` remains in config/protocol but does not limit this temporary access rule.
- Any player whose entity has the watched string attribute **`wmapWindSight`** set to a non-empty value sees the same. The value names the source and appears on the map: "worldwide weather now from the Santa Maria's binnacle".
- Wmap's server removes `wmapWindSight` when a player joins (`PlayerNowPlaying`), so a grant saved with the player cannot outlive its source.
- Wmap draws other mods' vanilla map layers on its atlas. Since 0.3.7, its player arrow and wind layer no longer hide what `ShipMapLayer` draws at depth 55: the arrow's transparent corners and the wind quad stopped writing depth.

## 1. The barometer in the binnacle (server)

Behaviour: put a Jonas barometer in a ship's binnacle and everyone aboard sees the winds on Wmap, as if each carried one. Take it out and the view ends.

1. **Slot.** Give the binnacle fixture one instrument slot, stored with the ship (her entity attributes, like the other fittings), and synced to clients so it can be drawn.
   - Right-click the binnacle holding an item whose `Attributes["wmapWindInstrument"].AsBool()` is true: move one into the slot if it is empty.
   - Right-click with an empty hand: take it back out. Use whatever gesture the binnacle doesn't already use; it currently gives the compass hint (`ShipHud` "compass").
   - A wreck, or the ship being broken up, drops the barometer like any other fitting's contents.
2. **Grant.** About once a second on the server, for each ship with an instrument in her binnacle, take every player aboard: on deck, below deck or in the rigging. Use the same test the Kraken uses ("a player standing on deck or below deck counts"). For each one:
   ```csharp
   const string Key = "wmapWindSight";
   string value = $"the {ship.Name}'s binnacle";   // or Lang: "magelan:wind-sight-binnacle"
   var wa = player.Entity.WatchedAttributes;
   if (wa.GetString(Key) != value) { wa.SetString(Key, value); wa.MarkPathDirty(Key); }
   ```
   Track the UIDs you granted. When a tracked player is no longer aboard such a ship (they left, the barometer was taken out, or they disconnected), remove it only if the value is still yours:
   ```csharp
   if (wa.GetString(Key) == valueYouSet) { wa.RemoveAttribute(Key); wa.MarkPathDirty(Key); }
   ```
   Don't clear a value Magelan didn't set; another mod may be granting it. A player aboard two ships at once doesn't happen, but if it did, the last write wins.
3. **Unnamed ships.** Use "the ship's binnacle".

Notes:
- Only the server writes the attribute. Watched attributes sync to every client by themselves, and Wmap reads it on the client.
- Wmap needn't be installed. Without it the attribute is harmless.
- The ship's boat has no binnacle; it gets no slot.

## 2. Drawing it (client)

A small mesh on the binnacle's top: Wmap's shape is `wmap:shapes/item/jonasbarometer.json` (a brass drum, a white dial with a glowing needle, a mast with four glowing cups). The simplest approach is to tesselate the slot's ItemStack as an item, scaled to about 0.5, on the binnacle's top face. Optionally spin the cups (the elements named `Hub`, `ArmX`, `ArmZ`, `Cup*`) at a rate that follows the true wind's strength.

## 3. A projected course line (client, `ShipMapLayer`)

A toggle ("Course line", default on, in `MagelanClientConfig` and with a key or `.magelan` subcommand, like the wind panel) that draws, for the player's own ships:

- a dashed line from the bow along her **course over the ground** (her velocity's direction, so it shows leeway and current), whose length is how far she sails in one game hour at her present speed, with tick marks every tenth of an hour. When she is nearly stopped (under 0.3 kn), draw a short stub along her heading instead.
- optionally a fainter solid line along her heading when the two differ by more than 5°.

Draw it at depth 55.25 (between the plan and the label), in the plan's gold for her own ships. It then appears on vanilla's map, the minimap and Wmap's atlas, which draws vanilla layers, all at once. Nothing is needed in Wmap.

## Later: autopilot (not now)

A Jonas apparatus fitted to the wheel that steers along a route plotted on the chart. The route plotting is Wmap's chart-table milestone. Steering belongs in Magelan: server-side, so the ship keeps sailing while players sleep, using `HelmMath` and `Sailing.NoGo` to tack in zigzags when the route lies in the no-go wedge, and reading Sea's wind. Seasonal wind charts recovered from Earth's ruins would unlock Wmap's prevailing-wind display in gameplay. The interface between Wmap and Magelan (a list of waypoints stored on the ship entity) will be specified when the chart table is designed.

## What to test in game

1. With Wmap 0.3.7 and Sea: carry a Jonas barometer (creative inventory: search "barometer"), open the atlas in Gameplay mode and check that arrows appear within the dashed circle and the line under the map reads the wind.
2. Put it in a carrack's binnacle, remove it from your inventory, and check that you and a second player aboard both see the winds. Step ashore and check they vanish within about a second.
3. In Development mode with **Winds: weather now**, check that the ships are visible on the atlas (0.3.6 hid them under the wind layer), and that your arrow no longer cuts a square out of your ship.
