using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Wmap.Core;

/// <summary>Line of sight over the real world chart (Earth): from a ship's deck in the middle of the Adriatic, the open
/// Adriatic and Italy's coast are seen, the Tyrrhenian Sea behind the Apennines is not (the reason for the feature:
/// sailing the Adriatic must not chart all of Italy).</summary>
public static class SightChecks
{
    public static string Run(BaseRaster raster, Topology t, Func<double, double, (double X, double Z)?> locate, int seaLevel)
    {
        if (raster == null || locate?.Invoke(43.0, 15.6) is not { } ship) return "Line of sight: not applicable (no world chart or geography)";
        var watch = Stopwatch.StartNew();
        var grid = HeightGrid.FromRaster(raster, t);
        double gridMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var seen = Viewshed.Cast(grid, ship.X, ship.Z, 3, 4096, 16);
        // Gapless: no exploration texel (32 blocks) of the open sea around the ship is left uncharted between charted ones.
        // (Sea behind the Croatian islands is hidden, but as whole patches, not single texels.)
        var cells = new HashSet<(int, int)>(seen.Select(s => ((int)Math.Floor((t.Canonical(s.X) - t.StartX) / 32), (int)Math.Floor(s.Z / 32))));
        int seaCells = 0, holes = 0;
        for (double dz = -1500; dz <= 1500; dz += 32)
        for (double dx = -1500; dx <= 1500; dx += 32)
        {
            double px = ship.X + dx, pz = ship.Z + dz;
            if (dx * dx + dz * dz > 1500 * 1500 || grid.At(px, pz) != 0) continue;
            // Only open sea well inside the water: the height grid's cell and its neighbours are all sea.
            bool open = true;
            for (int k = -1; k <= 1 && open; k++) for (int l = -1; l <= 1; l++) if (grid.At(px + k * grid.Texel, pz + l * grid.Texel) != 0) { open = false; break; }
            if (!open) continue;
            seaCells++;
            int cx = (int)Math.Floor((t.Canonical(px) - t.StartX) / 32), cz = (int)Math.Floor(pz / 32);
            if (!cells.Contains((cx, cz)) && cells.Contains((cx - 1, cz)) && cells.Contains((cx + 1, cz)) && cells.Contains((cx, cz - 1)) && cells.Contains((cx, cz + 1))) holes++;
        }
        if (seaCells < 100 || holes > 0) throw new Exception($"Line of sight: {holes} of {seaCells} open-sea texels around the ship are uncharted holes between charted texels");
        double castMs = watch.Elapsed.TotalMilliseconds;
        bool Seen(double lat, double lon)
        {
            var p = locate(lat, lon).Value;
            return seen.Any(s => Math.Abs(t.Delta(s.X, p.X)) < 60 && Math.Abs(s.Z - p.Z) < 60);
        }
        if (grid.At(ship.X, ship.Z) != 0) throw new Exception($"Line of sight: the check's ship at 43.0 N 15.6 E is not on the sea (height {grid.At(ship.X, ship.Z)})");
        var adriatic = new[] { (42.3, 16.6), (43.6, 14.6), (42.6, 17.6) };
        var coast = new[] { (42.45, 14.25), (43.6, 13.6) };          // Pescara, Ancona
        var tyrrhenian = new[] { (41.6, 12.4), (40.9, 13.6), (42.5, 11.3) };
        foreach (var (lat, lon) in adriatic) if (!Seen(lat, lon)) throw new Exception($"Line of sight: the open Adriatic at {lat} N {lon} E is not seen");
        int coastSeen = coast.Count(c => Seen(c.Item1, c.Item2));
        if (coastSeen == 0) throw new Exception("Line of sight: no part of Italy's Adriatic coast is seen");
        foreach (var (lat, lon) in tyrrhenian)
        {
            var p = locate(lat, lon).Value;
            double distance = Math.Sqrt(Math.Pow(t.Delta(ship.X, p.X), 2) + Math.Pow(p.Z - ship.Z, 2));
            if (distance > 4096) throw new Exception($"Line of sight: the Tyrrhenian point {lat} N {lon} E lies beyond the reach ({distance:0} blocks); the check proves nothing");
            if (Seen(lat, lon)) throw new Exception($"Line of sight: the Tyrrhenian Sea at {lat} N {lon} E is seen over the Apennines");
        }
        // The Tyrrhenian is hidden by the mountains, not missing: from above every summit it is seen.
        var above = Viewshed.Cast(grid, ship.X, ship.Z, 600, 4096);
        int fromAbove = tyrrhenian.Count(c => { var p = locate(c.Item1, c.Item2).Value; return above.Any(s => Math.Abs(t.Delta(s.X, p.X)) < 60 && Math.Abs(s.Z - p.Z) < 60); });
        if (fromAbove == 0) throw new Exception("Line of sight: the Tyrrhenian Sea is not seen even from 600 blocks up; its hiding proves nothing");
        return $"Line of sight: PASS ({fromAbove}/3 Tyrrhenian points seen from 600 blocks up; height grid {grid.Width}x{grid.Height} in {gridMs:0} ms; from the Adriatic at 3 blocks: {seen.Count} points in {castMs:0.0} ms; open Adriatic seen with no holes, {coastSeen}/2 Italian coast points seen, Tyrrhenian Sea hidden)";
    }
}
