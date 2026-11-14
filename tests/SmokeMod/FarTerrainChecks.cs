using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Wmap;
using Wmap.Client;
using Wmap.Core;
using Wmap.Integrations;
using SkiaSharp;

/// <summary>
/// Compiles the production far-terrain shader the way the game does (its own shader includes and #define
/// prefix, for every shadow/SSAO/godray/dynamic-light setting), then renders it from the real world chart
/// in the current GL context: a view over coast and mountains, a view straight down (inside view distance,
/// nothing drawn), and the same view one ring circumference east (identical: the seam wraps).
/// With Earth (0.4.0): the near band and climate tiles, fed through the real <see cref="FarTiles"/> from Earth's
/// samplers. Transform feedback captures the displaced vertices exactly: stitched edges lie on their coarse edge,
/// the band's heights equal a CPU oracle of its tiles, and they are closer to Earth's terrain than the chart's.
/// </summary>
public static class FarTerrainChecks
{
    const int W = 1280, H = 720;

    public static string Run(string output, MapRenderer renderer, Topology t, AtlasInfo info, Func<double, double, (double X, double Z)?> locate, WorldSource source = null)
    {
        string root = Path.GetFullPath(Path.Combine(output, "..", "..", ".."));
        if (!File.Exists(Path.Combine(root, "Wmap.csproj"))) throw new Exception("Far terrain check: project root not found from " + output);
        string shaders = Path.Combine(root, "assets", "wmap", "shaders");
        string includes = Path.Combine(AppContext.BaseDirectory, "assets", "game", "shaderincludes");
        if (!Directory.Exists(includes)) includes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory", "assets", "game", "shaderincludes");
        // The game collects shader includes from every domain; Wmap's own sit in assets/wmap/shaderincludes.
        string[] includeDirs = { includes, Path.Combine(root, "assets", "wmap", "shaderincludes") };
        string vsh = File.ReadAllText(Path.Combine(shaders, "farterrain.vsh")), fsh = File.ReadAllText(Path.Combine(shaders, "farterrain.fsh"));

        int variants = 0, program = 0;
        string plainPrefix = null;
        foreach (int shadow in new[] { 0, 1, 2 })
        foreach (int ssao in new[] { 0, 2 })
        foreach (int godrays in new[] { 0, 1 })
        foreach (int lights in new[] { 0, 10 })
        {
            string prefix = $"#define FXAA 1\n#define SSAOLEVEL {ssao}\n#define NORMALVIEW 0\n#define BLOOM 1\n#define GODRAYS {godrays}\n#define FOAMEFFECT 1\n#define SHINYEFFECT 1\n" +
                $"#define SHADOWQUALITY {shadow}\n#define DYNLIGHTS {lights}\n#define USEOIT 1\n#define USESSBO 0\n#define WAVINGSTUFF 1\n#define MINBRIGHT 0\n#define MAXANIMATEDELEMENTS 46\n";
            int p = Link(Expand(vsh, includeDirs, prefix), Expand(fsh, includeDirs, prefix));
            variants++;
            if (shadow == 0 && ssao == 0 && godrays == 0 && lights == 0) { program = p; plainPrefix = prefix; } else GL.DeleteProgram(p);
        }

        var mesh = FarMesh.Build(8192, FarMesh.CellFor(1));
        using var draw = new FarTerrainDraw();
        draw.Upload(mesh);
        int fbo = GL.GenFramebuffer(), color = GL.GenTexture(), depth = GL.GenRenderbuffer(), sky = GL.GenTexture(), glow = GL.GenTexture();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, color);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, W, H, 0, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, W, H);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Incomplete far-terrain framebuffer");
            // Stand-ins for the game's sky.png and sunlight.png: a pale horizon fading to blue overhead, no sun glow.
            var skyPixels = new byte[4 * 64];
            for (int y = 0; y < 64; y++) { float f = y / 63f; skyPixels[y * 4] = (byte)(70 + 140 * f); skyPixels[y * 4 + 1] = (byte)(120 + 100 * f); skyPixels[y * 4 + 2] = (byte)(200 + 40 * f); skyPixels[y * 4 + 3] = 255; }
            Texture(sky, 1, 64, skyPixels);
            Texture(glow, 1, 1, new byte[4]);

            GL.UseProgram(program);
            void U(string n, float v) { int l = GL.GetUniformLocation(program, n); if (l >= 0) GL.Uniform1(l, v); }
            void I(string n, int v) { int l = GL.GetUniformLocation(program, n); if (l >= 0) GL.Uniform1(l, v); }
            U("flatFogDensity", 0); U("flatFogStart", -1000); U("zNear", 0.1f); U("zFar", 9216); U("viewDistance", 512);
            U("playerToSealevelOffset", 0); U("sunsetMod", 0); U("fogWaveCounter", 0);
            I("fogSphereQuantity", 0); I("pointLightQuantity", 0); I("ditherSeed", 0); I("horizontalResolution", W);
            I("sky", 1); I("glow", 2);
            GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, sky);
            GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, glow);
            GL.ActiveTexture(TextureUnit.Texture0);

            var coast = locate?.Invoke(43.55, 7.0) ?? (t.StartX + t.Width / 2, t.Height / 2);
            double eye = info.SeaLevel + 90;
            var frame = new FarFrame
            {
                Topology = t, SeaLevel = info.SeaLevel, HeightScale = info.MapHeight > info.SeaLevel ? info.MapHeight - info.SeaLevel : 200, HeightRange = info.ChartHeightRange,
                ChartTexture = renderer.BaseTexture, BlocksPerTexel = renderer.BaseTexel, Start = 64, Far = 8192,
                FogColor = new[] { 0.75f, 0.82f, 0.9f, 1f }, SunPosition = new[] { 0.3f, 0.8f, -0.5f }, SunColor = new[] { 1f, 0.95f, 0.85f }, DayLight = 1
            };
            if (frame.ChartTexture == 0) throw new Exception("Far terrain check: the world chart is not uploaded");

            var depths = new float[W * H];
            int[] Render(string name, double x, double z, Vector3 look)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.Viewport(0, 0, W, H);
                GL.ClearColor(1f, 0f, 1f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Lequal);
                var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 9216);
                var view = Matrix4.LookAt(Vector3.Zero, look, look.Y > 0.99f || look.Y < -0.99f ? -Vector3.UnitZ : Vector3.UnitY);
                Copy(projection, frame.Projection); Copy(view, frame.View);
                frame.CameraX = x; frame.CameraY = eye; frame.CameraZ = z;
                GL.UseProgram(program);
                draw.Draw(program, frame);
                GL.Disable(EnableCap.DepthTest);
                var raw = new int[W * H];
                GL.ReadPixels(0, 0, W, H, PixelFormat.Bgra, PixelType.UnsignedByte, raw);
                var rows = new int[raw.Length]; for (int y = 0; y < H; y++) Array.Copy(raw, y * W, rows, (H - y - 1) * W, W);
                var rawDepth = new float[W * H];
                GL.ReadPixels(0, 0, W, H, PixelFormat.DepthComponent, PixelType.Float, rawDepth);
                for (int y = 0; y < H; y++) Array.Copy(rawDepth, y * W, depths, (H - y - 1) * W, W);
                using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, W, H);
                Marshal.Copy(rows, 0, surface.DataPtr, rows.Length); surface.MarkDirty();
                surface.WriteToPng(Path.Combine(output, $"far-{name}.png"));
                var error = GL.GetError();
                if (error != ErrorCode.NoError) throw new Exception($"Far terrain {name}: GL error {error}");
                return rows;
            }
            const int Clear = unchecked((int)0xFFFF00FF);
            bool Drawn(int p) => p != Clear;
            // Looking north over the sea toward the coast and mountains, slightly down.
            var north = Render("coast", coast.X, coast.Z + 300, Vector3.Normalize(new Vector3(0, -0.06f, -1)));
            // At night the sky colour function returns the fog colour, not the dark sky: far terrain blended toward it
            // glowed brown against the night sky. Like the game's chunks it now takes at most the daylight of it. Under a
            // black and a brown fog colour (fog itself off), night renders match and noon renders do not.
            string night;
            {
                float savedDay = frame.DayLight, savedDensity = frame.FogDensity, savedMin = frame.FogMin; var savedFog = frame.FogColor;
                frame.FogDensity = 0; frame.FogMin = 0;
                int[] Lit(string name, float day, float[] fog) { frame.DayLight = day; frame.FogColor = fog; return Render(name, coast.X, coast.Z + 300, Vector3.Normalize(new Vector3(0, -0.06f, -1))); }
                int Differ(int[] x, int[] y)
                {
                    int worst = 0;
                    for (int i = 0; i < x.Length; i++) if (Drawn(x[i]) && Drawn(y[i])) worst = Math.Max(worst, Diff(x[i], y[i]));
                    return worst;
                }
                float[] dark = { 0.01f, 0.01f, 0.02f, 1 }, brown = { 0.30f, 0.20f, 0.10f, 1 };
                float nightLight = FarTerrain.SkyDaylight(0, 0.3f, 0), noonLight = FarTerrain.SkyDaylight(1, 0, 0);
                int nightDiff = Differ(Lit("night-dark", nightLight, dark), Lit("night-brown", nightLight, brown));
                // By day the sky colour comes from the sky texture: a reddish one in its place must show in far terrain.
                var noonBlue = Lit("noon-sky", noonLight, dark);
                int reddish = GL.GenTexture();
                var redPixels = new byte[4 * 64];
                for (int y = 0; y < 64; y++) { redPixels[y * 4] = 230; redPixels[y * 4 + 1] = 90; redPixels[y * 4 + 2] = 60; redPixels[y * 4 + 3] = 255; }
                Texture(reddish, 1, 64, redPixels);
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, reddish); GL.ActiveTexture(TextureUnit.Texture0);
                var noonRed = Lit("noon-red-sky", noonLight, dark);
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, sky); GL.ActiveTexture(TextureUnit.Texture0);
                GL.DeleteTexture(reddish);
                int noonDiff = Differ(noonBlue, noonRed);
                // Polar twilight: the sun just under the horizon gives the game a deep red sun colour. The game lights its
                // chunks with its ambient colour (rgbaAmbientIn), not the sun colour; 0.4.2's far terrain took the sun colour
                // and glowed red-brown beyond grey-blue snow (reported in game, Antarctica). Far terrain must ignore the sun
                // colour and follow the ambient colour.
                float twilight = FarTerrain.SkyDaylight(0.3f, 0, 0);
                var savedSun = frame.SunColor; var savedAmbient = frame.Ambient;
                frame.Ambient = new[] { 0.40f, 0.42f, 0.48f };
                frame.SunColor = new[] { 1f, 1f, 1f };
                var duskWhite = Lit("twilight-white-sun", twilight, dark);
                frame.SunColor = new[] { 1f, 0.35f, 0.2f };
                var duskRed = Lit("twilight-red-sun", twilight, dark);
                frame.Ambient = new[] { 1f, 1f, 1f };
                var duskBright = Lit("twilight-bright-ambient", twilight, dark);
                frame.SunColor = savedSun; frame.Ambient = savedAmbient;
                int sunDiff = Differ(duskWhite, duskRed), ambientDiff = Differ(duskRed, duskBright);
                frame.DayLight = savedDay; frame.FogDensity = savedDensity; frame.FogMin = savedMin; frame.FogColor = savedFog;
                if (sunDiff > 6) throw new Exception($"Far terrain at twilight takes the sun colour: up to {sunDiff}/255 between a white and a red sun (daylight {twilight:0.000})");
                if (ambientDiff < 20) throw new Exception($"Far terrain does not follow the game's ambient colour: only {ambientDiff}/255 between a dim and a full ambient");
                if (nightDiff > 6) throw new Exception($"Far terrain at night takes the fog colour: up to {nightDiff}/255 between a black and a brown fog colour (daylight {nightLight:0.000})");
                if (noonDiff < 15) throw new Exception($"Far terrain at noon no longer hazes into the sky: only {noonDiff}/255 between sky colours");
                night = $"night: {nightDiff}/255 between fog colours at daylight {nightLight:0.000}; noon: {noonDiff}/255 between sky colours; twilight: {sunDiff}/255 between a white and a red sun, {ambientDiff}/255 between ambients";
            }
            // A lake above the sea is water at its own level: looking along Lake Baikal with the far water drawn pure red,
            // red pixels lie at the lake's surface. 0.4.2 held all water at the sea and drew the lake's bed or a sea-level
            // hollow under the real lake water (reported in game).
            string lake = "";
            if (source?.Sample != null && locate?.Invoke(53.3, 107.9) is { } baikal)
            {
                var at = source.Sample(t.Canonical(baikal.X), baikal.Z);
                double surfaceY = at.Height + at.Depth;
                if (!Semantic.IsWater(at.Kind) || surfaceY < info.SeaLevel + 5) throw new Exception($"Lake check: Baikal's centre is not a raised lake (kind {at.Kind}, surface {surfaceY}, sea {info.SeaLevel})");
                double savedEye = eye; var savedWater = frame.Water;
                eye = surfaceY + 40;
                var lookAlong = Vector3.Normalize(new Vector3(0.55f, -0.06f, -0.83f));
                frame.Water = new[] { 1f, 0f, 0f };
                var red = Render("lake-baikal", baikal.X, baikal.Z, lookAlong);
                frame.Water = savedWater;
                var inverse = Matrix4.Invert(Matrix4.LookAt(Vector3.Zero, lookAlong, Vector3.UnitY) * Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 9216));
                int lakePixels = 0, off = 0; double low = double.MaxValue, high = double.MinValue;
                for (int py = 0; py < H; py++)
                for (int px = 0; px < W; px++)
                {
                    int c = red[py * W + px];
                    int r = c >> 16 & 255, g = c >> 8 & 255, b = c & 255;
                    if (!Drawn(c) || r < 2 * g + 20 || r < 2 * b + 20) continue;
                    lakePixels++;
                    var ndc = new Vector4((2f * (px + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 2 * depths[py * W + px] - 1, 1);
                    var world = ndc * inverse; world /= world.W;
                    double rel = world.Y + eye - surfaceY;
                    low = Math.Min(low, rel); high = Math.Max(high, rel);
                    if (rel < -4 || rel > 3) off++;
                }
                eye = savedEye;
                if (lakePixels < 500) throw new Exception($"Lake check: looking along Baikal shows only {lakePixels} water pixels");
                if (off > lakePixels / 100) throw new Exception($"Lake check: {off} of {lakePixels} Baikal water pixels lie off the lake's surface ({low:0.0}..{high:0.0} blocks from it)");
                lake = $"; Baikal: {lakePixels} water pixels at the lake's surface ({low:0.0}..{high:0.0} blocks)";
            }
            int below = 0, belowTotal = 0, above = 0;
            for (int y = H / 2 + 20; y < H; y += 4) for (int x = 0; x < W; x += 4) { belowTotal++; if (Drawn(north[y * W + x])) below++; }
            for (int y = 0; y < H / 10; y++) for (int x = 0; x < W; x += 4) if (Drawn(north[y * W + x])) above++;
            if (below < belowTotal * 0.5) throw new Exception($"Far terrain drew only {below}/{belowTotal} pixels below the horizon");
            if (above > 0) throw new Exception($"Far terrain drew {above} pixels high in the sky");
            int distinct = Enumerable.Range(H / 2, H / 2).SelectMany(y => Enumerable.Range(0, W / 8).Select(i => north[y * W + i * 8] & 0xF0F0F0)).Distinct().Count();
            // Only a real chart has relief to colour; the plain profile's synthetic island is one flat level.
            if (distinct < 12 && locate != null) throw new Exception($"Far terrain is flat colour ({distinct} colours)");
            // Inside the view distance nothing is drawn: looking straight down shows only the clear colour at the centre.
            var down = Render("down", coast.X, coast.Z + 300, new Vector3(0, -1, 0));
            if (Drawn(down[H / 2 * W + W / 2])) throw new Exception("Far terrain drawn inside the view distance");
            string edge = SeaEdge();
            string curve = locate != null && info.HasGeography ? "; " + Curve() : "";
            string band = source?.Samplers != null && source.Biome != null && locate != null && !info.FlatPrediction ? "; " + NearBand() : "";
            string seam = "";
            if (t.Wraps)
            {
                // The same place one circumference east draws identically: the chart repeats across the seam.
                var west = Render("seam-west", t.StartX + 600, coast.Z, Vector3.Normalize(new Vector3(-1, -0.05f, 0)));
                var east = Render("seam-east", t.StartX + 600 + t.Width, coast.Z, Vector3.Normalize(new Vector3(-1, -0.05f, 0)));
                int differ = 0, seamDrawn = 0;
                for (int i = 0; i < west.Length; i += 3) { if (Drawn(west[i])) seamDrawn++; if (Diff(west[i], east[i]) > 6) differ++; }
                if (seamDrawn < west.Length / 3 / 4) throw new Exception("Far terrain across the seam is mostly missing");
                if (differ > west.Length / 3 / 200) throw new Exception($"Far terrain differs one lap apart ({differ} pixels)");
                seam = $", seam: {seamDrawn} drawn, {differ} differ";
            }
            // The near band and climate tiles (0.4.0), through the real FarTiles, fed from Earth's samplers.
            string NearBand()
            {
                const int View = 512;
                double start = FarTerrain.StartFor(View);
                int capture = Link(Expand(vsh, includeDirs, plainPrefix), Expand(fsh, includeDirs, plainPrefix), "wmapPos");
                var bandMesh = FarMesh.Build(8192, FarMesh.NearBandCell);
                using var bandDraw = new FarTerrainDraw();
                bandDraw.Upload(bandMesh);
                int tf = GL.GenBuffer(), query = GL.GenQuery(), plant = 0, waterTint = 0;
                var timings = new List<double>();
                FarTiles tiles = null;
                var savedStart = frame.Start; var savedEye = eye;
                try
                {
                    plant = Tint("planttint.png"); waterTint = Tint("watertint.png");
                    var sample = source.Samplers();
                    Topology topo = t; Func<double, double, MapSample> src = sample; Func<double, double, BiomeSample> biomeSrc = source.Biome;
                    // A server answering at once: the tiles the atlas server would send.
                    var pending = new Queue<TileRequest>();
                    TileResponse Answer(TileRequest r)
                    {
                        var key = new TileKey(0, r.Level, r.X, r.Z);
                        var response = new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = 1, Content = r.Content };
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        if (TileContent.HasSemantic(r.Content)) response.Data = SemanticTile.Pack(SemanticTile.Sample(topo, key, src, info.SeaLevel, System.Threading.CancellationToken.None, null, info.ChartHeightRange));
                        if (TileContent.HasBiome(r.Content)) response.Biome = BiomeTile.Pack(BiomeTile.Sample(topo, key, biomeSrc, System.Threading.CancellationToken.None));
                        timings.Add(watch.Elapsed.TotalMilliseconds);
                        return response;
                    }
                    void Fill(double x, double z)
                    {
                        var (ox, _) = FarTerrainDraw.Origin(bandMesh, x, z);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        while (watch.ElapsedMilliseconds < 120000)
                        {
                            tiles.Update(x, z, ox, start, 64);
                            while (pending.Count > 0) tiles.Receive(Answer(pending.Dequeue()));
                            bool done = tiles.Layers.All(l => l.Ready == l.Wanted && l.Wanted > 0);
                            if (done) break;
                            System.Threading.Thread.Sleep(10);
                        }
                        if (tiles.Layers.Any(l => l.Ready < l.Wanted)) throw new Exception("Near band: tiles did not load: " + string.Join(", ", tiles.Layers.Select(l => $"{l.Ready}/{l.Wanted}")) + " " + tiles.LastError);
                        // Fully faded in.
                        System.Threading.Thread.Sleep((int)(FarTiles.FadeSeconds * 1000) + 100);
                        tiles.Update(x, z, ox, start, 64);
                    }
                    tiles = new FarTiles(t, info.Session, r => pending.Enqueue(r), null, 512, 1536, 8192, true, true, info.BiomeId ?? "000000000000") { HeightRange = info.ChartHeightRange };
                    // Created while the game might have a pixel-unpack buffer bound and an odd alignment.
                    int pbo = GL.GenBuffer();
                    GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BufferData(BufferTarget.PixelUnpackBuffer, 64, IntPtr.Zero, BufferUsageHint.StreamDraw);
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2); GL.PixelStore(PixelStoreParameter.UnpackRowLength, 7);
                    tiles.CreateTextures();
                    GL.GetInteger(GetPName.PixelUnpackBufferBinding, out int pboAfter); GL.GetInteger(GetPName.UnpackAlignment, out int alignAfter); GL.GetInteger(GetPName.UnpackRowLength, out int rowAfter);
                    GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.DeleteBuffer(pbo);
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4); GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                    GL.BindTexture(TextureTarget.Texture2D, tiles.Band.SemanticTexture);
                    GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int created);
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                    if (created != FarTileWindow.AtlasTexels || pboAfter != pbo || alignAfter != 2 || rowAfter != 7 || GL.GetError() != ErrorCode.NoError)
                        throw new Exception($"Near band: texture creation under a bound unpack buffer (width {created}, buffer {pboAfter}/{pbo}, alignment {alignAfter}, row length {rowAfter})");
                    frame.Tiles = tiles; frame.PlantTint = plant; frame.WaterTint = waterTint;
                    frame.BandRadius = 1536; frame.BandFadeStart = 1200; frame.Start = start;

                    // Captures every displaced vertex (camera-relative position) of one draw.
                    var indices = bandMesh.Indices; var verts = bandMesh.Vertices;
                    float[] Capture(double x, double z, double eyeHeight, FarTerrainDraw with = null)
                    {
                        var d = with ?? bandDraw; var m = d.Mesh; var indices = m.Indices;
                        frame.CameraX = x; frame.CameraY = eyeHeight; frame.CameraZ = z;
                        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 9216);
                        Copy(projection, frame.Projection); Copy(Matrix4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY), frame.View);
                        GL.BindBuffer(BufferTarget.TransformFeedbackBuffer, tf);
                        GL.BufferData(BufferTarget.TransformFeedbackBuffer, indices.Length * 3 * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamRead);
                        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, 0, tf);
                        GL.UseProgram(capture);
                        GL.Enable(EnableCap.RasterizerDiscard);
                        GL.BeginTransformFeedback(TransformFeedbackPrimitiveType.Triangles);
                        d.Draw(capture, frame);
                        GL.EndTransformFeedback();
                        GL.Disable(EnableCap.RasterizerDiscard);
                        var flat = new float[indices.Length * 3];
                        GL.GetBufferSubData(BufferTarget.TransformFeedbackBuffer, IntPtr.Zero, flat.Length * sizeof(float), flat);
                        GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, 0, 0);
                        var error = GL.GetError();
                        if (error != ErrorCode.NoError) throw new Exception("Near band capture: GL error " + error);
                        // Per mesh vertex: its displaced y.
                        var displaced = new float[m.VertexCount];
                        for (int k = 0; k < indices.Length; k++) displaced[indices[k]] = flat[k * 3 + 1];
                        return displaced;
                    }
                    var byPosition = new Dictionary<(float, float), List<int>>();
                    for (int i = 0; i < bandMesh.VertexCount; i++) { var key = (verts[i * 4], verts[i * 4 + 1]); if (!byPosition.TryGetValue(key, out var list)) byPosition[key] = list = new(); list.Add(i); }
                    // Every vertex shared by two rings agrees, and every stitched vertex sits on its coarse edge's end below it
                    // (collapsed there: no T-junction, WM-0028).
                    (int Stitched, double Worst) Stitches(float[] y, string where)
                    {
                        int stitched = 0; double worst = 0;
                        for (int i = 0; i < bandMesh.VertexCount; i++)
                        {
                            float vx = verts[i * 4], vz = verts[i * 4 + 1], sx = verts[i * 4 + 2], sz = verts[i * 4 + 3];
                            if (sx == 0 && sz == 0)
                            {
                                foreach (int other in byPosition[(vx, vz)]) if (verts[other * 4 + 2] == 0 && verts[other * 4 + 3] == 0) worst = Math.Max(worst, Math.Abs(y[other] - y[i]));
                                continue;
                            }
                            if (!byPosition.TryGetValue((vx - sx, vz - sz), out var a) || !byPosition.TryGetValue((vx + sx, vz + sz), out var b)) throw new Exception("Near band: a stitched vertex has no coarse edge ends");
                            int ea = a.First(v => verts[v * 4 + 2] == 0 && verts[v * 4 + 3] == 0), eb = b.First(v => verts[v * 4 + 2] == 0 && verts[v * 4 + 3] == 0);
                            worst = Math.Max(worst, Math.Abs(y[i] - y[ea]));
                            stitched++;
                        }
                        if (worst > 2e-3 * Math.Max(1, Math.Abs(frame.CameraY - info.SeaLevel) / 100)) throw new Exception($"Near band {where}: ring edges crack by {worst:0.0000} blocks");
                        return (stitched, worst);
                    }
                    // The ground the CPU expects from the band tile at a point (bilinear on the stored bytes, then decoded, as the GPU does).
                    var tileCache = new Dictionary<TileKey, byte[]>();
                    double BandGround(double wx, double wz)
                    {
                        var window = new FarTileWindow(topo, FarTiles.BandLevel);
                        double rel = topo.Canonical(wx) - topo.StartX;
                        window.TileX(rel, out _, out int index, out double inside); window.TileZ(wz, out int j, out double insideZ);
                        var key = new TileKey(0, FarTiles.BandLevel, index, j);
                        if (!tileCache.TryGetValue(key, out var rgba)) tileCache[key] = rgba = SemanticTile.Sample(topo, key, src, info.SeaLevel, System.Threading.CancellationToken.None, null, info.ChartHeightRange);
                        double tx = window.Texel(inside) - 0.5, tz = window.Texel(insideZ) - 0.5;
                        int x0 = (int)Math.Floor(tx), z0 = (int)Math.Floor(tz); double fx = tx - x0, fz = tz - z0;
                        double G(int x, int z) => rgba[(z * TileData.Padded + x) * 4 + 1] / 255.0;
                        double g = (1 - fx) * (1 - fz) * G(x0, z0) + fx * (1 - fz) * G(x0 + 1, z0) + (1 - fx) * fz * G(x0, z0 + 1) + fx * fz * G(x0 + 1, z0 + 1);
                        double e = g * (255 / 127.5) - 1;
                        return Math.Max(0, Math.Sign(e) * e * e * info.ChartHeightRange);
                    }
                    // The same four-block grid from exact heights (0.5.3-rc.13, WM-0014): the error the band's sampling makes on this
                    // terrain whatever it stores, without the tiles' whole-block rounding and 8-bit code. On GLO-90's relief at Nice
                    // that alone is about 1.2 blocks at the median (Earth 2.0; 0.33 on the old built-in heights), so the fixed
                    // 1.0-block limit of 0.4.0 failed a band that was as good as its grid.
                    double BandGroundExact(double wx, double wz)
                    {
                        var window = new FarTileWindow(topo, FarTiles.BandLevel);
                        double rel = topo.Canonical(wx) - topo.StartX;
                        window.TileX(rel, out _, out int index, out double inside); window.TileZ(wz, out int j, out double insideZ);
                        var key = new TileKey(0, FarTiles.BandLevel, index, j);
                        double tx = window.Texel(inside) - 0.5, tz = window.Texel(insideZ) - 0.5;
                        int x0 = (int)Math.Floor(tx), z0 = (int)Math.Floor(tz); double fx = tx - x0, fz = tz - z0;
                        // Texel x of a tile samples (x - 0.5) steps from its corner (SemanticTile.Sample, past its margin).
                        double H(int x, int z)
                        {
                            double sx = topo.StartX + key.X * key.Span + (x - 0.5) * key.Step, sz = key.Z * key.Span + (z - 0.5) * key.Step;
                            var m = src(topo.Canonical(sx), sz);
                            return Semantic.RelativeHeight(m.Height, m.Depth, m.Kind, info.SeaLevel);
                        }
                        double e = (1 - fx) * (1 - fz) * H(x0, z0) + fx * (1 - fz) * H(x0 + 1, z0) + (1 - fx) * fz * H(x0, z0 + 1) + fx * fz * H(x0 + 1, z0 + 1);
                        return Math.Max(0, e);
                    }
                    double Truth(double wx, double wz)
                    {
                        var m = src(topo.Canonical(wx), wz);
                        return Math.Max(0, Semantic.RelativeHeight(m.Height, m.Depth, m.Kind, info.SeaLevel));
                    }
                    double surface = info.SeaLevel - FarTerrain.WaterBelowSeaLevel;
                    // Vertices where the band alone sets the height: past the push-down, inside the outer fade.
                    IEnumerable<int> Core(double x, double z) => Enumerable.Range(0, bandMesh.VertexCount).Where(i =>
                    {
                        if (verts[i * 4 + 2] != 0 || verts[i * 4 + 3] != 0) return false;
                        var (ox, oz) = FarTerrainDraw.Origin(bandMesh, x, z);
                        double dx = verts[i * 4] + ox - x, dz = verts[i * 4 + 1] + oz - z, d = Math.Sqrt(dx * dx + dz * dz);
                        return d > start + 520 && d < 1200 - 20;
                    });
                    (double Worst, int Count, double[] BandErrors, double[] ChartErrors, double[] GridErrors, double[] Signed) Oracle(double x, double z, double eyeY, string where)
                    {
                        frame.Canopy = 0;
                        var y = Capture(x, z, eyeY);
                        var (ox, oz) = FarTerrainDraw.Origin(bandMesh, x, z);
                        double worst = 0; var bandErrors = new List<double>(); var gridErrors = new List<double>(); var signed = new List<double>();
                        var core = Core(x, z).ToList();
                        var landCore = new List<int>();
                        foreach (int i in core)
                        {
                            double wx = verts[i * 4] + ox, wz = verts[i * 4 + 1] + oz, ground = y[i] + eyeY - surface;
                            double expected = BandGround(wx, wz), truth = Truth(wx, wz);
                            worst = Math.Max(worst, Math.Abs(ground - expected) / (0.05 + 0.002 * expected));
                            // Accuracy on land only: the flat sea is exact for both and would hide the difference.
                            if (truth > 1)
                            {
                                bandErrors.Add(Math.Abs(ground - truth)); signed.Add(ground - truth); landCore.Add(i);
                                gridErrors.Add(Math.Abs(BandGroundExact(wx, wz) - truth));
                            }
                        }
                        if (worst > 1) throw new Exception($"Near band {where}: heights differ from the band tiles (worst {worst:0.00} of the tolerance)");
                        // The same vertices drawn from the chart alone.
                        frame.Tiles = null;
                        var chartY = Capture(x, z, eyeY);
                        frame.Tiles = tiles;
                        var chartErrors = landCore.Select(i => Math.Abs(chartY[i] + eyeY - surface - Truth(verts[i * 4] + ox, verts[i * 4 + 1] + oz))).ToArray();
                        frame.Canopy = 8;
                        return (worst, core.Count, bandErrors.ToArray(), chartErrors, gridErrors.ToArray(), signed.ToArray());
                    }
                    static double Pct(double[] v, double p) { var s = v.OrderBy(a => a).ToArray(); return s.Length == 0 ? 0 : s[Math.Min(s.Length - 1, (int)(p * s.Length))]; }

                    // Coastal mountains above Nice: relief the 25-block chart smooths away.
                    var nice = locate(43.75, 7.25) ?? throw new Exception("Near band: no Nice");
                    double eyeY = info.SeaLevel + 40;
                    Fill(nice.X, nice.Z);
                    var (stitched, crack) = Stitches(Capture(nice.X, nice.Z, eyeY), "at Nice");
                    var o = Oracle(nice.X, nice.Z, eyeY, "at Nice");
                    double bandMedian = Pct(o.BandErrors, 0.5), band95 = Pct(o.BandErrors, 0.95), chartMedian = Pct(o.ChartErrors, 0.5), chart95 = Pct(o.ChartErrors, 0.95);
                    double gridMedian = Pct(o.GridErrors, 0.5), grid95 = Pct(o.GridErrors, 0.95), bias = o.Signed.Length == 0 ? 0 : o.Signed.Average();
                    if (o.Count < 1000 || o.BandErrors.Length < 300) throw new Exception($"Near band: only {o.Count} vertices in the band's core, {o.BandErrors.Length} on land");
                    if (band95 > chart95 / 2 || bandMedian > chartMedian / 2)
                        throw new Exception($"Near band: not closer to Earth's terrain than the chart (median {bandMedian:0.0} vs {chartMedian:0.0}, 95% {band95:0.0} vs {chart95:0.0} blocks)");
                    // As close as its four-block grid allows (WM-0014): within a quarter block of the grid's own error at the median
                    // and a block at the 95th percentile, and no offset (a half-block rounding, a shifted sample or another column
                    // method shows as a mean error that the medians of absolute errors hide).
                    if (bandMedian > gridMedian + 0.25 || band95 > grid95 + 1.0 || Math.Abs(bias) > 0.3)
                        throw new Exception($"Near band: farther from Earth's terrain than its grid allows (median {bandMedian:0.00} vs the grid's {gridMedian:0.00}, 95% {band95:0.0} vs {grid95:0.0}, mean {bias:+0.00;-0.00} blocks)");

                    // Continuity as tiles arrive: a weight just above zero changes heights (canopy) only a little.
                    frame.Canopy = 8;
                    double Jump(FarTiles.Layer layer)
                    {
                        var saved = (float[])layer.Slots.Fade.Clone();
                        Array.Fill(layer.Slots.Fade, 0f); var off = Capture(nice.X, nice.Z, eyeY);
                        Array.Fill(layer.Slots.Fade, 1e-4f); var on = Capture(nice.X, nice.Z, eyeY);
                        Array.Copy(saved, layer.Slots.Fade, saved.Length);
                        return Enumerable.Range(0, off.Length).Max(i => Math.Abs(on[i] - off[i]));
                    }
                    double climateJump = Jump(tiles.Climate), bandJump = Jump(tiles.Band);
                    if (climateJump > 0.02 || bandJump > 0.02) throw new Exception($"Near band: heights jump as tiles start to fade in (climate {climateJump:0.000}, band {bandJump:0.000} blocks)");

                    // GL state: what the draw and the uploads found is what they leave.
                    int dummy = GL.GenTexture(), dummyVao = GL.GenVertexArray();
                    GL.ActiveTexture(TextureUnit.Texture0 + FarTerrainDraw.ClimateBiomeUnit); GL.BindTexture(TextureTarget.Texture2D, dummy);
                    GL.ActiveTexture(TextureUnit.Texture3); GL.BindVertexArray(dummyVao); GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

                    // Pictures: toward the coast and the Alps, and out to sea. Tinted sea is the game's water hue.
                    var land = Render("band-coast", nice.X, nice.Z + 900, Vector3.Normalize(new Vector3(0.3f, -0.05f, -1)));
                    // Water only where the drawn surface is at the sea: the far sea drawn pure red (Sea's colour hook) must
                    // not climb up the coast. Each red pixel's height comes from the depth buffer.
                    // The chart's river lines take the same colour, and a river above the sea is drawn above it. A red pixel
                    // on one of Earth's river lines (within a chart texel of its banks) is judged against the valley floor
                    // there instead (the lowest ground or water surface within a texel): at most 6 blocks above it, the
                    // allowance of the shader's own rule (0.5.0-pre.10). Before, the lines hung on the coarse mesh's slopes
                    // over Earth's newer rivers in the hills above Nice, up to 57 blocks above their water.
                    string shore;
                    {
                        var coastLook = Vector3.Normalize(new Vector3(0.3f, -0.05f, -1));
                        frame.Water = new[] { 1f, 0f, 0f };
                        var red = Render("band-shore", nice.X, nice.Z + 900, coastLook);
                        frame.Water = null;
                        var inverse = Matrix4.Invert(Matrix4.LookAt(Vector3.Zero, coastLook, Vector3.UnitY) * Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 9216));
                        double seaSurface = info.SeaLevel - FarTerrain.WaterBelowSeaLevel;
                        double texel = renderer.BaseTexel;
                        var lines = (source?.Rivers ?? (IReadOnlyList<RiverSegment>)Array.Empty<RiverSegment>())
                            .Where(r => Math.Abs(topo.Delta(nice.X, r.X1)) < 8000 && Math.Abs(r.Z1 - nice.Z) < 8000).ToList();
                        bool OnRiverLine(double wx, double wz) => lines.Any(r =>
                        {
                            double x1 = topo.Delta(wx, r.X1), z1 = r.Z1 - wz, x2 = x1 + topo.Delta(r.X1, r.X2), z2 = r.Z2 - wz;
                            double dx = x2 - x1, dz = z2 - z1, length = dx * dx + dz * dz;
                            double u = length > 0 ? Math.Clamp(-(x1 * dx + z1 * dz) / length, 0, 1) : 0;
                            double px = x1 + u * dx, pz = z1 + u * dz;
                            return Math.Sqrt(px * px + pz * pz) < r.HalfWidth + 1.5 * texel;
                        });
                        double ValleyFloor(double wx, double wz)
                        {
                            double floor = double.MaxValue; int reach = (int)Math.Ceiling(texel);
                            for (int dx = -reach; dx <= reach; dx += 2)
                            for (int dz = -reach; dz <= reach; dz += 2)
                            {
                                var m = src(topo.Canonical(wx + dx), wz + dz);
                                floor = Math.Min(floor, (Semantic.IsWater(m.Kind) ? m.Height + m.Depth : m.Height) - seaSurface);
                            }
                            return floor;
                        }
                        // The highest water surface Earth has within a texel (0.5.3-rc.13): a lake above the sea is drawn at its own
                        // surface, so water there is not the far sea climbing the coast (Earth 2.0's lakes by Nice, 3.3 blocks up).
                        double WaterAbove(double wx, double wz)
                        {
                            double best = 0; int reach = (int)Math.Ceiling(texel);
                            for (int dx = -reach; dx <= reach; dx += 2)
                            for (int dz = -reach; dz <= reach; dz += 2)
                            {
                                var m = src(topo.Canonical(wx + dx), wz + dz);
                                if (Semantic.IsWater(m.Kind)) best = Math.Max(best, m.Height - seaSurface);
                            }
                            return best;
                        }
                        int seaPixels = 0, climbing = 0, riverPixels = 0, hanging = 0, lakePixels = 0; double highest = 0, riverHighest = 0, hangingWorst = 0;
                        for (int py = 0; py < H; py++)
                        for (int px = 0; px < W; px++)
                        {
                            int c = red[py * W + px];
                            int r = c >> 16 & 255, g = c >> 8 & 255, b = c & 255;
                            if (!Drawn(c) || r < 2 * g + 20 || r < 2 * b + 20) continue;
                            var ndc = new Vector4((2f * (px + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 2 * depths[py * W + px] - 1, 1);
                            var world = ndc * inverse; world /= world.W;
                            double above = world.Y + eye - seaSurface;
                            double wx = world.X + nice.X, wz = world.Z + nice.Z + 900;
                            if (above > 3 && OnRiverLine(wx, wz))
                            {
                                double over = above - ValleyFloor(wx, wz);
                                riverPixels++;
                                riverHighest = Math.Max(riverHighest, over);
                                if (over > 6) { hanging++; hangingWorst = Math.Max(hangingWorst, over); }
                                continue;
                            }
                            seaPixels++;
                            double overWater = above;
                            if (above > 3) { double lake = WaterAbove(wx, wz); if (lake > 1) { lakePixels++; overWater = above - lake; } }
                            highest = Math.Max(highest, overWater);
                            if (overWater > 3) climbing++;
                        }
                        if (seaPixels < 1000) throw new Exception($"Near band: the coast view shows only {seaPixels} sea pixels");
                        if (climbing > 0 || hanging > 0)
                            throw new Exception($"Near band: water drawn above its level: the far sea climbs the coast in {climbing} of {seaPixels} water pixels ({lakePixels} by a lake) " +
                                $"(more than 3 blocks above the sea, up to {highest:0.0}); river lines hang above their valleys in {hanging} of {riverPixels} " +
                                $"pixels more than 3 blocks up (more than 6 blocks above the valley floor, up to {hangingWorst:0.0})");
                        shore = $"far sea stays at the sea: {seaPixels} sea pixels, the highest {highest:0.0} blocks above it; {riverPixels} river-line pixels more than 3 blocks up, at most {riverHighest:0.0} blocks above the valley floor";
                    }
                    // GPU time of the draw alone (median of 9), with the band and tiles, the same mesh from the chart alone, and 0.3.10's mesh.
                    // After 60 draws first (0.5.0-pre.1): the GPU raises its clocks under load, and earlier runs timed at whatever
                    // clocks it had, from 0.31 to 1.24 ms for the same draw.
                    double Time(FarTerrainDraw d, FarTiles with)
                    {
                        frame.Tiles = with;
                        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); GL.Viewport(0, 0, W, H);
                        GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal);
                        GL.UseProgram(program);
                        for (int k = 0; k < 60; k++) { GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit); d.Draw(program, frame); }
                        GL.Finish();
                        var samples = new List<double>();
                        for (int k = 0; k < 9; k++)
                        {
                            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                            GL.UseProgram(program);
                            GL.BeginQuery(QueryTarget.TimeElapsed, query);
                            d.Draw(program, frame);
                            GL.EndQuery(QueryTarget.TimeElapsed);
                            GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns);
                            samples.Add(ns / 1e6);
                        }
                        GL.Disable(EnableCap.DepthTest);
                        frame.Tiles = tiles;
                        return samples.OrderBy(v => v).ElementAt(4);
                    }
                    double withBand = Time(bandDraw, tiles), chartOnly = Time(bandDraw, null), before = Time(draw, null);
                    GL.ActiveTexture(TextureUnit.Texture0 + FarTerrainDraw.ClimateBiomeUnit); GL.BindTexture(TextureTarget.Texture2D, dummy);
                    GL.ActiveTexture(TextureUnit.Texture3); GL.BindVertexArray(dummyVao); GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                    bandDraw.Draw(program, frame);
                    GL.GetInteger(GetPName.ActiveTexture, out int active); GL.GetInteger(GetPName.VertexArrayBinding, out int vaoAfter); GL.GetInteger(GetPName.UnpackAlignment, out int alignment);
                    GL.ActiveTexture(TextureUnit.Texture0 + FarTerrainDraw.ClimateBiomeUnit); GL.GetInteger(GetPName.TextureBinding2D, out int boundAfter);
                    GL.ActiveTexture(TextureUnit.Texture0); GL.BindVertexArray(0); GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                    GL.DeleteTexture(dummy); GL.DeleteVertexArray(dummyVao);
                    if (active != (int)TextureUnit.Texture3 || vaoAfter != dummyVao || boundAfter != dummy || alignment != 1)
                        throw new Exception($"Near band: GL state changed by the draw (unit {active - (int)TextureUnit.Texture0}, VAO {vaoAfter}/{dummyVao}, texture {boundAfter}/{dummy}, alignment {alignment})");
                    eye = info.SeaLevel + 3;
                    // From 3 blocks up the far sea fills a few rows just under the horizon (the screen's middle row).
                    var sea = Render("band-sea", nice.X, nice.Z + 900, new Vector3(0, 0, 1));
                    frame.PlantTint = frame.WaterTint = 0;
                    var plain = Render("band-sea-untinted", nice.X, nice.Z + 900, new Vector3(0, 0, 1));
                    frame.PlantTint = plant; frame.WaterTint = waterTint;
                    (double G, double B) Mean(int[] px)
                    {
                        double g = 0, b = 0; int n = 0;
                        for (int y = H / 2; y < H / 2 + 12; y++) for (int x = 0; x < W; x += 2) { int c = px[y * W + x]; if (!Drawn(c)) continue; g += c >> 8 & 255; b += c & 255; n++; }
                        if (n < 100) throw new Exception($"Near band: the sea view shows only {n} sea pixels");
                        return (g / n, b / n);
                    }
                    var tinted = Mean(sea); var untinted = Mean(plain);
                    if (tinted.G / Math.Max(1, tinted.B) <= untinted.G / Math.Max(1, untinted.B)) throw new Exception($"Near band: the climate tint does not change the sea (G/B {tinted.G / tinted.B:0.00} vs {untinted.G / untinted.B:0.00})");

                    // Readiness must be continuous wherever tiles are missing or part faded in. Probe points every quarter
                    // block along lines across: a corner whose diagonal tile is missing, an edge between tiles faded to
                    // 0.3 and 0.8, and (in a second world) a 30-block seam tile. A step in readiness shows as one large
                    // height step between neighbouring points; a smooth fade moves less than 1.5 blocks per quarter block.
                    double Probe(double camX, double camZ, (double X, double Z) from, (double X, double Z) to, string what)
                    {
                        var (ox, oz) = FarTerrainDraw.Origin(bandMesh, camX, camZ);
                        int count = (int)Math.Round(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Z - from.Z, 2)) / 0.25) + 1;
                        var points = new float[count * 4]; var order = new int[count * 3];
                        for (int i = 0; i < count; i++)
                        {
                            double f = i / (double)(count - 1);
                            points[i * 4] = (float)(from.X + (to.X - from.X) * f - ox); points[i * 4 + 1] = (float)(from.Z + (to.Z - from.Z) * f - oz);
                            order[i * 3] = order[i * 3 + 1] = order[i * 3 + 2] = i;
                        }
                        using var probeDraw = new FarTerrainDraw();
                        probeDraw.Upload(new FarMesh { Vertices = points, Indices = order, Rings = bandMesh.Rings, Cell0 = bandMesh.Cell0, Radius = bandMesh.Radius });
                        var y = Capture(camX, camZ, info.SeaLevel + 40, probeDraw);
                        double worst = 0;
                        for (int i = 1; i < count; i++) worst = Math.Max(worst, Math.Abs(y[i] - y[i - 1]));
                        if (worst > 1.5) throw new Exception($"Near band: height jumps {worst:0.00} blocks within a quarter block {what}");
                        return worst;
                    }

                    // A circumference that is not a multiple of a band tile: a 300-block tile at the seam. A flat chart
                    // and smooth band terrain, so any step in the band's weight shows as a step in height.
                    string partialPart;
                    {
                        var synthetic = new Topology { StartX = t.StartX, Width = 10240 + 300, Height = t.Height, Wraps = true };
                        int flat = GL.GenTexture();
                        var flatTexels = new byte[4 * 4 * 4];
                        for (int i = 0; i < 16; i++) { flatTexels[i * 4] = 255; flatTexels[i * 4 + 1] = 128; flatTexels[i * 4 + 3] = 255; }
                        Texture(flat, 4, 4, flatTexels);
                        var savedTopology = frame.Topology; int savedChart = frame.ChartTexture; double savedTexel = frame.BlocksPerTexel;
                        var earthTiles = tiles;
                        FarTiles seamTiles = null;
                        try
                        {
                            topo = synthetic; tileCache.Clear();
                            src = (x, z) => new MapSample(info.SeaLevel + 30 + (int)Math.Round(20 * Math.Sin(2 * Math.PI * (x - synthetic.StartX) / synthetic.Width * 3) + 10 * Math.Cos(z / 300.0)), 0, TileData.Land);
                            biomeSrc = (x, z) => new BiomeSample(0, 128, 128);
                            frame.Topology = synthetic; frame.ChartTexture = flat; frame.BlocksPerTexel = synthetic.Width / 4;
                            seamTiles = tiles = new FarTiles(synthetic, info.Session, r => pending.Enqueue(r), null, 512, 1536, 8192, true, true, "000000000000");
                            tiles.CreateTextures(); frame.Tiles = tiles;
                            // 800 blocks east of the seam: the seam lies inside the 16-block ring, not on a ring boundary.
                            double px = synthetic.StartX + 800, pz = nice.Z;
                            Fill(px, pz);
                            var (pStitched, pCrack) = Stitches(Capture(px, pz, info.SeaLevel + 40), "across a partial seam tile");
                            var po = Oracle(px, pz, info.SeaLevel + 40, "across a partial seam tile");
                            // Every tile half faded in: readiness dips toward each tile edge alike, so the step in height across
                            // the partial tile's east edge (the seam) must be like the step across an ordinary tile edge.
                            var (ox, oz) = FarTerrainDraw.Origin(bandMesh, px, pz);
                            var savedFade = (float[])tiles.Band.Slots.Fade.Clone();
                            Array.Fill(tiles.Band.Slots.Fade, 0.5f);
                            var y = Capture(px, pz, info.SeaLevel + 40);
                            Array.Copy(savedFade, tiles.Band.Slots.Fade, savedFade.Length);
                            double StepAcross(double edge)
                            {
                                double worst = 0; int pairs = 0;
                                for (int i = 0; i < bandMesh.VertexCount; i++)
                                {
                                    if (verts[i * 4 + 2] != 0 || verts[i * 4 + 3] != 0) continue;
                                    double wx = verts[i * 4] + ox, wz = verts[i * 4 + 1] + oz;
                                    if (Math.Abs(wz - pz) > 48 || wx >= edge || wx + 16 < edge) continue;
                                    if (!byPosition.TryGetValue((verts[i * 4] + 16, verts[i * 4 + 1]), out var next)) continue;
                                    int j = next.First(v => verts[v * 4 + 2] == 0 && verts[v * 4 + 3] == 0);
                                    worst = Math.Max(worst, Math.Abs(y[j] - y[i])); pairs++;
                                }
                                if (pairs == 0) throw new Exception($"Near band: no vertex pairs straddle the edge at {edge}");
                                return worst;
                            }
                            // Unwrapped, the seam is the origin lap's west edge; the next tile edge is 512 blocks east of it.
                            double across = StepAcross(synthetic.StartX), ordinary = StepAcross(synthetic.StartX + 512);
                            if (across > 2 * ordinary + 0.5) throw new Exception($"Near band: height steps {across:0.00} blocks across a partial seam tile, {ordinary:0.00} across an ordinary tile edge");
                            partialPart = $"; partial seam tile (300 blocks): {pStitched} stitched vertices within {pCrack:0.0000}, {po.Count} core vertices on the tiles, half-faded step across the seam {across:0.00} blocks (ordinary tile edge {ordinary:0.00})";

                            // A corner 1024 blocks east of the seam, about 1100 from the camera: its north-east tile missing.
                            var bw = new FarTileWindow(synthetic, FarTiles.BandLevel);
                            int lapShift = tiles.OriginLap * bw.LapTiles;
                            double cornerX = synthetic.StartX + 1024, cornerZ = Math.Round(pz / 512) * 512;
                            void Fade(double wx, double wz, float value)
                            {
                                bw.TileX(wx - synthetic.StartX, out int id, out _, out _); bw.TileZ(wz, out int row, out _);
                                id += lapShift;
                                int slot = FarTileWindow.Slot(id) + FarTileWindow.Slots * FarTileWindow.Slot(row);
                                if (tiles.Band.Slots.Ids[slot] != id || tiles.Band.Slots.Rows[slot] != row) throw new Exception("Near band: probe tile not loaded");
                                tiles.Band.Slots.Fade[slot] = value;
                            }
                            var keptFade = (float[])tiles.Band.Slots.Fade.Clone();
                            Fade(cornerX + 10, cornerZ - 10, 0f);
                            double corner = Math.Max(Probe(px, pz, (cornerX - 40, cornerZ + 8), (cornerX + 40, cornerZ + 8), "across an edge beside a missing diagonal tile"),
                                                     Probe(px, pz, (cornerX + 8, cornerZ - 40), (cornerX + 8, cornerZ + 40), "across an edge beside a missing diagonal tile"));
                            Array.Copy(keptFade, tiles.Band.Slots.Fade, keptFade.Length);
                            Fade(cornerX - 10, cornerZ + 200, 0.3f); Fade(cornerX + 10, cornerZ + 200, 0.8f);
                            double unequal = Probe(px, pz, (cornerX - 40, cornerZ + 200), (cornerX + 40, cornerZ + 200), "between tiles faded to 0.3 and 0.8");
                            Array.Copy(keptFade, tiles.Band.Slots.Fade, keptFade.Length);
                            partialPart += $"; quarter-block steps at most {corner:0.00} blocks beside a missing diagonal tile, {unequal:0.00} between unequal fades";

                            // Crossing the seam where tiles per lap (21) is not a multiple of 8: resident tiles keep their slots and fades.
                            Fill(synthetic.StartX + 100, pz);
                            var resident = tiles.Band.Slots.Keys.Select((k, i) => (k, i)).Where(p => p.k != null).ToDictionary(p => p.k.Value, p => p.i);
                            long uploads = tiles.Uploads;
                            double crossX = synthetic.EndX - 100;
                            tiles.Update(crossX, pz, FarTerrainDraw.Origin(bandMesh, crossX, pz).X, start, 64);
                            int moved = resident.Count(p => tiles.Band.Slots.Keys[p.Value] != p.Key || tiles.Band.Slots.Fade[p.Value] < 1);
                            if (tiles.OriginLap != -1 || moved > 0) throw new Exception($"Near band: crossing the seam moved or restarted {moved} resident tiles (origin lap {tiles.OriginLap}): " + string.Join(", ", resident.Where(p => tiles.Band.Slots.Keys[p.Value] != p.Key || tiles.Band.Slots.Fade[p.Value] < 1).Select(p => $"{p.Key.X},{p.Key.Z} slot {p.Value} now {tiles.Band.Slots.Keys[p.Value]?.X},{tiles.Band.Slots.Keys[p.Value]?.Z} fade {tiles.Band.Slots.Fade[p.Value]:0.00}")));
                            Fill(crossX, pz);
                            var co = Oracle(crossX, pz, info.SeaLevel + 40, "after crossing the seam");
                            partialPart += $"; crossing the seam kept all {resident.Count} resident tiles ({tiles.Uploads - uploads} new uploads), {co.Count} core vertices on the tiles after it";
                        }
                        finally
                        {
                            seamTiles?.Dispose(); tiles = earthTiles; frame.Tiles = tiles;
                            topo = t; src = sample; biomeSrc = source.Biome; tileCache.Clear();
                            frame.Topology = savedTopology; frame.ChartTexture = savedChart; frame.BlocksPerTexel = savedTexel;
                            GL.DeleteTexture(flat);
                        }
                    }

                    // A seam tile narrower than the 48-block fade: readiness reaches past it.
                    {
                        var narrow = new Topology { StartX = t.StartX, Width = 10240 + 30, Height = t.Height, Wraps = true };
                        int flat = GL.GenTexture();
                        var flatTexels = new byte[4 * 4 * 4];
                        for (int i = 0; i < 16; i++) { flatTexels[i * 4] = 255; flatTexels[i * 4 + 1] = 128; flatTexels[i * 4 + 3] = 255; }
                        Texture(flat, 4, 4, flatTexels);
                        var savedTopology = frame.Topology; int savedChart = frame.ChartTexture; double savedTexel = frame.BlocksPerTexel;
                        var earthTiles = tiles; FarTiles narrowTiles = null;
                        try
                        {
                            topo = narrow; tileCache.Clear();
                            src = (x, z) => new MapSample(info.SeaLevel + 30 + (int)Math.Round(20 * Math.Sin(2 * Math.PI * (x - narrow.StartX) / narrow.Width * 3) + 10 * Math.Cos(z / 300.0)), 0, TileData.Land);
                            biomeSrc = (x, z) => new BiomeSample(0, 128, 128);
                            frame.Topology = narrow; frame.ChartTexture = flat; frame.BlocksPerTexel = narrow.Width / 4;
                            narrowTiles = tiles = new FarTiles(narrow, info.Session, r => pending.Enqueue(r), null, 512, 1536, 8192, true, true, "000000000000");
                            tiles.CreateTextures(); frame.Tiles = tiles;
                            double nx = narrow.StartX + 800, nz = nice.Z;
                            Fill(nx, nz);
                            var nw = new FarTileWindow(narrow, FarTiles.BandLevel);
                            nw.TileZ(nz, out int row, out _);
                            // The 30-block tile half faded in, the tile west of it at 0.7, the one east of it ready.
                            void FadeId(int id, float value) { int slot = FarTileWindow.Slot(id) + FarTileWindow.Slots * FarTileWindow.Slot(row); if (tiles.Band.Slots.Ids[slot] != id) throw new Exception("Near band: narrow probe tile not loaded"); tiles.Band.Slots.Fade[slot] = value; }
                            FadeId(-1, 0.5f); FadeId(-2, 0.7f);
                            double seam = narrow.StartX;
                            double narrowStep = Probe(nx, nz, (seam - 30 - 60, nz), (seam + 60, nz), "across a 30-block seam tile");
                            partialPart += $"; across a 30-block seam tile at most {narrowStep:0.00} blocks per quarter block";
                        }
                        finally
                        {
                            narrowTiles?.Dispose(); tiles = earthTiles; frame.Tiles = tiles;
                            topo = t; src = sample; biomeSrc = source.Biome; tileCache.Clear();
                            frame.Topology = savedTopology; frame.ChartTexture = savedChart; frame.BlocksPerTexel = savedTexel;
                            GL.DeleteTexture(flat);
                        }
                    }

                    // Across Cylinder World's seam: tiles on both sides, negative laps in the shader.
                    string seamPart = "";
                    if (t.Wraps)
                    {
                        double sx = t.StartX + 150, sz = nice.Z;
                        eye = savedEye;
                        // Tile uploads keep the GL state too: filling the seam's tiles with an unusual state bound.
                        int marker = GL.GenTexture();
                        GL.ActiveTexture(TextureUnit.Texture5); GL.BindTexture(TextureTarget.Texture2D, marker); GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                        long uploadsBefore = tiles.Uploads;
                        Fill(sx, sz);
                        GL.GetInteger(GetPName.ActiveTexture, out int unitAfter); GL.GetInteger(GetPName.TextureBinding2D, out int markerAfter); GL.GetInteger(GetPName.UnpackAlignment, out int alignmentAfter);
                        GL.BindTexture(TextureTarget.Texture2D, 0); GL.ActiveTexture(TextureUnit.Texture0); GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4); GL.DeleteTexture(marker);
                        if (tiles.Uploads == uploadsBefore || unitAfter != (int)TextureUnit.Texture5 || markerAfter != marker || alignmentAfter != 1)
                            throw new Exception($"Near band: tile uploads changed the GL state ({tiles.Uploads - uploadsBefore} uploads; unit {unitAfter - (int)TextureUnit.Texture0}, texture {markerAfter}/{marker}, alignment {alignmentAfter})");
                        var (seamStitched, seamCrack) = Stitches(Capture(sx, sz, info.SeaLevel + 40), "at the seam");
                        var so = Oracle(sx, sz, info.SeaLevel + 40, "at the seam");
                        seamPart = $"; at the seam: {seamStitched} stitched vertices within {seamCrack:0.0000}, {so.Count} core vertices on the tiles";
                    }
                    var bandTimes = timings.ToArray();
                    return $"near band: {bandMesh.Rings} rings, {bandMesh.VertexCount} vertices; tiles {tiles.Band.Ready} band + {tiles.Climate.Ready} climate, sampled in median {Pct(bandTimes, 0.5):0} ms (max {bandTimes.Max():0}); " +
                        $"{stitched} stitched vertices within {crack:0.0000} blocks; {o.Count} core vertices equal their tiles (worst {o.Worst:0.00} of tolerance); " +
                        $"error against Earth's terrain: band median {bandMedian:0.00}, 95% {band95:0.00} blocks (its grid from exact heights {gridMedian:0.00}, {grid95:0.00}; mean {bias:+0.00;-0.00}); chart alone median {chartMedian:0.00}, 95% {chart95:0.00}; " +
                        $"sea G/B tinted {tinted.G / tinted.B:0.00} vs {untinted.G / untinted.B:0.00}; {climateJump:0.0000}/{bandJump:0.0000} blocks at a weight of 0.0001; {shore}; GL state kept; GPU draw {withBand:0.00} ms with the band (same mesh, chart only {chartOnly:0.00} ms; 0.3.10 mesh {before:0.00} ms){seamPart}{partialPart}";
                }
                finally
                {
                    frame.Tiles = null; frame.PlantTint = frame.WaterTint = 0; frame.Start = savedStart; eye = savedEye; frame.Canopy = 8;
                    tiles?.Dispose();
                    GL.DeleteBuffer(tf); GL.DeleteQuery(query); GL.DeleteProgram(capture);
                    if (plant != 0) GL.DeleteTexture(plant); if (waterTint != 0) GL.DeleteTexture(waterTint);
                }
            }

            int Tint(string name)
            {
                string path = Path.Combine(Path.GetDirectoryName(includes), "textures", "environment", name);
                using var bitmap = SKBitmap.Decode(path) ?? throw new Exception("Near band: cannot read " + path);
                using var rgba = bitmap.Copy(SKColorType.Rgba8888);
                int id = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, id);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, rgba.Width, rgba.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.GetPixels());
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                return id;
            }

            return $"far terrain: {variants} shader variants linked; {mesh.Rings} rings, {mesh.VertexCount} vertices, {mesh.Indices.Length / 3} triangles, {draw.Bytes / 1048576.0:0.0} MiB; coast view {below}/{belowTotal} below horizon, {distinct} tones; {edge}; {night}{lake}{seam}{band}{curve}";

            // From a boat at a 512-block view distance, the game's water fades out between 75 % and 80 % of it
            // (chunkliquid.vsh). Behind that fading water the far sea must lie just under the water surface: not above
            // it (it would cover the real water) and not far away (0.3.9 showed a sea twelve blocks down, kilometres
            // off, through the fading water: a pale band at the edge of the real sea).
            // 0.4.11: the planet's curve. From Athens, 5 blocks above the sea looking south, a flat world shows the far coasts
            // (the Cyclades, Crete) as a band along the horizon; on the planet (a radius from the ring, curving from 512 blocks,
            // a view distance) they sink by (d - 512)^2 / 2R and only high ground shows. Nothing is drawn under the far sea's
            // near edge where the flat world draws nothing: ground behind the bulge is discarded, not left below the sea.
            string Curve()
            {
                if (locate(37.75, 23.7) is not { } athens) return "planet curve: Athens is not on this map";
                double savedEye = eye; eye = info.SeaLevel + 5;
                try
                {
                    var look = new Vector3(0, 0, 1);
                    // Blocks along the view from the depth buffer (the projection's near 0.1 and far 9216).
                    double[] Along() => depths.Select(d => 2 * 0.1 * 9216 / (9216 + 0.1 - (2.0 * d - 1) * (9216 - 0.1))).ToArray();
                    frame.PlanetRadius = 0;
                    var flat = Render("curve-flat", athens.X, athens.Z, look); var flatAlong = Along();
                    frame.PlanetRadius = t.Width / (2 * Math.PI); frame.CurveFrom = 512;
                    var round = Render("curve-planet", athens.X, athens.Z, look); var roundAlong = Along();
                    int Far(int[] px, double[] along) { int n = 0; for (int i = 0; i < px.Length; i++) if (Drawn(px[i]) && along[i] > 2500) n++; return n; }
                    int flatFar = Far(flat, flatAlong), roundFar = Far(round, roundAlong), under = 0;
                    for (int i = 0; i < flat.Length; i++) if (!Drawn(flat[i]) && Drawn(round[i])) under++;
                    if (flatFar < 2000) throw new Exception($"Far terrain curve: only {flatFar} pixels past 2500 blocks from Athens on a flat world");
                    if (roundFar * 3 > flatFar) throw new Exception($"Far terrain curve: {roundFar} pixels past 2500 blocks on the planet, {flatFar} flat");
                    if (under > W / 4) throw new Exception($"Far terrain curve: {under} pixels drawn under the far sea's near edge (ground behind the bulge)");
                    return $"planet curve (radius {frame.PlanetRadius:0}): from Athens {roundFar} pixels past 2500 blocks, {flatFar} on a flat world; {under} under the sea's edge";
                }
                finally { eye = savedEye; frame.PlanetRadius = 0; frame.CurveFrom = 0; }
            }
            string SeaEdge()
            {
                // Needs open sea south of the coast: the plain profile's synthetic chart is a raised flat island there.
                if (locate == null) return "sea edge: no sea in the synthetic chart";
                const int View = 512; const double Boat = 3;
                double water = info.SeaLevel - FarTerrain.WaterBelowSeaLevel, savedStart = frame.Start, savedEye = eye;
                frame.Start = FarTerrain.StartFor(View); eye = water + Boat;
                int[] px;
                // Looking south from the coast check's sea, out over open water on Earth (the Mediterranean).
                try { px = Render("sea-edge", coast.X, coast.Z + 300, new Vector3(0, 0, 1)); }
                finally { frame.Start = savedStart; eye = savedEye; }
                float tan = MathF.Tan(MathHelper.DegreesToRadians(35)), aspect = W / (float)H;
                const double Near = 0.1, Far = 9216;
                int band = 0, sea = 0, missing = 0, far = 0, above = 0;
                double worst = 0;
                for (int y = H / 2 + 1; y < H; y++) for (int x = 0; x < W; x += 2)
                {
                    double dx = (2 * (x + 0.5) / W - 1) * tan * aspect, dy = -(2 * (y + 0.5) / H - 1) * tan;
                    double across = Math.Sqrt(1 + dx * dx);
                    double waterAt = Boat / -dy * across;
                    if (waterAt < 0.75 * View || waterAt > 0.80 * View) continue;
                    band++;
                    int i = y * W + x;
                    if (!Drawn(px[i])) { missing++; continue; }
                    double ndc = 2 * depths[i] - 1, along = 2 * Far * Near / (Far + Near - ndc * (Far - Near));
                    double hitY = Boat + dy * along, hitAt = along * across;
                    if (hitY > 0.5) continue; // far land, not sea
                    sea++;
                    if (hitAt < waterAt - 0.5) above++;
                    worst = Math.Max(worst, hitAt / waterAt);
                    if (hitAt > waterAt * 1.5) far++;
                }
                if (band == 0) throw new Exception("Far terrain sea edge: no pixels see the water's fade band");
                if (missing > 0) throw new Exception($"Far terrain sea edge: {missing}/{band} pixels behind the fading water show nothing");
                if (sea < band / 2 && locate != null) throw new Exception($"Far terrain sea edge: only {sea}/{band} pixels see sea");
                if (above > 0) throw new Exception($"Far terrain sea edge: the far sea lies above the real water at {above} pixels");
                if (far > 0) throw new Exception($"Far terrain sea edge: behind the fading water the far sea is up to {worst:0.0}× further away ({far}/{sea} pixels over 1.5×)");
                return $"sea edge {sea}/{band} behind the fading water, at most {worst:0.00}× its distance";
            }
        }
        finally
        {
            GL.UseProgram(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(color); GL.DeleteRenderbuffer(depth); GL.DeleteTexture(sky); GL.DeleteTexture(glow);
            if (program != 0) GL.DeleteProgram(program);
        }
    }

    static int Diff(int a, int b) => Math.Max(Math.Abs((a & 255) - (b & 255)), Math.Max(Math.Abs((a >> 8 & 255) - (b >> 8 & 255)), Math.Abs((a >> 16 & 255) - (b >> 16 & 255))));

    internal static void Copy(Matrix4 m, float[] to)
    {
        // OpenTK matrices are row-vector; their memory layout is what GL expects with transpose = false.
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) to[r * 4 + c] = m[r, c];
    }

    static void Texture(int id, int w, int h, byte[] rgba)
    {
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>The game's include handling (ShaderRegistry.HandleIncludes) and prefix placement (after #version).</summary>
    internal static string Expand(string code, string[] includes, string prefix)
    {
        var seen = new HashSet<string>();
        string Resolve(string text) => Regex.Replace(text, "^#include\\s+(.*)", m =>
        {
            string name = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (!seen.Add(name)) return "";
            string path = includes.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            if (path == null) throw new Exception("Missing shader include " + name);
            return Resolve(File.ReadAllText(path));
        }, RegexOptions.Multiline);
        code = Resolve(code);
        int at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
        return code.Insert(at, prefix);
    }

    internal static int Link(string vertex, string fragment, string capture = null)
    {
        int Compile(ShaderType type, string source)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, source); GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) { string log = GL.GetShaderInfoLog(s); GL.DeleteShader(s); throw new Exception($"Far terrain {type}: {log}"); }
            return s;
        }
        int vs = Compile(ShaderType.VertexShader, vertex), fs = Compile(ShaderType.FragmentShader, fragment);
        int p = GL.CreateProgram();
        GL.AttachShader(p, vs); GL.AttachShader(p, fs);
        GL.BindAttribLocation(p, 0, "vertexIn");
        if (capture != null) GL.TransformFeedbackVaryings(p, 1, new[] { capture }, TransformFeedbackMode.InterleavedAttribs);
        GL.LinkProgram(p);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) { string log = GL.GetProgramInfoLog(p); GL.DeleteProgram(p); throw new Exception("Far terrain link: " + log); }
        return p;
    }
}
