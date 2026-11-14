using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.GameContent;
using Wmap.Core;
using Wmap.Integrations;

namespace Wmap.Client;

/// <summary>The player's latitude, longitude and heading in a small box in the top left corner of the screen, under the
/// HUD minimap when that sits there, on the background of the game's own coordinates box (Ctrl+V): its dialog colour and
/// shaded edge (0.5.3, WM-0015). The coordinates and heading only on worlds with geography (Earth Map, Cylinder
/// World's planet layout). The local time (<see cref="LocalClock"/>, HH:MM, WM-0010) and the world's universal clock,
/// labelled UTC (WM-0016): "16:46 local" after the coordinates and "12:15 UTC" after the heading, so the box keeps two
/// lines and stays above the game's intro tip. Where the world has one clock, the hour once, unlabelled; on worlds
/// without geography the clocks alone ("Time 12:15", or "16:46 local · 12:15 UTC"). The <c>CoordinatesHudTime</c>
/// setting hides them (0.5.3). The <c>CoordinatesHud</c> setting,
/// toggled with Ctrl+Shift+V, hides the box (0.4.9). It draws as the game's HUD boxes do: under every dialog and in the
/// HUD depth layer, so whatever opens in its corner covers it (0.5.3-rc.11, WM-0026).</summary>
public sealed class CoordinatesHud : HudElement
{
    public const string HotkeyCode = "wmapcoordinates";
    readonly WmapClient client;
    readonly LocalClock clock;
    readonly LoadedTexture[] lines = { null, null };
    readonly string[] shown = { null, null };
    LoadedTexture background;
    int backgroundW, backgroundH;
    int count;
    long next;
    // The game toggles it through the hotkey's handler (WmapSystem), which also stores the setting.
    public override string ToggleKeyCombinationCode => null;
    public override bool Focusable => false;
    // Under every dialog and the game's other HUD boxes, over the minimap (0.07): 0.21 drew it over the inventories (0.2)
    // and anything else that reached its corner, a chisel panel among them (0.5.3-rc.11, WM-0026).
    public override double DrawOrder => 0.08;

    public CoordinatesHud(ICoreClientAPI api, WmapClient client) : base(api) { this.client = client; clock = new LocalClock(api); }

    public override void OnRenderGUI(float deltaTime)
    {
        var pos = capi.World?.Player?.Entity?.Pos;
        if (pos == null || pos.Dimension != 0) return;
        var geography = client.Geography;
        bool time = client.Config.CoordinatesHudTime;
        if (geography == null && !time) return;
        long now = Environment.TickCount64;
        if (now >= next)
        {
            next = now + 250;
            var t = client.Topology;
            var (local, universal) = time && clock.Read(pos.X, pos.Z, geography, t.Canonical) is double hours && clock.Universal() is double world
                ? LocalTime.Clocks(hours, world, clock.OneClock, capi.World.Calendar.HoursPerDay) : (null, null);
            if (geography != null)
            {
                string where = GeoFormat.Format(geography(t.Canonical(pos.X), pos.Z), client.Config.DecimalDegrees);
                int bearing = Heading.Bearing(pos.Yaw);
                string heading = $"Heading {bearing:000}° {Heading.Point(bearing)}";
                Line(0, local == null ? where : where + "   " + local);
                Line(1, universal == null ? heading : heading + "   " + universal);
                count = 2;
            }
            else if (local != null) { Line(0, universal == null ? "Time " + local : local + " \u00b7 " + universal); count = 1; }
            else count = 0;
        }
        if (count == 0) return;
        // Vanilla's coordinates box pads its text by 5 (ForkBoundingParent(5, 5, 5, 5)).
        double pad = GuiElement.scaled(5), gap = GuiElement.scaled(4), w = 0, h = 0;
        for (int i = 0; i < count; i++)
        {
            if (lines[i] == null) return;
            w = Math.Max(w, lines[i].Width); h += lines[i].Height;
        }
        // Widths step by 10 GUI units so the box does not twitch as the digits change.
        double step = GuiElement.scaled(10);
        w = Math.Ceiling((w + 2 * pad) / step) * step; h += 2 * pad;
        var (x, y) = Place(w, h, gap);
        x = Math.Floor(x); y = Math.Floor(y);
        Background((int)Math.Ceiling(w), (int)Math.Ceiling(h));
        // In the HUD's own depth, as HudElement.OnRenderGUI draws a HUD's composers: 150 back. Drawn at the dialog layer's
        // own depth (0.5.3-rc.10 and before), the box stood in front of the HUDs drawn after it, which failed the depth
        // test where they met it and went missing there (WM-0026).
        capi.Render.GlPushMatrix();
        capi.Render.GlTranslate(0f, 0f, -150f);
        try
        {
            // Drawn as a composer draws its static elements (GuiComposer.Render: Render2DTexture at whole pixels, not the
            // premultiplied call), so the fill comes out exactly as dark as vanilla's coordinates box.
            capi.Render.Render2DTexture(background.TextureId, (float)x, (float)y, backgroundW, backgroundH, 50);
            double lineY = y + pad;
            for (int i = 0; i < count; i++)
            {
                capi.Render.Render2DTexturePremultipliedAlpha(lines[i].TextureId, x + pad, lineY, lines[i].Width, lines[i].Height, 51);
                lineY += lines[i].Height;
            }
        }
        finally { capi.Render.GlPopMatrix(); }
    }

    void Line(int i, string text)
    {
        if (text == shown[i]) return;
        shown[i] = text;
        lines[i]?.Dispose();
        lines[i] = capi.Gui.TextTexture.GenUnscaledTextTexture(text, CairoFont.WhiteSmallishText());
    }

    /// <summary>Vanilla's coordinates box background at w × h pixels: <c>AddGameOverlay</c>'s fill in
    /// <see cref="GuiStyle.DialogDefaultBgColor"/> and its shaded edge (<c>GuiElement.ShadePath</c>: a 2-pixel
    /// <see cref="GuiStyle.DialogBorderColor"/> stroke drawn atop the fill). Made again only when the size changes.</summary>
    void Background(int w, int h)
    {
        if (background != null && w == backgroundW && h == backgroundH) return;
        background?.Dispose();
        backgroundW = w; backgroundH = h;
        background = capi.Gui.Icons.GenTexture(w, h, (ctx, _) =>
        {
            var fill = GuiStyle.DialogDefaultBgColor;
            var edge = GuiStyle.DialogBorderColor;
            ctx.SetSourceRGBA(fill[0], fill[1], fill[2], fill[3]);
            ctx.Rectangle(0, 0, w, h);
            ctx.FillPreserve();
            ctx.Operator = Operator.Atop;
            ctx.SetSourceRGBA(edge[0], edge[1], edge[2], edge[3]);
            ctx.LineWidth = 2;
            ctx.Stroke();
            ctx.Operator = Operator.Over;
        });
    }

    /// <summary>Screen pixels for a w × h box in the top left corner, where vanilla places its own top-left HUD boxes
    /// (<see cref="GuiStyle.DialogToScreenPadding"/> from both edges); under the HUD minimap when that is in the top left.
    /// The game's intro tip ("Need help…", in its first minutes) slides in lower, at 80 GUI units.</summary>
    (double X, double Y) Place(double w, double h, double gap)
    {
        double screenW = capi.Render.FrameWidth, screenH = capi.Render.FrameHeight, edge = GuiElement.scaled(GuiStyle.DialogToScreenPadding);
        double x = edge, y = edge;
        var minimap = capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg;
        if (minimap != null && minimap.IsOpened() && minimap.DialogType == EnumDialogType.HUD && minimap.SingleComposer?.Bounds is { } b
            && b.renderX + b.OuterWidth / 2 < screenW / 2 && b.renderY + b.OuterHeight / 2 < screenH / 2)
        { x = b.renderX; y = b.renderY + b.OuterHeight + gap; }
        return (Math.Clamp(x, 0, Math.Max(0, screenW - w)), Math.Clamp(y, 0, Math.Max(0, screenH - h)));
    }

    public override void Dispose()
    {
        foreach (var l in lines) l?.Dispose();
        lines[0] = lines[1] = null;
        background?.Dispose();
        background = null;
        base.Dispose();
    }
}
