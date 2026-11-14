using System;

namespace Wmap.Core;

/// <summary>
/// The season colour maps far terrain tints grass and leaves with (0.5.3-rc.5, WM-0022), one row each over the time of year:
/// Earth's seven grass maps (Earth's <c>GrassPhenology</c>), the game's own grass map, and its oak and needle maps for the
/// canopy. The game's colour-map shader (colormap.vsh) reads a map at the season plus up to 0.05 of noise, and a row picked
/// by noise; far terrain shows a whole field in a pixel, so each column here is that read averaged: over every row of the map
/// and over the noise's shift. Rows <see cref="Rows"/> by <see cref="Columns"/>, RGBA8. Row <see cref="Altitude"/> holds the
/// climate's altitude adjustments instead (0.5.3-rc.14, WM-0022).
/// </summary>
public static class GrassSeasons
{
    public const int Columns = 128, Rows = 11;
    /// <summary>The row of the climate's altitude adjustments (<see cref="AltitudeRow"/>).</summary>
    public const int Altitude = 10;
    /// <summary>The lowest height (blocks above sea) the altitude row covers; it runs to the world's top.</summary>
    public const int AltitudeFrom = -64;
    /// <summary>Earth's maps by their GrassPhenology index (rows 0 to 6), as named in its assets
    /// (<c>earth:textures/environment/seasons/earthgrass-&lt;name&gt;.png</c>).</summary>
    public static readonly string[] EarthMaps = { "evergreen", "season80", "season66", "season52", "season38", "summerdry", "steppe" };
    public const int VanillaGrass = 7, Oak = 8, Needles = 9;
    /// <summary>The game's maps for <see cref="VanillaGrass"/>, <see cref="Oak"/> and <see cref="Needles"/>.</summary>
    public static readonly string[] VanillaMaps = { "textures/environment/seasons/grasstint.png", "textures/environment/seasons/oaktint.png", "textures/environment/seasons/needletint.png" };

    /// <summary>Writes row <paramref name="row"/> of <paramref name="strip"/> (RGBA8, <see cref="Rows"/> x <see cref="Columns"/>)
    /// from a map's pixels (<paramref name="argb"/>: 0xAARRGGBB, rows top first, as the game's bitmaps hold them).</summary>
    public static void Row(byte[] strip, int row, int[] argb, int width, int height)
    {
        if (strip.Length != Rows * Columns * 4) throw new ArgumentException("Strip size", nameof(strip));
        if (argb == null || width <= 0 || height <= 0 || argb.Length < width * height) throw new ArgumentException("Map size", nameof(argb));
        const int shifts = 5;
        for (int c = 0; c < Columns; c++)
        {
            double t = (c + 0.5) / Columns, r = 0, g = 0, b = 0;
            int n = 0;
            for (int s = 0; s < shifts; s++)
            {
                // colormap.vsh: clamp(seasonRel + b / 40, 0.01, 0.99), b being two value noises (0..2, on average 1)
                double x = Math.Clamp(t + 0.05 * s / (shifts - 1), 0.01, 0.99) * width - 0.5;
                int x0 = (int)Math.Floor(x);
                double f = x - x0;
                int a0 = Math.Clamp(x0, 0, width - 1), a1 = Math.Clamp(x0 + 1, 0, width - 1);
                for (int y = 0; y < height; y++)
                {
                    int p = argb[y * width + a0], q = argb[y * width + a1];
                    r += ((p >> 16) & 255) * (1 - f) + ((q >> 16) & 255) * f;
                    g += ((p >> 8) & 255) * (1 - f) + ((q >> 8) & 255) * f;
                    b += (p & 255) * (1 - f) + (q & 255) * f;
                    n++;
                }
            }
            int o = (row * Columns + c) * 4;
            strip[o] = (byte)Math.Round(r / n);
            strip[o + 1] = (byte)Math.Round(g / n);
            strip[o + 2] = (byte)Math.Round(b / n);
            strip[o + 3] = 255;
        }
    }

    /// <summary>Writes the altitude row: for heights from <see cref="AltitudeFrom"/> to <paramref name="heightScale"/> blocks above
    /// sea over the columns, how much the game's climate cools a block there (<paramref name="cooling"/>, climate-byte units,
    /// vanilla's <c>Climate.GetAdjustedTemperature</c> taken from the stored byte). Earth patches it with the real lapse on its
    /// vertical scale (its climate rules 2 and 3), and its rewrite of the game's tint shader takes that back for the season
    /// weight; far terrain does the same with this row. Cooling in R and G as 16 bits ((bytes + 128) x 128).</summary>
    public static void AltitudeRow(byte[] strip, Func<int, int> cooling, double heightScale)
    {
        if (strip.Length != Rows * Columns * 4) throw new ArgumentException("Strip size", nameof(strip));
        for (int c = 0; c < Columns; c++)
        {
            int above = (int)Math.Round(AltitudeAt(c, heightScale));
            int v = Math.Clamp((int)Math.Round((cooling(above) + 128.0) * 128.0), 0, 65535);
            int o = (Altitude * Columns + c) * 4;
            strip[o] = (byte)(v >> 8);
            strip[o + 1] = (byte)(v & 255);
            strip[o + 2] = 128;
            strip[o + 3] = 255;
        }
    }

    /// <summary>The height (blocks above sea) column <paramref name="c"/> of the altitude row stands for.</summary>
    public static double AltitudeAt(double c, double heightScale) => AltitudeFrom + c / (Columns - 1) * (Math.Max(heightScale, 1) - AltitudeFrom);

    /// <summary>The altitude row's cooling read back at a height, between its columns, as the far shaders read it
    /// (wmapCooling).</summary>
    public static double ReadAltitude(byte[] strip, double above, double heightScale)
    {
        double c = Math.Clamp((above - AltitudeFrom) / (Math.Max(heightScale, 1) - AltitudeFrom) * (Columns - 1), 0, Columns - 1);
        int c0 = (int)Math.Floor(c), c1 = Math.Min(c0 + 1, Columns - 1);
        double f = c - c0;
        double At(int k)
        {
            int o = (Altitude * Columns + k) * 4;
            return (strip[o] * 256 + strip[o + 1]) / 128.0 - 128;
        }
        double a = At(c0), b = At(c1);
        return a + (b - a) * f;
    }

    /// <summary>Copies row <paramref name="from"/> into row <paramref name="to"/> (Earth's maps missing: the game's own).</summary>
    public static void CopyRow(byte[] strip, int from, int to) => Array.Copy(strip, from * Columns * 4, strip, to * Columns * 4, Columns * 4);
}
