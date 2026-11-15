using System;
using System.Collections.Generic;
using System.IO;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// A survival client's world chart and climate raster (0.5.4, WM-D63): empty but for the blocks the server sends of land the
/// player has seen (<see cref="ChartPatch"/>), and which of their texels are known, so far terrain draws only those. Main
/// thread. Blocks that changed go to the renderer one by one (scattered blocks would make one rectangle of the rows
/// between them); the places that became known are kept for far terrain to ask for its tiles there again.
/// </summary>
public sealed class ExploredChart
{
    public readonly BaseRaster Chart;
    /// <summary>The climate raster at its own size, made at its first block (servers without Earth's climate send none).</summary>
    public BaseRaster Climate { get; private set; }
    /// <summary>Per chart texel: 255 where the server has sent it.</summary>
    public readonly byte[] Known;
    byte[] climateKnown;
    readonly int climateWidth, climateHeight;
    readonly Topology topology;
    /// <summary>Blocks that brought land newly known.</summary>
    public int Blocks { get; private set; }
    /// <summary>Blocks changed since the last <see cref="Upload"/>, and whether their known texels changed too (a block round
    /// one that came changes only in its extended texels).</summary>
    readonly Dictionary<(bool Climate, int X, int Z), bool> dirty = new();
    /// <summary>World rectangles (blocks east of the world's start, and z) that became known since the last
    /// <see cref="TakeKnownChanges"/>: one per block, the bounds of its newly known texels.</summary>
    readonly List<(double X0, double Z0, double X1, double Z1)> fresh = new();
    /// <summary>More rectangles than this wait as their union: far terrain asks again for less than it would rectangle by
    /// rectangle only when they lie close together, and a reconnect can bring hundreds of blocks at once.</summary>
    const int MaxFresh = 64;
    /// <summary>Texels round known ones that take the nearest known texel (<see cref="RevealGate.Extend"/>): filtering and the
    /// chart's mipmaps blend known land with its continuation that far.</summary>
    public const int ExtendReach = 16;
    /// <summary>What the chart holds where nothing is known or near: land at the sea surface, far from a coast, no ice, no
    /// river (zeros read as deep sea in the middle of a river).</summary>
    static readonly byte[] Unknown = { 255, 128, 0, 255 };
    readonly bool wraps;

    public ExploredChart(Topology t, int chartWidth, int chartHeight)
    {
        topology = t; wraps = t.Wraps;
        Chart = new BaseRaster { Width = chartWidth, Height = chartHeight, Rgba = new byte[chartWidth * chartHeight * 4] };
        for (int i = 0; i < Chart.Rgba.Length; i += 4) Buffer.BlockCopy(Unknown, 0, Chart.Rgba, i, 4);
        Known = new byte[chartWidth * chartHeight];
        (climateWidth, climateHeight) = ClimateRaster.SizeFor(chartWidth, chartHeight);
    }

    /// <summary>Writes a block into the chart or the climate raster. False for a damaged or misplaced block.</summary>
    public bool Apply(ChartPatch patch)
    {
        if (patch?.Data == null) return false;
        try
        {
            if (patch.Climate)
            {
                Climate ??= new BaseRaster { Width = climateWidth, Height = climateHeight, Rgba = new byte[climateWidth * climateHeight * 4] };
                climateKnown ??= new byte[climateWidth * climateHeight];
                if (RevealGate.ApplyBlock(patch.Data, patch.BlockX, patch.BlockZ, Climate, climateKnown).Changed) Extended(Climate, climateKnown, patch, true, null);
                return true;
            }
            var change = RevealGate.ApplyBlock(patch.Data, patch.BlockX, patch.BlockZ, Chart, Known);
            if (change.Changed) Extended(Chart, Known, patch, false, Unknown);
            if (change.Fresh == 0) return true;
            double sx = topology.Width / Chart.Width, sz = topology.Height / Chart.Height;
            var rect = (change.X0 * sx, change.Z0 * sz, change.X1 * sx, change.Z1 * sz);
            if (fresh.Count < MaxFresh) fresh.Add(rect);
            else
            {
                var u = fresh[^1];
                fresh[^1] = (Math.Min(u.X0, rect.Item1), Math.Min(u.Z0, rect.Item2), Math.Max(u.X1, rect.Item3), Math.Max(u.Z1, rect.Item4));
            }
            Blocks++;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Extends the known texels round a block that changed into the texels not known, and marks the blocks that
    /// changed for upload: the block itself with its known texels, those round it (the texels within the reach, wrapped before
    /// they are divided into blocks: a last block narrower than the reach) only if an extended texel changed.</summary>
    void Extended(BaseRaster raster, byte[] known, ChartPatch patch, bool climate, byte[] neutral)
    {
        const int size = RevealGate.BlockSize;
        int bx0 = patch.BlockX * size, bz0 = patch.BlockZ * size;
        int x0 = bx0 - ExtendReach, z0 = bz0 - ExtendReach;
        int x1 = Math.Min(raster.Width, bx0 + size) + ExtendReach, z1 = Math.Min(raster.Height, bz0 + size) + ExtendReach;
        RevealGate.Extend(raster, known, x0, z0, x1, z1, wraps, ExtendReach, neutral, out bool extended);
        dirty[(climate, patch.BlockX, patch.BlockZ)] = true;
        if (!extended) return;
        var columns = new List<int>(4);
        for (int x = x0; x < x1; x++)
        {
            int rx = wraps ? (x % raster.Width + raster.Width) % raster.Width : x;
            if (rx < 0 || rx >= raster.Width || columns.Contains(rx / size)) continue;
            columns.Add(rx / size);
        }
        int blocksZ = (raster.Height + size - 1) / size;
        for (int bz = Math.Max(0, z0) / size; bz <= Math.Min(blocksZ - 1, (Math.Min(raster.Height, z1) - 1) / size); bz++)
            foreach (int bx in columns)
                dirty.TryAdd((climate, bx, bz), false);
    }

    /// <summary>Whether any chart texel of a world rectangle (blocks east of the world's start, wrapping where the world does;
    /// z clamped) is known: far terrain asks the server only for tiles that are.</summary>
    public bool AnyKnown(double rel0, double z0, double rel1, double z1)
    {
        double sx = Chart.Width / topology.Width, sz = Chart.Height / topology.Height;
        int x0 = (int)Math.Floor(rel0 * sx), x1 = (int)Math.Ceiling(rel1 * sx);
        int iz0 = Math.Max(0, (int)Math.Floor(z0 * sz)), iz1 = Math.Min(Chart.Height, (int)Math.Ceiling(z1 * sz));
        if (x1 - x0 >= Chart.Width) { x0 = 0; x1 = Chart.Width; }
        for (int z = iz0; z < iz1; z++)
        for (int x = x0; x < x1; x++)
        {
            int wx = topology.Wraps ? (int)Topology.Mod(x, Chart.Width) : x;
            if (wx < 0 || wx >= Chart.Width) continue;
            if (Known[z * Chart.Width + wx] != 0) return true;
        }
        return false;
    }

    /// <summary>Moves the world rectangles that became known since the last call into <paramref name="into"/>.</summary>
    public void TakeKnownChanges(List<(double X0, double Z0, double X1, double Z1)> into)
    {
        into.AddRange(fresh);
        fresh.Clear();
    }

    /// <summary>Sends the changed blocks to the renderer: the chart's texels and known texels, the climate's texels (the
    /// renderer makes the chart's mipmaps again at most every so often).</summary>
    public void Upload(MapRenderer renderer, bool wraps)
    {
        if (dirty.Count > 0)
        {
            const int size = RevealGate.BlockSize;
            bool climateWhole = false;
            foreach (var ((climate, bx, bz), knownToo) in dirty)
            {
                int x0 = bx * size, z0 = bz * size;
                if (climate)
                {
                    if (Climate == null || climateWhole) continue;
                    // The first climate block makes the renderer's raster, whole.
                    if (!renderer.ClimateReady) { renderer.SetClimate(Climate, wraps); climateWhole = true; continue; }
                    renderer.UpdateClimateRegion(Climate, x0, z0, Math.Min(Climate.Width, x0 + size), Math.Min(Climate.Height, z0 + size));
                    continue;
                }
                int x1 = Math.Min(Chart.Width, x0 + size), z1 = Math.Min(Chart.Height, z0 + size);
                renderer.UpdateBaseRegion(Chart, x0, z0, x1, z1);
                if (knownToo) renderer.UpdateKnownRegion(Known, x0, z0, x1, z1);
            }
            dirty.Clear();
        }
        renderer.RefreshBaseMips();
    }
}
