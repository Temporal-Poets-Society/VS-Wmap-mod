using System;
using System.IO;
using Path = System.IO.Path;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Wmap;
using Wmap.Client;

// Real AtlasDialog, GuiComposer, bounds, controls and Cairo composition. Only the
// client services and final GPU texture upload are simulated in this server test.
public class GuiServiceProxy : DispatchProxy
{
    public System.Func<MethodInfo, object[], object> Call;
    protected override object Invoke(MethodInfo method, object[] args) => Call(method, args);
    public static T Make<T>(System.Func<MethodInfo, object[], object> call) where T : class
    {
        var proxy = Create<T, GuiServiceProxy>();
        ((GuiServiceProxy)(object)proxy).Call = call;
        return proxy;
    }
    public static object Default(MethodInfo method) => method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
}
public static class DialogChecks
{
    public static string Run(string output, ILogger logger)
    {
        float oldScale = RuntimeEnv.GUIScale;
        int composed = 0;
        string menuResult = "";
        try
        {
            foreach (var size in new[] { (1280, 720, 1f), (1920, 1080, 1.5f), (3840, 2160, 2f), (800, 600, 1f) })
            {
                RuntimeEnv.GUIScale = size.Item3;
                ICoreClientAPI api = null;
                bool injectFailure = false;
                int failureMessages = 0;
                var assets = GuiServiceProxy.Make<IAssetManager>((method, args) =>
                {
                    if (method.Name != "Get") return GuiServiceProxy.Default(method);
                    var location = (AssetLocation)args[0];
                    byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "assets", location.Domain, location.Path.Replace('/', Path.DirectorySeparatorChar)));
                    return GuiServiceProxy.Make<IAsset>((m, a) => m.Name == "get_Data" ? bytes : GuiServiceProxy.Default(m));
                });
                var render = GuiServiceProxy.Make<IRenderAPI>((method, args) => method.Name switch
                {
                    "get_FrameWidth" => size.Item1,
                    "get_FrameHeight" => size.Item2,
                    "BitmapCreateFromPng" => new BitmapExternal((byte[])args[0], ((byte[])args[0]).Length, logger),
                    _ => GuiServiceProxy.Default(method)
                });
                GuiComposer composer = null;
                var loaded = new List<GuiDialog>();
                var gui = GuiServiceProxy.Make<IGuiAPI>((method, args) =>
                {
                    if (method.Name == "get_LoadedGuis") return loaded;
                    if (method.Name == "RegisterDialog") { loaded.AddRange((GuiDialog[])args[0]); return null; }
                    if (method.Name == "get_Icons") return new IconUtil(api);
                    if (method.Name == "CreateCompo")
                    {
                        if (injectFailure) throw new InvalidOperationException("Injected GUI construction failure for regression test");
                        var bounds = (ElementBounds)args[1];
                        var window = ElementBounds.Fixed(0, 0, size.Item1 / size.Item3, size.Item2 / size.Item3).WithEmptyParent();
                        window.CalcWorldBounds(); window.WithChild(bounds);
                        composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { api, bounds, args[0] }, null);
                        return composer;
                    }
                    if (method.Name == "LoadOrUpdateCairoTexture")
                    {
                        var surface = (ImageSurface)args[0];
                        if (surface.Width <= 0 || surface.Height <= 0) throw new Exception("Invalid GUI texture extent");
                        var texture = (LoadedTexture)args[2];
                        texture.Width = surface.Width; texture.Height = surface.Height;
                        if (composer != null && surface.Width == (int)composer.Bounds.OuterWidth && surface.Height == (int)composer.Bounds.OuterHeight)
                            surface.WriteToPng(Path.Combine(output, $"dialog-{size.Item1}-{size.Item2}-{size.Item3}.png"));
                        return null;
                    }
                    return GuiServiceProxy.Default(method);
                });
                var entity = new EntityPlayer();
                entity.Pos.X = 73000; entity.Pos.Y = 150; entity.Pos.Z = 41000;
                // IClientPlayer has an internal default interface method, so DispatchProxy cannot implement it.
                var player = (Vintagestory.Client.NoObf.ClientPlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientPlayer));
                var playerData = (Vintagestory.Client.NoObf.ClientWorldPlayerData)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientWorldPlayerData));
                playerData.EntityPlayer = entity; playerData.CurrentGameMode = EnumGameMode.Creative;
                typeof(Vintagestory.Client.NoObf.ClientPlayer).GetField("worlddata", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, playerData);
                var world = GuiServiceProxy.Make<IClientWorldAccessor>((m, a) => m.Name == "get_Player" ? player : GuiServiceProxy.Default(m));
                api = GuiServiceProxy.Make<ICoreClientAPI>((method, args) =>
                {
                    if (method.Name == "ShowChatMessage") { failureMessages++; return null; }
                    return method.Name switch
                    {
                        "get_Render" => render,
                        "get_World" => world,
                        "get_Gui" => gui,
                        "get_Assets" => assets,
                        "get_Logger" => logger,
                        "GetOrCreateDataPath" => Directory.CreateDirectory(Path.Combine(output, "gui-data", (string)args[0])).FullName,
                        _ => GuiServiceProxy.Default(method)
                    };
                });
                var channel = DispatchProxy.Create<IClientNetworkChannel, FakeChannel>();
                var info = new AtlasInfo { Session = "gui", CacheId = new string('a', 64), ObservationId = new string('b', 64), Width = 204800, Height = 102400, Wraps = true, Allowed = true, CanDevelop = true, CanPredict = true, Teleport = true,
                    OreHeatCodes = new[] { "nativecopper", "cassiterite" }, OreHeatMethod = OreHeatRequest.Reading };
                var config = new WmapConfig { DefaultStyle = 1 };
                using var client = new WmapClient(api, config, channel, info);
                var tiles = client.Tiles;
                using var dialog = new AtlasDialog(api, client);
                if (dialog.SingleComposer.GetElement("mode") == null) throw new Exception("Dev/gameplay button is missing");
                if (dialog.SingleComposer.GetDropDown("oreheat") == null) throw new Exception("Ore heat map choice is missing with the full map");
                var atlasElement = (AtlasElement)dialog.SingleComposer.GetElement("atlas");
                var camera = (Wmap.Core.MapCamera)typeof(AtlasElement).GetField("camera", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(atlasElement);
                if (!atlasElement.Follow || config.DefaultStyle != 0 || camera.X != entity.Pos.X || camera.Z != entity.Pos.Z ||
                    Math.Abs(camera.Scale - 0.5 * size.Item3) > 1e-6)
                    throw new Exception("Startup must select Chart, center/follow the player and zoom to two blocks per GUI pixel");
                atlasElement.SwitchMode();
                if (tiles.Development) throw new Exception("Gameplay button failed");
                atlasElement.SwitchMode();
                if (!tiles.Development) throw new Exception("Development button failed");
                var boundsMap = dialog.SingleComposer.GetElement("atlas").Bounds;
                if (!dialog.SingleComposer.Composed || boundsMap.InnerWidth < 1 || boundsMap.InnerHeight < 1) throw new Exception("Atlas composition failed");
                if (boundsMap.drawX < 0 || boundsMap.drawY < 0 || boundsMap.drawX + boundsMap.OuterWidth > composer.Bounds.OuterWidth || boundsMap.drawY + boundsMap.OuterHeight > composer.Bounds.OuterHeight)
                    throw new Exception("Atlas viewport falls outside the dialog surface");
                dialog.SingleComposer.ReCompose();
                if (composed == 0) menuResult = CheckOreHeat(client, (FakeChannel)(object)channel) + "; ";
                if (composed == 0) menuResult += CheckMenu(dialog, (AtlasElement)dialog.SingleComposer.GetElement("atlas"), (FakeChannel)(object)channel, api,
                    mode => client.GameMode = () => mode);
                for (int n = 0; n < 3; n++)
                {
                    dialog.TryOpen();
                    if (!dialog.IsOpened()) throw new Exception("Dialog did not open");
                    dialog.TryClose();
                    if (dialog.IsOpened()) throw new Exception("Dialog did not close");
                }
                if (composed == 0)
                {
                    var system = new WmapSystem();
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(WmapSystem).GetField("capi", flags).SetValue(system, api);
                    typeof(WmapSystem).GetField("client", flags).SetValue(system, client);
                    typeof(WmapSystem).GetField("config", flags).SetValue(system, new WmapConfig());
                    injectFailure = true;
                    typeof(WmapSystem).GetMethod("Open", flags).Invoke(system, null);
                    injectFailure = false;
                    if (failureMessages != 1) throw new Exception("Opening failure was not contained and explained to the player");
                }
                composed++;
            }
        }
        finally { RuntimeEnv.GUIScale = oldScale; }
        return $"GUI composition: PASS ({composed} window/scale combinations, real AtlasDialog and Cairo, Chart/player-center/follow/local-zoom startup, repeated open/close, recompose/disposal, opening-failure containment; {menuResult}; GPU upload simulated)";
    }

    /// <summary>The ore heat map's client side (WM-D47, tiled in WM-D48): nothing asked while off or while the view moves;
    /// once settled, one request with a coarse tile first and then every tile of the view; answers decoded (the GPU upload is
    /// simulated) and described under the cursor; a missing tile drawn from the coarse one until it arrives; a pan over the
    /// same tiles asks nothing new; zooming out and switching ores ask again.</summary>
    static string CheckOreHeat(WmapClient client, FakeChannel channel)
    {
        var heat = client.OreHeat;
        var t = client.Topology;
        const int Count = Wmap.Core.OreHeat.TileCount;
        double x = t.StartX + t.Width / 3, z = t.Height / 2;
        void See(double dx = 0, double scale = 0.5, double half = 800) => heat.View(t, x - half + dx, z - half / 2, x + half + dx, z + half / 2, scale);
        void Settle() => System.Threading.Thread.Sleep(OreHeatLayer.SettleMillis + 50);
        List<(int L, int X, int Z)> Keys(OreHeatRequest r) => Enumerable.Range(0, r.X?.Length ?? 0).Select(i => (r.Level[i], r.X[i], r.Z[i])).ToList();
        OreHeatResponse Answer((int L, int X, int Z) k, string code = "nativecopper")
        {
            var blocks = new float[Count]; var factors = new float[Count];
            for (int i = 0; i < Count; i++) { blocks[i] = i % 3 == 0 ? 0 : i + k.L; factors[i] = (i % 10) / 40f; }
            blocks[1] = factors[1] = float.NaN;
            return new OreHeatResponse { Code = code, Level = k.L, X = k.X, Z = k.Z, Blocks = blocks, Factors = factors, Millis = 1234 };
        }
        while (channel.OreHeat.TryDequeue(out _)) { }
        See();
        if (!channel.OreHeat.IsEmpty) throw new Exception("Ore heat map asked for while off");
        heat.Select("nativecopper");
        See();
        if (!channel.OreHeat.IsEmpty) throw new Exception("Ore heat map asked for before the view settled");
        Settle(); See();
        if (!channel.OreHeat.TryDequeue(out var request) || !channel.OreHeat.IsEmpty) throw new Exception("Settled view did not ask for exactly one set of ore heat tiles");
        int level = heat.Level;
        var own = Wmap.Core.OreHeat.Visible(t, x - 800, z - 400, x + 800, z + 400, level);
        var keys = Keys(request);
        if (request.Code != "nativecopper" || request.Level.Length != keys.Count || keys.Count == 0 || keys.Any(k => !Wmap.Core.OreHeat.ValidKey(t, k.L, k.X, k.Z)))
            throw new Exception("Ore heat request malformed");
        int coarse = keys.TakeWhile(k => k.L > level).Count();
        if (coarse == 0 || keys.Skip(coarse).Any(k => k.L != level) || keys.Count - coarse != own.Count || !own.All(v => keys.Contains((level, v.Key.X, v.Key.Z))))
            throw new Exception("Ore heat request should ask for a coarse tile first, then every tile of the view: " + string.Join(" ", keys));
        // Every tile but one arrives, and the coarse ones.
        var missing = own[^1];
        foreach (var k in keys) if (k != (level, missing.Key.X, missing.Key.Z)) heat.Receive(Answer(k));
        var first = own[0];
        double cell = Wmap.Core.OreHeat.CellSize(level);
        string text = heat.Describe(t, first.X + 2.5 * cell, first.Z + 1.5 * cell, OreHeatRequest.Reading);
        if (text == null || !text.Contains(Wmap.Core.OreHeat.Short(Wmap.Core.OreHeat.TileCells + 2 + level)) || !text.Contains("factor") || text.Contains("until")) throw new Exception("Ore heat hover text wrong: " + text);
        if (heat.Describe(t, first.X + 1.5 * cell, first.Z + 0.5 * cell, OreHeatRequest.Reading)?.Contains("no answer") != true) throw new Exception("Ore heat hover without an answer");
        if (!heat.Status.Contains($"{own.Count - 1} of {own.Count} tiles")) throw new Exception("Ore heat status does not count the tiles: " + heat.Status);
        // The missing tile is drawn from the coarse one meanwhile.
        heat.Draw(t, new Wmap.Core.MapCamera { X = x, Z = z, Scale = 0.5 }, 0, 0, 800, 400, 56);
        string stand = heat.Describe(t, missing.X + 0.5 * cell, missing.Z + 0.5 * cell, OreHeatRequest.Reading);
        if (stand?.Contains("until this tile arrives") != true) throw new Exception("Missing ore heat tile not drawn from a coarser one: " + stand);
        // A pan over the same tiles asks nothing new: the missing one is on its way.
        Settle(); See(10); Settle(); See(10);
        if (!channel.OreHeat.IsEmpty) throw new Exception("Small pan asked for ore heat tiles again");
        heat.Receive(Answer((level, missing.Key.X, missing.Key.Z)));
        if (heat.Describe(t, missing.X + 0.5 * cell, missing.Z + 0.5 * cell, OreHeatRequest.Reading)?.Contains("until") != false) throw new Exception("Arrived ore heat tile not shown");
        // Zooming out asks for the coarser level's tiles (the map follows every zoom).
        See(0, 0.5 / 16, 800 * 16); Settle(); See(0, 0.5 / 16, 800 * 16);
        if (!channel.OreHeat.TryDequeue(out var zoomed) || heat.Level <= level || Keys(zoomed).All(k => k.L != heat.Level)) throw new Exception("Zooming out did not ask for the coarser tiles");
        int outLevel = heat.Level;
        // Another ore asks for its own tiles.
        heat.Select("cassiterite");
        Settle(); See(0, 0.5 / 16, 800 * 16);
        if (!channel.OreHeat.TryDequeue(out var other) || other.Code != "cassiterite") throw new Exception("Switching ores did not ask for the new ore's tiles");
        while (channel.OreHeat.TryDequeue(out _)) { }
        int kept = heat.Count;
        heat.Select("");
        return $"ore heat client PASS ({own.Count} tiles of {cell:0}-block cells and {coarse} coarse; stand-in, zoom out to level {outLevel}, ore switch; {kept} tiles kept)";
    }

    /// <summary>Creative right-click menu: a right-click opens it without teleporting, a click elsewhere or Esc closes it,
    /// a left click on its item teleports to the right-clicked point; a survival player gets no menu.</summary>
    static string CheckMenu(AtlasDialog dialog, AtlasElement atlas, FakeChannel channel, ICoreClientAPI api, Action<EnumGameMode> setMode)
    {
        atlas.Fit();
        setMode(EnumGameMode.Creative);
        var b = atlas.Bounds;
        int cx = (int)(b.absX + b.InnerWidth / 2), cy = (int)(b.absY + b.InnerHeight / 2);
        // A right-click acts on release, as on the game's own map.
        void Click(int x, int y, EnumMouseButton button)
        {
            atlas.OnMouseDownOnElement(api, new MouseEvent(x, y, button, 0));
            if (button == EnumMouseButton.Right) atlas.OnMouseUp(api, new MouseEvent(x, y, button, 0));
        }
        Click(cx, cy, EnumMouseButton.Right);
        if (!atlas.MenuOpen || !channel.Teleports.IsEmpty) throw new Exception("Teleport menu: a right-click did not open the menu, or teleported at once");
        var item = atlas.MenuItemBounds(AtlasElement.MenuItem.Teleport).Value;
        if (item.X < b.absX || item.Y < b.absY || item.X + item.W > b.absX + b.InnerWidth + 1 || item.Y + item.H > b.absY + b.InnerHeight + 1 || item.W < 40)
            throw new Exception($"Teleport menu: its item {item} lies outside the map");
        Click((int)b.absX + 5, (int)b.absY + 5, EnumMouseButton.Left);
        if (atlas.MenuOpen || !channel.Teleports.IsEmpty) throw new Exception("Teleport menu: a click elsewhere did not close it, or teleported");
        Click(cx, cy, EnumMouseButton.Right);
        dialog.OnKeyDown(new KeyEvent { KeyCode = (int)GlKeys.Escape });
        if (atlas.MenuOpen) throw new Exception("Teleport menu: Esc did not close it");
        Click(cx, cy, EnumMouseButton.Right);
        item = atlas.MenuItemBounds(AtlasElement.MenuItem.Teleport).Value;
        Click((int)(item.X + item.W / 2), (int)(item.Y + item.H / 2), EnumMouseButton.Left);
        if (atlas.MenuOpen || channel.Teleports.Count != 1 || !channel.Teleports.TryDequeue(out var sent)) throw new Exception("Teleport menu: its item did not teleport once");
        // Fit() centres the camera on the world: the right-clicked centre is the world's centre.
        if (Math.Abs(sent.X - 102400) > 1 || Math.Abs(sent.Z - 51200) > 1) throw new Exception($"Teleport menu: teleported to {sent.X}, {sent.Z}, not the right-clicked point 102400, 51200");
        setMode(EnumGameMode.Survival);
        Click(cx, cy, EnumMouseButton.Right);
        if (atlas.MenuOpen) throw new Exception("Teleport menu: opened for a survival player");
        return "creative right-click menu (opens, closes on a click elsewhere and Esc, its item teleports to the clicked point; none in survival)";
    }
}
