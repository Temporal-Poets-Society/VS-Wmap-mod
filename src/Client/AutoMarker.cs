using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Asks the server for a waypoint when the player starts harvesting or breaking, or looks at while sneaking,
/// something of a kind they chose on the atlas (berries, mushrooms, surface ore, traders…). The server checks the
/// request and makes the marker unless one of the same kind and name is near; each thing is asked for once in 30 s (0.4.6).</summary>
public sealed class AutoMarker
{
    readonly ICoreClientAPI api;
    readonly WmapConfig config;
    readonly Action<AutoMarkRequest> send;
    // What was asked for lately, so holding the button or sneaking past a bush asks once.
    readonly Dictionary<string, long> asked = new();
    long next;

    public AutoMarker(ICoreClientAPI api, WmapConfig config, Action<AutoMarkRequest> send) { this.api = api; this.config = config; this.send = send; }

    public void Tick(bool serverMarks)
    {
        long now = Environment.TickCount64;
        if (!serverMarks || now < next || config.AutoMarkKinds.Length == 0) return;
        next = now + 200;
        var player = api.World?.Player;
        var entity = player?.Entity;
        if (entity == null || entity.Pos.Dimension != 0 || api.Gui.OpenedGuis.Any(g => g.DialogType == EnumDialogType.Dialog && g.Focused)) return;
        bool sneak = config.AutoMarkOnSneak && entity.Controls.Sneak;
        bool use = config.AutoMarkOnUse && (api.Input.InWorldMouseButton.Left || api.Input.InWorldMouseButton.Right);
        if (!sneak && !use) return;
        AutoMarkRequest request;
        string key;
        if (player.CurrentEntitySelection?.Entity is { Code: not null } target)
        {
            if (Wanted(AutoMarkCatalog.ForEntity(target.Code.Path)) is not { } kind) return;
            request = new AutoMarkRequest { Category = kind.Code, EntityId = target.EntityId };
            key = "e" + target.EntityId;
        }
        else if (player.CurrentBlockSelection?.Position is { } pos)
        {
            var block = api.World.BlockAccessor.GetBlock(pos);
            if (block?.Code == null || Wanted(AutoMarkCatalog.ForBlock(block.Code.Path)) is not { } kind) return;
            request = new AutoMarkRequest { Category = kind.Code, X = pos.X, Y = pos.Y, Z = pos.Z };
            key = $"b{pos.X},{pos.Y},{pos.Z}";
        }
        else return;
        if (asked.TryGetValue(key, out long at) && now - at < 30_000) return;
        if (asked.Count > 256) foreach (var old in asked.Where(a => now - a.Value >= 30_000).Select(a => a.Key).ToList()) asked.Remove(old);
        asked[key] = now;
        send(request);
    }

    AutoMarkCategory Wanted(AutoMarkCategory kind) => kind != null && config.AutoMarkKinds.Contains(kind.Code) ? kind : null;
}
