using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Wmap.Core;

/// <summary>What grows and how a place is tinted, for far terrain: forest cover and the climate the game tints
/// plants and water by. From Earth Map: <c>ForestPotential</c> and its climate-map pixel.</summary>
/// <param name="Forest">0..255: how much forest the climate supports.</param>
/// <param name="Temperature">The climate map's stored temperature byte (vanilla adjusts it for altitude).</param>
/// <param name="Rain">The climate map's stored rainfall byte (vanilla adjusts it for altitude).</param>
public readonly record struct BiomeSample(byte Forest, byte Temperature, byte Rain);

/// <summary>
/// Biome tiles: RGBA8 on the same padded grid and texel centres as <see cref="SemanticTile"/>.
/// R forest, G stored temperature, B stored rain, A 255 where known. Climate changes slowly, so the source
/// is sampled at most every <see cref="SampleBlocks"/> blocks and interpolated.
/// </summary>
public static class BiomeTile
{
    public const double SampleBlocks = 16;
    const int Version = 101; // distinct from semantic tiles (2) and the world raster
    public const int MaxCompressedBytes = 128 * 1024;
    /// <summary>Cache file name. <paramref name="identity"/> names what the samples depend on besides the world
    /// (the source's settings and this format), so a change makes new files instead of reusing old ones.</summary>
    public static string FileName(TileKey key, string identity) => $"b-{identity}-{key.Level}-{key.X}-{key.Z}.wmt";
    /// <summary>What a tile's samples depend on besides the world: this format and the source's settings.</summary>
    public static string Identity(string sourceSettings) => TileCodec.Hash($"biome-v{Version}|{SampleBlocks}|{sourceSettings}")[..12];

    /// <summary>Texels between source samples at this level: every 16 blocks near, every second texel from level 6
    /// (128 blocks), where tiles cover whole climate zones and nothing finer shows past the near band.</summary>
    public static int Stride(int level) => level >= 6 ? 2 : Math.Max(1, (int)(SampleBlocks / (1 << level)));

    public static byte[] Sample(Topology t, TileKey key, Func<double, double, BiomeSample> biome, CancellationToken token)
    {
        const int n = TileData.Padded;
        int stride = Stride(key.Level), coarse = (n - 1 + stride - 1) / stride + 1;
        var known = new bool[coarse * coarse];
        var values = new BiomeSample[coarse * coarse];
        for (int cz = 0; cz < coarse; cz++)
        {
            token.ThrowIfCancellationRequested();
            double wz = key.Z * key.Span + (cz * stride - 0.5) * key.Step;
            for (int cx = 0; cx < coarse; cx++)
            {
                double wx = t.StartX + key.X * key.Span + (cx * stride - 0.5) * key.Step;
                if (wz < 0 || wz >= t.Height || (!t.Wraps && (wx < t.StartX || wx >= t.EndX))) continue;
                values[cz * coarse + cx] = biome(t.Canonical(wx), wz);
                known[cz * coarse + cx] = true;
            }
        }
        var rgba = new byte[n * n * 4];
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            int cx = Math.Min(x / stride, coarse - 2 < 0 ? 0 : coarse - 2), cz = Math.Min(z / stride, coarse - 2 < 0 ? 0 : coarse - 2);
            double fx = coarse < 2 ? 0 : (x - cx * stride) / (double)stride, fz = coarse < 2 ? 0 : (z - cz * stride) / (double)stride;
            double r = 0, g = 0, b = 0, weight = 0;
            for (int k = 0; k < 4; k++)
            {
                int ix = Math.Min(coarse - 1, cx + (k & 1)), iz = Math.Min(coarse - 1, cz + (k >> 1));
                double w = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fz : fz);
                int i = iz * coarse + ix;
                if (!known[i] || w <= 0) continue;
                r += values[i].Forest * w; g += values[i].Temperature * w; b += values[i].Rain * w; weight += w;
            }
            if (weight <= 0) continue;
            int o = (z * n + x) * 4;
            rgba[o] = (byte)Math.Round(r / weight); rgba[o + 1] = (byte)Math.Round(g / weight); rgba[o + 2] = (byte)Math.Round(b / weight); rgba[o + 3] = 255;
        }
        return rgba;
    }

    public static byte[] Pack(byte[] rgba) => RasterCodec.Pack(rgba, TileData.Padded, TileData.Padded, Version, CompressionLevel.Fastest);
    public static byte[] Unpack(byte[] data)
    {
        if (data == null || data.Length > MaxCompressedBytes) throw new InvalidDataException("Invalid biome tile length");
        var raster = RasterCodec.Unpack(data, Version, TileData.Padded * TileData.Padded);
        if (raster.Width != TileData.Padded || raster.Height != TileData.Padded) throw new InvalidDataException("Invalid biome tile size");
        return raster.Rgba;
    }
}
