using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Wmap.Core;

namespace Wmap.Integrations;

/// <summary>Optional adapters validated once at world start, without compile-time mod dependencies.</summary>
public sealed class WorldSource
{
    public AtlasInfo Info;
    /// <summary>Creates a terrain sampler for one worker thread, or null without a prediction provider.</summary>
    public global::System.Func<global::System.Func<double, double, MapSample>> Samplers;
    /// <summary>A sampler for single-threaded callers.</summary>
    public global::System.Func<double, double, MapSample> Sample;
    /// <summary>Forest cover and the climate the game tints by, from Earth Map; null without it. Thread-safe.</summary>
    public global::System.Func<double, double, BiomeSample> Biome;
    /// <summary><see cref="Biome"/> on Earth's built-in heights, for maps of the whole world (the climate raster); null where
    /// Earth has no such switch (<see cref="EarthHeights"/>). Thread-safe.</summary>
    public global::System.Func<double, double, BiomeSample> WorldBiome;
    /// <summary>River centrelines from Earth Map, or null.</summary>
    public IReadOnlyList<RiverSegment> Rivers;
    public static object Member(object o, string name) => o == null ? null : o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);
    static object Private(object o, string name) => o?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(o);
    static double Num(object o, string n) => Convert.ToDouble(Member(o, n), CultureInfo.InvariantCulture);
    static bool Bool(object o, string n) => Member(o, n) is true;
    static object FindSystem(ICoreAPI api, string name) => api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == name);

    public static WorldSource Create(ICoreServerAPI api, WmapConfig config)
    {
        var wm = api.WorldManager;
        var info = new AtlasInfo { Session = Guid.NewGuid().ToString("N"), Width = wm.MapSizeX, Height = wm.MapSizeZ, SeaLevel = api.World.SeaLevel, MapHeight = wm.MapSizeY, Source = "Observed terrain only" };
        info.HeightRange = Semantic.HeightRangeFor(info.MapHeight, info.SeaLevel);
        object cylinderCore = FindSystem(api, "CylinderWorld.CylinderWorldCore"), cylinder = Member(cylinderCore, "Params");
        // Cylinder World flattens the seam band only with FlattenSeabed (default on); its saved layout does not say.
        bool flattenSeam = Member(Member(cylinderCore, "Config"), "FlattenSeabed") is not false;
        if (cylinder != null)
        {
            info.StartX = Num(cylinder, "JumpInset"); info.Width = Num(cylinder, "Circumference"); info.Wraps = true;
            info.EquatorZ = Num(cylinder, "EquatorZ"); info.PoleDistance = Num(cylinder, "PoleDistance");
            info.HasGeography = info.PoleDistance > 0 && Bool(cylinder, "IsPlanetLayout");
        }
        var source = new WorldSource { Info = info };
        object earth = FindSystem(api, "EarthMap.EarthMapSystem"), p = Member(earth, "Params"), model = Member(earth, "Model");
        // Terra Prety replaces vanilla's ocean and landform map factories with its own continents and
        // heights. The cylinder fallback then reads Terra Prety's ocean layer, still without its relief.
        object terraPrety = FindSystem(api, "TerraPrety.TerraPretyModSystem");
        // Topology and geography stay with PredictTerrain off: exploration and recordings are keyed by them.
        if (!config.PredictTerrain) info.Source = "Observed terrain only (PredictTerrain off)";
        if (model != null && Bool(p, "Enabled"))
        {
            object g = Member(model, "Geometry");
            info.StartX = Num(g, "RingStart"); info.Width = Num(g, "RingLength");
            // Active topology takes priority over the saved generator's Wraps if Cylinder World was removed.
            info.Wraps = cylinder != null && Bool(g, "Wraps");
            info.EquatorZ = Num(g, "EquatorZ"); info.PoleDistance = Num(g, "BlocksPerDegreeZ") * 90;
            info.HasGeography = true; info.GeographyProvider = AtlasInfo.EarthGeography;
            if (config.PredictTerrain)
            {
                // TerrainModel is a read-only worldgen sampler after setup. Capture a shallow model snapshot
                // and clone its mutable zone list, avoiding later list replacement/mutation on the live model.
                var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
                object snapshot = clone.Invoke(model, null);
                var zones = model.GetType().GetField("FlatZones");
                if (zones?.GetValue(model) is System.Collections.IList list)
                {
                    var copied = (System.Collections.IList)Activator.CreateInstance(list.GetType());
                    foreach (object zone in list) copied.Add(clone.Invoke(zone, null));
                    zones.SetValue(snapshot, copied);
                }
                // Earth's own map export samples this model from parallel workers; one delegate serves all threads.
                var earthSample = EarthSampler(snapshot);
                source.Samplers = () => earthSample;
                info.Source = "Earth terrain prediction";
                try { source.Biome = EarthBiome(snapshot, api.WorldManager.SaveGame.WorldConfiguration, out string settings); info.BiomeId = BiomeTile.Identity(settings); }
                catch (Exception e) { api.Logger.Warning("[wmap] Earth forest and climate unavailable; far terrain keeps the atlas colours: {0}", e.Message); }
                source.WorldBiome = EarthHeights.OnBuiltInHeights(model.GetType().Assembly, source.Biome);
                try { source.Rivers = EarthRivers(Member(model, "Rivers")); }
                catch (Exception e) { api.Logger.Warning("[wmap] Earth river courses unavailable; rivers are drawn from sampled water only: {0}", e.Message); }
            }
        }
        else if (config.PredictTerrain && cylinder != null && CylinderSampler(api, cylinder) != null)
        {
            source.Samplers = () => CylinderSampler(api, cylinder);
            // A land/sea mask at ocean-map resolution with two invented heights: no relief to draw in 3D.
            info.FlatPrediction = true;
            info.Source = terraPrety != null ? "Approximate continents from Terra Prety's ocean map" : "Approximate cylinder continents";
            if (terraPrety != null)
                api.Logger.Warning("[wmap] Terra Prety shapes this world's terrain. Wmap predicts only its land/sea mask, without heights, and its coasts are approximate; recorded terrain draws over the prediction. Set PredictTerrain to false to chart recorded terrain only.");
        }
        if (cylinder != null && source.Samplers != null)
        {
            var surface = new CylinderSurface(cylinder, wm.MapSizeY, info.SeaLevel, flattenSeam);
            var inner = source.Samplers;
            source.Samplers = () => surface.Bind(inner());
            info.Source += Member(cylinder, "EarthPolarIce") is true ? " + Earth polar ice/walls" : " + cylinder ice/walls";
        }
        source.Sample = source.Samplers?.Invoke();
        info.HasOverview = source.Samplers != null;
        info.FarTiles = true;
        info.Biome = source.Biome != null && info.BiomeId != null;
        if (info.Width <= 0 || info.Height <= 0) throw new InvalidOperationException("Invalid saved world geometry");
        info.ObservationId = TileCodec.Hash(FormattableString.Invariant($"wmap-observed-v1|{wm.SaveGame.SavegameIdentifier}|surface|{info.StartX}|{info.Width}|{info.Height}"));
        string mods = string.Join(";", api.ModLoader.Mods.Select(m => m.Info.ModID + "@" + m.Info.Version).OrderBy(s => s));
        // Development builds can change terrain functions without changing modinfo's
        // version. Assembly revisions must invalidate derived predictions as well.
        string providers = typeof(WorldSource).Module.ModuleVersionId + "|" + model?.GetType().Module.ModuleVersionId + "|" + cylinder?.GetType().Module.ModuleVersionId;
        // Only what shapes terrain: runtime state such as Cylinder's sea-ice date must not move the cache on restart.
        string terrain = JsonConvert.SerializeObject(p) + "|" + SavedLayout(cylinder) + "|flatten=" + flattenSeam + "|" + ForeignConfig(api, terraPrety);
        info.CacheId = TileCodec.Hash("wmap-tiles-v4|" + config.CacheRevision + "|" + wm.SaveGame.SavegameIdentifier + "|" + wm.Seed + "|" + mods + "|" + providers + "|" + terrain + "|" + info.Width + "|" + info.Height
            // 0.5.0-pre.11: a taller world's heights span more than 256 blocks; its tiles before that were cut at 256.
            + (info.HeightRange != Semantic.DefaultHeightRange ? "|heights=" + info.HeightRange.ToString(CultureInfo.InvariantCulture) : ""));
        return source;
    }

    /// <summary>Cylinder World's saved layout: its protobuf fields, without runtime state (the sea-ice date,
    /// the Earth polar model) or the mist and jump-fog settings, which change no terrain.</summary>
    public static string SavedLayout(object cylinder)
    {
        if (cylinder == null) return "";
        var saved = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var f in cylinder.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (f.GetCustomAttributes(false).Any(a => a.GetType().Name == "ProtoMemberAttribute") && !f.Name.StartsWith("Mist", StringComparison.Ordinal) && !f.Name.StartsWith("JumpFog", StringComparison.Ordinal))
                saved[f.Name] = f.GetValue(cylinder);
        return JsonConvert.SerializeObject(saved);
    }

    /// <summary>Terra Prety's settings (TerraPrety.json) shape its continents; a change must re-chart the world.</summary>
    static string ForeignConfig(ICoreServerAPI api, object terraPrety)
    {
        if (terraPrety == null) return "";
        try { return JsonConvert.SerializeObject(terraPrety.GetType().GetField("config", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)?.GetValue(terraPrety)); }
        catch (Exception e) { api.Logger.Warning("[wmap] Could not read Terra Prety's settings for the chart cache: {0}", e.Message); return "unreadable"; }
    }

    public static global::System.Func<double, double, MapSample> EarthSampler(object model)
    {
        var method = model.GetType().GetMethod("GetColumn", new[] { typeof(double), typeof(double) }) ?? throw new MissingMethodException("Earth TerrainModel.GetColumn");
        ParameterExpression x = Expression.Parameter(typeof(double)), z = Expression.Parameter(typeof(double));
        var column = Expression.Variable(method.ReturnType);
        Expression field(string n) => Expression.Field(column, n);
        Expression river = method.ReturnType.GetField("River") != null ? field("River") : Expression.Constant(false);
        var convert = typeof(WorldSource).GetMethod(nameof(ColumnSample));
        var surface = EarthSurface.TryCreate(model);
        Expression result = surface == null
            ? Expression.Call(convert, field("Top"), field("WaterTop"), field("Water"), field("IceBottom"), river)
            : Expression.Call(Expression.Constant(surface), nameof(EarthSurface.Convert), null,
                field("Top"), field("WaterTop"), field("Water"), field("IceBottom"), river,
                method.ReturnType.GetField("Beach") != null ? field("Beach") : Expression.Constant(false), x, z);
        var body = Expression.Block(new[] { column }, Expression.Assign(column, Expression.Call(Expression.Constant(model), method, x, z)), result);
        return Expression.Lambda<global::System.Func<double, double, MapSample>>(body, x, z).Compile();
    }

    // Earth pre.8 applies these surfaces after GetColumn (EarthWorldGen.GenerateTerrain / Surface). Keep the
    // optional adapter here until Earth exposes its final surface kind: the core terrain remains Earth's column.
    sealed class EarthSurface
    {
        delegate void Climate(double lat, double lon, out double temperature, out double rain, out double height);
        readonly global::System.Func<double, double, GeoPosition> position;
        readonly global::System.Func<double, double, double> glaciers;
        readonly Climate climate;
        readonly int sea, height;
        readonly System.Threading.ThreadLocal<Dictionary<(int, int), bool>> frozen = new(() => new());

        EarthSurface(global::System.Func<double, double, GeoPosition> position, global::System.Func<double, double, double> glaciers,
            Climate climate, int sea, int height)
        { this.position = position; this.glaciers = glaciers; this.climate = climate; this.sea = sea; this.height = height; }

        public static EarthSurface TryCreate(object model)
        {
            var geo = model.GetType().GetMethod("TerrainGeoAt", new[] { typeof(double), typeof(double) });
            if (geo == null) return null; // Older providers retain their column-only prediction.
            var x = Expression.Parameter(typeof(double)); var z = Expression.Parameter(typeof(double));
            var g = Expression.Variable(geo.ReturnType);
            var body = Expression.Block(new[] { g }, Expression.Assign(g, Expression.Call(Expression.Constant(model), geo, x, z)),
                Expression.New(typeof(GeoPosition).GetConstructors()[0], Expression.Field(g, "Lat"), Expression.Field(g, "Lon"),
                    Expression.Field(g, "InGutter"), Expression.Field(g, "BeyondPole")));
            var position = Expression.Lambda<global::System.Func<double, double, GeoPosition>>(body, x, z).Compile();
            var data = Member(model, "Data");
            var coverage = data?.GetType().GetMethod("GlacierCoverage", new[] { typeof(double), typeof(double) });
            var glaciers = Bool(data, "HasGlaciers") && coverage != null
                ? (global::System.Func<double, double, double>)coverage.CreateDelegate(typeof(global::System.Func<double, double, double>), data) : null;
            var method = model.GetType().GetMethod("ClimateAt");
            var climate = method == null ? null : (Climate)method.CreateDelegate(typeof(Climate), model);
            var vertical = Member(model, "Vertical");
            return new(position, glaciers, climate, (int)Num(vertical, "SeaLevel"), (int)Num(vertical, "MapHeight"));
        }

        bool Frozen(double x, double z)
        {
            if (climate == null) return false;
            var key = ((int)Math.Floor(x / 32), (int)Math.Floor(z / 32));
            var cache = frozen.Value;
            if (cache.TryGetValue(key, out bool value)) return value;
            var g = position(key.Item1 * 32 + 16, key.Item2 * 32 + 16);
            climate(g.Latitude, g.Longitude, out double temperature, out _, out _);
            if (cache.Count >= 256) cache.Clear();
            return cache[key] = temperature < -12; // Exactly Earth's chunk-wide LakesFreeze predicate.
        }

        public MapSample Convert(int top, int waterTop, byte water, int iceBottom, bool river, bool beach, double x, double z)
        {
            if (water == 2 && waterTop > top && Frozen(x, z)) return new(waterTop, 0, TileData.Ice);
            if (water == 0 && !beach && iceBottom > top && top >= sea && top < height - 2 && glaciers != null)
            {
                var g = position(x, z);
                if (!g.InGutter && !g.BeyondPole && glaciers(g.Latitude, g.Longitude) >= 0.5) return new(top, 0, TileData.Ice);
            }
            return ColumnSample(top, waterTop, water, iceBottom, river);
        }
    }
    /// <summary>Earth's <c>ForestPotential</c> (what its worldgen plants trees by) and its climate-map pixel
    /// (<c>ClimatePixel</c>: the stored temperature and rain that vanilla's altitude adjustment turns into the
    /// surface values the game tints plants and water by), with the world's temperature and rain settings as
    /// Earth's worldgen reads them.</summary>
    public static global::System.Func<double, double, BiomeSample> EarthBiome(object model, ITreeAttribute worldConfig, out string settings)
    {
        var forest = model.GetType().GetMethod("ForestPotential", new[] { typeof(double), typeof(double) }) ?? throw new MissingMethodException("Earth TerrainModel.ForestPotential");
        // The far tint applies vanilla's lapse to the stored temperature itself. Earth worlds with real cooling by height
        // (Earth decision 0009, climate rules v2) store a byte that would be lapsed twice; Earth's helper gives the byte
        // as worlds before v2 store it, for either rules (in v1 worlds the same as ClimatePixel). Older Earth: ClimatePixel.
        var parameters = new[] { typeof(double), typeof(double), typeof(int), typeof(float), typeof(float) };
        var climate = model.GetType().GetMethod("ClimatePixelForVanillaLapse", parameters);
        bool vanillaLapse = climate?.ReturnType == typeof(int);
        if (!vanillaLapse) climate = model.GetType().GetMethod("ClimatePixel", parameters) ?? throw new MissingMethodException("Earth TerrainModel.ClimatePixel");
        var forestAt = (global::System.Func<double, double, double>)forest.CreateDelegate(typeof(global::System.Func<double, double, double>), model);
        var climateAt = (global::System.Func<double, double, int, float, float, int>)climate.CreateDelegate(typeof(global::System.Func<double, double, int, float, float, int>), model);
        float Read(string name) => float.TryParse(worldConfig?.GetString(name, "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;
        float tempMul = Read("globalTemperature"), rainMul = Read("globalPrecipitation");
        // A different identity rebuilds biome tiles and climate rasters cached from the sea-level byte of a v2 world.
        settings = FormattableString.Invariant($"{(vanillaLapse ? "earth2" : "earth")}|{tempMul}|{rainMul}");
        return (x, z) =>
        {
            int pixel = climateAt(x, z, 0, tempMul, rainMul);
            return new((byte)Math.Clamp((int)Math.Round(forestAt(x, z) * 255), 0, 255), (byte)(pixel >> 16 & 255), (byte)(pixel >> 8 & 255));
        };
    }

    public static MapSample ColumnSample(int top, int waterTop, byte water, int iceBottom, bool river) =>
        new(top, water != 0 ? Math.Max(0, waterTop - top) : 0,
            water != 0 ? (river ? TileData.River : TileData.Water) : iceBottom <= top ? TileData.Ice : TileData.Land);

    /// <summary>Earth's laid-out river courses: each segment with its carved half width.</summary>
    public static List<RiverSegment> EarthRivers(object network)
    {
        if (network == null) return null;
        var segments = Private(network, "segs") as Array ?? throw new MissingFieldException("RiverNetwork.segs");
        var halfWidth = network.GetType().GetMethod("HalfWidth", new[] { typeof(double) }) ?? throw new MissingMethodException("RiverNetwork.HalfWidth");
        var type = segments.GetType().GetElementType();
        FieldInfo F(string n) => type.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException("RiverNetwork.Seg." + n);
        // Compile one reader instead of boxing every field of every segment through reflection.
        var item = Expression.Parameter(typeof(object));
        var typed = Expression.Unbox(item, type);
        Expression Read(string n) => Expression.Convert(Expression.Field(typed, F(n)), typeof(double));
        var tuple = typeof(ValueTuple<double, double, double, double, double>);
        var read = Expression.Lambda<global::System.Func<object, (double, double, double, double, double)>>(
            Expression.New(tuple.GetConstructor(new[] { typeof(double), typeof(double), typeof(double), typeof(double), typeof(double) }),
                Read("X1"), Read("Z1"), Read("X2"), Read("Z2"), Read("Q")), item).Compile();
        var widths = new Dictionary<double, double>();
        var result = new List<RiverSegment>(segments.Length);
        foreach (object segment in segments)
        {
            var (x1, z1, x2, z2, q) = read(segment);
            if (!widths.TryGetValue(q, out double hw)) widths[q] = hw = Convert.ToDouble(halfWidth.Invoke(network, new object[] { q }), CultureInfo.InvariantCulture);
            if (double.IsFinite(x1 + z1 + x2 + z2 + hw)) result.Add(new(x1, z1, x2, z2, hw));
        }
        return result;
    }

    static global::System.Func<double, double, MapSample> CylinderSampler(ICoreServerAPI api, object cylinder)
    {
        var gen = api.ModLoader.GetModSystem<GenMaps>();
        if (gen?.requireLandAt == null || gen.requireLandAt.Count == 0 || gen.noiseSizeOcean <= 0) return null;
        var save = api.WorldManager.SaveGame;
        float read(string name) => float.TryParse(save.WorldConfiguration.GetString(name, "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;
        var ocean = GenMaps.GetOceanMapGen(api.WorldManager.Seed + 1873, read("landcover"), TerraGenConfig.oceanMapScale, read("oceanscale"), new List<XZ>(gen.requireLandAt), GameVersion.IsLowerVersionThan(save.CreatedGameVersion, "1.20.0-pre.14"));
        int pixelBlocks = Math.Max(1, api.WorldManager.RegionSize / gen.noiseSizeOcean);
        var force = (global::System.Func<double, double, int>)cylinder.GetType().GetMethod("OceanForce").CreateDelegate(typeof(global::System.Func<double, double, int>), cylinder);
        int sea = api.World.SeaLevel;
        // A bounded set of small generator patches avoids allocating a row as wide as the planet.
        // The generator and patch cache belong to one worker thread.
        var patches = new Dictionary<(int, int), int[]>();
        return (x, z) =>
        {
            int px = (int)x / pixelBlocks, pz = (int)z / pixelBlocks, tx = px >> 5, tz = pz >> 5;
            if (!patches.TryGetValue((tx, tz), out var values))
            {
                if (patches.Count >= 256) patches.Clear();
                values = ocean.GenLayer(tx * 32, tz * 32, 32, 32); patches[(tx, tz)] = values;
            }
            bool water = Math.Max(values[(pz & 31) * 32 + (px & 31)], force(x, z)) > 127;
            return new MapSample(water ? sea - 40 : sea + 10, water ? 40 : 0, water ? TileData.Water : TileData.Land);
        };
    }

    public static global::System.Func<double, double, GeoPosition> Geography(ICoreAPI api, AtlasInfo info)
    {
        object earth = FindSystem(api, "EarthMap.EarthMapSystem"), model = Member(earth, "Model"), g = Member(model, "Geometry");
        if (g != null && Bool(Member(earth, "Params"), "Enabled"))
        {
            var method = g.GetType().GetMethod("ToGeo", new[] { typeof(double), typeof(double) });
            ParameterExpression x = Expression.Parameter(typeof(double)), z = Expression.Parameter(typeof(double)), result = Expression.Variable(method.ReturnType);
            var body = Expression.Block(new[] { result }, Expression.Assign(result, Expression.Call(Expression.Constant(g), method, x, z)),
                Expression.New(typeof(GeoPosition).GetConstructor(new[] { typeof(double), typeof(double), typeof(bool), typeof(bool) }),
                    Expression.Field(result, "Lat"), Expression.Field(result, "Lon"), Expression.Field(result, "InGutter"), Expression.Field(result, "BeyondPole")));
            return Expression.Lambda<global::System.Func<double, double, GeoPosition>>(body, x, z).Compile();
        }
        // Wait for Earth's synchronized client model rather than permanently using a wrong linear datum.
        // Also with PredictTerrain off, whose Source does not name Earth.
        if (info.GeographyProvider == AtlasInfo.EarthGeography || info.Source?.StartsWith("Earth", StringComparison.Ordinal) == true || !info.HasGeography || info.PoleDistance <= 0) return null;
        var topology = info.Topology();
        return (x, z) => new((info.EquatorZ - z) / info.PoleDistance * 90, (topology.Canonical(x) - info.StartX) / info.Width * 360 - 180, false, Math.Abs(z - info.EquatorZ) > info.PoleDistance);
    }
}
