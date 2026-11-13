using System;

namespace Wmap.Core;

/// <summary>
/// Map texels describe terrain, not colors. Styles are evaluated per screen pixel on the GPU,
/// so zooming never needs re-rendering and coastlines stay sharp under magnification.
/// RGBA8 channels:
///   R  signed distance to the coast (texels; land positive, water negative)
///   G  height relative to the sea surface (blocks; sea bed negative), over the world's height range (<see cref="HeightRangeFor"/>)
///   B  ice (0..255 coverage)
///   A  baked world: signed distance to the nearest river bank (texels, inside negative)
///      detail tile: known-data coverage (0..255)
/// Signed values use a square-root curve: most precision at the coastline and in lowlands.
/// </summary>
public static class Semantic
{
    public const double CoastRange = 128, DefaultHeightRange = 256, RiverRange = 12;

    /// <summary>The height range the G channel spans (0.5.0-pre.11): 256 blocks, or the whole height from the sea surface
    /// to the top of a taller world. A fixed 256 flattened everything higher than that above the sea on a 512-block world
    /// (the Tibetan plateau and the Himalayas, from sea level 110). Worlds up to 256 above the sea keep 256, and so their
    /// charts and tiles byte for byte.</summary>
    public static double HeightRangeFor(int mapHeight, int seaLevel) => Math.Max(DefaultHeightRange, mapHeight - seaLevel + 1);

    public static byte EncodeSigned(double value, double range)
    {
        double t = Math.Sqrt(Math.Clamp(Math.Abs(value) / range, 0, 1));
        return (byte)Math.Clamp(Math.Round(127.5 + 127.5 * Math.Sign(value) * t), 0, 255);
    }

    public static double DecodeSigned(byte value, double range)
    {
        double e = value / 127.5 - 1;
        return Math.Sign(e) * e * e * range;
    }

    public static bool IsWater(byte kind) => kind == TileData.Water || kind == TileData.River;

    /// <summary>Ice this close to the sea surface is sea ice: part of the sea on a chart, not a coast.</summary>
    public const double SeaIceHeight = 10;
    public static bool IsSeaIce(byte kind, double relativeHeight) => kind == TileData.Ice && relativeHeight <= SeaIceHeight;

    /// <summary>Height above the sea surface for land; for water of the sea, its bed (negative); for water standing above the
    /// sea (lakes, rivers inland), its surface. Far terrain draws water at this height, so a lake lies at its own level, not
    /// at the sea's or on its bed (Lake Baikal drew a lake-shaped hollow under the real water). The atlas showed such
    /// lakes as shallow water before too, since their bed lay above the sea.</summary>
    public static double RelativeHeight(int height, int depth, byte kind, int seaLevel)
    {
        double surface = seaLevel - 1;
        if (!IsWater(kind)) return Math.Max(0, height - surface);
        return height + depth > surface + 0.5 ? height + depth - surface : height - surface;
    }

    /// <summary>
    /// Exact squared Euclidean distance transform (Felzenszwalb and Huttenlocher) of one line.
    /// f holds squared distances along the other axis (0 at sites). Outputs d and, optionally, the nearest index.
    /// </summary>
    public static void Transform(ReadOnlySpan<double> f, Span<double> d, Span<int> v, Span<double> z)
    {
        int n = f.Length, k = 0;
        v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
        }
    }

    /// <summary>
    /// Distance in texels from each cell to the nearest site, clamped to range. Rows wrap when periodic.
    /// Column distances use two sweeps; rows use the exact parabola envelope. Work is O(width*height).
    /// </summary>
    public static void Distance(int width, int height, Func<int, bool> site, bool periodic, int range, Action<int, double> write,
        System.Threading.CancellationToken token = default, int threads = 1)
    {
        int cap = range + 1;
        var columns = new ushort[width * height];
        for (int x = 0; x < width; x++) columns[x] = site(x) ? (ushort)0 : (ushort)cap;
        for (int y = 1; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int row = y * width, above = row - width;
            for (int x = 0; x < width; x++)
                columns[row + x] = site(row + x) ? (ushort)0 : (ushort)Math.Min(cap, columns[above + x] + 1);
        }
        for (int y = height - 2; y >= 0; y--)
        {
            int row = y * width, below = row + width;
            for (int x = 0; x < width; x++)
                if (columns[below + x] + 1 < columns[row + x]) columns[row + x] = (ushort)(columns[below + x] + 1);
        }
        int pad = periodic ? Math.Min(width, cap) : 0, n = width + 2 * pad;
        // Scratch for one row, made once per thread: a row of a whole-world bake is tens of kilobytes of it.
        void Row(int y, (double[] F, double[] D, int[] V, double[] Z) scratch)
        {
            var (f, d, v, z) = scratch;
            int row = y * width;
            for (int i = 0; i < n; i++)
            {
                int x = i - pad;
                if (periodic) x = ((x % width) + width) % width;
                double c = columns[row + x];
                f[i] = c * c;
            }
            Transform(f, d, v, z);
            for (int x = 0; x < width; x++) write(row + x, Math.Min(range, Math.Sqrt(d[x + pad])));
        }
        (double[], double[], int[], double[]) Scratch() => (new double[n], new double[n], new int[n], new double[n + 1]);
        if (threads <= 1)
        {
            var scratch = Scratch();
            for (int y = 0; y < height; y++) { token.ThrowIfCancellationRequested(); Row(y, scratch); }
        }
        else
            System.Threading.Tasks.Parallel.For(0, height, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token },
                Scratch, (y, _, scratch) => { Row(y, scratch); return scratch; }, _ => { });
    }

    /// <summary>Writes the signed coast distance into channel R of an RGBA raster from a water mask.</summary>
    public static void CoastField(byte[] rgba, int width, int height, Func<int, sbyte> water, bool periodic,
        System.Threading.CancellationToken token = default, int threads = 1)
    {
        // water(i): 1 water, 0 land, -1 unknown. Unknown cells are sites of neither class,
        // so the boundary of recorded terrain never becomes a false coastline.
        int range = (int)CoastRange;
        // 1/256-texel fixed point keeps a whole-world bake to two bytes per cell here.
        var toWater = new ushort[width * height];
        Distance(width, height, i => water(i) == 1, periodic, range, (i, d) => toWater[i] = (ushort)Math.Round(d * 256), token, threads);
        Distance(width, height, i => water(i) == 0, periodic, range, (i, toLand) =>
        {
            int w = water(i);
            double dry = toWater[i] / 256.0;
            bool wet = w == 1 || (w == -1 && dry < toLand);
            // Cell centres are one texel apart; the coast lies halfway between opposite cells.
            double signed = wet ? -(toLand - 0.5) : dry - 0.5;
            if (wet && toLand >= range) signed = -range;
            if (!wet && dry >= range) signed = range;
            rgba[i * 4] = EncodeSigned(signed, CoastRange);
        }, token, threads);
    }
}
