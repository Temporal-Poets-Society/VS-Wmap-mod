using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Server;
using Wmap.Core;

namespace Wmap.Server;

/// <summary>
/// What each player has charted, and what their client has been sent of the prediction (0.5.4, WM-D63; the author: "unexplored
/// map should be available only in creative mode"). The server casts each player's line of sight from its own world chart, as
/// clients did before, and keeps their charted cells in the world save. A survival client gets the chart and the climate raster
/// only in blocks of land it has seen (<see cref="RevealGate"/>): what it has charted, and for the far view what lies in its
/// line of sight out to <see cref="FarSight"/>; tiles of other land come back blanked
/// (<see cref="KnownFor"/>). Players with the full map get everything, as before; their charted cells are kept too (paper
/// maps, a later switch to survival). Main thread, except the sight casts and the block packing, which run on workers.
/// </summary>
public sealed class ExplorationServer : IDisposable
{
    const string SaveKeyPrefix = "wmap-charted-";
    const double EyeHeight = 1.6;
    /// <summary>Blocks a player moves before their sight is cast again (as clients cast it before 0.5.4); the far sight, which
    /// reaches farther, waits for more.</summary>
    public const double Move = 32, FarMove = 256;
    /// <summary>Chart and climate blocks sent per server tick (50 ms), per player and in all.</summary>
    public const int BlocksPerPlayerTick = 8, BlocksPerTick = 32;
    /// <summary>Switch point (the author, 15 November 2026, WM-D63: "horizon of course stays. You need to be closer to make a
    /// map."; a constant, not a setting): blocks of land in a survival player's line of sight their client gets for the far
    /// view, which is what they see on the horizon. Their charting stays at the sight distance. 0 would send charted land
    /// only.</summary>
    public const double FarSight = 8192;
    /// <summary>Milliseconds a greeting with no change of mode waits after the last one: each sends the player's charted
    /// area and packs their blocks again, so a client cannot keep the server doing that.</summary>
    const long HelloInterval = 2000;

    readonly ICoreServerAPI api;
    readonly IServerNetworkChannel channel;
    readonly WmapConfig config;
    readonly Func<IServerPlayer, bool> full;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim packSignal = new(0);
    readonly Task packer;
    MaskGrid grid;
    string session;
    double heightRange = Semantic.DefaultHeightRange, planetRadius;
    bool predicts;
    volatile BaseRaster chart, climate;
    volatile HeightGrid heights;
    Task<HeightGrid> heightTask;
    readonly ConcurrentDictionary<string, Player> players = new();
    readonly List<(IServerPlayer P, Player Player)> sending = new();
    int turn;
    volatile bool disposed;

    sealed class Player
    {
        public CellSet Charted;
        /// <summary>What the client has been given; replaced at a greeting while the packer may read it.</summary>
        public volatile CellSet Known;
        public bool Full;
        /// <summary>A sight cast failed: logged once, and cast again from <see cref="CastAt"/>.</summary>
        public bool CastFailed;
        public long CastAt;
        public double X = double.NaN, Y, Z, FarX = double.NaN, FarZ;
        /// <summary>The last sight was cast without the chart's heights, though sight was wanted: cast again once they are here.</summary>
        public bool CastBlind;
        public Task<(int[] Charted, int[] InView)> Cast;
        /// <summary>When the last greeting was answered, and whether one waits (<see cref="HelloInterval"/>).</summary>
        public long HelloAt = long.MinValue;
        public bool HelloDue;
        /// <summary>Charted cells not yet sent, and whether the charted set changed since it was saved.</summary>
        public readonly List<int> Unsent = new();
        public bool Unsaved;
        /// <summary>Blocks to (re)pack for the client, under its own lock; packed messages wait in <see cref="Outgoing"/>.</summary>
        public readonly HashSet<(bool Climate, int X, int Z)> Dirty = new();
        public readonly ConcurrentQueue<ChartPatch> Outgoing = new();
        public int Generation;
        /// <summary>The packer's own: per block, a hash of what it last sent and for which greeting. A block marked again whose
        /// texels did not change is not sent again.</summary>
        public readonly Dictionary<(bool Climate, int X, int Z), (int Generation, ulong Hash)> Sent = new();
    }

    public ExplorationServer(ICoreServerAPI api, IServerNetworkChannel channel, WmapConfig config, Func<IServerPlayer, bool> full)
    {
        this.api = api; this.channel = channel; this.config = config; this.full = full;
        packer = Task.Run(Pack);
    }

    /// <summary>The grid and session of this world's atlas (<see cref="AtlasServer.Start"/>).</summary>
    public void Start(AtlasInfo info, double planetRadius, bool predicts)
    {
        grid = new MaskGrid(info.Topology());
        session = info.Session;
        heightRange = info.ChartHeightRange;
        this.planetRadius = planetRadius;
        this.predicts = predicts;
    }

    public MaskGrid Grid => grid;

    /// <summary>The world chart, once baked (any thread): sight is cast from its heights, survival clients get its blocks.</summary>
    public void SetChart(BaseRaster raster)
    {
        if (grid == null || raster == null) return;
        chart = raster;
        var t = grid.Topology; double range = heightRange;
        heightTask = Task.Run(() => HeightGrid.FromRaster(raster, t, range));
        foreach (var p in players.Values) MarkAll(p, climate: false);
    }

    /// <summary>The climate raster, once baked (any thread).</summary>
    public void SetClimate(BaseRaster raster)
    {
        if (grid == null || raster == null) return;
        climate = raster;
        foreach (var p in players.Values) MarkAll(p, climate: true);
    }

    /// <summary>A player greeted the atlas, at the start or after a change of game mode (main thread): their charted cells are
    /// loaded the first time and sent again, and a survival client's blocks again from nothing (its client starts from an
    /// empty raster when its manifest turns explored on). A greeting soon after another with no change of mode is answered
    /// when <see cref="HelloInterval"/> has passed.</summary>
    public void Hello(IServerPlayer p)
    {
        if (grid == null || disposed) return;
        var player = players.GetOrAdd(p.PlayerUID, _ => Load(p.PlayerUID));
        bool isFull = full(p);
        if (isFull == player.Full && player.HelloAt != long.MinValue && Environment.TickCount64 - player.HelloAt < HelloInterval) { player.HelloDue = true; return; }
        Greet(p, player, isFull);
    }

    void Greet(IServerPlayer p, Player player, bool isFull)
    {
        player.HelloAt = Environment.TickCount64; player.HelloDue = false;
        player.Full = isFull;
        lock (player.Dirty) { player.Dirty.Clear(); player.Generation++; }
        while (player.Outgoing.TryDequeue(out _)) { }
        player.Unsent.Clear();
        // What is in view is known again from nothing: sight is cast again at once, the far sight too.
        player.X = player.FarX = double.NaN;
        channel.SendPacket(new ExploreUpdate { Session = session, Cells = player.Charted.Pack(), Reset = true }, p);
        // Copied and marked by the set's blocks, not cell by cell: a player who has charted a continent greets cheaply too.
        player.Known = player.Full ? new CellSet(grid.Width, grid.Height) : player.Charted.Clone();
        if (player.Full) return;
        var boxes = player.Known.Boxes();
        Mark(player, boxes, climate: false);
        Mark(player, boxes, climate: true);
    }

    /// <summary>A player's charted cells from the world save. Switch point (the author, 15 November 2026, WM-D63: "this release
    /// does not support old saves"): worlds from before 0.5.4 kept each player's charted area on their own client
    /// (<c>explored.wmx</c>), which a client could have written itself; nothing is taken over from there, and such worlds are
    /// not supported (start a new world). Taking the old areas over would mean accepting each client's mask once, here.</summary>
    Player Load(string uid)
    {
        var player = new Player();
        byte[] saved = null;
        try { saved = api.WorldManager.SaveGame.GetData(SaveKeyPrefix + uid); } catch (Exception e) { api.Logger.Warning("[wmap] Could not read {0}'s charted area: {1}", uid, e.Message); }
        try { player.Charted = saved != null ? CellSet.Unpack(saved, grid.Width, grid.Height) : null; }
        catch (InvalidDataException e) { api.Logger.Warning("[wmap] {0}'s charted area is damaged and starts again: {1}", uid, e.Message); }
        player.Charted ??= new CellSet(grid.Width, grid.Height);
        player.Known = new CellSet(grid.Width, grid.Height);
        return player;
    }

    /// <summary>The player left (main thread): their charted cells are saved and their state dropped.</summary>
    public void Leave(string uid)
    {
        if (players.TryRemove(uid, out var player)) Save(uid, player);
    }

    /// <summary>The world is being saved (main thread): every player's charted cells that changed.</summary>
    public void Save()
    {
        foreach (var (uid, player) in players) Save(uid, player);
    }

    void Save(string uid, Player player)
    {
        if (!player.Unsaved || grid == null) return;
        try { api.WorldManager.SaveGame.StoreData(SaveKeyPrefix + uid, player.Charted.Pack()); player.Unsaved = false; }
        catch (Exception e) { api.Logger.Warning("[wmap] Could not save {0}'s charted area: {1}", uid, e.Message); }
    }

    /// <summary>For a tile request (any thread): whether a cell's prediction may go to this player; null when it all may (the
    /// full map). A player not known here gets nothing.</summary>
    public Func<int, bool> KnownFor(IServerPlayer p)
    {
        if (full(p)) return null;
        return players.TryGetValue(p.PlayerUID, out var player) ? player.Known.Contains : static _ => false;
    }

    /// <summary>The charted area as a paper map holds it (<c>ExploredMask.Pack</c>), from the server's own record; null without
    /// one. Any thread; it makes a byte for every cell of the grid, so not on the main thread.</summary>
    public byte[] ChartedMask(string uid, out int cells)
    {
        cells = 0;
        if (grid == null || !players.TryGetValue(uid, out var player) || player.Charted.Count == 0) return null;
        var data = new byte[grid.Count];
        foreach (int i in player.Charted.Cells()) data[i] = 255;
        cells = player.Charted.Count;
        return Client.ExploredMask.Pack(data, grid.Width, grid.Height);
    }

    /// <summary>The cells of a paper map's charted area (mask texels, one byte a cell); null if it is not of this grid. Any
    /// thread: it reads the whole grid, so not on the main thread.</summary>
    public int[] CellsOf(byte[] texels)
    {
        var g = grid;
        if (g == null || texels == null || texels.Length != g.Count) return null;
        var cells = new List<int>();
        for (int i = 0; i < texels.Length; i++) if (texels[i] == 255) cells.Add(i);
        return cells.ToArray();
    }

    /// <summary>A read paper map's charted cells (<see cref="CellsOf"/>) join the reader's (main thread).</summary>
    public void Merge(IServerPlayer p, IReadOnlyList<int> cells)
    {
        if (grid == null || cells == null || !players.TryGetValue(p.PlayerUID, out var player)) return;
        Reveal(player, cells, Array.Empty<int>());
    }

    /// <summary>Main thread, every server tick: casts sight where players moved, takes the casts that finished, and sends what
    /// is new.</summary>
    public void Tick()
    {
        if (grid == null || disposed) return;
        if (heightTask?.IsCompleted == true)
        {
            if (heightTask.Status == TaskStatus.RanToCompletion) heights = heightTask.Result;
            else api.Logger.Warning("[wmap] The chart's heights for line of sight could not be built: {0}", heightTask.Exception?.InnerException?.Message);
            heightTask = null;
        }
        sending.Clear();
        foreach (var (uid, player) in players)
        {
            if (api.World.PlayerByUid(uid) is not IServerPlayer p || p.ConnectionState != EnumClientState.Playing) continue;
            if (player.HelloDue && Environment.TickCount64 - player.HelloAt >= HelloInterval) Greet(p, player, full(p));
            if (player.Cast?.IsCompleted == true)
            {
                if (player.Cast.Status == TaskStatus.RanToCompletion) Reveal(player, player.Cast.Result.Charted, player.Cast.Result.InView);
                else if (player.Cast.IsFaulted)
                {
                    // Cast again, the far sight too, after a second; said once.
                    player.X = player.FarX = double.NaN;
                    player.CastAt = Environment.TickCount64 + 1000;
                    if (!player.CastFailed) api.Logger.Warning("[wmap] A sight cast for {0} failed: {1}", uid, player.Cast.Exception?.InnerException?.Message);
                    player.CastFailed = true;
                }
                player.Cast = null;
            }
            if (player.Cast == null && Environment.TickCount64 >= player.CastAt) StartCast(p, player);
            if (player.Unsent.Count > 0)
            {
                var update = new CellSet(grid.Width, grid.Height);
                update.AddRange(player.Unsent);
                player.Unsent.Clear();
                channel.SendPacket(new ExploreUpdate { Session = session, Cells = update.Pack() }, p);
            }
            if (!player.Outgoing.IsEmpty) sending.Add((p, player));
        }
        // The blocks' budget starts with another player each tick, so those first in the list cannot keep it.
        int budget = BlocksPerTick;
        for (int k = 0; k < sending.Count && budget > 0; k++)
        {
            var (p, player) = sending[(k + turn) % sending.Count];
            for (int n = 0; n < BlocksPerPlayerTick && budget > 0 && player.Outgoing.TryDequeue(out var patch); n++, budget--)
                channel.SendPacket(patch, p);
        }
        turn = sending.Count > 0 ? (turn + 1) % sending.Count : 0;
    }

    void StartCast(IServerPlayer p, Player player)
    {
        var pos = p.Entity?.Pos;
        if (pos == null || pos.Dimension != 0) return;
        double x = pos.X, y = pos.Y, z = pos.Z;
        var t = grid.Topology;
        var h = heights;
        bool moved = double.IsNaN(player.X) || Math.Abs(t.Delta(player.X, x)) >= Move || Math.Abs(z - player.Z) >= Move || Math.Abs(y - player.Y) >= 3
            || (player.CastBlind && h != null);
        bool far = !player.Full && FarSight > config.SightDistance
            && (double.IsNaN(player.FarX) || Math.Abs(t.Delta(player.FarX, x)) >= FarMove || Math.Abs(z - player.FarZ) >= FarMove);
        if (!moved && !(far && h != null)) return;
        player.X = x; player.Y = y; player.Z = z;
        if (far && h != null) { player.FarX = x; player.FarZ = z; }
        // Sight wanted but the chart's heights not here yet (the bake runs): the circle now, sight when they come.
        player.CastBlind = config.ExploreBySight && predicts && h == null;
        var g = grid; var charted = player.Charted; var known = player.Full ? null : player.Known;
        bool sight = config.ExploreBySight && h != null;
        // By sight: the circle around the player, and what the eye sees once the chart's heights are here. Without sight (the
        // server's choice) or without a prediction to see over: the explore circle.
        double circle = config.ExploreBySight && predicts ? Math.Max(16, config.SightCircle) : config.ExploreRadius;
        double eye = y + EyeHeight - (api.World.SeaLevel - 1), step = Math.Min(g.TexelX, g.TexelZ) / 2, radius = planetRadius;
        double distance = Math.Max(512, config.SightDistance), farDistance = FarSight;
        int holes = (int)(Math.Max(0, config.SightHoleSize) * (double)Math.Max(0, config.SightHoleSize) / (g.TexelX * g.TexelZ));
        bool castFar = far && h != null;
        player.Cast = Task.Run(() =>
        {
            var cells = g.Disc(x, z, circle);
            if (sight) cells.AddRange(g.SightCells(h, x, z, eye, distance, step, charted.Contains, holes, radius));
            // A cell whose centre is hidden may still show its near part (cells are as coarse as the chart's texels, 489 blocks on
            // a 1:10 Earth), and far terrain draws it from the same chart: the cells beside those in sight count as seen too, or
            // the far view had holes of sky where a slope dips out of sight (seen on the bench, 15 Nov 2026).
            int[] inView = castFar ? g.WithNeighbours(g.SightCells(h, x, z, eye, farDistance, step * 2, null, 0, radius)).ToArray() : Array.Empty<int>();
            // Only what is new goes to the main thread: a far cast sees hundreds of thousands of cells, nearly all known already.
            // (A greeting meanwhile replaces the known set; it casts again too.)
            return (charted.Missing(cells).ToArray(), known != null ? known.Missing(inView).ToArray() : Array.Empty<int>());
        }, stop.Token);
    }

    /// <summary>Charts cells for a player (main thread): new ones go to their client; for a survival client the cells, and
    /// those in view, become known and their blocks are packed.</summary>
    void Reveal(Player player, IReadOnlyList<int> charted, IReadOnlyList<int> inView)
    {
        int before = player.Unsent.Count;
        player.Charted.AddRange(charted, player.Unsent);
        if (player.Unsent.Count > before) player.Unsaved = true;
        if (player.Full) return;
        var known = new List<int>();
        player.Known.AddRange(charted, known);
        player.Known.AddRange(inView, known);
        if (known.Count == 0) return;
        // Marked by the rectangles of their blocks: the first far cast after a greeting brings hundreds of thousands.
        var fresh = new CellSet(grid.Width, grid.Height);
        fresh.AddRange(known);
        var boxes = fresh.Boxes();
        Mark(player, boxes, climate: false);
        Mark(player, boxes, climate: true);
    }

    void MarkAll(Player player, bool climate)
    {
        if (player.Full) return;
        Mark(player, player.Known.Boxes(), climate);
    }

    /// <summary>Marks the blocks that hold rectangles of cells for packing. A chart block also changes when land within the
    /// coast reach of it becomes known (its coast distances, <see cref="RevealGate.CoastReach"/>), so those round it are
    /// marked too; one without known texels costs the packer almost nothing.</summary>
    void Mark(Player player, IEnumerable<(int X0, int Z0, int X1, int Z1)> boxes, bool climate)
    {
        var raster = climate ? this.climate : chart;
        if (raster == null) return;
        int size = RevealGate.BlockSize, blocksX = (raster.Width + size - 1) / size, blocksZ = (raster.Height + size - 1) / size;
        int margin = climate ? 1 : RevealGate.CoastReach + 1;
        // Gathered first: neighbouring cells share blocks, and the set is locked once.
        var touched = new bool[blocksX * blocksZ];
        foreach (var (x0, z0, x1, z1) in boxes)
            RevealGate.BlocksOfCells(x0, z0, x1, z1, grid, raster.Width, raster.Height, (bx, bz) => touched[bz * blocksX + bx] = true, margin);
        bool any = false;
        lock (player.Dirty)
            for (int b = 0; b < touched.Length; b++)
                if (touched[b]) any |= player.Dirty.Add((climate, b % blocksX, b / blocksX));
        if (any) packSignal.Release();
    }

    /// <summary>The packing worker: takes dirty blocks player by player and packs them with what that player knows now.</summary>
    void Pack()
    {
        var batch = new List<(bool Climate, int X, int Z)>();
        try
        {
            while (true)
            {
                packSignal.Wait(stop.Token);
                bool more = true;
                while (more && !disposed)
                {
                    more = false;
                    foreach (var player in players.Values)
                    {
                        int generation;
                        batch.Clear();
                        lock (player.Dirty)
                        {
                            generation = player.Generation;
                            foreach (var b in player.Dirty) { batch.Add(b); if (batch.Count == 16) break; }
                            foreach (var b in batch) player.Dirty.Remove(b);
                            more |= player.Dirty.Count > 0;
                        }
                        // A client far behind waits: blocks are packed when it has taken most of what is queued.
                        if (player.Outgoing.Count > 256) { lock (player.Dirty) foreach (var b in batch) player.Dirty.Add(b); more = true; continue; }
                        var known = player.Known;
                        foreach (var (isClimate, bx, bz) in batch)
                        {
                            var raster = isClimate ? climate : chart;
                            if (raster == null) continue;
                            byte[] data = RevealGate.PackBlock(raster, bx, bz, (tx, tz) => RevealGate.Texel(tx, tz, raster.Width, raster.Height, grid, known.Contains, isClimate),
                                chart: !isClimate, wraps: grid.Wraps);
                            if (data == null) continue;
                            // The same texels as last sent to this client: nothing to send.
                            ulong hash = Fnv(data);
                            if (player.Sent.TryGetValue((isClimate, bx, bz), out var last) && last.Generation == generation && last.Hash == hash) continue;
                            lock (player.Dirty)
                            {
                                // A block packed for a client the player has since replaced (a new greeting) is not sent.
                                if (player.Generation != generation) break;
                                player.Outgoing.Enqueue(new ChartPatch { Session = session, Climate = isClimate, BlockX = bx, BlockZ = bz, Data = data });
                            }
                            player.Sent[(isClimate, bx, bz)] = (generation, hash);
                        }
                    }
                    if (more) Thread.Sleep(20);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e) { api.Logger.Error("[wmap] Packing chart blocks for survival clients stopped: {0}", e); }
    }

    /// <summary>FNV-1a, 64 bits: a block's fingerprint (a collision would keep one block's old texels until it changes again).</summary>
    static ulong Fnv(byte[] data)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    public void Dispose()
    {
        disposed = true;
        stop.Cancel();
    }
}
