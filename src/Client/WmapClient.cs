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
    readonly string maskPath;
    readonly CancellationTokenSource stop = new();
    Task<byte[]> seeding;
    Task<bool> unmerged;
    long mergeAt;
    // True once the exploration snapshot is on disk.
    Task<bool> saving = Saved;
    static readonly Task<bool> Saved = Task.FromResult(true);
    // The last exploration write per file, across sessions: writes to one file run in order, so an older
    // snapshot never replaces a newer one, and a new session loads only after the last one is written.
    static readonly Dictionary<string, Task<bool>> maskWrites = new();
    Func<double, double, GeoPosition> geography;
    long geographyAt, revealAt, savedAt;
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
        AutoMarks = new AutoMarker(api, config, r => channel?.SendPacket(r));
        BaseMap = new BaseMapClient(channel, cache);
        Climate = new BaseMapClient(channel, cache, true);
        maskPath = Path.Combine(observed, ExploredMask.FileName);
        Task<bool> pending;
        lock (maskWrites) maskWrites.TryGetValue(maskPath, out pending);
        // A previous session's write still running after 2 s: load what is on disk now, merge its result
        // when it lands, and merge the file into every write until then.
        if (pending != null && !pending.Wait(2000)) unmerged = pending;
        // An unreadable file is merged later, the same way: an empty mask must not be saved over it.
        if (!ExploredMask.TryLoad(Tiles.Topology, maskPath, out Mask)) unmerged ??= Saved;
        if (!File.Exists(maskPath))
        {
            // First run with this version: chart everything recorded by earlier Wmap versions.
            var t = Tiles.Topology; int w = Mask.Width, h = Mask.Height;
            seeding = Task.Run(() => ExploredMask.FromObservations(t, observed, w, h, stop.Token), stop.Token);
        }
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
        BaseMap.Update(info); Climate.Update(info);
    }

    public void Update(AtlasInfo info)
    {
        Tiles.Update(info); BaseMap.Update(info); Climate.Update(info);
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
        if (raster != null)
        {
            Renderer.SetBase(raster, Topology);
            var t = Topology;
            double range = Info.ChartHeightRange;
            heightTask = Task.Run(() => HeightGrid.FromRaster(raster, t, range), stop.Token);
        }
        if (heightTask?.IsCompleted == true)
        {
            if (heightTask.Status == TaskStatus.RanToCompletion) Heights = heightTask.Result;
            heightTask = null;
        }
        Renderer.PumpBase(12 << 20);
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
        long now = Environment.TickCount64;
        if (unmerged?.IsCompleted == true && now >= mergeAt)
        {
            if (ExploredMask.TryLoad(Tiles.Topology, maskPath, out var disk)) { Mask.Merge(disk.Data); unmerged = null; }
            else mergeAt = now + 1000;
        }
        if (seeding?.IsCompleted == true)
        {
            if (seeding.Status == TaskStatus.RanToCompletion) Mask.Merge(seeding.Result);
            seeding = null;
        }
        var pos = Api.World.Player?.Entity?.Pos;
        if (pos != null && pos.Dimension == 0 && now - revealAt > 250)
        {
            revealAt = now;
            // By line of sight (the server's choice): a small circle, and what the eye sees once the chart's heights are
            // here. Without a shared chart there are no heights: the full circle.
            bool sight = Info.ExploreBySight && Info.CanPredict;
            Mask.Reveal(pos.X, pos.Z, sight ? Math.Max(16, Info.SightCircle) : Config.ExploreRadius);
            if (sight) CastSight(pos.X, pos.Y, pos.Z);
        }
        if (sightTask?.IsCompleted == true)
        {
            if (sightTask.Status == TaskStatus.RanToCompletion) { Mask.RevealCells(sightTask.Result); SightCasts++; }
            sightTask = null;
        }
        SaveResult();
        if (Mask.Unsaved && now - savedAt > 30000 && saving.IsCompleted) Save();
    }

    /// <summary>The ground heights line of sight looks over (from the world chart), once it has arrived.</summary>
    public HeightGrid Heights { get; private set; }
    Task<HeightGrid> heightTask;
    Task<int[]> sightTask;
    double sightX = double.NaN, sightY, sightZ;
    public int SightCasts { get; private set; }
    /// <summary>Eye height above the feet; the deck of a ship or a mast top counts as where the player stands.</summary>
    const double EyeHeight = 1.6;

    /// <summary>Casts line of sight in the background after the player has moved 32 blocks or climbed 3 since the last cast.</summary>
    void CastSight(double x, double y, double z)
    {
        var heights = Heights;
        if (heights == null || sightTask != null) return;
        if (!double.IsNaN(sightX) && Math.Abs(Topology.Delta(sightX, x)) < 32 && Math.Abs(z - sightZ) < 32 && Math.Abs(y - sightY) < 3) return;
        sightX = x; sightY = y; sightZ = z;
        double eye = y + EyeHeight - (Info.SeaLevel - 1), distance = Math.Max(512, Info.SightDistance);
        double step = Math.Min(Mask.TexelX, Mask.TexelZ) / 2;
        var mask = Mask;
        // The distinct cells, collected on the worker: the main thread charts each once.
        // Enclosed hidden patches up to the server's square, in texels.
        int holes = (int)(Math.Max(0, Info.SightHoleSize) * (double)Math.Max(0, Info.SightHoleSize) / (mask.TexelX * mask.TexelZ));
        // Over the planet's curve (0.4.11): low coasts sink under the horizon long before the reach.
        double radius = Info.PlanetRadius;
        sightTask = Task.Run(() => mask.SightCells(heights, x, z, eye, distance, step, holes, radius), stop.Token);
    }

    byte[] saveBuffer;

    void Save()
    {
        savedAt = Environment.TickCount64;
        // The last save's copy again once its write is done (0.5.0-pre.4); while it is still being written, a new one.
        bool free = saving.IsCompleted;
        byte[] snapshot = Mask.Snapshot(free ? saveBuffer : null);
        if (free) saveBuffer = snapshot;
        int w = Mask.Width, h = Mask.Height; string path = maskPath;
        var topology = Tiles.Topology; bool merge = unmerged != null;
        bool Write()
        {
            try
            {
                // Runs after the earlier writes to this file, so the file holds their exploration.
                if (merge)
                {
                    if (!ExploredMask.TryLoad(topology, path, out var disk)) { Tiles.LastError = "Exploration save: the saved exploration cannot be read to merge it"; return false; }
                    for (int i = 0; i < snapshot.Length; i++) if (disk.Data[i] > snapshot[i]) snapshot[i] = disk.Data[i];
                }
                TileCodec.WriteAtomic(path, ExploredMask.Pack(snapshot, w, h)); return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Tiles.LastError = "Exploration save: " + e.Message; return false; }
        }
        lock (maskWrites)
        {
            var write = maskWrites.TryGetValue(path, out var previous) ? previous.ContinueWith(_ => Write(), TaskScheduler.Default) : Task.Run(Write);
            maskWrites[path] = saving = write;
            write.ContinueWith(_ => { lock (maskWrites) if (maskWrites.TryGetValue(path, out var last) && last == write) maskWrites.Remove(path); }, TaskScheduler.Default);
        }
    }

    /// <summary>Snapshot cleared Unsaved; a failed write sets it again for the next attempt.</summary>
    void SaveResult()
    {
        if (!saving.IsCompleted || saving == Saved) return;
        if (!saving.IsCompletedSuccessfully || !saving.Result) Mask.SaveFailed();
        saving = Saved;
    }

    public void Dispose()
    {
        stop.Cancel();
        SaveResult();
        // An unfinished write may still fail; the final snapshot is written after it either way.
        if (Mask.Unsaved || !saving.IsCompleted) Save();
        saving.Wait(2000);
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
