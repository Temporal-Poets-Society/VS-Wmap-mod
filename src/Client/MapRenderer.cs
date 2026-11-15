using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Everything one frame of the map needs; all positions are GUI pixels unless noted.</summary>
public sealed class MapFrame
{
    public double X, Y, Width, Height;
    public MapCamera Camera;
    public Topology Topology;
    public int Style;
    public bool Development;
    public float Gui = 1;
    /// <summary>Blocks from the sea surface to the top of the world.</summary>
    public double HeightScale = 200;
    /// <summary>Blocks the chart's and tiles' heights span (<see cref="AtlasInfo.ChartHeightRange"/>).</summary>
    public double HeightRange = Semantic.DefaultHeightRange;
    public double MeridianStep, ParallelStep;
    /// <summary>Shade the night side from <see cref="MapRenderer.SetDay"/>'s daylight.</summary>
    public bool DayNight;
}

/// <summary>
/// Owns Wmap's OpenGL objects: the shader, the mipmapped world raster, the exploration mask and the
/// latitude/longitude lookup. The game's texture helpers are bypassed on purpose: they limit mipmap
/// levels to the player's setting and use nearest-mipmap filtering, which is wrong for a map.
/// All methods run on the render thread.
/// </summary>
public sealed class MapRenderer : IDisposable
{
    public const int UnitBase = 0, UnitMask = 1, UnitGeo = 2, UnitTile = 3, UnitWind = 4, UnitDay = 5, UnitClimate = 6, UnitReference = 7;
    int program, vao, vbo, blank;
    readonly Dictionary<string, int> uniforms = new();
    readonly float[] quad = new float[12];
    readonly float[] guiProjection = new float[16], guiModelView = new float[16];
    int baseTexture, baseWidth, baseHeight, baseRows;
    BaseRaster pendingBase;
    int maskTexture, maskWidth, maskHeight;
    int geoTexture;
    int windTexture;
    int dayTexture;
    int climateTexture;
    bool windLocal;
    double windX0, windZ0, windSize;
    bool disposed;
    bool restoreCulling;
    public string Error { get; private set; }
    public bool BaseReady => baseTexture != 0 && pendingBase == null;
    /// <summary>The world chart's GL texture once fully uploaded (shared with far terrain), else 0.</summary>
    public int BaseTexture => BaseReady ? baseTexture : 0;
    public float BaseUploadProgress => pendingBase == null ? 1 : (float)baseRows / Math.Max(1, baseHeight);
    /// <summary>Blocks per world-raster texel.</summary>
    public double BaseTexel { get; private set; }
    MapFrame frame;

    public static string ShaderSource(string name)
    {
        using var stream = typeof(MapRenderer).Assembly.GetManifestResourceStream("Wmap.Shaders." + name) ?? throw new FileNotFoundException("Embedded shader " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    bool Ensure()
    {
        if (program != 0 || Error != null) return program != 0;
        try
        {
            const string header = "#version 330 core\n";
            int vs = 0, fs = 0, p;
            try
            {
                vs = Compile(ShaderType.VertexShader, header + ShaderSource("map.vert"));
                fs = Compile(ShaderType.FragmentShader, header + ShaderSource("map.frag"));
                p = GL.CreateProgram();
                GL.AttachShader(p, vs); GL.AttachShader(p, fs);
                GL.BindAttribLocation(p, 0, "aPos");
                GL.LinkProgram(p);
                GL.DetachShader(p, vs); GL.DetachShader(p, fs);
            }
            // A shader that compiled is deleted also when the other one did not.
            finally { if (vs != 0) GL.DeleteShader(vs); if (fs != 0) GL.DeleteShader(fs); }
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) { string log = GL.GetProgramInfoLog(p); GL.DeleteProgram(p); throw new InvalidOperationException("Map shader link: " + log); }
            program = p;
            GL.GetProgram(p, GetProgramParameterName.ActiveUniforms, out int count);
            for (int i = 0; i < count; i++)
            {
                string name = GL.GetActiveUniform(p, i, out _, out _);
                uniforms[name] = GL.GetUniformLocation(p, name);
            }
            vao = GL.GenVertexArray(); vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            blank = GL.GenTexture();
            using var unpack = new GlUnpack();
            GL.BindTexture(TextureTarget.Texture2D, blank);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new byte[4]);
            Filter(false, false);
            return true;
        }
        catch (Exception e) { Error = e.Message; return false; }
    }

    static int Compile(ShaderType type, string source)
    {
        int s = GL.CreateShader(type);
        GL.ShaderSource(s, source);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) { string log = GL.GetShaderInfoLog(s); GL.DeleteShader(s); throw new InvalidOperationException(type + ": " + log); }
        return s;
    }

    static void Filter(bool mipmaps, bool wrapX)
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(wrapX ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mipmaps ? 1000 : 0);
    }

    // ------------------------------------------------------------------ world raster

    /// <summary>Starts uploading a decoded world raster. Rows go up over several frames to avoid a hitch.</summary>
    public void SetBase(BaseRaster raster, Topology t)
    {
        if (!Ensure()) return;
        bool wraps = t.Wraps;
        BaseTexel = t.Width / raster.Width;
        if (baseTexture != 0) GL.DeleteTexture(baseTexture);
        baseTexture = GL.GenTexture();
        baseWidth = raster.Width; baseHeight = raster.Height; baseRows = 0; pendingBase = raster;
        // Storage only (no pixels): with a pixel-unpack buffer bound, the null pointer would be an offset into it.
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, baseTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, baseWidth, baseHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        Filter(false, wraps);
        wrapBase = wraps;
    }
    bool wrapBase;

    /// <summary>Uploads up to about budgetBytes of the pending raster; returns true when it became ready.</summary>
    public bool PumpBase(int budgetBytes = 24 << 20)
    {
        var raster = pendingBase;
        if (raster == null || baseTexture == 0) return false;
        int rows = Math.Max(1, budgetBytes / (baseWidth * 4));
        rows = Math.Min(rows, baseHeight - baseRows);
        // Runs in a game tick, between the game's own GL work: tight rows, no unpack buffer, and its binding given back.
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, baseTexture);
        Upload(raster.Rgba, baseRows * baseWidth * 4, 0, baseRows, baseWidth, rows, PixelFormat.Rgba);
        baseRows += rows;
        if (baseRows < baseHeight) return false;
        // Raise the level range first: GenerateMipmap only fills levels up to TEXTURE_MAX_LEVEL,
        // and a mipmap filter over missing levels leaves the texture incomplete (it samples as zero).
        Filter(true, wrapBase);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        pendingBase = null;
        return true;
    }

    /// <summary>A survival client's chart changed in a rectangle [x0, x1) × [z0, z1) (blocks of land it has seen, WM-D63): rows
    /// already uploaded go up again; rows still pending go up with the rest. The mipmaps catch up within a second.</summary>
    public void UpdateBaseRegion(BaseRaster raster, int x0, int z0, int x1, int z1)
    {
        if (baseTexture == 0 || raster == null || raster.Width != baseWidth || raster.Height != baseHeight) return;
        int rows = pendingBase == raster ? baseRows : baseHeight;
        z1 = Math.Min(z1, rows);
        if (x1 <= x0 || z1 <= z0) return;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, baseTexture);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, baseWidth);
        Upload(raster.Rgba, (z0 * baseWidth + x0) * 4, x0, z0, x1 - x0, z1 - z0, PixelFormat.Rgba);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        baseMipsStale = pendingBase == null;
    }
    bool baseMipsStale;
    long baseMipsAt;

    /// <summary>Rebuilds the chart's mipmaps after <see cref="UpdateBaseRegion"/>, at most once a second.</summary>
    public void RefreshBaseMips()
    {
        long now = Environment.TickCount64;
        if (!baseMipsStale || baseTexture == 0 || pendingBase != null || now < baseMipsAt) return;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, baseTexture);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        baseMipsStale = false; baseMipsAt = now + MaskMipsMs;
    }

    static void Upload(byte[] data, int offset, int x, int y, int width, int height, PixelFormat format)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, width, height, format, PixelType.UnsignedByte, Marshal.UnsafeAddrOfPinnedArrayElement(data, offset)); }
        finally { handle.Free(); }
    }

    // ------------------------------------------------------------------ exploration mask

    public void UpdateMask(ExploredMask mask)
    {
        if (!Ensure() || mask == null) return;
        if (maskTexture == 0 || maskWidth != mask.Width || maskHeight != mask.Height)
        {
            if (maskTexture != 0) GL.DeleteTexture(maskTexture);
            maskTexture = GL.GenTexture(); maskWidth = mask.Width; maskHeight = mask.Height;
            using var created = new GlUnpack();
            GL.BindTexture(TextureTarget.Texture2D, maskTexture);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            var handle = GCHandle.Alloc(mask.Data, GCHandleType.Pinned);
            try { GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, maskWidth, maskHeight, 0, PixelFormat.Red, PixelType.UnsignedByte, handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            Filter(true, mask.Wraps);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            mask.TakeDirty(out _, out _, out _, out _);
            return;
        }
        long now = Environment.TickCount64;
        bool dirty = mask.TakeDirty(out int x0, out int z0, out int x1, out int z1);
        if (!dirty && !(maskMipsStale && now >= maskMipsAt)) return;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, maskTexture);
        if (dirty)
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, maskWidth);
            Upload(mask.Data, z0 * maskWidth + x0, x0, z0, x1 - x0, z1 - z0, PixelFormat.Red);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            maskMipsStale = true;
        }
        // The mipmaps span the whole mask (millions of texels): rebuilt at most once a second while it changes. Level 0
        // is current at once; zoomed-out views catch up within the second.
        if (maskMipsStale && now >= maskMipsAt)
        {
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            maskMipsStale = false; maskMipsAt = now + MaskMipsMs;
        }
    }
    bool maskMipsStale;
    long maskMipsAt;
    const int MaskMipsMs = 1000;

    // ------------------------------------------------------------------ latitude / longitude

    public void SetGeography(float[] lonLat, int width, int height, bool wraps)
    {
        if (!Ensure()) return;
        if (geoTexture != 0) GL.DeleteTexture(geoTexture);
        using var unpack = new GlUnpack();
        geoTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, geoTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, width, height, 0, PixelFormat.Rgba, PixelType.Float, lonLat);
        Filter(false, wraps);
    }

    // ------------------------------------------------------------------ winds

    /// <summary>RG32F wind vectors (east, south) over the whole world, as <see cref="WindGrid.Build"/> lays them out.</summary>
    public void SetWind(float[] eastSouth, int width, int height, bool wraps)
    {
        if (!Ensure()) return;
        if (windTexture == 0) windTexture = GL.GenTexture();
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, windTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg32f, width, height, 0, PixelFormat.Rg, PixelType.Float, eastSouth);
        Filter(false, wraps);
        windLocal = false;
    }

    /// <summary>
    /// RG32F wind vectors over one square of the world, as <see cref="WindGrid.BuildLocal"/> lays them out: the
    /// barometer's weather around the player. Arrows are drawn only within the square's inscribed circle.
    /// </summary>
    public void SetLocalWind(float[] eastSouth, int size, double x0, double z0, double extent)
    {
        SetWind(eastSouth, size, size, false);
        if (windTexture == 0) return;
        windLocal = true; windX0 = x0; windZ0 = z0; windSize = extent;
    }

    public bool WindReady => windTexture != 0;

    // ------------------------------------------------------------------ day and night

    /// <summary>The game's daylight over the whole world, one byte per cell, as <see cref="DayGrid.BuildRows"/> lays it out.</summary>
    public void SetDay(byte[] daylight, int width, int height, bool wraps)
    {
        if (!Ensure()) return;
        if (dayTexture == 0) dayTexture = GL.GenTexture();
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, dayTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, daylight);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        Filter(false, wraps);
    }

    public bool DayReady => dayTexture != 0;

    // ------------------------------------------------------------------ climate

    /// <summary>The climate raster (<see cref="ClimateRaster"/>): Terrain colours land by it instead of by height alone.</summary>
    public void SetClimate(BaseRaster climate, bool wraps)
    {
        if (!Ensure()) return;
        if (climateTexture == 0) climateTexture = GL.GenTexture();
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, climateTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, climate.Width, climate.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, climate.Rgba);
        Filter(false, wraps);
    }

    public bool ClimateReady => climateTexture != 0;

    /// <summary>A survival client's climate raster changed in a rectangle (WM-D63).</summary>
    public void UpdateClimateRegion(BaseRaster climate, int x0, int z0, int x1, int z1)
    {
        if (climateTexture == 0 || climate == null || x1 <= x0 || z1 <= z0) return;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, climateTexture);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, climate.Width);
        Upload(climate.Rgba, (z0 * climate.Width + x0) * 4, x0, z0, x1 - x0, z1 - z0, PixelFormat.Rgba);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
    }

    // ------------------------------------------------------------------ known texels (survival, WM-D63)

    int knownTexture, knownWidth, knownHeight;
    /// <summary>Which chart texels a survival client has been given (R: 255 known), at the chart's size, so far terrain draws
    /// only land the player has seen; 0 with the full map.</summary>
    public int KnownTexture => knownTexture;

    public void SetKnown(byte[] known, int width, int height, bool wraps)
    {
        if (!Ensure()) return;
        ClearKnown();
        knownTexture = GL.GenTexture(); knownWidth = width; knownHeight = height;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, knownTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        var handle = GCHandle.Alloc(known, GCHandleType.Pinned);
        try { GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, width, height, 0, PixelFormat.Red, PixelType.UnsignedByte, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        Filter(false, wraps);
    }

    public void UpdateKnownRegion(byte[] known, int x0, int z0, int x1, int z1)
    {
        if (knownTexture == 0 || x1 <= x0 || z1 <= z0) return;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, knownTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, knownWidth);
        Upload(known, z0 * knownWidth + x0, x0, z0, x1 - x0, z1 - z0, PixelFormat.Red);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    public void ClearKnown()
    {
        if (knownTexture != 0) GL.DeleteTexture(knownTexture);
        knownTexture = 0;
    }

    // ------------------------------------------------------------------ drawing

    void Set(string name, float x, float y, float z, float w) { if (uniforms.TryGetValue(name, out int l)) GL.Uniform4(l, x, y, z, w); }
    void Set(string name, float x, float y) { if (uniforms.TryGetValue(name, out int l)) GL.Uniform2(l, x, y); }
    void Set(string name, float x) { if (uniforms.TryGetValue(name, out int l)) GL.Uniform1(l, x); }
    void Set(string name, int x) { if (uniforms.TryGetValue(name, out int l)) GL.Uniform1(l, x); }
    void Bind(int unit, string sampler, int texture)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture != 0 ? texture : blank);
        Set(sampler, unit);
    }

    /// <summary>Binds the program and shared state. Callers stop the game's active shader first.</summary>
    public bool Begin(IRenderAPI render, MapFrame f, bool explored)
    {
        // ClientMain returns the SAME temporary array from both getters. Consume each
        // matrix before reading the next; evaluating both as arguments loses projection.
        Array.Copy(render.CurrentProjectionMatrix, guiProjection, 16);
        Array.Copy(render.CurrentModelviewMatrix, guiModelView, 16);
        return Begin(guiProjection, guiModelView, f, explored);
    }

    /// <summary>Binds already-copied matrices; raw callers must supply independent arrays.</summary>
    public bool Begin(float[] projection, float[] modelView, MapFrame f, bool explored)
    {
        if (disposed || !Ensure()) return false;
        frame = f;
        // The top-left GUI projection reverses our quad winding. Draw both faces,
        // even when an earlier renderer left world-style back-face culling enabled.
        restoreCulling = GL.IsEnabled(EnableCap.CullFace);
        GL.Disable(EnableCap.CullFace);
        GL.UseProgram(program);
        if (uniforms.TryGetValue("uProj", out int lp)) GL.UniformMatrix4(lp, 1, false, projection);
        if (uniforms.TryGetValue("uView", out int lv)) GL.UniformMatrix4(lv, 1, false, modelView);
        var t = f.Topology; var c = f.Camera;
        Set("uViewport", (float)f.X, (float)f.Y, (float)f.Width, (float)f.Height);
        Set("uCamera", (float)c.Scale, f.Gui, 0, 0);
        // Double precision here keeps shader coordinates small after many laps of the ring.
        // A bounded map must not wrap: its camera may rest on the east edge, u = 1.
        double u = (t.Wraps ? Topology.Mod(c.X - t.StartX, t.Width) : c.X - t.StartX) / t.Width;
        Set("uCamUV", (float)u, (float)(c.Z / t.Height));
        double period = 4096 / Math.Pow(2, Math.Floor(Math.Log2(c.Scale)));
        Set("uAnchor", (float)Topology.Mod(c.X, period), (float)Topology.Mod(c.Z, period));
        Set("uWorld", (float)t.Width, (float)t.Height, t.Wraps ? 1 : 0, f.Development ? 1 : 0);
        Set("uStyle", f.Style);
        Set("uHeightScale", (float)Math.Max(16, f.HeightScale));
        Set("uHeightRange", (float)f.HeightRange);
        bool ready = BaseReady;
        Bind(UnitBase, "uBase", ready ? baseTexture : 0);
        Set("uBaseInfo", ready ? 1 : 0, (float)(t.Width / Math.Max(1, baseWidth)), baseWidth, baseHeight);
        Bind(UnitMask, "uMask", maskTexture);
        Set("uMaskInfo", explored && maskTexture != 0 ? 1 : 0, knownTexture != 0 ? RevealGate.CoastReach : 0, 0, 0);
        Bind(UnitGeo, "uGeo", geoTexture);
        Set("uGeoInfo", geoTexture != 0 ? 1 : 0, (float)f.MeridianStep, (float)f.ParallelStep, 0);
        Bind(UnitTile, "uTile", 0);
        Bind(UnitWind, "uWind", windTexture);
        Bind(UnitDay, "uDay", dayTexture);
        Set("uDayInfo", f.DayNight && dayTexture != 0 ? 1 : 0, 0, 0, 0);
        Bind(UnitClimate, "uClimate", climateTexture);
        Set("uClimateInfo", climateTexture != 0 ? 1 : 0, 0, 0, 0);
        Bind(UnitReference, "uRef", 0);
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.Enable(EnableCap.Blend);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        return true;
    }

    void Quad(double x, double y, double w, double h)
    {
        float x0 = (float)x, y0 = (float)y, x1 = (float)(x + w), y1 = (float)(y + h);
        quad[0] = x0; quad[1] = y0; quad[2] = x1; quad[3] = y0; quad[4] = x1; quad[5] = y1;
        quad[6] = x0; quad[7] = y0; quad[8] = x1; quad[9] = y1; quad[10] = x0; quad[11] = y1;
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, quad.Length * sizeof(float), quad);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    public void DrawWorld(float depth)
    {
        Set("uMode", 0); Set("uDepth", depth);
        Quad(frame.X, frame.Y, frame.Width, frame.Height);
    }

    /// <summary>A coarser recording drawn under a prediction tile (<see cref="ViewTiles.Reference"/>): its atlas page and
    /// slot, its world origin on the camera's lap and its blocks per texel.</summary>
    public readonly record struct TileReference(int Page, int SlotX, int SlotY, double X, double Z, double Step);

    /// <summary>Draws one detail tile from an atlas page. x/z: the tile's world origin on the camera's lap.</summary>
    /// <param name="recorded">A recorded tile: drawn in its recorded cells, never checked against a reference. Every tile,
    /// recorded or predicted, draws rivers from its own water only, not from the world raster's river courses (WM-D46).</param>
    /// <param name="reference">For a prediction tile over a coarser recording: the prediction is drawn only in recorded
    /// cells that confirm it, and in unrecorded ones (WM-D41).</param>
    public void DrawTile(int page, int pageSize, int slotX, int slotY, double x, double z, double width, double height, double step, float alpha, float depth,
        bool recorded = false, TileReference? reference = null)
    {
        var c = frame.Camera;
        Set("uMode", 1); Set("uDepth", depth);
        GL.ActiveTexture(TextureUnit.Texture0 + UnitTile);
        GL.BindTexture(TextureTarget.Texture2D, page);
        Set("uTileInfo", (float)(x - c.X), (float)(z - c.Z), (float)step, alpha);
        Set("uTileSlot", slotX, slotY, pageSize, recorded ? 1 : 0);
        if (reference is { } r && !recorded)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + UnitReference);
            GL.BindTexture(TextureTarget.Texture2D, r.Page);
            Set("uRefInfo", (float)(r.X - c.X), (float)(r.Z - c.Z), (float)r.Step, 1);
            Set("uRefSlot", r.SlotX, r.SlotY, pageSize, 0);
        }
        else Set("uRefInfo", 0, 0, 1, 0);
        double sx = frame.X + frame.Width / 2 + (x - c.X) * c.Scale, sy = frame.Y + frame.Height / 2 + (z - c.Z) * c.Scale;
        // Clip to the map rectangle; the scissor also applies, this only saves fragments.
        double x0 = Math.Max(frame.X, sx), y0 = Math.Max(frame.Y, sy);
        double x1 = Math.Min(frame.X + frame.Width, sx + width * c.Scale), y1 = Math.Min(frame.Y + frame.Height, sy + height * c.Scale);
        if (x1 > x0 && y1 > y0) Quad(x0, y0, x1 - x0, y1 - y0);
    }

    public void DrawMarker(double x, double y, double heading, double radius, float depth, float[] color = null)
    {
        Set("uMode", 2); Set("uDepth", depth);
        Set("uMarker", (float)x, (float)y, (float)heading, (float)radius);
        Set("uMarkerColor", color == null ? -1 : color[0], color == null ? 0 : color[1], color == null ? 0 : color[2], color == null ? 1 : color[3]);
        Quad(x - radius * 1.5, y - radius * 1.5, radius * 3, radius * 3);
    }

    /// <summary>A player's arrow along where they look (<see cref="Heading.OfPlayer"/>).</summary>
    public void DrawPlayer(double x, double y, double yaw, double radius, float depth, float[] color = null) =>
        DrawMarker(x, y, Heading.OfPlayer(yaw), radius, depth, color);

    public void DrawCompass(double x, double y, double radius, float depth)
    {
        Set("uMode", 3); Set("uDepth", depth);
        Set("uMarker", (float)x, (float)y, 0, (float)radius);
        Quad(x - radius - 2, y - radius - 2, radius * 2 + 4, radius * 2 + 4);
    }

    /// <summary>Wind arrows over the whole map, about spacing GUI pixels apart, anchored to the world.</summary>
    /// <param name="belts">Dash the boundaries between wind belts (prevailing winds only; weather would scribble them).</param>
    public void DrawWinds(float depth, bool belts, double spacing = 64)
    {
        if (windTexture == 0) return;
        Set("uMode", 4); Set("uDepth", depth);
        Set("uWindInfo", 1, (float)(spacing * frame.Gui), belts && !windLocal ? 1 : 0, windLocal ? 1 : 0);
        if (windLocal)
        {
            var c = frame.Camera;
            double x = frame.Topology.Nearest(windX0 + windSize / 2, c.X) - windSize / 2;
            Set("uWindRect", (float)(x - c.X), (float)(windZ0 - c.Z), (float)windSize, (float)windSize);
        }
        // The arrows lie over the whole map: other map layers drawn later (ships) go over them, not under.
        bool writes = GL.GetBoolean(GetPName.DepthWritemask);
        GL.DepthMask(false);
        try { Quad(frame.X, frame.Y, frame.Width, frame.Height); }
        finally { GL.DepthMask(writes); }
    }

    public void End()
    {
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        for (int unit = UnitReference; unit >= 0; unit--) { GL.ActiveTexture(TextureUnit.Texture0 + unit); GL.BindTexture(TextureTarget.Texture2D, 0); }
        GL.UseProgram(0);
        if (restoreCulling) GL.Enable(EnableCap.CullFace);
        frame = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (int t in new[] { baseTexture, maskTexture, geoTexture, windTexture, dayTexture, climateTexture, knownTexture, blank }) if (t != 0) GL.DeleteTexture(t);
        if (vbo != 0) GL.DeleteBuffer(vbo);
        if (vao != 0) GL.DeleteVertexArray(vao);
        if (program != 0) GL.DeleteProgram(program);
        pendingBase = null;
    }
}
