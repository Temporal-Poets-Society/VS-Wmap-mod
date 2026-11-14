using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// Where far terrain starts (0.5.0-pre.2). The game fades its chunks out from 0.75 of the view distance (water) and 0.80 to 0.85
/// (land), so far terrain starts at 0.75 of it; but after the view distance grows, after a teleport and while travelling
/// fast, the chunk columns there are not loaded yet, and the game shows the sky where they are missing. Far terrain then
/// starts at the nearest column that is not loaded, so it fills in until they arrive (reported in game at Santorini after
/// switching the view distance to 700: a pale band of sky between the real sea and the terraces).
/// </summary>
public static class FarStart
{
    public static double For(int viewDistance, double loadedRadius = double.PositiveInfinity) => Math.Max(48, Math.Min(0.75 * viewDistance, loadedRadius));

    /// <summary>Where the game starts fading its own blocks into the sky colour, blocks from the camera: chunkopaque.vsh's alpha
    /// is 17 - 20 x distance / view distance (horizontal), 1 up to 0.8 of the view distance and 0 at 0.85, where the block is
    /// dropped (blocks above the camera a little later). Far terrain takes the depth of this point along its ray beyond it, out
    /// to <see cref="GameFadeEnd"/> of it, and so covers the fading blocks (0.5.3-rc.5, WM-0022: they stood as a pale ring with
    /// pale trees between the real ground and far terrain). 0 without a view distance.</summary>
    public static double GameFade(int viewDistance) => viewDistance > 0 ? 0.8 * viewDistance : 0;

    /// <summary>Predicted forest's canopy height (blocks): the recorded forest's middle treetop height where at least
    /// <see cref="CanopyCellsNeeded"/> recorded cells have trees, held to 4..40; else 8 (0.5.3-rc.8).</summary>
    public static double CanopyFor(int recordedHeight, int recordedCells) =>
        recordedCells >= CanopyCellsNeeded && recordedHeight > 0 ? Math.Clamp(recordedHeight, 4, 40) : 8;
    /// <summary>Recorded tree cells (four blocks a side) a learned canopy height needs: about 70 x 70 blocks of forest.</summary>
    public const int CanopyCellsNeeded = 300;
    /// <summary>How far past <see cref="GameFade"/> far terrain covers the game's blocks, as a multiple of it: 0.95 of the view
    /// distance, where blocks 150 above the camera are dropped too.</summary>
    public const double GameFadeEnd = 0.95 / 0.8;

    /// <summary>Columns on each side of the camera's in <see cref="Scan"/>'s mask: enough for 0.8 of the game's largest view
    /// distance (1536), where far terrain covers its blocks (<see cref="GameFade"/>).</summary>
    public const int MaskReach = 40, MaskSide = 2 * MaskReach + 1;

    /// <summary>Blocks from (<paramref name="x"/>, <paramref name="z"/>) to the nearest point of the nearest chunk column that
    /// <paramref name="loaded"/> says is not loaded, looking no farther than <paramref name="reach"/> (returned when all are).
    /// Columns at negative indices do not exist and count as loaded.</summary>
    public static double LoadedRadius(Func<int, int, bool> loaded, double x, double z, double reach) => Scan(loaded, x, z, reach, null);

    /// <summary>As <see cref="LoadedRadius"/>, and fills <paramref name="mask"/> (<see cref="MaskSide"/>², row by row, the camera's
    /// column in the middle) with 255 for a loaded column and 0 for one not loaded (0.5.0-pre.3): far terrain draws nearer than
    /// 0.75 of the view distance only over columns not loaded, not in every direction (0.5.0-pre.2 pulled its start in all
    /// round, over loaded ground, for one missing column).</summary>
    public static double Scan(Func<int, int, bool> loaded, double x, double z, double reach, byte[] mask)
    {
        int cx = (int)Math.Floor(x / 32), cz = (int)Math.Floor(z / 32), r = Math.Min(MaskReach, (int)Math.Ceiling(reach / 32) + 1);
        if (mask != null) Array.Fill(mask, (byte)255);
        double nearest = reach;
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int ix = cx + dx, iz = cz + dz;
            double ex = Math.Max(0, Math.Max(ix * 32 - x, x - (ix * 32 + 32))), ez = Math.Max(0, Math.Max(iz * 32 - z, z - (iz * 32 + 32)));
            double d = Math.Sqrt(ex * ex + ez * ez);
            if (d >= reach || ix < 0 || iz < 0 || loaded(ix, iz)) continue;
            if (mask != null) mask[(dz + MaskReach) * MaskSide + dx + MaskReach] = 0;
            nearest = Math.Min(nearest, d);
        }
        return nearest;
    }

    /// <summary>Blocks from the north-west corner of the mask <see cref="Scan"/> made at (<paramref name="scanX"/>,
    /// <paramref name="scanZ"/>) to the camera now: small numbers, so the shader finds a point's column exactly.</summary>
    public static (double X, double Z) MaskOffset(double scanX, double scanZ, double cameraX, double cameraZ) =>
        (cameraX - (Math.Floor(scanX / 32) - MaskReach) * 32, cameraZ - (Math.Floor(scanZ / 32) - MaskReach) * 32);

    /// <summary>Blocks the camera may move from where the mask was made before it is made again at once.</summary>
    public const double Rescan = 32;
    /// <summary>The mask is made again at least this often (milliseconds), for columns that load or unload.</summary>
    public const long ScanMs = 250;

    /// <summary>Whether the mask made at (<paramref name="scanX"/>, <paramref name="scanZ"/>) for <paramref name="scanStart"/>
    /// must be made again: it is due, the camera moved a column or more (a walk, a teleport, or a jump across Cylinder World's
    /// seam, which moves it a whole lap: the mask's columns are the game's, around where it was made), or far terrain's start
    /// changed (the view distance). 0.5.0-pre.4: in 0.5.0-pre.3 only every 250 ms, so a seam jump or teleport drew a frame or
    /// more with the columns of where the camera had been.</summary>
    public static bool ScanDue(double scanX, double scanZ, double scanStart, double cameraX, double cameraZ, double start, long now, long dueAt) =>
        double.IsNaN(scanX) || now >= dueAt || start != scanStart || Math.Abs(cameraX - scanX) >= Rescan || Math.Abs(cameraZ - scanZ) >= Rescan;

    /// <summary>Whether far terrain draws at a point <paramref name="rx"/>, <paramref name="rz"/> blocks from the camera, this far
    /// out: past <paramref name="start"/> always, nearer only over a column the mask has as not loaded. Mirrors wmapfarshade.ash.</summary>
    public static bool Draws(byte[] mask, (double X, double Z) offset, double rx, double rz, double distance, double start)
    {
        if (distance >= start) return true;
        int i = (int)Math.Floor((offset.X + rx) / 32), j = (int)Math.Floor((offset.Z + rz) / 32);
        return i >= 0 && j >= 0 && i < MaskSide && j < MaskSide && mask[j * MaskSide + i] == 0;
    }
}
