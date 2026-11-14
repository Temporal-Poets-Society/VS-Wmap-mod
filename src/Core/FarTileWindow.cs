using System;
using System.Collections.Generic;

namespace Wmap.Core;

/// <summary>
/// The tiles far terrain keeps around the camera at one level, and where each one lives in an
/// <see cref="Slots"/> × <see cref="Slots"/> texture atlas. Addressing is toroidal: a tile's slot is its
/// position modulo <see cref="Slots"/>, so moving only replaces the tiles that fall out of the window.
/// East–west positions are counted in "unwrapped" tile ids: lap × <see cref="LapTiles"/> + the tile index in
/// that lap, laps counted from the far-terrain origin's own lap. Consecutive ids are neighbours on the
/// ground, also across Cylinder World's seam where the last tile of a lap can be partial, so any window
/// up to <see cref="Slots"/> tiles wide uses distinct slots. farterrain.vsh mirrors this; see
/// docs/far-terrain-near-band.md.
/// </summary>
public sealed class FarTileWindow
{
    public const int Slots = 8;
    /// <summary>Texels per slot side: the padded tile (<see cref="TileData.Padded"/>).</summary>
    public const int SlotTexels = TileData.Padded;
    public const int AtlasTexels = Slots * SlotTexels;
    /// <summary>A slot key that matches no tile.</summary>
    public const int Empty = int.MinValue / 2;
    /// <summary>Blocks over which detail fades out toward a tile that is missing or still fading in (wmapReadyAt); a seam
    /// tile narrower than this is reached from two tiles away.</summary>
    public const int SeamReach = 48;

    public readonly Topology Topology;
    public readonly int Level;
    public FarTileWindow(Topology topology, int level) { Topology = topology; Level = level; }
    public double Step => 1 << Level;
    public double Span => TileData.Size * Step;
    /// <summary>Tiles per lap; the last one is partial when the ring's circumference is not a multiple of a tile.</summary>
    public int LapTiles => (int)Math.Ceiling(Topology.Width / Span - 1e-9);

    /// <summary>x relative to the world's west edge, within the lap of <paramref name="originX"/>.</summary>
    public double Relative(double originX) => Topology.Wraps ? Topology.Mod(originX - Topology.StartX, Topology.Width) : originX - Topology.StartX;

    /// <summary>The tile at <paramref name="rel"/> (blocks east of the origin lap's west edge): its unwrapped id,
    /// its index in the world, and the position inside it; false beyond a bounded world's edges.</summary>
    public bool TileX(double rel, out int id, out int index, out double inside)
    {
        double lap = Topology.Wraps ? Math.Floor(rel / Topology.Width) : 0;
        double inLap = rel - lap * Topology.Width;
        index = Math.Clamp((int)Math.Floor(inLap / Span), 0, LapTiles - 1);
        inside = inLap - index * Span;
        id = (int)lap * LapTiles + index;
        return Topology.Wraps || (rel >= 0 && rel < Topology.Width);
    }

    public bool TileZ(double z, out int j, out double inside)
    {
        j = (int)Math.Floor(z / Span);
        inside = z - j * Span;
        return z >= 0 && z < Topology.Height;
    }

    public static int Slot(int id) => ((id % Slots) + Slots) % Slots;

    /// <summary>Texel coordinate (texel units, centres at +0.5) inside a slot for a position this many blocks
    /// into the tile. Texel p of a padded tile is centred (p − 0.5) steps from the tile's corner, as
    /// <see cref="SemanticTile.Sample"/> samples it, so the result runs from 1 to 129 across the tile.</summary>
    public double Texel(double inside) => inside / Step + 1;

    /// <summary>West edge of an unwrapped tile id (blocks east of the origin lap's west edge) and its width.</summary>
    public (double West, double Width) Extent(int id)
    {
        int n = LapTiles, lap = (int)Math.Floor((double)id / n), index = id - lap * n;
        double west = lap * Topology.Width + index * Span;
        return (west, Math.Min(Span, Topology.Width - index * Span));
    }

    public readonly record struct Cell(int Id, int Z, TileKey Key, double Distance);

    /// <summary>Tiles within <paramref name="radius"/> blocks (square) of the camera, nearest first.
    /// The camera is given in the same frame: <paramref name="cameraRel"/> east of the origin lap's west edge.</summary>
    public List<Cell> Window(double cameraRel, double cameraZ, double radius)
    {
        var cells = new List<Cell>();
        if (!TileX(cameraRel - radius, out int first, out _, out _) && Topology.Wraps) return cells;
        if (!Topology.Wraps) first = (int)Math.Floor(Math.Max(0, cameraRel - radius) / Span);
        int j0 = (int)Math.Floor(Math.Max(0, cameraZ - radius) / Span), j1 = (int)Math.Floor(Math.Min(Topology.Height - 1e-6, cameraZ + radius) / Span);
        for (int id = first; ; id++)
        {
            var (west, width) = Extent(id);
            if (west > cameraRel + radius) break;
            if (west + width < cameraRel - radius) continue;
            if (!Topology.Wraps && (west < 0 || west >= Topology.Width)) break;
            int index = id - (int)Math.Floor((double)id / LapTiles) * LapTiles;
            for (int j = j0; j <= j1; j++)
            {
                double dx = Math.Max(0, Math.Max(west - cameraRel, cameraRel - (west + width)));
                double dz = Math.Max(0, Math.Max(j * Span - cameraZ, cameraZ - (j + 1) * Span));
                cells.Add(new(id, j, new TileKey(0, Level, index, j), Math.Sqrt(dx * dx + dz * dz)));
            }
            if (cells.Count > Slots * Slots * 4) break;
        }
        cells.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return cells;
    }

    /// <summary>Whether a square window of this radius always fits in distinct slots (a partial seam tile can add one).</summary>
    public bool Fits(double radius) => Math.Ceiling(2 * radius / Span) + 1 + (LapTiles * Span > Topology.Width + 1e-6 ? 1 : 0) <= Slots;
}
