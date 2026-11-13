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

/// <summary>
/// Ore heat maps for players with the full map (WM-D47, tiled in WM-D48). Per cell and ore: the ore blocks a chunk column
/// there is expected to hold, and the prospecting pick's factor, from the world's own deposit generators through the
/// game's API — the same variant per ore code the pick asks (ProPickWorkSpace: the last one with an ore map, children
/// included).
/// <list type="bullet">
/// <item>Generators that answer the pick from their own evidence, without a block column or generated chunks (Earth Map's
/// regional ores, <c>EarthMap.EarthOreGenerator</c>), are asked exactly as the pick asks them, anywhere on the planet.
/// Blocks = parts per thousand × surface height × 1024 / 1000. Where Earth keeps vanilla's rock strata in a column (its
/// <c>EarthGeology.RegionalRock</c> is 0 at some depth above the rock floor), the pick hands it vanilla's simulated column
/// and Earth counts the host rock there; so does this map, from its own <see cref="ProPickWorkSpace"/>, where the column's
/// map chunk is loaded. Elsewhere such a cell is not known yet (no answer, asked again later), never 0 (WM-0011).</item>
/// <item>Vanilla's disc generators: the region's ore map × the expected blocks of a chunk at full map (their average
/// deposit volume × tries × the world's deposit rate × height scaling), where the region is loaded. Host rock is not
/// counted, so this is an upper bound, and the factor is the ore map's (the pick also multiplies by the column's share of
/// host rock).</item>
/// </list>
/// The map comes in tiles (<see cref="OreHeat"/>). A tile is worked out for every ore at once — one surface height per
/// cell, then each ore's reading — and complete tiles are stored in the world's cache folder beside the atlas's tiles, so
/// a tile is worked out once per world and mod set. Where every ore answers anywhere (Earth), the first request also
/// starts storing the coarse levels of the whole world in the background (<see cref="StoreAheadTiles"/>), so every zoom
/// has a map at once. One worker serves players' newest requests a tile at a time, players in turn; answers are sent from
/// <see cref="Tick"/> on the main thread.
/// </summary>
public sealed class OreHeatServer : IDisposable
{
    public const string EarthGenerator = "EarthMap.EarthOreGenerator";
    /// <summary>Coarse tiles stored ahead of time at most: whole levels from the top down while they fit.</summary>
    public const int StoreAheadTiles = 1000;
    /// <summary>Threads (below normal priority) storing coarse tiles ahead of time.</summary>
    public const int StoreAheadThreads = 2;
    /// <summary>Complete tiles kept in memory, so switching ores over the same view reads nothing.</summary>
    const int MemoryTiles = 48;
    /// <summary>Answers sent per server tick (50 ms): a tile's answer is about 8 KiB.</summary>
    const int SendsPerTick = 16;
    readonly ICoreServerAPI api;
    readonly IServerNetworkChannel channel;
    readonly System.Func<IServerPlayer, bool> allowed;
    readonly System.Func<Topology> topology;
    readonly System.Func<System.Func<double, double, MapSample>> samplers;
    readonly System.Func<string> folder;
    readonly int threads, diskMiB;
    // Per player, the ore and tiles of their newest request, taken a tile at a time, players in turn.
    readonly object gate = new();
    readonly Dictionary<string, (string Code, Queue<(int Level, int X, int Z)> Tiles)> queues = new();
    readonly List<string> order = new();
    int turn;
    readonly SemaphoreSlim signal = new(0);
    readonly ConcurrentQueue<(string Uid, OreHeatResponse Response)> done = new();
    readonly CancellationTokenSource stop = new();
    // Tiles being worked out now, by file name: a second asker (another player, the store-ahead) waits for the first.
    readonly ConcurrentDictionary<string, Lazy<OreHeat.OreTile>> making = new();
    readonly ConcurrentBag<System.Func<double, double, MapSample>> idleSamplers = new();
    readonly LinkedList<(string Name, OreHeat.OreTile Tile)> memory = new();
    Task worker;
    volatile bool disposed;
    int storeAhead, stored, storedOf;
    Ore[] ores;
    Dictionary<string, int> byCode;
    string fingerprint;
    // Where Earth keeps vanilla strata (WM-0011): Earth's regional rock at (x, z, depth below top, top), 0 for vanilla's,
    // and the top of its rock floor; and a prospecting workspace of our own for vanilla's simulated column (one at a time:
    // its strata and deposit generators keep state between calls).
    System.Func<double, double, int, int, int> earthRock;
    System.Func<int, int, int> earthFloor;
    ProPickWorkSpace pick;
    readonly object pickGate = new();
    int pickErrors;

    sealed class Ore
    {
        public string Code;
        public DepositVariant Variant;
        /// <summary>The generator answers the pick's question without a block column (Earth Map's).</summary>
        public bool Reading;
        /// <summary>Vanilla: expected ore blocks per chunk where the ore map is full.</summary>
        public double BlocksAtFull;
    }

    /// <param name="folder">The world's cache folder (null while unknown: nothing is stored).</param>
    /// <param name="diskMiB">The cache folder's size limit, shared with the atlas's tiles.</param>
    public OreHeatServer(ICoreServerAPI api, IServerNetworkChannel channel, System.Func<IServerPlayer, bool> allowed, System.Func<Topology> topology,
        System.Func<System.Func<double, double, MapSample>> samplers, int threads, System.Func<string> folder = null, int diskMiB = 512)
    {
        this.api = api; this.channel = channel; this.allowed = allowed; this.topology = topology; this.samplers = samplers; this.folder = folder;
        this.threads = Math.Clamp(threads, 1, 8);
        this.diskMiB = diskMiB;
    }

    /// <summary>The ore codes there are heat maps of, in the order the pick knows them; empty without deposits. Built on
    /// first use (main thread), after the world's deposit generators are set up.</summary>
    public string[] Codes { get { Catalog(); return ores.Select(o => o.Code).ToArray(); } }
    /// <summary><see cref="OreHeatRequest.Reading"/> when every ore is answered by its generator, <see cref="OreHeatRequest.OreMap"/>
    /// when by vanilla's ore maps, "mixed" for both; null without ores.</summary>
    public string Method
    {
        get
        {
            Catalog();
            if (ores.Length == 0) return null;
            int reading = ores.Count(o => o.Reading);
            return reading == ores.Length ? OreHeatRequest.Reading : reading == 0 ? OreHeatRequest.OreMap : "mixed";
        }
    }
    /// <summary>Coarse tiles stored ahead of time so far, and how many there are to store (0 before the first request).</summary>
    public (int Done, int Of) StoredAhead => (Volatile.Read(ref stored), Volatile.Read(ref storedOf));

    void Catalog()
    {
        if (ores != null) return;
        var list = new List<Ore>();
        try
        {
            var deposits = api.ModLoader.GetModSystem<GenDeposits>()?.Deposits;
            if (deposits != null)
            {
                // As the pick: the last variant with an ore map per code, parents before their children.
                var variants = new Dictionary<string, DepositVariant>();
                var order = new List<string>();
                void Add(DepositVariant v)
                {
                    if (v?.Code == null || !v.WithOreMap) return;
                    if (!variants.ContainsKey(v.Code)) order.Add(v.Code);
                    variants[v.Code] = v;
                }
                foreach (var v in deposits)
                {
                    Add(v);
                    if (v?.ChildDeposits != null) foreach (var child in v.ChildDeposits) Add(child);
                }
                double rate = DepositRate(), heightScale = api.WorldManager.MapSizeY / 256.0;
                foreach (string code in order)
                {
                    var v = variants[code];
                    var ore = new Ore { Code = code, Variant = v };
                    if (v.GeneratorInst?.GetType().FullName == EarthGenerator) ore.Reading = true;
                    else if (v.GeneratorInst is DiscDepositGenerator disc)
                        ore.BlocksAtFull = disc.GetAbsAvgQuantity(new LCGRandom(api.World.Seed)) * rate * (v.ScaleWithWorldheight ? heightScale : 1);
                    else continue;
                    list.Add(ore);
                    if (list.Count == OreHeat.MaxOres) break;
                }
            }
        }
        catch (Exception e) { api.Logger.Warning("[wmap] Ore heat maps unavailable: {0}", e.Message); list.Clear(); }
        ores = list.ToArray();
        byCode = new Dictionary<string, int>();
        for (int i = 0; i < ores.Length; i++) byCode[ores[i].Code] = i;
        // What a stored tile depends on beyond the cache folder (seed, mods and terrain): the ores, their generators and
        // amounts, the world's height and the tiles' layout.
        string catalogue = string.Join(";", ores.Select(o => o.Code + "=" + (o.Reading ? "reading" : o.BlocksAtFull.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
            + ":" + o.Variant.GeneratorInst?.GetType().FullName + "@" + o.Variant.GeneratorInst?.GetType().Module.ModuleVersionId));
        EarthStrata();
        fingerprint = TileCodec.Hash($"wmap-oreheat-v2|{catalogue}|{api.WorldManager.MapSizeY}|{api.World.Seed}|{OreHeat.TileCells}|{typeof(OreHeatServer).Module.ModuleVersionId}")[..16];
        if (ores.Length > 0) api.Logger.Notification("[wmap] Ore heat maps: {0} ores ({1})", ores.Length, Method);
    }

    /// <summary>Vanilla multiplies every deposit's tries by deposits.json's chanceMultiplier, which the world's
    /// globalDepositSpawnRate patches.</summary>
    double DepositRate()
    {
        try { return api.Assets.TryGet(new AssetLocation("game:worldgen/deposits.json"))?.ToObject<Deposits>()?.ChanceMultiplier ?? 1; }
        catch (Exception) { return 1; }
    }

    /// <summary>A packet from a player (main thread): checked, and kept as their newest request, replacing the tiles of
    /// the one before.</summary>
    public void Request(IServerPlayer p, OreHeatRequest r)
    {
        if (disposed || r == null || !allowed(p)) return;
        Catalog();
        var t = topology();
        if (t == null || ores.Length == 0 || r.Code == null || r.Code != OreHeat.Listed && !byCode.ContainsKey(r.Code)) return;
        int n = r.X?.Length ?? 0;
        if (n == 0 || n > OreHeat.MaxTilesPerRequest || r.Z?.Length != n || r.Level?.Length != n) return;
        var tiles = new Queue<(int, int, int)>();
        var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < n; i++)
            if (OreHeat.ValidKey(t, r.Level[i], r.X[i], r.Z[i]) && seen.Add((r.Level[i], r.X[i], r.Z[i]))) tiles.Enqueue((r.Level[i], r.X[i], r.Z[i]));
        if (tiles.Count == 0) return;
        lock (gate)
        {
            queues[p.PlayerUID] = (r.Code, tiles);
            if (!order.Contains(p.PlayerUID)) order.Add(p.PlayerUID);
        }
        worker ??= Task.Run(Work);
        signal.Release();
        StoreAhead(t);
    }

    /// <summary>Sends finished tiles (main thread) to players who still have the full map.</summary>
    public void Tick()
    {
        for (int i = 0; i < SendsPerTick && done.TryDequeue(out var d); i++)
            if (api.World.PlayerByUid(d.Uid) is IServerPlayer player && player.ConnectionState == EnumClientState.Playing && allowed(player))
                channel.SendPacket(d.Response, player);
    }

    public void Leave(string uid)
    {
        lock (gate) { queues.Remove(uid); order.Remove(uid); }
    }

    bool Next(out string uid, out string code, out (int Level, int X, int Z) key)
    {
        lock (gate)
        {
            for (int n = 0; n < order.Count; n++)
            {
                string u = order[(turn + n) % order.Count];
                if (!queues.TryGetValue(u, out var q) || q.Tiles.Count == 0) continue;
                turn = (turn + n + 1) % order.Count;
                uid = u; code = q.Code; key = q.Tiles.Dequeue();
                return true;
            }
        }
        uid = code = null; key = default;
        return false;
    }

    async Task Work()
    {
        while (!disposed)
        {
            if (!Next(out string uid, out string code, out var key))
            {
                try { await signal.WaitAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            OreHeatResponse response;
            try { response = Serve(code, key.Level, key.X, key.Z); }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                api.Logger.Warning("[wmap] Ore heat tile failed: {0}", e);
                response = new OreHeatResponse { Code = code, Level = key.Level, X = key.X, Z = key.Z, Error = "failed: " + e.Message };
            }
            done.Enqueue((uid, response));
        }
    }

    /// <summary>One ore's (or the listed ores') answer for a tile, from memory, the cache folder or worked out now (and
    /// stored). Public for the smoke checks.</summary>
    public OreHeatResponse Serve(string code, int level, int x, int z)
    {
        Catalog();
        var t = topology();
        var response = new OreHeatResponse { Code = code, Level = level, X = x, Z = z };
        if (!OreHeat.ValidKey(t, level, x, z)) { response.Error = "invalid tile"; return response; }
        bool listed = code == OreHeat.Listed;
        if (!listed && (code == null || !byCode.ContainsKey(code))) { response.Error = "unknown ore"; return response; }
        var watch = Stopwatch.StartNew();
        var tile = Tile(level, x, z, threads, true, out bool made);
        (response.Blocks, response.Factors) = listed ? tile.Listed() : tile.One(byCode[code]);
        response.Partial = !tile.Complete;
        response.Millis = made ? (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds) : 0;
        (response.Stored, response.StoredOf) = StoredAhead;
        return response;
    }

    string FileName(int level, int x, int z) => $"o-{fingerprint}-{level}-{x}-{z}.wmt";

    /// <summary>Every ore over a tile: from memory, from the cache folder, or worked out on <paramref name="workers"/>
    /// threads and stored when complete. <paramref name="made"/>: worked out now (or by another asker just now).</summary>
    OreHeat.OreTile Tile(int level, int x, int z, int workers, bool remember, out bool made)
    {
        made = false;
        string name = FileName(level, x, z);
        lock (memory)
            for (var node = memory.First; node != null; node = node.Next)
                if (node.Value.Name == name) { memory.Remove(node); memory.AddFirst(node); return node.Value.Tile; }
        string dir = folder?.Invoke();
        string path = dir != null ? Path.Combine(dir, name) : null;
        if (path != null && File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > OreHeat.OreTile.MaxPackedBytes(ores.Length)) throw new InvalidDataException("Oversized ore tile");
                var read = OreHeat.OreTile.Unpack(File.ReadAllBytes(path), ores.Length);
                if (remember) Remember(name, read);
                return read;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { /* Work out a damaged tile again. */ }
        }
        var lazy = making.GetOrAdd(name, _ => new Lazy<OreHeat.OreTile>(() =>
        {
            var tile = Make(level, x, z, workers);
            if (tile.Complete && path != null)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var bytes = tile.Pack();
                    TileCodec.WriteAtomic(path, bytes);
                    AtlasServer.Wrote(dir, bytes.Length, diskMiB);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* Storing is optional; the tile is still answered. */ }
            }
            return tile;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var tile = lazy.Value;
            made = true;
            if (remember && tile.Complete) Remember(name, tile);
            return tile;
        }
        finally { making.TryRemove(new KeyValuePair<string, Lazy<OreHeat.OreTile>>(name, lazy)); }
    }

    void Remember(string name, OreHeat.OreTile tile)
    {
        lock (memory)
        {
            for (var node = memory.First; node != null; node = node.Next) if (node.Value.Name == name) { memory.Remove(node); break; }
            memory.AddFirst((name, tile));
            while (memory.Count > MemoryTiles) memory.RemoveLast();
        }
    }

    /// <summary>Works out every ore over a tile: one surface height per cell (the loaded chunk's, else the terrain's
    /// prediction, else sea level), then each ore at that column. Rows run on up to <paramref name="workers"/> threads.</summary>
    public OreHeat.OreTile Make(int level, int tx, int tz, int workers)
    {
        Catalog();
        var t = topology();
        var tile = new OreHeat.OreTile(ores.Length);
        int complete = 1;
        int sea = api.World.SeaLevel, mapSizeY = api.WorldManager.MapSizeY, regionSize = api.WorldManager.RegionSize;
        // Only readings turn parts per thousand into blocks by the column's height; vanilla's ore maps need none.
        bool heights = ores.Any(o => o.Reading);
        void Row(int row, System.Func<double, double, MapSample> sampler)
        {
            for (int column = 0; column < OreHeat.TileCells; column++)
            {
                int cell = row * OreHeat.TileCells + column;
                var (sx, sz) = OreHeat.Sample(t, level, tx, tz, column, row);
                if (double.IsNaN(sx) || !t.Wraps && (sx < t.StartX || sx >= t.EndX))
                {
                    for (int i = 0; i < ores.Length; i++) tile.Set(i, cell, double.NaN, double.NaN);
                    continue;
                }
                var (pos, top, rock, unknown) = Column(t.Canonical(sx), sz, sea, mapSizeY, heights, sampler);
                for (int i = 0; i < ores.Length; i++)
                {
                    if (Cell(ores[i], pos, top, rock, unknown, regionSize, out double b, out double f)) tile.Set(i, cell, b, f);
                    else { tile.Set(i, cell, double.NaN, double.NaN); Volatile.Write(ref complete, 0); }
                }
            }
        }
        System.Func<double, double, MapSample> Borrow() => !heights ? null : idleSamplers.TryTake(out var s) ? s : samplers?.Invoke();
        if (workers <= 1)
        {
            var sampler = Borrow();
            try { for (int row = 0; row < OreHeat.TileCells; row++) { stop.Token.ThrowIfCancellationRequested(); Row(row, sampler); } }
            finally { if (sampler != null) idleSamplers.Add(sampler); }
        }
        else
        {
            Parallel.For(0, OreHeat.TileCells, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = stop.Token }, Borrow,
                (row, state, sampler) => { Row(row, sampler); return sampler; }, sampler => { if (sampler != null) idleSamplers.Add(sampler); });
        }
        tile.Complete = complete == 1;
        return tile;
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
    void EarthStrata()
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
        }
        catch (Exception e) { earthRock = null; earthFloor = null; api.Logger.Warning("[wmap] Ore heat maps: Earth's rock strata could not be read ({0}); columns where Earth keeps vanilla strata may read low", e.Message); }
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
                if (pick == null) { pick = new ProPickWorkSpace(); pick.OnLoaded(api); }
                return pick.GetRockColumn(pos.X, pos.Z);
            }
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref pickErrors) == 1) api.Logger.Warning("[wmap] Ore heat maps: vanilla's rock column could not be made: {0}", e.Message);
            unknown = true;
            return null;
        }
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
        string map = Cell(ore, pos, top, rock, unknown, regionSize, out double blocks, out double factor)
            ? $"map {blocks:0.#} blocks/chunk, factor {factor:0.000}" : "map: not known here";
        string column = !ore.Reading ? "ore map" : unknown ? "vanilla strata, chunk not loaded" : rock != null ? "vanilla strata, simulated column" : "Earth's rock";
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
    bool Cell(Ore ore, BlockPos pos, int top, int[] rock, bool unknown, int regionSize, out double blocks, out double factor)
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
        if (api.WorldManager.GetMapRegion(pos.X / regionSize, pos.Z / regionSize) == null) return false;
        double f = ore.Variant.GetOreMapFactor(pos.X / OreHeat.ChunkSize, pos.Z / OreHeat.ChunkSize);
        blocks = f * ore.BlocksAtFull;
        factor = f;
        return true;
    }

    /// <summary>Starts storing the coarse levels (<see cref="OreHeat.StoreAheadKeys"/>), once per server run, where every
    /// ore answers anywhere and there is a folder to store them in. Tiles already stored are counted and skipped.</summary>
    void StoreAhead(Topology t)
    {
        if (Method != OreHeatRequest.Reading || folder?.Invoke() == null || Interlocked.CompareExchange(ref storeAhead, 1, 0) != 0) return;
        var keys = OreHeat.StoreAheadKeys(t, StoreAheadTiles);
        Volatile.Write(ref storedOf, keys.Count);
        int next = -1, madeCount = 0, failed = 0;
        var watch = Stopwatch.StartNew();
        int running = StoreAheadThreads;
        for (int n = 0; n < StoreAheadThreads; n++)
        {
            new Thread(() =>
            {
                try
                {
                    for (int i; (i = Interlocked.Increment(ref next)) < keys.Count && !disposed;)
                    {
                        var (level, x, z) = keys[i];
                        try
                        {
                            string path = Path.Combine(folder() ?? "", FileName(level, x, z));
                            if (!File.Exists(path)) { Tile(level, x, z, 1, false, out bool made); if (made) Interlocked.Increment(ref madeCount); }
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception e) { if (Interlocked.Increment(ref failed) == 1) api.Logger.Warning("[wmap] Ore heat store-ahead tile failed: {0}", e.Message); }
                        Interlocked.Increment(ref stored);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref running) == 0 && !disposed)
                        api.Logger.Notification("[wmap] Ore heat maps: {0} coarse tiles stored (levels {1} to {2}, {3} worked out now) in {4:0.0} s",
                            keys.Count, keys.Count > 0 ? keys[0].Level : 0, keys.Count > 0 ? keys[^1].Level : 0, madeCount, watch.Elapsed.TotalSeconds);
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Wmap ore heat store-ahead" }.Start();
        }
    }

    public void Dispose()
    {
        disposed = true;
        stop.Cancel();
        lock (gate) { queues.Clear(); order.Clear(); }
        lock (pickGate) { pick?.Dispose(api); pick = null; }
    }
}
