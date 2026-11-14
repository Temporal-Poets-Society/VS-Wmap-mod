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

// The atlas's graticule labels, scale bar, ore legend and status line (part of AtlasElement).
public sealed partial class AtlasElement
{
    // ------------------------------------------------------------------ graticule labels and scale bar

    readonly Dictionary<string, LoadedTexture> labels = new();
    int labelStyle = -1;
    LoadedTexture Label(string value)
    {
        if (labels.TryGetValue(value, out var texture)) return texture;
        double[] color = config.DefaultStyle == 0 ? new[] { 0.30, 0.17, 0.10, 1 } : new[] { 1.0, 1, 1, 1 };
        var font = new CairoFont(13 * GuiElement.scaled(1), GuiStyle.StandardFontName, color);
        if (config.DefaultStyle != 0) font.WithStroke(new[] { 0, 0, 0, 0.7 }, 2);
        return labels[value] = api.Gui.TextTexture.GenUnscaledTextTexture(value, font);
    }

    // The labels drawn last frame and what they were worked out for: a still map draws them again without sampling the
    // geography along its edges (about 1,300 lookups) or formatting their text.
    readonly List<(LoadedTexture Label, double X, double Y, float Depth)> annotations = new();
    (double X, double Z, double Scale, double Left, double Top, double Width, double Height, double Meridians, double Parallels, int Style, bool Geography, float Gui, bool Decimals) annotated;
    double barNice, barUnit;

    void Annotate()
    {
        var geography = client.Geography;
        var key = (camera.X, camera.Z, camera.Scale, frame.X, frame.Y, frame.Width, frame.Height, frame.MeridianStep, frame.ParallelStep, config.DefaultStyle, geography != null, frame.Gui, config.DecimalDegrees);
        if (annotations.Count == 0 || key != annotated)
        {
            annotated = key;
            BuildAnnotations(geography);
        }
        foreach (var (label, x, y, depth) in annotations)
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x, y, label.Width, label.Height, depth);
        // Scale bar: sea miles (one minute of latitude) when a geographic datum exists, blocks otherwise.
        double gui = frame.Gui, right = frame.X + frame.Width, bottom = frame.Y + frame.Height;
        double length = barNice * barUnit * camera.Scale, barX = right - length - 20 * gui, barY = bottom - 26 * gui, barH = 5 * gui;
        int ink = config.DefaultStyle == 0 ? unchecked((int)0xff3b2914) : unchecked((int)0xffffffff), paper = config.DefaultStyle == 0 ? unchecked((int)0xfff1e6cc) : unchecked((int)0xff202830);
        api.Render.RenderRectangle((float)(barX - gui), (float)(barY - gui), 61, (float)(length + 2 * gui), (float)(barH + 2 * gui), ink);
        for (int i = 0; i < 4; i++)
            api.Render.RenderRectangle((float)(barX + i * length / 4), (float)barY, 62, (float)(length / 4), (float)barH, i % 2 == 0 ? ink : paper);
    }

    /// <summary>The ore heat map's colours and what they mean, at the top right under the meridian labels.</summary>
    void DrawOreLegend()
    {
        var heat = client.OreHeat;
        if (heat.Code.Length == 0 || heat.Level < 0) return;
        bool listed = heat.Code == OreHeat.Listed;
        string[] ticks = listed ? new[] { "0", "2", "4", "6", "8+" } : new[] { "1", "10", "100", "1k", "10k" };
        double gui = frame.Gui, right = frame.X + frame.Width, box = 30 * gui, height = 10 * gui;
        double x0 = right - ticks.Length * box - 16 * gui, y0 = frame.Y + 44 * gui;
        var title = Label(listed ? "Ores the prospecting pick lists" : OreHeatLayer.Name(heat.Code) + ": ore blocks per chunk");
        api.Render.Render2DTexturePremultipliedAlpha(title.TextureId, right - 16 * gui - title.Width, y0 - title.Height - 2 * gui, title.Width, title.Height, 62);
        for (int i = 0; i < ticks.Length; i++)
        {
            api.Render.RenderRectangle((float)(x0 + i * box), (float)y0, 62, (float)box, (float)height, OreHeat.Argb(i / (ticks.Length - 1.0), listed));
            var label = Label(ticks[i]);
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x0 + i * box + (box - label.Width) / 2, y0 + height + gui, label.Width, label.Height, 62);
        }
    }

    void BuildAnnotations(System.Func<double, double, GeoPosition> geography)
    {
        annotations.Clear();
        // Labels are cached by text; the cache is emptied only here, before any of this frame's labels are taken.
        if (labelStyle != config.DefaultStyle || labels.Count > 200) { foreach (var l in labels.Values) l.Dispose(); labels.Clear(); labelStyle = config.DefaultStyle; }
        var t = Topology;
        double gui = frame.Gui, scale = camera.Scale, left = frame.X, top = frame.Y, right = frame.X + frame.Width, bottom = frame.Y + frame.Height;
        double WorldX(double sx) => camera.X + (sx - left - frame.Width / 2) / scale;
        double WorldZ(double sy) => camera.Z + (sy - top - frame.Height / 2) / scale;
        if (geography != null)
        {
            // Meridians: sample longitude along the top edge and label each multiple of the step.
            double zEdge = Math.Clamp(WorldZ(top + 2), 1, t.Height - 1), previous = double.NaN, lastLabel = double.NegativeInfinity;
            for (double sx = left; sx <= right; sx += 3)
            {
                var g = geography(t.Canonical(WorldX(sx)), zEdge);
                if (g.InGutter) { previous = double.NaN; continue; }
                double lon = g.Longitude;
                if (!double.IsNaN(previous))
                {
                    while (lon - previous > 180) lon -= 360;
                    while (lon - previous < -180) lon += 360;
                    double k = Math.Floor(previous / frame.MeridianStep), at = (k + 1) * frame.MeridianStep;
                    if (at > previous && at <= lon && sx - lastLabel > 60 * gui)
                    {
                        double x = sx - 3 * (lon - at) / Math.Max(1e-9, lon - previous);
                        double wrapped = ((at + 540) % 360) - 180;
                        var label = Label(GeoFormat.GridLabel(wrapped, false, frame.MeridianStep, config.DecimalDegrees));
                        annotations.Add((label, x - label.Width / 2.0, top + 4 * gui, 60));
                        lastLabel = sx;
                    }
                }
                previous = lon;
            }
            // Parallels: latitude along the left edge.
            double xEdge = t.Canonical(WorldX(left + 2));
            previous = double.NaN; lastLabel = double.NegativeInfinity;
            for (double sy = top; sy <= bottom; sy += 3)
            {
                double z = WorldZ(sy);
                if (z < 0 || z >= t.Height) { previous = double.NaN; continue; }
                var g = geography(xEdge, z);
                if (g.BeyondPole) { previous = double.NaN; continue; }
                if (!double.IsNaN(previous))
                {
                    double lo = Math.Min(previous, g.Latitude), hi = Math.Max(previous, g.Latitude);
                    double at = Math.Ceiling(lo / frame.ParallelStep) * frame.ParallelStep;
                    if (at <= hi && at > lo && sy - lastLabel > 30 * gui && sy > top + 24 * gui)
                    {
                        var label = Label(GeoFormat.GridLabel(at, true, frame.ParallelStep, config.DecimalDegrees));
                        annotations.Add((label, left + 6 * gui, sy - label.Height - 1, 60));
                        lastLabel = sy;
                    }
                }
                previous = g.Latitude;
            }
        }
        double blocksPerMile = Tiles.Info.PoleDistance > 0 && geography != null ? Tiles.Info.PoleDistance / 90 / 60 : 0;
        double unit = blocksPerMile > 0 ? blocksPerMile : 1, target = 160 * gui / scale / unit, nice = 1;
        foreach (double m in new[] { 1.0, 2, 5 })
            for (double p = 1; p <= 1e6; p *= 10) if (m * p <= target) nice = Math.Max(nice, m * p);
        barNice = nice; barUnit = unit;
        double length = nice * unit * scale, barX = right - length - 20 * gui, barY = bottom - 26 * gui;
        string caption = blocksPerMile > 0 ? $"{nice:0.##} sea miles · {nice * blocksPerMile:0} blocks" : $"{nice:0} blocks";
        var text = Label(caption);
        annotations.Add((text, barX + length - text.Width, barY - text.Height - 2 * gui, 62));
    }

    void Status(Vintagestory.API.Common.Entities.EntityPos player, bool surface, bool development)
    {
        var t = Topology;
        var geography = client.Geography;
        double wx = camera.X + (api.Input.MouseX - Bounds.absX - Bounds.InnerWidth / 2) / camera.Scale;
        double wz = camera.Z + (api.Input.MouseY - Bounds.absY - Bounds.InnerHeight / 2) / camera.Scale;
        string Position(double x, double z) => geography != null ? GeoFormat.Format(geography(t.Canonical(x), z), config.DecimalDegrees) : $"X {t.Canonical(x):0}, Z {z:0}";
        string position = !surface ? "Atlas available in the surface dimension only" : "You: " + Position(player.X, player.Z);
        if (surface && Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) && wz >= 0 && wz < t.Height) position += "    Cursor: " + Position(wx, wz);
        if (surface && hover.Length > 0) position += "    " + hover;
        bool cursor = surface && Bounds.PointInside(api.Input.MouseX, api.Input.MouseY) && wz >= 0 && wz < t.Height;
        bool windSight = development || sight != null;
        if (cursor && windSight && client.Winds.Describe(t.Canonical(wx), wz) is { } wind) position += "    " + wind;
        if (cursor && development && client.OreHeat.Describe(t, wx, wz, Tiles.Info.OreHeatMethod) is { } ore) position += "    " + ore;
        // The chosen worldwide weather or prevailing-wind layer also supplies the player's reading.
        bool barometer = surface && !development && sight != null && client.Winds.Available && config.WindLayer != WindGrid.Off;
        if (barometer && client.Winds.Reading(player.X, player.Z) is { } here)
        {
            position += $"    Wind ({WindGrid.Name(config.WindLayer)}) here: " + here;
        }
        // The barometer's own reading: Earth's air pressure where you stand and whether it is rising or falling (WM-0006).
        if (surface && windSight && client.Winds.Barometer(player.X, player.Z) is { } pressure) position += "    Barometer: " + pressure;
        string source = development ? Tiles.Info.Source : "Charted by you";
        string chart = client.Renderer.Error != null ? "Map shader unavailable: " + client.Renderer.Error
            : client.BaseMap.Status.Length > 0 ? client.BaseMap.Status
            : !client.Renderer.BaseReady && client.BaseMap.Id != null ? $"Preparing world chart… {client.Renderer.BaseUploadProgress:P0}" : "";
        string status = $"{source} · {1 / camera.Scale:0.#} blocks/px · {(Follow ? "following" : "free pan")} · {Tiles.Pending} tiles pending · {atlas.Count} detail tiles";
        if (chart.Length > 0) status = chart + " · " + status;
        if (Tiles.LastError != null) status += " · " + Tiles.LastError;
        if (development && client.OreHeat.Status.Length > 0) status += " · " + client.OreHeat.Status;
        if (client.Winds.Available && config.WindLayer != WindGrid.Off && !development && surface)
            status += barometer ? $" · worldwide {WindGrid.Name(config.WindLayer)} from {sight}" : " · carry a Jonas barometer to read the winds";
        text(position, status);
    }
}
