using System;
using System.Collections.Generic;

namespace Wmap.Core;

/// <summary>
/// The exploration mask's grid over a world: cells of 32 blocks or more, at most 8192 on a side, spanning the world
/// exactly (a ring that is not a whole number of nominal cells wraps at its own circumference). The client's mask
/// (<c>ExploredMask</c>) and the server's record of what each player has charted and seen (WM-D63) share it, and so does
/// line of sight. Thread-safe: it holds geometry only.
/// </summary>
public sealed class MaskGrid
{
    public readonly int Width, Height;
    /// <summary>The nominal cell size in blocks, the larger of <see cref="TexelX"/> and <see cref="TexelZ"/>.</summary>
    public readonly double Texel;
    /// <summary>Blocks per cell along each axis.</summary>
    public readonly double TexelX, TexelZ;
    public readonly bool Wraps;
    public readonly Topology Topology;
    public int Count => Width * Height;

    public MaskGrid(Topology t)
    {
        Topology = t; Wraps = t.Wraps;
        (Width, Height) = Dimensions(t);
        TexelX = t.Width / Width; TexelZ = t.Height / Height;
        Texel = Math.Max(TexelX, TexelZ);
    }

    /// <summary>The grid's size in cells for a world: cells of 32 blocks or more, at most 8192 on a side.</summary>
    public static (int Width, int Height) Dimensions(Topology t)
    {
        double nominal = Math.Max(32, Math.Ceiling(Math.Max(t.Width, t.Height) / 8192));
        return (Math.Max(1, (int)Math.Ceiling(t.Width / nominal)), Math.Max(1, (int)Math.Ceiling(t.Height / nominal)));
    }

    /// <summary>The cell holding a world position (any lap), or -1 outside the world.</summary>
    public int Cell(double x, double z) => CellRelative(Topology.Canonical(x) - Topology.StartX, z);

    /// <summary>The cell holding a position given east of the world's start (tiles' coordinates; any lap where the world
    /// wraps), or -1 outside the world.</summary>
    public int CellRelative(double rel, double z)
    {
        if (Wraps) rel = Topology.Mod(rel, Topology.Width);
        int ix = (int)Math.Floor(rel / TexelX), iz = (int)Math.Floor(z / TexelZ);
        return ix < 0 || iz < 0 || ix >= Width || iz >= Height ? -1 : iz * Width + ix;
    }

    /// <summary>The cells and the eight beside each of them (each once; wrapping where the world does).</summary>
    public List<int> WithNeighbours(IReadOnlyList<int> cells)
    {
        var all = new HashSet<int>(cells);
        var result = new List<int>(all);
        foreach (int cell in cells)
        {
            int cx = cell % Width, cz = cell / Width;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (z < 0 || z >= Height) continue;
                if (Wraps) x = (x % Width + Width) % Width;
                else if (x < 0 || x >= Width) continue;
                int n = z * Width + x;
                if (all.Add(n)) result.Add(n);
            }
        }
        return result;
    }

    /// <summary>The cells whose centres lie within <paramref name="radius"/> of a world position, and the one holding it.</summary>
    public List<int> Disc(double x, double z, double radius)
    {
        var cells = new List<int>();
        double cx = (Topology.Canonical(x) - Topology.StartX) / TexelX - 0.5, cz = z / TexelZ - 0.5;
        int rx = (int)Math.Ceiling(radius / TexelX) + 1, rz = (int)Math.Ceiling(radius / TexelZ) + 1;
        int x0 = (int)Math.Floor(cx) - rx, x1 = (int)Math.Ceiling(cx) + rx;
        int z0 = Math.Max(0, (int)Math.Floor(cz) - rz), z1 = Math.Min(Height - 1, (int)Math.Ceiling(cz) + rz);
        if (!Wraps) { x0 = Math.Max(0, x0); x1 = Math.Min(Width - 1, x1); }
        else if (x1 - x0 + 1 > Width) { x0 = (int)Math.Floor(cx) - Width / 2; x1 = x0 + Width - 1; }
        for (int iz = z0; iz <= z1; iz++)
        for (int ix = x0; ix <= x1; ix++)
        {
            double ex = (ix - cx) * TexelX, ez = (iz - cz) * TexelZ;
            if (ex * ex + ez * ez > radius * radius) continue;
            int wx = Wraps ? (int)Topology.Mod(ix, Width) : ix;
            cells.Add(iz * Width + wx);
        }
        int own = Cell(x, z);
        if (own >= 0 && !cells.Contains(own)) cells.Add(own);
        return cells;
    }

    /// <summary>The distinct cells line of sight sees from an eye at x, z (<see cref="Viewshed.Cast"/>): rays sample the
    /// near ground many times over, so cells are collected once here.</summary>
    /// <param name="charted">Cells already charted, which count as seen when enclosing a hidden patch; null: none.</param>
    /// <param name="holeCells">Hidden patches of at most this many cells that the sight's own cells and the charted ones
    /// enclose are charted too (0: none). A patch reaching the sight's edge stays hidden: it may be the start of more.</param>
    public int[] SightCells(HeightGrid heights, double x, double z, double eye, double distance, double step, Func<int, bool> charted = null,
        int holeCells = 0, double radius = 0)
    {
        // Seen cells lie within distance (plus a cell) of the eye: a bounded window, wrapping around the ring.
        int rx = (int)Math.Ceiling(distance / TexelX) + 2, rz = (int)Math.Ceiling(distance / TexelZ) + 2;
        double rel = Wraps ? Topology.Mod(x - Topology.StartX, Topology.Width) : x - Topology.StartX;
        int cx = (int)Math.Floor(rel / TexelX), cz = (int)Math.Floor(z / TexelZ), side = 2 * rx + 1;
        var seen = new bool[side * (2 * rz + 1)];
        var cells = new List<int>();
        Viewshed.Cast(heights, x, z, eye, distance, step, (px, pz) =>
        {
            int i = Cell(px, pz);
            if (i < 0) return;
            int ix = i % Width, iz = i / Width, dx = ix - cx;
            if (Wraps) dx -= (int)Math.Round((double)dx / Width) * Width;
            int lx = dx + rx, lz = iz - cz + rz;
            if (lx < 0 || lz < 0 || lx >= side || lz > 2 * rz) { cells.Add(i); return; }
            int k = lz * side + lx;
            if (seen[k]) return;
            seen[k] = true; cells.Add(i);
        }, radius);
        if (holeCells > 0) FillHoles(seen, side, 2 * rz + 1, rx, rz, cx, cz, distance, holeCells, charted ?? (_ => false), cells);
        return cells.ToArray();
    }

    /// <summary>Adds to <paramref name="cells"/> the hidden patches of the sight window (4-connected) that lie within the
    /// sight's reach, touch neither its edge nor the world's, and have at most <paramref name="limit"/> cells. Cells
    /// already charted count as seen, so a patch between this sight and earlier ones is enclosed too.</summary>
    void FillHoles(bool[] seen, int side, int rows, int rx, int rz, int cx, int cz, double distance, int limit, Func<int, bool> charted, List<int> cells)
    {
        int Index(int lx, int lz)
        {
            int ix = cx + lx - rx, iz = cz + lz - rz;
            if (iz < 0 || iz >= Height) return -1;
            if (Wraps) ix = (int)Topology.Mod(ix, Width);
            else if (ix < 0 || ix >= Width) return -1;
            return iz * Width + ix;
        }
        // Within reach: the cell's centre no further than the sight goes.
        bool Inside(int lx, int lz)
        {
            double dx = (lx - rx) * TexelX, dz = (lz - rz) * TexelZ;
            return dx * dx + dz * dz <= distance * distance && Index(lx, lz) >= 0;
        }
        var visited = new bool[seen.Length];
        var patch = new List<int>();
        var queue = new Queue<int>();
        for (int start = 0; start < seen.Length; start++)
        {
            if (visited[start] || seen[start]) continue;
            int sx = start % side, sz = start / side;
            if (!Inside(sx, sz) || charted(Index(sx, sz))) { visited[start] = true; continue; }
            patch.Clear(); queue.Clear();
            bool open = false;
            visited[start] = true; queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), lx = k % side, lz = k / side;
                patch.Add(k);
                for (int d = 0; d < 4; d++)
                {
                    int nx = lx + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = lz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= side || nz >= rows || !Inside(nx, nz)) { open = true; continue; }
                    int n = nz * side + nx;
                    if (visited[n] || seen[n] || charted(Index(nx, nz))) continue;
                    visited[n] = true; queue.Enqueue(n);
                }
            }
            if (open || patch.Count > limit) continue;
            foreach (int k in patch) cells.Add(Index(k % side, k / side));
        }
    }
}
