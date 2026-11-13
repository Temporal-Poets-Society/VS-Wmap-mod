using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Wmap.Core;

public readonly record struct MapSample(int Height, int Depth, byte Kind, int Color = 0);

public sealed class TileData
{
    public const int Size = 128, Padded = Size + 2, Count = Padded * Padded;
    public const byte Unknown = 0, Land = 1, Water = 2, Ice = 3, Wall = 4, River = 5, MaxKind = River;
    public readonly short[] Heights = new short[Count], Depths = new short[Count];
    // Kinds above. River marks predicted river water; color is optional game RGBA for observed terrain.
    public readonly byte[] Kinds = new byte[Count];
    // Made with the first colour that is not 0: predicted tiles and older recordings have none, so they never need the 67 KB.
    int[] colors;
    /// <summary>The colours, made (all 0) when there were none yet; <see cref="ColorAt"/> reads without making them.</summary>
    public int[] Colors => colors ??= new int[Count];
    public int ColorAt(int i) => colors?[i] ?? 0;
    public bool Any;
    public TileData Copy()
    {
        var copy = new TileData();
        CopyTo(copy);
        return copy;
    }
    /// <summary>Into another tile's arrays (reused, so nothing is allocated).</summary>
    public void CopyTo(TileData copy)
    {
        copy.Any = Any;
        Array.Copy(Heights, copy.Heights, Count); Array.Copy(Depths, copy.Depths, Count);
        Array.Copy(Kinds, copy.Kinds, Count);
        // (playtest merge) colours copied into the reused arrays as before, made only when this tile has any (W1-15)
        if (colors != null) Array.Copy(colors, copy.Colors, Count);
        else if (copy.colors != null) Array.Clear(copy.colors);
    }
    public void Set(int i, MapSample s)
    {
        Heights[i] = (short)Math.Clamp(s.Height, short.MinValue, short.MaxValue);
        Depths[i] = (short)Math.Clamp(s.Depth, 0, short.MaxValue);
        Kinds[i] = s.Kind; Any |= s.Kind != 0;
        if (s.Color != 0) Colors[i] = s.Color; else if (colors != null) colors[i] = 0;
    }
}

public static class TileCodec
{
    public const int MaxCompressedBytes = 256 * 1024;
    const int Version = 1, RawLength = TileData.Count * 9;
    public static byte[] Encode(TileData tile)
    {
        using var raw = new MemoryStream(RawLength);
        using (var w = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
            for (int i = 0; i < TileData.Count; i++) { w.Write(tile.Heights[i]); w.Write(tile.Depths[i]); w.Write(tile.Kinds[i]); w.Write(tile.ColorAt(i)); }
        byte[] data = raw.ToArray();
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true)) { w.Write(Version); w.Write(SHA256.HashData(data)); }
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(data);
        return output.ToArray();
    }
    public static TileData Decode(byte[] compressed)
    {
        if (compressed == null || compressed.Length < 36 || compressed.Length > MaxCompressedBytes) throw new InvalidDataException("Invalid tile length");
        using var input = new MemoryStream(compressed);
        using var r = new BinaryReader(input);
        if (r.ReadInt32() != Version) throw new InvalidDataException("Tile version mismatch");
        byte[] hash = r.ReadBytes(32), raw = new byte[RawLength];
        using (var d = new DeflateStream(input, CompressionMode.Decompress, true))
        {
            d.ReadExactly(raw);
            if (d.ReadByte() != -1) throw new InvalidDataException("Oversized tile");
        }
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(raw))) throw new InvalidDataException("Tile checksum mismatch");
        var tile = new TileData();
        using var br = new BinaryReader(new MemoryStream(raw));
        for (int i = 0; i < TileData.Count; i++)
        {
            tile.Heights[i] = br.ReadInt16(); tile.Depths[i] = br.ReadInt16(); tile.Kinds[i] = br.ReadByte();
            int color = br.ReadInt32();
            if (color != 0) tile.Colors[i] = color;
            if (tile.Kinds[i] > TileData.MaxKind || tile.Depths[i] < 0) throw new InvalidDataException("Invalid terrain value");
            tile.Any |= tile.Kinds[i] != 0;
        }
        return tile;
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
