using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Wmap.Core;

namespace Wmap.Server;

/// <summary>Makes the waypoints players' clients ask for when they harvest, or look at while sneaking, something of an
/// automatic marker's kind. They are the game's own waypoints, tagged in their text, so the minimap, the player's group
/// and other mods see them, and the player edits or deletes them as any other (0.4.6).</summary>
public sealed class AutoMarkServer
{
    /// <summary>Blocks from the player's eyes a marked thing may be: a long reach, with lag to spare.</summary>
    public const double Reach = 12;
    readonly ICoreServerAPI api;
    readonly WmapConfig config;
    readonly Dictionary<string, long> warned = new();

    public AutoMarkServer(ICoreServerAPI api, WmapConfig config) { this.api = api; this.config = config; }

    public void Request(IServerPlayer player, AutoMarkRequest request)
    {
        if (!config.AutoMarks || AutoMarkCatalog.Find(request.Category) is not { } category) return;
        var eye = player.Entity?.Pos;
        if (eye == null || eye.Dimension != 0) return;
        double x, y, z;
        string title;
        if (request.EntityId != 0)
        {
            var entity = api.World.GetEntityById(request.EntityId);
            if (entity?.Code == null || AutoMarkCatalog.ForEntity(entity.Code.Path) != category) return;
            x = entity.Pos.X; y = entity.Pos.Y; z = entity.Pos.Z;
            title = entity.GetName();
        }
        else
        {
            var pos = new BlockPos(request.X, request.Y, request.Z, 0);
            var block = api.World.BlockAccessor.GetBlock(pos);
            if (block?.Code == null || AutoMarkCatalog.ForBlock(block.Code.Path) != category) return;
            x = pos.X + 0.5; y = pos.Y; z = pos.Z + 0.5;
            title = AutoMarkCatalog.Title(block.GetPlacedBlockName(api.World, pos));
        }
        double dx = x - eye.X, dy = y - (eye.Y + player.Entity.LocalEyePos.Y), dz = z - eye.Z;
        if (dx * dx + dy * dy + dz * dz > Reach * Reach) return;
        if (string.IsNullOrWhiteSpace(title)) title = category.Name;
        var layer = api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault();
        if (layer == null) return;
        var own = layer.Waypoints.Where(w => w?.OwningPlayerUid == player.PlayerUID && w.Position != null).ToList();
        if (AutoMarkCatalog.Known(own.Select(w => (w.Position.X, w.Position.Z, w.Text, w.Title)), category, title, x, z)) return;
        if (own.Count(w => AutoMarkCatalog.IsAuto(w.Text)) >= config.AutoMarkLimit)
        {
            // Said once an hour, not on every bush.
            long now = Environment.TickCount64;
            if (!warned.TryGetValue(player.PlayerUID, out long at) || now - at > 3_600_000)
            {
                warned[player.PlayerUID] = now;
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Wmap: you have {config.AutoMarkLimit} automatic markers, the server's limit. Delete some to make new ones.", EnumChatType.Notification);
            }
            return;
        }
        layer.AddWaypoint(new Waypoint
        {
            Position = new Vec3d(x, y, z), Title = title, Text = AutoMarkCatalog.TagFor(category), Icon = category.Icon, Color = category.Color,
            OwningPlayerUid = player.PlayerUID, Guid = Guid.NewGuid().ToString()
        }, player);
        player.SendMessage(GlobalConstants.GeneralChatGroup, $"Wmap: marked {title}", EnumChatType.Notification);
    }
}
