using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// The game's colour of a recorded sample and its block light, packed into <see cref="TileData.Colors"/> (the mid-distance
/// view, WM-P06 prototype). The low 24 bits are the colour as <c>Block.GetColor</c> gives it (red in the low byte, as the
/// game's map uploads it), the top byte 0x80 | structure (0x20) | block light (0..31). WCL6 adds structure provenance;
/// WCL5 colours/lights remain readable, without structure evidence. The terrain tile codec is unchanged.
/// Recordings before 0.4.4 stored <c>Block.GetColor</c> raw, alpha in the top byte (usually 0xFF: flag set, light 31), and
/// 0.4.4 to 0.4.11 stored 0. So a tile says which it holds (0.5.0-pre.3): <see cref="Mark"/> in its padding corner, which no
/// sample and no paper-map merge ever writes. Only in a marked tile are colours this format (<see cref="Of"/>); the recorder
/// marks a tile when it first writes a colour into it, clearing pre-WCL5 colours or upgrading WCL5 colours in place.
/// </summary>
public static class RecordedColor
{
    /// <summary>Block light from which a recorded sample is a light seen from afar (a torch gives 14, a lantern more).</summary>
    public const int Lit = 8;
    public static int Pack(int gameColor, int light) => Pack(gameColor, light, false);
    public static int Pack(int gameColor, int light, bool structure) =>
        (gameColor & 0xFFFFFF) | ((0x80 | (structure ? 0x20 : 0) | Math.Clamp(light, 0, 31)) << 24);
    /// <summary>Index 0, the north-west padding corner: the recorder copies samples into the padding along the edges only, and
    /// merges copy known cells only, so it is 0 in every tile written before.</summary>
    public const int MarkIndex = 0, MarkValue = 0x57434C36, LegacyMarkValue = 0x57434C35; // WCL6 / WCL5
    public static bool Marked(TileData tile) => tile.ColorAt(MarkIndex) is MarkValue or LegacyMarkValue;
    public static bool Current(TileData tile) => tile.ColorAt(MarkIndex) == MarkValue;
    /// <summary>Makes a tile hold WCL6, preserving valid WCL5 colours/lights and clearing earlier raw colours.</summary>
    public static void Mark(TileData tile)
    {
        if (Current(tile)) return;
        if (Marked(tile))
        {
            // V5 has colour/light but no evidence that any cell is a structure. Keep valid colours, never invent boxes.
            for (int i = 0; i < TileData.Count; i++)
                if (((tile.ColorAt(i) >> 29) & 7) != 4) tile.Colors[i] = 0;
        }
        else Array.Clear(tile.Colors);
        tile.Colors[MarkIndex] = MarkValue;
    }
    /// <summary>A cell's colour as this format reads it: 0 (none) in a tile not marked.</summary>
    public static int Of(TileData tile, int i) => Current(tile) ? tile.ColorAt(i)
        : Marked(tile) && ((tile.ColorAt(i) >> 29) & 7) == 4 ? tile.ColorAt(i) : 0;
    /// <summary>A colour of this format: its top byte is 0x80 to 0xBF (bit 5 is structure provenance in WCL6). 0.5.0-pre.4: the top bit alone also
    /// took a colour that a recorder from before 0.4.4 wrote raw (top byte 0xFF) into a marked tile after a rollback, as light
    /// 31. Such a colour is ignored now; one whose alpha was 0x80 to 0x9F would still pass (the game's block colours are
    /// opaque).</summary>
    public static bool Has(int packed) => ((packed >> 30) & 3) == 2;
    public static bool IsStructure(int packed) => Has(packed) && (packed & 0x20000000) != 0;
    public static int Light(int packed) => Has(packed) ? (packed >> 24) & 31 : 0;
    public static byte R(int packed) => (byte)packed;
    public static byte G(int packed) => (byte)(packed >> 8);
    public static byte B(int packed) => (byte)(packed >> 16);
    /// <summary>Whether a sample whose terrain did not change is still a change: it now has a colour where it had none, or its
    /// light came on, went out or changed by a step of 4. A colour alone is not (climate tints and seasons vary it).</summary>
    public static bool Changed(int old, int now) => Has(now) && (!Has(old) || IsStructure(old) != IsStructure(now)
        || (Light(old) >= Lit) != (Light(now) >= Lit) || Light(old) / 4 != Light(now) / 4);
}

/// <summary>
/// Recorded tiles as far terrain draws them over the prediction (WM-P06): per padded texel four half floats (the top of the
/// cell in world y, its coverage, 1 where its colour was recorded, 1 for water) and the game's colour (bytes). Recordings
/// made before 0.5 have no colour: far terrain keeps its own colours there. See docs/mid-distance-view.md.
/// </summary>
public static class RecordedTexels
{
    /// <summary>The top of a recorded cell in world y: land's top face, water's surface (a water block is 7/8 high, so the
    /// sea's surface is at SeaLevel − 1/8, where far terrain draws the sea).</summary>
    public static double Top(int height, int depth, byte kind) => Semantic.IsWater(kind) ? height + depth + 0.875 : height + 1;

    public const int Halves = 4;

    /// <summary>Fills <paramref name="heights"/> (<see cref="Halves"/> per texel: top, coverage, colour known, water) and
    /// <paramref name="rgba"/> (4 per texel). Cells not recorded have coverage 0 and take a recorded neighbour's values (two
    /// passes), so filtering at the edge of what was recorded does not pull heights toward zero.</summary>
    public static void Build(TileData data, Half[] heights, byte[] rgba)
    {
        const int n = TileData.Padded;
        // Scratch reused per thread (the recorded tiles' worker builds one tile after another).
        var known = Clear(ref knownScratch); var filled = Clear(ref filledScratch); var next = Clear(ref nextScratch);
        var tops = Clear(ref topsScratch); var colored = Clear(ref coloredScratch); var wet = Clear(ref wetScratch);
        Array.Clear(rgba);
        for (int i = 0; i < TileData.Count; i++)
        {
            byte kind = data.Kinds[i];
            if (kind == TileData.Unknown) continue;
            known[i] = true;
            tops[i] = (float)Top(data.Heights[i], data.Depths[i], kind);
            int c = RecordedColor.Of(data, i);
            wet[i] = Semantic.IsWater(kind) ? 1 : 0;
            colored[i] = RecordedColor.Has(c) ? 1 : 0;
            rgba[i * 4] = RecordedColor.R(c); rgba[i * 4 + 1] = RecordedColor.G(c); rgba[i * 4 + 2] = RecordedColor.B(c); rgba[i * 4 + 3] = 255;
        }
        Array.Copy(known, filled, known.Length);
        for (int pass = 0; pass < 2; pass++)
        {
            Array.Copy(filled, next, filled.Length);
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                if (filled[i]) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + NeighbourX[k], nz = z + NeighbourZ[k];
                    if (nx < 0 || nz < 0 || nx >= n || nz >= n || !filled[nz * n + nx]) continue;
                    int s = nz * n + nx;
                    tops[i] = tops[s]; colored[i] = colored[s]; wet[i] = wet[s];
                    Array.Copy(rgba, s * 4, rgba, i * 4, 4);
                    next[i] = true;
                    break;
                }
            }
            (filled, next) = (next, filled);
        }
        for (int i = 0; i < TileData.Count; i++)
        {
            heights[i * Halves] = (Half)tops[i];
            heights[i * Halves + 1] = known[i] ? (Half)1 : (Half)0;
            heights[i * Halves + 2] = (Half)colored[i];
            heights[i * Halves + 3] = (Half)wet[i];
        }
    }

    static readonly int[] NeighbourX = { -1, 1, 0, 0, -1, 1, -1, 1 }, NeighbourZ = { 0, 0, -1, 1, -1, -1, 1, 1 };
    [ThreadStatic] static bool[] knownScratch, filledScratch, nextScratch;
    [ThreadStatic] static float[] topsScratch, coloredScratch, wetScratch;
    static T[] Clear<T>(ref T[] scratch) { if (scratch == null) scratch = new T[TileData.Count]; else Array.Clear(scratch); return scratch; }

    /// <summary>The smooth mesh's height above the sea surface where a four-block recording has <paramref name="weight"/>
    /// (coverage × fade), over the prediction's (<paramref name="predicted"/>, the sea's bed negative). Mirrors
    /// wmapWithRecorded (wmapfartiles.ash): the sea keeps the predicted bed and a lake its recorded surface; recorded land
    /// keeps the predicted height where the near band predicts land (<paramref name="banded"/>), since the recorder records
    /// block by block wherever the world differs from it (WM-D29); elsewhere the recorded height.</summary>
    public static double Smooth(double weight, double recorded, bool recordedWater, double predicted, bool banded)
    {
        if (weight <= 0) return predicted;
        double y = recordedWater ? (recorded < 0.5 ? Math.Min(predicted, recorded) : recorded) : banded && predicted >= 0 ? predicted : recorded;
        return predicted + (y - predicted) * weight;
    }
}

/// <summary>
/// Terraces near the real chunks (WM-P06): each cell is a flat top at a whole-block height with vertical walls to its
/// neighbours. farcellmap.fsh works out each cell's top on the GPU, farcells.vsh places this template; both mirror
/// <see cref="CellTop"/>. A cell draws its top and its east and south walls; a wall runs between its own top and its
/// neighbour's, so each edge between two cells is closed once, whichever side is higher.
/// </summary>
public sealed class TerracePatch
{
    /// <summary>Cells per patch side. A patch is one instance of the terrace draw.</summary>
    public const int Cells = 32;
    /// <summary>Per vertex: cell i, j in the patch, corner x, z (0 or 1), side (0 top, 1 east wall, 2 south wall), and 1 when
    /// the vertex takes the neighbour's top (the wall's other edge).</summary>
    public const int Floats = 6;
    public const int VerticesPerCell = 12, IndicesPerCell = 18;
    public float[] Vertices;
    public int[] Indices;

    /// <summary>Vertex order per cell: the top's corners (0,0), (1,0), (0,1), (1,1); the east wall's own top at its north and
    /// south end, then the neighbour's; the south wall's likewise from west to east.</summary>
    public static TerracePatch Build(int cells = Cells)
    {
        var vertices = new float[cells * cells * VerticesPerCell * Floats];
        var indices = new int[cells * cells * IndicesPerCell];
        int v = 0, k = 0;
        void Add(int i, int j, int cx, int cz, int side, int neighbour)
        {
            vertices[v++] = i; vertices[v++] = j; vertices[v++] = cx; vertices[v++] = cz; vertices[v++] = side; vertices[v++] = neighbour;
        }
        for (int j = 0; j < cells; j++)
        for (int i = 0; i < cells; i++)
        {
            int b = (j * cells + i) * VerticesPerCell;
            Add(i, j, 0, 0, 0, 0); Add(i, j, 1, 0, 0, 0); Add(i, j, 0, 1, 0, 0); Add(i, j, 1, 1, 0, 0);
            Add(i, j, 1, 0, 1, 0); Add(i, j, 1, 1, 1, 0); Add(i, j, 1, 0, 1, 1); Add(i, j, 1, 1, 1, 1);
            Add(i, j, 0, 1, 2, 0); Add(i, j, 1, 1, 2, 0); Add(i, j, 0, 1, 2, 1); Add(i, j, 1, 1, 2, 1);
            foreach (int q in new[] { 0, 4, 8 })
            {
                indices[k++] = b + q; indices[k++] = b + q + 2; indices[k++] = b + q + 1;
                indices[k++] = b + q + 1; indices[k++] = b + q + 2; indices[k++] = b + q + 3;
            }
        }
        return new TerracePatch { Vertices = vertices, Indices = indices };
    }

    /// <summary>The template's quads in cell units, with heights from <paramref name="top"/>(cell i, j), as the vertex shader
    /// places them: four corners each, in the vertex order above.</summary>
    public IEnumerable<(double X, double Y, double Z)[]> Quads(Func<int, int, double> top)
    {
        for (int q = 0; q < Vertices.Length / Floats; q += 4)
        {
            var quad = new (double, double, double)[4];
            for (int c = 0; c < 4; c++)
            {
                int o = (q + c) * Floats;
                int i = (int)Vertices[o], j = (int)Vertices[o + 1], side = (int)Vertices[o + 4];
                bool neighbour = Vertices[o + 5] > 0.5f;
                int ni = i + (neighbour && side == 1 ? 1 : 0), nj = j + (neighbour && side == 2 ? 1 : 0);
                quad[c] = (i + Vertices[o + 2], top(ni, nj), j + Vertices[o + 3]);
            }
            yield return quad;
        }
    }

    /// <summary>A cell's top in world y: the two-block recording (where the world differs from the prediction); else the
    /// prediction where the near band predicts land and the four-block recording is land; else the four-block recording;
    /// else the prediction. Land at a whole block, water at its surface. Mirrors farcellmap.fsh (without the canopy).</summary>
    public static double CellTop(double? fine, double? coarse, bool coarseWater, double predicted, bool predictedWater, bool banded) =>
        fine ?? (coarse != null && (coarseWater || !banded || predictedWater) ? coarse.Value : predictedWater ? predicted : Math.Round(predicted));
}

/// <summary>A lit recorded sample: blocks east of the ring's start, world y of its top, z, and its block light.</summary>
public readonly record struct LightSample(double X, double Y, double Z, int Light);

/// <summary>A distant light: merged lit samples, at their light-weighted centre and the highest top among them; brightness
/// 0..1 from the brightest sample and how many there are.</summary>
public readonly record struct LightPoint(double X, double Y, double Z, float Brightness, int Count);

/// <summary>Distant lights from recordings (WM-P06): lit samples merged into a few points per chunk, so a town reads as a
/// cluster of glints and a lighthouse as one bright point.</summary>
public static class LightPoints
{
    public const int PerChunk = 4;
    /// <summary>Lit samples this close (blocks, horizontally) to a point's first sample join it.</summary>
    public const double MergeRadius = 8;

    /// <summary>Lit samples of a four-block recording tile (<paramref name="key"/> level 2 or coarser), in blocks east of
    /// the ring's start.</summary>
    public static IEnumerable<LightSample> Of(TileData tile, TileKey key)
    {
        double step = key.Step;
        for (int iz = 1; iz <= TileData.Size; iz++)
        for (int ix = 1; ix <= TileData.Size; ix++)
        {
            int i = iz * TileData.Padded + ix, light = RecordedColor.Light(RecordedColor.Of(tile, i));
            if (light < RecordedColor.Lit || tile.Kinds[i] == TileData.Unknown) continue;
            yield return new LightSample((key.X * TileData.Size + ix - 1 + 0.5) * step, RecordedTexels.Top(tile.Heights[i], tile.Depths[i], tile.Kinds[i]),
                (key.Z * TileData.Size + iz - 1 + 0.5) * step, light);
        }
    }

    /// <summary>Merges lit samples per 32-block chunk: the brightest unmerged sample starts a point and takes every unmerged
    /// sample within <see cref="MergeRadius"/>; at most <see cref="PerChunk"/> points per chunk, the rest are left out.</summary>
    public static List<LightPoint> Merge(IEnumerable<LightSample> samples)
    {
        var points = new List<LightPoint>();
        foreach (var chunk in samples.Where(s => s.Light >= RecordedColor.Lit).GroupBy(s => ((long)Math.Floor(s.X / 32), (long)Math.Floor(s.Z / 32))))
        {
            var left = chunk.OrderByDescending(s => s.Light).ThenBy(s => s.X).ThenBy(s => s.Z).ToList();
            for (int p = 0; p < PerChunk && left.Count > 0; p++)
            {
                var first = left[0];
                var members = left.Where(s => (s.X - first.X) * (s.X - first.X) + (s.Z - first.Z) * (s.Z - first.Z) <= MergeRadius * MergeRadius).ToList();
                left.RemoveAll(members.Contains);
                double weight = members.Sum(s => s.Light);
                float brightness = (float)Math.Clamp(first.Light / 24.0 * (1 + 0.15 * Math.Log2(members.Count)), 0.1, 1);
                points.Add(new LightPoint(members.Sum(s => s.X * s.Light) / weight, members.Max(s => s.Y), members.Sum(s => s.Z * s.Light) / weight, brightness, members.Count));
            }
        }
        return points;
    }
}

/// <summary>
/// Which tile each slot of an 8 × 8 toroidal texture atlas holds (<see cref="FarTileWindow"/>), for the recorded tiles of
/// the mid-distance view: a tile placed once stays in its slot while it stays in the window, also across the seam when the
/// ids' lap is kept (<see cref="LapCounter"/>). Each slot fades in over <see cref="FadeMs"/> after its tile arrives.
/// </summary>
public sealed class SlotTable
{
    public const int Count = FarTileWindow.Slots * FarTileWindow.Slots;
    public const long FadeMs = 1500;
    public readonly int[] Ids = new int[Count], Rows = new int[Count];
    public readonly TileKey?[] Keys = new TileKey?[Count];
    public readonly long[] Versions = new long[Count];
    readonly long[] since = new long[Count];
    public readonly float[] Fade = new float[Count];
    public SlotTable() { Array.Fill(Ids, FarTileWindow.Empty); Array.Fill(Rows, FarTileWindow.Empty); }
    public static int SlotOf(int id, int row) => FarTileWindow.Slot(id) + FarTileWindow.Slots * FarTileWindow.Slot(row);
    /// <summary>Whether the cell's tile is in its slot, under the same unwrapped id and row.</summary>
    public bool Holds(FarTileWindow.Cell cell)
    {
        int s = SlotOf(cell.Id, cell.Z);
        return Ids[s] == cell.Id && Rows[s] == cell.Z && Keys[s] == cell.Key;
    }
    /// <summary>Puts a cell's tile in its slot. A tile already there with a newer version keeps its fade.</summary>
    public void Place(FarTileWindow.Cell cell, long version, long now)
    {
        int s = SlotOf(cell.Id, cell.Z);
        bool same = Keys[s] == cell.Key;
        Ids[s] = cell.Id; Rows[s] = cell.Z; Keys[s] = cell.Key; Versions[s] = version;
        if (!same) { since[s] = now; Fade[s] = 0; }
    }
    /// <summary>The same tile under a new id (the window moved a lap): keeps its texels and fade.</summary>
    public void Rekey(FarTileWindow.Cell cell) { int s = SlotOf(cell.Id, cell.Z); Ids[s] = cell.Id; Rows[s] = cell.Z; }
    public void Clear(int slot) { Ids[slot] = Rows[slot] = FarTileWindow.Empty; Keys[slot] = null; Fade[slot] = 0; }
    public void UpdateFades(long now)
    {
        for (int s = 0; s < Count; s++) Fade[s] = Keys[s] == null ? 0 : (float)Math.Clamp((now - since[s]) / (double)FadeMs, 0, 1);
    }
}

/// <summary>
/// Where far terrain starts (0.5.0-pre.2). The game fades its chunks out from 0.75 of the view distance (water) and 0.80 to 0.85
/// (land), so far terrain starts at 0.75 of it; but after the view distance grows, after a teleport and while travelling
/// fast, the chunk columns there are not loaded yet, and the game shows the sky where they are missing. Far terrain then
/// starts at the nearest column that is not loaded, so it fills in until they arrive (reported in game at Santorini after
/// switching the view distance to 700: a pale band of sky between the real sea and the terraces).
/// </summary>
public static class FarStart
{
    public static double For(int viewDistance, double loadedRadius = double.PositiveInfinity) => Math.Max(48, Math.Min(0.75 * viewDistance, loadedRadius));

    /// <summary>Columns on each side of the camera's in <see cref="Scan"/>'s mask: enough for 0.75 of the game's largest view
    /// distance (1536).</summary>
    public const int MaskReach = 37, MaskSide = 2 * MaskReach + 1;

    /// <summary>Blocks from (<paramref name="x"/>, <paramref name="z"/>) to the nearest point of the nearest chunk column that
    /// <paramref name="loaded"/> says is not loaded, looking no farther than <paramref name="reach"/> (returned when all are).
    /// Columns at negative indices do not exist and count as loaded.</summary>
    public static double LoadedRadius(Func<int, int, bool> loaded, double x, double z, double reach) => Scan(loaded, x, z, reach, null);

    /// <summary>As <see cref="LoadedRadius"/>, and fills <paramref name="mask"/> (<see cref="MaskSide"/>², row by row, the camera's
    /// column in the middle) with 255 for a loaded column and 0 for one not loaded (0.5.0-pre.3): far terrain draws nearer than
    /// 0.75 of the view distance only over columns not loaded, not in every direction (0.5.0-pre.2 pulled its start in all
    /// round, over loaded ground, for one missing column).</summary>
    public static double Scan(Func<int, int, bool> loaded, double x, double z, double reach, byte[] mask)
    {
        int cx = (int)Math.Floor(x / 32), cz = (int)Math.Floor(z / 32), r = Math.Min(MaskReach, (int)Math.Ceiling(reach / 32) + 1);
        if (mask != null) Array.Fill(mask, (byte)255);
        double nearest = reach;
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int ix = cx + dx, iz = cz + dz;
            double ex = Math.Max(0, Math.Max(ix * 32 - x, x - (ix * 32 + 32))), ez = Math.Max(0, Math.Max(iz * 32 - z, z - (iz * 32 + 32)));
            double d = Math.Sqrt(ex * ex + ez * ez);
            if (d >= reach || ix < 0 || iz < 0 || loaded(ix, iz)) continue;
            if (mask != null) mask[(dz + MaskReach) * MaskSide + dx + MaskReach] = 0;
            nearest = Math.Min(nearest, d);
        }
        return nearest;
    }

    /// <summary>Blocks from the north-west corner of the mask <see cref="Scan"/> made at (<paramref name="scanX"/>,
    /// <paramref name="scanZ"/>) to the camera now: small numbers, so the shader finds a point's column exactly.</summary>
    public static (double X, double Z) MaskOffset(double scanX, double scanZ, double cameraX, double cameraZ) =>
        (cameraX - (Math.Floor(scanX / 32) - MaskReach) * 32, cameraZ - (Math.Floor(scanZ / 32) - MaskReach) * 32);

    /// <summary>Blocks the camera may move from where the mask was made before it is made again at once.</summary>
    public const double Rescan = 32;
    /// <summary>The mask is made again at least this often (milliseconds), for columns that load or unload.</summary>
    public const long ScanMs = 250;

    /// <summary>Whether the mask made at (<paramref name="scanX"/>, <paramref name="scanZ"/>) for <paramref name="scanStart"/>
    /// must be made again: it is due, the camera moved a column or more (a walk, a teleport, or a jump across Cylinder World's
    /// seam, which moves it a whole lap: the mask's columns are the game's, around where it was made), or far terrain's start
    /// changed (the view distance). 0.5.0-pre.4: in 0.5.0-pre.3 only every 250 ms, so a seam jump or teleport drew a frame or
    /// more with the columns of where the camera had been.</summary>
    public static bool ScanDue(double scanX, double scanZ, double scanStart, double cameraX, double cameraZ, double start, long now, long dueAt) =>
        double.IsNaN(scanX) || now >= dueAt || start != scanStart || Math.Abs(cameraX - scanX) >= Rescan || Math.Abs(cameraZ - scanZ) >= Rescan;

    /// <summary>Whether far terrain draws at a point <paramref name="rx"/>, <paramref name="rz"/> blocks from the camera, this far
    /// out: past <paramref name="start"/> always, nearer only over a column the mask has as not loaded. Mirrors wmapfarshade.ash.</summary>
    public static bool Draws(byte[] mask, (double X, double Z) offset, double rx, double rz, double distance, double start)
    {
        if (distance >= start) return true;
        int i = (int)Math.Floor((offset.X + rx) / 32), j = (int)Math.Floor((offset.Z + rz) / 32);
        return i >= 0 && j >= 0 && i < MaskSide && j < MaskSide && mask[j * MaskSide + i] == 0;
    }
}

/// <summary>Counts the laps the far-terrain origin crossed Cylinder World's seam, so unwrapped tile ids stay the same across
/// it (as <c>FarTiles.OriginLap</c>, WM-D25).</summary>
public sealed class LapCounter
{
    double last = double.NaN;
    public int Lap { get; private set; }
    public void Update(Topology t, double originX)
    {
        if (!t.Wraps) return;
        double rel = Topology.Mod(originX - t.StartX, t.Width);
        if (!double.IsNaN(last))
        {
            if (rel - last > t.Width / 2) Lap--;
            else if (last - rel > t.Width / 2) Lap++;
        }
        last = rel;
    }
}
