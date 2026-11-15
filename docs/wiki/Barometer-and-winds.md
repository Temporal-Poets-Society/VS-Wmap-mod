# Barometer and winds

With the **Sea** mod installed, the atlas and the minimap can show the winds across the whole world. Sea gets its winds
from a wind provider: on an Earth world, Earth's climate and weather. Without a provider the prevailing winds are calm.

## The Jonas barometer

On the game chart (survival) you need a **Jonas barometer**: a clockwork weather glass with glowing cups. Carry it in
your hotbar or backpack and the atlas and minimap show the winds across the whole world, over land you have explored
and land you have not. On the Creative map no barometer is needed.

Craft it in the grid:

| | | |
| --- | --- | --- |
| brass ingot | plain glass | brass ingot |
| oscillator (Jonas frame) | valve (Jonas part) | spring (Jonas frame) |
| | temporal gear | |

On an Earth world the barometer also reads the **air pressure** where you stand, from Earth's weather, and whether it
rose or fell over the last three hours: "Barometer: 1004 hPa, falling". A falling glass comes before storms and fronts.

A ship mod can let everyone aboard see the winds from a barometer mounted on the ship, if it supports World Map.

## The Winds list

Pick what to show in the **Winds** list above the atlas:

- **Weather now:** what is blowing now (gusts left out). It refreshes every quarter of a game hour. With Earth, the
  reading at the cursor adds the air pressure there.
- **Prevailing now:** the prevailing wind for today's date.
- **January … December:** the prevailing wind of that month. Dashed lines mark where easterly and westerly belts meet;
  calm regions show calm circles.
- **Off:** hide the winds.

Arrows fly with the wind, as on a pilot chart: longer and heavier with strength, with one pair of feathers per step
(light, moderate, strong); a small circle marks a calm. They are spaced evenly on screen at every zoom. With the cursor
over the map, the line under it reads the wind there in knots, with its Beaufort name.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `WindLayer` | `1` | Your own `wmap.json`: the remembered choice: 0 off, 1 weather now, 2 prevailing now, 3 to 14 January to December |
| `BarometerRange` | `3000` | Server: kept for older configs; it no longer limits anything |
