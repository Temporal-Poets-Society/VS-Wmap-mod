using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Wmap.Core;
using Wmap.Integrations;

namespace Wmap.Client;

/// <summary>Client state for one server session: tiles, the world chart, exploration and GPU resources.</summary>
public sealed class WmapClient : IDisposable
{
    public readonly ICoreClientAPI Api;
    public readonly WmapConfig Config;
    public readonly TileClient Tiles;
    public readonly BaseMapClient BaseMap;
    /// <summary>The climate raster for the Terrain colours (Earth Map; servers from 0.4.3).</summary>
    public readonly BaseMapClient Climate;
    public readonly ExploredMask Mask;
    public readonly MapRenderer Renderer = new();
    public readonly ObservedTerrain Observed;
    /// <summary>The Sea mod's winds on the maps; <see cref="WindLayer.Available"/> is false without Sea.</summary>
    public readonly WindLayer Winds;
    /// <summary>Whether this player sees the winds in gameplay (a Jonas barometer), and through what.</summary>
    public readonly WindSight Sight = new();
    /// <summary>The world chart drawn past the view distance; null if it could not start.</summary>
    public FarTerrain Far { get; private set; }
    /// <summary>The game's daylight over the world for the atlas's day and night overlay.</summary>
    public readonly DayLayer Day;
    /// <summary>Asks the server for automatic markers (0.4.6).</summary>
    public readonly AutoMarker AutoMarks;
    /// <summary>Drawing and reading paper maps (0.4.8).</summary>
    public readonly PaperMapClient PaperMaps;
    /// <summary>The full map's ore heat map (WM-D47).</summary>
    public readonly OreHeatLayer OreHeat;
    readonly CancellationTokenSource stop = new();
    Func<double, double, GeoPosition> geography;
    long geographyAt;
    bool geographyBuilt;
    Task<float[]> geoTask;
    public AtlasInfo Info => Tiles.Info;
    /// <summary>Legacy local barometer reach, retained for old manifests; worldwide winds no longer use it.</summary>
    public int BarometerRange => Info.BarometerRange > 0 ? Info.BarometerRange : 3000;
    public Topology Topology => Tiles.Topology;
    /// <summary>This session's tile cache folder (predicted tiles; shared by the atlas and far terrain).</summary>
    public readonly string CacheFolder;
    readonly IClientNetworkChannel channel;
    public void SendTileRequest(TileRequest request) => channel?.SendPacket(request);
    /// <summary>Whether a right-click on the atlas teleports: the server takes it, the setting allows it, the player is in creative.</summary>
    public bool CanTeleport => Info.Teleport && Config.RightClickTeleport && GameMode() == Vintagestory.API.Common.EnumGameMode.Creative;
    /// <summary>The player's game mode; the GUI smoke check replaces it (it has no player).</summary>
    public Func<Vintagestory.API.Common.EnumGameMode?> GameMode;
    public void SendTeleport(double x, double z) => channel?.SendPacket(new TeleportRequest { X = x, Z = z });
    public bool Creative => GameMode() == Vintagestory.API.Common.EnumGameMode.Creative;
    /// <summary>Whether the atlas offers the full (creative) map: in creative mode, or with the server's permission.</summary>
    public bool OffersFullMap => Creative || Info.CanDevelop;
    Vintagestory.API.Common.EnumGameMode? lastMode;
    /// <summary>Follows the player's game mode: creative opens the full map, any other mode the game chart. Asks the
    /// server for a new manifest, whose permissions follow the game mode too. True when it changed (not at start).</summary>
    public bool FollowGameMode()
    {
        var mode = GameMode();
        if (mode == null || mode == lastMode) return false;
        bool changed = lastMode != null;
        lastMode = mode;
        Config.DevelopmentMode = mode == Vintagestory.API.Common.EnumGameMode.Creative;
        Tiles.SetDevelopment(Config.DevelopmentMode);
        if (changed) channel?.SendPacket(new AtlasHello());
        return changed;
    }

    public WmapClient(ICoreClientAPI api, WmapConfig config, IClientNetworkChannel channel, AtlasInfo info)
    {
        Api = api; Config = config;
        GameMode = () => api.World?.Player?.WorldData?.CurrentGameMode;
        string data = api.GetOrCreateDataPath("Wmap");
        string cache = Path.Combine(data, "client", info.CacheId), observed = Path.Combine(data, "observed", info.ObservationId);
        Tiles = new TileClient(channel, info, config, cache, observed);
        this.channel = channel; CacheFolder = cache;
        // This world's tile cache marked as used, others unused for two weeks removed (WM-0057); recordings are never touched.
        Task.Run(() =>
        {
            try { int removed = TileCache.Prune(cache, TileCache.Unused); if (removed > 0) api.Logger.Notification("[wmap] Removed {0} tile caches unused for {1} days", removed, TileCache.Unused.TotalDays); }
            catch (Exception e) { api.Logger.Warning("[wmap] Could not clean old tile caches: {0}", e.Message); }
        });
        AutoMarks = new AutoMarker(api, config, r => channel?.SendPacket(r));
        BaseMap = new BaseMapClient(channel, cache);
        Climate = new BaseMapClient(channel, cache, true);
        // What this player has charted comes from the server (0.5.4, WM-D63), which casts their line of sight and keeps it in
        // the world save; before, the client cast it and kept it in its own folder, where a modified client could chart anything.
        Mask = new ExploredMask(Tiles.Topology);
        Observed = new ObservedTerrain(api, Tiles, config);
        PaperMaps = new PaperMapClient(Tiles, Mask, channel, text => api.ShowChatMessage(text));
        OreHeat = new OreHeatLayer(api, r => channel?.SendPacket(r), () => Topology);
        Day = new DayLayer(api, () => Topology);
        Winds = new WindLayer(api, this);
        try { Far = new FarTerrain(api, this); }
        catch (Exception e) { api.Logger?.Error("[wmap] Far terrain could not start: {0}", e); }
        // The recorder compares the world with the prediction where far terrain's near band holds it.
        Observed.Predict = (double x, double z, out double height, out bool water) =>
        {
            height = 0; water = false;
            return Far?.Tiles?.Predicted(x, z, out height, out water) == true;
        };
        Tiles.Predictable = Predictable;
        Follow(info);
        BaseMap.Update(info); Climate.Update(info);
    }

    /// <summary>A survival client's chart and climate raster, built from the blocks of land the player has seen (0.5.4, WM-D63);
    /// null with the full map.</summary>
    public ExploredChart Explored { get; private set; }

    /// <summary>Starts or ends a survival client's chart when the manifest's mode changes. Its renderer starts again from an
    /// empty chart; with the full map it gets the whole chart again (<see cref="BaseMap"/>).</summary>
    void Follow(AtlasInfo info)
    {
        if (info.Explored && info.BaseWidth > 0 && info.BaseHeight > 0)
        {
            if (Explored != null && Explored.Chart.Width == info.BaseWidth && Explored.Chart.Height == info.BaseHeight) return;
            Explored = new ExploredChart(Topology, info.BaseWidth, info.BaseHeight);
            keepKnown = false;
            BaseMap.Forget(); Climate.Forget();
            Renderer.SetBase(Explored.Chart, Topology);
            Renderer.SetKnown(Explored.Known, Explored.Chart.Width, Explored.Chart.Height, Topology.Wraps);
            Tiles.DropPredicted();
            return;
        }
        if (Explored == null) return;
        Explored = null;
        // The renderer keeps the survival chart and its known texels until the whole chart has come (Tick): far terrain draws
        // only those meanwhile, not the rest of a chart that holds nothing there (a grey plain to the horizon for a minute).
        keepKnown = true;
        Tiles.DropPredicted();
    }
    bool keepKnown;

    /// <summary>Whether the atlas asks for a predicted tile: with the full map any; in survival one holding a charted cell.</summary>
    bool Predictable(TileKey key)
    {
        if (!Info.Explored) return true;
        var grid = Mask.Grid;
        int x0 = (int)Math.Floor(key.X * key.Span / grid.TexelX), x1 = (int)Math.Ceiling((key.X + 1) * key.Span / grid.TexelX);
        int z0 = (int)Math.Floor(key.Z * key.Span / grid.TexelZ), z1 = (int)Math.Ceiling((key.Z + 1) * key.Span / grid.TexelZ);
        if (x1 - x0 >= grid.Width) { x0 = 0; x1 = grid.Width; }
        for (int z = Math.Max(0, z0); z < Math.Min(grid.Height, z1); z++)
        for (int x = x0; x < x1; x++)
        {
            int wx = grid.Wraps ? (int)Topology.Mod(x, grid.Width) : x;
            if (wx >= 0 && wx < grid.Width && Mask.Data[z * grid.Width + wx] == 255) return true;
        }
        return false;
    }

    /// <summary>Whether far terrain asks for a tile of this world rectangle (blocks east of the world's start): with the full
    /// map any; in survival one holding land the server has sent.</summary>
    public bool KnownAt(double rel0, double z0, double rel1, double z1) => Explored?.AnyKnown(rel0, z0, rel1, z1) ?? !Info.Explored;

    /// <summary>The cells the server says this player has charted (0.5.4, WM-D63): all of them after a greeting, else the new ones.</summary>
    public void ReceiveExplore(ExploreUpdate update)
    {
        if (update?.Cells == null || update.Session != Info.Session) return;
        CellSet cells;
        try { cells = CellSet.Unpack(update.Cells, Mask.Width, Mask.Height); }
        catch (InvalidDataException) { Tiles.LastError = "Invalid exploration update"; return; }
        if (update.Reset) Mask.Clear();
        var list = cells.Cells();
        Mask.RevealCells(list);
        // Predicted tiles holding newly charted cells (or next to them) are asked for again: they came blank there, or not at all.
        if (Info.Explored && list.Count > 0) { Tiles.RefreshPredicted(key => Touches(key, cells)); Tiles.Reconsider(); }
    }

    /// <summary>Whether newly charted cells lie in a tile or within its coast reach (<see cref="RevealGate.CoastReach"/> of its
    /// texels, whose coast distances they can change).</summary>
    bool Touches(TileKey key, CellSet cells)
    {
        var grid = Mask.Grid;
        double reach = RevealGate.CoastReach * key.Step;
        int px = (int)Math.Ceiling(reach / grid.TexelX) + 1, pz = (int)Math.Ceiling(reach / grid.TexelZ) + 1;
        int x0 = (int)Math.Floor(key.X * key.Span / grid.TexelX) - px, x1 = (int)Math.Ceiling((key.X + 1) * key.Span / grid.TexelX) + px;
        int z0 = (int)Math.Floor(key.Z * key.Span / grid.TexelZ) - pz, z1 = (int)Math.Ceiling((key.Z + 1) * key.Span / grid.TexelZ) + pz;
        if (!grid.Wraps) { x0 = Math.Max(0, x0); x1 = Math.Min(grid.Width, x1); }
        return cells.AnyIn(x0, z0, x1, z1);
    }
    readonly List<(double X0, double Z0, double X1, double Z1)> knownChanges = new();

    /// <summary>A block of the chart or the climate raster of land the player has seen (0.5.4, WM-D63).</summary>
    public void ReceivePatch(ChartPatch patch)
    {
        if (patch == null || patch.Session != Info.Session || Explored == null) return;
        if (!Explored.Apply(patch)) Tiles.LastError = "Invalid chart block";
    }

    public void Update(AtlasInfo info)
    {
        Tiles.Update(info); Follow(info); BaseMap.Update(info); Climate.Update(info);
        // Whether the near band and climate tiles may be drawn can change in a session (a player switched to creative
        // where predictions are not shared): far terrain starts again with their mesh and tiles.
        if (Far != null && !Far.Matches(info))
        {
            Far.Dispose(); Far = null;
            try { Far = new FarTerrain(Api, this); }
            catch (Exception e) { Api.Logger?.Error("[wmap] Far terrain could not start: {0}", e); }
        }
    }
    public void ReceiveChart(BaseChunk chunk) { BaseMap.Receive(chunk); Climate.Receive(chunk); }

    /// <summary>
    /// Far terrain on or off for this player, without a restart (0.5.5, WM-0053): saved in wmap.json (<c>FarTerrain</c>).
    /// Off disposes it, which frees its mesh, tiles, recordings, cell map and programs and gives back the camera's far plane;
    /// on builds it again. Returns a line saying what it does now.
    /// </summary>
    public string SetFarView(bool on)
    {
        Config.FarTerrain = on;
        Api.StoreModConfig(Config, "wmap.json");
        Far?.Dispose(); Far = null;
        try { Far = new FarTerrain(Api, this); }
        catch (Exception e) { Api.Logger?.Error("[wmap] Far terrain could not start: {0}", e); }
        return FarViewStatus();
    }

    /// <summary>The far view's state for chat: on (and why it may not draw yet), or off and how to bring it back.</summary>
    public string FarViewStatus()
    {
        if (!Config.FarTerrain) return "World Map: far view off. Ctrl+Shift+F, the atlas's Far view button or .wmap far on brings it back.";
        string status = Far?.Status;
        if (string.IsNullOrEmpty(status) || status.StartsWith("on", StringComparison.Ordinal)) return "World Map: far view on.";
        if (status.StartsWith("off", StringComparison.Ordinal)) return $"World Map: far view on, but not drawn: {status.Substring(3).TrimStart(':', ' ')}.";
        // Far terrain is drawn from the world chart (WM-0058): say how far it has come.
        string progress = Info?.BaseProgress is > 0 and < 1 ? $" ({Info.BaseProgress:P0} charted)" : "";
        return $"World Map: far view on, {status}{(status.Contains("chart", StringComparison.Ordinal) ? progress : "")}.";
    }

    /// <summary>Latitude/longitude for readouts, once an integration provides it.</summary>
    public Func<double, double, GeoPosition> Geography
    {
        get
        {
            if (geography == null && Environment.TickCount64 - geographyAt > 2000)
            {
                geographyAt = Environment.TickCount64;
                try { geography = WorldSource.Geography(Api, Info); } catch (Exception) { /* Late optional integration; raw coordinates remain available. */ }
            }
            return geography;
        }
    }

    /// <summary>Main thread, every game tick (the GL context is current).</summary>
    public void Tick()
    {
        Tiles.Tick(); Observed.Tick(); BaseMap.Tick(); Climate.Tick(); Day.Tick(Config.DayNightOverlay);
        AutoMarks.Tick(Info.AutoMarks);
        PaperMaps.Tick();
        if (Climate.Take() is { } climate) Renderer.SetClimate(climate, Topology.Wraps);
        var raster = BaseMap.Take();
        if (raster != null && Explored == null)
        {
            Renderer.SetBase(raster, Topology);
            if (keepKnown) { Renderer.ClearKnown(); keepKnown = false; }
        }
        Renderer.PumpBase(12 << 20);
        if (Explored != null)
        {
            Explored.Upload(Renderer, Topology.Wraps);
            // Land newly known: far terrain asks again for its tiles there.
            knownChanges.Clear();
            Explored.TakeKnownChanges(knownChanges);
            foreach (var (rel0, z0, rel1, z1) in knownChanges) Far?.Tiles?.Refresh(rel0, z0, rel1, z1);
        }
        if (!geographyBuilt && Geography is { } geo)
        {
            // Half a million lookups: built on a worker (the conversions are pure), uploaded here.
            geographyBuilt = true;
            var topology = Topology;
            geoTask = Task.Run(() => GeoGrid.Build(geo, topology), stop.Token);
        }
        if (geoTask?.IsCompleted == true)
        {
            if (geoTask.Status == TaskStatus.RanToCompletion) Renderer.SetGeography(geoTask.Result, GeoGrid.Width, GeoGrid.Height, Topology.Wraps);
            else Api.Logger?.Warning("[wmap] The latitude and longitude grid could not be built: {0}", geoTask.Exception?.InnerException?.Message);
            geoTask = null;
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        Far?.Dispose(); Winds.Dispose(); Observed.Dispose(); Tiles.Dispose(); BaseMap.Dispose(); Climate.Dispose(); Renderer.Dispose(); OreHeat.Dispose();
    }
}

/// <summary>A latitude/longitude lookup texture built from whichever geography integration is active.</summary>
public static class GeoGrid
{
    public const int Width = 1024, Height = 512;

    /// <summary>RGBA32F: continuous longitude, latitude, 1 where no geographic coordinate applies.</summary>
    public static float[] Build(Func<double, double, GeoPosition> geo, Topology t)
    {
        var data = new float[Width * Height * 4];
        for (int z = 0; z < Height; z++)
        {
            double wz = (z + 0.5) / Height * t.Height, previous = double.NaN;
            for (int x = 0; x < Width; x++)
            {
                var g = geo(t.StartX + (x + 0.5) / Width * t.Width, wz);
                double lon = g.Longitude;
                // Unwrap the ±180° datum line so filtering never interpolates across it.
                if (!double.IsNaN(previous)) { while (lon - previous > 180) lon -= 360; while (lon - previous < -180) lon += 360; }
                previous = lon;
                int i = (z * Width + x) * 4;
                data[i] = (float)lon; data[i + 1] = (float)g.Latitude; data[i + 2] = g.InGutter || g.BeyondPole ? 1 : 0;
            }
        }
        return data;
    }

    /// <summary>A graticule spacing that keeps lines at least minPixels apart.</summary>
    public static double Step(double pixelsPerDegree, double minPixels)
    {
        foreach (double s in new[] { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 45 }) if (s * pixelsPerDegree >= minPixels) return s;
        return 90;
    }
}
