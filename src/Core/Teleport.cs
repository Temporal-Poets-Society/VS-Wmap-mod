using System;

namespace Wmap.Core;

/// <summary>Where a right-click on the atlas teleports a creative player.</summary>
public static class Teleport
{
    /// <summary>The block column for a map position: x on the ring's own lap (across the seam), inside the world.
    /// Null beyond the poles, past the ends of a map that does not wrap, or for a position that is not a number.</summary>
    public static (int X, int Z)? Column(Topology t, double x, double z, int mapSizeX, int mapSizeZ)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || z < 0 || z >= t.Height) return null;
        if (!t.Wraps && (x < t.StartX || x >= t.EndX)) return null;
        x = t.Canonical(x);
        int bx = Math.Clamp((int)Math.Floor(x), 1, mapSizeX - 2), bz = Math.Clamp((int)Math.Floor(z), 1, mapSizeZ - 2);
        return (bx, bz);
    }
}
