# Coordinates and clocks

A small box in the top left corner of the screen shows where you are and what time it is, on the background of the
game's own coordinates box:

```
41° 18.2′ S  146° 13.7′ E   12:47 local
Heading 042° NE   01:03 UTC
```

- **Latitude and longitude** and your **heading** (degrees and the compass point), on worlds with geography: Earth, or
  Cylinder World's planet layout.
- **Local time:** the solar time where you stand. With Astra Terra for Earth it is the local solar time its sky follows;
  otherwise the solar time at your longitude on Earth or on the map; on other worlds the game's hour.
- **UTC:** the world's universal clock, the solar time at longitude 0. It is shown only where it differs from the local
  clock; where the world has one clock, the hour shows once.
- On worlds without geography the box shows the time alone ("Time 12:15").

**Ctrl+Shift+V** shows or hides the box (the game's own block coordinates stay on Ctrl+V). When the minimap sits in the
top left corner, the box moves below it. The box sits under any window you open in that corner.

## Settings

In your own `wmap.json`:

| Setting | Default | What it does |
| --- | --- | --- |
| `CoordinatesHud` | `true` | Show the box (Ctrl+Shift+V switches it and saves the choice) |
| `CoordinatesHudTime` | `true` | Show the clocks in the box |
| `DecimalDegrees` | `false` | Decimal degrees instead of degrees and minutes (the ° / °′ button on the atlas) |

## On the atlas

The line under the atlas shows your position and the cursor's, in the same format. **Day / Night** shades the night side
of the world, with the line where day turns to night.
