using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// Recorded tiles as far terrain draws them over the prediction (WM-P06): per padded texel four half floats (the top of the
/// cell in world y, its coverage, 1 where its colour was recorded, 1 for water) and the game's colour (bytes). Recordings
/// made before 0.5 have no colour: far terrain keeps its own colours there. See docs/mid-distance-view.md.
/// </summary>
public static class RecordedTexels
{
    /// <summary>The top of a recorded cell in world y: land's top face, water's surface (a water block is 7/8 high, so the
    /// sea's surface is at SeaLevel − 1/8, where far terrain draws the sea).</summary>
    public static double Top(int height, int depth, byte kind) => Semantic.IsWater(kind) ? height + depth + 0.875 : height + 1;

    /// <summary>The recorded canopy as the colour texture's alpha (0.5.3-rc.4): a quarter block a step up to 63.5 blocks, 255
    /// where it is not known (water, ice, recordings from before). wmapRecordedCanopy (wmapfartiles.ash) reads it back.</summary>
    public static byte CanopyAlpha(byte kind, int depth)
    {
        int canopy = RecordingRules.Canopy(kind, depth);
        return canopy < 0 ? (byte)255 : (byte)Math.Min(254, canopy * 4);
    }

    public const int Halves = 4;

    /// <summary>How many of a tile's own cells (not its padding) the recorder saw treetops over, by their height over the
    /// ground (blocks, 0..<see cref="RecordingRules.MaxCanopy"/>); trees only (2 blocks or more).</summary>
    public static int[] CanopyHistogram(TileData data)
    {
        var counts = new int[RecordingRules.MaxCanopy + 1];
        for (int iz = 1; iz <= TileData.Size; iz++)
        for (int ix = 1; ix <= TileData.Size; ix++)
        {
            int i = iz * TileData.Padded + ix, canopy = RecordingRules.Canopy(data.Kinds[i], data.Depths[i]);
            if (canopy >= 2) counts[Math.Min(canopy, RecordingRules.MaxCanopy)]++;
        }
        return counts;
    }

    /// <summary>The middle treetop height of the recorded forest in these tiles' histograms (<see cref="CanopyHistogram"/>),
    /// and how many cells it is taken from; 0 with none.</summary>
    public static int MedianCanopy(IEnumerable<int[]> histograms, out int cells)
    {
        var sum = new int[RecordingRules.MaxCanopy + 1];
        foreach (var h in histograms) AddCanopy(sum, h);
        return MedianOf(sum, out cells);
    }

    /// <summary>Adds one tile's canopy histogram (null: none) into <paramref name="sum"/>.</summary>
    public static void AddCanopy(int[] sum, int[] histogram)
    {
        if (histogram == null) return;
        for (int k = 0; k < sum.Length && k < histogram.Length; k++) sum[k] += histogram[k];
    }

    /// <summary>The median of a summed canopy histogram, and the cells it counts.</summary>
    public static int MedianOf(int[] sum, out int cells)
    {
        cells = 0;
        foreach (int n in sum) cells += n;
        if (cells == 0) return 0;
        int half = (cells + 1) / 2, seen = 0;
        for (int k = 0; k < sum.Length; k++) { seen += sum[k]; if (seen >= half) return k; }
        return sum.Length - 1;
    }

    /// <summary>Fills <paramref name="heights"/> (<see cref="Halves"/> per texel: top, coverage, colour known, water) and
    /// <paramref name="rgba"/> (4 per texel). Cells not recorded have coverage 0 and take a recorded neighbour's values (two
    /// passes), so filtering at the edge of what was recorded does not pull heights toward zero.</summary>
    public static void Build(TileData data, Half[] heights, byte[] rgba)
    {
        const int n = TileData.Padded;
        // Scratch reused per thread (the recorded tiles' worker builds one tile after another).
        var known = Clear(ref knownScratch); var filled = Clear(ref filledScratch); var next = Clear(ref nextScratch);
        var tops = Clear(ref topsScratch); var colored = Clear(ref coloredScratch); var wet = Clear(ref wetScratch);
        Array.Clear(rgba);
        for (int i = 0; i < TileData.Count; i++)
        {
            byte kind = data.Kinds[i];
            if (kind == TileData.Unknown) continue;
            known[i] = true;
            tops[i] = (float)Top(data.Heights[i], data.Depths[i], kind);
            int c = RecordedColor.Of(data, i);
            wet[i] = Semantic.IsWater(kind) ? 1 : 0;
            // Grass is drawn in far terrain's own colours (RecordedColor.GrassMarker), not as recorded.
            colored[i] = RecordedColor.Has(c) && !RecordedColor.IsGrass(c) ? 1 : 0;
            rgba[i * 4] = RecordedColor.R(c); rgba[i * 4 + 1] = RecordedColor.G(c); rgba[i * 4 + 2] = RecordedColor.B(c); rgba[i * 4 + 3] = CanopyAlpha(kind, data.Depths[i]);
        }
        Array.Copy(known, filled, known.Length);
        for (int pass = 0; pass < 2; pass++)
        {
            Array.Copy(filled, next, filled.Length);
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                if (filled[i]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + NeighbourX[k], nz = z + NeighbourZ[k];
                    if (nx < 0 || nz < 0 || nx >= n || nz >= n || !filled[nz * n + nx]) continue;
                    int s = nz * n + nx;
                    tops[i] = tops[s]; colored[i] = colored[s]; wet[i] = wet[s];
                    Array.Copy(rgba, s * 4, rgba, i * 4, 4);
                    next[i] = true;
                    break;
                }
            }
            (filled, next) = (next, filled);
        }
        for (int i = 0; i < TileData.Count; i++)
        {
            heights[i * Halves] = (Half)tops[i];
            heights[i * Halves + 1] = known[i] ? (Half)1 : (Half)0;
            heights[i * Halves + 2] = (Half)colored[i];
            heights[i * Halves + 3] = (Half)wet[i];
        }
    }

    static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 }, NeighbourZ = { 0, 0, -1, 1, -1, -1, 1, 1 };
    [ThreadStatic] static bool[] knownScratch, filledScratch, nextScratch;
    [ThreadStatic] static float[] topsScratch, coloredScratch, wetScratch;
    static T[] Clear<T>(ref T[] scratch) { if (scratch == null) scratch = new T[TileData.Count]; else Array.Clear(scratch); return scratch; }

    /// <summary>The smooth mesh's height above the sea surface where a four-block recording has <paramref name="weight"/>
    /// (coverage × fade), over the prediction's (<paramref name="predicted"/>, the sea's bed negative). Mirrors
    /// wmapWithRecorded (wmapfartiles.ash): the sea keeps the predicted bed and a lake its recorded surface; recorded land
    /// its recorded height (0.5.3, WM-D61; before, the band's prediction where it predicted land).</summary>
    public static double Smooth(double weight, double recorded, bool recordedWater, double predicted)
    {
        if (weight <= 0) return predicted;
        double y = recordedWater ? (recorded < 0.5 ? Math.Min(predicted, recorded) : recorded) : recorded;
        return predicted + (y - predicted) * weight;
    }
}
