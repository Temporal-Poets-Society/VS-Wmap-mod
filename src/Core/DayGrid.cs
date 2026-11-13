using System;

namespace Wmap.Core;

/// <summary>
/// The sun over the whole world for the atlas's day and night overlay: the sun's elevation (degrees above the horizon,
/// from the game's calendar) at the centre of each cell of a fixed grid, rows from north to south. One byte per cell:
/// <see cref="Encode"/>, a quarter degree per step from -32 to +31.75 degrees, enough for day, the three twilights and night.
/// </summary>
public static class DayGrid
{
    public const int Width = 256, Height = 128;
    public static byte Encode(double degrees) => (byte)Math.Clamp(Math.Round(128 + 4 * (double.IsFinite(degrees) ? degrees : -90)), 0, 255);
    public static double Decode(byte value) => (value - 128) / 4.0;

    /// <summary>Fills rows z0 (inclusive) to z1 (exclusive) of grid from the sun's elevation at (x, z) in world blocks.</summary>
    public static void BuildRows(Topology t, Func<double, double, double> elevation, byte[] grid, int z0, int z1)
    {
        for (int z = Math.Max(0, z0); z < Math.Min(Height, z1); z++)
        {
            double wz = (z + 0.5) * t.Height / Height;
            for (int x = 0; x < Width; x++)
                grid[z * Width + x] = Encode(elevation(t.StartX + (x + 0.5) * t.Width / Width, wz));
        }
    }
}
