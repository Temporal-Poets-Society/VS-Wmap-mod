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

// The atlas's right-click menu (part of AtlasElement).
public sealed partial class AtlasElement
{
    // ------------------------------------------------------------------ right-click menu

    // A small menu at the right-clicked point (screen pixels): its position, then its items for a left click.
    public enum MenuItem { Waypoint, Teleport }
    record struct Menu(double X, double Y, double W, double Row, double WorldX, double WorldZ, string Title, MenuItem[] Items);
    Menu? menu;
    bool rightDown;
    LoadedTexture menuTitle;
    LoadedTexture[] menuItems;
    static string ItemText(MenuItem item) => item == MenuItem.Waypoint ? "Add waypoint…" : "Teleport here";
    public bool MenuOpen => menu != null;
    /// <summary>A menu item in screen pixels while the menu is open and offers it.</summary>
    public (double X, double Y, double W, double H)? MenuItemBounds(MenuItem item) =>
        menu is { } m && Array.IndexOf(m.Items, item) is var i and >= 0 ? (m.X, m.Y + (i + 1) * m.Row, m.W, m.Row) : null;

    /// <summary>Acts on the map point under screen pixel x, y (on the surface, inside the world): a waypoint where the game
    /// has its waypoint layer, a teleport in creative. One choice, adding a waypoint, opens its dialog at once; teleporting
    /// always goes through the menu, so a stray right-click never moves the player. True when it did either.</summary>
    public bool OpenMenu(int screenX, int screenY)
    {
        CloseMenu();
        if ((api.World?.Player?.Entity?.Pos?.Dimension ?? 0) != 0) return false;
        double x = camera.X + (screenX - Bounds.absX - Bounds.InnerWidth / 2) / camera.Scale;
        double z = camera.Z + (screenY - Bounds.absY - Bounds.InnerHeight / 2) / camera.Scale;
        if (z < 0 || z >= Topology.Height) return false;
        var items = new List<MenuItem>();
        if (markers.Waypoints != null) items.Add(MenuItem.Waypoint);
        if (client.CanTeleport && Core.Teleport.Column(Topology, x, z, int.MaxValue, int.MaxValue) != null) items.Add(MenuItem.Teleport);
        x = Topology.Canonical(x);
        if (items.Count == 0) return false;
        if (items.Count == 1 && items[0] == MenuItem.Waypoint) { AddWaypoint(x, z); return true; }
        var geography = client.Geography;
        string title = geography != null ? GeoFormat.Format(geography(x, z), config.DecimalDegrees) : $"X {x:0}, Z {z:0}";
        double gui = GuiElement.scaled(1), pad = 8 * gui, row = 22 * gui;
        var font = MenuFont(false);
        double w = Math.Max(font.GetTextExtents(title).Width, items.Max(i => font.GetTextExtents(ItemText(i)).Width)) + 2 * pad, h = (items.Count + 1) * row;
        // At the cursor, kept inside the map.
        double mx = Math.Clamp(screenX + 2, Bounds.absX, Math.Max(Bounds.absX, Bounds.absX + Bounds.InnerWidth - w));
        double my = Math.Clamp(screenY + 2, Bounds.absY, Math.Max(Bounds.absY, Bounds.absY + Bounds.InnerHeight - h));
        menu = new Menu(mx, my, w, row, x, z, title, items.ToArray());
        return true;
    }

    public bool CloseMenu()
    {
        menuTitle?.Dispose(); menuTitle = null;
        if (menuItems != null) foreach (var t in menuItems) t?.Dispose();
        menuItems = null;
        bool open = menu != null;
        menu = null;
        return open;
    }

    // Dark ink on chart paper: white text was hard to read over the map.
    static CairoFont MenuFont(bool dim) =>
        new(13 * GuiElement.scaled(1), GuiStyle.StandardFontName, dim ? new[] { 0.36, 0.30, 0.24, 1 } : new[] { 0.10, 0.08, 0.06, 1 });

    void DrawMenu()
    {
        if (menu is not { } m) return;
        // Leaving creative takes the teleport away.
        if (!client.CanTeleport && m.Items.Contains(MenuItem.Teleport)) { CloseMenu(); return; }
        menuTitle ??= api.Gui.TextTexture.GenUnscaledTextTexture(m.Title, MenuFont(true));
        menuItems ??= m.Items.Select(i => api.Gui.TextTexture.GenUnscaledTextTexture(ItemText(i), MenuFont(false))).ToArray();
        double gui = GuiElement.scaled(1), pad = 8 * gui;
        // Screen (mouse) pixels to render pixels.
        float x = (float)(Bounds.renderX + m.X - Bounds.absX), y = (float)(Bounds.renderY + m.Y - Bounds.absY), w = (float)m.W, row = (float)m.Row;
        int n = m.Items.Length;
        api.Render.RenderRectangle(x - 1, y - 1, 70, w + 2, (n + 1) * row + 2, unchecked((int)0xff3b2914));
        api.Render.RenderRectangle(x, y, 71, w, (n + 1) * row, unchecked((int)0xfff3ead4));
        api.Render.Render2DTexturePremultipliedAlpha(menuTitle.TextureId, x + pad, y + (row - menuTitle.Height) / 2, menuTitle.Width, menuTitle.Height, 73);
        for (int i = 0; i < n; i++)
        {
            float top = y + (i + 1) * row;
            bool hover = api.Input.MouseX >= m.X && api.Input.MouseX < m.X + m.W && api.Input.MouseY >= m.Y + (i + 1) * m.Row && api.Input.MouseY < m.Y + (i + 2) * m.Row;
            if (hover) api.Render.RenderRectangle(x, top, 72, w, row, unchecked((int)0xffd9c79e));
            api.Render.RenderRectangle(x + 2, top, 72, w - 4, 1, unchecked((int)0xff9a8a6a));
            var label = menuItems[i];
            api.Render.Render2DTexturePremultipliedAlpha(label.TextureId, x + pad, top + (row - label.Height) / 2, label.Width, label.Height, 73);
        }
    }
}
