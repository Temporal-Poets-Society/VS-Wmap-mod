using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Use the atlas's heading arrow on the Wmap HUD, retaining native tracking and hover text.</summary>
public sealed class WmapPlayerMapLayer : PlayerMapLayer
{
    public WmapPlayerMapLayer(ICoreAPI api, IWorldMapManager sink) : base(api, sink) { }

    public override void Render(GuiElementMap map, float dt)
    {
        var system = api.ModLoader.GetModSystem<WmapSystem>();
        var client = system?.CurrentClient;
        if (mapSink is not WorldMapManager manager || manager.worldMapDlg?.DialogType != EnumDialogType.HUD ||
            client == null || !client.Config.ReplaceMinimap || client.Renderer.Error != null)
        { base.Render(map, dt); return; }
        if (!Active || system.AtlasOpened || map.Api.World.Player.Entity.Pos.Dimension != 0) return;
        var tracking = api.ModLoader.GetModSystem<SystemRemotePlayerTracking>();
        var view = map.CurrentBlockViewBounds;
        var frame = new MapFrame { X = map.Bounds.renderX, Y = map.Bounds.renderY, Width = map.Bounds.InnerWidth, Height = map.Bounds.InnerHeight,
            Camera = new MapCamera { X = (view.X1 + view.X2) / 2, Z = (view.Z1 + view.Z2) / 2, Scale = Math.Max(1e-6, map.ZoomLevel) },
            Topology = client.Topology, Style = client.Config.DefaultStyle, Development = client.Tiles.Development, Gui = (float)GuiElement.scaled(1) };
        var shader = client.Api.Render.CurrentActiveShader;
        shader?.Stop();
        try
        {
            if (!client.Renderer.Begin(client.Api.Render, frame, false)) return;
            try
            {
                foreach (var position in tracking.GetAllTrackedPlayerPositions())
                {
                    if (position.AssociatedPlayer is not IClientPlayer player) continue;
                    var pos = player.Entity?.Pos;
                    float[] color = null;
                    if (player != client.Api.World.Player)
                    {
                        string entitlement = player.Entitlements?.FirstOrDefault()?.Code;
                        color = entitlement != null && GlobalConstants.playerColorByEntitlement.TryGetValue(entitlement, out var rgb)
                            ? new[] { (float)rgb[0], (float)rgb[1], (float)rgb[2], (float)rgb[3] } : new[] { .7f, .7f, .7f, 1f };
                    }
                    DrawPlayer(client.Renderer, frame, pos?.X ?? position.PosX, pos?.Z ?? position.PosZ, pos?.Yaw ?? position.Yaw, color);
                }
            }
            finally { client.Renderer.End(); }
        }
        finally { client.Api.Render.GlToggleBlend(true); shader?.Use(); }
    }

    public static void DrawPlayer(MapRenderer renderer, MapFrame frame, double x, double z, double yaw, float[] color = null)
    {
        double nearest = frame.Topology.Nearest(x, frame.Camera.X);
        double sx = frame.X + frame.Width / 2 + (nearest - frame.Camera.X) * frame.Camera.Scale;
        double sy = frame.Y + frame.Height / 2 + (z - frame.Camera.Z) * frame.Camera.Scale;
        renderer.DrawPlayer(sx, sy, yaw, 9 * frame.Gui, 60, color);
    }
}
