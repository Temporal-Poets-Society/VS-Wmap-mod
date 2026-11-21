using System;
using System.Collections.Generic;

namespace Wmap.Core;

/// <summary>
/// The far-terrain mesh layout: nested square rings around the origin (a clipmap). Ring 0 is a full grid
/// of 2·<see cref="Cells"/> cells per side; every further ring doubles the cell size and the outer radius and
/// leaves out the previous ring's square. The layout is static; the shader places it at the camera,
/// snapped to <see cref="Snap"/>, and displaces it from the world chart. See docs/far-terrain.md.
/// </summary>
public sealed class FarMesh
{
    /// <summary>Cells from a ring's centre to its outer edge, at that ring's cell size. Even.</summary>
    public const int Cells = 64;
    /// <summary>Per vertex: local x, local z (blocks from the snapped origin), stitch offset x, z (blocks; 0 when none).</summary>
    public const int Floats = 4;
    public float[] Vertices;
    public int[] Indices;
    public int Rings;
    public double Cell0, Radius;
    public int VertexCount => Vertices.Length / Floats;
    /// <summary>The coarsest cell size: snapping by it keeps every vertex on its own ring's world grid.</summary>
    public double Snap => Cell0 * (1 << (Rings - 1));
    public double RingRadius(int ring) => Cell0 * Cells * (1 << ring);

    public static double CellFor(int detail) => detail switch { 0 => 64, 2 => 16, _ => 32 };
    /// <summary>Ring 0's cell with the near band: 8-block cells to 512, 16 to 1024, then the normal rings from 32/2048.</summary>
    public const double NearBandCell = 8;

    /// <summary>Rings enough to reach distance blocks, starting from cells of cell0 blocks.</summary>
    public static FarMesh Build(double distance, double cell0)
    {
        var mesh = new FarMesh { Cell0 = cell0 };
        int rings = 1;
        while (mesh.RingRadius(rings - 1) < distance && rings < 12) rings++;
        mesh.Rings = rings;
        mesh.Radius = mesh.RingRadius(rings - 1);
        var vertices = new List<float>();
        var indices = new List<int>();
        var runs = new List<Run>();
        int n = Cells, side = 2 * n + 1, hole = n / 2;
        var index = new int[side * side];
        for (int ring = 0; ring < rings; ring++)
        {
            double cell = cell0 * (1 << ring);
            bool stitch = ring < rings - 1;
            Array.Fill(index, -1);
            // A quad belongs to this ring unless it lies inside the previous ring's square.
            bool Quad(int i, int j) => ring == 0 || !(i >= -hole && i + 1 <= hole && j >= -hole && j + 1 <= hole);
            for (int j = -n; j <= n; j++)
            for (int i = -n; i <= n; i++)
            {
                bool used = false;
                for (int dj = -1; dj <= 0 && !used; dj++)
                for (int di = -1; di <= 0 && !used; di++)
                {
                    int qi = i + di, qj = j + dj;
                    used = qi >= -n && qi < n && qj >= -n && qj < n && Quad(qi, qj);
                }
                if (!used) continue;
                index[(j + n) * side + i + n] = vertices.Count / Floats;
                float sx = 0, sz = 0;
                // Odd vertices on the outer edge sit midway along a coarse cell's edge: the vertex shader collapses each onto
                // its neighbour at local - stitch, so the rings share their edge vertices and leave no T-junction (WM-0028).
                if (stitch && Math.Abs(i) == n && (j & 1) != 0) sz = (float)cell;
                else if (stitch && Math.Abs(j) == n && (i & 1) != 0) sx = (float)cell;
                vertices.Add((float)(i * cell)); vertices.Add((float)(j * cell)); vertices.Add(sx); vertices.Add(sz);
            }
            for (int j = -n; j < n; j++)
            for (int i = -n; i < n; i++)
            {
                if (!Quad(i, j)) continue;
                // A row's quads in a run each, split where the previous ring's square leaves a hole.
                if (runs.Count > 0 && runs[^1] is { } last && last.Ring == ring && last.J == j && last.I1 == i - 1) runs[^1] = last with { I1 = i };
                else runs.Add(new Run(ring, j, i, i, indices.Count));
                int a = index[(j + n) * side + i + n], b = index[(j + n) * side + i + 1 + n];
                int c = index[(j + 1 + n) * side + i + n], d = index[(j + 1 + n) * side + i + 1 + n];
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }
        }
        mesh.Vertices = vertices.ToArray();
        mesh.Indices = indices.ToArray();
        mesh.Runs = runs.ToArray();
        return mesh;
    }

    /// <summary>A row of a ring's quads that follow each other in <see cref="Indices"/>: ring, row j, its first and last quad
    /// i (cells from the origin), and the index of its first quad's first index (six indices a quad).</summary>
    public readonly record struct Run(int Ring, int J, int I0, int I1, int First);
    /// <summary>The quads' rows in index order (<see cref="Ranges"/>).</summary>
    public Run[] Runs;

    /// <summary>
    /// The parts of <see cref="Indices"/> worth drawing with the camera at (<paramref name="cx"/>, <paramref name="cz"/>) blocks
    /// from the mesh's origin, as (first index, index count) in index order, joined where they meet; returns how many. Left
    /// out are the quads whose four corners all lie nearer than <paramref name="from"/> or all farther than <paramref name="to"/>
    /// (horizontally), less or more a cell: the shaders discard every fragment there (wmapRange), and a fragment's distance is
    /// interpolated between its triangle's corners, so none of theirs is drawn either way (0.5.5, WM-0054: up to 0.5.4 the
    /// whole mesh went through every pass, its finest rings under the terraces and the game's chunks only to be discarded). The
    /// cell of margin covers the ring edges' vertices, which move along the edge by up to a cell (farterrain.vsh's stitch).
    /// </summary>
    /// <param name="firsts">Room for at least 2 x <see cref="Runs"/> + 1 ranges.</param>
    public int Ranges(double cx, double cz, double from, double to, int[] firsts, int[] counts)
    {
        int n = 0;
        void Emit(int first, int count)
        {
            if (count <= 0) return;
            if (n > 0 && firsts[n - 1] + counts[n - 1] == first) counts[n - 1] += count;
            else { firsts[n] = first; counts[n] = count; n++; }
        }
        foreach (var run in Runs)
        {
            double c = Cell0 * (1 << run.Ring);
            double z0 = run.J * c - cz, z1 = z0 + c;
            double zNear = z0 <= 0 && z1 >= 0 ? 0 : Math.Min(Math.Abs(z0), Math.Abs(z1)), zFar = Math.Max(Math.Abs(z0), Math.Abs(z1));
            // Quads reaching within the outer circle: the nearest point of quad i lies within `outer` of the camera.
            double outer = to + c;
            if (zNear > outer) continue;
            double reach = Math.Sqrt(outer * outer - zNear * zNear);
            int keepLo = Math.Max(run.I0, (int)Math.Ceiling((cx - reach) / c) - 1), keepHi = Math.Min(run.I1, (int)Math.Floor((cx + reach) / c));
            if (keepLo > keepHi) continue;
            // Quads wholly inside the inner circle: every corner of quad i nearer than `inner`.
            int skipLo = int.MaxValue, skipHi = int.MinValue;
            double inner = from - c;
            if (inner > 0 && zFar < inner)
            {
                double r = Math.Sqrt(inner * inner - zFar * zFar);
                skipLo = Math.Max(keepLo, (int)Math.Floor((cx - r) / c) + 1);
                skipHi = Math.Min(keepHi, (int)Math.Ceiling((cx + r) / c) - 2);
            }
            int First(int i) => run.First + (i - run.I0) * 6;
            if (skipLo > skipHi) Emit(First(keepLo), (keepHi - keepLo + 1) * 6);
            else
            {
                Emit(First(keepLo), (skipLo - keepLo) * 6);
                Emit(First(skipHi + 1), (keepHi - skipHi) * 6);
            }
        }
        return n;
    }

    /// <summary>The ring a point at this Chebyshev distance from the origin draws with; edges belong to the inner ring.</summary>
    public int RingAt(double chebyshev)
    {
        for (int ring = 0; ring < Rings; ring++) if (chebyshev <= RingRadius(ring) + 1e-3) return ring;
        return Rings - 1;
    }
}
