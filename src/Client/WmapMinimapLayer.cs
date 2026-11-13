using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Wmap terrain over the HUD's vanilla terrain, below its native markers.</summary>
public sealed class WmapMinimapLayer : MapLayer, IDisposable
{
    WmapClient attached;
    TileAtlas atlas;
    readonly TileView tileView = new();
    readonly MapCamera camera = new();
    readonly MapFrame frame = new();
    public override string Title => "Wmap";
    public override string LayerGroupCode => "terrain";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public WmapMinimapLayer(ICoreAPI api, IWorldMapManager sink) : base(api, sink) { }

    public static void Register(WorldMapManager manager)
    {
        const string code = "wmapchart";
        manager.RegisterMapLayer<WmapMinimapLayer>(code, 0);
        // The HUD renders layers in registry order. Insert before markers so their
        // transparent quads cannot leave pieces of vanilla terrain above our chart.
        // Do this before LevelFinalize starts the engine's map worker thread.
        var ordered = new Dictionary<string, Type>();
        foreach (var layer in manager.MapLayerRegistry)
        {
            if (layer.Key == code) continue;
            ordered.Add(layer.Key, layer.Key == "players" ? typeof(WmapPlayerMapLayer) : layer.Value);
            if (layer.Key == "chunks") ordered.Add(code, typeof(WmapMinimapLayer));
        }
        if (!ordered.ContainsKey(code)) ordered.Add(code, typeof(WmapMinimapLayer));
        manager.MapLayerRegistry = ordered;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (!Active || mapSink is not WorldMapManager manager || manager.worldMapDlg?.DialogType != EnumDialogType.HUD) return;
        var system = api.ModLoader.GetModSystem<WmapSystem>();
        // The full atlas owns detail requests while open. Its window covers the HUD.
        // The explicit .wmap vanilla dialog continues to use vanilla's terrain.
        if (system == null || system.AtlasOpened || system.CurrentClient is not { } client ||
            !client.Config.ReplaceMinimap || client.Renderer.Error != null) return;
        if (map.Api.World.Player.Entity.Pos.Dimension != 0) return;
        Draw(client, map);
    }

    /// <summary>Draws in the HUD's existing scissor and view; does not own its controls or markers.</summary>
    public void Draw(WmapClient client, GuiElementMap map)
    {
        if (attached != client)
        {
            atlas?.Dispose(); attached = client; atlas = new TileAtlas(client.Tiles, 1); tileView.Reset();
        }
        var t = client.Topology;
        var bounds = map.Bounds;
        var view = map.CurrentBlockViewBounds;
        camera.X = (view.X1 + view.X2) / 2; camera.Z = (view.Z1 + view.Z2) / 2;
        camera.Scale = Math.Max(1e-6, map.ZoomLevel);
        bool predict = client.Info.CanPredict;
        double texel = client.Renderer.BaseReady ? client.Renderer.BaseTexel : predict && client.Info.BaseWidth > 0 ? t.Width / client.Info.BaseWidth : 0;
        var policy = new ViewPolicy(texel, predict, true, client.Tiles.HasObservation,
            Math.Min(client.Config.ResidentTiles - 16, TileAtlas.SlotsPerPage - 8), client.Config.DetailBias == 1, client.Info.FlatPrediction);
        tileView.Update(client.Tiles, atlas, t, camera, bounds.InnerWidth, bounds.InnerHeight, policy, client.Renderer.BaseReady ? client.Renderer.BaseTexel : 0);
        client.Renderer.UpdateMask(client.Mask);
        frame.X = bounds.renderX; frame.Y = bounds.renderY;
        frame.Width = bounds.InnerWidth; frame.Height = bounds.InnerHeight;
        frame.Camera = camera; frame.Topology = t;
        frame.Style = client.Config.DefaultStyle; frame.Development = client.Tiles.Development;
        frame.Gui = (float)GuiElement.scaled(1);
        frame.HeightScale = client.Info.MapHeight > client.Info.SeaLevel ? client.Info.MapHeight - client.Info.SeaLevel : 200;
        frame.HeightRange = client.Info.ChartHeightRange;
        frame.MeridianStep = GeoGrid.Step(t.Width / 360 * camera.Scale, 110 * frame.Gui);
        frame.ParallelStep = GeoGrid.Step((client.Info.PoleDistance > 0 ? client.Info.PoleDistance / 90 : t.Height / 180) * camera.Scale, 110 * frame.Gui);
        var shader = client.Api.Render.CurrentActiveShader;
        shader?.Stop();
        try
        {
            if (!client.Renderer.Begin(client.Api.Render, frame, !frame.Development)) return;
            try
            {
                client.Renderer.DrawWorld(51);
                float fade = client.Renderer.BaseReady ? (float)Math.Clamp(client.Renderer.BaseTexel / 2 * camera.Scale - 1, 0, 1) : 1;
                int n = 0;
                foreach (var tile in tileView.Display)
                    atlas.Draw(tile, client.Renderer, ViewTiles.Opacity(tile.Key, fade), 51.05f + .005f * n++, tileView.Reference(tile));
                // The same worldwide selected layer as the atlas; gameplay requires a barometer.
                var player = client.Api.World?.Player?.Entity?.Pos;
                string sight = frame.Development || player == null ? null : client.Sight.Source(client.Api.World?.Player);
                if (player != null && client.Winds.Prepare(frame.Development, sight))
                    client.Renderer.DrawWinds(57, !WindGrid.IsWeather(client.Config.WindLayer), 48);
            }
            finally { client.Renderer.End(); }
        }
        finally
        {
            client.Api.Render.GlToggleBlend(true);
            shader?.Use();
        }
    }

    public override void OnMapClosedClient()
    {
        if (attached != null && api.ModLoader.GetModSystem<WmapSystem>()?.AtlasOpened != true) attached.Tiles.CloseView();
    }
    public override void Dispose() { atlas?.Dispose(); atlas = null; attached = null; tileView.Reset(); }
}
