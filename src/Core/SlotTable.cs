using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

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
