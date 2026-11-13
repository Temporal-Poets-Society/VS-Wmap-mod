using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Wmap.Client;

/// <summary>
/// Whether a player sees the winds on the gameplay map, and through what: a Jonas barometer they carry (hotbar or
/// backpack; any item whose attributes set <c>wmapWindInstrument</c>), or a source another mod grants. A ship mod
/// sets the player entity's watched attribute <see cref="WmapSystem.WindSightAttribute"/> on the server to a short
/// description (e.g. "the Santa Maria's binnacle") while a barometer in her binnacle serves everyone aboard, and
/// removes it when it no longer applies. The server clears it when a player joins, so a stale grant never survives.
/// </summary>
public sealed class WindSight
{
    long checkedAt;
    string source;

    public static bool IsInstrument(ItemStack stack) => stack?.Collectible?.Attributes?["wmapWindInstrument"].AsBool(false) == true;

    /// <summary>The source of wind sight, or null. Inventories are scanned at most twice a second.</summary>
    public string Source(IPlayer player)
    {
        long now = Environment.TickCount64;
        if (now - checkedAt < 500) return source;
        checkedAt = now;
        source = Carried(player) ? "your Jonas barometer" : Granted(player);
        return source;
    }

    static bool Carried(IPlayer player)
    {
        var manager = player?.InventoryManager;
        if (manager == null) return false;
        foreach (string name in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inventory = manager.GetOwnInventory(name);
            if (inventory == null) continue;
            foreach (var slot in inventory) if (IsInstrument(slot?.Itemstack)) return true;
        }
        return false;
    }

    static string Granted(IPlayer player)
    {
        string granted = player?.Entity?.WatchedAttributes?.GetString(WmapSystem.WindSightAttribute);
        return string.IsNullOrWhiteSpace(granted) ? null : granted.Trim();
    }
}
