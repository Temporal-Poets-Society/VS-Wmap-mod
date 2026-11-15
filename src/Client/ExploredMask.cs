using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Threading;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// What this player has charted: one coverage byte per mask texel across the whole world.
/// Gameplay mode reveals the world chart only where this is set; unexplored areas stay blank parchment.
/// </summary>
public sealed class ExploredMask
{
    public const string FileName = "explored.wmx";
    const uint Magic = 0x5058_4D57; // "WMXP"
    const int Version = 1;
    public readonly int Width, Height;
    /// <summary>The nominal texel size in blocks, the larger of <see cref="TexelX"/> and <see cref="TexelZ"/>.</summary>
    public readonly double Texel;
    /// <summary>Blocks per texel along each axis: the texture spans the world exactly, as the shader draws it, so a ring
    /// that is not a whole number of nominal texels wraps at its own circumference.</summary>
    public readonly double TexelX, TexelZ;
    public readonly bool Wraps;
    public readonly byte[] Data;
    readonly Topology topology;
    int dirtyX0 = int.MaxValue, dirtyZ0 = int.MaxValue, dirtyX1, dirtyZ1;
    public bool Unsaved { get; private set; }

    /// <summary>The mask's geometry (cells, line of sight), shared with the server's record of exploration (WM-D63).</summary>
    public readonly MaskGrid Grid;

    public ExploredMask(Topology t)
    {
        topology = t; Grid = new MaskGrid(t); Wraps = Grid.Wraps;
        Width = Grid.Width; Height = Grid.Height;
        TexelX = Grid.TexelX; TexelZ = Grid.TexelZ; Texel = Grid.Texel;
        Data = new byte[Width * Height];
    }

    /// <summary>The mask's size in texels for a world (<see cref="MaskGrid.Dimensions"/>).</summary>
    public static (int Width, int Height) Dimensions(Topology t) => MaskGrid.Dimensions(t);

    /// <summary>The texel holding a world position (any lap), or -1 outside the world. Thread-safe.</summary>
    public int Cell(double x, double z) => Grid.Cell(x, z);

    public double At(double x, double z)
    {
        int i = Cell(x, z);
        return i < 0 ? 0 : Data[i] / 255.0;
    }

    /// <summary>Charts a disc around a world position with a soft two-texel edge.</summary>
    public void Reveal(double x, double z, double radius)
    {
        // In texels of the larger size, so the disc stays round where the two sizes differ slightly.
        double sx = TexelX / Texel, sz = TexelZ / Texel;
        double cx = (topology.Canonical(x) - topology.StartX) / TexelX - 0.5, cz = z / TexelZ - 0.5, r = radius / Texel;
        int x0 = (int)Math.Floor(cx - (r + 1) / sx), x1 = (int)Math.Ceiling(cx + (r + 1) / sx);
        int z0 = Math.Max(0, (int)Math.Floor(cz - (r + 1) / sz)), z1 = Math.Min(Height - 1, (int)Math.Ceiling(cz + (r + 1) / sz));
        if (!Wraps) { x0 = Math.Max(0, x0); x1 = Math.Min(Width - 1, x1); }
        // A disc wider than a small ring covers each texel once.
        else if (x1 - x0 + 1 > Width) { x0 = 0; x1 = Width - 1; }
        bool changed = false;
        for (int iz = z0; iz <= z1; iz++)
        for (int ix = x0; ix <= x1; ix++)
        {
            int wx = Wraps ? ((ix % Width) + Width) % Width : ix;
            double ex = (ix - cx) * sx, ez = (iz - cz) * sz;
            if (Wraps && Width > 1) { double lap = Width * sx; ex -= Math.Round(ex / lap) * lap; }
            double d = Math.Sqrt(ex * ex + ez * ez);
            byte v = (byte)Math.Round(255 * Math.Clamp((r + 1 - d) / 2, 0, 1));
            int i = iz * Width + wx;
            if (v > Data[i]) { Data[i] = v; changed = true; }
        }
        if (!changed) return;
        bool crosses = x0 < 0 || x1 >= Width;
        Dirty(crosses ? 0 : x0, z0, crosses ? Width : x1 + 1, z1 + 1);
    }

    /// <summary>Charts the mask texels holding these world points (line of sight).</summary>
    public void RevealCells(IReadOnlyList<(double X, double Z)> points)
    {
        var cells = new List<int>(points.Count);
        foreach (var (x, z) in points) { int i = Cell(x, z); if (i >= 0) cells.Add(i); }
        RevealCells(cells);
    }

    /// <summary>Charts these mask texels (indices from <see cref="Cell"/>).</summary>
    public void RevealCells(IReadOnlyList<int> cells)
    {
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, z1 = -1;
        foreach (int i in cells)
        {
            if (i < 0 || i >= Data.Length || Data[i] == 255) continue;
            Data[i] = 255;
            int ix = i % Width, iz = i / Width;
            x0 = Math.Min(x0, ix); z0 = Math.Min(z0, iz); x1 = Math.Max(x1, ix); z1 = Math.Max(z1, iz);
        }
        if (x1 >= 0) Dirty(x0, z0, x1 + 1, z1 + 1);
    }

    /// <summary>The distinct texels line of sight sees from an eye at x, z (<see cref="MaskGrid.SightCells"/>), charted
    /// texels counting as seen when they enclose a hidden patch. Thread-safe: it reads the mask while the main thread may
    /// chart it, and a texel charted meanwhile only makes a patch smaller.</summary>
    public int[] SightCells(HeightGrid heights, double x, double z, double eye, double distance, double step, int holeCells = 0, double radius = 0)
        => Grid.SightCells(heights, x, z, eye, distance, step, i => Data[i] == 255, holeCells, radius);

    /// <summary>Uncharts everything (the server is about to send all the player has charted, WM-D63).</summary>
    public void Clear()
    {
        Array.Clear(Data);
        Dirty(0, 0, Width, Height);
    }

    public void Merge(byte[] other)
    {
        if (other == null || other.Length != Data.Length) return;
        for (int i = 0; i < Data.Length; i++) if (other[i] > Data[i]) Data[i] = other[i];
        Dirty(0, 0, Width, Height);
    }

    void Dirty(int x0, int z0, int x1, int z1)
    {
        dirtyX0 = Math.Min(dirtyX0, x0); dirtyZ0 = Math.Min(dirtyZ0, z0);
        dirtyX1 = Math.Max(dirtyX1, x1); dirtyZ1 = Math.Max(dirtyZ1, z1);
        Unsaved = true;
    }

    /// <summary>The changed rectangle since the last upload, as [x0, x1) × [z0, z1).</summary>
    public bool TakeDirty(out int x0, out int z0, out int x1, out int z1)
    {
        x0 = dirtyX0; z0 = dirtyZ0; x1 = dirtyX1; z1 = dirtyZ1;
        dirtyX0 = dirtyZ0 = int.MaxValue; dirtyX1 = dirtyZ1 = 0;
        return x1 > x0 && z1 > z0;
    }

    /// <summary>A copy of the mask to save; into <paramref name="into"/> when it is the right size and no longer in use (the
    /// mask can be tens of megabytes: a new copy every save was most of what the client's tick allocated).</summary>
    public byte[] Snapshot(byte[] into = null)
    {
        Unsaved = false;
        var copy = into != null && into.Length == Data.Length ? into : new byte[Data.Length];
        Buffer.BlockCopy(Data, 0, copy, 0, Data.Length);
        return copy;
    }
    /// <summary>A snapshot's write failed: keep the mask unsaved so it is written again.</summary>
    public void SaveFailed() => Unsaved = true;

    public static byte[] Pack(byte[] data, int width, int height)
    {
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true)) { w.Write(Magic); w.Write(Version); w.Write(width); w.Write(height); }
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>The texels <see cref="Pack"/> wrote, which must be of this size; throws <see cref="InvalidDataException"/>
    /// for anything else (a paper map's charted area, WM-P05).</summary>
    public static byte[] Unpack(byte[] packed, int width, int height)
    {
        try
        {
            using var input = new MemoryStream(packed);
            using var r = new BinaryReader(input);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadInt32() != width || r.ReadInt32() != height) throw new InvalidDataException("Not a charted area of this world");
            var data = new byte[width * height];
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            deflate.ReadExactly(data);
            if (deflate.ReadByte() != -1) throw new InvalidDataException("Oversized charted area");
            return data;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("The charted area is cut short"); }
    }

    public static ExploredMask Load(Topology t, string path) { TryLoad(t, path, out var mask); return mask; }

    /// <summary>Loads the saved mask. False when the file exists but cannot be read now (locked, a disk
    /// error): the mask is then empty and must not be saved over the file. A missing, damaged or
    /// different-sized file loads as empty and returns true.</summary>
    public static bool TryLoad(Topology t, string path, out ExploredMask mask)
    {
        mask = new ExploredMask(t);
        byte[] bytes;
        // No File.Exists check: it also answers false when the file cannot be reached.
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        // Decoding reads from memory: any failure here is damage.
        try
        {
            using var input = new MemoryStream(bytes);
            using var r = new BinaryReader(input);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadInt32() != mask.Width || r.ReadInt32() != mask.Height) return true;
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            deflate.ReadExactly(mask.Data);
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException) { Array.Clear(mask.Data); }
        mask.Dirty(0, 0, mask.Width, mask.Height);
        mask.Unsaved = false;
        return true;
    }

    /// <summary>Marks every mask texel holding a recorded sample, from finest-level observation tiles.</summary>
    public static byte[] FromObservations(Topology t, string folder, int width, int height, CancellationToken token)
    {
        double texelX = t.Width / width, texelZ = t.Height / height;
        var data = new byte[width * height];
        if (!Directory.Exists(folder)) return data;
        foreach (string file in Directory.EnumerateFiles(folder, "1-2-*.wmt"))
        {
            token.ThrowIfCancellationRequested();
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 4 || !int.TryParse(parts[2], out int tx) || !int.TryParse(parts[3], out int tz)) continue;
            TileData tile;
            try { tile = TileCodec.Decode(File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException) { continue; }
            var key = new TileKey(1, 2, tx, tz);
            for (int z = 1; z <= TileData.Size; z++)
            for (int x = 1; x <= TileData.Size; x++)
            {
                if (tile.Kinds[z * TileData.Padded + x] == TileData.Unknown) continue;
                double wx = key.X * key.Span + (x - 0.5) * key.Step, wz = key.Z * key.Span + (z - 0.5) * key.Step;
                int ix = (int)(wx / texelX), iz = (int)(wz / texelZ);
                if (ix >= 0 && iz >= 0 && ix < width && iz < height) data[iz * width + ix] = 255;
            }
        }
        return data;
    }
}
