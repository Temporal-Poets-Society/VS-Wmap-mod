using System;
using System.Collections.Generic;
using System.Linq;

namespace Wmap.Core;

/// <summary>Which detail tiles a view needs on top of the baked world raster.</summary>
public sealed record ViewPolicy(
    double BaseTexel,          // blocks per world-raster texel; 0 while no raster is available
    bool Predictions,          // predicted detail tiles may be requested
    bool Observations,         // recorded detail tiles are drawn
    Func<TileKey, bool> HasObservation,
    int Budget,
    bool Sharp = true,
    bool FlatPrediction = false); // a land/sea mask without relief: predicted detail starts at level 2

public static class ViewTiles
{
    /// <summary>Finest level worth requesting: detail must be at least twice as sharp as the world raster.</summary>
    public static int MaxDetailLevel(Topology t, double baseTexel) =>
        baseTexel > 0 ? Math.Min(t.MaxLevel, (int)Math.Floor(Math.Log2(baseTexel / 2))) : t.MaxLevel;

    /// <summary>
    /// Fills the tiles to request and draw, coarse first. Returns the detail level, or -1 when the
    /// world raster alone is sharp enough for prediction detail (observations can still be returned).
    /// </summary>
    public static int Select(List<TileInstance> into, Topology t, MapCamera c, double width, double height, ViewPolicy p)
    {
        into.Clear();
        int maxLevel = MaxDetailLevel(t, p.BaseTexel);
        double blocksPerPixel = 1 / c.Scale;
        void OverviewRecordings()
        {
            if (!p.Observations) return;
            int coarse = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, blocksPerPixel))), Math.Min(2, t.MaxLevel), t.MaxLevel);
            // Observations correct every provider, including an Earth prediction made after these chunks were generated.
            // Coarsen within the same tile budget rather than dropping the correction at regional/world zoom.
            for (; coarse <= t.MaxLevel; coarse++)
            {
                into.Clear();
                into.AddRange(TileSelection.Visible(t, c, width, height, coarse, 1).Where(v => p.HasObservation(v.Key)));
                if (into.Select(v => v.Key).Distinct().Count() <= p.Budget) return;
            }
            into.Clear();
        }
        if (maxLevel < 0 || (p.BaseTexel > 0 && blocksPerPixel >= p.BaseTexel / 2))
        {
            OverviewRecordings();
            return -1;
        }
        // A texel no larger than a pixel when sharp; otherwise up to two pixels per texel.
        double ideal = Math.Log2(Math.Max(1, blocksPerPixel));
        int level = Math.Max(p.Predictions && p.FlatPrediction ? Math.Min(2, maxLevel) : 0, p.Sharp ? (int)Math.Floor(ideal) : (int)Math.Ceiling(ideal));
        // The four-block recordings also come at close zoom: a finer prediction refines only the cells they confirm (Display).
        void Recordings(int at)
        {
            into.AddRange(TileSelection.Visible(t, c, width, height, at, 1).Where(v => p.HasObservation(v.Key)));
            if (at < 2) into.AddRange(TileSelection.Visible(t, c, width, height, 2, 1).Where(v => p.HasObservation(v.Key)));
        }
        for (; level <= maxLevel; level++)
        {
            into.Clear();
            if (p.Predictions)
            {
                into.AddRange(TileSelection.Visible(t, c, width, height, level, 0));
                // Recorded terrain is what the world holds: drawn over every prediction (Display).
                if (p.Observations) Recordings(level);
            }
            else if (p.Observations && p.BaseTexel > 0) Recordings(level);
            else if (p.Observations)
                // Without a world raster, coarser recordings fill the view while finer ones load.
                for (int l = t.MaxLevel; l >= level; l--)
                    into.AddRange(TileSelection.Visible(t, c, width, height, l, 1).Where(v => p.HasObservation(v.Key)));
            if (into.Select(v => v.Key).Distinct().Count() <= p.Budget) return level;
        }
        into.Clear();
        OverviewRecordings();
        return -1;
    }

    /// <summary>Coarse levels below fine ones, predictions below recordings of the same level (WM-D41). A finer prediction
    /// draws over a coarser recording, but only where that recording confirms it (<see cref="Reference"/>; the map shader
    /// leaves every other cell to the recording under it), so recorded terrain still beats a contrary prediction (WM-D40)
    /// while zooming in refines what the four-block samples agree with. Cached fallback tiles join the same order.</summary>
    public static IEnumerable<TileInstance> Display(IEnumerable<TileInstance> requested, Func<TileKey, bool> ready,
        IEnumerable<TileInstance> fallback = null) =>
        requested.Concat(fallback ?? Enumerable.Empty<TileInstance>()).Where(v => ready(v.Key))
            .OrderByDescending(v => v.Key.Level).ThenBy(v => v.Key.Layer);

    /// <summary>The recording a finer prediction tile is checked against: the finest drawn (<paramref name="shown"/>)
    /// recording of four blocks or coarser that contains it, positioned on the same lap as the tile. Null for recordings,
    /// and where no coarser recording is drawn. The one- and two-block levels hold only cells that differ from the
    /// prediction, and draw over it anyway.</summary>
    public static TileInstance? Reference(TileInstance tile, Func<TileKey, bool> shown, int maxLevel)
    {
        var k = tile.Key;
        if (k.Layer != 0) return null;
        for (int level = Math.Max(k.Level + 1, 2); level <= maxLevel; level++)
        {
            int shift = level - k.Level;
            var key = new TileKey(1, level, k.X >> shift, k.Z >> shift);
            if (!shown(key)) continue;
            return new TileInstance(key, tile.X - (k.X * k.Span - key.X * key.Span), tile.Z - (k.Z * k.Span - key.Z * key.Span), key.Span, key.Span);
        }
        return null;
    }

    /// <summary>Only prediction detail fades at its zoom boundary. An observation's own texel coverage still masks
    /// unknown cells, but recorded dry ground/water must not fade back into a contrary prediction.</summary>
    public static float Opacity(TileKey key, float predictionFade) => key.Layer == 1 ? 1 : predictionFade;
}
