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
                int a = index[(j + n) * side + i + n], b = index[(j + n) * side + i + 1 + n];
                int c = index[(j + 1 + n) * side + i + n], d = index[(j + 1 + n) * side + i + 1 + n];
                indices.Add(a); indices.Add(c); indices.Add(b);
                indices.Add(b); indices.Add(c); indices.Add(d);
            }
        }
        mesh.Vertices = vertices.ToArray();
        mesh.Indices = indices.ToArray();
        return mesh;
    }

    /// <summary>The ring a point at this Chebyshev distance from the origin draws with; edges belong to the inner ring.</summary>
    public int RingAt(double chebyshev)
    {
        for (int ring = 0; ring < Rings; ring++) if (chebyshev <= RingRadius(ring) + 1e-3) return ring;
        return Rings - 1;
    }
}
