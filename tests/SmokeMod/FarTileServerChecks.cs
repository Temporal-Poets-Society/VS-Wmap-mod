using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using ProtoBuf;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Wmap;
using Wmap.Core;
using Wmap.Integrations;
using Wmap.Server;

/// <summary>
/// Far terrain's tile stream on the server (0.4.0): it does not cancel the atlas's requests or the other way round,
/// it takes only what it serves, caps each player, takes players in turn, drops work for a connection that left,
/// and old and new messages read each other. With Earth: biome tiles are plausible, and their cost is measured.
/// </summary>
public static class FarTileServerChecks
{
    sealed class Player : ServerPlayer
    {
        public string Uid;
        public Player() : base(null, null) { }
        public override string PlayerUID => Uid;
        public override EnumClientState ConnectionState => EnumClientState.Playing;
        public override bool HasPrivilege(string code) => false;
    }

    // The 0.3.x messages, as an old peer reads and writes them.
    [ProtoContract] public sealed class OldTileRequest { [ProtoMember(1)] public string Session; [ProtoMember(2)] public int View; [ProtoMember(3)] public int Level; [ProtoMember(4)] public int X; [ProtoMember(5)] public int Z; }
    [ProtoContract] public sealed class OldTileResponse { [ProtoMember(1)] public string Session; [ProtoMember(2)] public int View; [ProtoMember(3)] public int Level; [ProtoMember(4)] public int X; [ProtoMember(5)] public int Z; [ProtoMember(6)] public byte[] Data; [ProtoMember(7)] public string Error; }
    [ProtoContract] public sealed class OldAtlasInfo { [ProtoMember(1)] public string Session; [ProtoMember(16)] public bool CanPredict; [ProtoMember(24)] public bool FlatPrediction; }

    static T RoundTrip<T>(object message)
    {
        using var stream = new MemoryStream();
        Serializer.NonGeneric.Serialize(stream, message);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }

    public static string Run(WorldSource source, Func<double, double, (double X, double Z)?> locate)
    {
        string protocol = CheckProtocol();
        if (source.Samplers == null) return "Far tile stream: PASS (" + protocol + "; no prediction provider, stream checks skipped)";
        var players = Enumerable.Range(0, 6).Select(i => { var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player)); p.Uid = "far-" + i; return p; }).ToArray();
        var world = GuiServiceProxy.Make<IServerWorldAccessor>((method, args) =>
            method.Name == "PlayerByUid" ? players.FirstOrDefault(p => p.PlayerUID == (string)args[0]) : GuiServiceProxy.Default(method));
        var api = GuiServiceProxy.Make<ICoreServerAPI>((method, args) => method.Name == "get_World" ? world : GuiServiceProxy.Default(method));
        var sent = new ConcurrentQueue<(string Uid, TileResponse Response)>();
        AtlasInfo manifest = null;
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is TileResponse r && args.Length > 1 && args[1] is IServerPlayer[] { Length: 1 } to) sent.Enqueue((to[0].PlayerUID, r));
            if (method.Name == "SendPacket" && args[0] is AtlasInfo info) manifest = info;
            return GuiServiceProxy.Default(method);
        });
        string cache = Path.Combine(Path.GetTempPath(), "wmap-far-stream-" + Guid.NewGuid().ToString("N"));
        var config = new WmapConfig { ShareWorldChart = true };
        using var server = new AtlasServer(api, channel, config);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(AtlasServer).GetField(name, flags).SetValue(server, value);
        object Get(string name) => typeof(AtlasServer).GetField(name, flags).GetValue(server);
        Field("source", source); Field("cache", cache);
        typeof(AtlasServer).GetMethod("StartWorkers", flags).Invoke(server, new object[] { 2 });
        var t = source.Info.Topology();
        string session = source.Info.Session;
        var at = locate?.Invoke(46.0, 8.0) ?? (t.StartX + t.Width / 2, t.Height / 2);
        int bx = (int)((t.Canonical(at.X) - t.StartX) / 512), bz = (int)(at.Z / 512);
        TileRequest Far(int dx, int dz, int content = TileContent.SemanticAndBiome, int level = 2, int view = 1) =>
            new() { Session = session, View = view, Level = level, X = (bx >> (level - 2)) + dx, Z = (bz >> (level - 2)) + dz, Stream = 1, Content = content };
        var outstanding = (Dictionary<string, int>)Get("outstanding");
        List<(string Uid, TileResponse Response)> Wait(int count, int ms = 20000)
        {
            var got = new List<(string, TileResponse)>();
            var watch = Stopwatch.StartNew();
            while (got.Count < count && watch.ElapsedMilliseconds < ms)
            {
                server.Tick();
                while (sent.TryDequeue(out var item)) got.Add(item);
                if (got.Count < count) Thread.Sleep(5);
            }
            return got;
        }
        try
        {
            var p = players[0];
            // The manifest a player is actually sent, through the wire format, turns far terrain's tiles on.
            server.Hello(p, new AtlasHello());
            var served = RoundTrip<AtlasInfo>(manifest ?? throw new Exception("Far tile stream: no manifest sent"));
            var (bandOn, climateOn) = Wmap.Client.FarTerrain.TilesFor(served, new WmapConfig());
            bool relief = !source.Info.FlatPrediction, biomeExpected = relief && source.Biome != null;
            if (!served.FarTiles || bandOn != relief || climateOn != biomeExpected || (biomeExpected && served.BiomeId != source.Info.BiomeId))
                throw new Exception($"Far tile stream: the served manifest gives band {bandOn}, climate {climateOn} (FarTiles {served.FarTiles}, Biome {served.Biome}, BiomeId {served.BiomeId})");
            if (!served.Teleport) throw new Exception("Protocol: the served manifest does not offer teleporting");
            // Neither stream cancels the other: an atlas request with a newer view and a far one both get answers.
            server.Request(p, new TileRequest { Session = session, View = 5, Level = 3, X = bx / 2, Z = bz / 2 });
            server.Request(p, Far(0, 0));
            var both = Wait(2);
            if (both.Count != 2 || both.Any(b => b.Response.Error != null) || both.Count(b => b.Response.Stream == 1) != 1)
                throw new Exception("Far tile stream: atlas and far requests did not both complete: " + string.Join(", ", both.Select(b => $"stream {b.Response.Stream} {b.Response.Error}")));
            var far = both.First(b => b.Response.Stream == 1).Response;
            if (far.Data == null || (source.Biome != null && far.Biome == null) || far.Content != TileContent.SemanticAndBiome) throw new Exception("Far tile stream: a near-band answer lacks its semantic or biome tile");
            SemanticTile.Unpack(far.Data);
            byte[] biome = far.Biome != null ? BiomeTile.Unpack(far.Biome) : null;

            // Only what it serves: no semantic tile on the far stream alone, no unknown stream, no near-band level other than 2.
            int before = outstanding.Values.Sum();
            server.Request(p, Far(1, 0, TileContent.Semantic));
            server.Request(p, new TileRequest { Session = session, View = 1, Level = 2, X = bx, Z = bz, Stream = 2 });
            server.Request(p, Far(1, 0, TileContent.SemanticAndBiome, 3));
            server.Request(p, new TileRequest { Session = session, View = 1, Level = 3, X = bx, Z = bz, Stream = 0, Content = TileContent.Biome });
            if (outstanding.Values.Sum() != before) throw new Exception("Far tile stream: the server queued a request it does not serve");

            // Capped per player and stream: 20 requests, at most 8 waiting.
            for (int i = 0; i < 20; i++) server.Request(p, Far(i % 5 - 2, i / 5 + 3));
            outstanding.TryGetValue(p.PlayerUID + "|1", out int open);
            if (open > AtlasServer.StreamLimits[1]) throw new Exception($"Far tile stream: {open} requests open for one player");
            int capped = open;
            Wait(capped, 60000);

            // Players take turns, also beside players whose queues ran empty: four players are served once each and go
            // idle, then two players with 8 requests each are answered alternately.
            for (int k = 2; k < 6; k++) server.Request(players[k], Far(k, -8));
            if (Wait(4, 60000).Count != 4) throw new Exception("Far tile stream: idle players' requests were not answered");
            for (int i = 0; i < 8; i++) { server.Request(players[0], Far(i - 4, -3)); server.Request(players[1], Far(i - 4, -4)); }
            var turns = Wait(16, 90000);
            var firstHalf = turns.Take(8).Select(x => x.Uid).ToList();
            int firstA = firstHalf.Count(u => u == players[0].PlayerUID);
            if (turns.Count != 16 || firstA < 3 || firstA > 5) throw new Exception("Far tile stream: turns are not shared: " + string.Join(",", turns.Select(x => x.Uid)));

            // A player who leaves: work already queued for that connection is neither sent nor counted later.
            for (int i = 0; i < 4; i++) server.Request(p, Far(i, -6));
            server.Leave(p.PlayerUID);
            var late = Wait(1, 3000);
            if (late.Any(x => x.Uid == p.PlayerUID)) throw new Exception("Far tile stream: a tile was sent to a connection that had left");
            if (outstanding.TryGetValue(p.PlayerUID + "|1", out int left) && left != 0) throw new Exception("Far tile stream: a departed connection's work changed the new count");
            server.Request(p, Far(0, 0));
            if (Wait(1).Count != 1) throw new Exception("Far tile stream: a returning player is not served");

            string plausible = source.Biome != null && locate != null ? CheckBiome(source, t, locate) : "no biome sampler";
            string cost = source.Biome != null ? Cost(source, t, bx, bz) : "";
            return $"Far tile stream: PASS ({protocol}; the served manifest turns on band {bandOn} and climate {climateOn}; atlas and far requests independent; only near band, biome and atlas combinations queued; {capped} of 20 open per player; two players alternate beside four idle ones ({string.Join("", firstHalf.Select(u => u == players[0].PlayerUID ? 'A' : 'B'))}); departed connection dropped; {plausible}{cost})";
        }
        finally
        {
            server.Dispose();
            try { Directory.Delete(cache, true); } catch (IOException) { }
        }
    }

    static string CheckProtocol()
    {
        var newRequest = new TileRequest { Session = "s", View = 3, Level = 2, X = 7, Z = 9, Stream = 1, Content = TileContent.SemanticAndBiome };
        var asOld = RoundTrip<OldTileRequest>(newRequest);
        if (asOld.Session != "s" || asOld.View != 3 || asOld.Level != 2 || asOld.X != 7 || asOld.Z != 9) throw new Exception("Protocol: an old server misreads a new request's fields");
        var fromOld = RoundTrip<TileRequest>(new OldTileRequest { Session = "s", View = 3, Level = 4, X = 1, Z = 2 });
        if (fromOld.Stream != 0 || fromOld.Content != TileContent.Semantic || fromOld.Level != 4) throw new Exception("Protocol: an old client's request is not an atlas request");
        var oldResponse = RoundTrip<OldTileResponse>(new TileResponse { Session = "s", View = 1, Level = 2, X = 3, Z = 4, Data = new byte[] { 1, 2 }, Biome = new byte[] { 3 }, Stream = 1, Content = 1 });
        if (oldResponse.Data?.Length != 2 || oldResponse.X != 3) throw new Exception("Protocol: an old client misreads a new response");
        var newFromOld = RoundTrip<TileResponse>(new OldTileResponse { Session = "s", View = 1, Data = new byte[] { 1 } });
        if (newFromOld.Stream != 0 || newFromOld.Biome != null) throw new Exception("Protocol: an old server's response is not an atlas response");
        var info = RoundTrip<AtlasInfo>(new OldAtlasInfo { Session = "s", CanPredict = true });
        if (info.FarTiles || info.Biome || info.BiomeId != null || !info.CanPredict) throw new Exception("Protocol: an old server's manifest offers far tiles");
        // 0.4.3: a client asks to teleport only when the server says it takes the message.
        if (info.Teleport) throw new Exception("Protocol: an old server's manifest offers teleporting");
        var teleport = RoundTrip<TeleportRequest>(new TeleportRequest { X = 230399.75, Z = -0.5 });
        if (teleport.X != 230399.75 || teleport.Z != -0.5) throw new Exception("Protocol: a teleport request changes on the wire");
        return "old and new messages read each other; old manifests offer no far tiles and no teleporting";
    }

    /// <summary>Forest where Europe's climate supports it and not in the Sahara; colder toward the pole.</summary>
    static string CheckBiome(WorldSource source, Topology t, Func<double, double, (double X, double Z)?> locate)
    {
        BiomeSample At(double lat, double lon) { var p = locate(lat, lon).Value; return source.Biome(t.Canonical(p.X), p.Z); }
        var germany = At(50.5, 10.0); var sahara = At(23.0, 10.0); var equator = At(0.5, 20.0); var north = At(66.0, 25.0);
        if (germany.Forest < 100) throw new Exception($"Biome: central Germany has forest {germany.Forest}");
        if (sahara.Forest > 30 || sahara.Rain >= germany.Rain) throw new Exception($"Biome: the Sahara has forest {sahara.Forest}, rain {sahara.Rain} (Germany {germany.Rain})");
        if (equator.Temperature <= north.Temperature) throw new Exception($"Biome: the equator ({equator.Temperature}) is not warmer than Lapland ({north.Temperature})");
        return $"biome: Germany forest {germany.Forest} rain {germany.Rain}, Sahara forest {sahara.Forest} rain {sahara.Rain}, temperature equator {equator.Temperature} > Lapland {north.Temperature}";
    }

    static string Cost(WorldSource source, Topology t, int bx, int bz)
    {
        var sample = source.Samplers();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) SemanticTile.Pack(SemanticTile.Sample(t, new TileKey(0, 2, bx + 20 + i, bz + 20), sample, source.Info.SeaLevel, CancellationToken.None, null, source.Info.ChartHeightRange));
        double semantic = watch.Elapsed.TotalMilliseconds / 4; watch.Restart();
        for (int i = 0; i < 4; i++) BiomeTile.Pack(BiomeTile.Sample(t, new TileKey(0, 2, bx + 20 + i, bz + 21), source.Biome, CancellationToken.None));
        double band = watch.Elapsed.TotalMilliseconds / 4; watch.Restart();
        for (int i = 0; i < 2; i++) BiomeTile.Pack(BiomeTile.Sample(t, new TileKey(0, 6, (bx >> 4) + 3 + i, (bz >> 4) + 1), source.Biome, CancellationToken.None));
        double climate = watch.Elapsed.TotalMilliseconds / 2;
        return $"; cold cost per tile: semantic {semantic:0} ms, near-band biome {band:0} ms, climate (level 6) biome {climate:0} ms";
    }
}
