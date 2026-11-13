using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Server;
using Wmap.Core;
using Wmap.Integrations;

/// <summary>The seam band predicted by CylinderSurface against the installed Cylinder's own SeamGround, for the
/// world's layout and the same params as layout v9, over natural land and deep sea, out past the v10 ramp.</summary>
public static class SeamBandChecks
{
    public static string Run(ICoreServerAPI api)
    {
        object system = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "CylinderWorld.CylinderWorldCore");
        object world = WorldSource.Member(system, "Params");
        if (world == null) return "Seam band: not applicable";
        var ground = world.GetType().GetMethod("SeamGround", new[] { typeof(double), typeof(double), typeof(int) });
        if (ground == null) return "Seam band: not applicable (Cylinder World before 0.11.5 has no SeamGround)";
        int sea = api.World.SeaLevel, worldVersion = Convert.ToInt32(WorldSource.Member(world, "Version"));
        var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        string result = "";
        // Cylinder 0.11.6: new Earth-mode worlds keep Earth's real seabed in the band (SeamGround returns the natural
        // ground). The shaped band (v10 relief, v9 flat) is still checked on copies without it.
        var earthSeabedField = world.GetType().GetField("EarthSeabed");
        bool worldEarthSeabed = earthSeabedField?.GetValue(world) is true;
        var layouts = new[] { (worldVersion, worldEarthSeabed), (worldVersion, false), (9, false) }.Distinct();
        foreach (var (version, earthSeabed) in layouts)
        {
            object p = clone.Invoke(world, null);
            p.GetType().GetField("Version").SetValue(p, version);
            earthSeabedField?.SetValue(p, earthSeabed);
            double N(string name) => Convert.ToDouble(WorldSource.Member(p, name));
            bool Owned(double x, double z) => (bool)p.GetType().GetMethod("PolarOwnsSeabed").Invoke(p, new object[] { x, z });
            int Ground(double x, double z, int natural) => (int)ground.Invoke(p, new object[] { x, z, natural });
            double width = N("WorldWidth"), half = N("SeamHalfWidth"), reach = N("SeamWorkReach"), length = N("WorldLength");
            if (version >= 10 && reach <= half) throw new Exception($"Layout {version}: SeamWorkReach {reach} does not pass the band's half-width {half}");
            if (version < 10 && reach != half) throw new Exception($"Layout {version}: SeamWorkReach {reach} != half-width {half}");
            int columns = 0, changed = 0, pastBand = 0, owned = 0;
            var floors = new System.Collections.Generic.HashSet<int>();
            // Land well above the band's seabed lengthens the v10 ramp; deep sea lowers into the band from below.
            foreach (int natural in new[] { sea + 30, sea - 70 })
            {
                var surface = new CylinderSurface(p, api.WorldManager.MapSizeY, sea).Bind((_, _) =>
                    natural >= sea ? new MapSample(natural, 0, TileData.Land) : new MapSample(natural, sea - 1 - natural, TileData.Water));
                foreach (double z in new[] { length / 2 - 3001.3, length / 2 + 17.6, length / 2 + 4410.2 })
                for (double d = 0.25; d < reach + 96; d += 5.5)
                foreach (double x in new[] { d, width - d })
                {
                    double wx = Math.Floor(x) + .5, wz = Math.Floor(z) + .5;
                    if (Owned(wx, wz)) { owned++; continue; }
                    var sample = surface(x, z);
                    int expected = Math.Min(wx, width - wx) < reach ? Ground(wx, wz, natural) : natural;
                    bool water = expected < sea - 1 || expected == natural && natural < sea;
                    if (sample.Height != expected || sample.Kind != (water ? TileData.Water : TileData.Land) || (water ? sample.Height + sample.Depth != sea - 1 : sample.Depth != 0))
                        throw new Exception($"Layout {version}: x={x} z={z} natural {natural}: predicted {sample.Height}+{sample.Depth} kind {sample.Kind}, Cylinder SeamGround {expected}");
                    if (version < 10 && Math.Min(wx, width - wx) < half - 160 && Math.Abs(Math.Min(wx, width - wx) - N("JumpInset")) > 2)
                        floors.Add(expected);
                    else if (version >= 10 && Math.Min(wx, width - wx) < N("JumpInset") + N("IdenticalReach")) floors.Add(expected);
                    columns++;
                    if (expected != natural) { changed++; if (Math.Min(wx, width - wx) >= half) pastBand++; }
                }
            }
            if (earthSeabed)
            {
                if (columns == 0 || changed > 0) throw new Exception($"Layout {version} with Earth's seabed: {columns} columns compared, {changed} shaped (expected none)");
                result += $"{(result == "" ? "" : "; ")}layout {version} (world) keeps Earth's seabed: {columns} columns follow the natural ground";
                continue;
            }
            if (columns == 0 || changed == 0) throw new Exception($"Layout {version}: no seam columns compared");
            if (version >= 10 && pastBand == 0) throw new Exception($"Layout {version}: no ramp columns past the band's half-width");
            if (version < 10 && pastBand > 0) throw new Exception($"Layout {version}: {pastBand} columns changed past the band");
            if (version < 10 && (floors.Count != 1 || floors.Single() != (int)N("SeabedY"))) throw new Exception($"Layout {version}: flat band seabed {string.Join(",", floors)} != SeabedY {N("SeabedY")}");
            if (version >= 10 && floors.Count < 3) throw new Exception($"Layout {version}: band seabed shows no relief ({string.Join(",", floors)})");
            result += $"{(result == "" ? "" : "; ")}layout {version}{(version == worldVersion && !worldEarthSeabed ? " (world)" : "")}: {columns} columns, {changed} shaped, {pastBand} past H = {half}, reach {reach}, identical seabed {floors.Min()}..{floors.Max()}";
            if (owned > 0) result += $", {owned} polar-owned skipped";
        }
        return "Seam band against Cylinder SeamGround: PASS (" + result + ")";
    }
}
