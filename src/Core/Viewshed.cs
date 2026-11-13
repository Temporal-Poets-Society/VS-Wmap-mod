using System;
using System.Collections.Generic;

namespace Wmap.Core;

/// <summary>
/// Ground heights for line of sight: the world chart at a coarser grid, each cell the highest point it covers (so
/// mountains never shrink), in blocks above the sea surface. Water lies at its surface: the sea at 0, lakes at their level.
/// </summary>
public sealed class HeightGrid
{
    public readonly int Width, Height;
    public readonly double Texel;
    public readonly ushort[] Heights;
    /// <summary>The highest cell.</summary>
    public readonly int Highest;
    readonly Topology topology;

    public HeightGrid(Topology t, int width, int height, ushort[] heights)
    {
        topology = t; Width = width; Height = height; Heights = heights;
        Texel = t.Width / width;
        foreach (ushort v in heights) if (v > Highest) Highest = v;
    }

    /// <summary>From the world chart's G channel (<see cref="Semantic"/>, over <paramref name="heightRange"/>),
    /// <paramref name="factor"/> chart texels per cell.</summary>
    public static HeightGrid FromRaster(BaseRaster raster, Topology t, double heightRange = Semantic.DefaultHeightRange, int factor = 2)
    {
        int w = Math.Max(1, raster.Width / factor), h = Math.Max(1, raster.Height / factor);
        var heights = new ushort[w * h];
        var decoded = new ushort[256];
        for (int v = 0; v < 256; v++) decoded[v] = (ushort)Math.Clamp(Math.Round(Semantic.DecodeSigned((byte)v, heightRange)), 0, ushort.MaxValue);
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            ushort top = 0;
            for (int dz = 0; dz < factor; dz++)
            for (int dx = 0; dx < factor; dx++)
            {
                int sx = Math.Min(raster.Width - 1, x * factor + dx), sz = Math.Min(raster.Height - 1, z * factor + dz);
                top = Math.Max(top, decoded[raster.Rgba[(sz * raster.Width + sx) * 4 + 1]]);
            }
            heights[z * w + x] = top;
        }
        return new HeightGrid(t, w, h, heights);
    }

    /// <summary>The cell's height at a world position (any lap); -1 outside the world.</summary>
    public int At(double x, double z)
    {
        if (z < 0 || z >= topology.Height) return -1;
        double rel = topology.Wraps ? Topology.Mod(x - topology.StartX, topology.Width) : x - topology.StartX;
        if (rel < 0 || rel >= topology.Width) return -1;
        int ix = Math.Min(Width - 1, (int)(rel / Texel)), iz = Math.Min(Height - 1, (int)(z * Height / topology.Height));
        return Heights[iz * Width + ix];
    }
}

/// <summary>What can be seen from an eye over the ground, all around: rays one cell apart at the far end, each keeping the
/// steepest angle seen so far; a point rising to that angle or above is visible. Ground only (no forests or buildings), no
/// fog or night. On a planet (a radius) ground sinks below the eye's level by distance² / (2 × radius): low coasts drop
/// under the horizon first, mountains show farther; a radius of 0 is flat, with no horizon.</summary>
public static class Viewshed
{
    /// <summary>Visible points, every <paramref name="step"/> blocks along rays that far apart at the reach, out to
    /// <paramref name="distance"/> blocks. A step of half the exploration mask's texel marks every texel it covers;
    /// sampling only once per height cell left a checkered pattern of uncharted texels.</summary>
    /// <param name="eye">Eye height in blocks above the sea surface.</param>
    /// <param name="step">Blocks between samples; 0 for one per height cell.</param>
    /// <param name="radius">The planet's radius in blocks; 0 for a flat world.</param>
    public static List<(double X, double Z)> Cast(HeightGrid g, double x, double z, double eye, double distance, double step = 0, double radius = 0)
    {
        var visible = new List<(double, double)>();
        Cast(g, x, z, eye, distance, step, (px, pz) => visible.Add((px, pz)), radius);
        return visible;
    }

    /// <summary>As <see cref="Cast(HeightGrid, double, double, double, double, double, double)"/>, giving each visible point to
    /// <paramref name="visible"/> instead of a list (near the eye, rays see the same ground many times over).</summary>
    public static void Cast(HeightGrid g, double x, double z, double eye, double distance, double step, Action<double, double> visible, double radius = 0)
    {
        if (step <= 0) step = g.Texel;
        double curve = radius > 0 ? 1 / (2 * radius) : 0;
        int steps = Math.Max(1, (int)Math.Ceiling(distance / step));
        int rays = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * steps));
        // Farther out, no ground rises above the highest cell, and it sinks: once even that would stay below a ray's
        // steepest angle, nothing more along the ray can be seen.
        double rise = Math.Max(0, g.Highest - eye);
        for (int r = 0; r < rays; r++)
        {
            double a = 2 * Math.PI * r / rays, dx = Math.Cos(a), dz = Math.Sin(a);
            double steepest = double.NegativeInfinity;
            for (int s = 1; s <= steps; s++)
            {
                double d = s * step, px = x + dx * d, pz = z + dz * d;
                if (rise / d - curve * d < steepest) break;
                int h = g.At(px, pz);
                if (h < 0) break;
                double slope = (h - curve * d * d - eye) / d;
                if (slope >= steepest) visible(px, pz);
                if (slope > steepest) steepest = slope;
            }
        }
    }
}
