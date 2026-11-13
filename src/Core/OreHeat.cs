using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// Ore heat maps on the full (creative) atlas (WM-D47, tiled in WM-D48): the tiles a view shows, the cells they hold, the
/// colours the answers are drawn in, and the prospecting pick's words for them. Each cell holds the ore blocks a chunk
/// column (32 × 32 blocks, surface to the bottom) is expected to have there, and the factor the pick reports (vanilla
/// lists an ore only above <see cref="TraceFactor"/>).
/// <para>Tiles follow the atlas's own tiles (<see cref="TileKey"/>: level L spans <see cref="TileData.Size"/> × 2^L blocks
/// from the ring's start) with <see cref="TileCells"/> × <see cref="TileCells"/> cells, so a cell is 4 × 2^L blocks and
/// level <see cref="MinLevel"/> has one chunk per cell. The server works out a tile for every ore at once and stores it;
/// any zoom shows the tiles it has, a coarser one standing in until a finer one arrives.</para>
/// </summary>
public static class OreHeat
{
    public const int ChunkSize = 32;
    /// <summary>Cells per side of a tile.</summary>
    public const int TileCells = 32, TileCount = TileCells * TileCells;
    /// <summary>The finest level: one chunk per cell.</summary>
    public const int MinLevel = 3;
    /// <summary>Screen pixels per cell at least: finer cells would only cost samples.</summary>
    public const double MinCellPixels = 10;
    /// <summary>Tiles one request may ask for; the server refuses more.</summary>
    public const int MaxTilesPerRequest = 64;
    /// <summary>Ores a stored tile may hold (the pick knows a few dozen).</summary>
    public const int MaxOres = 256;
    /// <summary>The code that asks for the number of ores the pick would list at each cell instead of one ore.</summary>
    public const string Listed = "*";
    /// <summary>The pick lists an ore only when its factor exceeds this (PropickReading.ToHumanReadable); smaller
    /// readings are summed up as "miniscule amounts".</summary>
    public const double TraceFactor = 0.025;
    /// <summary>The game's language keys for the pick's density words, by <see cref="WordIndex"/>.</summary>
    public static readonly string[] WordKeys = { "propick-density-verypoor", "propick-density-poor", "propick-density-decent",
        "propick-density-high", "propick-density-veryhigh", "propick-density-ultrahigh" };
    /// <summary>The pick's word for a factor (its index into <see cref="WordKeys"/>), or -1 below <see cref="TraceFactor"/>.</summary>
    public static int WordIndex(double factor) => factor > TraceFactor ? (int)Math.Clamp(factor * 7.5, 0, 5) : -1;

    /// <summary>Blocks per cell at a level (a power-of-two number of chunks from <see cref="MinLevel"/> up).</summary>
    public static double CellSize(int level) => (double)TileData.Size * (1L << level) / TileCells;
    /// <summary>Blocks per tile side at a level, as the atlas's <see cref="TileKey.Span"/>.</summary>
    public static double Span(int level) => (double)TileData.Size * (1L << level);
    /// <summary>The coarsest level: one tile holds the whole world (never finer than <see cref="MinLevel"/>).</summary>
    public static int MaxLevel(Topology t) => Math.Max(MinLevel, t.MaxLevel);

    /// <summary>The level a view at <paramref name="pixelsPerBlock"/> asks for: the finest whose cells are at least
    /// <paramref name="minCellPixels"/> on screen, between <see cref="MinLevel"/> and <see cref="MaxLevel"/>.</summary>
    public static int LevelFor(Topology t, double pixelsPerBlock, double minCellPixels = MinCellPixels)
    {
        int max = MaxLevel(t);
        if (!(pixelsPerBlock > 0) || double.IsInfinity(pixelsPerBlock)) return max;
        for (int level = MinLevel; level < max; level++) if (CellSize(level) * pixelsPerBlock >= minCellPixels) return level;
        return max;
    }

    /// <summary>Whether a tile asked for over the network exists: a level the client could ask for, inside the world.</summary>
    public static bool ValidKey(Topology t, int level, int x, int z) => t != null && level >= MinLevel && level <= MaxLevel(t) && x >= 0 && z >= 0
        && x * Span(level) < t.Width && z * Span(level) < t.Height;

    /// <summary>The tiles a view of blocks x1..x2 (east-west, may run past a ring's seam), z1..z2 shows at a level, on
    /// every copy of the ring it crosses: the atlas's own selection (<see cref="TileSelection.Visible"/>).</summary>
    public static List<TileInstance> Visible(Topology t, double x1, double z1, double x2, double z2, int level)
    {
        if (!(x2 > x1) || !(z2 > z1)) return new List<TileInstance>();
        var camera = new MapCamera { X = (x1 + x2) / 2, Z = (z1 + z2) / 2, Scale = 1 };
        return TileSelection.Visible(t, camera, x2 - x1, z2 - z1, level).ToList();
    }

    /// <summary>The tile holding world position x (any copy of the ring), z at a level.</summary>
    public static (int X, int Z) KeyAt(Topology t, int level, double x, double z)
    {
        double span = Span(level);
        return ((int)Math.Floor((t.Canonical(x) - t.StartX) / span), (int)Math.Floor(z / span));
    }

    /// <summary>The cell of tile <paramref name="tx"/>, <paramref name="tz"/> holding world position x (any copy of the
    /// ring), z, or -1 outside the tile.</summary>
    public static int CellAt(Topology t, int level, int tx, int tz, double x, double z)
    {
        double cell = CellSize(level), span = Span(level);
        int c = (int)Math.Floor((t.Canonical(x) - t.StartX - tx * span) / cell), r = (int)Math.Floor((z - tz * span) / cell);
        return c >= 0 && c < TileCells && r >= 0 && r < TileCells ? r * TileCells + c : -1;
    }

    /// <summary>The block a cell is sampled at: the centre of the chunk at the cell's centre (deposit factors and the
    /// pick's reading are per chunk), not yet wrapped onto the ring. NaN for cells whose centre lies past the world's
    /// east end (the next copy's first tile covers them) or south edge.</summary>
    public static (double X, double Z) Sample(Topology t, int level, int tx, int tz, int column, int row)
    {
        double cell = CellSize(level), span = Span(level);
        double x = t.StartX + tx * span + (column + 0.5) * cell, z = tz * span + (row + 0.5) * cell;
        if (x >= t.EndX || z >= t.Height) return (double.NaN, double.NaN);
        return (Math.Floor(x / ChunkSize) * ChunkSize + ChunkSize / 2.0, Math.Floor(z / ChunkSize) * ChunkSize + ChunkSize / 2.0);
    }

    /// <summary>The coarse tiles the server stores ahead of time: whole levels from the one tile holding the world down,
    /// coarsest first, while the total stays within <paramref name="budget"/> (the top level always).</summary>
    public static List<(int Level, int X, int Z)> StoreAheadKeys(Topology t, int budget)
    {
        var keys = new List<(int, int, int)>();
        for (int level = MaxLevel(t); level >= MinLevel; level--)
        {
            double span = Span(level);
            int columns = (int)Math.Ceiling(t.Width / span), rows = (int)Math.Ceiling(t.Height / span);
            if (keys.Count > 0 && keys.Count + (long)columns * rows > budget) break;
            for (int z = 0; z < rows; z++) for (int x = 0; x < columns; x++) keys.Add((level, x, z));
        }
        return keys;
    }

    /// <summary>A tile's values (row-major from the north-west cell) worked out from the tile <paramref name="up"/> levels
    /// above holding it: each cell takes the coarser cell its own lies in. For drawing until the tile itself arrives.</summary>
    public static float[] Derive(float[] ancestor, int up, int tx, int tz)
    {
        var values = new float[TileCount];
        if (ancestor == null || ancestor.Length != TileCount || up < 1 || up > 30) { Array.Fill(values, float.NaN); return values; }
        long n = 1L << up;
        long ax = (tx >> up) * (long)TileCells, az = (tz >> up) * (long)TileCells;
        for (int r = 0; r < TileCells; r++)
        {
            int ar = (int)(((long)tz * TileCells + r) / n - az);
            for (int c = 0; c < TileCells; c++)
            {
                int ac = (int)(((long)tx * TileCells + c) / n - ax);
                values[r * TileCells + c] = ancestor[ar * TileCells + ac];
            }
        }
        return values;
    }

    /// <summary>
    /// Every ore's values over one tile, as the server works them out and stores them: per ore (in the server's catalogue
    /// order) and cell, the ore blocks per chunk and the pick's factor, as half floats; NaN where there is no answer
    /// (vanilla's ore maps of regions not loaded) or off the world. Only complete tiles are stored.
    /// </summary>
    public sealed class OreTile
    {
        public const int Magic = 0x31484F57; // "WOH1"
        public const int Version = 1;
        public readonly int Ores;
        /// <summary>[ore × <see cref="TileCount"/> + cell].</summary>
        public readonly Half[] Blocks, Factors;
        /// <summary>Every cell on the world has an answer for every ore.</summary>
        public bool Complete;

        public OreTile(int ores)
        {
            if (ores < 0 || ores > MaxOres) throw new ArgumentOutOfRangeException(nameof(ores));
            Ores = ores; Blocks = new Half[ores * TileCount]; Factors = new Half[ores * TileCount];
        }

        /// <summary>Half floats hold ore counts to 65,504; larger values are kept at that.</summary>
        public static Half ToHalf(double value) => double.IsNaN(value) ? Half.NaN : (Half)Math.Clamp(value, 0, 65504);

        public void Set(int ore, int cell, double blocks, double factor)
        {
            Blocks[ore * TileCount + cell] = ToHalf(blocks);
            Factors[ore * TileCount + cell] = ToHalf(factor);
        }

        /// <summary>One ore's cells: blocks and factors.</summary>
        public (float[] Blocks, float[] Factors) One(int ore)
        {
            var blocks = new float[TileCount];
            var factors = new float[TileCount];
            for (int i = 0; i < TileCount; i++) { blocks[i] = (float)Blocks[ore * TileCount + i]; factors[i] = (float)Factors[ore * TileCount + i]; }
            return (blocks, factors);
        }

        /// <summary>Per cell, the number of ores the pick would list (factor above <see cref="TraceFactor"/>) and the
        /// strongest factor; NaN where no ore has an answer.</summary>
        public (float[] Counts, float[] Strongest) Listed()
        {
            var counts = new float[TileCount];
            var strongest = new float[TileCount];
            for (int i = 0; i < TileCount; i++)
            {
                int n = 0; float best = 0; bool known = false;
                for (int ore = 0; ore < Ores; ore++)
                {
                    float f = (float)Factors[ore * TileCount + i];
                    if (float.IsNaN(f)) continue;
                    known = true;
                    if (f > TraceFactor) n++;
                    if (f > best) best = f;
                }
                counts[i] = known ? n : float.NaN;
                strongest[i] = known ? best : float.NaN;
            }
            return (counts, strongest);
        }

        public static int MaxPackedBytes(int ores) => 12 + ores * TileCount * 4 + 4096;

        /// <summary>Header (magic, version, ores) and the deflated half floats: every ore's blocks, then every ore's factors.</summary>
        public byte[] Pack()
        {
            var raw = new byte[Ores * TileCount * 4];
            int half = Ores * TileCount;
            for (int i = 0; i < half; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), BitConverter.HalfToUInt16Bits(Blocks[i]));
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((half + i) * 2), BitConverter.HalfToUInt16Bits(Factors[i]));
            }
            using var output = new MemoryStream();
            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], Ores);
            output.Write(header);
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) deflate.Write(raw);
            return output.ToArray();
        }

        /// <summary>A stored tile, checked: the header, <paramref name="ores"/> ores, exactly the data they need.</summary>
        public static OreTile Unpack(byte[] data, int ores)
        {
            if (ores < 0 || ores > MaxOres || data == null || data.Length < 12 || data.Length > MaxPackedBytes(ores)) throw new InvalidDataException("Invalid ore tile length");
            if (BinaryPrimitives.ReadInt32LittleEndian(data) != Magic || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) != Version
                || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) != ores) throw new InvalidDataException("Ore tile of another catalogue");
            var raw = new byte[ores * TileCount * 4];
            using (var input = new MemoryStream(data, 12, data.Length - 12))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length) { int n = inflate.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
                if (read != raw.Length || inflate.ReadByte() != -1) throw new InvalidDataException("Ore tile data of the wrong size");
            }
            var tile = new OreTile(ores) { Complete = true };
            int half = ores * TileCount;
            for (int i = 0; i < half; i++)
            {
                tile.Blocks[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)));
                tile.Factors[i] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((half + i) * 2)));
            }
            return tile;
        }
    }

    /// <summary>Ore blocks per chunk the colour ramp spans: 1 to 10⁴, logarithmic. Vanilla's 256-high worlds average
    /// about 130 blocks of copper ore per land chunk.</summary>
    public const double RampDecades = 4;
    /// <summary>Ores listed by the pick that reach the top of the ramp in <see cref="Listed"/> maps.</summary>
    public const int ListedTop = 8;
    // Blue → green → yellow → red, readable over the chart's paper and the terrain's colours.
    static readonly (double t, byte r, byte g, byte b)[] ramp =
    {
        (0, 52, 40, 140), (0.25, 30, 110, 210), (0.5, 40, 175, 110), (0.75, 245, 200, 40), (1, 215, 35, 30)
    };

    /// <summary>Ramp position 0..1 for ore blocks per chunk; NaN when there is no ore (under half a block) or no answer.</summary>
    public static double RampOf(double blocks) => double.IsFinite(blocks) && blocks >= 0.5 ? Math.Clamp(Math.Log10(Math.Max(1, blocks)) / RampDecades, 0, 1) : double.NaN;
    /// <summary>Ramp position for a count of listed ores: 0 for none (drawn grey, the pick finds nothing).</summary>
    public static double RampOfListed(double count) => double.IsFinite(count) && count >= 0 ? Math.Clamp(count / ListedTop, 0, 1) : double.NaN;

    /// <summary>The game's RGBA int (r | g ≪ 8 | b ≪ 16 | a ≪ 24), alpha premultiplied, for a ramp position; transparent for NaN.
    /// <paramref name="grey"/> draws position 0 grey (the pick lists nothing) instead of the ramp's first colour.</summary>
    public static int Color(double position, bool grey = false)
    {
        if (double.IsNaN(position)) return 0;
        double p = Math.Clamp(position, 0, 1);
        double r, g, b;
        if (grey && p <= 0) { r = g = b = 120; }
        else
        {
            int i = 1;
            while (i < ramp.Length - 1 && ramp[i].t < p) i++;
            var (t0, r0, g0, b0) = ramp[i - 1];
            var (t1, r1, g1, b1) = ramp[i];
            double f = (p - t0) / (t1 - t0);
            r = r0 + (r1 - r0) * f; g = g0 + (g1 - g0) * f; b = b0 + (b1 - b0) * f;
        }
        double a = 0.38 + 0.42 * p;
        int A = (int)Math.Round(a * 255), R = (int)Math.Round(r * a), G = (int)Math.Round(g * a), B = (int)Math.Round(b * a);
        return R | G << 8 | B << 16 | A << 24;
    }

    /// <summary>The opaque game ARGB colour (as <c>RenderRectangle</c> takes it) of a ramp position, for the legend.</summary>
    public static int Argb(double position, bool grey = false)
    {
        int c = Color(double.IsNaN(position) ? 0 : position, grey);
        int a = c >>> 24 & 0xff;
        if (a == 0) return unchecked((int)0xff000000);
        int r = Math.Min(255, (c & 0xff) * 255 / a), g = Math.Min(255, (c >> 8 & 0xff) * 255 / a), b = Math.Min(255, (c >> 16 & 0xff) * 255 / a);
        return unchecked((int)0xff000000) | r << 16 | g << 8 | b;
    }

    /// <summary>Pixels for a tile's values, row-major from the north-west cell: ore blocks per chunk, or listed ores.</summary>
    public static int[] Pixels(float[] values, bool listed)
    {
        var pixels = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
            pixels[i] = listed ? Color(RampOfListed(values[i]), true) : Color(RampOf(values[i]));
        return pixels;
    }

    /// <summary>Short form of a block count for the legend and hover: 0.4, 7, 130, 2.4k, 18k.</summary>
    public static string Short(double blocks)
    {
        if (!double.IsFinite(blocks)) return "?";
        if (blocks < 1) return blocks.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (blocks < 1000) return blocks.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (blocks < 10000) return (blocks / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
        return (blocks / 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k";
    }
}
