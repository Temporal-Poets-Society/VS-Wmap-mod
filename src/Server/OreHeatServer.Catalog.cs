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

// The ore catalogue: which ores there are heat maps of, and how each is asked (part of OreHeatServer).
public sealed partial class OreHeatServer
{
    sealed class Ore
    {
        public string Code;
        public DepositVariant Variant;
        /// <summary>The generator answers the pick's question without a block column (Earth Map's).</summary>
        public bool Reading;
        /// <summary>Vanilla: expected ore blocks per chunk where the ore map is full.</summary>
        public double BlocksAtFull;
        /// <summary>Vanilla's disc deposit, asked as the pick asks it (WM-0025): its generator, its host blocks (null: any
        /// block hosts it) and its height band (<see cref="Band"/>).</summary>
        public DiscDepositGenerator Disc;
        public HashSet<int> Hosts;
        public int BandKind;
        public double BandMin, BandMax;
    }

    static int Code32(string code)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (char c in code) h = (h ^ c) * 16777619;
            return h & 0x7fffffff;
        }
    }

    /// <summary>The ore codes there are heat maps of, in the order the pick knows them; empty without deposits. Built on
    /// first use (main thread), after the world's deposit generators are set up.</summary>
    public string[] Codes { get { Catalog(); return codes; } }
    /// <summary><see cref="OreHeatRequest.Reading"/> when every ore is answered by its generator, <see cref="OreHeatRequest.OreMap"/>
    /// when by vanilla's ore maps, "mixed" for both; null without ores.</summary>
    public string Method { get { Catalog(); return method; } }
    string[] codes;
    string method;
    readonly object catalogGate = new();

    /// <summary>Builds the ore catalogue once, on whichever thread asks first (the main thread or the worker), and publishes
    /// it whole: everything else is set before <see cref="ores"/> (0.5.3, WM-0037: a second thread could see the ores while
    /// their index and fingerprint were still unset).</summary>
    void Catalog()
    {
        if (Volatile.Read(ref ores) != null) return;
        lock (catalogGate) { if (ores == null) BuildCatalog(); }
    }

    void BuildCatalog()
    {
        var list = new List<Ore>();
        try
        {
            var deposits = api.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
            if (deposits != null)
            {
                // As the pick: the last variant with an ore map per code, parents before their children.
                var variants = new Dictionary<string, DepositVariant>();
                var parents = new Dictionary<string, DepositVariant>();
                var order = new List<string>();
                void Add(DepositVariant v, DepositVariant parent)
                {
                    if (v?.Code == null || !v.WithOreMap) return;
                    if (!variants.ContainsKey(v.Code)) order.Add(v.Code);
                    variants[v.Code] = v;
                    if (parent != null) parents[v.Code] = parent; else parents.Remove(v.Code);
                }
                foreach (var v in deposits)
                {
                    Add(v, null);
                    if (v?.ChildDeposits != null) foreach (var child in v.ChildDeposits) Add(child, v);
                }
                double rate = DepositRate(), heightScale = api.WorldManager.MapSizeY / 256.0;
                foreach (string code in order)
                {
                    var v = variants[code];
                    var ore = new Ore { Code = code, Variant = v };
                    if (v.GeneratorInst?.GetType().FullName == EarthGenerator) ore.Reading = true;
                    else if (v.GeneratorInst is DiscDepositGenerator disc)
                    {
                        ore.BlocksAtFull = disc.GetAbsAvgQuantity(new LCGRandom(api.World.Seed)) * rate * (v.ScaleWithWorldheight ? heightScale : 1);
                        ore.Disc = disc;
                        var bearing = disc.GetBearingBlocks();
                        ore.Hosts = bearing == null ? null : new HashSet<int>(bearing);
                        Band(ore, parents.TryGetValue(code, out var parent) ? parent.GeneratorInst as DiscDepositGenerator : null);
                    }
                    else continue;
                    list.Add(ore);
                    if (list.Count == OreHeat.MaxOres) break;
                }
            }
        }
        catch (Exception e) { api.Logger.Warning("[wmap] Ore heat maps unavailable: {0}", e.Message); list.Clear(); }
        var built = list.ToArray();
        var index = new Dictionary<string, int>();
        for (int i = 0; i < built.Length; i++) index[built[i].Code] = i;
        // What a stored tile depends on beyond the cache folder (seed and terrain): the ores, their generators and amounts, the
        // world's height, the tiles' layout, and every mod (0.5.5, WM-0055: the folder no longer changes with the mod list, and
        // any mod may add or patch deposits and their host rocks).
        string catalogue = string.Join(";", built.Select(o => o.Code + "=" + (o.Reading ? "reading" : o.BlocksAtFull.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            + ":" + o.Variant.GeneratorInst?.GetType().FullName + "@" + o.Variant.GeneratorInst?.GetType().Module.ModuleVersionId));
        EarthStrata(built);
        EarthColumn(built);
        int reading = built.Count(o => o.Reading);
        method = built.Length == 0 ? null : reading == built.Length ? OreHeatRequest.Reading : reading == 0 ? OreHeatRequest.OreMap : "mixed";
        codes = built.Select(o => o.Code).ToArray();
        byCode = index;
        fingerprint = TileCodec.Hash($"wmap-oreheat-v4|{catalogue}|{api.WorldManager.MapSizeY}|{api.World.Seed}|{OreHeat.TileCells}|{typeof(OreHeatServer).Module.ModuleVersionId}|{Wmap.Integrations.WorldSource.ModList(api)}")[..16];
        Volatile.Write(ref ores, built);
        if (built.Length > 0) api.Logger.Notification("[wmap] Ore heat maps: {0} ores ({1})", built.Length, method);
    }

    /// <summary>Vanilla multiplies every deposit's tries by deposits.json's chanceMultiplier, which the world's
    /// globalDepositSpawnRate patches.</summary>
    double DepositRate()
    {
        try { return api.Assets.TryGet(new AssetLocation("game:worldgen/deposits.json"))?.ToObject<Deposits>()?.ChanceMultiplier ?? 1; }
        catch (Exception) { return 1; }
    }
}
