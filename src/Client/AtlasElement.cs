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

public sealed partial class AtlasElement : GuiElement
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
    ViewPolicy policy;
    (TileClient, double, bool, int, bool, bool) policyKey;
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
        // Made again only when one of its inputs changed (0.5.3, WM-0042: every frame).
        var policyKey = (Tiles, chartTexel, predict, Math.Min(config.ResidentTiles - 16, config.AtlasPages * TileAtlas.SlotsPerPage - 8),
            config.DetailBias == 1, Tiles.Info.FlatPrediction);
        if (policy == null || policyKey != this.policyKey)
        {
            this.policyKey = policyKey;
            policy = new ViewPolicy(chartTexel, predict, true, Tiles.HasObservation, policyKey.Item4, policyKey.Item5, policyKey.Item6);
        }
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
