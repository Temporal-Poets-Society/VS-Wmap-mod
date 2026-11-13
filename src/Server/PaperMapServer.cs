using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Wmap.Client;
using Wmap.Core;

namespace Wmap.Server;

/// <summary>
/// Paper maps (WM-P05, docs/paper-maps.md). A player holding a blank map draws what they have discovered: their client
/// uploads its charted area and recorded terrain, checked here for format; this adds the drawer's waypoints and readings
/// from the server's own lists, keeps the whole in the world save and turns the blank map into a drawn one carrying only
/// the map's id. A player holding a drawn map reads it: their waypoints and readings are made here, as the game's own,
/// and the charted area and terrain are sent to their client, which merges them.
/// </summary>
public sealed class PaperMapServer
{
    public const string BlankCode = "papermap-blank", DrawnCode = "papermap-drawn";
    /// <summary>The drawn map's attributes: the map's id and what its tooltip and reading dialog show.</summary>
    public const string Attribute = "wmapMap";
    /// <summary>Download pieces sent a player a tick (50 ms), and to all players together.</summary>
    public const int PiecesPerTick = 8, PiecesPerTickTotal = 16;
    /// <summary>Milliseconds between the starts of one player's drawings, and between two reads of theirs.</summary>
    public long DrawInterval = 5000, ReadInterval = 3000;
    /// <summary>An upload that receives nothing for this long is dropped (milliseconds); so is one still unfinished
    /// <see cref="UploadGrace"/> after it would have taken at <see cref="UploadRate"/> bytes a second.</summary>
    public const long UploadTimeout = 60_000, UploadGrace = 30_000, UploadRate = 1 << 20;
    readonly ICoreServerAPI api;
    readonly IServerNetworkChannel channel;
    readonly WmapConfig config;
    readonly System.Func<Topology> topology;
    /// <summary>The slot a player draws or reads from: the active hotbar slot. The smoke check's players have no inventory.</summary>
    public System.Func<IServerPlayer, ItemSlot> Held = p => p.InventoryManager?.ActiveHotbarSlot;
    /// <summary>Tells a player what happened, in chat.</summary>
    public Action<IServerPlayer, string> Tell = (p, text) => p.SendMessage(GlobalConstants.GeneralChatGroup, text, EnumChatType.Notification);
    /// <summary>Runs a drawing's check off the main thread and its end on it; the smoke check runs both at once.</summary>
    public Action<System.Func<object>, Action<object>> Background;
    // An upload keeps the pieces as they arrive (the received arrays themselves): memory follows what was sent, not
    // what a drawing announced.
    sealed class Upload { public MapDraw Draw; public byte[][] Pieces; public int Received, Released, Generation; public long Last, Deadline; }
    readonly ConcurrentDictionary<string, Upload> uploads = new();
    readonly object drawLock = new();
    readonly Dictionary<string, long> drawStarts = new(), toldAt = new();
    long uploading;
    sealed class Download { public IServerPlayer Player; public string Id; public byte[] Data; public int Offset; }
    readonly Dictionary<string, Download> downloads = new();
    readonly Dictionary<string, long> readAt = new();
    // Each player's latest accepted read: an earlier one that finishes later is dropped.
    readonly Dictionary<string, int> reads = new();
    // Bumped when a player leaves: the end of a drawing or reading that finishes after it is dropped.
    readonly ConcurrentDictionary<string, int> generations = new();
    volatile bool ended;
    int rotation;

    public PaperMapServer(ICoreServerAPI api, IServerNetworkChannel channel, WmapConfig config, System.Func<Topology> topology)
    {
        this.api = api; this.channel = channel; this.config = config; this.topology = topology;
        Background = (work, done) => Task.Run(() =>
        {
            object result;
            try { result = work(); } catch (Exception e) { result = e; }
            api.Event.EnqueueMainThreadTask(() => done(result), "wmap-papermap");
        });
    }

    WaypointMapLayer Waypoints => api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault();
    OreMapLayer Readings => api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers?.OfType<OreMapLayer>().FirstOrDefault();
    int MaxBytes => config.PaperMapMaxMiB << 20;
    /// <summary>Bytes announced by all uploads in progress together.</summary>
    long MaxUploading => 4L * MaxBytes;
    int Generation(string uid) => generations.TryGetValue(uid, out int g) ? g : 0;
    static bool Is(ItemSlot slot, string code) => slot?.Itemstack?.Collectible?.Code is { } c && c.Domain == "wmap" && c.Path == code;

    /// <summary>A drawing starts: the upload of <see cref="MapDraw.Total"/> bytes follows. Any thread, as the pieces: they
    /// may arrive before a main-thread task would have run. One drawing a player at a time, at most one start every
    /// <see cref="DrawInterval"/>, and <see cref="MaxUploading"/> announced bytes in all. Whether the player holds a blank
    /// map is checked on the main thread at once (dropping the upload) and again at the end.</summary>
    public void Draw(IServerPlayer player, MapDraw draw)
    {
        if (!config.PaperMaps || draw?.Id == null || draw.Id.Length > 64 || draw.Total < 0) return;
        string uid = player.PlayerUID;
        long now = Environment.TickCount64;
        Upload upload;
        lock (drawLock)
        {
            if (drawStarts.TryGetValue(uid, out long last) && now - last < DrawInterval) { Refuse(player, draw.Id, "Wmap: wait a moment before drawing again."); return; }
            drawStarts[uid] = now;
            if (uploads.TryRemove(uid, out var previous)) Release(previous);
            if (draw.Total > MaxBytes)
            {
                Refuse(player, draw.Id, $"Wmap: that map would be {draw.Total / 1048576.0:0.0} MiB, over the server's {config.PaperMapMaxMiB} MiB. Leave out the recorded terrain.");
                return;
            }
            if (Interlocked.Read(ref uploading) + draw.Total > MaxUploading) { Refuse(player, draw.Id, "Wmap: the server is busy with other maps; try again shortly."); return; }
            Interlocked.Add(ref uploading, draw.Total);
            upload = new Upload
            {
                Draw = draw, Pieces = new byte[(draw.Total + MapPiece.Size - 1) / MapPiece.Size][], Last = now,
                Deadline = now + draw.Total * 1000L / UploadRate + UploadGrace, Generation = Generation(uid)
            };
            uploads[uid] = upload;
        }
        if (draw.Total == 0) { if (uploads.TryRemove(new KeyValuePair<string, Upload>(uid, upload))) Uploaded(player, upload); return; }
        api.Event.EnqueueMainThreadTask(() =>
        {
            if (!ended && !Is(Held(player), BlankCode) && uploads.TryRemove(new KeyValuePair<string, Upload>(uid, upload)))
            {
                Release(upload);
                Tell(player, "Wmap: hold a blank map to draw on.");
                Stop(player, draw.Id);
            }
        }, "wmap-map-draw");
    }

    /// <summary>Stops the client's upload of every refused drawing, and tells why at most once per
    /// <see cref="DrawInterval"/> a player. A player has at most one such main-thread task waiting: refusals arriving
    /// meanwhile only change the drawing it stops. Under the draw lock.</summary>
    void Refuse(IServerPlayer player, string id, string text)
    {
        string uid = player.PlayerUID;
        long now = Environment.TickCount64;
        bool tell = !(toldAt.TryGetValue(uid, out long last) && now - last < DrawInterval);
        if (tell) toldAt[uid] = now;
        bool queued = refusals.ContainsKey(uid);
        refusals[uid] = (id, tell ? text : refusals.GetValueOrDefault(uid).Text);
        if (queued) return;
        api.Event.EnqueueMainThreadTask(() =>
        {
            (string Id, string Text) refusal;
            lock (drawLock) { if (!refusals.Remove(uid, out refusal)) return; }
            if (ended) return;
            if (refusal.Text != null) Tell(player, refusal.Text);
            Stop(player, refusal.Id);
        }, "wmap-map-draw");
    }
    // Per player, under the draw lock: the refused drawing to stop and what to tell (null: told recently).
    readonly Dictionary<string, (string Id, string Text)> refusals = new();

    /// <summary>Main thread: tells the drawer's client to stop uploading a drawing the server will not take (a piece with
    /// <see cref="MapPiece.Total"/> -1 and no data; clients before this ignore it).</summary>
    void Stop(IServerPlayer player, string id) => channel.SendPacket(new MapPiece { Id = id, Total = -1 }, player);

    void Release(Upload upload)
    {
        if (Interlocked.Exchange(ref upload.Released, 1) == 0) Interlocked.Add(ref uploading, -upload.Draw.Total);
    }

    /// <summary>A piece of a drawing's upload. Any thread: pieces are put together without main-thread work.</summary>
    public void Piece(IServerPlayer player, MapPiece piece)
    {
        if (piece?.Data == null || !uploads.TryGetValue(player.PlayerUID, out var upload) || piece.Id != upload.Draw.Id) return;
        lock (upload)
        {
            int total = upload.Draw.Total, length = Math.Min(MapPiece.Size, total - piece.Offset);
            if (piece.Total != total || piece.Offset < 0 || piece.Offset % MapPiece.Size != 0 || length <= 0 || piece.Data.Length != length) return;
            int index = piece.Offset / MapPiece.Size;
            if (upload.Pieces[index] != null) return;
            upload.Pieces[index] = piece.Data;
            upload.Received += length;
            upload.Last = Environment.TickCount64;
            if (upload.Received < total) return;
        }
        if (uploads.TryRemove(new KeyValuePair<string, Upload>(player.PlayerUID, upload))) Uploaded(player, upload);
    }

    void Uploaded(IServerPlayer player, Upload upload)
    {
        var t = topology();
        if (t == null) { Release(upload); return; }
        var (w, h) = ExploredMask.Dimensions(t);
        // The generation when the drawing was admitted: a player who left since, even before this ran, gets nothing.
        int generation = upload.Generation;
        Background(() =>
        {
            try
            {
                if (upload.Draw.Total == 0) return new PaperMap();
                byte[] data = new byte[upload.Draw.Total];
                for (int i = 0; i < upload.Pieces.Length; i++) Buffer.BlockCopy(upload.Pieces[i], 0, data, i * MapPiece.Size, upload.Pieces[i].Length);
                upload.Pieces = null;
                var map = PaperMap.Read(data, t, w, h);
                // The check unpacked the charted area and counted it (ChartedTexels): the main thread need not again.
                map.MaskTexels = null;
                return map;
            }
            finally { Release(upload); }
        }, result =>
        {
            // The player left (or the server stopped) while it was checked.
            if (ended || Generation(player.PlayerUID) != generation) return;
            if (result is PaperMap map) Drawn(player, upload.Draw, map);
            else Tell(player, "Wmap: the map could not be drawn: " + ((result as Exception)?.Message ?? "unknown error"));
        });
    }

    /// <summary>Main thread: the upload checked out. Adds the drawer's waypoints and readings, keeps the map and hands over
    /// the drawn item.</summary>
    void Drawn(IServerPlayer player, MapDraw draw, PaperMap map)
    {
        var slot = Held(player);
        if (!Is(slot, BlankCode)) { Tell(player, "Wmap: hold a blank map to draw on."); return; }
        var parts = (MapParts)draw.Parts & MapParts.All;
        map.Parts = parts;
        // The client gives only what only it has.
        if (!parts.HasFlag(MapParts.Charted)) map.Mask = null;
        if (!parts.HasFlag(MapParts.Terrain)) map.Tiles.Clear();
        map.Waypoints.Clear(); map.Readings.Clear();
        if ((parts & (MapParts.Waypoints | MapParts.AutoMarks)) != 0 && Waypoints is { } layer)
            foreach (var w in layer.Waypoints)
            {
                if (w?.Position == null || w.Temporary || w.OwningPlayerUid != player.PlayerUID) continue;
                if (!parts.HasFlag(AutoMarkCatalog.IsAuto(w.Text) ? MapParts.AutoMarks : MapParts.Waypoints)) continue;
                map.Waypoints.Add(new MapWaypoint(w.Position.X, w.Position.Y, w.Position.Z, Clip(w.Title), Clip(w.Text), w.Icon ?? "circle", w.Color, w.ShowInWorld));
            }
        if (parts.HasFlag(MapParts.Readings) && Readings is { } ores)
            foreach (var r in ores.getOrLoadReadings(player))
                if (r?.Position != null) map.Readings.Add(new MapReading(r.Position.X, r.Position.Y, r.Position.Z, SerializerUtil.Serialize(r)));
        string title = (draw.Title ?? "").Trim();
        map.Title = title.Length == 0 ? $"{player.PlayerName}'s map" : title.Length > PaperMap.MaxTitle ? title[..PaperMap.MaxTitle] : title;
        map.Author = player.PlayerName ?? "";
        map.Drawn = api.World.Calendar?.PrettyDate() ?? "";
        if (map.Holds == MapParts.None) { Tell(player, "Wmap: there is nothing to draw yet."); return; }
        byte[] bytes = map.Write();
        if (bytes.Length > MaxBytes)
        {
            Tell(player, $"Wmap: that map would be {bytes.Length / 1048576.0:0.0} MiB, over the server's {config.PaperMapMaxMiB} MiB. Leave out the recorded terrain.");
            return;
        }
        string id = Guid.NewGuid().ToString("N");
        api.WorldManager.SaveGame.StoreData(PaperMap.SaveKey(id), bytes);
        var item = api.World.GetItem(new AssetLocation("wmap", DrawnCode));
        if (item == null) return;
        var stack = new ItemStack(item);
        Describe(stack.Attributes.GetOrAddTreeAttribute(Attribute), id, map, topology());
        slot.TakeOut(1);
        if (slot.Empty) slot.Itemstack = stack;
        else if (player.InventoryManager?.TryGiveItemstack(stack, true) != true && player.Entity != null) api.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
        slot.MarkDirty();
        Tell(player, $"Wmap: drew \"{map.Title}\": {Summary(map, topology())}.");
    }

    static string Clip(string text) => text == null ? "" : text.Length > PaperMap.MaxText ? text[..PaperMap.MaxText] : text;

    /// <summary>What the item shows of its map without loading it.</summary>
    static void Describe(ITreeAttribute a, string id, PaperMap map, Topology t)
    {
        a.SetString("id", id); a.SetString("title", map.Title); a.SetString("author", map.Author); a.SetString("drawn", map.Drawn);
        a.SetInt("holds", (int)map.Holds);
        a.SetDouble("km2", ChartedKm2(map, t));
        a.SetInt("tiles", map.Tiles.Count);
        a.SetInt("waypoints", map.Waypoints.Count(w => !w.Auto));
        a.SetInt("automarks", map.Waypoints.Count(w => w.Auto));
        a.SetInt("readings", map.Readings.Count);
    }

    /// <summary>From the count <see cref="PaperMap.Read"/> made when it checked the map, else by unpacking it.</summary>
    static double ChartedKm2(PaperMap map, Topology t)
    {
        if (map.Mask == null || t == null) return 0;
        var (w, h) = ExploredMask.Dimensions(t);
        long texels = map.ChartedTexels >= 0 ? map.ChartedTexels : PaperMap.Charted(ExploredMask.Unpack(map.Mask, w, h));
        return texels * (t.Width / w) * (t.Height / h) / 1e6;
    }

    static string Summary(PaperMap map, Topology t)
    {
        var parts = new List<string>();
        if (map.Mask != null) parts.Add($"{ChartedKm2(map, t):0.#} km² charted");
        if (map.Tiles.Count > 0) parts.Add($"{map.Tiles.Count} terrain tiles");
        int own = map.Waypoints.Count(w => !w.Auto), auto = map.Waypoints.Count - own;
        if (own > 0) parts.Add($"{own} waypoints");
        if (auto > 0) parts.Add($"{auto} automatic markers");
        if (map.Readings.Count > 0) parts.Add($"{map.Readings.Count} prospecting readings");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>Main thread: a player holding a drawn map copies the ticked parts into their own atlas. A read while the
    /// same map is still being sent to them, or within <see cref="ReadInterval"/> of their last, is ignored. The map is
    /// parsed and the client's part written on a worker; the markers are made on the main thread.</summary>
    public void Read(IServerPlayer player, MapRead read)
    {
        if (!config.PaperMaps || read?.Id == null) return;
        string uid = player.PlayerUID;
        long now = Environment.TickCount64;
        if (downloads.TryGetValue(uid, out var running) && running.Id == read.Id || readAt.TryGetValue(uid, out long last) && now - last < ReadInterval) return;
        var slot = Held(player);
        var description = Is(slot, DrawnCode) ? slot.Itemstack.Attributes.GetTreeAttribute(Attribute) : null;
        if (description?.GetString("id") != read.Id) { Tell(player, "Wmap: hold the map to read it."); return; }
        readAt[uid] = now;
        int sequence = reads[uid] = (reads.TryGetValue(uid, out int previous) ? previous : 0) + 1;
        double km2 = description.GetDouble("km2");
        byte[] bytes = api.WorldManager.SaveGame.GetData(PaperMap.SaveKey(read.Id));
        var t = topology();
        if (bytes == null || t == null) { Tell(player, "Wmap: this map has faded; nothing on it can be read."); return; }
        var (mw, mh) = ExploredMask.Dimensions(t);
        int generation = Generation(uid);
        Background(() =>
        {
            var map = PaperMap.Read(bytes, t, mw, mh, check: false);
            var parts = (MapParts)read.Parts & map.Holds;
            var client = map.Only(parts & MapParts.Client);
            return (map, parts, client, client.Mask != null || client.Tiles.Count > 0 ? client.Write() : null);
        }, result =>
        {
            // The player left, or read another map since (that one's download must not be replaced).
            if (ended || Generation(uid) != generation || reads.GetValueOrDefault(uid) != sequence) return;
            if (result is not ValueTuple<PaperMap, MapParts, PaperMap, byte[]> parsed) { Tell(player, "Wmap: this map has faded; nothing on it can be read."); return; }
            var (map, parts, client, data) = parsed;
            var copied = new List<string>();
            int waypoints = AddWaypoints(player, map.Waypoints.Where(w => parts.HasFlag(w.Auto ? MapParts.AutoMarks : MapParts.Waypoints)).ToList(), out int full);
            if (waypoints > 0) copied.Add($"{waypoints} waypoints");
            int readings = parts.HasFlag(MapParts.Readings) ? AddReadings(player, map.Readings) : 0;
            if (readings > 0) copied.Add($"{readings} prospecting readings");
            if (data != null)
            {
                downloads[uid] = new Download { Player = player, Id = read.Id, Data = data };
                if (client.Mask != null) copied.Add($"{km2:0.#} km² of chart");
                if (client.Tiles.Count > 0) copied.Add($"{client.Tiles.Count} terrain tiles");
            }
            string text = copied.Count == 0 ? "nothing new" : string.Join(", ", copied);
            if (full > 0) text += $" ({full} automatic markers left out: you have the server's limit)";
            Tell(player, $"Wmap: copied from \"{map.Title}\": {text}.");
        });
    }

    /// <summary>Makes the reader's own copies of a map's waypoints, skipping those they have (<see cref="PaperMap.Has(IEnumerable{MapWaypoint}, MapWaypoint)"/>)
    /// and automatic markers beyond the server's limit. The game's list is sent to the player once, at the end.</summary>
    int AddWaypoints(IServerPlayer player, List<MapWaypoint> wanted, out int full)
    {
        full = 0;
        if (wanted.Count == 0 || Waypoints is not { } layer) return 0;
        var own = layer.Waypoints.Where(w => w?.Position != null && w.OwningPlayerUid == player.PlayerUID)
            .Select(w => new MapWaypoint(w.Position.X, w.Position.Y, w.Position.Z, w.Title, w.Text, w.Icon, w.Color, w.ShowInWorld)).ToList();
        int autos = own.Count(w => w.Auto);
        // Grouped by title and icon, which a duplicate shares (two titles in one group only cost comparisons).
        var near = new Nearby<MapWaypoint>(2, w => (w.X, w.Y, w.Z), w => w.Title + "|" + w.Icon);
        foreach (var w in own) near.Add(w);
        var made = new List<Waypoint>();
        foreach (var w in wanted)
        {
            if (PaperMap.Has(near.Around(w), w)) continue;
            if (w.Auto && autos >= config.AutoMarkLimit) { full++; continue; }
            if (w.Auto) autos++;
            near.Add(w);
            made.Add(new Waypoint
            {
                Position = new Vec3d(w.X, w.Y, w.Z), Title = w.Title, Text = w.Text, Icon = w.Icon, Color = w.Color, ShowInWorld = w.ShowInWorld,
                OwningPlayerUid = player.PlayerUID, Guid = Guid.NewGuid().ToString()
            });
        }
        if (made.Count == 0) return 0;
        // AddWaypoint sends the player their whole list: once, with the last.
        layer.Waypoints.AddRange(made.Take(made.Count - 1));
        layer.AddWaypoint(made[^1], player);
        return made.Count;
    }

    int AddReadings(IServerPlayer player, List<MapReading> wanted)
    {
        if (wanted.Count == 0 || Readings is not { } ores) return 0;
        var list = ores.getOrLoadReadings(player);
        var own = new Nearby<(double X, double Y, double Z)>(1, o => o);
        foreach (var r in list) if (r?.Position != null) own.Add((r.Position.X, r.Position.Y, r.Position.Z));
        var made = new List<PropickReading>();
        foreach (var r in wanted)
        {
            if (PaperMap.Has(own.Around((r.X, r.Y, r.Z)), r)) continue;
            PropickReading reading;
            try { reading = SerializerUtil.Deserialize<PropickReading>(r.Data); }
            catch (Exception) { continue; }
            if (reading == null) continue;
            reading.Position = new Vec3d(r.X, r.Y, r.Z);
            reading.Guid = Guid.NewGuid().ToString();
            own.Add((r.X, r.Y, r.Z));
            made.Add(reading);
        }
        if (made.Count == 0) return 0;
        list.AddRange(made.Take(made.Count - 1));
        ores.AddWaypoint(made[^1], player);
        return made.Count;
    }

    /// <summary>Main thread: sends read maps' charted area and terrain, a few pieces a player a tick and
    /// <see cref="PiecesPerTickTotal"/> in all, starting with a different player each tick; drops stalled uploads.</summary>
    public void Tick()
    {
        if (!uploads.IsEmpty)
        {
            long now = Environment.TickCount64;
            foreach (var pair in uploads)
                if ((now - Volatile.Read(ref pair.Value.Last) > UploadTimeout || now > pair.Value.Deadline) && uploads.TryRemove(pair))
                {
                    Release(pair.Value);
                    if (api.World.PlayerByUid(pair.Key) is IServerPlayer player)
                    {
                        Tell(player, "Wmap: the map took too long to upload and was not drawn; try again.");
                        Stop(player, pair.Value.Draw.Id);
                    }
                }
        }
        if (downloads.Count == 0) return;
        var all = downloads.Values.ToArray();
        int budget = PiecesPerTickTotal;
        rotation = (rotation + 1) % all.Length;
        for (int n = 0; n < all.Length && budget > 0; n++)
        {
            var d = all[(rotation + n) % all.Length];
            for (int i = 0; i < PiecesPerTick && budget > 0 && d.Offset < d.Data.Length; i++, budget--)
            {
                int length = Math.Min(MapPiece.Size, d.Data.Length - d.Offset);
                channel.SendPacket(new MapPiece { Id = d.Id, Offset = d.Offset, Total = d.Data.Length, Data = d.Data.AsSpan(d.Offset, length).ToArray() }, d.Player);
                d.Offset += length;
            }
            if (d.Offset >= d.Data.Length) downloads.Remove(d.Player.PlayerUID);
        }
    }

    public void Leave(string uid)
    {
        // Under the draw lock, as Draw admits: a drawing is either admitted before this, and removed here, or after it,
        // which takes a packet from the player once they have left.
        lock (drawLock)
        {
            generations.AddOrUpdate(uid, 1, (_, g) => g + 1);
            if (uploads.TryRemove(uid, out var upload)) Release(upload);
            refusals.Remove(uid);
        }
        downloads.Remove(uid); readAt.Remove(uid); reads.Remove(uid);
        // Draw starts stay: leaving and joining again does not skip the interval.
    }

    /// <summary>The server stops: checks still running end without touching the world.</summary>
    public void Stop() => ended = true;
}
