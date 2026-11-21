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

public sealed class AtlasDialog : GuiDialog
{
    readonly WmapClient client;
    readonly WmapConfig config;
    readonly AtlasElement map;
    /// <summary>Whether this window was built with the Creative / Game button (<see cref="WmapClient.OffersFullMap"/>).</summary>
    public readonly bool OffersFullMap;
    public override string ToggleKeyCombinationCode => MapKeyBinding.Code;
    // WmapSystem owns the handler, including initialization and the original-map fallback.
    public override void OnBlockTexturesLoaded() { }
    public AtlasDialog(ICoreClientAPI api, WmapClient client) : base(api)
    {
        this.client = client; config = client.Config;
        double scale = GuiElement.scaled(1);
        // Nearly the whole screen: the map is the point of this window.
        double width = Math.Clamp(api.Render.FrameWidth / scale - 80, 500, 2400);
        double height = Math.Clamp(api.Render.FrameHeight / scale - 244, 200, 1500);
        const int mapY = 108;
        var mapBounds = ElementBounds.Fixed(0, mapY, width, height);
        // A fill-sized background cannot determine the size of an autosized parent.
        // Fit the fixed-size controls instead, as the game's own map dialog does.
        var bg = ElementStdBounds.DialogBackground(12, 12);
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        map = new AtlasElement(api, mapBounds, client, UpdateText);
        map.Owner = this;
        try
        {
            SingleComposer = api.Gui.CreateCompo("wmap-atlas", dialogBounds);
            SingleComposer.AddShadedDialogBG(bg).AddDialogTitleBar("World Map", () => TryClose())
            .BeginChildElements(bg)
            .AddSmallButton("Whole world", () => { map.Fit(); return true; }, ElementBounds.Fixed(0, 34, 120, 28))
            .AddSmallButton("Follow", () => { map.Follow = !map.Follow; map.Center(); return true; }, ElementBounds.Fixed(128, 34, 80, 28))
            .AddSmallButton("Chart / Terrain", () => { map.SwitchStyle(); return true; }, ElementBounds.Fixed(216, 34, 140, 28));
            double x = 364;
            // The full (creative) map and the game chart: offered in creative mode or with the server's wmap privilege.
            // The window is built again when the game mode changes (WmapSystem).
            OffersFullMap = client.OffersFullMap;
            if (OffersFullMap)
            {
                SingleComposer.AddSmallButton("Creative / Game", () => { map.SwitchMode(); return true; }, ElementBounds.Fixed(x, 34, 150, 28), key: "mode");
                x += 158;
            }
            SingleComposer.AddSmallButton("° / °′", () => { config.DecimalDegrees = !config.DecimalDegrees; api.StoreModConfig(config, "wmap.json"); return true; }, ElementBounds.Fixed(x, 34, 70, 28))
            .AddSmallButton("Day / Night", () => { config.DayNightOverlay = !config.DayNightOverlay; api.StoreModConfig(config, "wmap.json"); return true; }, ElementBounds.Fixed(x + 78, 34, 110, 28))
            // The far view on or off (0.5.5, WM-0053), as Ctrl+Shift+F: the chat line says which.
            .AddSmallButton("Far view", () => { api.ShowChatMessage(client.SetFarView(!config.FarTerrain)); return true; }, ElementBounds.Fixed(x + 196, 34, 90, 28));
            x += 294;
            // Worldwide weather and monthly prevailing winds; gameplay requires a Jonas barometer.
            double labelX = x + 6;
            if (client.Winds.Available)
            {
                var choices = Enumerable.Range(0, WindGrid.Choices).ToArray();
                SingleComposer.AddDropDown(choices.Select(c => c.ToString()).ToArray(), choices.Select(c => "Winds: " + WindGrid.Name(c)).ToArray(), config.WindLayer,
                    (code, _) => { config.WindLayer = int.Parse(code); api.StoreModConfig(config, "wmap.json"); }, ElementBounds.Fixed(x, 34, 200, 28), "winds");
                labelX = x + 214;
            }
            SingleComposer.AddDynamicText(ModeLabel(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(labelX, 39, Math.Max(100, width - labelX), 25), "modeLabel")
            .AddStaticText("Show:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 75, 46, 25))
            .AddMultiSelectDropDown(ShowCodes, new[] { "Your waypoints", "Auto markers", "Prospecting", "Other layers" }, 0, (code, on) => { Show(code, on); Save(); },
                ElementBounds.Fixed(48, 70, 230, 28), "show")
            .AddDropDown(OreCodes(), OreNames(OreCodes()), 0, (code, _) => { config.ProspectingOre = code; Save(); }, ElementBounds.Fixed(286, 70, 200, 28), "ores")
            .AddStaticText("Auto-mark:", CairoFont.WhiteSmallText(), ElementBounds.Fixed(500, 75, 84, 25))
            .AddMultiSelectDropDown(AutoCodes(), new[] { "When harvesting", "When sneaking" }.Concat(AutoMarkCatalog.All.Select(c => c.Name)).ToArray(), 0,
                (code, on) => { AutoMark(code, on); Save(); }, ElementBounds.Fixed(586, 70, 260, 28), "automark");
            // Ore heat maps on the full map (WM-D47): offered with it, when the server works them out.
            if (OffersFullMap && client.Info.OreHeatCodes is { Length: > 0 } heat)
            {
                // The listed-ores map needs the pick's own factors (host rock counted): not from vanilla's ore maps alone.
                var first = client.Info.OreHeatMethod == OreHeatRequest.Reading ? new[] { "", OreHeat.Listed } : new[] { "" };
                var codes = first.Concat(heat.Distinct().OrderBy(c => OreHeatLayer.Name(c), StringComparer.CurrentCultureIgnoreCase)).ToArray();
                SingleComposer.AddDropDown(codes, codes.Select(c => "Ore heat: " + OreHeatLayer.Name(c)).ToArray(), Math.Max(0, Array.IndexOf(codes, client.OreHeat.Code)),
                    (code, _) => client.OreHeat.Select(code), ElementBounds.Fixed(854, 70, 240, 28), "oreheat");
            }
            SingleComposer.AddInteractiveElement(map, "atlas")
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, height + mapY + 8, width, 25), "position")
            .AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, height + mapY + 32, width, 25), "status")
            .AddStaticText("Drag to pan · Wheel to zoom · Space: follow · Home: whole world · Right-click: add or edit a waypoint (creative: or teleport) · Esc: close", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, height + mapY + 58, width, 20))
            // Waypoint, reading and other layers' text by the cursor, VTML as on the game's map (WM-0059). Added after
            // the map so it draws over it.
            .AddHoverText("", CairoFont.WhiteDetailText(), 350, mapBounds.FlatCopy(), "hoverText")
            .EndChildElements().Compose();
            SingleComposer.GetHoverText("hoverText").SetAutoWidth(true);
            SingleComposer.GetDropDown("show").SetSelectedValue(ShowCodes.Where(Shows).ToArray());
            SingleComposer.GetDropDown("automark").SetSelectedValue(AutoCodes().Where(AutoMarks).ToArray());
            SingleComposer.GetDropDown("ores").SetSelectedValue(config.ProspectingOre);
            map.Start();
        }
        catch
        {
            // The constructor can fail before the caller receives the dialog reference.
            // Release partially composed textures and the tile invalidation subscription here.
            map.Dispose();
            base.Dispose();
            throw;
        }
    }
    // ------------------------------------------------------------------ filters (0.4.6)

    static readonly string[] ShowCodes = { "waypoints", "auto", "prospecting", "layers" };
    bool Shows(string code) => code switch
    {
        "waypoints" => config.ShowWaypoints, "auto" => config.ShowAutoMarks, "prospecting" => config.ShowProspecting, _ => config.ShowOtherLayers
    };
    void Show(string code, bool on)
    {
        switch (code)
        {
            case "waypoints": config.ShowWaypoints = on; break;
            case "auto": config.ShowAutoMarks = on; break;
            case "prospecting": config.ShowProspecting = on; break;
            default: config.ShowOtherLayers = on; break;
        }
    }
    static string[] AutoCodes() => new[] { "use", "sneak" }.Concat(AutoMarkCatalog.All.Select(c => c.Code)).ToArray();
    bool AutoMarks(string code) => code == "use" ? config.AutoMarkOnUse : code == "sneak" ? config.AutoMarkOnSneak : config.AutoMarkKinds.Contains(code);
    void AutoMark(string code, bool on)
    {
        if (code == "use") config.AutoMarkOnUse = on;
        else if (code == "sneak") config.AutoMarkOnSneak = on;
        else config.AutoMarkKinds = on ? config.AutoMarkKinds.Append(code).Distinct().ToArray() : config.AutoMarkKinds.Where(k => k != code).ToArray();
    }
    // The ores the player's readings mention, and the one chosen even when no reading mentions it any more.
    string[] OreCodes() => new[] { "" }.Concat(map.Markers.Ores()).Append(config.ProspectingOre).Distinct().ToArray();
    static string[] OreNames(string[] codes) => codes.Select(c => c.Length == 0 ? "Ores: all" : "Ores: " + Lang.Get("ore-" + c)).ToArray();
    void Save() => capi.StoreModConfig(config, "wmap.json");

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Readings come and go while the window is closed.
        var codes = OreCodes();
        var ores = SingleComposer?.GetDropDown("ores");
        if (ores == null) return;
        ores.SetList(codes, OreNames(codes));
        ores.SetSelectedValue(config.ProspectingOre);
    }

    void UpdateText(string position, string status)
    {
        SingleComposer?.GetDynamicText("position")?.SetNewText(position);
        SingleComposer?.GetDynamicText("status")?.SetNewText(status);
        SingleComposer?.GetDynamicText("modeLabel")?.SetNewText(ModeLabel());
    }
    string ModeLabel() => (client.Tiles.Development ? "Creative" : "Game") + " · " + WmapConfig.StyleName(config.DefaultStyle);
    string shownHover = "";
    public override void OnFinalizeFrame(float dt)
    {
        base.OnFinalizeFrame(dt);
        if (SingleComposer?.GetHoverText("hoverText") is not { } box) return;
        // Recomposed only when the text changes; shown while the cursor is over the map, as the game's map does.
        string text = map.HoverText ?? "";
        if (text != shownHover) { shownHover = text; box.SetNewText(text); }
        bool over = map.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY) && Focused;
        box.SetVisible(over);
        box.SetAutoDisplay(over);
    }
    public override void OnGuiClosed() { client.Tiles.CloseView(); map.ReleaseDrag(); base.OnGuiClosed(); }
    public override void OnKeyDown(KeyEvent args)
    {
        if (args.KeyCode == (int)GlKeys.Escape && map.CloseMenu()) args.Handled = true;
        else if (args.KeyCode == (int)GlKeys.Space) { map.Follow = true; map.Center(); args.Handled = true; }
        else if (args.KeyCode == (int)GlKeys.Home) { map.Fit(); args.Handled = true; }
        else base.OnKeyDown(args);
    }
}
