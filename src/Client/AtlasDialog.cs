using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Wmap.Core;

namespace Wmap.Client;

public sealed class AtlasDialog : GuiDialog
{
    readonly WmapClient client;
    readonly WmapConfig config;
    readonly AtlasElement map;
    /// <summary>Whether this window was built with the Creative / Game button (<see cref="WmapClient.OffersFullMap"/>).</summary>
    public readonly bool OffersFullMap;
    public override string ToggleKeyCombinationCode => MapKeyBinding.Code;
    // WmapSystem owns the handler, including initialization and the original-map fallback.
    public override void OnBlockTexturesLoaded() { }
    public AtlasDialog(ICoreClientAPI api, WmapClient client) : base(api)
    {
        this.client = client; config = client.Config;
        double scale = GuiElement.scaled(1);
        // Nearly the whole screen: the map is the point of this window.
        double width = Math.Clamp(api.Render.FrameWidth / scale - 80, 500, 2400);
        double height = Math.Clamp(api.Render.FrameHeight / scale - 244, 200, 1500);
        const int mapY = 108;
        var mapBounds = ElementBounds.Fixed(0, mapY, width, height);
        // A fill-sized background cannot determine the size of an autosized parent.
        // Fit the fixed-size controls instead, as the game's own map dialog does.
        var bg = ElementStdBounds.DialogBackground(12, 12);
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        map = new AtlasElement(api, mapBounds, client, UpdateText);
        map.Owner = this;
        try
        {
            SingleComposer = api.Gui.CreateCompo("wmap-atlas", dialogBounds);
            SingleComposer.AddShadedDialogBG(bg).AddDialogTitleBar("World Map", () => TryClose())
            .BeginChildElements(bg)
            .AddSmallButton("Whole world", () => { map.Fit(); return true; }, ElementBounds.Fixed(0, 34, 120, 28))
            .AddSmallButton("Follow", () => { map.Follow = !map.Follow; map.Center(); return true; }, ElementBounds.Fixed(128, 34, 80, 28))
            .AddSmallButton("Chart / Terrain", () => { map.SwitchStyle(); return true; }, ElementBounds.Fixed(216, 34, 140, 28));
            double x = 364;
            // The full (creative) map and the game chart: offered in creative mode or with the server's wmap privilege.
            // The window is built again when the game mode changes (WmapSystem).
            OffersFullMap = client.OffersFullMap;
            if (OffersFullMap)
            {
                SingleComposer.AddSmallButton("Creative / Game", () => { map.SwitchMode(); return true; }, ElementBounds.Fixed(x, 34, 150, 28), key: "mode");
                x += 158;
            }
            SingleComposer.AddSmallButton("° / °′", () => { config.DecimalDegrees = !config.DecimalDegrees; api.StoreModConfig(config, "wmap.json"); return true; }, ElementBounds.Fixed(x, 34, 70, 28))
            .AddSmallButton("Day / Night", () => { config.DayNightOverlay = !config.DayNightOverlay; api.StoreModConfig(config, "wmap.json"); return true; }, ElementBounds.Fixed(x + 78, 34, 110, 28));
            x += 196;
            // Worldwide weather and monthly prevailing winds; gameplay requires a Jonas barometer.
            double labelX = x + 6;
            if (client.Winds.Available)
            {
                var choices = Enumerable.Range(0, WindGrid.Choices).ToArray();
                SingleComposer.AddDropDown(choices.Select(c => c.ToString()).ToArray(), choices.Select(c => "Winds: " + WindGrid.Name(c)).ToArray(), config.WindLayer,
                    (code, _) => { config.WindLayer = int.Parse(code); api.StoreModConfig(config, "wmap.json"); }, ElementBounds.Fixed(x, 34, 200, 28), "winds");
                labelX = x + 214;
            }
            SingleComposer.AddDynamicText(ModeLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, 39, Math.Max(100, width - labelX), 25), "modeLabel")
            .AddStaticText("Show:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 75, 46, 25))
            .AddMultiSelectDropDown(ShowCodes, new[] { "Your waypoints", "Auto markers", "Prospecting", "Other layers" }, 0, (code, on) => { Show(code, on); Save(); },
                ElementBounds.Fixed(48, 70, 230, 28), "show")
            .AddDropDown(OreCodes(), OreNames(OreCodes()), 0, (code, _) => { config.ProspectingOre = code; Save(); }, ElementBounds.Fixed(286, 70, 200, 28), "ores")
            .AddStaticText("Auto-mark:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(500, 75, 84, 25))
            .AddMultiSelectDropDown(AutoCodes(), new[] { "When harvesting", "When sneaking" }.Concat(AutoMarkCatalog.All.Select(c => c.Name)).ToArray(), 0,
                (code, on) => { AutoMark(code, on); Save(); }, ElementBounds.Fixed(586, 70, 260, 28), "automark");
            // Ore heat maps on the full map (WM-D47): offered with it, when the server works them out.
            if (OffersFullMap && client.Info.OreHeatCodes is { Length: > 0 } heat)
            {
                // The listed-ores map needs the pick's own factors (host rock counted): not from vanilla's ore maps alone.
                var first = client.Info.OreHeatMethod == OreHeatRequest.Reading ? new[] { "", OreHeat.Listed } : new[] { "" };
                var codes = first.Concat(heat.Distinct().OrderBy(c => OreHeatLayer.Name(c), StringComparer.CurrentCultureIgnoreCase)).ToArray();
                SingleComposer.AddDropDown(codes, codes.Select(c => "Ore heat: " + OreHeatLayer.Name(c)).ToArray(), Math.Max(0, Array.IndexOf(codes, client.OreHeat.Code)),
                    (code, _) => client.OreHeat.Select(code), ElementBounds.Fixed(854, 70, 240, 28), "oreheat");
            }
            SingleComposer.AddInteractiveElement(map, "atlas")
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, height + mapY + 8, width, 25), "position")
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, height + mapY + 32, width, 25), "status")
            .AddStaticText("Drag to pan · Wheel to zoom · Space: follow · Home: whole world · Right-click: add or edit a waypoint (creative: or teleport) · Esc: close", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, height + mapY + 58, width, 20))
            .EndChildElements().Compose();
            SingleComposer.GetDropDown("show").SetSelectedValue(ShowCodes.Where(Shows).ToArray());
            SingleComposer.GetDropDown("automark").SetSelectedValue(AutoCodes().Where(AutoMarks).ToArray());
            SingleComposer.GetDropDown("ores").SetSelectedValue(config.ProspectingOre);
            map.Start();
        }
        catch
        {
            // The constructor can fail before the caller receives the dialog reference.
            // Release partially composed textures and the tile invalidation subscription here.
            map.Dispose();
            base.Dispose();
            throw;
        }
    }
    // ------------------------------------------------------------------ filters (0.4.6)

    static readonly string[] ShowCodes = { "waypoints", "auto", "prospecting", "layers" };
    bool Shows(string code) => code switch
    {
        "waypoints" => config.ShowWaypoints, "auto" => config.ShowAutoMarks, "prospecting" => config.ShowProspecting, _ => config.ShowOtherLayers
    };
    void Show(string code, bool on)
    {
        switch (code)
        {
            case "waypoints": config.ShowWaypoints = on; break;
            case "auto": config.ShowAutoMarks = on; break;
            case "prospecting": config.ShowProspecting = on; break;
            default: config.ShowOtherLayers = on; break;
        }
    }
    static string[] AutoCodes() => new[] { "use", "sneak" }.Concat(AutoMarkCatalog.All.Select(c => c.Code)).ToArray();
    bool AutoMarks(string code) => code == "use" ? config.AutoMarkOnUse : code == "sneak" ? config.AutoMarkOnSneak : config.AutoMarkKinds.Contains(code);
    void AutoMark(string code, bool on)
    {
        if (code == "use") config.AutoMarkOnUse = on;
        else if (code == "sneak") config.AutoMarkOnSneak = on;
        else config.AutoMarkKinds = on ? config.AutoMarkKinds.Append(code).Distinct().ToArray() : config.AutoMarkKinds.Where(k => k != code).ToArray();
    }
    // The ores the player's readings mention, and the one chosen even when no reading mentions it any more.
    string[] OreCodes() => new[] { "" }.Concat(map.Markers.Ores()).Append(config.ProspectingOre).Distinct().ToArray();
    static string[] OreNames(string[] codes) => codes.Select(c => c.Length == 0 ? "Ores: all" : "Ores: " + Lang.Get("ore-" + c)).ToArray();
    void Save() => capi.StoreModConfig(config, "wmap.json");

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Readings come and go while the window is closed.
        var codes = OreCodes();
        var ores = SingleComposer?.GetDropDown("ores");
        if (ores == null) return;
        ores.SetList(codes, OreNames(codes));
        ores.SetSelectedValue(config.ProspectingOre);
    }

    void UpdateText(string position, string status)
    {
        SingleComposer?.GetDynamicText("position")?.SetNewText(position);
        SingleComposer?.GetDynamicText("status")?.SetNewText(status);
        SingleComposer?.GetDynamicText("modeLabel")?.SetNewText(ModeLabel());
    }
    string ModeLabel() => (client.Tiles.Development ? "Creative" : "Game") + " · " + WmapConfig.StyleName(config.DefaultStyle);
    public override void OnGuiClosed() { client.Tiles.CloseView(); map.ReleaseDrag(); base.OnGuiClosed(); }
    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape && map.CloseMenu()) args.Handled = true;
        else if (args.KeyCode == (int)GlKeys.Space) { map.Follow = true; map.Center(); args.Handled = true; }
        else if (args.KeyCode == (int)GlKeys.Home) { map.Fit(); args.Handled = true; }
        else base.OnKeyDown(args);
    }
}

public sealed class AtlasElement : GuiElement
{
    readonly WmapClient client;
    readonly WmapConfig config;
    readonly Action<string, string> text;
    readonly TileAtlas atlas;
    readonly MapCamera camera = new();
    readonly TileView view = new();
    readonly MapFrame frame = new();
    double targetScale = 1, anchorX, anchorY;
    bool dragging;
    int prevX, prevY;
    float update;
    // Gameplay: the source of wind sight (a barometer), and whether its winds are drawn this frame.
    string sight;
    bool winds;
    public bool Follow;
    TileClient Tiles => client.Tiles;
    Topology Topology => client.Topology;
    public AtlasElement(ICoreClientAPI api, ElementBounds bounds, WmapClient client, Action<string, string> text) : base(api, bounds)
    { this.client = client; config = client.Config; this.text = text; atlas = new(client.Tiles, config.AtlasPages); markers = new(api, config); }
    public override void ComposeElements(Context ctx, ImageSurface surface) { Bounds.CalcWorldBounds(); }
    public void Start()
    {
        Bounds.CalcWorldBounds();
        config.DefaultStyle = 0;
        camera.Scale = MapCamera.ClampScale(Topology, Bounds.InnerWidth, Bounds.InnerHeight, 0.5 * GuiElement.scaled(1));
        targetScale = camera.Scale;
        Follow = true;
        Center();
    }
    public void Fit()
    {
        Bounds.CalcWorldBounds();
        camera.Fit(Topology, Bounds.InnerWidth, Bounds.InnerHeight);
        targetScale = camera.Scale; Follow = false;
    }
    public void Center()
    {
        var pos = api.World?.Player?.Entity?.Pos;
        if (pos == null) return;
        camera.X = Topology.Nearest(pos.X, camera.X); camera.Z = pos.Z;
        camera.Constrain(Topology);
    }
    public void SwitchStyle()
    {
        config.DefaultStyle = (config.DefaultStyle + 1) % WmapConfig.StyleCount;
        api.StoreModConfig(config, "wmap.json"); update = 1;
    }
    public void SwitchMode()
    {
        if (!Tiles.Development && !Tiles.Info.CanDevelop)
        {
            api.ShowChatMessage(client.Creative ? "Wmap: waiting for the server to open the full map." : "The full map needs creative mode, the server's wmap privilege or its AllowAllPlayers setting.");
            return;
        }
        Tiles.SetDevelopment(!Tiles.Development);
        config.DevelopmentMode = Tiles.Development;
        api.StoreModConfig(config, "wmap.json");
        update = 1;
    }
    public void ReleaseDrag() { dragging = rightDown = false; CloseMenu(); }

    public override void RenderInteractiveElements(float deltaTime)
    {
        var t = Topology;
        double w = Bounds.InnerWidth, h = Bounds.InnerHeight;
        // Smooth, cursor-anchored zoom towards the wheel target.
        if (Math.Abs(Math.Log(targetScale / camera.Scale)) > 1e-4)
        {
            double step = Math.Pow(targetScale / camera.Scale, 1 - Math.Exp(-deltaTime * 14));
            camera.Zoom(t, w, h, step, anchorX, anchorY);
        }
        if (Follow) Center();
        var player = api.World.Player.Entity.Pos;
        bool surface = player.Dimension == 0;
        bool development = Tiles.Development;
        // Predicted detail when the server shares it, with recorded terrain drawn over it.
        bool predict = Tiles.Info.CanPredict;
        // While the world chart is on its way, detail coarser than it would be wasted work.
        double chartTexel = client.Renderer.BaseReady ? client.Renderer.BaseTexel : predict && Tiles.Info.BaseWidth > 0 ? t.Width / Tiles.Info.BaseWidth : 0;
        var policy = new ViewPolicy(chartTexel, predict, true, Tiles.HasObservation,
            Math.Min(config.ResidentTiles - 16, config.AtlasPages * TileAtlas.SlotsPerPage - 8), config.DetailBias == 1, Tiles.Info.FlatPrediction);
        // GL uploads happen outside the scissor and the shader.
        view.Update(Tiles, atlas, t, camera, w, h, policy, client.Renderer.BaseReady ? client.Renderer.BaseTexel : 0, surface);
        client.Renderer.UpdateMask(client.Mask);
        if (client.Day.Take() is { } daylight) client.Renderer.SetDay(daylight, DayGrid.Width, DayGrid.Height, client.Day.Wraps);
        if (surface && development)
        {
            double halfW = w / 2 / camera.Scale, halfH = h / 2 / camera.Scale;
            client.OreHeat.View(t, camera.X - halfW, camera.Z - halfH, camera.X + halfW, camera.Z + halfH, camera.Scale);
        }
        sight = development || !surface ? null : client.Sight.Source(api.World.Player);
        winds = surface && client.Winds.Prepare(development, sight);
        Draw(player, surface, development);
        if (surface) DrawOtherLayers(deltaTime);
        update += deltaTime;
        if (update >= .25f) { update = 0; Status(player, surface, development); }
    }

    void Draw(Vintagestory.API.Common.Entities.EntityPos player, bool surface, bool development)
    {
        var t = Topology;
        frame.X = Bounds.renderX; frame.Y = Bounds.renderY; frame.Width = Bounds.InnerWidth; frame.Height = Bounds.InnerHeight;
        frame.Camera = camera; frame.Topology = t; frame.Style = config.DefaultStyle; frame.Development = development;
        frame.DayNight = config.DayNightOverlay && surface;
        frame.Gui = (float)GuiElement.scaled(1);
        frame.HeightScale = Tiles.Info.MapHeight > Tiles.Info.SeaLevel ? Tiles.Info.MapHeight - Tiles.Info.SeaLevel : 200;
        frame.HeightRange = Tiles.Info.ChartHeightRange;
        double minPixels = 110 * frame.Gui;
        frame.MeridianStep = GeoGrid.Step(t.Width / 360 * camera.Scale, minPixels);
        frame.ParallelStep = GeoGrid.Step((Tiles.Info.PoleDistance > 0 ? Tiles.Info.PoleDistance / 90 : t.Height / 180) * camera.Scale, minPixels);
        api.Render.PushScissor(Bounds);
        var shader = api.Render.CurrentActiveShader;
        shader?.Stop();
        try
        {
            if (!client.Renderer.Begin(api.Render, frame, !development)) return;
            try
            {
                // The composer's static background is at depth 50. Stay in front
                // of it even when a previous renderer switched LEQUAL to LESS.
                client.Renderer.DrawWorld(51);
                // Detail fades in as the zoom passes the world raster's own resolution.
                float fade = client.Renderer.BaseReady ? (float)Math.Clamp((client.Renderer.BaseTexel / 2 * camera.Scale - 1) / 1.0, 0, 1) : 1;
                int n = 0;
                // Observations are authoritative for every terrain provider, at every zoom; a finer prediction refines
                // only the recorded cells that confirm it (WM-D41).
                foreach (var tile in view.Display)
                    atlas.Draw(tile, client.Renderer, ViewTiles.Opacity(tile.Key, fade), 51.05f + 0.005f * n++, view.Reference(tile));
                if (surface && winds) client.Renderer.DrawWinds(57, !WindGrid.IsWeather(config.WindLayer));
                if (surface) DrawPlayer(player);
                double rose = Math.Min(64 * frame.Gui, Math.Min(frame.Width, frame.Height) / 7);
                client.Renderer.DrawCompass(frame.X + 18 * frame.Gui + rose, frame.Y + frame.Height - 18 * frame.Gui - rose, rose, 59);
            }
            finally { client.Renderer.End(); }
        }
        finally
        {
            api.Render.GlToggleBlend(true);
            shader?.Use();
            try
            {
                // Over the terrain and under the winds (57), the player (58) and the compass (59); full map only.
                if (surface && development) client.OreHeat.Draw(t, camera, frame.X, frame.Y, frame.Width, frame.Height, 56);
                if (surface) { Annotate(); if (development) DrawOreLegend(); DrawMenu(); }
            }
            finally { api.Render.PopScissor(); }
        }
    }

    void DrawPlayer(Vintagestory.API.Common.Entities.EntityPos player)
    {
        var t = Topology;
        // Every visible copy of the player on a wrapped world.
        double px = t.Nearest(player.X, camera.X), radius = 9 * frame.Gui;
        for (int k = t.Wraps ? -1 : 0; k <= (t.Wraps ? 1 : 0); k++)
        {
            double x = frame.X + frame.Width / 2 + (px + k * t.Width - camera.X) * camera.Scale;
            double y = frame.Y + frame.Height / 2 + (player.Z - camera.Z) * camera.Scale;
            if (x > frame.X - 20 && x < frame.X + frame.Width + 20) client.Renderer.DrawPlayer(x, y, player.Yaw, radius, 58);
        }
    }

    // ------------------------------------------------------------------ other mods' map layers

    // Vanilla map layers other than terrain and players (entities, other mods' layers such as Magelan's ships) drawn
    // over the atlas through a vanilla map element that mirrors the atlas camera: a layer renders with
    // map.TranslateWorldPosToViewPos, map.Bounds and map.ZoomLevel, which this keeps in step. Waypoints and prospecting
    // readings are drawn by Wmap (AtlasMarkers), so they follow the zoom and the filters.
    GuiElementMap overlay;
    readonly List<MapLayer> overlayLayers = new();
    readonly HashSet<MapLayer> failedLayers = new();
    readonly AtlasMarkers markers;
    string hover = "";
    bool markersFailed;

    static bool IsOverlay(MapLayer layer) => layer is not ChunkMapLayer && layer is not PlayerMapLayer && layer is not WmapMinimapLayer
        && layer is not WaypointMapLayer && layer is not OreMapLayer;

    /// <summary>The vanilla map element for the other layers, its view on one copy of the ring: the layers place their
    /// markers at canonical world positions, so copy <paramref name="lap"/> (laps east of the camera's own) is drawn by
    /// shifting the view bounds a circumference west. Null without the game's map.</summary>
    GuiElementMap Overlay(int lap = 0)
    {
        var manager = api.ModLoader?.GetModSystem<WorldMapManager>();
        if (manager?.MapLayers == null) return null;
        overlayLayers.Clear();
        if (config.ShowOtherLayers) foreach (var layer in manager.MapLayers) if (IsOverlay(layer) && !failedLayers.Contains(layer)) overlayLayers.Add(layer);
        overlay ??= new GuiElementMap(overlayLayers, api, null, Bounds, false);
        var (x1, x2, halfH) = RingCopies.View(Topology, camera, Bounds.InnerWidth, Bounds.InnerHeight, lap);
        overlay.ZoomLevel = RingCopies.LayerZoom(camera.Scale);
        overlay.CurrentBlockViewBounds = new Vintagestory.API.MathTools.Cuboidd(x1, 0, camera.Z - halfH, x2, 0, camera.Z + halfH);
        return overlay;
    }

    /// <summary>Whether copy <paramref name="lap"/> draws a pinned waypoint at <paramref name="x"/>: the first copy that
    /// shows it, or the first copy, pinned at its edge, when none does. Drawn once, not at the edge of every copy.</summary>
    bool PinnedHere(double x, int lap)
    {
        var (first, last) = RingCopies.Laps(Topology, camera, Bounds.InnerWidth);
        for (int k = first; k <= last; k++)
        {
            var (x1, x2, _) = RingCopies.View(Topology, camera, Bounds.InnerWidth, Bounds.InnerHeight, k);
            if (x >= x1 && x <= x2) return k == lap;
        }
        return lap == first;
    }

    void DrawOtherLayers(float dt)
    {
        if (Overlay() == null) return;
        var (first, last) = RingCopies.Laps(Topology, camera, Bounds.InnerWidth);
        api.Render.PushScissor(Bounds);
        try
        {
            // Each copy of the ring the view shows; time advances once per frame.
            for (int lap = first; lap <= last; lap++)
            {
                var map = Overlay(lap);
                foreach (var layer in overlayLayers)
                {
                    if (!layer.Active) continue;
                    try { layer.Render(map, lap == first ? dt : 0); }
                    catch (Exception e)
                    {
                        // one bad layer doesn't take the atlas down
                        failedLayers.Add(layer);
                        api.Logger.Warning("[wmap] Map layer {0} failed to draw on the atlas and is left out: {1}", layer.GetType().Name, e.Message);
                    }
                }
                int here = lap;
                try { markers.Draw(map, camera.Scale, api.Input.MouseX, api.Input.MouseY, x => PinnedHere(x, here)); }
                catch (Exception e)
                {
                    if (!markersFailed) api.Logger.Warning("[wmap] Waypoints could not be drawn on the atlas: {0}", e);
                    markersFailed = true;
                }
            }
        }
        finally { api.Render.PopScissor(); }
    }

    void HoverOtherLayers(MouseEvent args)
    {
        if (!Bounds.PointInside(args.X, args.Y)) { hover = ""; return; }
        // The copy of the ring under the cursor.
        int lap = RingCopies.LapAt(Topology, camera, Bounds.InnerWidth, args.X - Bounds.absX);
        if (Overlay(lap) is not { } map) { hover = ""; return; }
        var text = new System.Text.StringBuilder();
        try { text.AppendLine(markers.Hover(map, camera.Scale, args.X, args.Y, x => PinnedHere(x, lap))); } catch (Exception) { }
        foreach (var layer in overlayLayers)
        {
            if (!layer.Active) continue;
            try { layer.OnMouseMoveClient(args, map, text); } catch (Exception) { }
        }
        hover = text.ToString().Trim().Replace("\r", "").Replace("\n", " · ");
    }

    /// <summary>A right-click goes to the markers and layers first, as on the game's own map: a waypoint opens its editor,
    /// a prospecting reading asks to be deleted. True when one took it.</summary>
    bool RightClickOtherLayers(MouseEvent args)
    {
        int lap = RingCopies.LapAt(Topology, camera, Bounds.InnerWidth, args.X - Bounds.absX);
        if (Overlay(lap) is not { } map) return false;
        if (markers.RightClick(map, camera.Scale, args.X, args.Y, x => PinnedHere(x, lap))) { args.Handled = true; return true; }
        foreach (var layer in overlayLayers)
        {
            if (!layer.Active) continue;
            try { layer.OnMouseUpClient(args, map); }
            catch (Exception e) { api.Logger.Warning("[wmap] Map layer {0} failed to take a click on the atlas: {1}", layer.GetType().Name, e.Message); }
            if (args.Handled) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ waypoints

    GuiDialog owner;
    /// <summary>The atlas window, given focus back when a waypoint dialog closes.</summary>
    public GuiDialog Owner { get => owner; set { owner = value; markers.Owner = value; } }
    GuiDialogAddWayPoint addWaypoint;
    /// <summary>The atlas's waypoints and prospecting readings: their filters and ore codes for the window's controls.</summary>
    public AtlasMarkers Markers => markers;

    /// <summary>Opens the game's own waypoint dialog for the map point x, z: its icons, colours and /waypoint command,
    /// so the marker is the server's, shared with the minimap, the player's group and other mods.</summary>
    void AddWaypoint(double x, double z)
    {
        if (markers.Waypoints is not { } layer) return;
        addWaypoint?.TryClose(); addWaypoint?.Dispose();
        // The ground where its chunk is loaded (as the game's map), sea level elsewhere.
        int ground = api.World.BlockAccessor.GetRainMapHeightAt((int)x, (int)z);
        addWaypoint = new GuiDialogAddWayPoint(api, layer) { WorldPos = new Vintagestory.API.MathTools.Vec3d(x, (ground > 0 ? ground : Tiles.Info.SeaLevel) + 1, z) };
        addWaypoint.TryOpen();
        addWaypoint.OnClosed += () => { if (Owner?.IsOpened() == true) api.Gui.RequestFocus(Owner); };
    }

    // ------------------------------------------------------------------ graticule labels and scale bar

    readonly Dictionary<string, LoadedTexture> labels = new();
    int labelStyle = -1;
    LoadedTexture Label(string value)
    {
        if (labels.TryGetValue(value, out var texture)) return texture;
        double[] color = config.DefaultStyle == 0 ? new[] { 0.30, 0.17, 0.10, 1 } : new[] { 1.0, 1, 1, 1 };
        var font = new CairoFont(13 * GuiElement.scaled(1), GuiStyle.StandardFontName, color);
        if (config.DefaultStyle != 0) font.WithStroke(new[] { 0, 0, 0, 0.7 }, 2);
        return labels[value] = api.Gui.TextTexture.GenUnscaledTextTexture(value, font);
    }

    // The labels drawn last frame and what they were worked out for: a still map draws them again without sampling the
    // geography along its edges (about 1,300 lookups) or formatting their text.
    readonly List<(LoadedTexture Label, double X, double Y, float Depth)> annotations = new();
    (double X, double Z, double Scale, double Left, double Top, double Width, double Height, double Meridians, double Parallels, int Style, bool Geography, float Gui, bool Decimals) annotated;
    double barNice, barUnit;

    void Annotate()
    {
        var geography = client.Geography;
        var key = (camera.X, camera.Z, camera.Scale, frame.X, frame.Y, frame.Width, frame.Height, frame.MeridianStep, frame.ParallelStep, config.DefaultStyle, geography != null, frame.Gui, config.DecimalDegrees);
        if (annotations.Count == 0 || key != annotated)
        {
            annotated = key;
            BuildAnnotations(geography);
        }
        foreach (var (label, x, y, depth) in annotations)
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x, y, label.Width, label.Height, depth);
        // Scale bar: sea miles (one minute of latitude) when a geographic datum exists, blocks otherwise.
        double gui = frame.Gui, right = frame.X + frame.Width, bottom = frame.Y + frame.Height;
        double length = barNice * barUnit * camera.Scale, barX = right - length - 20 * gui, barY = bottom - 26 * gui, barH = 5 * gui;
        int ink = config.DefaultStyle == 0 ? unchecked((int)0xff3b2914) : unchecked((int)0xffffffff), paper = config.DefaultStyle == 0 ? unchecked((int)0xfff1e6cc) : unchecked((int)0xff202830);
        api.Render.RenderRectangle((float)(barX - gui), (float)(barY - gui), 61, (float)(length + 2 * gui), (float)(barH + 2 * gui), ink);
        for (int i = 0; i < 4; i++)
            api.Render.RenderRectangle((float)(barX + i * length / 4), (float)barY, 62, (float)(length / 4), (float)barH, i % 2 == 0 ? ink : paper);
    }

    /// <summary>The ore heat map's colours and what they mean, at the top right under the meridian labels.</summary>
    void DrawOreLegend()
    {
        var heat = client.OreHeat;
        if (heat.Code.Length == 0 || heat.Level < 0) return;
        bool listed = heat.Code == OreHeat.Listed;
        string[] ticks = listed ? new[] { "0", "2", "4", "6", "8+" } : new[] { "1", "10", "100", "1k", "10k" };
        double gui = frame.Gui, right = frame.X + frame.Width, box = 30 * gui, height = 10 * gui;
        double x0 = right - ticks.Length * box - 16 * gui, y0 = frame.Y + 44 * gui;
        var title = Label(listed ? "Ores the prospecting pick lists" : OreHeatLayer.Name(heat.Code) + ": ore blocks per chunk");
        api.Render.Render2DTexturePremultipliedAlpha(title.TextureId, right - 16 * gui - title.Width, y0 - title.Height - 2 * gui, title.Width, title.Height, 62);
        for (int i = 0; i < ticks.Length; i++)
        {
            api.Render.RenderRectangle((float)(x0 + i * box), (float)y0, 62, (float)box, (float)height, OreHeat.Argb(i / (ticks.Length - 1.0), listed));
            var label = Label(ticks[i]);
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x0 + i * box + (box - label.Width) / 2, y0 + height + gui, label.Width, label.Height, 62);
        }
    }

    void BuildAnnotations(System.Func<double, double, GeoPosition> geography)
    {
        annotations.Clear();
        // Labels are cached by text; the cache is emptied only here, before any of this frame's labels are taken.
        if (labelStyle != config.DefaultStyle || labels.Count > 200) { foreach (var l in labels.Values) l.Dispose(); labels.Clear(); labelStyle = config.DefaultStyle; }
        var t = Topology;
        double gui = frame.Gui, scale = camera.Scale, left = frame.X, top = frame.Y, right = frame.X + frame.Width, bottom = frame.Y + frame.Height;
        double WorldX(double sx) => camera.X + (sx - left - frame.Width / 2) / scale;
        double WorldZ(double sy) => camera.Z + (sy - top - frame.Height / 2) / scale;
        if (geography != null)
        {
            // Meridians: sample longitude along the top edge and label each multiple of the step.
            double zEdge = Math.Clamp(WorldZ(top + 2), 1, t.Height - 1), previous = double.NaN, lastLabel = double.NegativeInfinity;
            for (double sx = left; sx <= right; sx += 3)
            {
                var g = geography(t.Canonical(WorldX(sx)), zEdge);
                if (g.InGutter) { previous = double.NaN; continue; }
                double lon = g.Longitude;
                if (!double.IsNaN(previous))
                {
                    while (lon - previous > 180) lon -= 360;
                    while (lon - previous < -180) lon += 360;
                    double k = Math.Floor(previous / frame.MeridianStep), at = (k + 1) * frame.MeridianStep;
                    if (at > previous && at <= lon && sx - lastLabel > 60 * gui)
                    {
                        double x = sx - 3 * (lon - at) / Math.Max(1e-9, lon - previous);
                        double wrapped = ((at + 540) % 360) - 180;
                        var label = Label(GeoFormat.GridLabel(wrapped, false, frame.MeridianStep, config.DecimalDegrees));
                        annotations.Add((label, x - label.Width / 2.0, top + 4 * gui, 60));
                        lastLabel = sx;
                    }
                }
                previous = lon;
            }
            // Parallels: latitude along the left edge.
            double xEdge = t.Canonical(WorldX(left + 2));
            previous = double.NaN; lastLabel = double.NegativeInfinity;
            for (double sy = top; sy <= bottom; sy += 3)
            {
                double z = WorldZ(sy);
                if (z < 0 || z >= t.Height) { previous = double.NaN; continue; }
                var g = geography(xEdge, z);
                if (g.BeyondPole) { previous = double.NaN; continue; }
                if (!double.IsNaN(previous))
                {
                    double lo = Math.Min(previous, g.Latitude), hi = Math.Max(previous, g.Latitude);
                    double at = Math.Ceiling(lo / frame.ParallelStep) * frame.ParallelStep;
                    if (at <= hi && at > lo && sy - lastLabel > 30 * gui && sy > top + 24 * gui)
                    {
                        var label = Label(GeoFormat.GridLabel(at, true, frame.ParallelStep, config.DecimalDegrees));
                        annotations.Add((label, left + 6 * gui, sy - label.Height - 1, 60));
                        lastLabel = sy;
                    }
                }
                previous = g.Latitude;
            }
        }
        double blocksPerMile = Tiles.Info.PoleDistance > 0 && geography != null ? Tiles.Info.PoleDistance / 90 / 60 : 0;
        double unit = blocksPerMile > 0 ? blocksPerMile : 1, target = 160 * gui / scale / unit, nice = 1;
        foreach (double m in new[] { 1.0, 2, 5 })
            for (double p = 1; p <= 1e6; p *= 10) if (m * p <= target) nice = Math.Max(nice, m * p);
        barNice = nice; barUnit = unit;
        double length = nice * unit * scale, barX = right - length - 20 * gui, barY = bottom - 26 * gui;
        string caption = blocksPerMile > 0 ? $"{nice:0.##} sea miles · {nice * blocksPerMile:0} blocks" : $"{nice:0} blocks";
        var text = Label(caption);
        annotations.Add((text, barX + length - text.Width, barY - text.Height - 2 * gui, 62));
    }

    void Status(Vintagestory.API.Common.Entities.EntityPos player, bool surface, bool development)
    {
        var t = Topology;
        var geography = client.Geography;
        double wx = camera.X + (api.Input.MouseX - Bounds.absX - Bounds.InnerWidth / 2) / camera.Scale;
        double wz = camera.Z + (api.Input.MouseY - Bounds.absY - Bounds.InnerHeight / 2) / camera.Scale;
        string Position(double x, double z) => geography != null ? GeoFormat.Format(geography(t.Canonical(x), z), config.DecimalDegrees) : $"X {t.Canonical(x):0}, Z {z:0}";
        string position = !surface ? "Atlas available in the surface dimension only" : "You: " + Position(player.X, player.Z);
        if (surface && Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) && wz >= 0 && wz < t.Height) position += "    Cursor: " + Position(wx, wz);
        if (surface && hover.Length > 0) position += "    " + hover;
        bool cursor = surface && Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) && wz >= 0 && wz < t.Height;
        bool windSight = development || sight != null;
        if (cursor && windSight && client.Winds.Describe(t.Canonical(wx), wz) is { } wind) position += "    " + wind;
        if (cursor && development && client.OreHeat.Describe(t, wx, wz, Tiles.Info.OreHeatMethod) is { } ore) position += "    " + ore;
        // The chosen worldwide weather or prevailing-wind layer also supplies the player's reading.
        bool barometer = surface && !development && sight != null && client.Winds.Available && config.WindLayer != WindGrid.Off;
        if (barometer && client.Winds.Reading(player.X, player.Z) is { } here)
        {
            position += $"    Wind ({WindGrid.Name(config.WindLayer)}) here: " + here;
        }
        // The barometer's own reading: Earth's air pressure where you stand and whether it is rising or falling (WM-0006).
        if (surface && windSight && client.Winds.Barometer(player.X, player.Z) is { } pressure) position += "    Barometer: " + pressure;
        string source = development ? Tiles.Info.Source : "Charted by you";
        string chart = client.Renderer.Error != null ? "Map shader unavailable: " + client.Renderer.Error
            : client.BaseMap.Status.Length > 0 ? client.BaseMap.Status
            : !client.Renderer.BaseReady && client.BaseMap.Id != null ? $"Preparing world chart… {client.Renderer.BaseUploadProgress:P0}" : "";
        string status = $"{source} · {1 / camera.Scale:0.#} blocks/px · {(Follow ? "following" : "free pan")} · {Tiles.Pending} tiles pending · {atlas.Count} detail tiles";
        if (chart.Length > 0) status = chart + " · " + status;
        if (Tiles.LastError != null) status += " · " + Tiles.LastError;
        if (development && client.OreHeat.Status.Length > 0) status += " · " + client.OreHeat.Status;
        if (client.Winds.Available && config.WindLayer != WindGrid.Off && !development && surface)
            status += barometer ? $" · worldwide {WindGrid.Name(config.WindLayer)} from {sight}" : " · carry a Jonas barometer to read the winds";
        text(position, status);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (menu != null)
        {
            var m = menu.Value;
            bool onMenu = args.X >= m.X && args.X < m.X + m.W && args.Y >= m.Y && args.Y < m.Y + (m.Items.Length + 1) * m.Row;
            int row = (int)Math.Floor((args.Y - m.Y) / m.Row) - 1;
            if (onMenu && args.Button == EnumMouseButton.Left && row >= 0 && row < m.Items.Length)
            {
                CloseMenu();
                if (m.Items[row] == MenuItem.Waypoint) AddWaypoint(m.WorldX, m.WorldZ);
                else if (client.CanTeleport) client.SendTeleport(m.WorldX, m.WorldZ);
                args.Handled = true; return;
            }
            // Its title does nothing; a click anywhere else closes it and acts as usual (a right-click reopens it there).
            if (onMenu) { args.Handled = true; return; }
            CloseMenu();
        }
        // A right-click acts when the button comes up, as on the game's own map.
        if (args.Button == EnumMouseButton.Right) { rightDown = true; args.Handled = true; return; }
        if (args.Button != EnumMouseButton.Left) return;
        dragging = true; Follow = false; prevX = args.X; prevY = args.Y; args.Handled = true;
    }

    void RightClick(MouseEvent args)
    {
        if ((api.World?.Player?.Entity?.Pos?.Dimension ?? 0) != 0) return;
        if (RightClickOtherLayers(args)) return;
        OpenMenu(args.X, args.Y);
    }

    // ------------------------------------------------------------------ right-click menu

    // A small menu at the right-clicked point (screen pixels): its position, then its items for a left click.
    public enum MenuItem { Waypoint, Teleport }
    record struct Menu(double X, double Y, double W, double Row, double WorldX, double WorldZ, string Title, MenuItem[] Items);
    Menu? menu;
    bool rightDown;
    LoadedTexture menuTitle;
    LoadedTexture[] menuItems;
    static string ItemText(MenuItem item) => item == MenuItem.Waypoint ? "Add waypoint…" : "Teleport here";
    public bool MenuOpen => menu != null;
    /// <summary>A menu item in screen pixels while the menu is open and offers it.</summary>
    public (double X, double Y, double W, double H)? MenuItemBounds(MenuItem item) =>
        menu is { } m && Array.IndexOf(m.Items, item) is var i and >= 0 ? (m.X, m.Y + (i + 1) * m.Row, m.W, m.Row) : null;

    /// <summary>Acts on the map point under screen pixel x, y (on the surface, inside the world): a waypoint where the game
    /// has its waypoint layer, a teleport in creative. One choice, adding a waypoint, opens its dialog at once; teleporting
    /// always goes through the menu, so a stray right-click never moves the player. True when it did either.</summary>
    public bool OpenMenu(int screenX, int screenY)
    {
        CloseMenu();
        if ((api.World?.Player?.Entity?.Pos?.Dimension ?? 0) != 0) return false;
        double x = camera.X + (screenX - Bounds.absX - Bounds.InnerWidth / 2) / camera.Scale;
        double z = camera.Z + (screenY - Bounds.absY - Bounds.InnerHeight / 2) / camera.Scale;
        if (z < 0 || z >= Topology.Height) return false;
        var items = new List<MenuItem>();
        if (markers.Waypoints != null) items.Add(MenuItem.Waypoint);
        if (client.CanTeleport && Core.Teleport.Column(Topology, x, z, int.MaxValue, int.MaxValue) != null) items.Add(MenuItem.Teleport);
        x = Topology.Canonical(x);
        if (items.Count == 0) return false;
        if (items.Count == 1 && items[0] == MenuItem.Waypoint) { AddWaypoint(x, z); return true; }
        var geography = client.Geography;
        string title = geography != null ? GeoFormat.Format(geography(x, z), config.DecimalDegrees) : $"X {x:0}, Z {z:0}";
        double gui = GuiElement.scaled(1), pad = 8 * gui, row = 22 * gui;
        var font = MenuFont(false);
        double w = Math.Max(font.GetTextExtents(title).Width, items.Max(i => font.GetTextExtents(ItemText(i)).Width)) + 2 * pad, h = (items.Count + 1) * row;
        // At the cursor, kept inside the map.
        double mx = Math.Clamp(screenX + 2, Bounds.absX, Math.Max(Bounds.absX, Bounds.absX + Bounds.InnerWidth - w));
        double my = Math.Clamp(screenY + 2, Bounds.absY, Math.Max(Bounds.absY, Bounds.absY + Bounds.InnerHeight - h));
        menu = new Menu(mx, my, w, row, x, z, title, items.ToArray());
        return true;
    }

    public bool CloseMenu()
    {
        menuTitle?.Dispose(); menuTitle = null;
        if (menuItems != null) foreach (var t in menuItems) t?.Dispose();
        menuItems = null;
        bool open = menu != null;
        menu = null;
        return open;
    }

    // Dark ink on chart paper: white text was hard to read over the map.
    static CairoFont MenuFont(bool dim) =>
        new(13 * GuiElement.scaled(1), GuiStyle.StandardFontName, dim ? new[] { 0.36, 0.30, 0.24, 1 } : new[] { 0.10, 0.08, 0.06, 1 });

    void DrawMenu()
    {
        if (menu is not { } m) return;
        // Leaving creative takes the teleport away.
        if (!client.CanTeleport && m.Items.Contains(MenuItem.Teleport)) { CloseMenu(); return; }
        menuTitle ??= api.Gui.TextTexture.GenUnscaledTextTexture(m.Title, MenuFont(true));
        menuItems ??= m.Items.Select(i => api.Gui.TextTexture.GenUnscaledTextTexture(ItemText(i), MenuFont(false))).ToArray();
        double gui = GuiElement.scaled(1), pad = 8 * gui;
        // Screen (mouse) pixels to render pixels.
        float x = (float)(Bounds.renderX + m.X - Bounds.absX), y = (float)(Bounds.renderY + m.Y - Bounds.absY), w = (float)m.W, row = (float)m.Row;
        int n = m.Items.Length;
        api.Render.RenderRectangle(x - 1, y - 1, 70, w + 2, (n + 1) * row + 2, unchecked((int)0xff3b2914));
        api.Render.RenderRectangle(x, y, 71, w, (n + 1) * row, unchecked((int)0xfff3ead4));
        api.Render.Render2DTexturePremultipliedAlpha(menuTitle.TextureId, x + pad, y + (row - menuTitle.Height) / 2, menuTitle.Width, menuTitle.Height, 73);
        for (int i = 0; i < n; i++)
        {
            float top = y + (i + 1) * row;
            bool hover = api.Input.MouseX >= m.X && api.Input.MouseX < m.X + m.W && api.Input.MouseY >= m.Y + (i + 1) * m.Row && api.Input.MouseY < m.Y + (i + 2) * m.Row;
            if (hover) api.Render.RenderRectangle(x, top, 72, w, row, unchecked((int)0xffd9c79e));
            api.Render.RenderRectangle(x + 2, top, 72, w - 4, 1, unchecked((int)0xff9a8a6a));
            var label = menuItems[i];
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x + pad, top + (row - label.Height) / 2, label.Width, label.Height, 73);
        }
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (!dragging) { HoverOtherLayers(args); return; }
        camera.X -= (args.X - prevX) / camera.Scale; camera.Z -= (args.Y - prevY) / camera.Scale;
        prevX = args.X; prevY = args.Y; camera.Constrain(Topology); args.Handled = true;
    }
    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging) { dragging = false; args.Handled = true; }
        if (args.Button == EnumMouseButton.Right && rightDown)
        {
            rightDown = false;
            if (Bounds.PointInside(args.X, args.Y)) RightClick(args);
            args.Handled = true;
        }
    }
    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (!Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;
        CloseMenu();
        Follow = false;
        anchorX = api.Input.MouseX - Bounds.absX; anchorY = api.Input.MouseY - Bounds.absY;
        targetScale = MapCamera.ClampScale(Topology, Bounds.InnerWidth, Bounds.InnerHeight, targetScale * Math.Pow(1.4, args.delta));
        args.SetHandled(true);
    }
    public override void Dispose()
    {
        CloseMenu();
        addWaypoint?.TryClose(); addWaypoint?.Dispose(); markers.Dispose();
        // The overlay element owns nothing of the layers it was given (its Dispose is empty in 1.22).
        overlay?.Dispose(); overlay = null;
        atlas.Dispose();
        foreach (var l in labels.Values) l.Dispose();
        labels.Clear(); annotations.Clear();
        base.Dispose();
    }
}
