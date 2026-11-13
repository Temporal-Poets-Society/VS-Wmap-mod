using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Wmap.Core;

/// <summary>
/// The day and night overlay against the real calendar. With AstraTerra (earth profile) the sun follows longitude through
/// Earth Map's LongitudeAt: the binding must be logged, and at an equinox and both solstices the sampled sun elevations
/// must show day and night at once, local noon where the calendar's hour puts it, polar day and night, a straight boundary
/// at the equinox, a curved one at the solstices, and twilight bands about 6 degrees of longitude wide. Without AstraTerra
/// the whole world shares one time of day: each row of the grid is one elevation (latitude bands only).
/// Runs on the main thread: it moves the calendar forward (equinox, June, December).
/// </summary>
public static class SunChecks
{
    public sealed record Map(string Name, byte[] Grid);
    const int W = DayGrid.Width, H = DayGrid.Height;
    static readonly (string Name, double YearRel)[] Dates = { ("equinox", 0.2226), ("june", 0.4726), ("december", 0.9726) };

    public static string Run(ICoreServerAPI api, Topology t, object earth, Func<double, double, GeoPosition> geography, List<Map> maps)
    {
        var calendar = api.World.Calendar ?? throw new Exception("Real-calendar day and night: no calendar");
        // the suite's AstraTerra fork packs as astraearth (0.15.0-dev.25 on); upstream AstraTerra is astraterra
        bool astra = api.ModLoader.IsModEnabled("astraterra") || api.ModLoader.IsModEnabled("astraearth");
        string binding = astra ? CheckBinding(api) : null;
        double ColX(int c) => t.StartX + (c + 0.5) * t.Width / W;
        double RowZ(int r) => (r + 0.5) * t.Height / H;
        var longitudeAt = earth?.GetType().GetMethod("LongitudeAt", new[] { typeof(double) });
        double?[] lon = Enumerable.Range(0, W).Select(c => longitudeAt?.Invoke(earth, new object[] { ColX(c) }) as double?).ToArray();
        // A row's latitude, read away from the seam (the map's middle column).
        double?[] lat = Enumerable.Range(0, H).Select(r =>
        {
            if (geography == null) return (double?)null;
            var g = geography(t.StartX + t.Width / 2, RowZ(r));
            return g.BeyondPole || g.InGutter ? null : g.Latitude;
        }).ToArray();
        var lines = new List<string>();
        foreach (var (name, target) in Dates)
        {
            double ahead = target - calendar.YearRel;
            if (ahead < 0) ahead += 1;
            calendar.Add((float)(ahead * calendar.DaysPerYear * calendar.HoursPerDay));
            if (Math.Abs(calendar.YearRel - target) > 0.01) throw new Exception($"Real-calendar day and night: the calendar went to year {calendar.YearRel:0.0000}, not {target}");
            double days = calendar.TotalDays;
            var grid = new byte[W * H];
            // The grid keeps -32..+31.75 degrees (enough for day, twilight and night); the checks on noon and on the range
            // use the elevations before that clamp.
            var raw = new double[W * H];
            int sampled = 0;
            DayGrid.BuildRows(t, (x, z) =>
            {
                double e = Math.Asin(Math.Clamp(calendar.GetSunPosition(new Vec3d(x, 0, z), days).Y, -1, 1)) * 180 / Math.PI;
                raw[sampled++] = e;
                return e;
            }, grid, 0, H);
            maps.Add(new Map("world-daynight-" + name, grid));
            double E(int r, int c) => DayGrid.Decode(grid[r * W + c]);
            double Raw(int r, int c) => raw[r * W + c];
            double low = raw.Min(), high = raw.Max();
            string head = $"{name} (year {calendar.YearRel:0.0000}, hour {calendar.HourOfDay:0.00}): sun {low:0.0}..{high:0.0} degrees";
            if (!astra)
            {
                // One time of day: every row is one elevation; rows differ by latitude.
                double worst = Enumerable.Range(0, H).Max(r => Enumerable.Range(0, W).Max(c => E(r, c)) - Enumerable.Range(0, W).Min(c => E(r, c)));
                int distinct = Enumerable.Range(0, H).Select(r => grid[r * W]).Distinct().Count();
                if (worst > 0.5) throw new Exception($"Real-calendar day and night ({name}): without AstraTerra a row varies by {worst:0.00} degrees across the world");
                if (distinct < 2) throw new Exception($"Real-calendar day and night ({name}): no latitude bands (one elevation everywhere)");
                lines.Add($"{head}; latitude bands only ({distinct} distinct rows, at most {worst:0.00} degrees across a row)");
                continue;
            }
            if (lon.All(l => l == null) || lat.All(l => l == null)) throw new Exception("Real-calendar day and night: AstraTerra is loaded but there is no Earth longitude or latitude to check against");
            int Row(double latitude) => Enumerable.Range(0, H).Where(r => lat[r] != null).OrderBy(r => Math.Abs(lat[r].Value - latitude)).First();
            int eq = Row(0);
            double eqRange = Enumerable.Range(0, W).Max(c => Raw(eq, c)) - Enumerable.Range(0, W).Min(c => Raw(eq, c));
            if (eqRange < 60) throw new Exception($"Real-calendar day and night ({name}): the equator row spans only {eqRange:0.0} degrees of sun elevation (no day and night at once)");
            // Local noon: AstraTerra shifts each place's day by its longitude / 360, so noon lies where (0.5 - dayRel) * 360.
            double dayRel = calendar.HourOfDay / calendar.HoursPerDay, noonLon = Wrap(( 0.5 - dayRel) * 360);
            int noonCol = Enumerable.Range(0, W).Where(c => lon[c] != null).OrderBy(c => Math.Abs(Wrap(lon[c].Value - noonLon))).First();
            int hotCol = Enumerable.Range(0, W).OrderByDescending(c => Raw(eq, c)).First();
            int colGap = Math.Min(Math.Abs(hotCol - noonCol), W - Math.Abs(hotCol - noonCol));
            if (colGap > 1)
            {
                // Where the sun stands along the equator, every 16 columns, for the report.
                string profile = string.Join(", ", Enumerable.Range(0, W / 16).Select(k => $"{(lon[k * 16] is { } l ? l.ToString("0") : "gutter")}:{Raw(eq, k * 16):0}"));
                int mirror = Enumerable.Range(0, W).Where(c => lon[c] != null).OrderBy(c => Math.Abs(Wrap(lon[c].Value + noonLon))).First();
                throw new Exception($"Real-calendar day and night ({name}): the highest sun on the equator is at column {hotCol} ({lon[hotCol]:0.0}, {Raw(eq, hotCol):0.0} degrees), local noon at column {noonCol} ({noonLon:0.0}, {Raw(eq, noonCol):0.0} degrees); " +
                    $"at the mirrored longitude {-noonLon:0.0} (column {mirror}) {Raw(eq, mirror):0.0} degrees; dayRel {dayRel:0.0000}. Equator (longitude:elevation): {profile}");
            }
            // The evening terminator: walking east from noon, where the elevation first falls below a level.
            double? Cross(int r, double level)
            {
                for (int k = 1; k < W; k++)
                {
                    int a = (noonCol + k - 1) % W, b = (noonCol + k) % W;
                    double ea = E(r, a), eb = E(r, b);
                    if (ea >= level && eb < level)
                    {
                        if (lon[a] == null || lon[b] == null) return null;
                        double f = (ea - level) / (ea - eb);
                        return Wrap(lon[a].Value + f * Wrap(lon[b].Value - lon[a].Value));
                    }
                }
                return null;
            }
            double Need(double? v, string what) => v ?? throw new Exception($"Real-calendar day and night ({name}): no {what}");
            double c0 = Need(Cross(eq, 0), "sunset line on the equator");
            // Polar day and polar night beyond 68 degrees (the polar circles lie at 66.56).
            if (name != "equinox")
            {
                bool north = name == "june";
                int wrong = 0; string first = null;
                for (int r = 0; r < H; r++)
                {
                    if (lat[r] is not { } l || Math.Abs(l) < 68) continue;
                    bool day = (l > 0) == north;
                    for (int c = 0; c < W; c++)
                        if (day ? E(r, c) <= 0 : E(r, c) >= 0) { wrong++; first ??= $"lat {l:0.0} lon {lon[c]:0.0}: {E(r, c):0.0} degrees"; }
                }
                if (wrong > 0) throw new Exception($"Real-calendar day and night ({name}): {wrong} cells beyond 68 degrees break polar {(north ? "day in the north" : "night in the north")} (first: {first})");
                // A curved boundary: the sunset line shifts with latitude, opposite ways north and south.
                double n60 = Need(Cross(Row(60), 0), "sunset line at 60 N") - c0, s60 = Need(Cross(Row(-60), 0), "sunset line at 60 S") - c0;
                if (Math.Abs(Wrap(n60)) < 20 || Math.Abs(Wrap(s60)) < 20 || Math.Sign(Wrap(n60)) == Math.Sign(Wrap(s60)))
                    throw new Exception($"Real-calendar day and night ({name}): the sunset line is not curved (60 N {Wrap(n60):+0.0;-0.0}, 60 S {Wrap(s60):+0.0;-0.0} degrees from the equator's)");
            }
            else
            {
                // A straight boundary: the sunset line at the same longitude from 60 S to 60 N.
                var crossings = Enumerable.Range(0, H).Where(r => lat[r] is { } l && Math.Abs(l) <= 60).Select(r => Cross(r, 0)).Where(v => v != null).Select(v => Wrap(v.Value - c0)).ToList();
                double spread = crossings.Max() - crossings.Min();
                if (crossings.Count < H / 3 || spread > 3) throw new Exception($"Real-calendar day and night (equinox): the sunset line spreads over {spread:0.0} degrees of longitude between 60 S and 60 N ({crossings.Count} rows)");
            }
            // Twilight bands on the equator: civil, nautical and astronomical, each about 6 degrees of longitude.
            double t6 = Need(Cross(eq, -6), "civil twilight line"), t12 = Need(Cross(eq, -12), "nautical twilight line"), t18 = Need(Cross(eq, -18), "astronomical twilight line");
            double[] widths = { Wrap(t6 - c0), Wrap(t12 - t6), Wrap(t18 - t12) };
            if (widths.Any(w => w < 4.5 || w > 8.5)) throw new Exception($"Real-calendar day and night ({name}): twilight bands {string.Join(", ", widths.Select(w => w.ToString("0.0")))} degrees of longitude wide on the equator");
            string At(double l) => Cross(Row(l), 0) is { } v ? v.ToString("0.0") : "none";
            lines.Add($"{head}; noon at {noonLon:0.0} (highest sun {Raw(eq, hotCol):0.0} degrees in column {hotCol}, noon column {noonCol}); sunset line at 0: {c0:0.0}, 45N {At(45)}, 45S {At(-45)}, 60N {At(60)}, 60S {At(-60)}; " +
                $"twilight bands {widths[0]:0.0}/{widths[1]:0.0}/{widths[2]:0.0} degrees wide");
        }
        return "Real-calendar day and night: PASS (" + (binding ?? "no AstraTerra: one time of day for the whole world") + ")\n  " + string.Join("\n  ", lines);
    }

    static double Wrap(double degrees) => ((degrees % 360) + 540) % 360 - 180;

    /// <summary>Earth Map's and AstraTerra's log lines saying the sun follows Earth's meridians.</summary>
    static string CheckBinding(ICoreServerAPI api)
    {
        string logs = Path.Combine(api.DataBasePath, "Logs");
        var text = new List<string>();
        foreach (string file in new[] { "server-main.log", "server-debug.log" })
        {
            string path = Path.Combine(logs, file);
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.AddRange(reader.ReadToEnd().Split('\n'));
        }
        const string earthLine = "Earth Map longitude: AstraTerra's sky follows Earth's meridians", astraLine = "longitude-aware sun installed after survival solar setup";
        bool earthOk = text.Any(l => l.Contains(earthLine)), astraOk = text.Any(l => l.Contains(astraLine));
        if (!earthOk || !astraOk)
        {
            var relevant = text.Where(l => l.Contains("Earth Map longitude") || l.Contains("AstraTerra")).Select(l => l.Trim()).Distinct().Take(12);
            throw new Exception($"Real-calendar day and night: the longitude binding was not logged ({(earthOk ? "" : "Earth's line missing; ")}{(astraOk ? "" : "AstraTerra's line missing; ")}relevant lines: {string.Join(" | ", relevant)})");
        }
        return "AstraTerra's sun follows Earth's meridians, both lines logged";
    }
}
