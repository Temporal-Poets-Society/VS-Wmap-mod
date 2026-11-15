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

/// <summary>How the game colours a place's grass through the year (0.5.3-rc.5, WM-0022), from Earth Map's
/// <c>GrassSeasonAt</c>: which of Earth's grass season maps the place follows, and its wet and dry seasons.</summary>
/// <param name="Share">The map as its growing season, 0..255 for 0..1: 255 Earth's evergreen map, 204, 168, 133 and 97 its
/// season maps of 80, 66, 52 and 38 % of the year; with <paramref name="Dry"/> 255 the summer-dry map, 97 the steppe's.
/// 0: no map from Earth (the game's own season map).</param>
/// <param name="Dry">255 on Earth's summer-dry and steppe maps, 0 on the others.</param>
/// <param name="WetCos">The place's wetness curve (Earth's GrassSeason, log units, -2..2) as its yearly wave
/// a cos(2 pi t) + b sin(2 pi t) over the calendar year t: a as 128 + 63.5 a.</param>
/// <param name="WetSin">b, the same way.</param>
public readonly record struct SeasonSample(byte Share, byte Dry, byte WetCos, byte WetSin)
{
    /// <summary>No map from Earth and no wet or dry season: the game's own season map and the yearly rainfall.</summary>
    public static readonly SeasonSample None = new(0, 0, 128, 128);
    /// <summary>The share bytes of Earth's maps by their <c>GrassPhenology.Codes</c> index: evergreen, season 80, 66, 52, 38,
    /// summer-dry, steppe.</summary>
    static readonly byte[] Shares = { 255, 204, 168, 133, 97, 255, 97 };

    /// <summary>A place's season from Earth's map index (-1: none) and its wetness curve (null or empty: none).</summary>
    public static SeasonSample FromEarth(int map, float[] curve)
    {
        byte share = map >= 0 && map < Shares.Length ? Shares[map] : (byte)0, dry = map is 5 or 6 ? (byte)255 : (byte)0;
        double a = 0, b = 0;
        int steps = curve?.Length ?? 0;
        if (steps > 0)
        {
            for (int i = 0; i < steps; i++)
            {
                double t = 2 * Math.PI * (i + 0.5) / steps;
                a += curve[i] * Math.Cos(t);
                b += curve[i] * Math.Sin(t);
            }
            a *= 2.0 / steps;
            b *= 2.0 / steps;
        }
        static byte Wave(double v) => (byte)Math.Clamp((int)Math.Round(128 + 63.5 * Math.Clamp(v, -2, 2)), 1, 255);
        return new(share, dry, Wave(a), Wave(b));
    }

    /// <summary>The wave at a calendar year fraction, in the curve's log units.</summary>
    public double WetnessAt(double yearRel) => (WetCos - 128) / 63.5 * Math.Cos(2 * Math.PI * yearRel) + (WetSin - 128) / 63.5 * Math.Sin(2 * Math.PI * yearRel);
}

/// <summary>
/// Biome tiles: RGBA8 on the same padded grid and texel centres as <see cref="SemanticTile"/>.
/// R forest, G stored temperature, B stored rain, A 255 where known. Climate changes slowly, so the source
/// is sampled at most every <see cref="SampleBlocks"/> blocks and interpolated.
/// From 0.5.3-rc.5 (format 102) the tile is twice as tall: below the biome its grass season (<see cref="SeasonSample"/>:
/// R share, G dry, B and A the wet season's wave), <see cref="SeasonSample.None"/> where the source has none.
/// </summary>
public static class BiomeTile
{
    public const double SampleBlocks = 16;
    const int Version = 102; // distinct from semantic tiles (2) and the world raster; 101 had no season half
    const int OldVersion = 101;
    public const int MaxCompressedBytes = 192 * 1024;
    /// <summary>Bytes of either half (biome, season).</summary>
    public const int HalfBytes = TileData.Padded * TileData.Padded * 4;
    /// <summary>Cache file name. <paramref name="identity"/> names what the samples depend on besides the world
    /// (the source's settings and this format), so a change makes new files instead of reusing old ones.</summary>
    public static string FileName(TileKey key, string identity) => $"b-{identity}-{key.Level}-{key.X}-{key.Z}.wmt";
    /// <summary>What a tile's samples depend on besides the world: this format and the source's settings.</summary>
    public static string Identity(string sourceSettings) => TileCodec.Hash($"biome-v{Version}|{SampleBlocks}|{sourceSettings}")[..12];

    /// <summary>Texels between source samples at this level: every 16 blocks near, every second texel from level 6
    /// (128 blocks), where tiles cover whole climate zones and nothing finer shows past the near band.</summary>
    public static int Stride(int level) => level >= 6 ? 2 : Math.Max(1, (int)(SampleBlocks / (1 << level)));

    /// <summary>Samples per side of a tile at this stride (the last at or past the tile's last texel).</summary>
    public static int Coarse(int stride) => (TileData.Padded - 1 + stride - 1) / stride + 1;

    /// <summary>Along one axis, the first of the two samples a texel is interpolated from (texel <c>First · stride</c>) and
    /// the weight of the second; <see cref="Core.RevealGate"/> blanks a texel any of whose samples is in land not seen.</summary>
    public static (int First, double Weight) Between(int x, int stride, int coarse)
    {
        int c = Math.Min(x / stride, coarse - 2 < 0 ? 0 : coarse - 2);
        return (c, coarse < 2 ? 0 : (x - c * stride) / (double)stride);
    }

    /// <summary>The biome half alone (<see cref="ClimateRaster"/> and tests).</summary>
    public static byte[] Sample(Topology t, TileKey key, Func<double, double, BiomeSample> biome, CancellationToken token)
        => Sample(t, key, biome, null, token)[..HalfBytes];

    /// <summary>A whole tile: the biome, and below it the season (<see cref="SeasonSample.None"/> without
    /// <paramref name="season"/>).</summary>
    public static byte[] Sample(Topology t, TileKey key, Func<double, double, BiomeSample> biome, Func<double, double, SeasonSample> season, CancellationToken token)
    {
        const int n = TileData.Padded;
        int stride = Stride(key.Level), coarse = Coarse(stride);
        var known = new bool[coarse * coarse];
        var values = new BiomeSample[coarse * coarse];
        var seasons = new SeasonSample[coarse * coarse];
        for (int cz = 0; cz < coarse; cz++)
        {
            token.ThrowIfCancellationRequested();
            double wz = key.Z * key.Span + (cz * stride - 0.5) * key.Step;
            for (int cx = 0; cx < coarse; cx++)
            {
                double wx = t.StartX + key.X * key.Span + (cx * stride - 0.5) * key.Step;
                if (wz < 0 || wz >= t.Height || (!t.Wraps && (wx < t.StartX || wx >= t.EndX))) continue;
                values[cz * coarse + cx] = biome(t.Canonical(wx), wz);
                seasons[cz * coarse + cx] = season?.Invoke(t.Canonical(wx), wz) ?? SeasonSample.None;
                known[cz * coarse + cx] = true;
            }
        }
        var rgba = new byte[2 * HalfBytes];
        for (int i = HalfBytes; i < rgba.Length; i += 4)
        {
            rgba[i + 2] = SeasonSample.None.WetCos;
            rgba[i + 3] = SeasonSample.None.WetSin;
        }
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            var (cx, fx) = Between(x, stride, coarse);
            var (cz, fz) = Between(z, stride, coarse);
            double r = 0, g = 0, b = 0, weight = 0, share = 0, dry = 0, wetCos = 0, wetSin = 0;
            for (int k = 0; k < 4; k++)
            {
                int ix = Math.Min(coarse - 1, cx + (k & 1)), iz = Math.Min(coarse - 1, cz + (k >> 1));
                double w = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fz : fz);
                int i = iz * coarse + ix;
                if (!known[i] || w <= 0) continue;
                r += values[i].Forest * w; g += values[i].Temperature * w; b += values[i].Rain * w; weight += w;
                var s = seasons[i];
                share += s.Share * w; dry += s.Dry * w; wetCos += s.WetCos * w; wetSin += s.WetSin * w;
            }
            if (weight <= 0) continue;
            int o = (z * n + x) * 4;
            rgba[o] = (byte)Math.Round(r / weight); rgba[o + 1] = (byte)Math.Round(g / weight); rgba[o + 2] = (byte)Math.Round(b / weight); rgba[o + 3] = 255;
            o += HalfBytes;
            rgba[o] = (byte)Math.Round(share / weight); rgba[o + 1] = (byte)Math.Round(dry / weight);
            rgba[o + 2] = (byte)Math.Round(wetCos / weight); rgba[o + 3] = (byte)Math.Round(wetSin / weight);
        }
        return rgba;
    }

    public static byte[] Pack(byte[] rgba)
    {
        if (rgba.Length == HalfBytes) rgba = WithNoSeason(rgba);
        return RasterCodec.Pack(rgba, TileData.Padded, 2 * TileData.Padded, Version, CompressionLevel.Fastest);
    }

    /// <summary>A whole tile (<see cref="HalfBytes"/> x 2): the biome over the season. A tile from a server before 0.5.3-rc.5
    /// (format 101) gets <see cref="SeasonSample.None"/>.</summary>
    public static byte[] Unpack(byte[] data)
    {
        if (data == null || data.Length > MaxCompressedBytes) throw new InvalidDataException("Invalid biome tile length");
        RasterCodec.Raster raster;
        try { raster = RasterCodec.Unpack(data, Version, 2L * TileData.Padded * TileData.Padded); }
        catch (InvalidDataException)
        {
            var old = RasterCodec.Unpack(data, OldVersion, TileData.Padded * TileData.Padded);
            if (old.Width != TileData.Padded || old.Height != TileData.Padded) throw new InvalidDataException("Invalid biome tile size");
            return WithNoSeason(old.Rgba);
        }
        if (raster.Width != TileData.Padded || raster.Height != 2 * TileData.Padded) throw new InvalidDataException("Invalid biome tile size");
        return raster.Rgba;
    }

    static byte[] WithNoSeason(byte[] biome)
    {
        var rgba = new byte[2 * HalfBytes];
        Array.Copy(biome, rgba, HalfBytes);
        for (int i = HalfBytes; i < rgba.Length; i += 4)
        {
            rgba[i + 2] = SeasonSample.None.WetCos;
            rgba[i + 3] = SeasonSample.None.WetSin;
        }
        return rgba;
    }
}
