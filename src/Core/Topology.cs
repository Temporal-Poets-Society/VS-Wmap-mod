using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wmap.Core;

public sealed class Topology
{
    public double StartX, Width, Height;
    public bool Wraps;
    public double EndX => StartX + Width;
    public static double Mod(double value, double period) => value - Math.Floor(value / period) * period;
    public double Canonical(double x) => Wraps ? StartX + Mod(x - StartX, Width) : x;
    public double Delta(double from, double to) => Wraps ? Mod(to - from + Width / 2, Width) - Width / 2 : to - from;
    public double Nearest(double x, double reference) => reference + Delta(reference, x);
    public int MaxLevel => Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(Width, Height) / TileData.Size)), 0, 20);
    public bool Valid(TileKey key) => key.Layer is 0 or 1 && key.Level >= 0 && key.Level <= MaxLevel && key.X >= 0 && key.Z >= 0
        && key.X * key.Span < Width && key.Z * key.Span < Height;
}

public sealed class MapCamera
{
    public double X, Z, Scale = 1;
    public void Fit(Topology t, double width, double height)
    {
        X = t.StartX + t.Width / 2; Z = t.Height / 2;
        Scale = Math.Min(width / t.Width, height / t.Height) * 0.96;
    }
    public const double MaxScale = 8;
    public static double ClampScale(Topology t, double width, double height, double scale) =>
        Math.Clamp(scale, Math.Min(width / t.Width, height / t.Height) * 0.8, MaxScale);
    public void Zoom(Topology t, double width, double height, double factor, double mouseX, double mouseY)
    {
        double old = Scale;
        Scale = ClampScale(t, width, height, Scale * factor);
        X += (mouseX - width / 2) * (1 / old - 1 / Scale);
        Z += (mouseY - height / 2) * (1 / old - 1 / Scale);
        Constrain(t);
    }
    public void Constrain(Topology t)
    {
        Z = Math.Clamp(Z, 0, t.Height);
        if (!t.Wraps) X = Math.Clamp(X, t.StartX, t.EndX);
        // Rebase by whole laps before floating-point resolution becomes relevant.
        else if (Math.Abs(X - t.StartX) > t.Width * 1000) X = t.Canonical(X);
    }
    public int Level(Topology t) => Math.Clamp((int)Math.Floor(Math.Log2(1 / Scale)), 0, t.MaxLevel);
}

public readonly record struct TileKey(int Layer, int Level, int X, int Z)
{
    public double Step => 1 << Level;
    public double Span => TileData.Size * Step;
    public string FileName => $"{Layer}-{Level}-{X}-{Z}.wmt";
}

public readonly record struct TileInstance(TileKey Key, double X, double Z, double Width, double Height);

public static class TileSelection
{
    // Enumerates tiles, never game chunks. A finite ring is clipped before selecting its repeated copies.
    public static IEnumerable<TileInstance> Visible(Topology t, MapCamera c, double width, double height, int level, int layer = 0)
    {
        double left = c.X - width / c.Scale / 2, right = c.X + width / c.Scale / 2;
        double top = Math.Max(0, c.Z - height / c.Scale / 2), bottom = Math.Min(t.Height, c.Z + height / c.Scale / 2);
        if (bottom <= top) yield break;
        double span = TileData.Size * (double)(1 << level);
        int firstLap = t.Wraps ? (int)Math.Floor((left - t.StartX) / t.Width) : 0;
        int lastLap = t.Wraps ? (int)Math.Floor((right - t.StartX) / t.Width) : 0;
        // Camera zoom limits keep this below four; defensive bound protects callers.
        if (lastLap - firstLap > 8) yield break;
        for (int lap = firstLap; lap <= lastLap; lap++)
        {
            double origin = t.StartX + lap * t.Width;
            double l = Math.Max(0, left - origin), r = Math.Min(t.Width, right - origin);
            if (r <= l) continue;
            for (int z = (int)Math.Floor(top / span); z < Math.Ceiling(bottom / span); z++)
            for (int x = (int)Math.Floor(l / span); x < Math.Ceiling(r / span); x++)
                yield return new(new(layer, level, x, z), origin + x * span, z * span, Math.Min(span, t.Width - x * span), Math.Min(span, t.Height - z * span));
        }
    }
}

/// <summary>The copies of a ring a map view shows, for layers that place things at canonical world positions (vanilla map
/// layers: waypoints, ships): copy <c>lap</c> is drawn by shifting the view a circumference west.</summary>
public static class RingCopies
{
    /// <summary>West and east view edges in canonical world x for copy <paramref name="lap"/> (laps east of the camera's
    /// canonical position), and half the view's height in blocks. The camera itself may be laps east of the ring.</summary>
    public static (double X1, double X2, double HalfHeight) View(Topology t, MapCamera c, double width, double height, int lap)
    {
        double halfW = width / 2 / c.Scale, halfH = height / 2 / c.Scale, x = t.Wraps ? t.Canonical(c.X) - lap * t.Width : c.X;
        return (x - halfW, x + halfW, halfH);
    }

    /// <summary>The copies the view shows: 0, and -1 or 1 where it crosses the seam (at most two each way).</summary>
    public static (int First, int Last) Laps(Topology t, MapCamera c, double width)
    {
        if (!t.Wraps) return (0, 0);
        double x = t.Canonical(c.X), halfW = width / 2 / c.Scale;
        int first = (int)Math.Floor((x - halfW - t.StartX) / t.Width), last = (int)Math.Floor((x + halfW - t.StartX) / t.Width);
        return (Math.Max(first, -2), Math.Min(last, 2));
    }

    /// <summary>The copy holding the view position <paramref name="viewX"/> (pixels from the view's west edge).</summary>
    public static int LapAt(Topology t, MapCamera c, double width, double viewX) =>
        t.Wraps ? (int)Math.Floor((t.Canonical(c.X) + (viewX - width / 2) / c.Scale - t.StartX) / t.Width) : 0;

    /// <summary>The zoom the layers are told. They place markers by the view bounds; the zoom only sizes them, and vanilla's
    /// own map keeps it within 0.25..6: its waypoint and ore markers shrink by 1.5 px per 1/zoom, so the atlas's far zoom
    /// (thousands of blocks a pixel) turned them inside out into circles larger than a continent.</summary>
    public static float LayerZoom(double scale) => (float)Math.Clamp(scale, 0.25, 6);
}

public readonly record struct GeoPosition(double Latitude, double Longitude, bool InGutter, bool BeyondPole);

public static class GeoFormat
{
    public static string Format(GeoPosition p, bool decimals = false)
    {
        if (p.BeyondPole) return "Beyond geographic poles";
        string text = Angle(p.Latitude, true, decimals) + "  " + Angle(p.Longitude, false, decimals);
        return p.InGutter ? text + "  (seam waters, approximate)" : text;
    }
    public static string Angle(double value, bool latitude, bool decimals)
    {
        string suffix = latitude ? (value < 0 ? "S" : "N") : (value < 0 ? "W" : "E");
        if (decimals) return Math.Abs(value).ToString("0.0000", CultureInfo.InvariantCulture) + "° " + suffix;
        double minutes = Math.Round(Math.Abs(value) * 600) / 10;
        int degrees = (int)(minutes / 60);
        return degrees + "° " + (minutes - degrees * 60).ToString("00.0", CultureInfo.InvariantCulture) + "′ " + suffix;
    }

    /// <summary>A grid line's label, in the readout's format (<paramref name="decimals"/> as in <see cref="Angle"/>) so the
    /// lines read against the position shown beside them: "169.75°W" with decimal degrees, "169°45′W" with minutes. Till
    /// Wmap 0.5.2 the labels always had minutes, and beside a decimal readout of 169.4513° W, 0.3° east of the 169°45′W
    /// line, that line read as the player's own longitude.</summary>
    public static string GridLabel(double value, bool latitude, double step, bool decimals)
    {
        string hemisphere = Math.Abs(value) < 1e-6 ? "" : latitude ? (value < 0 ? "S" : "N") : (value < 0 ? "W" : "E");
        double v = Math.Abs(value);
        if (step >= 1) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "°" + hemisphere;
        if (decimals) return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) + "°" + hemisphere;
        int degrees = (int)v, minutes = (int)Math.Round((v - degrees) * 60);
        if (minutes == 60) { degrees++; minutes = 0; }
        return $"{degrees}°{minutes:00}′{hemisphere}";
    }
}
