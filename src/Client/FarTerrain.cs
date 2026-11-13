using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Far terrain: past the view distance, the world chart drawn as a hazy 3D landscape out to
/// <see cref="WmapConfig.FarTerrainDistance"/>. It reuses the atlas's world chart texture; see docs/far-terrain.md.
/// With Earth Map and a 0.4.0 server, the near band (4-block detail tiles out to
/// <see cref="WmapConfig.NearBandDistance"/>) and climate tiles (forest and the game's colour tints) come on their
/// own request stream; see docs/far-terrain-near-band.md.
/// The mid-distance view (WM-P06 prototype; docs/mid-distance-view.md) adds the player's recordings over the prediction,
/// terraces near the real chunks, recorded structures and distant lights (<see cref="RecordedTiles"/>,
/// <see cref="MidDistanceDraw"/>), each switched by its own setting.
/// </summary>
public sealed class FarTerrain : IRenderer
{
    // Before the game's terrain (0.37), after the sky (0.2) and sun (0.3).
    public double RenderOrder => 0.36;
    public int RenderRange => 99999;

    readonly ICoreClientAPI api;
    readonly WmapClient client;
    readonly FarTerrainDraw draw = new();
    readonly FarFrame frame = new();
    readonly Task<FarMesh> building;
    readonly SeaWater water;
    readonly bool wantBand, wantClimate;
    FarTiles tiles;
    /// <summary>The near band and climate tiles, while the server provides them; null otherwise.</summary>
    public FarTiles Tiles => tiles;
    int plantTint, waterTint;
    bool tintsLoaded;
    IShaderProgram program, cellsProgram, mapProgram, lightsProgram;
    bool registered, failed, disposed;
    // The mid-distance view: recordings around the camera, its GL resources, the lights' renderer (after the game's chunks).
    RecordedTiles recorded;
    readonly MidDistanceDraw mid = new();
    readonly FarLights lights;
    bool midFailed, drawnThisFrame;
    readonly GpuTimer terrainTime = new(), lightsTime = new();
    /// <summary>The player's recordings for the mid-distance view, once any of its parts is on; null otherwise.</summary>
    public RecordedTiles Recorded => recorded;
    object camera;
    FieldInfo zFarField;
    float raisedZFar, originalZFar;
    public string Status { get; private set; } = "";

    public FarTerrain(ICoreClientAPI api, WmapClient client)
    {
        this.api = api; this.client = client;
        var config = client.Config;
        if (!config.FarTerrain) { Status = "off"; return; }
        if (api.Event == null || api.Shader == null) { Status = "off: no renderer"; return; }
        if (api.ModLoader?.IsModEnabled("farseer") == true)
        {
            Status = "off: Farseer draws far terrain";
            api.Logger.Notification("[wmap] Farseer is installed; Wmap's far terrain stays off.");
            return;
        }
        (wantBand, wantClimate) = TilesFor(client.Info, config);
        // The near band needs finer rings near the camera; beyond 2048 blocks the mesh is the normal one.
        double distance = config.FarTerrainDistance, cell = wantBand ? FarMesh.NearBandCell : FarMesh.CellFor(config.FarTerrainDetail);
        building = Task.Run(() => FarMesh.Build(distance, cell));
        water = new SeaWater(api);
        api.Event.ReloadShader += LoadShader;
        LoadShader();
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "wmap-far-terrain");
        lights = new FarLights(this);
        api.Event.RegisterRenderer(lights, EnumRenderStage.Opaque, "wmap-far-lights");
        registered = true;
    }

    bool LoadShader()
    {
        IShaderProgram Load(ref IShaderProgram slot, string name, out bool ok)
        {
            slot?.Dispose();
            slot = api.Shader.NewShaderProgram();
            slot.AssetDomain = "wmap";
            api.Shader.RegisterFileShaderProgram(name, slot);
            ok = slot.Compile();
            if (ok && !draw.PlanUnits(slot.ProgramId, PinGameSamplers(slot)))
            {
                api.Logger.Error("[wmap] Far terrain's {0} shader: its texture units do not fit with the game's.", name);
                ok = false;
            }
            return slot;
        }
        draw.ProgramReloaded();
        Load(ref program, "farterrain", out bool compiled);
        failed = !compiled;
        if (failed) { Status = "off: shader did not compile (see client-main.log)"; api.Logger.Error("[wmap] Far terrain shader did not compile; far terrain is off."); }
        // The mid-distance view's programs: without them far terrain still draws, as before.
        Load(ref cellsProgram, "farcells", out bool cells);
        Load(ref mapProgram, "farcellmap", out bool map);
        Load(ref lightsProgram, "farlights", out bool glows);
        midFailed = !(cells && map && glows);
        if (midFailed) api.Logger.Error("[wmap] The mid-distance view's shaders did not compile (farcells {0}, farcellmap {1}, farlights {2}); it is off.", cells, map, glows);
        return !failed;
    }

    /// <summary>The game's samplers pinned below Wmap's units (<see cref="FarTerrainDraw.PinGameSamplers"/>); returns how many.
    /// Reflection: ShaderProgramBase.textureLocations; without it, as many as the game's includes can declare (5).</summary>
    static int PinGameSamplers(IShaderProgram program)
    {
        var field = program?.GetType().GetField("textureLocations", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(program) is not System.Collections.Generic.Dictionary<string, int> units) return 5;
        return FarTerrainDraw.PinGameSamplers(units);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        drawnThisFrame = false;
        if (disposed || failed || program == null) return;
        if (!draw.Ready)
        {
            if (building == null || !building.IsCompleted) return;
            if (!building.IsCompletedSuccessfully) { failed = true; Status = "off: " + building.Exception?.InnerException?.Message; return; }
            draw.Upload(building.Result);
            api.Logger.Notification("[wmap] Far terrain: {0} rings to {1} blocks, {2} vertices, {3} triangles, {4:0.0} MiB.",
                draw.Mesh.Rings, draw.Mesh.Radius, draw.Mesh.VertexCount, draw.Mesh.Indices.Length / 3, draw.Bytes / 1048576.0);
        }
        var renderer = client.Renderer;
        var player = api.World.Player?.Entity;
        if (!renderer.BaseReady || player == null || player.Pos.Dimension != 0) { if (!renderer.BaseReady) Status = "waiting for the world chart"; return; }
        // A land/sea mask with invented heights would raise flat plates of land on the horizon,
        // also where another generator (Terra Prety) put mountains or sea.
        if (client.Info?.FlatPrediction == true) { Status = "off: the world chart has no relief (approximate continents)"; return; }
        // The chart drawn unmasked on the horizon shows terrain no one has charted: only while the server shares it.
        // A chart downloaded in creative stays for the atlas, which masks it in gameplay.
        if (client.Info?.CanPredict != true) { Status = "off: the server does not share its world chart with you"; return; }
        Status = OnStatus(Status);

        var data = api.World.Player.WorldData;
        int view = data.LastApprovedViewDistance > 0 ? Math.Min(data.LastApprovedViewDistance, data.DesiredViewDistance) : data.DesiredViewDistance;
        double far = Math.Min(client.Config.FarTerrainDistance, draw.Mesh.Radius);
        if (far <= view + 64) return;
        RaiseFarPlane((float)far + 1024);

        var render = api.Render;
        // Consume each matrix before reading the next (see MapRenderer.Begin).
        Array.Copy(render.CurrentProjectionMatrix, frame.Projection, 16);
        Array.Copy(render.CameraMatrixOriginf, frame.View, 16);
        var cam = player.CameraPos;
        var info = client.Info;
        frame.CameraX = cam.X; frame.CameraY = cam.Y; frame.CameraZ = cam.Z;
        frame.Topology = client.Topology;
        frame.SeaLevel = info.SeaLevel;
        frame.HeightScale = info.MapHeight > info.SeaLevel ? info.MapHeight - info.SeaLevel : 200;
        frame.HeightRange = info.ChartHeightRange;
        frame.ChartTexture = renderer.BaseTexture;
        frame.BlocksPerTexel = renderer.BaseTexel;
        // Where the game's chunks end: 0.75 of the view distance; nearer only over chunk columns not loaded yet (FarStart,
        // a mask the shaders read).
        long now = Environment.TickCount64;
        frame.Start = StartFor(view);
        // Again at once when the camera moved a column (also a teleport or a seam jump) or the start changed (FarStart.ScanDue).
        if (FarStart.ScanDue(loadedScanX, loadedScanZ, loadedScanStart, cam.X, cam.Z, frame.Start, now, loadedAt))
        {
            loadedAt = now + FarStart.ScanMs;
            loadedSeaChunk = info.SeaLevel / 32;
            loadedRadius = FarStart.Scan(loadedColumn ??= ColumnLoaded, cam.X, cam.Z, frame.Start, loadedMask);
            loadedScanX = cam.X; loadedScanZ = cam.Z; loadedScanStart = frame.Start;
            frame.LoadedMask = draw.UploadLoaded(loadedMask);
        }
        frame.FillStart = loadedRadius;
        (frame.MaskX, frame.MaskZ) = FarStart.MaskOffset(loadedScanX, loadedScanZ, cam.X, cam.Z);
        frame.Far = far;
        frame.PlanetRadius = info.PlanetRadius; frame.CurveFrom = view;
        frame.Water = water.Color;
        var ambient = api.Ambient;
        Copy(ambient.BlendedFogColor, frame.FogColor);
        frame.FogDensity = ambient.BlendedFogDensity;
        frame.FogMin = ambient.BlendedFogMin;
        frame.HorizonFog = ambient.BlendedCloudDensity;
        var calendar = api.World.Calendar;
        Copy(calendar.SunPositionNormalized, frame.SunPosition);
        Copy(calendar.SunColor, frame.SunColor);
        Copy(api.Render.AmbientColor, frame.Ambient);
        frame.DayLight = SkyDaylight(calendar.DayLightStrength, calendar.MoonLightStrength, player.Pos.Y - info.SeaLevel);
        UpdateTiles(info, cam.X, cam.Z, far);
        UpdateRecorded(cam.X, cam.Z);

        api.World.FrameProfiler.Enter("wmap-far-terrain");
        terrainTime.Begin();
        try
        {
            if (frame.TerraceRadius > frame.DrawFrom)
            {
                mid.PlaceCellMap(frame, draw.Mesh);
                long signature = mid.CellMapSignature(frame, draw.Mesh);
                if (signature != cellMapSignature)
                {
                    mapProgram.Use();
                    try { if (mid.DrawCellMap(mapProgram.ProgramId, draw, frame)) cellMapSignature = signature; }
                    finally { mapProgram.Stop(); }
                }
            }
            program.Use();
            try { draw.Draw(program.ProgramId, frame); }
            finally { program.Stop(); }
            if (frame.TerraceRadius > frame.Start || frame.Recorded != null)
            {
                cellsProgram.Use();
                try
                {
                    mid.DrawTerraces(cellsProgram.ProgramId, draw, frame, info.MapHeight > 0 ? info.MapHeight : 512);
                    if (frame.Recorded != null) mid.DrawStructures(cellsProgram.ProgramId, draw, frame, frame.SmoothStart);
                }
                finally { cellsProgram.Stop(); }
            }
            drawnThisFrame = true;
        }
        finally { terrainTime.End(); api.World.FrameProfiler.Leave(); }
    }

    /// <summary>The mid-distance view's parts this frame, from the settings (switched live by <c>.wmap mid</c>): recorded
    /// terrain over the prediction (and its structures), terraces from far terrain's start, distant lights. Each needs its
    /// shaders; recordings need a world with a recording grid.</summary>
    void UpdateRecorded(double cameraX, double cameraZ)
    {
        var config = client.Config;
        frame.Recorded = null; frame.TerraceRadius = 0;
        frame.Push = 12; frame.PushFade = 512;
        if (midFailed || (!config.MidDistanceRecorded && !config.MidDistanceTerraces && !config.MidDistanceLights)) return;
        if (!mid.Ready)
        {
            try { mid.Create(); }
            catch (Exception e) { midFailed = true; api.Logger.Error("[wmap] The mid-distance view could not start: {0}", e.Message); return; }
        }
        bool terraces = config.MidDistanceTerraces && config.TerraceDistance > frame.Start;
        if (terraces)
        {
            frame.TerraceRadius = config.TerraceDistance;
            // Terraces stand at the real ground's height: a small push-down is enough for the game's chunks to cover them,
            // and it is gone before the smooth mesh begins at the terraces' edge.
            frame.Push = 2; frame.PushFade = Math.Max(64, Math.Min(256, config.TerraceDistance - frame.Start));
        }
        if (!config.MidDistanceRecorded && !config.MidDistanceLights) return;
        if (recorded == null)
        {
            recorded = new RecordedTiles(client.Topology, new TileClientRecordings(client.Tiles));
            recorded.CreateTextures();
            api.Logger.Notification("[wmap] Mid-distance view: recorded terrain {0}, terraces {1} (to {2} blocks), lights {3} (to {4} blocks); {5:0.0} MiB of textures.",
                config.MidDistanceRecorded ? "on" : "off", config.MidDistanceTerraces ? "on" : "off", config.TerraceDistance,
                config.MidDistanceLights ? "on" : "off", config.LightDistance, (RecordedTiles.TextureBytes + mid.Bytes) / 1048576.0);
        }
        var origin = FarTerrainDraw.Origin(draw.Mesh, cameraX, cameraZ);
        bool show = config.MidDistanceRecorded;
        recorded.Update(cameraX, cameraZ, origin.X, show ? config.NearBandDistance : 0, show && terraces ? config.TerraceDistance : 0,
            show ? config.NearBandDistance : 0, config.MidDistanceLights ? config.LightDistance : 0);
        if (show) frame.Recorded = recorded;
    }

    long cellMapSignature = long.MinValue;
    int statusCounts;
    double loadedRadius = double.PositiveInfinity, loadedScanX = double.NaN, loadedScanZ, loadedScanStart;
    readonly byte[] loadedMask = new byte[FarStart.MaskSide * FarStart.MaskSide];
    long loadedAt;
    int loadedSeaChunk;
    System.Func<int, int, bool> loadedColumn;

    /// <summary>A chunk column is loaded: its map chunk and the chunk at the sea's level are there.</summary>
    bool ColumnLoaded(int cx, int cz)
    {
        var ba = api.World.BlockAccessor;
        return ba.GetMapChunk(cx, cz) != null && ba.GetChunk(cx, loadedSeaChunk, cz) != null;
    }

    /// <summary>The status while far terrain draws: "on", or the near band's line already there (0.5.0-pre.3: setting "on"
    /// every frame made <see cref="BandStatus"/> build its line anew every frame).</summary>
    public static string OnStatus(string current) => current != null && current.StartsWith("on", StringComparison.Ordinal) ? current : "on";

    /// <summary>The status with the near band's counts, built again only when a count changes (a string per frame was most of
    /// far terrain's allocations).</summary>
    public static string BandStatus(string current, ref int counts, int ready, int wanted, int? climateReady, int? climateWanted)
    {
        int now = HashCode.Combine(ready, wanted, climateReady, climateWanted);
        if (now == counts && current != null && current.StartsWith("on; near band", StringComparison.Ordinal)) return current;
        counts = now;
        return $"on; near band {ready}/{wanted} tiles" + (climateReady != null ? $", climate {climateReady}/{climateWanted}" : "");
    }

    /// <summary>The distant lights, after the game's chunks so they hide them (<see cref="FarLights"/>).</summary>
    internal void DrawLights()
    {
        if (!drawnThisFrame || disposed || midFailed || lightsProgram == null || recorded == null || !client.Config.MidDistanceLights) { mid.Lights = 0; return; }
        var render = api.Render;
        Array.Copy(render.CurrentProjectionMatrix, frame.Projection, 16);
        Array.Copy(render.CameraMatrixOriginf, frame.View, 16);
        api.World.FrameProfiler.Enter("wmap-far-lights");
        lightsTime.Begin();
        lightsProgram.Use();
        try { mid.DrawLights(lightsProgram.ProgramId, frame, recorded, frame.Start, client.Config.LightDistance, render.FrameWidth, render.FrameHeight); }
        finally { lightsProgram.Stop(); lightsTime.End(); api.World.FrameProfiler.Leave(); }
    }

    /// <summary>What the mid-distance view does now, for <c>.wmap mid</c>.</summary>
    public string MidStatus()
    {
        var c = client.Config;
        string parts = $"recorded terrain {(c.MidDistanceRecorded ? "on" : "off")}, terraces {(c.MidDistanceTerraces ? "on" : "off")} (to {c.TerraceDistance}), lights {(c.MidDistanceLights ? "on" : "off")} (to {c.LightDistance})";
        if (midFailed) return parts + "; off: its shaders did not compile (see client-main.log)";
        string tiles = recorded == null ? "no recordings loaded" : $"four-block tiles {recorded.CoarseReady}/{recorded.CoarseWanted}, two-block {recorded.FineReady}/{recorded.FineWanted}, {mid.Boxes} structure blocks, {mid.Lights} lights, {mid.Patches} terrace patches";
        return $"{parts}; {tiles}; far terrain {Status}; GPU {terrainTime.Milliseconds:0.00} ms far terrain, {lightsTime.Milliseconds:0.00} ms lights";
    }

    /// <summary>The daylight the game's sky and chunk shaders get (SystemRenderSkyColor: 1.25 × the day's light less half
    /// the moon's, at least 0.05, fading out far above the sea). Far terrain's sky colour, lighting and how much it takes
    /// of the sky's colour use the same value, so at night it stays as dark as the game's own terrain.</summary>
    public static float SkyDaylight(float dayLight, float moonLight, double aboveSea)
    {
        float daylight = 1.25f * Math.Max(dayLight - moonLight / 2, 0.05f);
        float high = (float)Math.Clamp((aboveSea - 1000) / 30000, 0, 1);
        return Math.Max(0, daylight * (1 - high));
    }

    /// <summary>
    /// The game fades its chunks out well inside the view distance: water from 75 % to 80 % of it
    /// (chunkliquid.vsh), land from 80 % to 85 % (chunkopaque.vsh). Far terrain starts where water begins to fade,
    /// so it is already there behind fading water and land. Starting at the view distance left a ring
    /// where fading water showed far terrain twelve blocks lower and kilometres away.
    /// </summary>
    public static double StartFor(int viewDistance) => FarStart.For(viewDistance);

    /// <summary>A sea-level water block's surface is 7/8 of a block high: the real sea lies 1/8 below <c>SeaLevel</c>.</summary>
    public const double WaterBelowSeaLevel = 0.125;

    /// <summary>Whether the near band and the climate tiles can be drawn with this server's manifest and these settings:
    /// a 0.4.0 server that shares predictions with relief; climate also needs its biome sampler (Earth Map).</summary>
    /// <summary>Whether this far terrain was made for the near band and climate tiles a manifest allows (always, when it is off).</summary>
    public bool Matches(AtlasInfo info) => !registered || disposed || TilesFor(info, client.Config) == (wantBand, wantClimate);

    public static (bool Band, bool Climate) TilesFor(AtlasInfo info, WmapConfig config)
    {
        bool tiles = info != null && info.FarTiles && info.CanPredict && !info.FlatPrediction;
        return (tiles && config.FarTerrainNearBand, tiles && config.FarTerrainClimate && info.Biome && info.BiomeId != null);
    }

    /// <summary>Keeps the near band and climate tiles around the camera while the server allows them.</summary>
    void UpdateTiles(AtlasInfo info, double cameraX, double cameraZ, double far)
    {
        frame.Tiles = null;
        if (!wantBand && !wantClimate) return;
        // The band's tiles carry biomes either way; without climate their tints and forests are not drawn.
        if (!tintsLoaded && wantClimate) { tintsLoaded = true; plantTint = LoadTint("textures/environment/planttint.png"); waterTint = LoadTint("textures/environment/watertint.png"); }
        frame.PlantTint = plantTint; frame.WaterTint = waterTint;
        frame.Canopy = wantClimate ? 8 : 0;
        // Permissions can change during a session: then no more requests, and the chart alone.
        if (!info.CanPredict) return;
        if (tiles == null)
        {
            var config = client.Config;
            tiles = new FarTiles(client.Topology, info.Session, client.SendTileRequest, client.CacheFolder, config.DiskCacheMiB,
                config.NearBandDistance, far, wantBand, wantClimate, info.BiomeId) { HeightRange = info.ChartHeightRange };
            tiles.CreateTextures();
            api.Logger.Notification("[wmap] Far terrain tiles: near band {0} (to {1} blocks), climate {2}.",
                tiles.Band != null ? "on" : "off", config.NearBandDistance, tiles.Climate != null ? "on" : "off");
        }
        var origin = FarTerrainDraw.Origin(draw.Mesh, cameraX, cameraZ);
        tiles.Update(cameraX, cameraZ, origin.X, frame.DrawFrom);
        frame.Tiles = tiles;
        frame.BandRadius = client.Config.NearBandDistance;
        frame.BandFadeStart = frame.BandRadius - 336;
        if (tiles.Band != null) Status = BandStatus(Status, ref statusCounts, tiles.Band.Ready, tiles.Band.Wanted, tiles.Climate?.Ready, tiles.Climate?.Wanted);
    }

    /// <summary>One of the game's climate colour maps as a GL texture, or 0.</summary>
    int LoadTint(string path)
    {
        try
        {
            var asset = api.Assets.TryGet(new AssetLocation("game", path));
            if (asset?.Data == null) return 0;
            using var bitmap = api.Render.BitmapCreateFromPng(asset.Data);
            using var unpack = new GlUnpack();
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, bitmap.Width, bitmap.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, bitmap.Pixels);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return id;
        }
        catch (Exception e)
        {
            api.Logger.Warning("[wmap] Could not load the game's {0}; far terrain keeps the atlas colours: {1}", path, e.Message);
            return 0;
        }
    }

    public void Receive(TileResponse response) => tiles?.Receive(response);

    static void Copy(Vec4f v, float[] to) { to[0] = v.R; to[1] = v.G; to[2] = v.B; to[3] = v.A; }
    static void Copy(Vec3f v, float[] to) { to[0] = v.X; to[1] = v.Y; to[2] = v.Z; }

    /// <summary>The game's camera clips at 3000 blocks. Raise its far plane while far terrain is drawn.</summary>
    void RaiseFarPlane(float wanted)
    {
        if (zFarField == null)
        {
            camera = api.World.GetType().GetField("MainCamera")?.GetValue(api.World);
            zFarField = camera?.GetType().GetField("ZFar");
            if (zFarField == null) return;
            originalZFar = (float)zFarField.GetValue(camera);
        }
        float current = (float)zFarField.GetValue(camera);
        if (current >= wanted) return;
        zFarField.SetValue(camera, wanted);
        raisedZFar = wanted;
        api.Render.Reset3DProjection();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered)
        {
            api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            api.Event.ReloadShader -= LoadShader;
        }
        // Give the far plane back unless someone else changed it since.
        if (zFarField != null && raisedZFar > 0 && (float)zFarField.GetValue(camera) == raisedZFar)
        {
            zFarField.SetValue(camera, originalZFar);
            api.Render.Reset3DProjection();
        }
        if (registered) api.Event.UnregisterRenderer(lights, EnumRenderStage.Opaque);
        draw.Dispose();
        tiles?.Dispose();
        recorded?.Dispose();
        mid.Dispose();
        terrainTime.Dispose(); lightsTime.Dispose();
        if (plantTint != 0) GL.DeleteTexture(plantTint);
        if (waterTint != 0) GL.DeleteTexture(waterTint);
        program?.Dispose(); cellsProgram?.Dispose(); mapProgram?.Dispose(); lightsProgram?.Dispose();
    }
}

/// <summary>The mid-distance view's distant lights, drawn after the game's chunks (the far terrain renderer runs before
/// them) so the real terrain in front hides them too.</summary>
sealed class FarLights : IRenderer
{
    readonly FarTerrain far;
    public FarLights(FarTerrain far) { this.far = far; }
    public double RenderOrder => 0.9;
    public int RenderRange => 99999;
    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => far.DrawLights();
    public void Dispose() { }
}

/// <summary>GPU time between <see cref="Begin"/> and <see cref="End"/>, from timestamp queries read a few frames later (never
/// waiting for them), averaged. Timestamps, unlike elapsed-time queries, cannot clash with another mod's open query.</summary>
public sealed class GpuTimer : IDisposable
{
    const int Depth = 4;
    readonly int[] queries = new int[Depth * 2];
    readonly bool[] pending = new bool[Depth];
    int next;
    /// <summary>Averaged over about the last 30 measured frames.</summary>
    public double Milliseconds { get; private set; }
    public void Begin()
    {
        if (queries[0] == 0) GL.GenQueries(queries.Length, queries);
        int i = next;
        if (pending[i])
        {
            GL.GetQueryObject(queries[i * 2 + 1], GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available == 0) { skip = true; return; }
            GL.GetQueryObject(queries[i * 2], GetQueryObjectParam.QueryResult, out long start);
            GL.GetQueryObject(queries[i * 2 + 1], GetQueryObjectParam.QueryResult, out long end);
            Milliseconds += ((end - start) / 1e6 - Milliseconds) / 30;
            pending[i] = false;
        }
        skip = false;
        GL.QueryCounter(queries[i * 2], QueryCounterTarget.Timestamp);
    }
    bool skip;
    public void End()
    {
        if (skip || queries[0] == 0) return;
        GL.QueryCounter(queries[next * 2 + 1], QueryCounterTarget.Timestamp);
        pending[next] = true;
        next = (next + 1) % Depth;
    }
    public void Dispose() { if (queries[0] != 0) GL.DeleteQueries(queries.Length, queries); queries[0] = 0; }
}

/// <summary>
/// Optional sea colour from the Sea mod, read by reflection: a member <c>FarWaterColor</c> on <c>Sea.SeaSystem</c>
/// (field, property or parameterless method) returning RGB 0..1 as float[] / Vec3f / Vec4f, or an int 0xRRGGBB.
/// Sea does not provide one yet; without it the atlas's sea colours are used.
/// </summary>
public sealed class SeaWater
{
    readonly object system;
    readonly MemberInfo member;
    float[] color;
    long readAt;

    public SeaWater(ICoreClientAPI api)
    {
        system = api.ModLoader?.Systems?.FirstOrDefault(s => s?.GetType().FullName == "Sea.SeaSystem");
        var type = system?.GetType();
        member = (MemberInfo)type?.GetField("FarWaterColor") ?? (MemberInfo)type?.GetProperty("FarWaterColor") ?? type?.GetMethod("FarWaterColor", Type.EmptyTypes);
    }

    /// <summary>RGB, or null when Sea does not supply a colour. Refreshed every few seconds.</summary>
    public float[] Color
    {
        get
        {
            if (member == null || Environment.TickCount64 - readAt < 5000) return color;
            readAt = Environment.TickCount64;
            try
            {
                object v = member switch
                {
                    FieldInfo f => f.GetValue(system),
                    PropertyInfo p => p.GetValue(system),
                    MethodInfo m => m.Invoke(system, null),
                    _ => null
                };
                color = Parse(v);
            }
            catch (Exception) { color = null; }
            return color;
        }
    }

    public static float[] Parse(object v) => v switch
    {
        float[] a when a.Length >= 3 => new[] { a[0], a[1], a[2] },
        double[] a when a.Length >= 3 => new[] { (float)a[0], (float)a[1], (float)a[2] },
        Vec3f a => new[] { a.X, a.Y, a.Z },
        Vec4f a => new[] { a.R, a.G, a.B },
        int rgb => new[] { (rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f },
        _ => null
    };
}
