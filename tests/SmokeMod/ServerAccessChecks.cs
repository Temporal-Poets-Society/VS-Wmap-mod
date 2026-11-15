using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Wmap;
using Wmap.Integrations;
using Wmap.Server;

public static class ServerAccessChecks
{
    // IServerPlayer has an internal default interface member and cannot be wrapped
    // by DispatchProxy. Only these two virtual accessors are used by the server checks.
    sealed class PermissionPlayer : ServerPlayer
    {
        public bool Privileged, Host;
        public EnumClientState State;
        public string Uid;
        public PermissionPlayer() : base(null, null) { }
        public override string PlayerUID => Uid ?? "permission-test";
        public override EnumClientState ConnectionState => State;
        // A single-player host (the admin role) holds every privilege granted to admins; wmap is not one of them.
        public override bool HasPrivilege(string code) => Privileged && code == "wmap" || Host && code != "wmap";
    }
    /// <summary>The player's game mode as the server keeps it (a connected player; a disconnected one reads as spectator).</summary>
    static void SetGameMode(ServerPlayer p, EnumGameMode mode)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var field = typeof(ServerPlayer).GetField("worlddata", flags);
        object data = field.GetValue(p) ?? RuntimeHelpers.GetUninitializedObject(field.FieldType);
        data.GetType().GetField("GameMode", flags).SetValue(data, mode);
        data.GetType().GetField("connected", flags).SetValue(data, true);
        field.SetValue(p, data);
    }
    public static string Run(WorldSource source)
    {
        AtlasInfo received = null;
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is AtlasInfo info) received = info;
            return GuiServiceProxy.Default(method);
        });
        var player = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
        var config = new WmapConfig();
        using var server = new AtlasServer(null, channel, config);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AtlasServer).GetField("source", flags).SetValue(server, source);
        object jobs = typeof(AtlasServer).GetField("jobs", flags).GetValue(server);
        int Queued() => (int)jobs.GetType().GetProperty("Count").GetValue(jobs);
        var request = new TileRequest { Session = source.Info.Session, View = 1, Level = source.Info.Topology().MaxLevel };
        // 0.5.4 (WM-D63): an ordinary player gets the game chart and the prediction only for land they have seen: no whole
        // chart, no full map; their requests are taken and answered for seen land only (SurvivalChecks).
        server.Hello(player, new AtlasHello());
        if (received == null || !received.Allowed || received.CanDevelop || received.HasOverview || received.BaseId != null
            || received.CanPredict != source.Info.HasOverview || received.Explored != source.Info.HasOverview)
            throw new Exception("Ordinary player must receive gameplay access with the prediction of seen land only");
        var ordinary = received;
        server.Request(player, request);
        if (Queued() != (source.Info.HasOverview ? 1 : 0)) throw new Exception("An ordinary player's request for seen land was not taken");
        int queued = Queued();
        player.Privileged = true;
        server.Hello(player, new AtlasHello());
        if (!received.Allowed || !received.CanDevelop || received.HasOverview != source.Info.HasOverview || received.Explored)
            throw new Exception("Developer did not receive overview capability");
        if (ordinary.CanDevelop || ordinary.HasOverview) throw new Exception("One player's permissions changed another manifest");
        server.Request(player, request);
        if (Queued() != queued + (source.Info.HasOverview ? 1 : 0)) throw new Exception("Server rejected a valid developer request");
        // 0.4.3: the full map follows the game mode. A survival player gets the game chart, the same player in creative
        // the full map, at once (the client asks for a new manifest when its game mode changes).
        player.Privileged = false;
        SetGameMode(player, EnumGameMode.Survival);
        server.Hello(player, new AtlasHello());
        if (received.CanDevelop || received.HasOverview || received.Explored != source.Info.HasOverview) throw new Exception("A survival player received the full map");
        // 0.4.5: nor a single-player host in survival (0.4.4 granted it with controlserver).
        player.Host = true;
        server.Hello(player, new AtlasHello());
        player.Host = false;
        if (received.CanDevelop || received.HasOverview) throw new Exception("A single-player host in survival received the full map");
        SetGameMode(player, EnumGameMode.Creative);
        queued = Queued();
        server.Hello(player, new AtlasHello());
        if (!received.CanDevelop || received.HasOverview != source.Info.HasOverview || received.Explored) throw new Exception("A creative player did not receive the full map");
        server.Request(player, request);
        if (Queued() != queued + (source.Info.HasOverview ? 1 : 0)) throw new Exception("Server rejected a creative player's prediction request");
        SetGameMode(player, EnumGameMode.Survival);
        config.AllowAllPlayers = true;
        server.Hello(player, new AtlasHello());
        if (!received.CanDevelop) throw new Exception("AllowAllPlayers did not enable development access");
        CheckReadyAnnouncement();
        string busy = CheckBusy(source);
        string budget = busy + "; " + CheckChartBudget();
        return "Server permissions: PASS (ordinary gameplay with the prediction of seen land only, privileged development, isolated manifests, survival denied (a host too) and creative granted the full map, AllowAllPlayers; chart-ready announcement after 100% bake progress; " + budget + ")";
    }

    /// <summary>0.4.4: with the workers' queue full (many players at once), a request is answered "busy" at once instead of
    /// being dropped, so the client asks again soon rather than after its 15-second timeout.</summary>
    static string CheckBusy(WorldSource source)
    {
        if (source.Samplers == null) return "busy reply: not applicable (no predictions)";
        var answers = new System.Collections.Generic.List<(TileResponse Response, IServerPlayer To)>();
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is TileResponse r) answers.Add((r, args.Length > 1 && args[1] is IServerPlayer[] { Length: 1 } to ? to[0] : null));
            return GuiServiceProxy.Default(method);
        });
        using var server = new AtlasServer(null, channel, new WmapConfig { AllowAllPlayers = true });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AtlasServer).GetField("source", flags).SetValue(server, source);
        object jobs = typeof(AtlasServer).GetField("jobs", flags).GetValue(server);
        int Queued() => (int)jobs.GetType().GetProperty("Count").GetValue(jobs);
        var request = new TileRequest { Session = source.Info.Session, View = 1, Level = source.Info.Topology().MaxLevel };
        PermissionPlayer last = null;
        // No workers run here: the queue fills. Each player may have 12 requests open, so six fill its 64 places.
        for (int p = 0; p < 6 && Queued() < 64; p++)
        {
            last = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
            last.Uid = "busy-" + p;
            for (int n = 0; n < AtlasServer.StreamLimits[0] && Queued() < 64; n++) server.Request(last, request);
        }
        if (Queued() != 64 || answers.Count != 0) throw new Exception($"Busy setup: {Queued()} queued, {answers.Count} answers");
        var waiting = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
        waiting.Uid = "busy-late";
        server.Request(waiting, request);
        if (answers.Count != 1 || answers[0].Response.Error != TileResponse.Busy || answers[0].To != waiting || answers[0].Response.Level != request.Level)
            throw new Exception("A request to a full queue was not answered busy: " + string.Join(", ", answers.Select(a => a.Response.Error)));
        return "a full queue answers busy";
    }

    /// <summary>A modified client flooding chart requests gets each chunk once, within the per-tick budget.</summary>
    static string CheckChartBudget()
    {
        var player = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
        player.State = EnumClientState.Playing;
        var world = GuiServiceProxy.Make<IServerWorldAccessor>((method, args) =>
            method.Name == "PlayerByUid" ? player : GuiServiceProxy.Default(method));
        var api = GuiServiceProxy.Make<ICoreServerAPI>((method, args) =>
            method.Name == "get_World" ? world : GuiServiceProxy.Default(method));
        var sent = new System.Collections.Generic.List<int>();
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is BaseChunk chunk) sent.Add(chunk.Offset);
            return GuiServiceProxy.Default(method);
        });
        using var server = new AtlasServer(api, channel, new WmapConfig { AllowAllPlayers = true });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(AtlasServer).GetField(name, flags).SetValue(server, value);
        string id = new string('b', 64);
        Field("source", new WorldSource { Info = new AtlasInfo { Session = "budget", HasOverview = true } });
        Field("baseId", id); Field("basePacked", new byte[64 * BaseChunk.Size]);
        for (int n = 0; n < 1000; n++) server.RequestBase(player, new BaseRequest { BaseId = id, Offset = 0 });
        for (int n = 0; n < 64; n++) { server.RequestBase(player, new BaseRequest { BaseId = id, Offset = n * BaseChunk.Size }); server.RequestBase(player, new BaseRequest { BaseId = id, Offset = n * BaseChunk.Size }); }
        server.Tick();
        if (sent.Count != AtlasServer.ChartChunksPerPlayerTick) throw new Exception($"Chart flood sent {sent.Count} chunks in one tick");
        for (int n = 0; n < 20; n++) server.Tick();
        if (sent.Count != AtlasServer.ChartPendingLimit || sent.Distinct().Count() != sent.Count)
            throw new Exception($"Chart flood: {sent.Count} chunks ({sent.Distinct().Count()} distinct) instead of {AtlasServer.ChartPendingLimit} once each");
        // An honest client keeps 16 requests open: none is dropped.
        sent.Clear();
        for (int n = 32; n < 48; n++) server.RequestBase(player, new BaseRequest { BaseId = id, Offset = n * BaseChunk.Size });
        server.Tick(); server.Tick();
        if (sent.Count != 16) throw new Exception($"Windowed chart download received {sent.Count} of 16 chunks");
        return $"chart flood capped at {AtlasServer.ChartChunksPerPlayerTick} chunks per tick and {AtlasServer.ChartPendingLimit} pending, deduplicated; windowed download intact; " + CheckChartFairness();
    }

    /// <summary>Three players asking for more than the tick budget all keep receiving chunks.</summary>
    static string CheckChartFairness()
    {
        var players = Enumerable.Range(0, 3).Select(i =>
        {
            var p = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
            p.State = EnumClientState.Playing; p.Uid = "fair-" + i; return p;
        }).ToArray();
        var world = GuiServiceProxy.Make<IServerWorldAccessor>((method, args) =>
            method.Name == "PlayerByUid" ? players.FirstOrDefault(p => p.PlayerUID == (string)args[0]) : GuiServiceProxy.Default(method));
        var api = GuiServiceProxy.Make<ICoreServerAPI>((method, args) =>
            method.Name == "get_World" ? world : GuiServiceProxy.Default(method));
        var received = new int[players.Length];
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is BaseChunk && args.Length > 1 && args[1] is IServerPlayer[] { Length: 1 } to) received[Array.IndexOf(players, to[0])]++;
            return GuiServiceProxy.Default(method);
        });
        using var server = new AtlasServer(api, channel, new WmapConfig { AllowAllPlayers = true });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(AtlasServer).GetField(name, flags).SetValue(server, value);
        string id = new string('c', 64);
        Field("source", new WorldSource { Info = new AtlasInfo { Session = "fair", HasOverview = true } });
        Field("baseId", id); Field("basePacked", new byte[64 * BaseChunk.Size]);
        for (int tick = 0; tick < 30; tick++)
        {
            foreach (var p in players)
                for (int n = 0; n < 64; n++) server.RequestBase(p, new BaseRequest { BaseId = id, Offset = n * BaseChunk.Size });
            server.Tick();
        }
        int total = received.Sum();
        if (total != 30 * AtlasServer.ChartChunksPerTick) throw new Exception($"Chart budget: {total} chunks in 30 ticks");
        if (received.Min() < total / players.Length / 2) throw new Exception("Chart download starved a player: " + string.Join("/", received));
        return "three saturating players served " + string.Join("/", received) + " chunks";
    }

    static void CheckReadyAnnouncement()
    {
        var player = (PermissionPlayer)RuntimeHelpers.GetUninitializedObject(typeof(PermissionPlayer));
        player.State = EnumClientState.Playing;
        var world = GuiServiceProxy.Make<IServerWorldAccessor>((method, args) =>
            method.Name == "PlayerByUid" ? player : GuiServiceProxy.Default(method));
        var api = GuiServiceProxy.Make<ICoreServerAPI>((method, args) =>
            method.Name == "get_World" ? world : GuiServiceProxy.Default(method));
        var manifests = new System.Collections.Generic.List<AtlasInfo>();
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((method, args) =>
        {
            if (method.Name == "SendPacket" && args[0] is AtlasInfo info) manifests.Add(info);
            return GuiServiceProxy.Default(method);
        });
        using var server = new AtlasServer(api, channel, new WmapConfig { AllowAllPlayers = true });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(AtlasServer).GetField(name, flags).SetValue(server, value);
        Field("source", new WorldSource { Info = new AtlasInfo { Session = "ready", HasOverview = true } });
        server.Hello(player, new AtlasHello());
        // Sampling finishes before compression, hashing and disk caching finish.
        Field("baseProgress", 1f);
        Field("announcedAt", Environment.TickCount64 - 3000);
        server.Tick();
        if (manifests[^1].BaseProgress != 1 || manifests[^1].BaseId != null)
            throw new Exception("Chart progress setup failed");
        manifests.Clear();
        string id = new string('a', 64);
        Field("baseId", id);
        Field("basePacked", new byte[10]);
        server.Tick();
        if (manifests.Count != 1 || manifests[0].BaseId != id || manifests[0].BaseBytes != 10)
            throw new Exception("Finished world chart was never announced after bake progress reached 100%");
        server.Tick();
        if (manifests.Count != 1) throw new Exception("Ready chart announced on every server tick");
        server.Leave(player.PlayerUID);
        Field("basePacked", null); Field("baseId", null);
        player.State = EnumClientState.Connecting;
        server.Hello(player, new AtlasHello()); manifests.Clear();
        Field("baseId", id); Field("basePacked", new byte[10]);
        server.Tick();
        if (manifests.Count != 0) throw new Exception("Loading player setup failed");
        player.State = EnumClientState.Playing;
        server.Tick();
        if (manifests.Count != 1 || manifests[0].BaseId != id)
            throw new Exception("Player who was loading at chart publication never received the ready chart");
    }
}

/// <summary>The prediction cache key must survive restarts and change with what shapes terrain.</summary>
public static class CacheIdentityChecks
{
    public static string Run(ICoreServerAPI api, WorldSource source)
    {
        string first = WorldSource.Create(api, new WmapConfig()).Info.CacheId;
        if (first != source.Info.CacheId) throw new Exception("Cache identity differs between two starts");
        var off = WorldSource.Create(api, new WmapConfig { PredictTerrain = false });
        if (off.Samplers != null || off.Info.HasOverview || off.Info.ObservationId != source.Info.ObservationId || off.Info.Width != source.Info.Width)
            throw new Exception("PredictTerrain off must keep topology and recordings and drop predictions");
        object core = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "CylinderWorld.CylinderWorldCore");
        object p = WorldSource.Member(core, "Params"), config = WorldSource.Member(core, "Config");
        if (p == null) return "Cache identity: PASS (stable across starts; PredictTerrain off keeps topology)";
        var year = p.GetType().GetField("SeaIceYear");
        object before = year?.GetValue(p);
        try
        {
            year?.SetValue(p, 0.437);
            if (WorldSource.Create(api, new WmapConfig()).Info.CacheId != first) throw new Exception("Cylinder's sea-ice date changed the prediction cache");
        }
        finally { year?.SetValue(p, before); }
        var flatten = config?.GetType().GetField("FlattenSeabed");
        if (flatten == null) throw new Exception("Cylinder World config has no FlattenSeabed");
        object flat = flatten.GetValue(config);
        // From Cylinder World 1.1.0 (CW-0011) a world saves FlattenSeabed (FlattenSeabedFrozen): World Map reads the world's own
        // value there (WM-0043), and the config no longer changes the world.
        var frozen = p.GetType().GetField("FlattenSeabedFrozen");
        var saved = p.GetType().GetField("FlattenSeabed");
        if (frozen?.GetValue(p) is true && saved != null)
        {
            object was = saved.GetValue(p);
            try
            {
                flatten.SetValue(config, !(bool)flat);
                if (WorldSource.Create(api, new WmapConfig()).Info.CacheId != first) throw new Exception("The config's FlattenSeabed changed the cache of a world that saved its own");
                flatten.SetValue(config, flat);
                saved.SetValue(p, !(bool)was);
                if (WorldSource.Create(api, new WmapConfig()).Info.CacheId == first) throw new Exception("The world's saved FlattenSeabed did not change the prediction cache");
            }
            finally { flatten.SetValue(config, flat); saved.SetValue(p, was); }
            return "Cache identity: PASS (stable across starts and sea-ice dates; the world's saved FlattenSeabed changes it, the config's no longer does; PredictTerrain off keeps topology)";
        }
        try
        {
            flatten.SetValue(config, false);
            var unflattened = WorldSource.Create(api, new WmapConfig());
            if (unflattened.Info.CacheId == first) throw new Exception("FlattenSeabed did not change the prediction cache");
        }
        finally { flatten.SetValue(config, flat); }
        return "Cache identity: PASS (stable across starts and sea-ice dates; FlattenSeabed changes it; PredictTerrain off keeps topology)";
    }
}
