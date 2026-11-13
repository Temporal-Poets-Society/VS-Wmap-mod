using System;

namespace Wmap.Core;

/// <summary>When the recorder records a cell block by block (WM-D29).</summary>
public static class RecordingRules
{
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
