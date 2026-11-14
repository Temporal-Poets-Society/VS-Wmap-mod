using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>
/// Terraces near the real chunks (WM-P06): each cell is a flat top at a whole-block height with vertical walls to its
/// neighbours. farcellmap.fsh works out each cell's top on the GPU, farcells.vsh places this template; both mirror
/// <see cref="CellTop"/>. A cell draws its top and its east and south walls; a wall runs between its own top and its
/// neighbour's, so each edge between two cells is closed once, whichever side is higher.
/// </summary>
public sealed class TerracePatch
{
    /// <summary>Blocks the terraces and the smooth mesh each reach past their join (wmapfarheight.ash); FarFrame.JoinOverlap.</summary>
    public const double JoinOverlap = 16;
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
    /// four-block recording (0.5.3, WM-D61; before, the prediction where the near band predicted land); else the prediction.
    /// Land at a whole block, water at its surface. Mirrors farcellmap.fsh (without the canopy).</summary>
    public static double CellTop(double? fine, double? coarse, bool coarseWater, double predicted, bool predictedWater) =>
        fine ?? (coarse != null ? coarseWater ? coarse.Value : Math.Floor(coarse.Value + 0.5) : predictedWater ? predicted : Math.Floor(predicted + 0.5));
}
