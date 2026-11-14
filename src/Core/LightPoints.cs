using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>A lit recorded sample: blocks east of the ring's start, world y of its top, z, and its block light.</summary>
public readonly record struct LightSample(double X, double Y, double Z, int Light);

/// <summary>A distant light: merged lit samples, at their light-weighted centre and the highest top among them; brightness
/// 0..1 from the brightest sample and how many there are.</summary>
public readonly record struct LightPoint(double X, double Y, double Z, float Brightness, int Count);

/// <summary>Distant lights from recordings (WM-P06): lit samples merged into a few points per chunk, so a town reads as a
/// cluster of glints and a lighthouse as one bright point.</summary>
public static class LightPoints
{
    public const int PerChunk = 4;
    /// <summary>Lit samples this close (blocks, horizontally) to a point's first sample join it.</summary>
    public const double MergeRadius = 8;

    /// <summary>Lit samples of a four-block recording tile (<paramref name="key"/> level 2 or coarser), in blocks east of
    /// the ring's start.</summary>
    public static IEnumerable<LightSample> Of(TileData tile, TileKey key)
    {
        double step = key.Step;
        for (int iz = 1; iz <= TileData.Size; iz++)
        for (int ix = 1; ix <= TileData.Size; ix++)
        {
            int i = iz * TileData.Padded + ix, light = RecordedColor.Light(RecordedColor.Of(tile, i));
            if (light < RecordedColor.Lit || tile.Kinds[i] == TileData.Unknown) continue;
            yield return new LightSample((key.X * TileData.Size + ix - 1 + 0.5) * step, RecordedTexels.Top(tile.Heights[i], tile.Depths[i], tile.Kinds[i]),
                (key.Z * TileData.Size + iz - 1 + 0.5) * step, light);
        }
    }

    /// <summary>Merges lit samples per 32-block chunk: the brightest unmerged sample starts a point and takes every unmerged
    /// sample within <see cref="MergeRadius"/>; at most <see cref="PerChunk"/> points per chunk, the rest are left out.</summary>
    public static List<LightPoint> Merge(IEnumerable<LightSample> samples)
    {
        var points = new List<LightPoint>();
        foreach (var chunk in samples.Where(s => s.Light >= RecordedColor.Lit).GroupBy(s => ((long)Math.Floor(s.X / 32), (long)Math.Floor(s.Z / 32))))
        {
            var left = chunk.OrderByDescending(s => s.Light).ThenBy(s => s.X).ThenBy(s => s.Z).ToList();
            for (int p = 0; p < PerChunk && left.Count > 0; p++)
            {
                var first = left[0];
                var members = left.Where(s => (s.X - first.X) * (s.X - first.X) + (s.Z - first.Z) * (s.Z - first.Z) <= MergeRadius * MergeRadius).ToList();
                left.RemoveAll(members.Contains);
                double weight = members.Sum(s => s.Light);
                float brightness = (float)Math.Clamp(first.Light / 24.0 * (1 + 0.15 * Math.Log2(members.Count)), 0.1, 1);
                points.Add(new LightPoint(members.Sum(s => s.X * s.Light) / weight, members.Max(s => s.Y), members.Sum(s => s.Z * s.Light) / weight, brightness, members.Count));
            }
        }
        return points;
    }
}
