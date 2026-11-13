using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using Wmap.Core;
using Wmap.Server;

namespace Wmap.Client;

/// <summary>Drawing onto a blank paper map (a title, the parts to draw) or reading a drawn one (the parts to copy into the
/// atlas), WM-P05.</summary>
public sealed class PaperMapDialog : GuiDialog
{
    readonly WmapClient client;
    readonly ITreeAttribute map;
    readonly List<(MapParts Part, string Label)> choices = new();
    public override string ToggleKeyCombinationCode => null;

    /// <param name="map">The drawn map's description (<see cref="PaperMapServer.Attribute"/>); null draws on a blank map.</param>
    public PaperMapDialog(ICoreClientAPI api, WmapClient client, ITreeAttribute map) : base(api)
    {
        this.client = client; this.map = map;
        if (map == null)
        {
            var maps = api.ModLoader?.GetModSystem<WorldMapManager>()?.MapLayers;
            var own = maps?.OfType<WaypointMapLayer>().FirstOrDefault()?.ownWaypoints ?? new List<Waypoint>();
            int readings = maps?.OfType<OreMapLayer>().FirstOrDefault()?.ownPropickReadings.Count ?? 0;
            var m = client.Mask;
            choices.Add((MapParts.Charted, $"Charted area ({PaperMap.ChartedKm2(m.Data, m.TexelX, m.TexelZ):0.#} km²)"));
            choices.Add((MapParts.Terrain, $"Recorded terrain ({client.Tiles.RecordingCount} tiles)"));
            choices.Add((MapParts.Waypoints, $"Waypoints ({own.Count(w => !AutoMarkCatalog.IsAuto(w?.Text))})"));
            choices.Add((MapParts.AutoMarks, $"Automatic markers ({own.Count(w => AutoMarkCatalog.IsAuto(w?.Text))})"));
            choices.Add((MapParts.Readings, $"Prospecting readings ({readings})"));
        }
        else
        {
            var holds = (MapParts)map.GetInt("holds");
            if (holds.HasFlag(MapParts.Charted)) choices.Add((MapParts.Charted, $"Charted area ({map.GetDouble("km2"):0.#} km²)"));
            if (holds.HasFlag(MapParts.Terrain)) choices.Add((MapParts.Terrain, $"Recorded terrain ({map.GetInt("tiles")} tiles)"));
            if (holds.HasFlag(MapParts.Waypoints)) choices.Add((MapParts.Waypoints, $"Waypoints ({map.GetInt("waypoints")})"));
            if (holds.HasFlag(MapParts.AutoMarks)) choices.Add((MapParts.AutoMarks, $"Automatic markers ({map.GetInt("automarks")})"));
            if (holds.HasFlag(MapParts.Readings)) choices.Add((MapParts.Readings, $"Prospecting readings ({map.GetInt("readings")})"));
        }
        Compose();
    }

    void Compose()
    {
        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        var font = CairoFont.WhiteSmallText();
        double y = 34;
        SingleComposer = capi.Gui.CreateCompo("wmap-papermap", dialogBounds).AddShadedDialogBG(bg)
            .AddDialogTitleBar(map == null ? "Draw a map" : map.GetString("title"), () => TryClose()).BeginChildElements(bg);
        if (map == null)
        {
            SingleComposer.AddStaticText("Title", font, ElementBounds.Fixed(0, y + 4, 60, 25))
                .AddTextInput(ElementBounds.Fixed(64, y, 300, 28), null, CairoFont.TextInput(), "title");
            y += 40;
            SingleComposer.AddStaticText("Everything you have discovered of:", font, ElementBounds.Fixed(0, y, 364, 25));
        }
        else
        {
            SingleComposer.AddStaticText($"Drawn by {map.GetString("author")}, {map.GetString("drawn")}", font, ElementBounds.Fixed(0, y, 364, 25));
            y += 26;
            SingleComposer.AddStaticText("Copy into your atlas:", font, ElementBounds.Fixed(0, y, 364, 25));
        }
        y += 30;
        foreach (var (part, label) in choices)
        {
            SingleComposer.AddSwitch(_ => { }, ElementBounds.Fixed(0, y, 25, 25), Key(part), 25)
                .AddStaticText(label, font, ElementBounds.Fixed(34, y + 3, 330, 25));
            y += 32;
        }
        SingleComposer.AddSmallButton(map == null ? "Draw" : "Copy into my atlas", Act, ElementBounds.Fixed(0, y + 6, 200, 28), key: "act")
            .EndChildElements().Compose();
        foreach (var (part, _) in choices) SingleComposer.GetSwitch(Key(part)).SetValue(true);
    }

    static string Key(MapParts part) => "part-" + (int)part;

    /// <summary>The ticked parts.</summary>
    public MapParts Ticked => choices.Where(c => SingleComposer.GetSwitch(Key(c.Part))?.On == true).Aggregate(MapParts.None, (all, c) => all | c.Part);

    bool Act()
    {
        var parts = Ticked;
        if (parts == MapParts.None) return true;
        if (map == null) client.PaperMaps.Draw(SingleComposer.GetTextInput("title")?.GetText() ?? "", parts);
        else client.PaperMaps.Read(map.GetString("id"), parts);
        TryClose();
        return true;
    }
}
