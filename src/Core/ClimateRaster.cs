using System;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Wmap.Core;

/// <summary>
/// The world's climate for the atlas's Terrain colours (deserts, grassland, forest, tundra): a coarse world raster of
/// <see cref="BiomeSample"/>s, R forest, G stored temperature, B stored rain, A 255. Baked by the server after the world
/// chart, from the same source as far terrain's biome tiles (Earth Map), and sent like the chart under its own id.
/// </summary>
public static class ClimateRaster
{
    public const int Version = 201; // distinct from the world raster (1), semantic (2) and biome tiles (101)
    /// <summary>World chart texels per climate texel along each side (about 200 blocks on a 204,800-block ring).</summary>
    public const int Divisor = 8;
    public const long MaxTexels = 2048L * 1024;

    public static (int Width, int Height) SizeFor(int chartWidth, int chartHeight) =>
        (Math.Max(1, chartWidth / Divisor), Math.Max(1, chartHeight / Divisor));

    /// <summary>Samples each texel's centre. The source must be safe to call from several threads.</summary>
    public static byte[] Bake(Topology t, int width, int height, Func<double, double, BiomeSample> biome, int threads, CancellationToken token)
    {
        var rgba = new byte[width * height * 4];
        double texelX = t.Width / width, texelZ = t.Height / height;
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = token }, z =>
        {
            double wz = (z + 0.5) * texelZ;
            for (int x = 0; x < width; x++)
            {
                var s = biome(t.StartX + (x + 0.5) * texelX, wz);
                int o = (z * width + x) * 4;
                rgba[o] = s.Forest; rgba[o + 1] = s.Temperature; rgba[o + 2] = s.Rain; rgba[o + 3] = 255;
            }
        });
        return rgba;
    }

    public static byte[] Pack(byte[] rgba, int width, int height) => RasterCodec.Pack(rgba, width, height, Version, CompressionLevel.Optimal);
    public static BaseRaster Unpack(byte[] data)
    {
        var r = RasterCodec.Unpack(data, Version, MaxTexels);
        return new BaseRaster { Width = r.Width, Height = r.Height, Rgba = r.Rgba };
    }
}
