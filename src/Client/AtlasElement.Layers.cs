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

// Other mods' map layers and waypoints on the atlas (part of AtlasElement).
public sealed partial class AtlasElement
{
    // ------------------------------------------------------------------ other mods' map layers

    // Vanilla map layers other than terrain and players (entities, other mods' layers such as Magelan's ships) drawn
    // over the atlas through a vanilla map element that mirrors the atlas camera: a layer renders with
    // map.TranslateWorldPosToViewPos, map.Bounds and map.ZoomLevel, which this keeps in step. Waypoints and prospecting
    // readings are drawn by Wmap (AtlasMarkers), so they follow the zoom and the filters.
    GuiElementMap overlay;
    readonly List<MapLayer> overlayLayers = new();
    readonly HashSet<MapLayer> failedLayers = new();
    readonly AtlasMarkers markers;
    string hover = "";
    bool markersFailed;

    static bool IsOverlay(MapLayer layer) => layer is not ChunkMapLayer && layer is not PlayerMapLayer && layer is not WmapMinimapLayer
        && layer is not WaypointMapLayer && layer is not OreMapLayer;

    /// <summary>The vanilla map element for the other layers, its view on one copy of the ring: the layers place their
    /// markers at canonical world positions, so copy <paramref name="lap"/> (laps east of the camera's own) is drawn by
    /// shifting the view bounds a circumference west. Null without the game's map.</summary>
    GuiElementMap Overlay(int lap = 0)
    {
        var manager = api.ModLoader?.GetModSystem<WorldMapManager>();
        if (manager?.MapLayers == null) return null;
        overlayLayers.Clear();
        if (config.ShowOtherLayers) foreach (var layer in manager.MapLayers) if (IsOverlay(layer) && !failedLayers.Contains(layer)) overlayLayers.Add(layer);
        overlay ??= new GuiElementMap(overlayLayers, api, null, Bounds, false);
        var (x1, x2, halfH) = RingCopies.View(Topology, camera, Bounds.InnerWidth, Bounds.InnerHeight, lap);
        overlay.ZoomLevel = RingCopies.LayerZoom(camera.Scale);
        overlay.CurrentBlockViewBounds = new Vintagestory.API.MathTools.Cuboidd(x1, 0, camera.Z - halfH, x2, 0, camera.Z + halfH);
        return overlay;
    }

    /// <summary>Whether copy <paramref name="lap"/> draws a pinned waypoint at <paramref name="x"/>: the first copy that
    /// shows it, or the first copy, pinned at its edge, when none does. Drawn once, not at the edge of every copy.</summary>
    bool PinnedHere(double x, int lap)
    {
        var (first, last) = RingCopies.Laps(Topology, camera, Bounds.InnerWidth);
        for (int k = first; k <= last; k++)
        {
            var (x1, x2, _) = RingCopies.View(Topology, camera, Bounds.InnerWidth, Bounds.InnerHeight, k);
            if (x >= x1 && x <= x2) return k == lap;
        }
        return lap == first;
    }

    void DrawOtherLayers(float dt)
    {
        if (Overlay() == null) return;
        var (first, last) = RingCopies.Laps(Topology, camera, Bounds.InnerWidth);
        api.Render.PushScissor(Bounds);
        try
        {
            // Each copy of the ring the view shows; time advances once per frame.
            for (int lap = first; lap <= last; lap++)
            {
                var map = Overlay(lap);
                foreach (var layer in overlayLayers)
                {
                    if (!layer.Active) continue;
                    try { layer.Render(map, lap == first ? dt : 0); }
                    catch (Exception e)
                    {
                        // one bad layer doesn't take the atlas down
                        failedLayers.Add(layer);
                        api.Logger.Warning("[wmap] Map layer {0} failed to draw on the atlas and is left out: {1}", layer.GetType().Name, e.Message);
                    }
                }
                int here = lap;
                try { markers.Draw(map, camera.Scale, api.Input.MouseX, api.Input.MouseY, x => PinnedHere(x, here)); }
                catch (Exception e)
                {
                    if (!markersFailed) api.Logger.Warning("[wmap] Waypoints could not be drawn on the atlas: {0}", e);
                    markersFailed = true;
                }
            }
        }
        finally { api.Render.PopScissor(); }
    }

    void HoverOtherLayers(MouseEvent args)
    {
        if (!Bounds.PointInside(args.X, args.Y)) { hover = ""; return; }
        // The copy of the ring under the cursor.
        int lap = RingCopies.LapAt(Topology, camera, Bounds.InnerWidth, args.X - Bounds.absX);
        if (Overlay(lap) is not { } map) { hover = ""; return; }
        var text = new System.Text.StringBuilder();
        try { text.AppendLine(markers.Hover(map, camera.Scale, args.X, args.Y, x => PinnedHere(x, lap))); } catch (Exception) { }
        foreach (var layer in overlayLayers)
        {
            if (!layer.Active) continue;
            try { layer.OnMouseMoveClient(args, map, text); } catch (Exception) { }
        }
        hover = text.ToString().Trim().Replace("\r", "").Replace("\n", " · ");
    }

    /// <summary>A right-click goes to the markers and layers first, as on the game's own map: a waypoint opens its editor,
    /// a prospecting reading asks to be deleted. True when one took it.</summary>
    bool RightClickOtherLayers(MouseEvent args)
    {
        int lap = RingCopies.LapAt(Topology, camera, Bounds.InnerWidth, args.X - Bounds.absX);
        if (Overlay(lap) is not { } map) return false;
        if (markers.RightClick(map, camera.Scale, args.X, args.Y, x => PinnedHere(x, lap))) { args.Handled = true; return true; }
        foreach (var layer in overlayLayers)
        {
            if (!layer.Active) continue;
            try { layer.OnMouseUpClient(args, map); }
            catch (Exception e) { api.Logger.Warning("[wmap] Map layer {0} failed to take a click on the atlas: {1}", layer.GetType().Name, e.Message); }
            if (args.Handled) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ waypoints

    GuiDialog owner;
    /// <summary>The atlas window, given focus back when a waypoint dialog closes.</summary>
    public GuiDialog Owner { get => owner; set { owner = value; markers.Owner = value; } }
    GuiDialogAddWayPoint addWaypoint;
    /// <summary>The atlas's waypoints and prospecting readings: their filters and ore codes for the window's controls.</summary>
    public AtlasMarkers Markers => markers;

    /// <summary>Opens the game's own waypoint dialog for the map point x, z: its icons, colours and /waypoint command,
    /// so the marker is the server's, shared with the minimap, the player's group and other mods.</summary>
    void AddWaypoint(double x, double z)
    {
        if (markers.Waypoints is not { } layer) return;
        addWaypoint?.TryClose(); addWaypoint?.Dispose();
        // The ground where its chunk is loaded (as the game's map), sea level elsewhere.
        int ground = api.World.BlockAccessor.GetRainMapHeightAt((int)x, (int)z);
        addWaypoint = new GuiDialogAddWayPoint(api, layer) { WorldPos = new Vintagestory.API.MathTools.Vec3d(x, (ground > 0 ? ground : Tiles.Info.SeaLevel) + 1, z) };
        addWaypoint.TryOpen();
        addWaypoint.OnClosed += () => { if (Owner?.IsOpened() == true) api.Gui.RequestFocus(Owner); };
    }
}
