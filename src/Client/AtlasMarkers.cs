using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>The game's waypoints and prospecting readings, drawn on the atlas by Wmap rather than by the game's layers so
/// that they follow the zoom (<see cref="MarkerZoom"/>) and the atlas's filters: the player's own waypoints stay and
/// shrink, automatic markers and readings fade out. Hover text and right-clicks (edit a waypoint, delete a reading)
/// behave as on the game's map (0.4.6).</summary>
public sealed class AtlasMarkers : IDisposable
{
    readonly ICoreClientAPI api;
    readonly WmapConfig config;
    readonly Matrixf model = new();
    // Reused every marker, every frame: a thousand markers allocated thousands of these a frame.
    readonly Matrixf shadowModel = new();
    Vec4f color = new(), shade = new();
    LoadedTexture readingIcon;
    GuiDialogEditWayPoint editWaypoint;
    GuiDialogConfirm confirm;
    /// <summary>Gets focus back when a dialog this opened closes.</summary>
    public GuiDialog Owner;
    // Icons the game's map draws 20 GUI pixels wide, at 0.85 of half that from the centre.
    const float IconScale = 0.85f;

    public AtlasMarkers(ICoreClientAPI api, WmapConfig config) { this.api = api; this.config = config; }

    WorldMapManager Manager => api.ModLoader?.GetModSystem<WorldMapManager>();
    public WaypointMapLayer Waypoints => Manager?.MapLayers?.OfType<WaypointMapLayer>().FirstOrDefault();
    public OreMapLayer Readings => Manager?.MapLayers?.OfType<OreMapLayer>().FirstOrDefault();

    /// <summary>Opacity of a waypoint at this zoom, 0 when hidden: the player's own by the filter alone, automatic ones
    /// also fade out (a pinned one stays, as the player asked for it).</summary>
    public float Visible(Waypoint w, double scale)
    {
        if (w?.Position == null) return 0;
        if (!AutoMarkCatalog.IsAuto(w.Text)) return config.ShowWaypoints ? 1 : 0;
        return !config.ShowAutoMarks ? 0 : w.Pinned ? 1 : MarkerZoom.Detail(scale);
    }

    public float Visible(PropickReading r, double scale) =>
        r?.Position == null || !config.ShowProspecting || !Mentions(r, config.ProspectingOre) ? 0 : MarkerZoom.Detail(scale);

    /// <summary>A reading's strength for one ore (the game's own lookup is internal).</summary>
    static double Factor(PropickReading r, string ore) => r.OreReadings != null && r.OreReadings.TryGetValue(ore, out var o) && o != null ? o.TotalFactor : 0;

    static bool Mentions(PropickReading r, string ore) => string.IsNullOrEmpty(ore) || Factor(r, ore) > PropickReading.MentionThreshold;

    /// <summary>Ore codes the player's readings mention, for the atlas's ore filter.</summary>
    public string[] Ores() => Readings?.ownPropickReadings.Where(r => r?.OreReadings != null).SelectMany(r => r.OreReadings.Keys).Distinct().OrderBy(c => c).ToArray()
        ?? Array.Empty<string>();

    public void Draw(GuiElementMap map, double scale, int hoverX, int hoverY, System.Func<double, bool> pinnedHere)
    {
        var shader = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        shader.Uniform("extraGlow", 0); shader.Uniform("applyColor", 0); shader.Uniform("noTexture", 0f);
        shader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        var view = new Vec2f();
        if (Readings is { Active: true } ores && ores.quadModel != null && MarkerZoom.Detail(scale) > 0.01f)
        {
            readingIcon ??= api.Gui.LoadSvg(new AssetLocation("textures/icons/worldmap/0-circle.svg"), Size(), Size(), Size(), Size(), -1);
            foreach (var r in ores.ownPropickReadings)
            {
                float alpha = Visible(r, scale);
                if (alpha <= 0.01f || !OnMap(map, r.Position, ref view)) continue;
                double factor = string.IsNullOrEmpty(config.ProspectingOre) ? r.HighestReading : Factor(r, config.ProspectingOre);
                ColorUtil.ToRGBAVec4f(GuiStyle.DamageColorGradient[(int)Math.Clamp(factor * 150, 0, 99)], ref color);
                color.W = alpha;
                float half = readingIcon.Width * 0.5f * IconScale * (Near(map, view, hoverX, hoverY) ? 1.25f : 1);
                shade.Set(0, 0, 0, 0.7f * alpha);
                Icon(shader, ores.quadModel, readingIcon, map, view, half, 1.25f, shade, color);
            }
        }
        if (Waypoints is { Active: true } layer && layer.quadModel != null)
        {
            if (layer.texturesByIcon == null) layer.reloadIconTextures();
            float size = MarkerZoom.WaypointSize(scale);
            // Automatic markers first, so the player's own lie on top.
            foreach (bool own in new[] { false, true })
                foreach (var w in layer.ownWaypoints)
                {
                    if (AutoMarkCatalog.IsAuto(w?.Text) == own) continue;
                    float alpha = Visible(w, scale);
                    if (alpha <= 0.01f) continue;
                    bool pinned = w.Pinned;
                    if (pinned && !pinnedHere(w.Position.X) || !pinned && !OnMap(map, w.Position, ref view)) continue;
                    if (pinned) { map.TranslateWorldPosToViewPos(w.Position, ref view); map.ClampButPreserveAngle(ref view, 2); }
                    if (!layer.texturesByIcon.TryGetValue(w.Icon ?? "circle", out var icon)) layer.texturesByIcon.TryGetValue("circle", out icon);
                    if (icon == null) continue;
                    ColorUtil.ToRGBAVec4f(w.Color, ref color);
                    color.W = alpha;
                    float half = icon.Width * 0.5f * IconScale * size * (Near(map, view, hoverX, hoverY) ? 1.25f : 1);
                    shade.Set(0, 0, 0, 0.6f * alpha);
                    Icon(shader, layer.quadModel, icon, map, view, half, 1.1f, shade, color);
                }
        }
    }

    static int Size() => (int)Math.Ceiling(20 * RuntimeEnv.GUIScale);

    static bool OnMap(GuiElementMap map, Vec3d pos, ref Vec2f view)
    {
        map.TranslateWorldPosToViewPos(pos, ref view);
        return view.X >= -10 && view.Y >= -10 && view.X <= map.Bounds.InnerWidth + 10 && view.Y <= map.Bounds.InnerHeight + 10;
    }

    static bool Near(GuiElementMap map, Vec2f view, int x, int y)
    {
        double reach = RuntimeEnv.GUIScale * 8;
        return Math.Abs(x - (map.Bounds.renderX + view.X)) < reach && Math.Abs(y - (map.Bounds.renderY + view.Y)) < reach;
    }

    void Icon(IShaderProgram shader, MeshRef quad, LoadedTexture icon, GuiElementMap map, Vec2f view, float half, float shadow, Vec4f shade, Vec4f color)
    {
        float x = (float)(map.Bounds.renderX + view.X), y = (float)(map.Bounds.renderY + view.Y);
        shader.BindTexture2D("tex2d", icon.TextureId, 0);
        model.Set(api.Render.CurrentModelviewMatrix).Translate(x, y, 60f).Scale(half, half, 0f);
        shader.Uniform("rgbaIn", shade);
        shader.UniformMatrix("modelViewMatrix", shadowModel.Set(model.Values).Scale(shadow, shadow, 1).Values);
        api.Render.RenderMesh(quad);
        shader.Uniform("rgbaIn", color);
        shader.UniformMatrix("modelViewMatrix", model.Values);
        api.Render.RenderMesh(quad);
    }

    /// <summary>The topmost visible marker under the cursor: (waypoint index, reading index), -1 for none. A pinned waypoint
    /// counts on the copy of the ring that draws it (<paramref name="pinnedHere"/>).</summary>
    (int Waypoint, int Reading) At(GuiElementMap map, double scale, int x, int y, System.Func<double, bool> pinnedHere)
    {
        var view = new Vec2f();
        if (Waypoints is { Active: true } layer)
        {
            // The player's own lie on top.
            foreach (bool own in new[] { true, false })
                for (int i = layer.ownWaypoints.Count - 1; i >= 0; i--)
                {
                    var w = layer.ownWaypoints[i];
                    if (AutoMarkCatalog.IsAuto(w?.Text) == own || Visible(w, scale) <= 0.01f || w.Pinned && !pinnedHere(w.Position.X)) continue;
                    map.TranslateWorldPosToViewPos(w.Position, ref view);
                    if (w.Pinned) map.ClampButPreserveAngle(ref view, 2);
                    if (Near(map, view, x, y)) return (i, -1);
                }
        }
        if (Readings is { Active: true } ores)
            for (int i = ores.ownPropickReadings.Count - 1; i >= 0; i--)
            {
                var r = ores.ownPropickReadings[i];
                if (Visible(r, scale) <= 0.01f) continue;
                map.TranslateWorldPosToViewPos(r.Position, ref view);
                if (Near(map, view, x, y)) return (-1, i);
            }
        return (-1, -1);
    }

    /// <summary>The hover text for the marker under the cursor, as the game's map gives it; empty for none.</summary>
    public string Hover(GuiElementMap map, double scale, int x, int y, System.Func<double, bool> pinnedHere)
    {
        var (w, r) = At(map, scale, x, y, pinnedHere);
        if (w >= 0) return Lang.Get("Waypoint {0}", w) + "\n" + Waypoints.ownWaypoints[w].Title;
        if (r >= 0)
        {
            var pages = api.ModLoader.GetModSystem<ModSystemOreMap>()?.prospectingMetaData?.PageCodes ?? new Dictionary<string, string>();
            return Readings.ownPropickReadings[r].ToHumanReadable(api.Settings.String["language"], pages);
        }
        return "";
    }

    /// <summary>A right-click: a waypoint opens the game's editor, a reading asks to be deleted. True when it hit one.</summary>
    public bool RightClick(GuiElementMap map, double scale, int x, int y, System.Func<double, bool> pinnedHere)
    {
        var (w, r) = At(map, scale, x, y, pinnedHere);
        if (w >= 0)
        {
            editWaypoint?.TryClose(); editWaypoint?.Dispose();
            // Numbered by its place in the player's list: the game's /waypoint numbering.
            editWaypoint = new GuiDialogEditWayPoint(api, Waypoints, Waypoints.ownWaypoints[w], w);
            editWaypoint.TryOpen();
            editWaypoint.OnClosed += Refocus;
            return true;
        }
        if (r >= 0)
        {
            var ores = Readings;
            var reading = ores.ownPropickReadings[r];
            confirm?.TryClose(); confirm?.Dispose();
            confirm = new GuiDialogConfirm(api, Lang.Get("prospecting-reading-confirmdelete"), ok =>
            {
                // The list may have changed while the question was open.
                int index = ores.ownPropickReadings.IndexOf(reading);
                if (ok && index >= 0) ores.Delete(api.World.Player, index);
            });
            confirm.TryOpen();
            confirm.OnClosed += Refocus;
            return true;
        }
        return false;
    }

    void Refocus() { if (Owner?.IsOpened() == true) api.Gui.RequestFocus(Owner); }

    public void Dispose()
    {
        editWaypoint?.TryClose(); editWaypoint?.Dispose(); editWaypoint = null;
        confirm?.TryClose(); confirm?.Dispose(); confirm = null;
        readingIcon?.Dispose(); readingIcon = null;
    }
}
