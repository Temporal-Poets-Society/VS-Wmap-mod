using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Wmap.Core;

/// <summary>
/// A set of cells of a <see cref="MaskGrid"/> (WM-D63): what a player has charted, or what their client has been sent.
/// Sparse: blocks of 64 × 64 cells are kept only once one of their cells is in the set, as 64 rows of 64 bits, so a
/// server keeps one per player without the whole grid's 32 MB each. Thread-safe: the server's workers read it while the
/// main thread adds to it.
/// </summary>
public sealed class CellSet
{
    public const int Block = 64;
    const uint Magic = 0x5343_4D57; // "WMCS"
    const int Version = 1;
    public readonly int Width, Height;
    readonly int blocksX, blocksZ;
    readonly Dictionary<int, ulong[]> blocks = new();
    readonly object gate = new();
    int count;

    public CellSet(int width, int height)
    {
        Width = width; Height = height;
        blocksX = (width + Block - 1) / Block; blocksZ = (height + Block - 1) / Block;
    }

    /// <summary>Cells in the set.</summary>
    public int Count { get { lock (gate) return count; } }

    /// <summary>Adds a cell; true when it was not in the set. Cells outside the grid are ignored.</summary>
    public bool Add(int cell)
    {
        lock (gate) return AddLocked(cell);
    }

    bool AddLocked(int cell)
    {
        if (cell < 0 || cell >= Width * Height) return false;
        int x = cell % Width, z = cell / Width, b = z / Block * blocksX + x / Block;
        ulong bit = 1UL << (x % Block);
        if (!blocks.TryGetValue(b, out var rows)) blocks[b] = rows = new ulong[Block];
        ref ulong row = ref rows[z % Block];
        if ((row & bit) != 0) return false;
        row |= bit; count++;
        return true;
    }

    /// <summary>Adds cells, under one lock; the ones not in the set before go to <paramref name="added"/> (when given).
    /// Returns how many.</summary>
    public int AddRange(IEnumerable<int> cells, List<int> added = null)
    {
        int n = 0;
        lock (gate) foreach (int cell in cells) if (AddLocked(cell)) { n++; added?.Add(cell); }
        return n;
    }

    /// <summary>The cells of a list not in the set (each once), under one lock: a sight cast's cells, most of them in the set
    /// already, are sorted out off the main thread.</summary>
    public List<int> Missing(IEnumerable<int> cells)
    {
        var missing = new List<int>();
        var seen = new HashSet<int>();
        lock (gate)
            foreach (int cell in cells)
            {
                if (cell < 0 || cell >= Width * Height) continue;
                int x = cell % Width, z = cell / Width;
                if (blocks.TryGetValue(z / Block * blocksX + x / Block, out var rows) && (rows[z % Block] & (1UL << (x % Block))) != 0) continue;
                if (seen.Add(cell)) missing.Add(cell);
            }
        return missing;
    }

    /// <summary>A copy of the set.</summary>
    public CellSet Clone()
    {
        var copy = new CellSet(Width, Height);
        lock (gate)
        {
            foreach (var (b, rows) in blocks) copy.blocks[b] = (ulong[])rows.Clone();
            copy.count = count;
        }
        return copy;
    }

    /// <summary>Per block of the set, the rectangle of cells that holds its cells ([x0, x1) × [z0, z1)): a whole set's places
    /// in a few hundred rectangles instead of a million cells.</summary>
    public List<(int X0, int Z0, int X1, int Z1)> Boxes()
    {
        var boxes = new List<(int, int, int, int)>();
        lock (gate)
            foreach (var (b, rows) in blocks)
            {
                ulong any = 0; int r0 = -1, r1 = -1;
                for (int r = 0; r < Block; r++)
                {
                    if (rows[r] == 0) continue;
                    any |= rows[r];
                    if (r0 < 0) r0 = r;
                    r1 = r;
                }
                if (any == 0) continue;
                int bx = b % blocksX * Block, bz = b / blocksX * Block;
                boxes.Add((bx + BitOperations.TrailingZeroCount(any), bz + r0, bx + Block - BitOperations.LeadingZeroCount(any), bz + r1 + 1));
            }
        return boxes;
    }

    public bool Contains(int cell)
    {
        if (cell < 0 || cell >= Width * Height) return false;
        int x = cell % Width, z = cell / Width, b = z / Block * blocksX + x / Block;
        lock (gate) return blocks.TryGetValue(b, out var rows) && (rows[z % Block] & (1UL << (x % Block))) != 0;
    }

    /// <summary>Whether any cell of the rectangle [x0, x1) × [z0, z1) is in the set; x may run past the grid's edges
    /// (it wraps), z is clamped. Looks at the set's blocks, not the rectangle's cells: a coarse tile's rectangle can hold
    /// millions of cells.</summary>
    public bool AnyIn(int x0, int z0, int x1, int z1)
    {
        z0 = Math.Max(0, z0); z1 = Math.Min(Height, z1);
        if (z1 <= z0 || x1 <= x0) return false;
        // The rectangle's columns on the grid: one range, or two where it crosses the edge.
        int a0, a1, b0 = 0, b1 = 0;
        if (x1 - x0 >= Width) { a0 = 0; a1 = Width; }
        else
        {
            a0 = ((x0 % Width) + Width) % Width; a1 = a0 + (x1 - x0);
            if (a1 > Width) { b0 = 0; b1 = a1 - Width; a1 = Width; }
        }
        lock (gate)
        {
            foreach (var (b, rows) in blocks)
            {
                int bx = b % blocksX * Block, bz = b / blocksX * Block;
                int rz0 = Math.Max(z0, bz), rz1 = Math.Min(z1, bz + Block);
                if (rz1 <= rz0) continue;
                ulong mask = Bits(a0, a1, bx) | Bits(b0, b1, bx);
                if (mask == 0) continue;
                for (int z = rz0; z < rz1; z++) if ((rows[z - bz] & mask) != 0) return true;
            }
        }
        return false;
    }

    /// <summary>The bits of a block (its first column <paramref name="bx"/>) for the columns [x0, x1).</summary>
    static ulong Bits(int x0, int x1, int bx)
    {
        int l = Math.Max(x0, bx) - bx, h = Math.Min(x1, bx + Block) - bx;
        if (h <= l) return 0;
        return h - l == Block ? ulong.MaxValue : ((1UL << (h - l)) - 1) << l;
    }

    /// <summary>Every cell in the set, in no particular order.</summary>
    public List<int> Cells()
    {
        var list = new List<int>();
        lock (gate)
        {
            foreach (var (b, rows) in blocks)
            {
                int bx = b % blocksX * Block, bz = b / blocksX * Block;
                for (int r = 0; r < Block; r++)
                {
                    ulong row = rows[r];
                    while (row != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(row);
                        row &= row - 1;
                        list.Add((bz + r) * Width + bx + bit);
                    }
                }
            }
        }
        return list;
    }

    /// <summary>The set as bytes for the world save or a network update: header, then deflated (block, 64 rows) pairs.</summary>
    public byte[] Pack()
    {
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true)) { w.Write(Magic); w.Write(Version); w.Write(Width); w.Write(Height); }
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
        using (var w = new BinaryWriter(deflate))
        {
            lock (gate)
            {
                w.Write(blocks.Count);
                foreach (var (b, rows) in blocks) { w.Write(b); foreach (ulong row in rows) w.Write(row); }
            }
        }
        return output.ToArray();
    }

    /// <summary>A packed set of this grid size; throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static CellSet Unpack(byte[] packed, int width, int height)
    {
        var set = new CellSet(width, height);
        if (packed == null) throw new InvalidDataException("No cell set");
        try
        {
            using var input = new MemoryStream(packed);
            using var r = new BinaryReader(input);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version || r.ReadInt32() != width || r.ReadInt32() != height)
                throw new InvalidDataException("Not a cell set of this grid");
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var d = new BinaryReader(deflate);
            int n = d.ReadInt32();
            if (n < 0 || n > set.blocksX * set.blocksZ) throw new InvalidDataException("Too many blocks");
            for (int i = 0; i < n; i++)
            {
                int b = d.ReadInt32();
                if (b < 0 || b >= set.blocksX * set.blocksZ) throw new InvalidDataException("Block outside the grid");
                var rows = new ulong[Block];
                int bx = b % set.blocksX * Block, bz = b / set.blocksX * Block;
                for (int k = 0; k < Block; k++)
                {
                    ulong row = d.ReadUInt64();
                    // Bits past the grid's edge in a partial block are dropped.
                    if (bz + k >= height) row = 0;
                    else if (bx + Block > width) row &= (1UL << (width - bx)) - 1;
                    rows[k] = row;
                    set.count += BitOperations.PopCount(row);
                }
                if (!set.blocks.TryAdd(b, rows)) throw new InvalidDataException("Block twice");
            }
            return set;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("The cell set is cut short"); }
    }
}
