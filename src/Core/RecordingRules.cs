using System;

namespace Wmap.Core;

/// <summary>When the recorder records a cell block by block (WM-D29).</summary>
public static class RecordingRules
{
    /// <summary>How much of a soil block's top face the game draws as grass, by its grasscoverage variant: the top half of the
    /// coverage overlay's alpha (grasscoverage/&lt;coverage&gt;.png, its right half): normal 1, sparse 0.84, very sparse 0.72,
    /// none 0.</summary>
    public static double GrassTopShare(string coverage) => coverage switch { "normal" => 1.0, "sparse" => 0.84, "verysparse" => 0.72, _ => 0.0 };

    /// <summary>The colour a recorded top block is kept with: grassy soil (any grass coverage) as
    /// <see cref="RecordedColor.GrassMarker"/>, which far terrain draws in its own grass colours; anything else the game's
    /// colour for it.</summary>
    public static int TopColor(string grassCoverage, Func<int> gameColor) => GrassTop(grassCoverage) ? RecordedColor.GrassMarker : gameColor();
    /// <summary>Whether a top block with this grass coverage is kept as <see cref="RecordedColor.GrassMarker"/>.</summary>
    public static bool GrassTop(string grassCoverage) => GrassTopShare(grassCoverage) > 0;

    /// <summary>Only a dry surface standing above the generated terrain is a possible structure. Prediction errors,
    /// natural cliffs and glacier ice can need fine recording without being structures. Without a generated baseline
    /// there is no evidence for an extrusion. Snow one block above natural terrain is not a building.</summary>
    public static bool Structure(MapSample sample, int generatedTop) => generatedTop > 0 && !Semantic.IsWater(sample.Kind)
        && sample.Kind != TileData.Unknown && sample.Height > generatedTop + 3;
    /// <summary>A recorded sample differs from the prediction at its place: land against water, or a height off by more
    /// than 3 blocks (plus 3 % of the height, the stored heights' rounding high up).</summary>
    public static bool Differs(MapSample real, int seaLevel, double predictedHeight, bool predictedWater)
    {
        double height = Semantic.RelativeHeight(real.Height, real.Depth, real.Kind, seaLevel);
        return Shore(real, seaLevel, predictedWater) || Math.Abs(height - predictedHeight) > 3 + 0.03 * Math.Abs(predictedHeight);
    }

    /// <summary>A recorded sample is water where land is predicted, or land where water is: a shore lies near it. Its cell's
    /// neighbours are recorded block by block too, since a cell whose centre matches the prediction may still hold the
    /// rest of that shore (a lake recorded by cell centres alone drew in four-block steps).</summary>
    public static bool Shore(MapSample real, int seaLevel, bool predictedWater)
    {
        double height = Semantic.RelativeHeight(real.Height, real.Depth, real.Kind, seaLevel);
        return (Semantic.IsWater(real.Kind) || Semantic.IsSeaIce(real.Kind, height)) != predictedWater;
    }

    /// <summary>The forest canopy over a recorded land sample (0.5.3-rc.4, WM-0020, WM-0021), kept in its depth, which land
    /// had no use for (always 0 before): 0 not recorded (older recordings), else 1 + the blocks from the ground's top up to
    /// the column's rain height (the treetops; 0 on open ground). The recorder reads both anyway: no extra block reads.</summary>
    public static int CanopyDepth(int rainTop, int ground) => 1 + Math.Clamp(rainTop - ground, 0, MaxCanopy);
    /// <summary>The canopy a land sample's depth holds in blocks, or -1 where it was not recorded (see <see cref="CanopyDepth"/>).</summary>
    public static int Canopy(byte kind, int depth) => kind == TileData.Land && depth > 0 ? depth - 1 : -1;
    /// <summary>The tallest canopy kept, in blocks (what the recorded colour texture's alpha holds: a quarter block a step).</summary>
    public const int MaxCanopy = 63;

    /// <summary>A four-block sample's height (0.5.3, WM-0021, WM-D61): the mean generated ground of its 4 x 4 cell from the
    /// chunk's generated height map (<paramref name="generated"/>, 32 x 32), the cell spanning lx - 2 .. lx + 1 and
    /// lz - 2 .. lz + 1 of the sample column (lx, lz), rounded, plus however far the column's own ground stands from its
    /// generated height; null where that is more than a block (a pond, a ruin, a drift: the sample's own height stands).
    /// Only the columns within <see cref="CellSurface"/> blocks of the sample column's generated height count: the others
    /// are another surface (a river or lake bed under its water, the foot of a cliff), which would sink the bank below the
    /// water beside it (the re-review of 14 Nov 2026). Before WM-D61 the sample column's own ground alone stood for the cell.</summary>
    public static int? CellMean(ushort[] generated, int lx, int lz, int ground)
    {
        if (generated == null || generated.Length < 1024 || lx < 2 || lz < 2 || lx > 30 || lz > 30) return null;
        int own = generated[lz * 32 + lx];
        if (Math.Abs(ground - own) > 1) return null;
        int sum = 0, count = 0;
        for (int dz = -2; dz <= 1; dz++)
        for (int dx = -2; dx <= 1; dx++)
        {
            int g = generated[(lz + dz) * 32 + lx + dx];
            if (Math.Abs(g - own) > CellSurface) continue;
            sum += g; count++;
        }
        return (int)Math.Round(sum / (double)count, MidpointRounding.AwayFromZero) + ground - own;
    }
    /// <summary>Blocks a column of a four-block cell may differ from the sample column and still count as its surface
    /// (<see cref="CellMean"/>): the recorder records a cell block by block where it differs from the prediction by more.</summary>
    public const int CellSurface = 3;

    /// <summary>The recording levels a sample goes into: a block of a cell recorded block by block the one- and two-block
    /// levels, a four-block sample the four-block level and every coarser one.</summary>
    public static (int First, int Last) Levels(bool fine, int maxLevel) => fine ? (0, Math.Min(1, maxLevel)) : (2, maxLevel);

    /// <summary>Whether a sample at this block (bx, bz: blocks east of the ring's start and south of the north edge) is the
    /// one a cell of this level keeps, so its cells do not flicker between the blocks inside them. One-block cells keep
    /// every block, two-block cells their centre block. Four-block samples lie every four blocks, on a lattice that need
    /// not line up with the ring's start: a four-block cell keeps the one inside it, a coarser cell the one among the
    /// four blocks around its centre (0.4.3 asked for the centre block itself, which no four-block sample is). A cell
    /// cut off by the world's east or south edge (<paramref name="width"/>, <paramref name="height"/> in blocks) is centred
    /// on its part inside the world, so it keeps a sample too when that part is four blocks or more.</summary>
    public static bool Keeps(int level, int bx, int bz, double width = double.MaxValue, double height = double.MaxValue)
    {
        if (level == 0 || level == 2) return true;
        int cell = 1 << level;
        int Centre(int b, double size) => (int)Math.Floor(Math.Min(cell, size - (b - b % cell)) / 2);
        int cx = Centre(bx, width), cz = Centre(bz, height);
        if (level == 1) return bx % cell == cx && bz % cell == cz;
        int ox = bx % cell - cx, oz = bz % cell - cz;
        return ox >= -2 && ox < 2 && oz >= -2 && oz < 2;
    }
}
