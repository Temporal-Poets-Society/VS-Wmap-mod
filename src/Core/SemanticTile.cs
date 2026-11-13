using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace Wmap.Core;

/// <summary>Detail tiles in the semantic texel format, drawn by the same shader as the world raster.</summary>
public static class SemanticTile
{
    /// <summary>Predictions sample this many extra texels around a tile, so coast distances are exact across tile edges.</summary>
    public const int Margin = 24, Grid = TileData.Padded + 2 * Margin;
    public const int Bytes = TileData.Count * 4;

    /// <summary>Samples a prediction tile with its margin. Cells outside a bounded world are unknown. With a
    /// <paramref name="cache"/>, the samples a neighbouring tile already took (its margin overlaps this one by 50 texels a
    /// side) are copied instead of taken again: every tile samples a position at the same coordinates, so the tile is the
    /// same with or without it.</summary>
    public static byte[] Sample(Topology t, TileKey key, Func<double, double, MapSample> sample, int seaLevel, CancellationToken token, SampleCache cache = null,
        double heightRange = Semantic.DefaultHeightRange)
    {
        var kinds = new byte[Grid * Grid];
        var heights = new short[Grid * Grid];
        var copied = cache?.Fill(key, seaLevel, kinds, heights);
        for (int z = 0; z < Grid; z++)
        {
            token.ThrowIfCancellationRequested();
            double wz = key.Z * key.Span + (z - Margin - 0.5) * key.Step;
            for (int x = 0; x < Grid; x++)
            {
                if (copied?[z * Grid + x] == true) continue;
                // The offset from the world's start is exact (multiples of half a step): a position has the same
                // coordinates in every tile that samples it.
                double wx = t.StartX + (key.X * key.Span + (x - Margin - 0.5) * key.Step);
                if (wz < 0 || wz >= t.Height || (!t.Wraps && (wx < t.StartX || wx >= t.EndX))) continue;
                var s = sample(t.Canonical(wx), wz);
                int i = z * Grid + x;
                kinds[i] = s.Kind;
                heights[i] = (short)Math.Clamp(Math.Round(Semantic.RelativeHeight(s.Height, s.Depth, s.Kind, seaLevel)), short.MinValue, short.MaxValue);
            }
        }
        cache?.Add(key, seaLevel, kinds, heights);
        return Encode(kinds, heights, Grid, Margin, null, heightRange);
    }

    /// <summary>The samples of recently sampled prediction tiles, margin included, for their neighbours to reuse
    /// (<see cref="Sample"/>). Thread-safe; at most <c>capacity</c> tiles (about 95 KB each), least recently used out
    /// first. Only for one terrain source: a cache belongs to the sampler it was filled from.</summary>
    public sealed class SampleCache
    {
        readonly record struct Id(int Level, int X, int Z, int SeaLevel);
        sealed class Tile { public byte[] Kinds; public short[] Heights; public long Used; }
        readonly Dictionary<Id, Tile> tiles = new();
        readonly int capacity;
        long clock;
        /// <summary>Positions copied from a neighbour instead of sampled.</summary>
        public long Reused;
        public SampleCache(int capacity = 64) => this.capacity = Math.Max(1, capacity);

        /// <summary>Copies what the eight neighbours of <paramref name="key"/> sampled where they overlap it; which
        /// positions were copied.</summary>
        internal bool[] Fill(TileKey key, int seaLevel, byte[] kinds, short[] heights)
        {
            bool[] copied = null;
            const int size = TileData.Size;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                Tile n;
                lock (tiles)
                {
                    if (!tiles.TryGetValue(new Id(key.Level, key.X + dx, key.Z + dz, seaLevel), out n)) continue;
                    n.Used = ++clock;
                }
                // This tile's (x, z) is the neighbour's (x - dx * size, z - dz * size); a stored tile is never changed.
                copied ??= new bool[Grid * Grid];
                int x0 = Math.Max(0, dx * size), x1 = Math.Min(Grid, Grid + dx * size), count = 0;
                for (int z = Math.Max(0, dz * size), z1 = Math.Min(Grid, Grid + dz * size); z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    int i = z * Grid + x;
                    if (copied[i]) continue;
                    int j = (z - dz * size) * Grid + x - dx * size;
                    kinds[i] = n.Kinds[j]; heights[i] = n.Heights[j]; copied[i] = true; count++;
                }
                Interlocked.Add(ref Reused, count);
            }
            return copied;
        }

        internal void Add(TileKey key, int seaLevel, byte[] kinds, short[] heights)
        {
            lock (tiles)
            {
                if (tiles.Count >= capacity)
                {
                    Id oldest = default; long used = long.MaxValue;
                    foreach (var pair in tiles) if (pair.Value.Used < used) { used = pair.Value.Used; oldest = pair.Key; }
                    tiles.Remove(oldest);
                }
                tiles[new Id(key.Level, key.X, key.Z, seaLevel)] = new Tile { Kinds = kinds, Heights = heights, Used = ++clock };
            }
        }
    }

    /// <summary>Encodes a recorded tile. It has no margin, so coast distances stop at its edges.</summary>
    /// <param name="prediction">The prediction tile of the same level and place (semantic texels), or null. The recorder
    /// records only cells that differ from it, so a cell not recorded is as predicted: its land or water bounds the coast
    /// of what was recorded (a lake's shore lies at its edge), and its height and ice fill the texels under the tile's
    /// faded edge.</param>
    public static byte[] Encode(TileData data, int seaLevel, byte[] prediction = null, double heightRange = Semantic.DefaultHeightRange)
    {
        var heights = new short[TileData.Count];
        for (int i = 0; i < TileData.Count; i++)
            heights[i] = (short)Math.Clamp(Math.Round(Semantic.RelativeHeight(data.Heights[i], data.Depths[i], data.Kinds[i], seaLevel)), short.MinValue, short.MaxValue);
        return Encode(data.Kinds, heights, TileData.Padded, 0, prediction?.Length == Bytes ? prediction : null, heightRange);
    }

    /// <summary>heights: relative heights from <see cref="Semantic.RelativeHeight"/>. prediction: semantic texels under
    /// an unmargined grid's unknown cells (see <see cref="Encode(TileData, int, byte[], double)"/>). heightRange: <see cref="Semantic.HeightRangeFor"/>.</summary>
    public static byte[] Encode(byte[] kinds, short[] heights, int grid, int margin, byte[] prediction = null, double heightRange = Semantic.DefaultHeightRange)
    {
        if (margin != 0 || grid != TileData.Padded) prediction = null;
        var full = new byte[grid * grid * 4];
        // Rivers are resolved water at detail scale. Water in the prediction has a negative coast distance.
        Semantic.CoastField(full, grid, grid, i => kinds[i] != TileData.Unknown
            ? Semantic.IsWater(kinds[i]) || Semantic.IsSeaIce(kinds[i], heights[i]) ? (sbyte)1 : (sbyte)0
            : prediction != null && prediction[i * 4 + 3] != 0 ? prediction[i * 4] < 128 ? (sbyte)1 : (sbyte)0 : (sbyte)-1, false);
        const int n = TileData.Padded;
        var rgba = new byte[n * n * 4];
        var known = new bool[n * n];
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            int i = (z + margin) * grid + x + margin, o = (z * n + x) * 4;
            rgba[o] = full[i * 4];
            byte kind = kinds[i];
            if (kind == TileData.Unknown)
            {
                if (prediction == null || prediction[o + 3] == 0) continue;
                rgba[o + 1] = prediction[o + 1]; rgba[o + 2] = prediction[o + 2];
                known[z * n + x] = true;
                continue;
            }
            known[z * n + x] = true;
            rgba[o + 1] = Semantic.EncodeSigned(heights[i], heightRange);
            rgba[o + 2] = kind is TileData.Ice or TileData.Wall ? (byte)255 : (byte)0;
            rgba[o + 3] = 255;
        }
        // Coverage blends a tile's edge into the world raster. Copy known neighbours into unknown
        // cells so filtering near that edge does not pull heights towards zero.
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (bool[])known.Clone();
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                if (known[z * n + x]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + NeighbourX[k], nz = z + NeighbourZ[k];
                    if (nx < 0 || nz < 0 || nx >= n || nz >= n || !known[nz * n + nx]) continue;
                    int o = (z * n + x) * 4, s = (nz * n + nx) * 4;
                    rgba[o + 1] = rgba[s + 1]; rgba[o + 2] = rgba[s + 2];
                    next[z * n + x] = true;
                    break;
                }
            }
            known = next;
        }
        for (int i = 0; i < n * n; i++) if (!known[i]) rgba[i * 4 + 1] = 128;
        return rgba;
    }

    static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 }, NeighbourZ = { 0, 0, -1, 1, -1, -1, 1, 1 };

    public static bool Any(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] != 0) return true;
        return false;
    }

    // Compressed transport/cache format: planar channels with horizontal deltas, then Deflate.
    const int Version = 2;
    public const int MaxCompressedBytes = 128 * 1024;
    public static byte[] Pack(byte[] rgba) => RasterCodec.Pack(rgba, TileData.Padded, TileData.Padded, Version, CompressionLevel.Fastest);
    public static byte[] Unpack(byte[] data)
    {
        if (data == null || data.Length > MaxCompressedBytes) throw new InvalidDataException("Invalid tile length");
        var raster = RasterCodec.Unpack(data, Version, TileData.Padded * TileData.Padded);
        if (raster.Width != TileData.Padded || raster.Height != TileData.Padded) throw new InvalidDataException("Invalid tile size");
        return raster.Rgba;
    }
}

/// <summary>RGBA rasters stored as planar channels with horizontal deltas, Deflate and a SHA-256 of the pixels.</summary>
public static class RasterCodec
{
    public sealed record Raster(int Width, int Height, byte[] Rgba);
    const uint Magic = 0x504D5752; // "WMRP"

    public static byte[] Pack(byte[] rgba, int width, int height, int version, CompressionLevel level)
    {
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("Raster size mismatch");
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic); w.Write(version); w.Write(width); w.Write(height);
            w.Write(SHA256.HashData(rgba));
        }
        using (var deflate = new DeflateStream(output, level, true))
        {
            var row = new byte[width];
            for (int c = 0; c < 4; c++)
            for (int y = 0; y < height; y++)
            {
                byte previous = 0;
                for (int x = 0, i = (y * width) * 4 + c; x < width; x++, i += 4)
                {
                    byte value = rgba[i];
                    row[x] = (byte)(value - previous);
                    previous = value;
                }
                deflate.Write(row);
            }
        }
        return output.ToArray();
    }

    public static Raster Unpack(byte[] data, int version, long maxPixels)
    {
        if (data == null || data.Length < 48) throw new InvalidDataException("Truncated raster");
        using var input = new MemoryStream(data);
        using var r = new BinaryReader(input);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != version) throw new InvalidDataException("Raster version mismatch");
        int width = r.ReadInt32(), height = r.ReadInt32();
        if (width <= 0 || height <= 0 || (long)width * height > maxPixels) throw new InvalidDataException("Invalid raster size");
        byte[] hash = r.ReadBytes(32);
        var rgba = new byte[width * height * 4];
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress, true))
        {
            var row = new byte[width];
            for (int c = 0; c < 4; c++)
            for (int y = 0; y < height; y++)
            {
                deflate.ReadExactly(row);
                byte previous = 0;
                for (int x = 0, i = (y * width) * 4 + c; x < width; x++, i += 4)
                {
                    previous = (byte)(previous + row[x]);
                    rgba[i] = previous;
                }
            }
            if (deflate.ReadByte() != -1) throw new InvalidDataException("Oversized raster");
        }
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(rgba))) throw new InvalidDataException("Raster checksum mismatch");
        return new Raster(width, height, rgba);
    }
}
