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
/// <item>Vanilla's disc generators (0.5.3-rc.13, WM-0025): the pick's own question, where the column's map chunk is loaded:
/// the region's ore map × the share of the deposit's host blocks in the pick's rock column, within the deposit's height
/// band. The column is our own <see cref="ProPickWorkSpace"/>'s simulated one, adjusted by Earth as Earth adjusts the
/// pick's (its own rock in its layers, nothing below its rock floor; <c>RockFloorPass.RockFloorReading</c>). The band is
/// drawn as the generator draws it for the pick, from a random of our own: the generator's own random is world
/// generation's, reseeded before each deposit it places, and drawing from it here would move ore in chunks being made.
/// Blocks = factor × the expected blocks of a chunk at full map (their average deposit volume × tries × the world's
/// deposit rate × height scaling). Until rc.12 the ore map alone, host rock not counted: on Earth's default ores the map
/// promised ore the pick does not find (gold 0.675 against 0 by Riga). Elsewhere a cell is not known yet.</item>
/// </list>
/// The map comes in tiles (<see cref="OreHeat"/>). A tile is worked out for every ore at once — one surface height per
/// cell, then each ore's reading — and complete tiles are stored in the world's cache folder beside the atlas's tiles, so
/// a tile is worked out once per world and mod set. Where every ore answers anywhere (Earth), the first request also
/// starts storing the coarse levels of the whole world in the background (<see cref="StoreAheadTiles"/>), so every zoom
/// has a map at once. One worker serves players' newest requests a tile at a time, players in turn; answers are sent from
/// <see cref="Tick"/> on the main thread.
/// </summary>
public sealed partial class OreHeatServer : IDisposable
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

    /// <param name="folder">The world's cache folder (null while unknown: nothing is stored).</param>
    /// <param name="diskMiB">The cache folder's size limit, shared with the atlas's tiles.</param>
    public OreHeatServer(ICoreServerAPI api, IServerNetworkChannel channel, System.Func<IServerPlayer, bool> allowed, System.Func<Topology> topology,
        System.Func<System.Func<double, double, MapSample>> samplers, int threads, System.Func<string> folder = null, int diskMiB = 512)
    {
        this.api = api; this.channel = channel; this.allowed = allowed; this.topology = topology; this.samplers = samplers; this.folder = folder;
        this.threads = Math.Clamp(threads, 1, 8);
        this.diskMiB = diskMiB;
    }

    /// <summary>Coarse tiles stored ahead of time so far, and how many there are to store (0 before the first request).</summary>
    public (int Done, int Of) StoredAhead => (Volatile.Read(ref stored), Volatile.Read(ref storedOf));

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
                    TileCache.Wrote(dir, bytes.Length, diskMiB);
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
                PickCell picked = null;
                for (int i = 0; i < ores.Length; i++)
                {
                    if (Cell(ores[i], pos, top, rock, unknown, regionSize, ref picked, out double b, out double f)) tile.Set(i, cell, b, f);
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
