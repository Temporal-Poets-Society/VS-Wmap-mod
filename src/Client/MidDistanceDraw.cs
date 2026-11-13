using System;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// The mid-distance view's GPU side (WM-P06 prototype; docs/mid-distance-view.md): the terraces' cell map (every cell's top,
/// worked out on the GPU each frame by farcellmap.fsh), the terraces themselves (instanced <see cref="TerracePatch"/>es, only
/// those in the ring between far terrain's start and the terraces' edge and in view), the recorded structures past them
/// (one instanced box per two-block recording) and the distant lights (one instanced glow each). Every draw keeps the GL
/// state it found. Render thread only.
/// </summary>
public sealed class MidDistanceDraw : IDisposable
{
    /// <summary>Terrace cells are two blocks, on the two-block recordings' grid.</summary>
    public const double CellSize = 2;
    /// <summary>Cells per side of the cell map: ±832 blocks around the camera snapped to a patch, enough for terraces to 768.</summary>
    public const int MapCells = 832;
    const double PatchBlocks = TerracePatch.Cells * CellSize;
    const int PatchesPerSide = MapCells / TerracePatch.Cells;

    int mapFbo, mapTop, mapColor, emptyVao;
    int patchVao, patchVbo, patchIbo, patchInstances, patchIndexCount;
    int boxVao, boxVbo, boxIbo, boxInstances;
    int lightVao, lightVbo, lightInstances;
    long boxVersion = -1, lightVersion = -1;
    int boxCount, lightCount;
    readonly float[] patches = new float[PatchesPerSide * PatchesPerSide * 4];
    readonly float[] planes = new float[24];
    /// <summary>Patches, boxes and lights in the last frame's draws.</summary>
    public int Patches, Boxes, Lights;
    public bool Ready => mapFbo != 0;
    /// <summary>GPU bytes of the cell map and buffers.</summary>
    public long Bytes { get; private set; }

    public void Create()
    {
        if (Ready) return;
        // No pixel-unpack buffer while the cell map is allocated (0.5.0-pre.3): with one bound by another renderer, the
        // texture would be read from it, fail, and switch the mid-distance view off. GlUnpack also keeps the binding.
        using var unpack = new GlUnpack();
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int read);
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int previousBuffer);
        int Target(PixelInternalFormat format, PixelFormat layout, PixelType type)
        {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, format, MapCells, MapCells, 0, layout, type, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return id;
        }
        mapTop = Target(PixelInternalFormat.R32f, PixelFormat.Red, PixelType.Float);
        mapColor = Target(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        mapFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, mapFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, mapTop, 0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, mapColor, 0);
        GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        if (status != FramebufferErrorCode.FramebufferComplete) { Dispose(); throw new InvalidOperationException("Terrace cell map framebuffer: " + status); }
        emptyVao = GL.GenVertexArray();

        // Terraces: one patch template, drawn once per patch in view.
        var patch = TerracePatch.Build();
        patchVao = GL.GenVertexArray(); patchVbo = GL.GenBuffer(); patchIbo = GL.GenBuffer(); patchInstances = GL.GenBuffer();
        GL.BindVertexArray(patchVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, patchVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, patch.Vertices.Length * sizeof(float), patch.Vertices, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, TerracePatch.Floats * sizeof(float), 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, TerracePatch.Floats * sizeof(float), 4 * sizeof(float));
        GL.EnableVertexAttribArray(0); GL.EnableVertexAttribArray(1);
        GL.BindBuffer(BufferTarget.ArrayBuffer, patchInstances);
        GL.BufferData(BufferTarget.ArrayBuffer, patches.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamDraw);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(2); GL.VertexAttribDivisor(2, 1);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, patchIbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, patch.Indices.Length * sizeof(int), patch.Indices, BufferUsageHint.StaticDraw);
        patchIndexCount = patch.Indices.Length;

        // Structures: a box of five faces (no bottom), one instance per two-block recording.
        var box = new float[5 * 4 * 4];
        int[][] faces = { new[] { 0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1 }, new[] { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1 }, new[] { 1, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1 },
            new[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, new[] { 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1 } };
        for (int f = 0; f < 5; f++)
        for (int c = 0; c < 4; c++)
        {
            int o = (f * 4 + c) * 4;
            box[o] = faces[f][c * 3]; box[o + 1] = faces[f][c * 3 + 1]; box[o + 2] = faces[f][c * 3 + 2]; box[o + 3] = f;
        }
        var boxIndices = new int[30];
        for (int f = 0; f < 5; f++) { int b = f * 4, k = f * 6; boxIndices[k] = b; boxIndices[k + 1] = b + 1; boxIndices[k + 2] = b + 2; boxIndices[k + 3] = b + 1; boxIndices[k + 4] = b + 3; boxIndices[k + 5] = b + 2; }
        boxVao = GL.GenVertexArray(); boxVbo = GL.GenBuffer(); boxIbo = GL.GenBuffer(); boxInstances = GL.GenBuffer();
        GL.BindVertexArray(boxVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, boxVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, box.Length * sizeof(float), box, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, boxInstances);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, RecordedTiles.BoxFloats * sizeof(float), 0);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedByte, true, RecordedTiles.BoxFloats * sizeof(float), 4 * sizeof(float));
        GL.EnableVertexAttribArray(3); GL.VertexAttribDivisor(3, 1);
        GL.EnableVertexAttribArray(4); GL.VertexAttribDivisor(4, 1);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, boxIbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, boxIndices.Length * sizeof(int), boxIndices, BufferUsageHint.StaticDraw);

        // Lights: a quad, one instance per light.
        lightVao = GL.GenVertexArray(); lightVbo = GL.GenBuffer(); lightInstances = GL.GenBuffer();
        GL.BindVertexArray(lightVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, lightVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 8 * sizeof(float), new float[] { -1, -1, 1, -1, -1, 1, 1, 1 }, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, lightInstances);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, RecordedTiles.LightFloats * sizeof(float), 0);
        GL.EnableVertexAttribArray(1); GL.VertexAttribDivisor(1, 1);

        GL.BindVertexArray(previousVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousBuffer);
        Bytes = (long)MapCells * MapCells * 8 + (patch.Vertices.Length + patch.Indices.Length) * 4L + patches.Length * 4L;
    }

    /// <summary>Works out the cell map's corner for this frame: the camera snapped to a patch, in blocks from the snapped
    /// origin, on the recordings' grid (blocks east of the ring's start).</summary>
    public void PlaceCellMap(FarFrame f, FarMesh mesh)
    {
        var t = f.Topology;
        var (ox, oz) = FarTerrainDraw.Origin(mesh, f.CameraX, f.CameraZ);
        double originRel = FarTerrainDraw.OriginRel(t, ox), cameraRel = originRel + (f.CameraX - ox);
        double half = MapCells / 2 * CellSize;
        f.CellMapX = Math.Floor(cameraRel / PatchBlocks) * PatchBlocks - half - originRel;
        f.CellMapZ = Math.Floor(f.CameraZ / PatchBlocks) * PatchBlocks - half - oz;
        f.CellSize = CellSize; f.CellTop = mapTop; f.CellColor = mapColor;
    }

    long fadingFrame;

    /// <summary>What the cell map is drawn from: its place, far terrain's origin and start, the terraces' edge and the tiles in
    /// their slots. The caller draws it again when this changes; while a tile is fading in, it changes every frame.</summary>
    public long CellMapSignature(FarFrame f, FarMesh mesh)
    {
        static bool Fading(float[] fades) { foreach (float v in fades) if (v > 0 && v < 1) return true; return false; }
        var tiles = f.Tiles; var rec = f.Recorded;
        if ((tiles?.Band != null && Fading(tiles.Band.SlotFade)) || (tiles?.Climate != null && Fading(tiles.Climate.SlotFade))
            || (rec != null && (Fading(rec.CoarseSlots.Fade) || Fading(rec.FineSlots.Fade))))
            return ++fadingFrame;
        var (ox, oz) = FarTerrainDraw.Origin(mesh, f.CameraX, f.CameraZ);
        var hash = new HashCode();
        hash.Add(f.CellMapX); hash.Add(f.CellMapZ); hash.Add(ox); hash.Add(oz); hash.Add(f.Start); hash.Add(f.DrawFrom); hash.Add(f.TerraceRadius);
        hash.Add(tiles?.Uploads ?? -1); hash.Add(tiles?.OriginLap ?? 0); hash.Add(rec?.Uploads ?? -1); hash.Add(rec?.Lap ?? 0); hash.Add(rec != null);
        return (long)(hash.ToHashCode() & 0x7FFFFFFF) << 20;
    }

    /// <summary>Draws every cell's top and colour into the cell map with <paramref name="program"/> (farcellmap, current).
    /// The caller draws it again only when the map moved or what it is drawn from changed. False when it could not.</summary>
    public bool DrawCellMap(int program, FarTerrainDraw common, FarFrame f)
    {
        if (!Ready || !common.Bind(program, f, 3, f.DrawFrom, f.TerraceRadius)) return false;
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int read);
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        bool depth = GL.IsEnabled(EnableCap.DepthTest), blend = GL.IsEnabled(EnableCap.Blend), scissor = GL.IsEnabled(EnableCap.ScissorTest), cull = GL.IsEnabled(EnableCap.CullFace);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, mapFbo);
        GL.Viewport(0, 0, MapCells, MapCells);
        GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(emptyVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(previousVao);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
        GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        if (depth) GL.Enable(EnableCap.DepthTest);
        if (blend) GL.Enable(EnableCap.Blend);
        if (scissor) GL.Enable(EnableCap.ScissorTest);
        if (cull) GL.Enable(EnableCap.CullFace);
        common.Unbind();
        return true;
    }

    /// <summary>Draws the terraces with <paramref name="program"/> (farcells, current): the patches of the cell map that reach
    /// into the ring from far terrain's start to the terraces' edge and into the view.</summary>
    public void DrawTerraces(int program, FarTerrainDraw common, FarFrame f, double worldTop)
    {
        Patches = 0;
        if (!Ready || f.TerraceRadius <= f.Start) return;
        var (ox, oz) = FarTerrainDraw.Origin(common.Mesh, f.CameraX, f.CameraZ);
        // The map's corner relative to the camera; heights from the world's bottom to its top, relative to the eye.
        double cx = f.CellMapX + ox - f.CameraX, cz = f.CellMapZ + oz - f.CameraZ, y0 = -f.CameraY - 1, y1 = worldTop - f.CameraY + 16;
        Frustum(f.Projection, f.View);
        int n = 0;
        for (int pz = 0; pz < PatchesPerSide; pz++)
        for (int px = 0; px < PatchesPerSide; px++)
        {
            double x0 = cx + px * PatchBlocks, z0 = cz + pz * PatchBlocks, x1 = x0 + PatchBlocks, z1 = z0 + PatchBlocks;
            double nx = Math.Max(0, Math.Max(x0, -x1)), nz = Math.Max(0, Math.Max(z0, -z1));
            double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            if (Math.Sqrt(fx * fx + fz * fz) < f.DrawFrom - 2 * CellSize || Math.Sqrt(nx * nx + nz * nz) > f.TerraceRadius + FarFrame.JoinOverlap + 2 * CellSize) continue;
            if (!InView(x0, y0, z0, x1, y1, z1)) continue;
            patches[n * 4] = px * TerracePatch.Cells; patches[n * 4 + 1] = pz * TerracePatch.Cells; patches[n * 4 + 2] = 0; patches[n * 4 + 3] = 0;
            n++;
        }
        // Past the terraces' edge into the smooth mesh, sinking under it there (FarFrame.JoinOverlap).
        if (n == 0 || !common.Bind(program, f, 1, f.DrawFrom, f.TerraceRadius + FarFrame.JoinOverlap)) return;
        Patches = n;
        GL.GetInteger(GetPName.ArrayBufferBinding, out int previousBuffer);
        GL.BindBuffer(BufferTarget.ArrayBuffer, patchInstances);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, n * 4 * sizeof(float), patches);
        GL.BindBuffer(BufferTarget.ArrayBuffer, previousBuffer);
        Instanced(patchVao, patchIndexCount, n);
        common.Unbind();
    }

    /// <summary>Draws the recorded structures with <paramref name="program"/> (farcells, current), from
    /// <paramref name="from"/> to the near band's edge.</summary>
    public void DrawStructures(int program, FarTerrainDraw common, FarFrame f, double from)
    {
        Boxes = 0;
        var rec = f.Recorded;
        if (!Ready || rec == null) return;
        if (rec.BoxVersion != boxVersion)
        {
            boxVersion = rec.BoxVersion; boxCount = rec.BoxCount;
            GL.GetInteger(GetPName.ArrayBufferBinding, out int previousBuffer);
            GL.BindBuffer(BufferTarget.ArrayBuffer, boxInstances);
            GL.BufferData(BufferTarget.ArrayBuffer, Math.Max(1, boxCount * RecordedTiles.BoxFloats) * sizeof(float), boxCount > 0 ? rec.BoxData : One, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, previousBuffer);
        }
        if (boxCount == 0 || !common.Bind(program, f, 2, from, f.BandRadius)) return;
        Boxes = boxCount;
        Instanced(boxVao, 30, boxCount);
        common.Unbind();
    }

    static readonly float[] One = new float[1];

    static void Instanced(int vao, int indices, int instances)
    {
        bool cull = GL.IsEnabled(EnableCap.CullFace), blend = GL.IsEnabled(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        GL.BindVertexArray(vao);
        GL.DrawElementsInstanced(PrimitiveType.Triangles, indices, DrawElementsType.UnsignedInt, IntPtr.Zero, instances);
        GL.BindVertexArray(previousVao);
        if (cull) GL.Enable(EnableCap.CullFace);
        if (blend) GL.Enable(EnableCap.Blend);
    }

    /// <summary>Draws the distant lights with <paramref name="program"/> (farlights, current): added to what is drawn,
    /// depth-tested against it and not written to depth.</summary>
    /// <param name="from">Lights start this far from the camera (far terrain's start), out to <paramref name="to"/>.</param>
    public void DrawLights(int program, FarFrame f, RecordedTiles rec, double from, double to, int width, int height)
    {
        Lights = 0;
        if (!Ready || rec == null) return;
        if (rec.LightVersion != lightVersion)
        {
            lightVersion = rec.LightVersion; lightCount = rec.LightCount;
            GL.GetInteger(GetPName.ArrayBufferBinding, out int previousBuffer);
            GL.BindBuffer(BufferTarget.ArrayBuffer, lightInstances);
            GL.BufferData(BufferTarget.ArrayBuffer, Math.Max(1, lightCount * RecordedTiles.LightFloats) * sizeof(float), lightCount > 0 ? rec.LightData : One, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ArrayBuffer, previousBuffer);
        }
        if (lightCount == 0) return;
        var t = f.Topology;
        Matrix(program, "projectionMatrix", f.Projection);
        Matrix(program, "viewMatrix", f.View);
        double cameraRel = t.Wraps ? Topology.Mod(f.CameraX - t.StartX, t.Width) : f.CameraX - t.StartX;
        Set4(program, "wmapLightCamera", cameraRel, f.CameraZ, f.CameraY, t.Wraps ? t.Width : 0);
        Set4(program, "wmapLightRange", from, to, f.PlanetRadius > 0 ? 1 / (2 * f.PlanetRadius) : 0, Math.Max(f.Start, f.CurveFrom));
        Set4(program, "wmapLightView", width, height, f.DayLight, f.CameraY - (f.SeaLevel - FarTerrain.WaterBelowSeaLevel));
        Set1(program, "fogMinIn", f.FogMin); Set1(program, "fogDensityIn", f.FogDensity);
        bool blend = GL.IsEnabled(EnableCap.Blend), cull = GL.IsEnabled(EnableCap.CullFace), depth = GL.IsEnabled(EnableCap.DepthTest);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
        GL.GetInteger(GetPName.BlendSrcRgb, out int srcRgb); GL.GetInteger(GetPName.BlendDstRgb, out int dstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int srcAlpha); GL.GetInteger(GetPName.BlendDstAlpha, out int dstAlpha);
        GL.GetInteger(GetPName.BlendEquationRgb, out int equationRgb); GL.GetInteger(GetPName.BlendEquationAlpha, out int equationAlpha);
        GL.GetInteger(GetPName.VertexArrayBinding, out int previousVao);
        GL.Enable(EnableCap.Blend); GL.BlendEquation(BlendEquationMode.FuncAdd); GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        GL.Disable(EnableCap.CullFace); GL.Enable(EnableCap.DepthTest); GL.DepthMask(false);
        GL.BindVertexArray(lightVao);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, lightCount);
        GL.BindVertexArray(previousVao);
        GL.DepthMask(depthMask);
        GL.BlendEquationSeparate((BlendEquationMode)equationRgb, (BlendEquationMode)equationAlpha);
        GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb, (BlendingFactorDest)dstRgb, (BlendingFactorSrc)srcAlpha, (BlendingFactorDest)dstAlpha);
        if (!blend) GL.Disable(EnableCap.Blend);
        if (cull) GL.Enable(EnableCap.CullFace);
        if (!depth) GL.Disable(EnableCap.DepthTest);
        Lights = lightCount;
    }

    // Uniforms of the lights' program; methods, not local functions, so a frame allocates no closures.
    static void Set4(int program, string name, double x, double y, double z, double w) { int l = GL.GetUniformLocation(program, name); if (l >= 0) GL.Uniform4(l, (float)x, (float)y, (float)z, (float)w); }
    static void Matrix(int program, string name, float[] m) { int l = GL.GetUniformLocation(program, name); if (l >= 0) GL.UniformMatrix4(l, 1, false, m); }
    static void Set1(int program, string name, float x) { int l = GL.GetUniformLocation(program, name); if (l >= 0) GL.Uniform1(l, x); }

    readonly float[] viewProjection = new float[16];
    static readonly (int Row, int Sign)[] Planes = { (0, 1), (0, -1), (1, 1), (1, -1), (2, 1), (2, -1) };

    /// <summary>The view frustum's six planes from the projection and view matrices (GL column order, camera at the origin).</summary>
    void Frustum(float[] p, float[] v)
    {
        var m = viewProjection;
        for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++)
        {
            float s = 0;
            for (int k = 0; k < 4; k++) s += p[k * 4 + r] * v[c * 4 + k];
            m[c * 4 + r] = s;
        }
        float Row(int r, int c) => m[c * 4 + r];
        int i = 0;
        foreach (var (row, sign) in Planes)
            for (int c = 0; c < 4; c++) planes[i++] = Row(3, c) + sign * Row(row, c);
    }

    /// <summary>Whether a box (relative to the camera) reaches into the view: no plane has it wholly outside.</summary>
    bool InView(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        for (int p = 0; p < 6; p++)
        {
            float a = planes[p * 4], b = planes[p * 4 + 1], c = planes[p * 4 + 2], d = planes[p * 4 + 3];
            if (a * (a >= 0 ? x1 : x0) + b * (b >= 0 ? y1 : y0) + c * (c >= 0 ? z1 : z0) + d < 0) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (mapFbo != 0) GL.DeleteFramebuffer(mapFbo);
        foreach (int t in new[] { mapTop, mapColor }) if (t != 0) GL.DeleteTexture(t);
        foreach (int a in new[] { emptyVao, patchVao, boxVao, lightVao }) if (a != 0) GL.DeleteVertexArray(a);
        foreach (int b in new[] { patchVbo, patchIbo, patchInstances, boxVbo, boxIbo, boxInstances, lightVbo, lightInstances }) if (b != 0) GL.DeleteBuffer(b);
        mapFbo = mapTop = mapColor = emptyVao = patchVao = boxVao = lightVao = 0;
        patchVbo = patchIbo = patchInstances = boxVbo = boxIbo = boxInstances = lightVbo = lightInstances = 0;
        Bytes = 0;
    }
}
