using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Threading;

namespace Wmap.Core;

/// <summary>A river centreline piece in world blocks.</summary>
public readonly record struct RiverSegment(double X1, double Z1, double X2, double Z2, double HalfWidth);

/// <summary>
/// The whole world baked once into one semantic raster ("premade image"). It is uploaded as a
/// single mipmapped texture, so every zoom level from the whole world down to regional scale
/// is immediately available and the cylinder seam wraps through texture addressing.
/// </summary>
public sealed class BaseRaster
{
    public const int Version = 1;
    public const long MaxTexels = 8192L * 4096;
    /// <summary>Texels a side of the blocks <see cref="Bake"/> hands to its workers.</summary>
    public const int Block = 16;
    public int Width, Height;
    public byte[] Rgba;

    /// <summary>Square texels, at most maxSide per side and <see cref="MaxTexels"/> in total.</summary>
    public static (int Width, int Height) SizeFor(Topology t, int maxSide)
    {
        maxSide = Math.Clamp(maxSide, 512, 8192);
        double texel = Math.Max(1, Math.Max(t.Width, t.Height) / maxSide);
        texel = Math.Max(texel, Math.Sqrt(t.Width * t.Height / MaxTexels));
        return (Math.Max(1, (int)Math.Floor(t.Width / texel + 1e-6)), Math.Max(1, (int)Math.Floor(t.Height / texel + 1e-6)));
    }

    public static BaseRaster Bake(Topology t, int width, int height, int seaLevel, Func<Func<double, double, MapSample>> samplers,
        IReadOnlyList<RiverSegment> rivers, int threads, CancellationToken token, Action<double> progress = null,
        double heightRange = Semantic.DefaultHeightRange)
    {
        var raster = new BaseRaster { Width = width, Height = height, Rgba = new byte[width * height * 4] };
        var kinds = new byte[width * height];
        double texelX = t.Width / width, texelZ = t.Height / height;
        // The workers take blocks of Block x Block texels in turn, each walked row by row (0.5.5, EA-0194): the terrain
        // provider's grids are tiled (Earth: 6-degree tiles, a small shared cache of decoded ones), and whole rows had every
        // thread cross dozens of tiles in a latitude of its own. The same texels either way.
        int across = (width + Block - 1) / Block, down = (height + Block - 1) / Block, blocks = across * down;
        int done = 0, next = -1;
        Exception failure = null;
        // Sampling dominates the bake. Each worker owns its sampler (providers keep per-thread caches)
        // and runs below normal priority, so a bake during play leaves the game's threads their time.
        var workers = new Thread[Math.Max(1, threads)];
        for (int n = 0; n < workers.Length; n++)
        {
            workers[n] = new Thread(() =>
            {
                try
                {
                    var sample = samplers();
                    for (int b; (b = Interlocked.Increment(ref next)) < blocks && Volatile.Read(ref failure) == null;)
                    {
                        token.ThrowIfCancellationRequested();
                        int x0 = b % across * Block, z0 = b / across * Block, x1 = Math.Min(width, x0 + Block), z1 = Math.Min(height, z0 + Block);
                        for (int z = z0; z < z1; z++)
                        {
                            double wz = (z + 0.5) * texelZ;
                            for (int x = x0; x < x1; x++)
                            {
                                var s = sample(t.StartX + (x + 0.5) * texelX, wz);
                                int i = z * width + x;
                                kinds[i] = s.Kind;
                                raster.Rgba[i * 4 + 1] = Semantic.EncodeSigned(Semantic.RelativeHeight(s.Height, s.Depth, s.Kind, seaLevel), heightRange);
                                raster.Rgba[i * 4 + 2] = s.Kind is TileData.Ice or TileData.Wall ? (byte)255 : (byte)0;
                                raster.Rgba[i * 4 + 3] = 255;
                            }
                        }
                        progress?.Invoke(0.85 * Interlocked.Increment(ref done) / blocks);
                    }
                }
                catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Wmap world chart" };
            workers[n].Start();
        }
        foreach (var worker in workers) worker.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        bool riverLines = rivers != null && rivers.Count > 0;
        // With river centrelines, sampled river cells (narrower than a texel) stay land: the river
        // channel draws them as continuous lines instead of a dotted chain of lakes.
        var rgba = raster.Rgba;
        Semantic.CoastField(rgba, width, height, i => kinds[i] switch
        {
            TileData.Unknown => (sbyte)-1,
            TileData.Water => (sbyte)1,
            TileData.River => riverLines ? (sbyte)0 : (sbyte)1,
            TileData.Ice when Semantic.IsSeaIce(TileData.Ice, Semantic.DecodeSigned(rgba[i * 4 + 1], heightRange)) => (sbyte)1,
            _ => (sbyte)0
        }, t.Wraps, token, threads);
        progress?.Invoke(0.95);
        if (riverLines) DrawRivers(raster, t, rivers, token);
        progress?.Invoke(1);
        return raster;
    }

    /// <summary>Signed distance to the nearest river bank, in texels, written as a minimum into channel A.</summary>
    static void DrawRivers(BaseRaster raster, Topology t, IReadOnlyList<RiverSegment> rivers, CancellationToken token)
    {
        int width = raster.Width, height = raster.Height;
        double texelX = t.Width / width, texelZ = t.Height / height, texel = (texelX + texelZ) / 2;
        var rgba = raster.Rgba;
        for (int n = 0; n < rivers.Count; n++)
        {
            if ((n & 4095) == 0) token.ThrowIfCancellationRequested();
            var s = rivers[n];
            // Texel coordinates with centres on integers; a wrapped world keeps the segment near its first point.
            double x1 = (t.Canonical(s.X1) - t.StartX) / texelX - 0.5, z1 = s.Z1 / texelZ - 0.5;
            double x2 = x1 + t.Delta(s.X1, s.X2) / texelX, z2 = s.Z2 / texelZ - 0.5;
            double half = s.HalfWidth / texel, reach = half + Semantic.RiverRange;
            int minX = (int)Math.Floor(Math.Min(x1, x2) - reach), maxX = (int)Math.Ceiling(Math.Max(x1, x2) + reach);
            int minZ = Math.Max(0, (int)Math.Floor(Math.Min(z1, z2) - reach)), maxZ = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(z1, z2) + reach));
            double dx = x2 - x1, dz = z2 - z1, length = dx * dx + dz * dz;
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                int cx = x;
                if (t.Wraps) cx = ((x % width) + width) % width;
                else if (x < 0 || x >= width) continue;
                double u = length > 0 ? Math.Clamp(((x - x1) * dx + (z - z1) * dz) / length, 0, 1) : 0;
                double px = x1 + u * dx - x, pz = z1 + u * dz - z;
                double distance = Math.Sqrt(px * px + pz * pz) - half;
                if (distance >= Semantic.RiverRange) continue;
                int i = (z * width + cx) * 4 + 3;
                byte encoded = Semantic.EncodeSigned(distance, Semantic.RiverRange);
                if (encoded < rgba[i]) rgba[i] = encoded;
            }
        }
    }

    public byte[] Pack() => RasterCodec.Pack(Rgba, Width, Height, Version, CompressionLevel.Optimal);
    public static BaseRaster Unpack(byte[] data)
    {
        var r = RasterCodec.Unpack(data, Version, MaxTexels);
        return new BaseRaster { Width = r.Width, Height = r.Height, Rgba = r.Rgba };
    }
}
