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
public sealed partial class FarTerrain : IRenderer
{
    // Before the game's terrain (0.37), after the sky (0.2) and sun (0.3).
    public double RenderOrder => 0.36;
    public int RenderRange => 99999;

    readonly ICoreClientAPI api;
    readonly WmapClient client;
    readonly FarTerrainDraw draw = new();
    readonly FarFrame frame = new();
    readonly Task<FarMesh> building;
    readonly bool wantBand, wantClimate;
    FarTiles tiles;
    /// <summary>The near band and climate tiles, while the server provides them; null otherwise.</summary>
    public FarTiles Tiles => tiles;
    int plantTint, waterTint, grassSeasons;
    bool tintsLoaded;
    IShaderProgram program, cellsProgram, mapProgram, lightsProgram;
    // The cover pass's programs (0.5.5, WM-0054): farterrain and farcells compiled with WMAP_COVER, the only ones that write
    // gl_FragDepth, so the other passes keep the GPU's early depth test (wmapfarshade.ash).
    IShaderProgram coverProgram, coverCellsProgram;
    const string CoverDefine = "#define WMAP_COVER 1\r\n";
    bool registered, failed, disposed;
    // The mid-distance view: recordings around the camera, its GL resources, the lights' renderer (after the game's chunks).
    RecordedTiles recorded;
    readonly MidDistanceDraw mid = new();
    readonly FarLights lights;
    bool midFailed, drawnThisFrame;
    internal bool DrawnThisFrame => drawnThisFrame;
    readonly GpuTimer terrainTime = new(), lightsTime = new();
    /// <summary>The player's recordings for the mid-distance view, once any of its parts is on; null otherwise.</summary>
    public RecordedTiles Recorded => recorded;
    object camera;
    FieldInfo zFarField;
    bool farPlaneMissing;
    float raisedZFar, originalZFar;
    public string Status { get; private set; } = "";

    public FarTerrain(ICoreClientAPI api, WmapClient client)
    {
        this.api = api; this.client = client;
        explored = client.Info?.Explored == true;
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
        api.Event.ReloadShader += LoadShader;
        LoadShader();
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "wmap-far-terrain");
        lights = new FarLights(this);
        api.Event.RegisterRenderer(lights, EnumRenderStage.Opaque, "wmap-far-lights");
        registered = true;
    }

    bool LoadShader()
    {
        IShaderProgram Load(ref IShaderProgram slot, string name, out bool ok, string define = null)
        {
            slot?.Dispose();
            slot = api.Shader.NewShaderProgram();
            slot.AssetDomain = "wmap";
            api.Shader.RegisterFileShaderProgram(name, slot);
            // A variant of the same files: its define goes after the game's own (ShaderRegistry's prefix code).
            if (define != null && slot.FragmentShader != null) slot.FragmentShader.PrefixCode += define;
            ok = slot.Compile();
            if (ok && !draw.PlanUnits(slot.ProgramId, PinGameSamplers(slot)))
            {
                api.Logger.Error("[wmap] Far terrain's {0} shader: its texture units do not fit with the game's.", name);
                ok = false;
            }
            return slot;
        }
        draw.ProgramReloaded();
        mid.ProgramReloaded();
        Load(ref program, "farterrain", out bool compiled);
        Load(ref coverProgram, "farterrain", out bool cover, CoverDefine);
        compiled &= cover;
        failed = !compiled;
        if (failed) { Status = "off: shader did not compile (see client-main.log)"; api.Logger.Error("[wmap] Far terrain shader did not compile; far terrain is off."); }
        // The mid-distance view's programs: without them far terrain still draws, as before.
        Load(ref cellsProgram, "farcells", out bool cells);
        Load(ref coverCellsProgram, "farcells", out bool coverCells, CoverDefine);
        cells &= coverCells;
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
        if (!CanDraw(out var player)) return;
        var data = api.World.Player.WorldData;
        int view = data.LastApprovedViewDistance > 0 ? Math.Min(data.LastApprovedViewDistance, data.DesiredViewDistance) : data.DesiredViewDistance;
        double far = Math.Min(client.Config.FarTerrainDistance, draw.Mesh.Radius);
        if (far <= view + 64) return;
        RaiseFarPlane((float)far + 1024);
        PrepareFrame(player, data, view, far);
        DrawFrame(client.Info);
    }

    /// <summary>Whether far terrain is drawn this frame: its mesh is up (uploaded here once built), the world chart is ready,
    /// the player is in the main dimension, and the server shares a chart with relief. Sets <see cref="Status"/>.</summary>
    bool CanDraw(out EntityPlayer player)
    {
        player = null;
        if (disposed || failed) return false;
        if (program == null) return Not("waiting for its shader");
        if (!draw.Ready)
        {
            if (building == null || !building.IsCompleted) return false;
            if (!building.IsCompletedSuccessfully)
            {
                failed = true;
                api.Logger.Error("[wmap] Far terrain could not build its mesh: {0}", building.Exception?.InnerException);
                return Not("off: " + building.Exception?.InnerException?.Message);
            }
            draw.Upload(building.Result);
            api.Logger.Notification("[wmap] Far terrain: {0} rings to {1} blocks, {2} vertices, {3} triangles, {4:0.0} MiB.",
                draw.Mesh.Rings, draw.Mesh.Radius, draw.Mesh.VertexCount, draw.Mesh.Indices.Length / 3, draw.Bytes / 1048576.0);
        }
        var renderer = client.Renderer;
        player = api.World.Player?.Entity;
        // A land/sea mask with invented heights would raise flat plates of land on the horizon,
        // also where another generator (Terra Prety) put mountains or sea.
        if (client.Info?.FlatPrediction == true) return Not("off: the world chart has no relief (approximate continents)");
        // The chart drawn unmasked on the horizon shows terrain no one has charted: only while the server shares it.
        // A chart downloaded in creative stays for the atlas, which masks it in gameplay.
        if (client.Info?.CanPredict != true) return Not("off: the server does not share its world chart with you");
        if (!renderer.BaseReady) return Not("waiting for the world chart");
        if (player == null || player.Pos.Dimension != 0) return false;
        Status = OnStatus(Status);
        return true;
    }

    /// <summary>Far terrain is not drawn this frame, for this reason: kept in <see cref="Status"/> and logged when it changes, so a
    /// far view that stays empty says why in the client log.</summary>
    bool Not(string why)
    {
        if (Status == why) return false;
        Status = why;
        api.Logger.Notification("[wmap] Far terrain: {0}.", why);
        return false;
    }

    /// <summary>This frame's values (<see cref="frame"/>): the camera and its matrices, where far terrain starts, the loaded
    /// columns, light and fog, and the tiles and recordings around the camera.</summary>
    void PrepareFrame(EntityPlayer player, IWorldPlayerData data, int view, double far)
    {
        var renderer = client.Renderer;
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
        // A survival client draws only land the server has sent (WM-D63). So does a client that has just left survival, until
        // the whole chart has come: the renderer still holds its chart, whose other texels are not the world's (WmapClient.Follow).
        frame.KnownTexture = renderer.KnownTexture;
        frame.Explored = frame.KnownTexture != 0;
        frame.BlocksPerTexel = renderer.BaseTexel;
        // Where the game's chunks end: 0.75 of the view distance; nearer only over chunk columns not loaded yet (FarStart,
        // a mask the shaders read).
        long now = Environment.TickCount64;
        frame.Start = StartFor(view);
        // The game fades its blocks into the sky by its own view distance setting (the shaders' viewDistance).
        frame.GameFade = FarStart.GameFade(data.DesiredViewDistance);
        frame.CloudAbove = CloudAbove(cam.Y);
        // Loaded columns are known out to where far terrain covers the game's blocks: nearer, it draws only over columns not
        // loaded (wmapfarshade.ash).
        double scanReach = Math.Max(frame.Start, frame.GameFade);
        // Again at once when the camera moved a column (also a teleport or a seam jump) or the start changed (FarStart.ScanDue).
        if (FarStart.ScanDue(loadedScanX, loadedScanZ, loadedScanStart, cam.X, cam.Z, scanReach, now, loadedAt))
        {
            loadedAt = now + FarStart.ScanMs;
            loadedSeaChunk = info.SeaLevel / 32;
            loadedRadius = FarStart.Scan(loadedColumn ??= ColumnLoaded, cam.X, cam.Z, scanReach, loadedMask);
            loadedScanX = cam.X; loadedScanZ = cam.Z; loadedScanStart = scanReach;
            frame.LoadedMask = draw.UploadLoaded(loadedMask);
        }
        frame.FillStart = loadedRadius;
        (frame.MaskX, frame.MaskZ) = FarStart.MaskOffset(loadedScanX, loadedScanZ, cam.X, cam.Z);
        frame.Far = far;
        frame.PlanetRadius = info.PlanetRadius; frame.CurveFrom = view;
        var ambient = api.Ambient;
        Copy(ambient.BlendedFogColor, frame.FogColor);
        frame.FogDensity = ambient.BlendedFogDensity;
        frame.FogMin = ambient.BlendedFogMin;
        frame.HorizonFog = ambient.BlendedCloudDensity;
        var calendar = api.World.Calendar;
        Copy(calendar.SunPositionNormalized, frame.SunPosition);
        Copy(calendar.SunColor, frame.SunColor);
        Copy(api.Render.AmbientColor, frame.Ambient);
        frame.SunLevel = SunLevel(api.World.SunLightLevels, api.World.SunBrightness);
        frame.DebugView = DebugView;
        frame.DayLight = SkyDaylight(calendar.DayLightStrength, calendar.MoonLightStrength, player.Pos.Y - info.SeaLevel);
        UpdateTiles(info, cam.X, cam.Z, far);
        UpdateRecorded(cam.X, cam.Z);
        // Far terrain covers the game's blocks from where they fade, at its own height: the push-down that keeps it under them
        // nearer is gone by then, so the ground does not step down at the edge of the real chunks.
        if (frame.GameFade > frame.Start) frame.PushFade = Math.Min(frame.PushFade, Math.Max(1, frame.GameFade - frame.Start));
    }

    /// <summary>Draws the frame: the cell map when it changed, then one pass, or three where the game fades its blocks.</summary>
    void DrawFrame(AtlasInfo info)
    {
        api.World.FrameProfiler.Enter("wmap-far-terrain");
        terrainTime.Begin();
        frame.Stamp++;
        draw.BeginFrame();
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
            int mapHeight = info.MapHeight > 0 ? info.MapHeight : 512;
            lightsDrawn = false;
            if (frame.GameFade <= 0) DrawPass(0, mapHeight);
            else
            {
                // Three passes (0.5.3-rc.6, WM-D56), before the game's chunks (render order 0.36 < 0.37): far terrain's own depth,
                // its colour where that left it nearest, the distant lights against it, and then the cover: the depth of the point
                // where the game starts fading its blocks, along each ray to far land past it and below the clouds. The game's
                // blocks from there on (fading into the sky colour) then lie behind it at any distance, and every nearer one in
                // front. rc.5 forced the depth in one pass out to 0.95 of the view distance only (two far surfaces on a pixel
                // would have had the same depth); trees in the fading band stayed in front of far land farther away.
                // The depth pass draws the terraces and recorded structures only, and the colour pass the smooth mesh with its own
                // depth, then their colour without depth writes; the cover has its own programs (0.5.5, WM-0054). A depth pass
                // pays where many surfaces cover a pixel, as the terraces' cells and walls do, not under the smooth mesh; with depth
                // writes the shaders' discard has the GPU shade every fragment before its depth test, and a program that writes
                // gl_FragDepth (the cover) has that in every pass. The same picture and depth, 1.87 ms down to 1.08 ms in the GPU
                // bench (tests/FarGpuBench) with the mesh's hidden rows left out (FarMesh.Ranges).
                GL.GetInteger(GetPName.DepthFunc, out int depthFunc);
                bool depthMask = GL.GetBoolean(GetPName.DepthWritemask);
                GL.GetBoolean(GetPName.ColorWritemask, colorMask);
                try
                {
                    GL.ColorMask(false, false, false, false); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    DrawCells(1, mapHeight, cellsProgram);
                    GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    DrawMesh(2, program);
                    GL.DepthMask(false);
                    DrawCells(2, mapHeight, cellsProgram);
                    GL.DepthFunc((DepthFunction)depthFunc);
                    DrawLights();
                    GL.ColorMask(false, false, false, false); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Less);
                    DrawMesh(3, coverProgram);
                    DrawCells(3, mapHeight, coverCellsProgram);
                }
                finally
                {
                    GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
                    GL.DepthMask(depthMask);
                    GL.DepthFunc((DepthFunction)depthFunc);
                    frame.Pass = 0;
                }
            }
            drawnThisFrame = true;
        }
        finally { draw.EndFrame(); terrainTime.End(); api.World.FrameProfiler.Leave(); }
    }

    /// <summary>The smooth mesh, the terraces and the recorded structures in one pass (<see cref="FarFrame.Pass"/>).</summary>
    void DrawPass(int pass, int mapHeight)
    {
        DrawMesh(pass, program);
        DrawCells(pass, mapHeight, cellsProgram);
    }

    /// <summary>The smooth mesh with <paramref name="with"/> (farterrain or its cover variant).</summary>
    void DrawMesh(int pass, IShaderProgram with)
    {
        frame.Pass = pass;
        with.Use();
        try { draw.Draw(with.ProgramId, frame); }
        finally { with.Stop(); }
    }

    /// <summary>The terraces and the recorded structures with <paramref name="with"/> (farcells or its cover variant).</summary>
    void DrawCells(int pass, int mapHeight, IShaderProgram with)
    {
        frame.Pass = pass;
        if (frame.TerraceRadius <= frame.Start && frame.Recorded == null) return;
        with.Use();
        try
        {
            mid.DrawTerraces(with.ProgramId, draw, frame, mapHeight);
            if (frame.Recorded != null) mid.DrawStructures(with.ProgramId, draw, frame, frame.SmoothStart);
        }
        finally { with.Stop(); }
    }

    bool lightsDrawn;
    readonly bool[] colorMask = new bool[4];

    /// <summary>Blocks from the camera up to the underside of the game's cloud layer (its cloud level less 16), 0 when unknown.</summary>
    double CloudAbove(double cameraY)
    {
        var weather = api.ModLoader.GetModSystem<Vintagestory.GameContent.WeatherSystemClient>();
        if (weather == null) return 0;
        return weather.CloudLevelRel * api.World.BlockAccessor.MapSizeY - 16 - cameraY;
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

    /// <summary>The distant lights: between far terrain's colour and its cover (0.5.3-rc.6), so far hills hide them and the game's
    /// chunks drawn after cover them where nearer; else after the game's chunks (<see cref="FarLights"/>).</summary>
    internal void DrawLights()
    {
        if (lightsDrawn) return;
        lightsDrawn = true;
        if (disposed || midFailed || lightsProgram == null || recorded == null || !client.Config.MidDistanceLights) { mid.Lights = 0; return; }
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
        string tiles = recorded == null ? "no recordings loaded" : $"four-block tiles {recorded.CoarseReady}/{recorded.CoarseWanted}, two-block {recorded.FineReady}/{recorded.FineWanted}, {mid.Boxes} structure blocks, {mid.Lights} lights, {mid.Patches} terrace patches"
            + $"; forest canopy {frame.Canopy:0} blocks ({(recorded.CanopyCells >= FarStart.CanopyCellsNeeded ? "recorded" : "default")}: middle treetop {recorded.CanopyHeight} of {recorded.CanopyCells} recorded tree cells)";
        return $"{parts}; {tiles}; far terrain {Status}; GPU {terrainTime.Milliseconds:0.00} ms far terrain, {lightsTime.Milliseconds:0.00} ms lights";
    }

    /// <summary>Debug view for every far terrain (<c>.wmap fardebug</c>, not saved): 0 off, 1 the colour alone, 2 lit without
    /// haze or fog, to compare far terrain's colour with the game's own ground on screen.</summary>
    public static int DebugView;

    /// <summary>The brightness of full sunlight the game's chunks are lit with: the world's sun-light level table at its sun
    /// brightness (vanilla: SunLightLevels[24] = 0.866, which the chunk tesselator puts in each sky-lit vertex's light and
    /// chunkopaque.vsh multiplies the ambient colour by). 0 when the table is missing.</summary>
    public static float SunLevel(float[] levels, int brightness) =>
        levels == null || levels.Length == 0 ? 0 : levels[Math.Clamp(brightness, 0, levels.Length - 1)];

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

    /// <summary>Whether this far terrain was made for the near band and climate tiles a manifest allows (always, when it is off).</summary>
    public bool Matches(AtlasInfo info) => !registered || disposed || TilesFor(info, client.Config) == (wantBand, wantClimate) && info.Explored == explored;
    /// <summary>Made for a survival client (0.5.4, WM-D63): far terrain draws only land the server has sent, and its tiles stay
    /// off the shared disk cache.</summary>
    readonly bool explored;

    /// <summary>Whether the near band and the climate tiles can be drawn with this server's manifest and these settings:
    /// a 0.4.0 server that shares predictions with relief; climate also needs its biome sampler (Earth Map).</summary>
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
        if (!tintsLoaded && wantClimate)
        {
            tintsLoaded = true;
            plantTint = LoadTint("textures/environment/planttint.png"); waterTint = LoadTint("textures/environment/watertint.png");
            grassSeasons = LoadGrassSeasons();
        }
        if (grassSeasons != 0) UpdateAltitudeRow(frame.HeightScale);
        frame.PlantTint = plantTint; frame.WaterTint = waterTint; frame.GrassSeasons = grassSeasons;
        // The time of year the game colours plants by (its colour-map shader's seasonRel), and the calendar's.
        frame.SeasonRel = api.Render.ShaderUniforms.SeasonRel;
        frame.YearRel = (float)api.World.Calendar.YearRel;
        // A full forest's canopy: the recorded forest's middle treetop height around the camera where enough of it is
        // recorded (0.5.3-rc.8, WM-0020), else 8 blocks. Predicted forest stood 8 blocks high everywhere, so it met real
        // recorded treetops (about 15 blocks in Latvia) with a step all along where the recording ended.
        frame.Canopy = !wantClimate ? 0 : FarStart.CanopyFor(recorded?.CanopyHeight ?? 0, recorded?.CanopyCells ?? 0);
        // Permissions can change during a session: then no more requests, and the chart alone.
        if (!info.CanPredict) return;
        if (tiles == null)
        {
            var config = client.Config;
            tiles = new FarTiles(client.Topology, info.Session, client.SendTileRequest, explored ? null : client.CacheFolder, config.DiskCacheMiB,
                config.NearBandDistance, far, wantBand, wantClimate, info.BiomeId) { HeightRange = info.ChartHeightRange, Requestable = explored ? client.KnownAt : null };
            tiles.CreateTextures();
            api.Logger.Notification("[wmap] Far terrain tiles: near band {0} (to {1} blocks), climate {2}.",
                tiles.Band != null ? "on" : "off", config.NearBandDistance, tiles.Climate != null ? "on" : "off");
        }
        var origin = FarTerrainDraw.Origin(draw.Mesh, cameraX, cameraZ);
        tiles.Update(cameraX, cameraZ, origin.X, frame.DrawFrom);
        frame.Tiles = tiles;
        frame.BandRadius = client.Config.NearBandDistance;
        frame.BandFadeStart = frame.BandRadius - FarFrame.BandFade;
        if (tiles.Band != null) Status = BandStatus(Status, ref statusCounts, tiles.Band.Ready, tiles.Band.Wanted, tiles.Climate?.Ready, tiles.Climate?.Wanted);
    }

    public void Receive(TileResponse response) => tiles?.Receive(response);

    static void Copy(Vec4f v, float[] to) { to[0] = v.R; to[1] = v.G; to[2] = v.B; to[3] = v.A; }
    static void Copy(Vec3f v, float[] to) { to[0] = v.X; to[1] = v.Y; to[2] = v.Z; }

    /// <summary>The game's camera clips at 3000 blocks. Raise its far plane while far terrain is drawn.</summary>
    void RaiseFarPlane(float wanted)
    {
        if (zFarField == null)
        {
            // Looked up once (0.5.3, WM-0042: tried again every frame, without a word, where the game has no such field).
            if (farPlaneMissing) return;
            camera = api.World.GetType().GetField("MainCamera")?.GetValue(api.World);
            zFarField = camera?.GetType().GetField("ZFar");
            if (zFarField == null)
            {
                farPlaneMissing = true;
                api.Logger.Warning("[wmap] The game's camera has no ZFar field: far terrain past {0:0} blocks is clipped.", api.Render.ShaderUniforms.ZFar);
                return;
            }
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
        if (grassSeasons != 0) GL.DeleteTexture(grassSeasons);
        program?.Dispose(); cellsProgram?.Dispose(); mapProgram?.Dispose(); lightsProgram?.Dispose(); coverProgram?.Dispose(); coverCellsProgram?.Dispose();
    }
}
