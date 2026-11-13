using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnumChatType = Vintagestory.API.Common.EnumChatType;
using EnumClientState = Vintagestory.API.Server.EnumClientState;
using EnumGameMode = Vintagestory.API.Common.EnumGameMode;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Wmap.Core;
using Wmap.Integrations;

namespace Wmap.Server;

/// <summary>
/// Bakes the world raster once per world revision, streams it to clients, and samples predicted
/// detail tiles for zoom levels finer than that raster. All sampling runs on worker threads.
/// </summary>
public sealed class AtlasServer : IDisposable
{
    readonly ICoreServerAPI api;
    readonly IServerNetworkChannel channel;
    readonly WmapConfig config;
    readonly CancellationTokenSource stop = new();
    readonly BlockingCollection<Job> jobs = new(64);
    // Far terrain's tiles have their own queue per player and one worker, taking players in turn: a near band
    // filling in never occupies the atlas's workers, and one player's band cannot hold up another's.
    readonly ConcurrentDictionary<string, FarQueue> farJobs = new();
    // Players with far work waiting, in turn order: each is listed once while it has work. A turn names the queue it was
    // listed for: one left behind by a player who left (and maybe came back with a new queue) is skipped.
    readonly ConcurrentQueue<(string Uid, FarQueue Queue)> farTurns = new();
    readonly SemaphoreSlim farSignal = new(0);
    sealed class FarQueue { public readonly ConcurrentQueue<Job> Jobs = new(); public int Listed; }
    readonly BlockingCollection<(string uid, int generation, TileResponse response)> completed = new(64);
    // Derived tiles being made now, by cache path: a second request for one waits for it instead of sampling again.
    readonly ConcurrentDictionary<string, Lazy<byte[]>> making = new();
    // Per player, bumped when they leave: work finished for an earlier connection neither counts nor is sent.
    readonly Dictionary<string, int> generations = new();
    // Per player and stream ("uid|stream"): the current view, and requests not yet answered.
    readonly ConcurrentDictionary<string, int> views = new();
    readonly Dictionary<string, int> outstanding = new();
    /// <summary>Unanswered requests a player may have per stream: the atlas, far terrain.</summary>
    public static readonly int[] StreamLimits = { 12, 8 };
    static string StreamKey(string uid, int stream) => uid + "|" + stream;
    readonly HashSet<string> greeted = new();
    readonly HashSet<string> chartAnnounced = new();
    WorldSource source;
    readonly List<Task> workers = new();
    volatile bool disposed;
    string cache;
    // World raster state, written by the bake task and read on the main thread.
    volatile byte[] basePacked;
    volatile string baseId;
    int baseWidth, baseHeight;
    volatile float baseProgress;
    // The climate raster (Earth Map), baked after the chart; sent the same way under its own id.
    volatile byte[] climatePacked;
    volatile string climateId;
    volatile bool climateFresh;
    float announcedProgress = -1;
    long announcedAt;
    sealed record Job(string Uid, TileRequest Request, int Generation);
    /// <summary>Whether a request asks for something this server serves: the atlas wants semantic tiles; far terrain
    /// wants semantic and biome tiles of the near band (level 2) or biome tiles at any level.</summary>
    public static bool Acceptable(TileRequest r, bool biome) => r.Stream switch
    {
        0 => r.Content == TileContent.Semantic,
        1 => r.Content == TileContent.SemanticAndBiome && r.Level == 2 || r.Content == TileContent.Biome && biome,
        _ => false
    };
    // World chart transfers: each player's requested offsets, deduplicated and capped on arrival, then sent
    // from Tick within a byte budget. A client can ask again and again; it cannot make the server send more.
    // Chunks of the chart (Climate false) or of the climate raster (Climate true).
    readonly record struct ChartPart(bool Climate, int Offset);
    sealed class ChartQueue { public readonly Queue<ChartPart> Offsets = new(); public readonly HashSet<ChartPart> Pending = new(); }
    readonly ConcurrentDictionary<string, ChartQueue> chartQueues = new();
    int chartRound;
    /// <summary>Above the client's 16-chunk window, so an honest download never loses a request.</summary>
    public const int ChartPendingLimit = 32;
    /// <summary>64 KiB chunks per 50 ms server tick: 10 MiB/s for one player, 20 MiB/s for all.</summary>
    public const int ChartChunksPerPlayerTick = 8, ChartChunksPerTick = 16;
    public AtlasServer(ICoreServerAPI api, IServerNetworkChannel channel, WmapConfig config) { this.api = api; this.channel = channel; this.config = config; }
    public WorldSource Source => source;
    /// <summary>This world's cache folder (by its terrain's cache id); null until <see cref="Start"/>.</summary>
    public string CacheFolder => cache;
    /// <summary>Ore heat maps (WM-D47), offered in the manifest to players with the full map; null without.</summary>
    public OreHeatServer OreHeat;
    public bool BaseReady => basePacked != null;

    public void Start()
    {
        source = WorldSource.Create(api, config);
        cache = Path.Combine(api.GetOrCreateDataPath("Wmap"), "server", source.Info.CacheId);
        StartWorkers(Math.Clamp(config.Threads / 2, 1, 4));
        api.Logger.Notification("[wmap] {0}; ring {1} x {2}; wrap {3}; {4} river segments", source.Info.Source, source.Info.Width, source.Info.Height, source.Info.Wraps, source.Rivers?.Count ?? 0);
        if (source.Samplers != null)
        {
            // Announce the planned size now; clients then request only detail finer than it.
            (baseWidth, baseHeight) = BaseRaster.SizeFor(source.Info.Topology(), config.WorldChartSize);
            workers.Add(Task.Run(BakeWorld));
        }
    }

    /// <summary>The atlas's tile workers and the one far-terrain worker. The smoke checks call this directly.</summary>
    internal void StartWorkers(int threads)
    {
        for (int i = 0; i < threads; i++) workers.Add(Task.Run(Work));
        workers.Add(Task.Run(WorkFar));
    }

    // The full (creative) map: creative players, the wmap privilege, or everyone by setting. Asked per request, so a
    // change of game mode applies at once; the client asks for a new manifest when its game mode changes. Not
    // controlserver: a single-player host holds it, and before 0.4.5 it gave them the full map in survival.
    internal bool Allowed(IServerPlayer p) => config.AllowAllPlayers || p.HasPrivilege("wmap")
        || p.WorldData?.CurrentGameMode == EnumGameMode.Creative;
    bool MayPredict(IServerPlayer p) => Allowed(p) || config.ShareWorldChart;

    public void Hello(IServerPlayer p, AtlasHello hello)
    {
        if (disposed || source == null || hello.Protocol != WmapProtocol.Version) return;
        greeted.Add(p.PlayerUID);
        SendInfo(p);
    }

    /// <summary>The planet's radius for the horizon: a world with geography is a globe whose east-west circumference is the
    /// ring; others are flat (0).</summary>
    internal static double PlanetRadius(AtlasInfo s, double scale) => s.HasGeography && scale > 0 && s.Width > 0 ? scale * s.Width / (2 * Math.PI) : 0;

    void SendInfo(IServerPlayer p)
    {
        var s = source.Info;
        bool chart = MayPredict(p) && s.HasOverview;
        byte[] packed = basePacked;
        string id = packed != null ? baseId : null;
        // Never mutate a shared manifest's permission while serving different players.
        channel.SendPacket(new AtlasInfo { Session = s.Session, CacheId = s.CacheId, StartX = s.StartX, Width = s.Width, Height = s.Height, Wraps = s.Wraps,
            Allowed = true, CanDevelop = Allowed(p), HasOverview = s.HasOverview && Allowed(p), CanPredict = chart,
            Source = s.Source, SeaLevel = s.SeaLevel, MapHeight = s.MapHeight, HeightRange = s.HeightRange, EquatorZ = s.EquatorZ, PoleDistance = s.PoleDistance, HasGeography = s.HasGeography, GeographyProvider = s.GeographyProvider, ObservationId = s.ObservationId,
            BaseId = chart ? id : null, BaseWidth = baseWidth, BaseHeight = baseHeight, BaseBytes = chart ? packed?.Length ?? 0 : 0, BaseProgress = baseProgress, BarometerRange = config.BarometerRange, FlatPrediction = s.FlatPrediction,
            FarTiles = s.FarTiles, Biome = s.Biome, BiomeId = s.Biome ? s.BiomeId : null, Teleport = true,
            ClimateId = chart ? climateId : null, ClimateBytes = chart ? climatePacked?.Length ?? 0 : 0,
            ExploreBySight = config.ExploreBySight, SightDistance = config.SightDistance, SightCircle = config.SightCircle, SightHoleSize = config.SightHoleSize,
            PlanetRadius = PlanetRadius(s, config.PlanetScale), AutoMarks = config.AutoMarks, PaperMaps = config.PaperMaps,
            OreHeatCodes = Allowed(p) ? OreHeat?.Codes : null, OreHeatMethod = Allowed(p) ? OreHeat?.Method : null }, p);
        if (packed != null) chartAnnounced.Add(p.PlayerUID);
    }

    // Per player, the latest teleport asked for: a column still loading for an earlier click does not move them.
    readonly Dictionary<string, int> teleports = new();

    /// <summary>A right-click on the atlas: creative players with the tp privilege go to the top of that column,
    /// which is loaded (generated if need be) first.</summary>
    public void Teleport(IServerPlayer p, TeleportRequest r)
    {
        if (disposed || source == null || p.Entity == null) return;
        void Tell(string text) => p.SendMessage(GlobalConstants.GeneralChatGroup, "Wmap: " + text, EnumChatType.Notification);
        if (p.WorldData.CurrentGameMode != EnumGameMode.Creative) { Tell("teleporting from the map needs creative mode."); return; }
        if (!p.HasPrivilege(Privilege.tp)) { Tell("teleporting from the map needs the tp privilege."); return; }
        if (p.Entity.Pos.Dimension != 0) return;
        if (Core.Teleport.Column(source.Info.Topology(), r.X, r.Z, api.WorldManager.MapSizeX, api.WorldManager.MapSizeZ) is not { } column) return;
        teleports.TryGetValue(p.PlayerUID, out int previous);
        int ticket = teleports[p.PlayerUID] = previous + 1;
        Land(api, column.X, column.Z, (x, y, z) =>
        {
            if (disposed || teleports.GetValueOrDefault(p.PlayerUID) != ticket || p.ConnectionState != EnumClientState.Playing || p.Entity == null) return;
            p.Entity.TeleportToDouble(x, y, z);
        });
    }

    /// <summary>Loads the column at block x, z (generating it if need be), then gives the main thread the point
    /// just above its top block or water surface. The smoke checks call this directly.</summary>
    public static void Land(ICoreServerAPI api, int x, int z, Action<double, double, double> arrive)
    {
        void Arrive()
        {
            int top = api.World.BlockAccessor.GetRainMapHeightAt(x, z);
            arrive(x + 0.5, Math.Clamp(top + 1, 1, api.WorldManager.MapSizeY - 2), z + 0.5);
        }
        api.WorldManager.LoadChunkColumnPriority(x / 32, z / 32, new ChunkLoadOptions { OnLoaded = () => api.Event.EnqueueMainThreadTask(Arrive, "wmap-teleport-land") });
    }

    public void Request(IServerPlayer p, TileRequest r)
    {
        if (disposed || source?.Samplers == null || !MayPredict(p) || r.Session != source.Info.Session || !source.Info.Topology().Valid(new(0, r.Level, r.X, r.Z))) return;
        if (!Acceptable(r, source.Biome != null)) return;
        string key = StreamKey(p.PlayerUID, r.Stream);
        if (r.View < views.GetOrAdd(key, r.View)) return;
        views[key] = r.View;
        outstanding.TryGetValue(key, out int count);
        if (count >= StreamLimits[r.Stream]) return;
        generations.TryGetValue(p.PlayerUID, out int generation);
        var job = new Job(p.PlayerUID, r, generation);
        if (r.Stream == 0)
        {
            if (jobs.TryAdd(job)) outstanding[key] = count + 1;
            // All workers busy (many players at once): say so, so the client asks again soon instead of waiting out its timeout.
            else channel.SendPacket(new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = r.Stream, Content = r.Content, Error = TileResponse.Busy }, p);
            return;
        }
        var queue = farJobs.GetOrAdd(p.PlayerUID, _ => new());
        queue.Jobs.Enqueue(job);
        if (Interlocked.CompareExchange(ref queue.Listed, 1, 0) == 0) farTurns.Enqueue((p.PlayerUID, queue));
        outstanding[key] = count + 1;
        farSignal.Release();
    }

    /// <summary>Queues a chart chunk request. Thread-safe and cheap, so it may run on the network thread.</summary>
    public void RequestBase(IServerPlayer p, BaseRequest r)
    {
        bool climate = r.BaseId != null && r.BaseId == climateId;
        byte[] data = climate ? climatePacked : basePacked;
        if (disposed || data == null || (!climate && r.BaseId != baseId) || r.Offset < 0 || r.Offset >= data.Length || r.Offset % BaseChunk.Size != 0) return;
        var queue = chartQueues.GetOrAdd(p.PlayerUID, _ => new());
        var part = new ChartPart(climate, r.Offset);
        lock (queue) if (queue.Pending.Count < ChartPendingLimit && queue.Pending.Add(part)) queue.Offsets.Enqueue(part);
    }

    void SendChart()
    {
        if (basePacked == null) return;
        int budget = ChartChunksPerTick;
        // Start with a different player each tick, so players served first cannot starve the others.
        var queues = chartQueues.ToArray();
        if (queues.Length == 0) return;
        int start = chartRound % queues.Length;
        chartRound = (chartRound + 1) & int.MaxValue;
        for (int i = 0; i < queues.Length && budget > 0; i++)
        {
            var (uid, queue) = queues[(start + i) % queues.Length];
            lock (queue) if (queue.Offsets.Count == 0) continue;
            var player = api.World.PlayerByUid(uid) as IServerPlayer;
            // A request that arrived after its player left made a new queue: drop it with the player.
            if (player == null) { chartQueues.TryRemove(uid, out _); continue; }
            if (!MayPredict(player)) { lock (queue) { queue.Offsets.Clear(); queue.Pending.Clear(); } continue; }
            for (int n = 0; n < ChartChunksPerPlayerTick && budget > 0; n++)
            {
                ChartPart part;
                lock (queue)
                {
                    if (!queue.Offsets.TryDequeue(out part)) break;
                    queue.Pending.Remove(part);
                }
                byte[] data = part.Climate ? climatePacked : basePacked;
                if (data == null || part.Offset >= data.Length) continue;
                int offset = part.Offset, length = Math.Min(BaseChunk.Size, data.Length - offset);
                channel.SendPacket(new BaseChunk { BaseId = part.Climate ? climateId : baseId, Offset = offset, Data = data.AsSpan(offset, length).ToArray() }, player);
                budget--;
            }
        }
    }

    public void Tick()
    {
        SendChart();
        for (int i = 0; i < 16 && completed.TryTake(out var done); i++)
        {
            generations.TryGetValue(done.uid, out int generation);
            if (done.generation != generation) continue;
            string key = StreamKey(done.uid, done.response.Stream);
            if (outstanding.TryGetValue(key, out int count)) outstanding[key] = Math.Max(0, count - 1);
            if (api.World.PlayerByUid(done.uid) is not IServerPlayer player || !MayPredict(player)) continue;
            if (views.TryGetValue(key, out var view) && view == done.response.View) channel.SendPacket(done.response, player);
        }
        // Sampling can report 100% before compression/hash/cache publication finishes.
        // Announce readiness separately, including players who were still loading at publication.
        long now = Environment.TickCount64;
        if (climateFresh)
        {
            climateFresh = false;
            foreach (string uid in greeted.ToArray())
                if (api.World.PlayerByUid(uid) is IServerPlayer player && player.ConnectionState == EnumClientState.Playing) SendInfo(player);
        }
        bool ready = BaseReady;
        float progress = ready ? 1 : baseProgress;
        bool announceProgress = progress != announcedProgress && (ready || now - announcedAt > 2000);
        if (announceProgress)
        {
            announcedProgress = progress; announcedAt = now;
        }
        if (announceProgress || ready)
        {
            foreach (string uid in greeted.ToArray())
                if ((announceProgress || !chartAnnounced.Contains(uid)) && api.World.PlayerByUid(uid) is IServerPlayer player && player.ConnectionState == EnumClientState.Playing) SendInfo(player);
        }
    }

    public void Leave(string uid)
    {
        for (int stream = 0; stream < StreamLimits.Length; stream++) { views.TryRemove(StreamKey(uid, stream), out _); outstanding.Remove(StreamKey(uid, stream)); }
        generations[uid] = generations.TryGetValue(uid, out int generation) ? generation + 1 : 1;
        farJobs.TryRemove(uid, out _);
        chartQueues.TryRemove(uid, out _); greeted.Remove(uid); chartAnnounced.Remove(uid); teleports.Remove(uid);
    }

    void BakeWorld()
    {
        try
        {
            var t = source.Info.Topology();
            int width = baseWidth, height = baseHeight;
            string path = Path.Combine(cache, $"world-{BaseRaster.Version}-{width}x{height}.wmr");
            byte[] packed = null;
            if (File.Exists(path))
            {
                try { packed = File.ReadAllBytes(path); if (!Verified(path, packed)) { BaseRaster.Unpack(packed); WriteDigest(path, packed); } }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { packed = null; api.Logger.Warning("[wmap] Rebuilding damaged world chart cache: {0}", e.Message); }
            }
            if (packed == null)
            {
                var watch = Stopwatch.StartNew();
                api.Logger.Notification("[wmap] Charting the whole world once: {0} x {1} texels, {2:0.#} blocks each, {3} threads", width, height, t.Width / width, config.Threads);
                var raster = BaseRaster.Bake(t, width, height, source.Info.SeaLevel, source.Samplers, source.Rivers, config.Threads, stop.Token, p => baseProgress = (float)p, source.Info.ChartHeightRange);
                packed = raster.Pack();
                // Caching is optional: a chart that could not be written is still published.
                try { TileCodec.WriteAtomic(path, packed); WriteDigest(path, packed); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { api.Logger.Warning("[wmap] Could not cache the world chart: {0}", e.Message); }
                api.Logger.Notification("[wmap] World chart ready in {0:0.0} s ({1:0.0} MiB)", watch.Elapsed.TotalSeconds, packed.Length / 1048576.0);
            }
            baseId = Digest(packed);
            basePacked = packed;
            if (source.Biome != null) BakeClimate(t, width, height);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { api.Logger.Error("[wmap] World chart bake failed; detail tiles remain available: {0}", e); }
    }

    // A cached chart is checked by unpacking it (up to 134 MB of pixels) unless the SHA-256 written beside it when it was
    // made, or when it was last checked that way, still matches: then it is the same bytes. Clients check the hash again.
    // The digest of the last array hashed, so that checking a cache and naming it (baseId) hash it once.
    [ThreadStatic] static (byte[] Data, string Hex) lastDigest;
    static string Digest(byte[] packed)
    {
        if (lastDigest.Data == packed) return lastDigest.Hex;
        string hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packed)).ToLowerInvariant();
        lastDigest = (packed, hex);
        return hex;
    }
    static bool Verified(string path, byte[] packed)
    {
        string digest = Digest(packed);
        try { return File.ReadAllText(path + ".sha256").Trim() == digest; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
    }
    static void WriteDigest(string path, byte[] packed)
    {
        try { TileCodec.WriteAtomic(path + ".sha256", System.Text.Encoding.ASCII.GetBytes(Digest(packed))); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* Next start checks by unpacking. */ }
    }

    /// <summary>The climate raster for the atlas's Terrain colours, cached like the chart (by the biome source's identity).</summary>
    void BakeClimate(Topology t, int chartWidth, int chartHeight)
    {
        try
        {
            var (width, height) = ClimateRaster.SizeFor(chartWidth, chartHeight);
            string path = Path.Combine(cache, $"climate-{ClimateRaster.Version}-{source.Info.BiomeId}-{width}x{height}.wmr");
            byte[] packed = null;
            if (File.Exists(path))
            {
                try { packed = File.ReadAllBytes(path); if (!Verified(path, packed)) { ClimateRaster.Unpack(packed); WriteDigest(path, packed); } }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { packed = null; }
            }
            if (packed == null)
            {
                var watch = Stopwatch.StartNew();
                // On Earth's built-in heights where it has them (EarthHeights): sampled everywhere, GLO-90 decoded tiles all over
                // the globe. Far terrain's biome tiles keep source.Biome, the column's own heights.
                packed = ClimateRaster.Pack(ClimateRaster.Bake(t, width, height, source.WorldBiome ?? source.Biome, config.Threads, stop.Token), width, height);
                try { TileCodec.WriteAtomic(path, packed); WriteDigest(path, packed); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { api.Logger.Warning("[wmap] Could not cache the climate raster: {0}", e.Message); }
                api.Logger.Notification("[wmap] Climate raster {0} x {1} ready in {2:0.0} s ({3:0.0} MiB{4})", width, height, watch.Elapsed.TotalSeconds, packed.Length / 1048576.0,
                    source.WorldBiome != null ? ", on Earth's built-in heights" : "");
            }
            climateId = Digest(packed);
            climatePacked = packed;
            climateFresh = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { api.Logger.Error("[wmap] Climate raster bake failed; Terrain colours stay by height: {0}", e); }
    }

    void Work()
    {
        var sample = source.Samplers?.Invoke();
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable(stop.Token)) if (!Process(job, sample)) break;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The far-terrain worker: one at a time, players in turn.</summary>
    void WorkFar()
    {
        var sample = source.Samplers?.Invoke();
        try
        {
            while (true)
            {
                farSignal.Wait(stop.Token);
                // The next player in turn does one job and goes to the back while it has more; players without
                // work are not listed, so they cannot shift the turns. A player who left took their queue along.
                if (!farTurns.TryDequeue(out var turn) || !farJobs.TryGetValue(turn.Uid, out var queue) || queue != turn.Queue) continue;
                queue.Jobs.TryDequeue(out var job);
                if (!queue.Jobs.IsEmpty) farTurns.Enqueue(turn);
                else
                {
                    Volatile.Write(ref queue.Listed, 0);
                    // Work that arrived between the check and the reset lists the player again.
                    if (!queue.Jobs.IsEmpty && Interlocked.CompareExchange(ref queue.Listed, 1, 0) == 0) farTurns.Enqueue(turn);
                }
                if (job != null && !Process(job, sample)) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Answers one tile request; false once the server is stopping.</summary>
    bool Process(Job job, Func<double, double, MapSample> sample)
    {
        var t = source.Info.Topology();
        var r = job.Request;
        var response = new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = r.Stream, Content = r.Content };
        try
        {
            if (!views.TryGetValue(StreamKey(job.Uid, r.Stream), out int view) || view != r.View) { response.Error = "obsolete"; return true; }
            if (sample == null) { response.Error = "No overview provider"; return true; }
            var key = new TileKey(0, r.Level, r.X, r.Z);
            if (TileContent.HasSemantic(r.Content))
                response.Data = Cached(Path.Combine(cache, key.FileName), SemanticTile.MaxCompressedBytes, b => SemanticTile.Unpack(b),
                    () => SemanticTile.Pack(SemanticTile.Sample(t, key, sample, source.Info.SeaLevel, stop.Token, sampled.GetValue(source, _ => new SemanticTile.SampleCache()), source.Info.ChartHeightRange)));
            // Without a biome sampler a near-band request gets its semantic tile alone.
            if (TileContent.HasBiome(r.Content) && source.Biome != null)
                response.Biome = Cached(Path.Combine(cache, BiomeTile.FileName(key, source.Info.BiomeId)), BiomeTile.MaxCompressedBytes, b => BiomeTile.Unpack(b),
                    () => BiomeTile.Pack(BiomeTile.Sample(t, key, source.Biome, stop.Token)));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) { response.Error = e.GetType().Name + ": " + e.Message; return true; }
        finally { if (!disposed) try { completed.Add((job.Uid, job.Generation, response), stop.Token); } catch (OperationCanceledException) { } }
    }

    // Neighbouring prediction tiles overlap by their sampling margins: the samples a tile took are kept for its
    // neighbours (W1-11), per terrain source.
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSource, SemanticTile.SampleCache> sampled = new();

    /// <summary>A derived tile from the disk cache, or made and cached. Damaged cache files are made again.</summary>
    byte[] Cached(string path, int maxBytes, Action<byte[]> validate, Func<byte[]> make)
    {
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > maxBytes) throw new InvalidDataException("Oversized cache tile");
                var bytes = File.ReadAllBytes(path); validate(bytes); return bytes;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { /* Regenerate damaged or unreadable derived data. */ }
        }
        // One maker per tile: an atlas and a far-terrain request, or two players, for the same tile share the work.
        var lazy = making.GetOrAdd(path, _ => new Lazy<byte[]>(() =>
        {
            var made = make();
            try { TileCodec.WriteAtomic(path, made); Wrote(cache, made.Length, config.DiskCacheMiB); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* Disk caching is optional; the tile is still usable. */ }
            return made;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        finally { making.TryRemove(new KeyValuePair<string, Lazy<byte[]>>(path, lazy)); }
    }

    // Each cache folder's trimmed tiles in bytes (-1 until first counted), and whether a trim of it is running.
    // Bytes and RetryAt are used only by the writer holding Trimming; the others add to During.
    sealed class CacheSize { public long Bytes = -1, During, RetryAt; public int Trimming; }
    static readonly ConcurrentDictionary<string, CacheSize> cacheSizes = new();

    /// <summary>Counts a predicted tile written to a cache folder. The folder is scanned once, when first counted, then
    /// only when the count passes <paramref name="mib"/>: it is trimmed to 90 % of that. One trim of a folder at a time,
    /// for every writer sharing it; the others do not wait, and what they wrote meanwhile is counted after it. Rewritten
    /// files, and files written while a scan ran, may count twice, which only trims a little sooner. A trim that cannot
    /// get under the limit (files in use or read-only) is not tried again for a minute.</summary>
    public static void Wrote(string folder, long bytes, int mib)
    {
        var size = cacheSizes.GetOrAdd(Path.GetFullPath(folder), _ => new());
        if (Interlocked.CompareExchange(ref size.Trimming, 1, 0) != 0) { Interlocked.Add(ref size.During, bytes); return; }
        long limit = (long)mib << 20, now = Environment.TickCount64;
        try
        {
            long added = bytes + Interlocked.Exchange(ref size.During, 0);
            if (size.Bytes >= 0 && (size.Bytes += added) <= limit || now < size.RetryAt) return;
            // The scan counts what was written before it; what is written while it runs is added after.
            long left = TrimCache(folder, mib, 0.9);
            size.Bytes = left + Interlocked.Exchange(ref size.During, 0);
            size.RetryAt = left > limit ? now + 60_000 : 0;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { size.RetryAt = now + 60_000; }
        finally { Volatile.Write(ref size.Trimming, 0); }
    }

    /// <summary>Deletes the oldest predicted tiles while they take more than <paramref name="mib"/>, down to
    /// <paramref name="to"/> of it. Returns the bytes they take afterwards.</summary>
    public static long TrimCache(string folder, int mib, double to = 1)
    {
        if (!Directory.Exists(folder)) return 0;
        // Only predicted tiles (and ore heat tiles, WM-D48) are trimmed; the world chart and recorded observations are kept.
        var dir = new DirectoryInfo(folder);
        var files = dir.EnumerateFiles("0-*.wmt").Concat(dir.EnumerateFiles("b-*.wmt")).Concat(dir.EnumerateFiles("o-*.wmt")).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length), limit = (long)mib * 1024 * 1024;
        if (total <= limit) return total;
        long target = (long)(limit * to);
        foreach (var f in files)
        {
            if (total <= target) break;
            // A file another trim removed, one in use or read-only: skip it; trimming must not fail the tile being made.
            try { long size = f.Length; f.Delete(); total -= size; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        return total;
    }

    public void Dispose() { disposed = true; stop.Cancel(); jobs.CompleteAdding(); /* Workers own no game API calls and observe cancellation per tile row. */ }
}
