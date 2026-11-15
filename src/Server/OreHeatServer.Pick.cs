using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;
using Wmap.Core;

namespace Wmap.Server;

// A cell's column and the prospecting pick's question at it (part of OreHeatServer, WM-0011, WM-0025).
public sealed partial class OreHeatServer
{
    // Where Earth keeps vanilla strata (WM-0011): Earth's regional rock at (x, z, depth below top, top), 0 for vanilla's,
    // and the top of its rock floor; and a prospecting workspace of our own for vanilla's simulated column (one at a time:
    // its strata and deposit generators keep state between calls).
    System.Func<double, double, int, int, int> earthRock;
    System.Func<int, int, int> earthFloor;
    ProPickWorkSpace pick;
    readonly object pickGate = new();
    int pickErrors;

    /// <summary>A cell's rock column as the pick makes it, made once for all its ores: null where its map chunk is not
    /// loaded (or the workspace failed).</summary>
    sealed class PickCell
    {
        public bool Tried;
        public int[] Column;
        public BlockPos Probe;
    }

    /// <summary>Earth's adjustment of the pick's column for one deposit (<c>EarthMap.RockFloorPass.RockFloorReading</c>, the
    /// prefix Earth puts on <see cref="DiscDepositGenerator.GetPropickReading"/>): its own rock in the layers the deposit may
    /// use, nothing below its rock floor. Null without Earth.</summary>
    delegate void ColumnAdjust(DiscDepositGenerator gen, BlockPos pos, ref int[] column);
    ColumnAdjust earthColumn;

    /// <summary>The height band the pick counts host blocks in, as the generator's <c>GetYMinMax</c> draws it (100 draws of its
    /// height distribution), but from a random of our own seeded from the world: 1 from the lowest draw × the surface to the
    /// surface (following the surface), 2 the lowest to the highest draw as they are (following the sea level, anywhere),
    /// 3 the surface less the deepest draw to the surface (below the surface), 0 the whole column (another generator). A
    /// child deposit has its parent's band.</summary>
    void Band(Ore ore, DiscDepositGenerator parent)
    {
        var gen = ore.Disc is ChildDepositGenerator && parent != null ? parent : ore.Disc;
        NatFloat height = null;
        switch (gen)
        {
            case FollowSurfaceDiscGenerator s: ore.BandKind = 1; height = s.YPosRel; break;
            case FollowSealevelDiscGenerator s: ore.BandKind = 2; height = s.YPosRel; break;
            case AnywhereDiscGenerator s: ore.BandKind = 2; height = s.YPosRel; break;
            case FollowSurfaceBelowDiscGenerator s: ore.BandKind = 3; height = s.Depth; break;
            default: ore.BandKind = 0; break;
        }
        if (height == null) { ore.BandKind = 0; return; }
        var random = new LCGRandom(api.World.Seed);
        random.InitPositionSeed(Code32(ore.Code), 0);
        double low = double.MaxValue, high = double.MinValue;
        for (int i = 0; i < 100; i++)
        {
            double v = height.nextFloat(1f, random);
            low = Math.Min(low, v); high = Math.Max(high, v);
        }
        if (ore.BandKind == 3) low = Math.Max(0, low);
        ore.BandMin = low; ore.BandMax = high;
    }

    /// <summary>A cell's column: its position, surface height (the loaded chunk's, else the terrain's prediction, else sea
    /// level) and, where generators read rock and Earth keeps vanilla strata there, vanilla's simulated rock column (null
    /// where Earth's own rock is all there is), or <c>Unknown</c> where that column is needed but its chunk is not loaded.</summary>
    (BlockPos Pos, int Top, int[] Rock, bool Unknown) Column(double x, double z, int sea, int mapSizeY, bool heights, System.Func<double, double, MapSample> sampler)
    {
        var pos = new BlockPos((int)Math.Floor(x), sea, (int)Math.Floor(z), 0);
        int top = heights ? api.World.BlockAccessor.GetTerrainMapheightAt(pos) : sea;
        if (top <= 0 && sampler != null) top = sampler(pos.X + 0.5, pos.Z + 0.5).Height;
        if (top <= 0) top = sea;
        top = Math.Clamp(top, 1, mapSizeY - 1);
        if (!heights) return (pos, top, null, false);
        var rock = VanillaColumn(pos, top, out bool unknown);
        return (pos, top, rock, unknown);
    }

    /// <summary>Earth's rock lookups by reflection (WM-D04), once the catalogue is built; none without Earth's geology.</summary>
    void EarthStrata(Ore[] ores)
    {
        earthRock = null; earthFloor = null;
        if (!ores.Any(o => o.Reading)) return;
        try
        {
            var geology = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthGeology");
            var regional = geology?.GetType().GetMethod("RegionalRock", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(double), typeof(double), typeof(int), typeof(int) }, null);
            if (regional == null || regional.ReturnType != typeof(int)) return;
            earthRock = (System.Func<double, double, int, int, int>)regional.CreateDelegate(typeof(System.Func<double, double, int, int, int>), geology);
            var floor = geology.GetType().Assembly.GetType("EarthMap.EarthWorldGen")?.GetMethod("RockFloorTop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
            if (floor != null && floor.ReturnType == typeof(int)) earthFloor = (System.Func<int, int, int>)floor.CreateDelegate(typeof(System.Func<int, int, int>));
            // Said, not silent (0.5.4, WM-0048): without it columns where Earth keeps vanilla strata are scanned from y 1.
            else api.Logger.Warning("[wmap] Ore heat maps: Earth's RockFloorTop(int, int) was not found; columns where Earth keeps vanilla strata are read from the bottom up");
        }
        catch (Exception e) { earthRock = null; earthFloor = null; api.Logger.Warning("[wmap] Ore heat maps: Earth's rock strata could not be read ({0}); columns where Earth keeps vanilla strata may read low", e.Message); }
    }

    /// <summary>Earth's adjustment of the pick's column (<see cref="ColumnAdjust"/>) by reflection (WM-D04), where vanilla's disc
    /// deposits are mapped; none without Earth.</summary>
    void EarthColumn(Ore[] ores)
    {
        earthColumn = null;
        if (!ores.Any(o => o.Disc != null)) return;
        try
        {
            var geology = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthGeology");
            if (geology == null) return;
            var method = geology.GetType().Assembly.GetType("EarthMap.RockFloorPass")?.GetMethod("RockFloorReading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(DiscDepositGenerator), typeof(BlockPos), typeof(int[]).MakeByRefType() }, null);
            if (method == null || method.ReturnType != typeof(void))
            {
                api.Logger.Warning("[wmap] Ore heat maps: Earth's prospecting column was not found; vanilla's deposits are mapped on vanilla's simulated rock");
                return;
            }
            earthColumn = (ColumnAdjust)method.CreateDelegate(typeof(ColumnAdjust));
        }
        catch (Exception e) { earthColumn = null; api.Logger.Warning("[wmap] Ore heat maps: Earth's prospecting column could not be read ({0})", e.Message); }
    }

    /// <summary>The pick's rock column at a cell, from our own workspace (one at a time), and the surface height the pick reads
    /// there, where the cell's map chunk is loaded; else none.</summary>
    void MakePickColumn(BlockPos pos, PickCell cell)
    {
        cell.Tried = true;
        if (api.WorldManager.GetMapChunk(pos.X / OreHeat.ChunkSize, pos.Z / OreHeat.ChunkSize) == null) return;
        try
        {
            int top = api.World.BlockAccessor.GetTerrainMapheightAt(pos);
            if (top <= 0) return;
            int[] column;
            lock (pickGate)
            {
                if (disposed) return;
                column = Workspace().GetRockColumn(pos.X, pos.Z);
            }
            cell.Probe = new BlockPos(pos.X, top, pos.Z, 0);
            cell.Column = column;
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref pickErrors) == 1) api.Logger.Warning("[wmap] Ore heat maps: the pick's rock column could not be made: {0}", e.Message);
            cell.Column = null;
        }
    }

    /// <summary>Vanilla's simulated rock column (as the pick makes it) where Earth keeps vanilla strata somewhere above its
    /// rock floor in this column, else null. <paramref name="unknown"/> where that column is needed but cannot be made:
    /// its map chunk is not loaded, or the workspace failed.</summary>
    int[] VanillaColumn(BlockPos pos, int top, out bool unknown)
    {
        unknown = false;
        var rock = earthRock;
        if (rock == null) return null;
        bool vanilla = false;
        try
        {
            int floor = earthFloor?.Invoke(pos.X, pos.Z) ?? 0;
            for (int y = Math.Max(1, floor + 1); y <= top && !vanilla; y++) vanilla = rock(pos.X, pos.Z, top - y, top) <= 0;
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref pickErrors) == 1) api.Logger.Warning("[wmap] Ore heat maps: Earth's rock lookup failed: {0}", e.Message);
            return null;
        }
        if (!vanilla) return null;
        if (api.WorldManager.GetMapChunk(pos.X / OreHeat.ChunkSize, pos.Z / OreHeat.ChunkSize) == null) { unknown = true; return null; }
        try
        {
            lock (pickGate)
            {
                if (disposed) { unknown = true; return null; }
                return Workspace().GetRockColumn(pos.X, pos.Z);
            }
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref pickErrors) == 1) api.Logger.Warning("[wmap] Ore heat maps: vanilla's rock column could not be made: {0}", e.Message);
            unknown = true;
            return null;
        }
    }

    /// <summary>Our own prospecting workspace, made on first use; call under <see cref="pickGate"/> (its strata and deposit
    /// generators keep state between calls).</summary>
    ProPickWorkSpace Workspace()
    {
        if (pick == null) { pick = new ProPickWorkSpace(); pick.OnLoaded(api); }
        return pick;
    }

    /// <summary>One ore at the player's column for <c>/wmapore</c>: this map's answer worked out there now, beside the
    /// prospecting pick's own reading with its own workspace (main thread). Null where the ore is unknown.</summary>
    public string Compare(string code, BlockPos at)
    {
        Catalog();
        if (code == null || !byCode.TryGetValue(code, out int index)) return null;
        var ore = ores[index];
        int sea = api.World.SeaLevel, mapSizeY = api.WorldManager.MapSizeY, regionSize = api.WorldManager.RegionSize;
        var (pos, top, rock, unknown) = Column(at.X + 0.5, at.Z + 0.5, sea, mapSizeY, ore.Reading, samplers?.Invoke());
        PickCell picked0 = null;
        string map = Cell(ore, pos, top, rock, unknown, regionSize, ref picked0, out double blocks, out double factor)
            ? $"map {blocks:0.#} blocks/chunk, factor {factor:0.000}" : "map: not known here";
        string column = !ore.Reading ? (picked0?.Column == null ? "the pick's column, chunk not loaded" : earthColumn != null ? "the pick's column, Earth's rock" : "the pick's column")
            : unknown ? "vanilla strata, chunk not loaded" : rock != null ? "vanilla strata, simulated column" : "Earth's rock";
        string picked;
        try
        {
            var workspace = api.ObjectCache.TryGetValue("propickworkspace", out object o) ? o as ProPickWorkSpace : null;
            int[] pickColumn = workspace?.GetRockColumn(pos.X, pos.Z);
            var region = api.World.BlockAccessor.GetMapRegion(pos.X / regionSize, pos.Z / regionSize);
            int dist = 0;
            if (region?.OreMaps != null && region.OreMaps.TryGetValue(code, out var oreMap))
                dist = oreMap.GetUnpaddedColorLerped((float)(pos.X % regionSize) / regionSize * oreMap.InnerSize, (float)(pos.Z % regionSize) / regionSize * oreMap.InnerSize);
            var probe = pos.Copy();
            probe.Y = api.World.BlockAccessor.GetTerrainMapheightAt(probe);
            ore.Variant.GetPropickReading(probe, dist, pickColumn, out double ppt, out double total);
            picked = workspace == null ? "pick: no workspace" : $"pick {ppt:0.###} ppt = {ppt / 1000 * probe.Y * 1024:0.#} blocks/chunk, factor {total:0.000}";
        }
        catch (Exception e) { picked = "pick failed: " + e.Message; }
        return $"{code} at x={pos.X} z={pos.Z} (top {top}, {column}): {map}; {picked}";
    }

    /// <summary>One ore at one column: expected ore blocks in its chunk column and the pick's factor; false when unknown.
    /// <paramref name="rock"/>: vanilla's simulated column where Earth keeps vanilla strata (null where it does not);
    /// <paramref name="unknown"/>: that column was needed but could not be made.</summary>
    bool Cell(Ore ore, BlockPos pos, int top, int[] rock, bool unknown, int regionSize, ref PickCell picked, out double blocks, out double factor)
    {
        blocks = factor = 0;
        if (ore.Reading)
        {
            if (unknown) return false;
            ore.Variant.GetPropickReading(pos, 0, rock, out double ppt, out double total);
            if (!double.IsFinite(ppt) || !double.IsFinite(total)) return false;
            blocks = Math.Max(0, ppt) / 1000 * top * 1024;
            factor = Math.Max(0, total);
            return true;
        }
        // Vanilla's disc deposits: the pick's question (WM-0025), where the column's map chunk is loaded.
        var region = api.WorldManager.GetMapRegion(pos.X / regionSize, pos.Z / regionSize);
        if (region == null) return false;
        picked ??= new PickCell();
        if (!picked.Tried) MakePickColumn(pos, picked);
        if (picked.Column == null) return false;
        double dist = 0;
        if (region.OreMaps != null && region.OreMaps.TryGetValue(ore.Code, out var oreMap))
            dist = (oreMap.GetUnpaddedColorLerped((float)(pos.X % regionSize) / regionSize * oreMap.InnerSize, (float)(pos.Z % regionSize) / regionSize * oreMap.InnerSize) & 0xFF) / 255.0;
        double share = 0;
        if (dist > 0)
        {
            var column = picked.Column;
            if (earthColumn != null)
            {
                try { earthColumn(ore.Disc, picked.Probe, ref column); }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref pickErrors) == 1) api.Logger.Warning("[wmap] Ore heat maps: Earth's prospecting column failed: {0}", e.Message);
                    return false;
                }
            }
            share = OreHeat.HostShare(column, ore.Hosts, ore.BandKind, ore.BandMin, ore.BandMax, picked.Probe.Y);
        }
        factor = dist * share;
        blocks = factor * ore.BlocksAtFull;
        return true;
    }
}
