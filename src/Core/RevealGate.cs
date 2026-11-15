using System;
using System.IO;
using System.IO.Compression;

namespace Wmap.Core;

using Codec = Wmap.Core.Semantic;

/// <summary>
/// What a survival client may be sent of the prediction (WM-D63): only land the player has seen. The server blanks a tile's
/// texels whose centre lies in a cell the player's client has not been given, and sends the world chart and the climate
/// raster in blocks holding only such cells' texels. <c>known</c> answers for a cell of the <see cref="MaskGrid"/>.
/// <para>A texel's own values (height, ice, biome, climate) describe its place only. Its distances do not: the coast distance
/// (R) says where the nearest texel of the other kind lies, and the chart's river distance (A) where the nearest river bank
/// is, either of which may be in land not seen. They are limited to what the known texels show, out to
/// <see cref="CoastReach"/> texels (<see cref="LimitCoast"/>, <see cref="LimitRivers"/>). Coarse semantic tiles need nothing
/// more: their texels are point samples at their centres (<see cref="SemanticTile.Sample"/>), not averages over land around
/// them. Biome tiles interpolate between samples up to 16 texels apart (<see cref="BiomeTile.Stride"/>): a texel goes only
/// when all of its samples are in seen land.</para>
/// </summary>
public static class RevealGate
{
    /// <summary>Texels per side of a chart or climate block.</summary>
    public const int BlockSize = 64;

    /// <summary>Texels of coast distance a survival client gets (of the 128 the codec holds): out to this a texel's distance
    /// is the one the known land shows, past it the far value (<see cref="LimitCoast"/>). Distances are worked out from the
    /// texels this far round a tile or block, so a chart block is packed again when land this near it becomes known. The
    /// atlas reads a tile's coast only within its 22-texel sampling margin and the chart's past it, and fades the chart's
    /// coastal wash and waterlines out by this reach for a survival client (<c>uMaskInfo.y</c>); far terrain uses coast
    /// distances only within ten blocks of the shore. Further would cost more work per block for little to see.</summary>
    public const int CoastReach = 32;

    /// <summary>Texels round a chart block within which a river distance is checked (the river range and the bank's
    /// texel).</summary>
    const int RiverReach = (int)Codec.RiverRange + 2;

    // A texel of a window round a tile or block: an index into the texels held (known), or one of these.
    const int Unknown = -2, Neutral = -1;

    /// <summary>A tile and the <see cref="CoastReach"/> texels round it: which the player has seen. Made once per tile for
    /// its semantic and biome texels.</summary>
    public sealed class TileWindow
    {
        public const int Side = TileData.Padded + 2 * CoastReach;
        /// <summary>Per window texel: the tile texel's index where it is in the tile and known; <see cref="Neutral"/> where
        /// it is known but outside the tile, or beyond the world; <see cref="Unknown"/> where it is a cell not seen.</summary>
        internal readonly int[] At = new int[Side * Side];
        /// <summary>Per tile texel: whether it is known.</summary>
        internal readonly bool[] Inside = new bool[TileData.Padded * TileData.Padded];
        /// <summary>Whether any texel of the tile is known.</summary>
        public bool Any { get; internal set; }
        /// <summary>The tile's level (its biome samples' stride).</summary>
        public int Level { get; internal set; }
    }

    /// <summary>Which texels of a tile, and of the <see cref="CoastReach"/> texels round it, lie in known cells.</summary>
    public static TileWindow Window(TileKey key, MaskGrid grid, Func<int, bool> known)
    {
        var window = new TileWindow { Level = key.Level };
        const int n = TileData.Padded, side = TileWindow.Side, r = CoastReach;
        int lastCell = int.MinValue; bool lastKnown = false;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int tx = x - r, tz = z - r;
            int cell = grid.CellRelative(key.X * key.Span + (tx - 0.5) * key.Step, key.Z * key.Span + (tz - 0.5) * key.Step);
            // Neighbouring texels mostly share a cell.
            if (cell != lastCell) { lastCell = cell; lastKnown = cell >= 0 && known(cell); }
            bool inTile = tx >= 0 && tz >= 0 && tx < n && tz < n;
            // Beyond the world there is no land to give away; the tile's texels there stay blank.
            window.At[z * side + x] = cell < 0 ? Neutral : !lastKnown ? Unknown : inTile ? tz * n + tx : Neutral;
            if (inTile && cell >= 0 && lastKnown) { window.Inside[tz * n + tx] = true; window.Any = true; }
        }
        return window;
    }

    /// <summary>Blanks a semantic tile's texels (130 × 130 RGBA, <see cref="SemanticTile"/>) outside the known cells: all
    /// zero, the codec's unknown. Returns whether any texel is known.</summary>
    public static bool Semantic(byte[] rgba, TileKey key, MaskGrid grid, Func<int, bool> known)
    {
        var window = Window(key, grid, known);
        Semantic(rgba, window);
        return window.Any;
    }

    /// <summary>Blanks a semantic tile's texels outside the known cells and limits the coast distances of the others to
    /// what known land shows.</summary>
    public static void Semantic(byte[] rgba, TileWindow window)
    {
        for (int i = 0; i < window.Inside.Length; i++) if (!window.Inside[i]) Array.Clear(rgba, i * 4, 4);
        if (!window.Any) return;
        LimitCoast(rgba, window.At, TileWindow.Side, TileWindow.Side, verify: true);
        Spread(rgba, window.Inside, 0, 3);
    }

    /// <summary>Copies the first <paramref name="channels"/> bytes of seen texels into the blanked texels beside them, two
    /// texels out (their A, the coverage, stays 0), as <see cref="SemanticTile.Encode(byte[], short[], int, int, byte[], double)"/>
    /// does for a recording's edge: filtering between a seen texel and a blanked one blends like data, not toward zero (a ring
    /// of water round land not seen). <paramref name="offset"/>: where in the RGBA the half begins (a biome tile's season).
    /// The copies are of seen texels, so they say nothing more.</summary>
    static void Spread(byte[] rgba, bool[] inside, int offset, int channels)
    {
        const int n = TileData.Padded;
        var done = (bool[])inside.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            var next = (bool[])done.Clone();
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                if (done[z * n + x]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + SpreadX[k], nz = z + SpreadZ[k];
                    if (nx < 0 || nz < 0 || nx >= n || nz >= n || !done[nz * n + nx]) continue;
                    Buffer.BlockCopy(rgba, offset + (nz * n + nx) * 4, rgba, offset + (z * n + x) * 4, channels);
                    next[z * n + x] = true;
                    break;
                }
            }
            done = next;
        }
    }
    static readonly int[] SpreadX = { -1, 1, 0, 0, -1, 1, -1, 1 }, SpreadZ = { 0, 0, -1, 1, -1, -1, 1, 1 };

    /// <summary>Blanks a biome tile's texels (130 × 260 RGBA: the biome over the season, <see cref="BiomeTile"/>) outside
    /// the known cells: no biome, and the season <see cref="SeasonSample.None"/>. Returns whether any texel is known.</summary>
    public static bool Biome(byte[] rgba, TileKey key, MaskGrid grid, Func<int, bool> known)
    {
        var window = Window(key, grid, known);
        Biome(rgba, window);
        return window.Any;
    }

    /// <summary>Blanks a biome tile's texels outside the known cells (see <see cref="Biome(byte[], TileKey, MaskGrid, Func{int, bool})"/>),
    /// and those interpolated from a sample in land not seen.</summary>
    public static void Biome(byte[] rgba, TileWindow window)
    {
        bool season = rgba.Length >= 2 * BiomeTile.HalfBytes;
        var none = SeasonSample.None;
        int stride = BiomeTile.Stride(window.Level), coarse = BiomeTile.Coarse(stride);
        var sent = new bool[window.Inside.Length];
        bool any = false;
        for (int i = 0; i < window.Inside.Length; i++)
        {
            if (window.Inside[i] && SamplesSeen(window, i % TileData.Padded, i / TileData.Padded, stride, coarse)) { sent[i] = any = true; continue; }
            int o = i * 4;
            Array.Clear(rgba, o, 4);
            if (!season) continue;
            o += BiomeTile.HalfBytes;
            rgba[o] = none.Share; rgba[o + 1] = none.Dry; rgba[o + 2] = none.WetCos; rgba[o + 3] = none.WetSin;
        }
        if (!any) return;
        Spread(rgba, sent, 0, 3);
        if (season) Spread(rgba, sent, BiomeTile.HalfBytes, 4);
    }

    /// <summary>A survival client's raster (the chart, the climate raster) where it holds no texel yet: each such texel within
    /// <paramref name="reach"/> texels of known ones takes the nearest known texel's RGBA, the rest <paramref name="neutral"/>
    /// (or stays as it is when null), over [x0, x1) × [z0, z1) (x wrapping where the world does). Filtering and mipmaps then
    /// blend known land with its own continuation, not with zeros (which read as deep sea and the middle of a river, and drew
    /// rings of water round land not seen). Made from known texels only, so it says nothing more; far terrain still draws
    /// only known texels.</summary>
    public static void Extend(BaseRaster raster, byte[] known, int x0, int z0, int x1, int z1, bool wraps, int reach, byte[] neutral = null)
    {
        Extend(raster, known, x0, z0, x1, z1, wraps, reach, neutral, out _);
    }
    [ThreadStatic] static int[] extendSource, extendColumns;
    [ThreadStatic] static float[] extendDistance;

    /// <summary><see cref="Extend(BaseRaster, byte[], int, int, int, int, bool, int, byte[])"/>, saying whether it changed a
    /// texel.</summary>
    public static void Extend(BaseRaster raster, byte[] known, int x0, int z0, int x1, int z1, bool wraps, int reach, byte[] neutral, out bool changed)
    {
        changed = false;
        int width = raster.Width, height = raster.Height;
        z0 = Math.Max(0, z0); z1 = Math.Min(height, z1);
        if (!wraps) { x0 = Math.Max(0, x0); x1 = Math.Min(width, x1); }
        if (x1 <= x0 || z1 <= z0) return;
        if (x1 - x0 > width) { x0 = 0; x1 = width; }
        // A window reaching past the rectangle by the reach: known texels there are sources too. Its buffers are kept per
        // thread: a client applies blocks by the hundred after a greeting, each a window of about 25,000 texels.
        int wx0 = x0 - reach, wz0 = Math.Max(0, z0 - reach), w = x1 - x0 + 2 * reach, h = Math.Min(height, z1 + reach) - wz0;
        if (extendSource == null || extendSource.Length < w * h) { extendSource = new int[w * h]; extendDistance = new float[w * h]; }
        if (extendColumns == null || extendColumns.Length < w) extendColumns = new int[w];
        int[] source = extendSource, columns = extendColumns;
        float[] distance = extendDistance;
        for (int x = 0; x < w; x++)
        {
            int rx = wx0 + x;
            if (wraps) rx = (rx % width + width) % width;
            columns[x] = rx < 0 || rx >= width ? -1 : rx;
        }
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int i = z * w + x, rx = columns[x];
            int r = rx < 0 ? -1 : (wz0 + z) * width + rx;
            bool isKnown = r >= 0 && known[r] != 0;
            source[i] = isKnown ? r : -1;
            distance[i] = isKnown ? 0 : float.MaxValue;
        }
        // Two chamfer passes carrying the nearest known texel (8 neighbours, diagonal √2).
        const float D = 1.41421356f;
        void Relax(int i, int j, float cost)
        {
            if (source[j] < 0 || distance[j] + cost >= distance[i]) return;
            distance[i] = distance[j] + cost; source[i] = source[j];
        }
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int i = z * w + x;
            if (x > 0) Relax(i, i - 1, 1);
            if (z > 0) { Relax(i, i - w, 1); if (x > 0) Relax(i, i - w - 1, D); if (x < w - 1) Relax(i, i - w + 1, D); }
        }
        for (int z = h - 1; z >= 0; z--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = z * w + x;
            if (x < w - 1) Relax(i, i + 1, 1);
            if (z < h - 1) { Relax(i, i + w, 1); if (x < w - 1) Relax(i, i + w + 1, D); if (x > 0) Relax(i, i + w - 1, D); }
        }
        for (int z = z0; z < z1; z++)
        for (int x = x0; x < x1; x++)
        {
            int rx = columns[x - wx0], r = z * width + rx;
            if (known[r] != 0) continue;
            int i = (z - wz0) * w + (x - wx0);
            int from = source[i] >= 0 && distance[i] <= reach ? source[i] * 4 : -1;
            if (from < 0 && neutral == null) continue;
            byte[] values = from >= 0 ? raster.Rgba : neutral;
            if (from < 0) from = 0;
            if (BitConverter.ToInt32(values, from) == BitConverter.ToInt32(raster.Rgba, r * 4)) continue;
            Buffer.BlockCopy(values, from, raster.Rgba, r * 4, 4);
            changed = true;
        }
    }

    /// <summary>Whether every sample a biome texel is interpolated from (<see cref="BiomeTile.Between"/>) lies in land seen, or
    /// beyond the world, where none is taken.</summary>
    static bool SamplesSeen(TileWindow window, int x, int z, int stride, int coarse)
    {
        var (cx, fx) = BiomeTile.Between(x, stride, coarse);
        var (cz, fz) = BiomeTile.Between(z, stride, coarse);
        for (int k = 0; k < 4; k++)
        {
            double weight = ((k & 1) == 0 ? 1 - fx : fx) * ((k >> 1) == 0 ? 1 - fz : fz);
            if (weight <= 0) continue;
            int wx = Math.Min(coarse - 1, cx + (k & 1)) * stride + CoastReach, wz = Math.Min(coarse - 1, cz + (k >> 1)) * stride + CoastReach;
            if (wx >= TileWindow.Side || wz >= TileWindow.Side || window.At[wz * TileWindow.Side + wx] == Unknown) return false;
        }
        return true;
    }

    /// <summary>Whether a texel of a world-spanning raster (the chart, the climate raster: <paramref name="width"/> ×
    /// <paramref name="height"/> texels over the whole world) may be sent: its centre lies in a known cell, or for a
    /// raster coarser than the grid (<paramref name="anyPart"/>, the climate raster) its centre or one of its corners does.
    /// A climate texel describes the climate at its place, smoothly varying, so one reaching half a texel past seen land
    /// gives away no more than the climate there; without its corners, land seen in a sliver of a coarse texel would show
    /// no climate at all.</summary>
    public static bool Texel(int x, int z, int width, int height, MaskGrid grid, Func<int, bool> known, bool anyPart)
    {
        var t = grid.Topology;
        double sx = t.Width / width, sz = t.Height / height;
        if (known(grid.CellRelative((x + 0.5) * sx, (z + 0.5) * sz))) return true;
        if (!anyPart) return false;
        // Just inside the corners, so a corner on a cell boundary counts for the texel's own side.
        const double e = 0.01;
        return known(grid.CellRelative((x + e) * sx, (z + e) * sz)) || known(grid.CellRelative((x + 1 - e) * sx, (z + e) * sz))
            || known(grid.CellRelative((x + e) * sx, (z + 1 - e) * sz)) || known(grid.CellRelative((x + 1 - e) * sx, (z + 1 - e) * sz));
    }

    /// <summary>A block of a raster for a client: which of its texels are known (a bit each, row by row) and their RGBA
    /// (the others zero), deflated; null when none is known. For the world chart (<paramref name="chart"/>) the coast and
    /// river distances are limited to what known texels show, from the <see cref="CoastReach"/> texels round the block
    /// (wrapping east and west where the world does). <paramref name="known"/> takes texels inside the raster.</summary>
    public static byte[] PackBlock(BaseRaster raster, int bx, int bz, Func<int, int, bool> known, bool chart = false, bool wraps = false)
    {
        int x0 = bx * BlockSize, z0 = bz * BlockSize, w = Math.Min(BlockSize, raster.Width - x0), h = Math.Min(BlockSize, raster.Height - z0);
        if (w <= 0 || h <= 0) return null;
        // Most blocks marked round newly known land hold none of it: those cost no window.
        bool any = false;
        for (int z = 0; z < h && !any; z++)
        for (int x = 0; x < w; x++) if (known(x0 + x, z0 + z)) { any = true; break; }
        if (!any) return null;
        int reach = chart ? CoastReach : 0, side = BlockSize + 2 * reach;
        // The block and the texels round it, copied where known: the distances are limited in this copy, never in the raster.
        var at = new int[side * side];
        var window = new byte[side * side * 4];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = z * side + x, rx = x0 + x - reach, rz = z0 + z - reach;
            if (wraps) rx = (int)Topology.Mod(rx, raster.Width);
            // Beyond the world: nothing there.
            if (rz < 0 || rz >= raster.Height || rx < 0 || rx >= raster.Width) { at[i] = Neutral; continue; }
            if (!known(rx, rz)) { at[i] = Unknown; continue; }
            at[i] = i;
            Buffer.BlockCopy(raster.Rgba, (rz * raster.Width + rx) * 4, window, i * 4, 4);
        }
        if (chart)
        {
            LimitCoast(window, at, side, side, verify: false);
            LimitRivers(window, at, side, reach, w, h);
        }
        var bits = new byte[(BlockSize * BlockSize + 7) / 8];
        var rgba = new byte[BlockSize * BlockSize * 4];
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int i = (z + reach) * side + x + reach;
            if (at[i] < 0) continue;
            int k = z * BlockSize + x;
            bits[k >> 3] |= (byte)(1 << (k & 7));
            Buffer.BlockCopy(window, i * 4, rgba, k * 4, 4);
        }
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true)) { deflate.Write(bits); deflate.Write(rgba); }
        return output.ToArray();
    }

    /// <summary>
    /// Limits the signed coast distances (channel R) of the known texels in a window (<paramref name="at"/>: per window
    /// texel, the index of its RGBA in <paramref name="rgba"/>, or <see cref="Neutral"/> / <see cref="Unknown"/>; the window
    /// reaches <see cref="CoastReach"/> texels past those that matter) so they say nothing of land not seen. A distance tells
    /// where the nearest texel of the other kind (land or water) lies and that none lies nearer. It becomes the distance to
    /// the nearest known texel of the other kind in the window, which is never shorter and is the true one wherever the
    /// known land shows it, and from <see cref="CoastReach"/> on the far value (±128). With <paramref name="verify"/> (a
    /// tile, whose texels round it are not at hand: its distances were measured over its sampling margin) a distance that
    /// every texel as near is known to confirm stays as it is.
    /// </summary>
    static void LimitCoast(byte[] rgba, int[] at, int w, int h, bool verify)
    {
        float[] near = verify ? NearUnknown(at, w, h, CoastReach) : null;
        byte[] seen = null;
        for (int i = 0; i < at.Length; i++)
        {
            int o = at[i];
            if (o < 0) continue;
            if (verify && Upper(rgba[o * 4], Codec.CoastRange) + 0.5 < near[i]) continue;
            seen ??= SeenCoast(rgba, at, w, h);
            rgba[o * 4] = seen[i];
        }
    }

    /// <summary>Per window texel, the coast distance (channel R's byte) from its known texels only, the others of neither
    /// kind, and the far value from <see cref="CoastReach"/> on.</summary>
    static byte[] SeenCoast(byte[] rgba, int[] at, int w, int h)
    {
        var field = new byte[w * h * 4];
        Codec.CoastField(field, w, h, i => at[i] < 0 ? (sbyte)-1 : rgba[at[i] * 4] < 128 ? (sbyte)1 : (sbyte)0, false);
        var coast = new byte[w * h];
        for (int i = 0; i < coast.Length; i++)
        {
            byte b = field[i * 4];
            coast[i] = Math.Abs(Codec.DecodeSigned(b, Codec.CoastRange)) + 0.5 < CoastReach ? b : b < 128 ? (byte)0 : (byte)255;
        }
        return coast;
    }

    /// <summary>Clears the river distance (channel A, 255 none) of a chart block's texels whose nearest river bank may lie
    /// in land not seen: kept where every texel within its distance and a texel more is known. Rivers stop a texel or two
    /// short of the edge of the known land. The block is the window's <paramref name="w"/> × <paramref name="h"/> texels
    /// <paramref name="reach"/> in from its corner.</summary>
    static void LimitRivers(byte[] rgba, int[] at, int side, int reach, int w, int h)
    {
        bool any = false;
        for (int z = 0; z < h && !any; z++)
        for (int x = 0; x < w; x++)
        {
            int o = at[(z + reach) * side + x + reach];
            if (o >= 0 && rgba[o * 4 + 3] != 255) { any = true; break; }
        }
        if (!any) return;
        // Only the texels within the river reach of the block matter: a smaller window for the distances.
        int pad = reach - RiverReach, n = side - 2 * pad;
        if (pad < 0) throw new InvalidOperationException("The window is smaller than the river reach");
        var inner = new int[n * n];
        for (int z = 0; z < n; z++) Array.Copy(at, (z + pad) * side + pad, inner, z * n, n);
        var near = NearUnknown(inner, n, n, RiverReach);
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int o = at[(z + reach) * side + x + reach];
            if (o < 0 || rgba[o * 4 + 3] == 255) continue;
            if (Upper(rgba[o * 4 + 3], Codec.RiverRange) + 1 < near[(z + RiverReach) * n + x + RiverReach]) continue;
            rgba[o * 4 + 3] = 255;
        }
    }

    /// <summary>Per window texel, the distance in texels to the nearest <see cref="Unknown"/> one, up to
    /// <paramref name="reach"/> (which stands for that far or more).</summary>
    static float[] NearUnknown(int[] at, int w, int h, int reach)
    {
        var near = new float[w * h];
        if (Array.IndexOf(at, Unknown) < 0) { Array.Fill(near, reach); return near; }
        Codec.Distance(w, h, i => at[i] == Unknown, false, reach, (i, d) => near[i] = (float)d);
        return near;
    }

    /// <summary>The largest magnitude a signed byte (<see cref="Codec.EncodeSigned"/>) may stand for: its value half a step
    /// out, so rounding never lets a distance past the check.</summary>
    static double Upper(byte value, double range)
    {
        double e = Math.Abs(value / 127.5 - 1) + 0.5 / 127.5;
        return e * e * range;
    }

    /// <summary>What <see cref="ApplyBlock"/> changed: the texels newly known and their bounds (raster texels, the far edges
    /// exclusive), and whether any texel changed at all (a known texel's distances can change as more land round it is
    /// seen).</summary>
    public readonly record struct BlockChange(int Fresh, int X0, int Z0, int X1, int Z1, bool Changed);

    /// <summary>Writes a block from <see cref="PackBlock"/> into a client's raster and its known texels (one byte each, 255
    /// known). Throws <see cref="InvalidDataException"/> for a damaged block.</summary>
    public static BlockChange ApplyBlock(byte[] packed, int bx, int bz, BaseRaster raster, byte[] known)
    {
        var bits = new byte[(BlockSize * BlockSize + 7) / 8];
        var rgba = new byte[BlockSize * BlockSize * 4];
        try
        {
            using var input = new MemoryStream(packed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            deflate.ReadExactly(bits); deflate.ReadExactly(rgba);
            if (deflate.ReadByte() != -1) throw new InvalidDataException("Oversized block");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("The block is cut short"); }
        int x0 = bx * BlockSize, z0 = bz * BlockSize, w = Math.Min(BlockSize, raster.Width - x0), h = Math.Min(BlockSize, raster.Height - z0);
        if (x0 < 0 || z0 < 0 || w <= 0 || h <= 0) throw new InvalidDataException("Block outside the raster");
        int fresh = 0, fx0 = int.MaxValue, fz0 = int.MaxValue, fx1 = int.MinValue, fz1 = int.MinValue;
        bool changed = false;
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int k = z * BlockSize + x;
            if ((bits[k >> 3] & (1 << (k & 7))) == 0) continue;
            int i = (z0 + z) * raster.Width + x0 + x;
            if (known[i] == 0)
            {
                known[i] = 255;
                fresh++; changed = true;
                fx0 = Math.Min(fx0, x0 + x); fz0 = Math.Min(fz0, z0 + z); fx1 = Math.Max(fx1, x0 + x + 1); fz1 = Math.Max(fz1, z0 + z + 1);
            }
            else if (!changed && BitConverter.ToInt32(rgba, k * 4) != BitConverter.ToInt32(raster.Rgba, i * 4)) changed = true;
            Buffer.BlockCopy(rgba, k * 4, raster.Rgba, i * 4, 4);
        }
        return fresh > 0 ? new(fresh, fx0, fz0, fx1, fz1, true) : new(0, 0, 0, 0, 0, changed);
    }

    /// <summary>The blocks of a raster (<paramref name="width"/> × <paramref name="height"/> texels over the world) that hold
    /// a texel of a grid cell or one within <paramref name="margin"/> texels of it (at least one: the cell's own texels as the
    /// rasters sample them): its whole cell rectangle, mapped onto the raster.</summary>
    public static void BlocksOfCell(int cell, MaskGrid grid, int width, int height, Action<int, int> visit, int margin = 1)
        => BlocksOfCells(cell % grid.Width, cell / grid.Width, cell % grid.Width + 1, cell / grid.Width + 1, grid, width, height, visit, margin);

    /// <summary>The blocks of a raster that hold a texel of a rectangle of grid cells ([x0, x1) × [z0, z1)) or one within
    /// <paramref name="margin"/> texels of it (see <see cref="BlocksOfCell"/>). Each block once. On a wrapping world the texels
    /// wrap before they are divided into blocks, so a last block narrower than the margin still reaches the ones before it.</summary>
    public static void BlocksOfCells(int cx0, int cz0, int cx1, int cz1, MaskGrid grid, int width, int height, Action<int, int> visit, int margin = 1)
    {
        var t = grid.Topology;
        double sx = width / t.Width, sz = height / t.Height;
        margin = Math.Max(1, margin);
        int x0 = (int)Math.Floor(cx0 * grid.TexelX * sx) - margin, x1 = (int)Math.Floor(cx1 * grid.TexelX * sx) + margin;
        int z0 = Math.Max(0, (int)Math.Floor(cz0 * grid.TexelZ * sz) - margin), z1 = Math.Min(height - 1, (int)Math.Floor(cz1 * grid.TexelZ * sz) + margin);
        if (z1 < z0) return;
        int bz0 = z0 / BlockSize, bz1 = z1 / BlockSize;
        void Columns(int a0, int a1)
        {
            for (int bz = bz0; bz <= bz1; bz++)
            for (int bx = a0 / BlockSize; bx <= a1 / BlockSize; bx++) visit(bx, bz);
        }
        if (!grid.Wraps) { x0 = Math.Max(0, x0); x1 = Math.Min(width - 1, x1); if (x1 >= x0) Columns(x0, x1); return; }
        if (x1 - x0 + 1 >= width) { Columns(0, width - 1); return; }
        int w0 = (int)Topology.Mod(x0, width), w1 = w0 + (x1 - x0);
        if (w1 < width) { Columns(w0, w1); return; }
        // Across the seam: two ranges, or every column where they meet in one block.
        if ((w1 - width) / BlockSize >= w0 / BlockSize) { Columns(0, width - 1); return; }
        Columns(w0, width - 1);
        Columns(0, w1 - width);
    }
}
