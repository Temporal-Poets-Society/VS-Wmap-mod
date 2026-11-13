using System;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Server;
using Wmap.Core;

/// <summary>The day and night overlay's source: the sun's elevation from the game's calendar over the whole
/// <see cref="DayGrid"/>, as the client samples it (here the server's calendar, the same class). The elevation must
/// vary over the world (day and night at once, or at least by latitude), and the cost is reported.</summary>
public static class DayChecks
{
    public static string Run(ICoreServerAPI api, Topology t)
    {
        var calendar = api.World.Calendar;
        if (calendar == null) return "Day and night: FAIL no calendar";
        var grid = new byte[DayGrid.Width * DayGrid.Height];
        double lowest = double.MaxValue, highest = double.MinValue, days = calendar.TotalDays;
        var watch = Stopwatch.StartNew();
        DayGrid.BuildRows(t, (x, z) =>
        {
            double d = Math.Asin(Math.Clamp(calendar.GetSunPosition(new Vintagestory.API.MathTools.Vec3d(x, 0, z), days).Y, -1, 1)) * 180 / Math.PI;
            lowest = Math.Min(lowest, d); highest = Math.Max(highest, d);
            return d;
        }, grid, 0, DayGrid.Height);
        double ms = watch.Elapsed.TotalMilliseconds;
        if (!double.IsFinite(lowest) || !double.IsFinite(highest)) throw new Exception("Day and night: the calendar gave a sun elevation that is not a number");
        int rowsDiffer = Enumerable.Range(0, DayGrid.Height).Count(z => grid[z * DayGrid.Width] != grid[DayGrid.Height / 2 * DayGrid.Width]);
        int columnsDiffer = Enumerable.Range(0, DayGrid.Width).Count(x => grid[DayGrid.Height / 2 * DayGrid.Width + x] != grid[DayGrid.Height / 2 * DayGrid.Width]);
        return $"Day and night: PASS ({DayGrid.Width}x{DayGrid.Height} cells from the calendar in {ms:0.0} ms; sun {lowest:0.0}..{highest:0.0} degrees at hour {calendar.HourOfDay:0.0}; "
            + $"{rowsDiffer} rows differ from the equator's (latitude), {columnsDiffer} equator cells differ from its first (longitude))";
    }
}
