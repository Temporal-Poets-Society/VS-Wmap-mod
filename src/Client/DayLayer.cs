using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Where it is day, twilight and night: the sun's elevation from the game's calendar (<c>Calendar.GetSunPosition</c> at
/// each place, so latitude, seasons and any mod that moves the sun by longitude) sampled over a <see cref="DayGrid"/>,
/// a few rows per tick on the main thread, and handed to the renderer once the grid is complete.
/// </summary>
public sealed class DayLayer
{
    readonly ICoreClientAPI api;
    readonly Func<Topology> topology;
    readonly byte[] building = new byte[DayGrid.Width * DayGrid.Height];
    int row = -1;
    long nextAt;
    public byte[] Ready { get; private set; }
    public bool Wraps { get; private set; }
    /// <summary>Set when a new grid is ready for the renderer; cleared by <see cref="Take"/>.</summary>
    bool fresh;
    double buildMs;
    readonly Vintagestory.API.MathTools.Vec3d place = new();
    /// <summary>Main-thread milliseconds the last complete grid took, over all its ticks.</summary>
    public double LastMs { get; private set; }
    /// <summary>Real seconds between refreshes; the sun moves about 0.6° of the map per game minute at the default day length.</summary>
    public const int RefreshMs = 5000;

    public DayLayer(ICoreClientAPI api, Func<Topology> topology) { this.api = api; this.topology = topology; }

    public void Tick(bool wanted)
    {
        var calendar = api.World?.Calendar;
        if (!wanted || calendar == null) return;
        long now = Environment.TickCount64;
        if (row < 0) { if (now < nextAt) return; row = 0; }
        var t = topology();
        var watch = Stopwatch.StartNew();
        int rows = 16;
        double days = calendar.TotalDays;
        // One position for every sample (0.5.0-pre.4: a new one each, 4096 a tick); the sun's vector is the game's own.
        var at = place;
        DayGrid.BuildRows(t, (x, z) => Math.Asin(Math.Clamp(calendar.GetSunPosition(at.Set(x, 0, z), days).Y, -1, 1)) * 180 / Math.PI, building, row, row + rows);
        buildMs += watch.Elapsed.TotalMilliseconds;
        row += rows;
        if (row < DayGrid.Height) return;
        Ready = (byte[])building.Clone(); Wraps = t.Wraps; fresh = true;
        LastMs = buildMs; buildMs = 0;
        row = -1; nextAt = now + RefreshMs;
    }

    /// <summary>The newest complete grid once, then null until the next one.</summary>
    public byte[] Take()
    {
        if (!fresh) return null;
        fresh = false;
        return Ready;
    }
}
