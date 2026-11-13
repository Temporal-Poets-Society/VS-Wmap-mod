using System;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Server;
using Wmap.Core;
using Wmap.Integrations;

/// <summary>
/// The wind layer against the real Sea mod: binds its model by reflection, checks the prevailing belts where Earth
/// puts the trades and westerlies, the seasonal shift between January and July, and the cost of a world grid.
/// </summary>
public static class WindChecks
{
    public static string Run(ICoreServerAPI api, SeaWinds winds, Topology t, Func<double, double, (double X, double Z)?> locate, out float[] january, out (float[] Field, double X, double Z, double Range) barometer)
    {
        january = null; barometer = default;
        if (winds == null) return "Winds: SKIP (Sea not installed)";
        for (int i = 0; i < 100 && !winds.Ready; i++) Thread.Sleep(50);
        if (!winds.Ready) throw new Exception("Sea is installed but its wind model never became ready");
        double now = api.World.Calendar.TotalHours;
        double jan = WindGrid.Hours(WindGrid.FirstMonth, now, winds.DaysPerYear, winds.HoursPerDay);
        double jul = WindGrid.Hours(WindGrid.FirstMonth + 6, now, winds.DaysPerYear, winds.HoursPerDay);
        string places = "";
        if (locate != null)
        {
            (double X, double Z) At(double lat, double lon, double hours)
            {
                var p = locate(lat, lon) ?? throw new Exception("No place for " + lat + ", " + lon);
                return winds.Prevailing(p.X, p.Z, hours);
            }
            string Say(string name, (double X, double Z) w) => $"{name} from {WindGrid.From(w.X, w.Z)} {Math.Sqrt(w.X * w.X + w.Z * w.Z):0.00}";
            var trades = At(15, -40, jan);
            if (!(trades.X < -0.1 && trades.Z > 0.02)) throw new Exception($"North Atlantic trades should blow from the NE in January: {trades}");
            var westerlies = At(48, -30, jan);
            if (!(westerlies.X > 0.15)) throw new Exception($"North Atlantic westerlies should blow from the west in January: {westerlies}");
            var southeast = At(-15, -20, jan);
            if (!(southeast.X < -0.1 && southeast.Z < -0.02)) throw new Exception($"South Atlantic trades should blow from the SE: {southeast}");
            // At 30°N the prevailing wind changes with the season. Before Sea 0.4.0 Sea's own belts moved by about 8 degrees;
            // since then the winds come from a provider (Earth Map 0.15), whose seasons are milder: any change is enough
            // to show that Wmap reads the month.
            var winter = At(30, -40, jan); var summer = At(30, -40, jul);
            double shift = Math.Sqrt((summer.X - winter.X) * (summer.X - winter.X) + (summer.Z - winter.Z) * (summer.Z - winter.Z));
            if (shift < 0.01) throw new Exception($"No seasonal change at 30°N: January {winter}, July {summer}");
            places = $"; {Say("15°N January", trades)}, {Say("48°N", westerlies)}, {Say("15°S", southeast)}, 30°N {Say("January", winter)} / {Say("July", summer)}";
        }
        var watch = Stopwatch.StartNew();
        var prevailing = winds.Sampler(false);
        january = WindGrid.Build((x, z) => prevailing(x, z, jan), t, CancellationToken.None);
        double prevailingMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var weather = winds.Sampler(true);
        var now_ = WindGrid.Build((x, z) => weather(x, z, now), t, CancellationToken.None);
        double weatherMs = watch.Elapsed.TotalMilliseconds;
        double strongest = 0;
        for (int i = 0; i < now_.Length; i += 2) strongest = Math.Max(strongest, Math.Sqrt(now_[i] * now_[i] + now_[i + 1] * now_[i + 1]));
        if (!(strongest > 0.05)) throw new Exception("The weather grid is calm everywhere");
        // A barometer's field in the North Atlantic: the weather now within 3000 blocks.
        var sea = locate?.Invoke(47, -20) ?? (t.StartX + t.Width / 2, t.Height / 2);
        watch.Restart();
        var field = WindGrid.BuildLocal((x, z) => weather(x, z, now), t, sea.X - 3000, sea.Z - 3000, 6000, CancellationToken.None);
        barometer = (field, sea.X, sea.Z, 3000);
        double localMs = watch.Elapsed.TotalMilliseconds;
        string belt = winds.BeltName(t.Height / 2, jan);
        return $"Winds: PASS (Sea {winds.Mode}; belt at the equator \"{belt}\"; {WindGrid.Width}x{WindGrid.Height} grid: prevailing {prevailingMs:0} ms, weather {weatherMs:0} ms, strongest weather {strongest:0.00}; barometer field {WindGrid.LocalSize}x{WindGrid.LocalSize} in {localMs:0} ms{places})";
    }
}
