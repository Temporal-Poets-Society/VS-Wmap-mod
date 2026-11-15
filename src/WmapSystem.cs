using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Wmap.Client;
using Wmap.Server;

namespace Wmap;

public sealed class WmapSystem : ModSystem
{
    ICoreClientAPI capi;
    ICoreServerAPI sapi;
    IClientNetworkChannel clientChannel;
    AtlasServer server;
    WmapClient client;
    AtlasDialog dialog;
    CoordinatesHud coordinates;
    MapKeyBinding mapKey;
    WmapConfig config;
    bool wantOpen, ended;
    long clientTick, serverTick, helloAt;
    /// <summary>
    /// A player entity's watched string attribute granting wind sight on the gameplay map: set on the server by another
    /// mod (Magelan: a barometer in her binnacle, for everyone aboard) to a short description of the source, removed
    /// when it no longer applies. Wmap clears it when a player joins.
    /// </summary>
    public const string WindSightAttribute = "wmapWindSight";
    public WmapClient CurrentClient => client;
    public bool AtlasOpened => dialog?.IsOpened() == true;
    public override double ExecuteOrder() => 1.1;
    /// <summary>Main-thread callbacks a player may have waiting; packets beyond it are dropped on arrival.
    /// Clients keep at most 8 tile requests in flight and resend a hello every few seconds.</summary>
    public const int PendingPerPlayer = 32;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> pending = new();
    /// <summary>Runs a packet's handler on the main thread, unless the player already has too many waiting.
    /// Network handlers may run off the main thread, so this is the only work done per packet.</summary>
    void Admit(ICoreServerAPI api, IServerPlayer player, Action handle, string code)
    {
        string uid = player.PlayerUID;
        // Never below zero: a disconnect forgets the count while its callbacks are still queued.
        if (pending.AddOrUpdate(uid, 1, (_, n) => n + 1) > PendingPerPlayer) { pending.AddOrUpdate(uid, 0, (_, n) => Math.Max(0, n - 1)); return; }
        api.Event.EnqueueMainThreadTask(() =>
        {
            pending.AddOrUpdate(uid, 0, (_, n) => Math.Max(0, n - 1));
            if (!ended) handle();
        }, code);
    }
    WmapConfig Load(ICoreAPI api)
    {
        WmapConfig cfg = null;
        try { cfg = api.LoadModConfig<WmapConfig>("wmap.json"); } catch (Exception e) { api.Logger.Warning("[wmap] Invalid config: {0}", e.Message); }
        cfg ??= new(); cfg.Validate(); api.StoreModConfig(cfg, "wmap.json"); return cfg;
    }
    static T Register<T>(T channel) where T : class, INetworkChannel
    {
        channel.RegisterMessageType<AtlasHello>().RegisterMessageType<AtlasInfo>().RegisterMessageType<TileRequest>().RegisterMessageType<TileResponse>()
            .RegisterMessageType<BaseRequest>().RegisterMessageType<BaseChunk>()
            // Appended: earlier message types keep their numbers for older peers.
            .RegisterMessageType<TeleportRequest>().RegisterMessageType<AutoMarkRequest>()
            .RegisterMessageType<MapDraw>().RegisterMessageType<MapPiece>().RegisterMessageType<MapRead>()
            .RegisterMessageType<OreHeatRequest>().RegisterMessageType<OreHeatResponse>()
            .RegisterMessageType<ExploreUpdate>().RegisterMessageType<ChartPatch>();
        return channel;
    }
    public override void Start(ICoreAPI api) => api.RegisterItemClass(PaperMapItem.ClassName, typeof(PaperMapItem));
    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api; config = Load(api);
        api.Permissions.RegisterPrivilege("wmap", "Use the Wmap development atlas and predicted terrain", false);
        var channel = Register(api.Network.RegisterChannel(WmapProtocol.Channel));
        server = new(api, channel, config);
        // Ore heat maps for players with the full map (WM-D47): sampled off the main thread, sent from the tick.
        // Tiles are stored in the world's cache folder beside the atlas's, under the same size limit (WM-D48).
        var oreHeat = new OreHeatServer(api, channel, server.Allowed, () => server.Source?.Info.Topology(), () => server.Source?.Samplers?.Invoke(),
            Math.Clamp(config.Threads / 2, 1, 4), () => server.CacheFolder, config.DiskCacheMiB);
        if (config.OreHeatMaps) server.OreHeat = oreHeat; // off: no codes offered, no atlas drop-down (WM-0025)
        channel.SetMessageHandler<OreHeatRequest>((p, r) => { if (config.OreHeatMaps) Admit(api, p, () => oreHeat.Request(p, r), "wmap-ore-heat"); });
        // Checks a heat map against the pick where you stand (WM-0011): this map's answer worked out at your column now, beside
        // the prospecting pick's own reading there. For players with the full map.
        api.ChatCommands.Create("wmapore").WithDescription("World Map: the ore heat map at your column beside the prospecting pick's reading there")
            .RequiresPrivilege(Privilege.chat).RequiresPlayer().WithArgs(api.ChatCommands.Parsers.OptionalWord("ore"))
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || !server.Allowed(player)) return TextCommandResult.Error("Needs the full map (the wmap privilege or AllowAllPlayers)");
                if (!config.OreHeatMaps) return TextCommandResult.Error("Ore heat maps are switched off (OreHeatMaps in wmap.json)");
                var codes = oreHeat.Codes;
                if (codes.Length == 0) return TextCommandResult.Error("This world has no ore heat maps");
                string code = args[0] as string;
                var pos = player.Entity.Pos.AsBlockPos;
                if (string.IsNullOrEmpty(code))
                    return TextCommandResult.Success(string.Join("\n", Array.ConvertAll(codes, c => oreHeat.Compare(c, pos))));
                return oreHeat.Compare(code, pos) is { } line ? TextCommandResult.Success(line) : TextCommandResult.Error("Unknown ore; these have maps: " + string.Join(", ", codes));
            });
        channel.SetMessageHandler<AtlasHello>((p, h) => Admit(api, p, () => server.Hello(p, h), "wmap-hello"));
        channel.SetMessageHandler<TileRequest>((p, r) => Admit(api, p, () => server.Request(p, r), "wmap-tile-request"));
        // Chart requests are queued under a per-player cap without main-thread work; Tick sends them.
        channel.SetMessageHandler<BaseRequest>((p, r) => { if (!ended) server.RequestBase(p, r); });
        channel.SetMessageHandler<TeleportRequest>((p, r) => Admit(api, p, () => server.Teleport(p, r), "wmap-teleport"));
        var autoMarks = new AutoMarkServer(api, config);
        channel.SetMessageHandler<AutoMarkRequest>((p, r) => Admit(api, p, () => autoMarks.Request(p, r), "wmap-automark"));
        var paperMaps = new PaperMapServer(api, channel, config, () => server.Source?.Info.Topology(), server.Exploration);
        // A drawing's upload is put together on arrival, without main-thread work (there may be hundreds of pieces).
        channel.SetMessageHandler<MapDraw>((p, d) => { if (!ended) paperMaps.Draw(p, d); });
        channel.SetMessageHandler<MapPiece>((p, m) => { if (!ended) paperMaps.Piece(p, m); });
        channel.SetMessageHandler<MapRead>((p, r) => Admit(api, p, () => paperMaps.Read(p, r), "wmap-map-read"));
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            try
            {
                // World config is available now. Load() has already saved the global defaults;
                // these per-world overrides remain in memory and use the existing AtlasInfo sync.
                config.ApplyWorldOptions(api.World.Config.GetBool);
                server.Start();
            }
            catch (Exception e) { api.Logger.Error("[wmap] Could not initialize atlas: {0}", e); }
        });
        serverTick = api.Event.RegisterGameTickListener(_ => { server.Tick(); paperMaps.Tick(); oreHeat.Tick(); }, 50);
        // Players' charted areas are kept in the world save (WM-D63). Never unsubscribed in Dispose: the server saves after the
        // mods are disposed.
        api.Event.GameWorldSave += () => server.Exploration.Save();
        api.Event.PlayerDisconnect += p => { server.Leave(p.PlayerUID); paperMaps.Leave(p.PlayerUID); oreHeat.Leave(p.PlayerUID); pending.TryRemove(p.PlayerUID, out _); };
        // A grant saved with the player (a crash, a removed ship mod) must not outlive its source.
        api.Event.PlayerNowPlaying += p =>
        {
            var attributes = p.Entity?.WatchedAttributes;
            if (attributes?.HasAttribute(WindSightAttribute) != true) return;
            attributes.RemoveAttribute(WindSightAttribute);
            attributes.MarkPathDirty(WindSightAttribute);
        };
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () => { ended = true; paperMaps.Stop(); oreHeat.Dispose(); server.Dispose(); });
    }
    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api; config = Load(api);
        // Start each client session in Chart, including the minimap before the atlas is first opened.
        config.DefaultStyle = 0;
        if (config.ReplaceMinimap && api.ModLoader.GetModSystem<WorldMapManager>() is { } maps)
            WmapMinimapLayer.Register(maps);
        clientChannel = Register(api.Network.RegisterChannel(WmapProtocol.Channel))
            .SetMessageHandler<AtlasInfo>(info => api.Event.EnqueueMainThreadTask(() => { if (!ended) ReceiveInfo(info); }, "wmap-info"))
            .SetMessageHandler<TileResponse>(r => api.Event.EnqueueMainThreadTask(() =>
            {
                if (ended || client == null) return;
                // Far terrain's tiles come on their own stream; the atlas takes only its own.
                if (r.Stream == 1) client.Far?.Receive(r); else client.Tiles.Receive(r);
            }, "wmap-response"))
            .SetMessageHandler<BaseChunk>(c => api.Event.EnqueueMainThreadTask(() => { if (!ended) client?.ReceiveChart(c); }, "wmap-chart"))
            // A survival client's exploration and its blocks of the chart (0.5.4, WM-D63).
            .SetMessageHandler<ExploreUpdate>(u => api.Event.EnqueueMainThreadTask(() => { if (!ended) client?.ReceiveExplore(u); }, "wmap-explore"))
            .SetMessageHandler<ChartPatch>(c => api.Event.EnqueueMainThreadTask(() => { if (!ended) client?.ReceivePatch(c); }, "wmap-chart-patch"))
            .SetMessageHandler<MapPiece>(m => api.Event.EnqueueMainThreadTask(() => { if (!ended) client?.PaperMaps.Receive(m); }, "wmap-map-piece"))
            .SetMessageHandler<OreHeatResponse>(r => api.Event.EnqueueMainThreadTask(() => { if (!ended) client?.OreHeat.Receive(r); }, "wmap-ore-heat"));
        api.ChatCommands.Create("wmap").WithDescription("Open the Wmap world atlas (map key, normally M)").HandleWith(_ => { Open(); return TextCommandResult.Success(); })
            .BeginSubCommand("vanilla").WithDescription("Open the original Vintage Story map")
            .HandleWith(_ => { dialog?.TryClose(); return mapKey?.OpenVanilla() == true ? TextCommandResult.Success() : TextCommandResult.Error("The original map is unavailable in this world."); })
            .EndSubCommand()
            // The mid-distance view (WM-P06 prototype): its parts switched live, to compare them in game.
            .BeginSubCommand("mid").WithDescription("Mid-distance view (prototype): show what it draws, or switch recorded terrain, terraces or lights on or off")
            .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("part", "recorded", "terraces", "lights"), api.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing) return TextCommandResult.Success("Wmap mid-distance view: " + (client?.Far?.MidStatus() ?? "far terrain is not running"));
                string part = (string)args[0];
                bool Switch(bool now) => args.Parsers[1].IsMissing ? !now : (bool)args[1];
                if (part == "recorded") config.MidDistanceRecorded = Switch(config.MidDistanceRecorded);
                else if (part == "terraces") config.MidDistanceTerraces = Switch(config.MidDistanceTerraces);
                else config.MidDistanceLights = Switch(config.MidDistanceLights);
                api.StoreModConfig(config, "wmap.json");
                return TextCommandResult.Success("Wmap mid-distance view: " + (client?.Far?.MidStatus() ?? "far terrain is not running"));
            })
            .EndSubCommand()
            // Far terrain's colour on its own (0.5.3-rc.4), to compare it on screen with the game's ground: not saved.
            .BeginSubCommand("fardebug").WithDescription("Far terrain debug view: off, albedo (its colour alone) or lit (lit, without haze or fog)")
            .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("view", "off", "albedo", "lit"))
            .HandleWith(args =>
            {
                if (!args.Parsers[0].IsMissing) FarTerrain.DebugView = (string)args[0] switch { "albedo" => 1, "lit" => 2, _ => 0 };
                return TextCommandResult.Success("Wmap far terrain debug view: " + FarTerrain.DebugView switch { 1 => "albedo (its colour alone)", 2 => "lit, without haze or fog", _ => "off" });
            })
            .EndSubCommand();
        // Vanilla registers its map key during LevelFinalize. Bind after all finalize listeners
        // have run, otherwise vanilla overwrites our handler. Reuse its action and saved remapping.
        api.Event.LevelFinalize += () => api.Event.EnqueueMainThreadTask(() =>
        {
            if (ended) return;
            mapKey?.Dispose();
            if (api.Input.GetHotKeyByCode(MapKeyBinding.Code) == null)
                api.Input.RegisterHotKey(MapKeyBinding.Code, "Wmap: world atlas", GlKeys.M, HotkeyType.HelpAndOverlays);
            mapKey = new(api.Input.GetHotKeyByCode(MapKeyBinding.Code), Open);
            // Latitude, longitude and heading beside the minimap (0.4.9).
            if (api.Input.GetHotKeyByCode(CoordinatesHud.HotkeyCode) == null)
                api.Input.RegisterHotKey(CoordinatesHud.HotkeyCode, "Wmap: latitude and longitude", GlKeys.V, HotkeyType.HelpAndOverlays, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler(CoordinatesHud.HotkeyCode, _ =>
            {
                config.CoordinatesHud = !config.CoordinatesHud;
                api.StoreModConfig(config, "wmap.json");
                if (config.CoordinatesHud) coordinates?.TryOpen(); else coordinates?.TryClose();
                return true;
            });
            api.Logger.Notification("[wmap] Atlas bound to the world map action; .wmap also opens it.");
            clientChannel.SendPacket(new AtlasHello());
        }, "wmap-map-key");
        clientTick = api.Event.RegisterGameTickListener(_ =>
        {
            if (ended) return;
            try
            {
                client?.Tick();
                // A new game mode changes what the atlas offers (its Creative / Game button): build its window again.
                if (client?.FollowGameMode() == true) Rebuild();
            }
            catch (Exception e) { api.Logger.Error("[wmap] Client update failed: {0}", e); }
            if (wantOpen && client == null && Environment.TickCount64 - helloAt > 3000) { helloAt = Environment.TickCount64; clientChannel.SendPacket(new AtlasHello()); }
        }, 50);
        api.Event.LeaveWorld += CloseClient;
    }
    void Open()
    {
        if (client == null) { wantOpen = true; helloAt = Environment.TickCount64; clientChannel.SendPacket(new AtlasHello()); capi.ShowChatMessage("Wmap: requesting atlas access…"); return; }
        try
        {
            dialog ??= new AtlasDialog(capi, client);
            dialog.Toggle();
        }
        catch (Exception e)
        {
            capi.Logger.Error("[wmap] Could not open atlas: {0}", e);
            dialog?.TryClose(); dialog?.Dispose(); dialog = null;
            capi.ShowChatMessage("Wmap could not open its window. Details are in client-main.log. Use .wmap vanilla for the original map.");
        }
    }
    PaperMapDialog paperMap;
    /// <summary>A right-click with a paper map: draw on a blank one, read a drawn one (0.4.8).</summary>
    public void OpenPaperMap(Vintagestory.API.Common.ItemStack stack)
    {
        if (client == null || !client.Info.PaperMaps) { capi.ShowChatMessage("Wmap: this server does not take paper maps."); return; }
        var map = stack?.Attributes.GetTreeAttribute(PaperMapServer.Attribute);
        if (stack?.Collectible?.Code?.Path == PaperMapServer.DrawnCode && map == null) { capi.ShowChatMessage("Wmap: this map is blank inside."); return; }
        paperMap?.TryClose(); paperMap?.Dispose();
        paperMap = new PaperMapDialog(capi, client, stack?.Collectible?.Code?.Path == PaperMapServer.BlankCode ? null : map);
        paperMap.TryOpen();
    }
    void Rebuild()
    {
        if (dialog == null) return;
        bool open = dialog.IsOpened();
        dialog.TryClose(); dialog.Dispose(); dialog = null;
        if (open) Open();
    }
    void ReceiveInfo(AtlasInfo info)
    {
        if (!info.Allowed)
        {
            if (wantOpen) capi.ShowChatMessage(info.Declined != null ? "Wmap: the server declined the atlas: " + info.Declined + "."
                : "The server declined Wmap access. Check that client and server use the same Wmap version.");
            wantOpen = false; return;
        }
        if (info.Width <= 0 || info.Height <= 0 || !double.IsFinite(info.Width + info.Height + info.StartX) || info.CacheId?.Length != 64 || info.ObservationId?.Length != 64) return;
        // Cache folder names are fixed-format hashes, never server-supplied paths.
        if (!System.Text.RegularExpressions.Regex.IsMatch(info.CacheId, "^[a-f0-9]{64}$") || !System.Text.RegularExpressions.Regex.IsMatch(info.ObservationId, "^[a-f0-9]{64}$")) return;
        if (client?.Info.Session == info.Session)
        {
            client.Update(info);
            // The manifest asked for on a game-mode change arrives after the window was built again with the previous
            // one's permissions: leaving survival's window with creative's Creative / Game button (0.4.5).
            if (dialog != null && dialog.OffersFullMap != client.OffersFullMap) Rebuild();
            if (wantOpen) { wantOpen = false; Open(); }
            return;
        }
        // A new session: every window bound to the old client goes with it.
        dialog?.Dispose(); dialog = null; paperMap?.TryClose(); paperMap?.Dispose(); paperMap = null; coordinates?.TryClose(); coordinates?.Dispose(); coordinates = null; client?.Dispose();
        client = new WmapClient(capi, config, clientChannel, info);
        coordinates = new CoordinatesHud(capi, client);
        if (config.CoordinatesHud) coordinates.TryOpen();
        if (wantOpen) { wantOpen = false; Open(); }
    }
    void CloseClient()
    {
        ended = true; mapKey?.Dispose(); mapKey = null; paperMap?.TryClose(); paperMap?.Dispose(); paperMap = null; coordinates?.TryClose(); coordinates?.Dispose(); coordinates = null; dialog?.TryClose(); dialog?.Dispose(); dialog = null; client?.Dispose(); client = null;
    }
    public override void Dispose()
    {
        ended = true;
        if (capi != null) { capi.Event.UnregisterGameTickListener(clientTick); CloseClient(); }
        if (sapi != null) { sapi.Event.UnregisterGameTickListener(serverTick); server?.Dispose(); }
        base.Dispose();
    }
}
