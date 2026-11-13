using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Wmap.Core;
using Wmap.Server;

namespace Wmap;

/// <summary>A paper map (WM-P05): a right-click with a blank one draws what the player has discovered onto it, with a drawn
/// one copies what the player chooses into their atlas. A drawn map carries only its map's id and a description
/// (<see cref="PaperMapServer.Attribute"/>); the content is in the world save.</summary>
public sealed class PaperMapItem : Item
{
    public const string ClassName = "WmapPaperMap";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent) return;
        handling = EnumHandHandling.PreventDefault;
        if (api is ICoreClientAPI capi && byEntity is EntityPlayer player && player.Player == capi.World.Player)
            api.ModLoader.GetModSystem<WmapSystem>()?.OpenPaperMap(slot.Itemstack);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        if (inSlot?.Itemstack?.Attributes.GetTreeAttribute(PaperMapServer.Attribute) is not { } map) return;
        dsc.AppendLine($"\"{map.GetString("title")}\", drawn by {map.GetString("author")}, {map.GetString("drawn")}");
        dsc.AppendLine(Contents(map));
    }

    /// <summary>What a drawn map holds, in one line.</summary>
    public static string Contents(ITreeAttribute map)
    {
        var parts = (MapParts)map.GetInt("holds");
        var text = new StringBuilder();
        void Add(string s) { if (text.Length > 0) text.Append(", "); text.Append(s); }
        if (parts.HasFlag(MapParts.Charted)) Add($"{map.GetDouble("km2"):0.#} km² charted");
        if (parts.HasFlag(MapParts.Terrain)) Add($"{map.GetInt("tiles")} terrain tiles");
        if (parts.HasFlag(MapParts.Waypoints)) Add($"{map.GetInt("waypoints")} waypoints");
        if (parts.HasFlag(MapParts.AutoMarks)) Add($"{map.GetInt("automarks")} automatic markers");
        if (parts.HasFlag(MapParts.Readings)) Add($"{map.GetInt("readings")} prospecting readings");
        return text.Length == 0 ? "Empty" : text.ToString();
    }
}
