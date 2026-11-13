using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Everything one far-terrain frame needs besides the game's own shader uniforms.</summary>
public sealed class FarFrame
{
    /// <summary>Independent copies: the game's matrix getters may share a temporary array.</summary>
    public float[] Projection = new float[16], View = new float[16];
    public double CameraX, CameraY, CameraZ;
    public Topology Topology;
    public int SeaLevel;
    /// <summary>Blocks from the sea surface to the top of the world, for elevation tints.</summary>
    public double HeightScale = 200;
    /// <summary>Blocks the chart's heights span (<see cref="AtlasInfo.ChartHeightRange"/>).</summary>
    public double HeightRange = Semantic.DefaultHeightRange;
    public int ChartTexture;
    public double BlocksPerTexel;
    /// <summary>Far terrain starts Start blocks (horizontal) from the camera, where the game's chunks fade, and ends at Far.</summary>
    public double Start, Far;
    /// <summary>Blocks the mesh sits lower at Start, fading out over <see cref="PushFade"/> blocks.</summary>
    public double Push = 12, PushFade = 512;
    /// <summary>The planet's radius in blocks (<see cref="AtlasInfo.PlanetRadius"/>); 0 is flat. Past <see cref="CurveFrom"/>
    /// the ground sinks by (distance − CurveFrom)² / (2 × radius).</summary>
    public double PlanetRadius;
    /// <summary>Blocks from the camera where the curve starts: the view distance, past the game's own flat chunks and their
    /// fade into this mesh (at least Start).</summary>
    public double CurveFrom;
    /// <summary>Sea colour override (Sea mod), or null for the atlas palette.</summary>
    public float[] Water;
    public float[] FogColor = { 0.7f, 0.8f, 0.9f, 1 }, SunPosition = { 0, 1, 0 }, SunColor = { 1, 1, 1 };
    public float FogDensity, FogMin, HorizonFog, DayLight = 1;
    /// <summary>The game's ambient light colour (scene brightness included), which lights its own chunks.</summary>
    public float[] Ambient = { 1, 1, 1 };
    /// <summary>The near band and climate tiles, or null.</summary>
    public FarTiles Tiles;
    /// <summary>The game's plant and water colour maps (GL textures), or 0.</summary>
    public int PlantTint, WaterTint;
    /// <summary>Blocks a full forest stands above the ground.</summary>
    public double Canopy = 8;
    /// <summary>Where the near band starts fading out toward the chart, and where it ends.</summary>
    public double BandFadeStart = 1200, BandRadius = 1536;
    /// <summary>The mid-distance view (WM-P06 prototype): the player's recordings drawn over the prediction, or null.</summary>
    public RecordedTiles Recorded;
    /// <summary>Terraces from <see cref="Start"/> to here (0: none); the smooth mesh then starts here.</summary>
    public double TerraceRadius;
    /// <summary>The terraces' cell map (<see cref="MidDistanceDraw"/>): its textures, its north-west corner in blocks from the
    /// snapped origin, and its cell size.</summary>
    public int CellTop, CellColor;
    public double CellMapX, CellMapZ, CellSize = 2;
    /// <summary>Far terrain draws nearer than <see cref="Start"/>, from here, but only over chunk columns not loaded yet
    /// (<see cref="LoadedMask"/>; 0.5.0-pre.3). NaN: from Start.</summary>
    public double FillStart = double.NaN;
    /// <summary>Loaded chunk columns around the camera (<see cref="FarStart.Scan"/>, a GL texture, 0 for none) and the blocks
    /// from its north-west corner to the camera (<see cref="FarStart.MaskOffset"/>).</summary>
    public int LoadedMask;
    public double MaskX, MaskZ;
    /// <summary>Where far terrain draws from: <see cref="FillStart"/> where columns are missing, else Start.</summary>
    public double DrawFrom => double.IsNaN(FillStart) ? Start : Math.Max(48, Math.Min(Start, FillStart));
    /// <summary>Where the smooth mesh starts: past the terraces when there are any.</summary>
    public double SmoothStart => TerraceRadius > Start ? TerraceRadius : DrawFrom;
    /// <summary>Blocks the terraces and the smooth mesh reach past <see cref="TerraceRadius"/> into each other (0.5.0-pre.10),
    /// each sinking under the other toward its own cut (wmapJoinPush): where the smooth mesh began higher than the last
    /// terrace, no surface closed the step and single pixels of sky showed along the ring.</summary>
    public const double JoinOverlap = 16;
    /// <summary>Where the terraces end, 0 without terraces (the shaders' wmapPush.w).</summary>
    public double TerraceEdge => TerraceRadius > Start ? TerraceRadius : 0;
}

/// <summary>
/// Uploads a <see cref="FarMesh"/> and draws it in one call with whatever program is current. Only Wmap's own
/// uniforms are set here; the game's shader system sets its fog, sky and light uniforms when the program is used.
/// <see cref="Bind"/> and <see cref="Unbind"/> set the same uniforms and textures for the mid-distance view's programs
/// (<see cref="MidDistanceDraw"/>). Render thread only.
/// </summary>
public sealed class FarTerrainDraw : IDisposable
{
    /// <summary>Texture units with the plan's base at 4 (<see cref="UnitBase"/>): above the units the game's samplers are pinned
    /// to (<see cref="PinGameSamplers"/>). A program with more game samplers has all of these moved up by as many.</summary>
    public const int ChartUnit = 15, BandUnit = 14, BandBiomeUnit = 13, ClimateBiomeUnit = 12, PlantTintUnit = 11, WaterTintUnit = 10,
        RecHeightUnit = 9, RecColorUnit = 8, RecSlotsUnit = 7, CellTopUnit = 6, CellColorUnit = 5, LoadedUnit = 4;
    public static readonly int[] Units = { ChartUnit, BandUnit, BandBiomeUnit, ClimateBiomeUnit, PlantTintUnit, WaterTintUnit, RecHeightUnit, RecColorUnit, RecSlotsUnit, CellTopUnit, CellColorUnit, LoadedUnit };
    /// <summary>Wmap's samplers, in the order of <see cref="Units"/>.</summary>
    public static readonly string[] SamplerNames = { "wmapChart", "wmapBandTex", "wmapBandBiome", "wmapClimateBiome", "wmapPlantTint", "wmapWaterTint", "wmapRecHeight", "wmapRecColor", "wmapRecSlots", "wmapCellTop", "wmapCellColor", "wmapLoaded" };
    readonly int[] previousBindings = new int[Units.Length];
    int previousUnit, unitShift;
    readonly Dictionary<int, int> unitShifts = new();

    /// <summary>
    /// The game binds its own textures (shadow maps, sky, glow, liquid depth, or any other sampler a game include declares) to
    /// the unit <c>textureLocations</c> gives each, numbered by the order samplers are declared in, Wmap's among them. Every
    /// sampler that is not Wmap's is pinned to units 0, 1, ... in that order (0.5.0-pre.4: in 0.5.0-pre.3 the five names it
    /// knew only; with all five present the fifth landed on the loaded-columns mask's unit 4). Returns how many were pinned.
    /// </summary>
    public static int PinGameSamplers(Dictionary<string, int> units)
    {
        var names = new List<KeyValuePair<string, int>>(units);
        names.Sort((a, b) => a.Value.CompareTo(b.Value));
        int next = 0;
        foreach (var (name, _) in names)
            if (!name.StartsWith("wmap", StringComparison.Ordinal)) units[name] = next++;
        return next;
    }

    /// <summary>The lowest of Wmap's units in a program with <paramref name="gameSamplers"/> pinned game samplers: 4, or right
    /// above them.</summary>
    public static int UnitBase(int gameSamplers) => Math.Max(LoadedUnit, gameSamplers);

    /// <summary>Wmap's units for <paramref name="programId"/>, above its <paramref name="gameSamplers"/> pinned ones. False when
    /// they would not fit in this GPU's texture units (the program must not be drawn with).</summary>
    public bool PlanUnits(int programId, int gameSamplers)
    {
        int shift = UnitBase(gameSamplers) - LoadedUnit;
        GL.GetInteger(GetPName.MaxCombinedTextureImageUnits, out int units);
        if (ChartUnit + shift >= Math.Max(units, 16)) return false;
        unitShifts[programId] = shift;
        return true;
    }
    readonly int[] keyBuffer = new int[FarTileWindow.Slots * FarTileWindow.Slots * 2];
    int vao, vbo, ibo, indexCount, program;
    readonly Dictionary<(int, string), int> locations = new();
    public FarMesh Mesh { get; private set; }
    public bool Ready => vao != 0;
    /// <summary>GPU bytes held by the mesh buffers.</summary>
    public long Bytes { get; private set; }

    public void Upload(FarMesh mesh)
    {
        DeleteBuffers();
        Mesh = mesh;
        vao = GL.GenVertexArray(); vbo = GL.GenBuffer(); ibo = GL.GenBuffer();
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int previousBuffer);
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * sizeof(float), mesh.Vertices, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, FarMesh.Floats, VertexAttribPointerType.Float, false, FarMesh.Floats * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ibo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(int), mesh.Indices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(previousVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousBuffer);
        indexCount = mesh.Indices.Length;
        Bytes = (long)mesh.Vertices.Length * sizeof(float) + (long)mesh.Indices.Length * sizeof(int);
    }

    /// <summary>The program was compiled again: forget its uniform locations, also when GL gives it the same id.</summary>
    public void ProgramReloaded() { program = 0; locations.Clear(); unitShifts.Clear(); }

    int At(string name)
    {
        if (!locations.TryGetValue((program, name), out int l)) locations[(program, name)] = l = GL.GetUniformLocation(program, name);
        return l;
    }
    public void Set(string name, float x) { int l = At(name); if (l >= 0) GL.Uniform1(l, x); }
    public void Set(string name, float x, float y) { int l = At(name); if (l >= 0) GL.Uniform2(l, x, y); }
    public void Set(string name, float x, float y, float z) { int l = At(name); if (l >= 0) GL.Uniform3(l, x, y, z); }
    public void Set(string name, float x, float y, float z, float w) { int l = At(name); if (l >= 0) GL.Uniform4(l, x, y, z, w); }
    public void Matrix(string name, float[] m) { int l = At(name); if (l >= 0) GL.UniformMatrix4(l, 1, false, m); }
    void Keys(string name, int[] keys) { int l = At(name); if (l >= 0) GL.Uniform2(l, keys.Length / 2, keys); }
    void Floats(string name, float[] values) { int l = At(name); if (l >= 0) GL.Uniform1(l, values.Length, values); }
    void Sampler(string name, int unit) { int l = At(name); if (l >= 0) GL.Uniform1(l, unit); }

    /// <summary>Where the mesh sits: the camera snapped to the coarsest cell, so every vertex stays on its own ring's
    /// world grid and never swims.</summary>
    public static (double X, double Z) Origin(FarMesh mesh, double cameraX, double cameraZ) =>
        (Math.Floor(cameraX / mesh.Snap + 0.5) * mesh.Snap, Math.Floor(cameraZ / mesh.Snap + 0.5) * mesh.Snap);

    /// <summary>The snapped origin in blocks east of its lap's west edge (the shader's wmapOriginWorld.x).</summary>
    public static double OriginRel(Topology t, double originX) => t.Wraps ? Topology.Mod(originX - t.StartX, t.Width) : originX - t.StartX;

    // The layers' uniform names, made once (0.5.0-pre.4: joined on every bind, four strings a draw).
    static readonly string[] BandNames = { "wmapBand", "wmapBandKeys", "wmapBandFade" }, ClimateNames = { "wmapClimate", "wmapClimateKeys", "wmapClimateFade" };

    void Layer(string[] names, FarTiles.Layer layer, bool on)
    {
        var w = layer?.Window;
        // 2: a seam tile under 48 blocks wide, so readiness reaches two tiles east and west (wmapReadyAt).
        bool narrow = w != null && w.Topology.Width - (w.LapTiles - 1) * w.Span < 48;
        Set(names[0], (float)(w?.Span ?? 1), (float)(w?.Step ?? 1), w?.LapTiles ?? 1, on && layer != null ? narrow ? 2 : 1 : 0);
        if (layer != null) Array.Copy(layer.SlotKeys, keyBuffer, keyBuffer.Length);
        else Array.Fill(keyBuffer, FarTileWindow.Empty);
        Keys(names[1], keyBuffer);
        Floats(names[2], layer?.SlotFade ?? NoFade);
    }
    static readonly float[] NoFade = new float[FarTileWindow.Slots * FarTileWindow.Slots];

    /// <summary>Sets Wmap's uniforms on <paramref name="programId"/> (current) and binds far terrain's textures, remembering
    /// what the units held. mode: 0 the smooth mesh, 1 terraces, 2 recorded structures, 3 the terraces' cell map (its own
    /// textures are not bound while it is drawn into). The program draws from <paramref name="from"/> to
    /// <paramref name="to"/> blocks from the camera. False when there is nothing to draw with.</summary>
    public bool Bind(int programId, FarFrame f, float mode, double from, double to)
    {
        if (!Ready || f.ChartTexture == 0) return false;
        program = programId;
        // A program without a plan (compiled outside the game's shader system, as in the tests) has the base at 4.
        unitShift = unitShifts.TryGetValue(programId, out int shift) ? shift : 0;
        var t = f.Topology; var mesh = Mesh;
        var (ox, oz) = Origin(mesh, f.CameraX, f.CameraZ);
        double u = t.Wraps ? Topology.Mod(ox - t.StartX, t.Width) / t.Width : (ox - t.StartX) / t.Width;
        var tiles = f.Tiles;
        var rec = f.Recorded;
        Matrix("projectionMatrix", f.Projection);
        Matrix("viewMatrix", f.View);
        Set("wmapOrigin", (float)(ox - f.CameraX), (float)(oz - f.CameraZ), (float)f.CameraY, (float)(f.SeaLevel - FarTerrain.WaterBelowSeaLevel));
        Set("wmapChartUV", (float)u, (float)(oz / t.Height), (float)(1 / t.Width), (float)(1 / t.Height));
        Set("wmapChartInfo", (float)f.BlocksPerTexel, (float)mesh.RingRadius(0), (float)mesh.Cell0, (float)f.HeightRange);
        Set("wmapRange", (float)from, (float)to, (float)f.Push, t.Wraps ? 1 : 0);
        Set("wmapPush", (float)Math.Max(1, f.PushFade), (float)Math.Min(f.Far, mesh.Radius), (float)f.Start, (float)f.TerraceEdge);
        Set("wmapWater", f.Water?[0] ?? 0, f.Water?[1] ?? 0, f.Water?[2] ?? 0, f.Water != null ? 1 : 0);
        Set("wmapHeightScale", (float)Math.Max(16, f.HeightScale));
        Set("wmapCurve", f.PlanetRadius > 0 ? (float)(1 / (2 * f.PlanetRadius)) : 0f, (float)Math.Max(f.Start, f.CurveFrom));
        Set("wmapMode", mode);
        Set("rgbaFogIn", f.FogColor[0], f.FogColor[1], f.FogColor[2], f.FogColor[3]);
        Set("fogDensityIn", f.FogDensity);
        Set("fogMinIn", f.FogMin);
        Set("horizonFog", f.HorizonFog);
        Set("sunPosition", f.SunPosition[0], f.SunPosition[1], f.SunPosition[2]);
        Set("sunColor", f.SunColor[0], f.SunColor[1], f.SunColor[2]);
        Set("dayLight", f.DayLight);
        Set("wmapAmbient", f.Ambient[0], f.Ambient[1], f.Ambient[2]);
        Set("wmapWorld", (float)t.Width, (float)t.Height, t.Wraps ? 1 : 0, 0);
        int l = At("wmapOriginWorld");
        if (l >= 0) GL.Uniform2(l, (float)OriginRel(t, ox), (float)oz);
        int lapLocation = At("wmapOriginLap");
        if (lapLocation >= 0) GL.Uniform1(lapLocation, tiles?.OriginLap ?? 0);
        Layer(BandNames, tiles?.Band, tiles?.Band?.SemanticTexture > 0);
        Layer(ClimateNames, tiles?.Climate, tiles?.Climate?.BiomeTexture > 0);
        Set("wmapBandRange", (float)f.BandFadeStart, (float)f.BandRadius, (float)f.Canopy, 0);
        Set("wmapTint", f.PlantTint != 0 && f.WaterTint != 0 ? 1 : 0, 0, 0, 0);
        Set("wmapRec", rec?.HeightTexture > 0 ? 1 : 0, 0, rec?.Lap ?? 0, rec?.FineWanted > 0 ? 1 : 0);
        Set("wmapRecCoarse", (float)(rec?.Coarse.Span ?? 1), (float)(rec?.Coarse.Step ?? 1), rec?.Coarse.LapTiles ?? 1, 0);
        Set("wmapRecFine", (float)(rec?.Fine.Span ?? 1), (float)(rec?.Fine.Step ?? 1), rec?.Fine.LapTiles ?? 1, 0);
        Set("wmapCells", 0, (float)f.CellSize, (float)f.CellMapX, (float)f.CellMapZ);
        for (int i = 0; i < Units.Length; i++) Sampler(SamplerNames[i], Units[i] + unitShift);
        Set("wmapLoadedInfo", (float)f.MaskX, (float)f.MaskZ, FarStart.MaskSide, f.LoadedMask != 0 ? 1 : 0);
        // Remember what the units held, to give them back after the draw.
        GL.GetInteger(GetPName.ActiveTexture, out previousUnit);
        for (int i = 0; i < Units.Length; i++) { GL.ActiveTexture(TextureUnit.Texture0 + Units[i] + unitShift); GL.GetInteger(GetPName.TextureBinding2D, out previousBindings[i]); }
        bool map = mode > 2.5f;
        Texture(ChartUnit, f.ChartTexture);
        Texture(BandUnit, tiles?.Band?.SemanticTexture ?? 0);
        Texture(BandBiomeUnit, tiles?.Band?.BiomeTexture ?? 0);
        Texture(ClimateBiomeUnit, tiles?.Climate?.BiomeTexture ?? 0);
        Texture(PlantTintUnit, f.PlantTint);
        Texture(WaterTintUnit, f.WaterTint);
        Texture(RecHeightUnit, rec?.HeightTexture ?? 0);
        Texture(RecColorUnit, rec?.ColorTexture ?? 0);
        Texture(RecSlotsUnit, rec?.SlotTexture ?? 0);
        Texture(CellTopUnit, map ? 0 : f.CellTop);
        Texture(CellColorUnit, map ? 0 : f.CellColor);
        Texture(LoadedUnit, f.LoadedMask);
        return true;
    }

    void Texture(int unit, int texture) { GL.ActiveTexture(TextureUnit.Texture0 + unit + unitShift); GL.BindTexture(TextureTarget.Texture2D, texture); }

    /// <summary>Gives the texture units back what <see cref="Bind"/> found.</summary>
    public void Unbind()
    {
        for (int i = 0; i < Units.Length; i++) Texture(Units[i], previousBindings[i]);
        GL.ActiveTexture((TextureUnit)previousUnit);
    }

    /// <summary>Draws the smooth mesh with <paramref name="programId"/>, which the caller has made current.</summary>
    public void Draw(int programId, FarFrame f)
    {
        double from = f.TerraceEdge > 0 ? Math.Max(f.DrawFrom, f.SmoothStart - FarFrame.JoinOverlap) : f.SmoothStart;
        if (!Bind(programId, f, 0, from, Math.Min(f.Far, Mesh?.Radius ?? 0))) return;
        bool cull = GL.IsEnabled(EnableCap.CullFace), blend = GL.IsEnabled(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        GL.BindVertexArray(vao);
        GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, IntPtr.Zero);
        GL.BindVertexArray(previousVao);
        if (cull) GL.Enable(EnableCap.CullFace);
        if (blend) GL.Enable(EnableCap.Blend);
        Unbind();
    }

    int loadedTexture;
    /// <summary>The loaded-columns mask (<see cref="FarStart.Scan"/>) into its texture; returns the texture. Keeps the GL state.</summary>
    public int UploadLoaded(byte[] mask)
    {
        using var unpack = new GlUnpack();
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (loadedTexture == 0)
        {
            loadedTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, loadedTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, FarStart.MaskSide, FarStart.MaskSide, 0, PixelFormat.Red, PixelType.UnsignedByte, mask);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        }
        else
        {
            GL.BindTexture(TextureTarget.Texture2D, loadedTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, FarStart.MaskSide, FarStart.MaskSide, PixelFormat.Red, PixelType.UnsignedByte, mask);
        }
        return loadedTexture;
    }

    void DeleteBuffers()
    {
        if (vbo != 0) GL.DeleteBuffer(vbo);
        if (ibo != 0) GL.DeleteBuffer(ibo);
        if (vao != 0) GL.DeleteVertexArray(vao);
        vao = vbo = ibo = 0; indexCount = 0; Bytes = 0;
    }

    public void Dispose()
    {
        DeleteBuffers();
        if (loadedTexture != 0) GL.DeleteTexture(loadedTexture);
        loadedTexture = 0;
    }
}
