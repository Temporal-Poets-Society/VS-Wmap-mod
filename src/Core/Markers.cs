using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>How the atlas draws markers as the zoom changes (pixels a block). The player's own waypoints stay, smaller
/// on a wider view; automatic markers and prospecting readings fade out, since a continent-wide view has no use for
/// every berry bush (0.4.6).</summary>
public static class MarkerZoom
{
    /// <summary>Full detail at 4 blocks a pixel or closer: the game's own map never zooms out further.</summary>
    public const double DetailFull = 0.25;
    /// <summary>Detail markers are gone at 16 blocks a pixel: a chunk is two pixels wide.</summary>
    public const double DetailGone = 1.0 / 16;
    /// <summary>The player's waypoints reach their smallest size at 256 blocks a pixel.</summary>
    public const double SizeSmallest = 1.0 / 256;
    public const float SmallestSize = 0.55f;

    /// <summary>Opacity of automatic markers and prospecting readings: 1 up close, 0 far out, eased between on a log scale.</summary>
    public static float Detail(double scale) => Ease(scale, DetailGone, DetailFull);

    /// <summary>Size of the player's own waypoints relative to the game's: 1 up close, shrinking to <see cref="SmallestSize"/>.</summary>
    public static float WaypointSize(double scale) => SmallestSize + (1 - SmallestSize) * Ease(scale, SizeSmallest, DetailFull);

    static float Ease(double scale, double low, double high)
    {
        if (!(scale > low)) return 0;
        if (scale >= high) return 1;
        double t = Math.Log(scale / low) / Math.Log(high / low);
        return (float)(t * t * (3 - 2 * t));
    }
}

/// <summary>A kind of thing worth a waypoint when the player harvests it or looks at it while sneaking.</summary>
public sealed record AutoMarkCategory(string Code, string Name, string Icon, int Color, double Radius, string[] Blocks, string[] Entities);

/// <summary>The automatic markers' kinds and how a block or creature is matched to one. Patterns are game codes with
/// <c>*</c> wildcards, without the domain.</summary>
public static class AutoMarkCatalog
{
    /// <summary>Text the server writes on a waypoint it makes automatically: the game keeps a waypoint's text but shows
    /// its title, so the tag survives editing and tells the atlas which markers to hide when zoomed out.</summary>
    public const string Tag = "wmap-auto:";

    // Colours from the game's waypoint palette (argb).
    public static readonly AutoMarkCategory[] All =
    {
        new("berries", "Berries", "berries", unchecked((int)0xff8f3fbf), 12, new[] { "fruitingbush-wild-*", "bigberrybush-*", "smallberrybush-*" }, Array.Empty<string>()),
        new("mushrooms", "Mushrooms", "mushroom", unchecked((int)0xffd36a2c), 12, new[] { "mushroom-*-normal", "mushroom-*-harvested" }, Array.Empty<string>()),
        new("ores", "Surface ore", "rocks", unchecked((int)0xffd8a43a), 16, new[] { "looseores-*" }, Array.Empty<string>()),
        new("beehives", "Wild beehives", "bee", unchecked((int)0xffe6c619), 8, new[] { "wildbeehive-*" }, Array.Empty<string>()),
        new("resin", "Resin", "tree", unchecked((int)0xff9a6a2c), 8, new[] { "log-resin-*", "log-resinharvested-*" }, Array.Empty<string>()),
        new("fruittrees", "Fruit trees", "apple", unchecked((int)0xffd13a3a), 12, new[] { "fruittree-foliage*", "fruittree-branch*" }, Array.Empty<string>()),
        new("clay", "Clay", "vessel", unchecked((int)0xff3a7bd1), 24, new[] { "rawclay-*" }, Array.Empty<string>()),
        new("translocators", "Translocators", "spiral", unchecked((int)0xff2fb3b3), 4, new[] { "statictranslocator-*" }, Array.Empty<string>()),
        new("traders", "Traders", "trader", unchecked((int)0xff3aa65a), 24, Array.Empty<string>(), new[] { "trader-*" }),
    };

    public static AutoMarkCategory Find(string code) => All.FirstOrDefault(c => c.Code == code);

    /// <summary>The category whose block patterns match a block code (no domain), or null.</summary>
    public static AutoMarkCategory ForBlock(string code) => code == null ? null : All.FirstOrDefault(c => c.Blocks.Any(p => Matches(p, code)));

    /// <summary>The category whose creature patterns match an entity code (no domain), or null.</summary>
    public static AutoMarkCategory ForEntity(string code) => code == null ? null : All.FirstOrDefault(c => c.Entities.Any(p => Matches(p, code)));

    /// <summary>Whether <paramref name="code"/> matches <paramref name="pattern"/>, where <c>*</c> stands for any text.</summary>
    public static bool Matches(string pattern, string code)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1) return pattern == code;
        if (!code.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        int at = parts[0].Length;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            int found = code.IndexOf(parts[i], at, StringComparison.Ordinal);
            if (found < 0) return false;
            at = found + parts[i].Length;
        }
        string last = parts[^1];
        return code.Length - at >= last.Length && code.EndsWith(last, StringComparison.Ordinal);
    }

    /// <summary>A block's name without its state: "Blueberry bush (ripe)" and "(empty)" are the same bush.</summary>
    public static string Title(string name) => name == null ? null : System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^)]*\)", "").Trim();

    public static bool IsAuto(string text) => text != null && text.StartsWith(Tag, StringComparison.Ordinal);
    public static string TagFor(AutoMarkCategory category) => Tag + category.Code;

    /// <summary>Whether a marker of the same kind and title lies within the category's radius (horizontally): the same
    /// patch of berries or the same trader is marked once.</summary>
    public static bool Known(IEnumerable<(double X, double Z, string Text, string Title)> markers, AutoMarkCategory category, string title, double x, double z)
    {
        string tag = TagFor(category);
        double r2 = category.Radius * category.Radius;
        foreach (var m in markers)
        {
            if (m.Text != tag || m.Title != title) continue;
            double dx = m.X - x, dz = m.Z - z;
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }
}
