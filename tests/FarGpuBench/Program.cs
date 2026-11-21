using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Wmap;
using Wmap.Client;
using Wmap.Core;

/// <summary>
/// Far terrain's GPU time, offscreen (WM-0054). A hidden 64 x 64 window gives an OpenGL context; each frame is drawn into a
/// 1920 x 1080 framebuffer laid out as the game's Opaque stage has it (colour, glow, SSAO's normal and position, depth), with
/// the production classes (FarTerrainDraw, MidDistanceDraw, FarTiles, MapRenderer) and shaders, in the passes FarTerrain draws
/// at view distance 384 (the author's settings: shadows 2, SSAO 1, bloom, FXAA, field of view 70), on a synthetic world of
/// 1:10 Earth's size (gentle hills and a coast; no game, no Earth). Back-face culling on, as the game leaves it before its
/// Opaque stage. Times are GPU timer queries per draw, the median of the frames after a warm-up. Each way of drawing is also
/// read back and compared with the first, pixel by pixel (colour and depth), so a faster way that draws something else
/// shows.
///   dotnet run --project tests/FarGpuBench -c Release -- [--baseline &lt;assets/wmap folder of another version&gt;] [--frames 40]
///       [--out &lt;folder for the report&gt;] [--pictures: each view and way as a 6 MB picture there]
/// Run it with no game running: it times this GPU, and a game sharing it skews the numbers.
/// </summary>
static class Program
{
    const int W = 1920, H = 1080, ViewDistance = 384, Sea = 200, MapHeight = 1024;
    const double WorldWidth = 4007936, WorldLength = 2003968, StartX = 1000000;
    static readonly double CamX = StartX + 1_200_000.5, CamZ = 700_000.5;

    static int Main(string[] args)
    {
        string root = FindRoot();
        string current = Path.Combine(root, "assets", "wmap");
        string baseline = Arg(args, "--baseline");
        string output = Arg(args, "--out") ?? Path.Combine(root, ".local", "fargpu");
        int frames = int.Parse(Arg(args, "--frames") ?? "40", CultureInfo.InvariantCulture);
        bool pictures = Array.IndexOf(args, "--pictures") >= 0;
        Directory.CreateDirectory(output);
        using var context = new Context();
        Console.WriteLine($"GPU: {GL.GetString(StringName.Renderer)}, {GL.GetString(StringName.Version)}");

        var t = new Topology { StartX = StartX, Width = WorldWidth, Height = WorldLength, Wraps = true };
        double heightRange = Semantic.HeightRangeFor(MapHeight, Sea);
        var watch = Stopwatch.StartNew();
        var (cw, ch) = BaseRaster.SizeFor(t, 8192);
        var raster = BaseRaster.Bake(t, cw, ch, Sea, () => Sample, null, Math.Max(1, Environment.ProcessorCount / 2), CancellationToken.None, null, heightRange);
        Console.WriteLine($"Chart {cw} x {ch} baked in {watch.Elapsed.TotalSeconds:0.0} s");
        using var renderer = new MapRenderer();
        renderer.SetBase(raster, t);
        while (!renderer.PumpBase()) { }

        var mesh = FarMesh.Build(8192, FarMesh.NearBandCell);
        using var draw = new FarTerrainDraw();
        draw.Upload(mesh);
        using var mid = new MidDistanceDraw();
        mid.Create();
        var pending = new Queue<TileRequest>();
        var tiles = new FarTiles(t, "bench", r => pending.Enqueue(r), null, 512, 1536, 8192, true, true, "000000000000") { HeightRange = heightRange };
        tiles.CreateTextures();

        // The frame as FarTerrain.PrepareFrame and UpdateRecorded make it at view distance 384, every column loaded.
        double groundY = Sample(CamX, CamZ).Height;
        var frame = new FarFrame
        {
            Topology = t, SeaLevel = Sea, HeightScale = MapHeight - Sea, HeightRange = heightRange, ChartTexture = renderer.BaseTexture,
            BlocksPerTexel = renderer.BaseTexel, Start = FarStart.For(ViewDistance), Far = 8192, GameFade = FarStart.GameFade(ViewDistance),
            CloudAbove = 300, PlanetRadius = WorldWidth / (2 * Math.PI), CurveFrom = ViewDistance, Tiles = tiles, Canopy = 8,
            BandRadius = 1536, BandFadeStart = 1536 - FarFrame.BandFade, TerraceRadius = 768, Push = 2,
            FogColor = new[] { 0.78f, 0.85f, 0.93f, 1f }, FogDensity = 0.0006f, FogMin = 0, HorizonFog = 0.15f,
            SunPosition = Normal(0.25f, 0.92f, 0.3f), SunColor = new[] { 1f, 0.97f, 0.9f }, DayLight = FarTerrain.SkyDaylight(1, 0, groundY + 40 - Sea),
            Ambient = new[] { 1f, 1f, 1f }, SunLevel = 0.866f, SeasonRel = 0.45f, YearRel = 0.45f,
            CameraX = CamX, CameraY = groundY + 40, CameraZ = CamZ
        };
        frame.PushFade = Math.Max(64, Math.Min(256, frame.TerraceRadius - frame.Start));
        if (frame.GameFade > frame.Start) frame.PushFade = Math.Min(frame.PushFade, Math.Max(1, frame.GameFade - frame.Start));
        var mask = new byte[FarStart.MaskSide * FarStart.MaskSide];
        double reach = Math.Max(frame.Start, frame.GameFade);
        frame.FillStart = FarStart.Scan((x, z) => true, CamX, CamZ, reach, mask);
        (frame.MaskX, frame.MaskZ) = FarStart.MaskOffset(CamX, CamZ, CamX, CamZ);
        frame.LoadedMask = draw.UploadLoaded(mask);
        frame.PlantTint = Gradient(64, 0.35f, 0.55f, 0.22f); frame.WaterTint = Gradient(64, 0.2f, 0.4f, 0.55f);

        // Near band and climate tiles, as the server would send them.
        var origin = FarTerrainDraw.Origin(mesh, CamX, CamZ);
        var fill = Stopwatch.StartNew();
        while (fill.ElapsedMilliseconds < 300_000)
        {
            tiles.Update(CamX, CamZ, origin.X, frame.DrawFrom, 64);
            while (pending.Count > 0) tiles.Receive(Answer(t, pending.Dequeue(), heightRange));
            if (tiles.Layers.All(l => l.Ready == l.Wanted && l.Wanted > 0)) break;
            Thread.Sleep(5);
        }
        Thread.Sleep((int)SlotTable.FadeMs + 100);
        tiles.Update(CamX, CamZ, origin.X, frame.DrawFrom, 64);
        Console.WriteLine($"Tiles: {string.Join(", ", tiles.Layers.Select(l => $"{l.Ready}/{l.Wanted}"))} in {fill.Elapsed.TotalSeconds:0.0} s");

        var target = new Target();
        var ways = new List<Way>();
        if (baseline != null)
        {
            var old = Programs.Compile(baseline, draw);
            ways.Add(new Way("0.5.4: three passes, gl_FragDepth in every pass", old, Passes.Three, trim: false));
            ways.Add(new Way("0.5.4 without terraces", old, Passes.Three, terraces: false, trim: false));
        }
        var now = Programs.Compile(current, draw);
        ways.Add(new Way("three passes, the cover's own program", now, Passes.Three, trim: false));
        ways.Add(new Way("three passes, cover program, trimmed mesh", now, Passes.Three));
        ways.Add(new Way("0.5.5 (FarTerrain): terraces' depth first, trimmed mesh", now, Passes.TerraceDepth));
        ways.Add(new Way("two passes, trimmed mesh", now, Passes.Two));
        ways.Add(new Way("three passes, trimmed mesh, without terraces", now, Passes.Three, terraces: false));

        var views = new (string Name, double Yaw, double Pitch)[] { ("east, level", 90, 0), ("east, 10 degrees down", 90, -10), ("west to the coast, level", 270, 0), ("north, 20 degrees down", 0, -20) };
        var report = new StringBuilder();
        report.AppendLine($"Far terrain GPU time, {W} x {H}, view distance {ViewDistance}, camera 40 blocks over the ground; median of {frames} frames (ms)");
        foreach (var view in views)
        {
            var (projection, look) = Camera(view.Yaw, view.Pitch);
            Copy(projection, frame.Projection); Copy(look, frame.View);
            report.AppendLine($"\n{view.Name}:");
            Console.WriteLine($"\n{view.Name}:");
            float[] reference = null, referenceDepth = null; string referenceName = null;
            foreach (var way in ways)
            {
                var result = way.Measure(frame, draw, mid, mesh, target, frames);
                string compared = "";
                if (way.Terraces)
                {
                    if (reference == null) { reference = result.Color; referenceDepth = result.Depth; referenceName = way.Name; compared = "(reference picture)"; }
                    else compared = Compare(reference, referenceDepth, result.Color, result.Depth);
                }
                string line = $"  {way.Name,-52} {result.Total,6:0.000}  ({result.Parts})  {compared}";
                Console.WriteLine(line); report.AppendLine(line);
                if (pictures) Save(Path.Combine(output, $"{Slug(view.Name)}-{Slug(way.Name)}.png"), result.Color);
            }
        }
        File.WriteAllText(Path.Combine(output, "report.txt"), report.ToString());
        Console.WriteLine($"\nReport{(pictures ? " and pictures" : "")} in {output}");
        tiles.Dispose();
        return 0;
    }

    // ---- the synthetic world: gentle hills to 40 blocks above the sea (as Riga's land at 1:10), a coast 2,500 blocks west ----

    static double Hash(long x, long z, int seed)
    {
        ulong h = unchecked((ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL ^ (ulong)seed * 0x165667B19E3779F9UL);
        unchecked { h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29; }
        return (h & 0xFFFFFF) / (double)0xFFFFFF;
    }
    static double Value(double x, double z, int seed)
    {
        long ix = (long)Math.Floor(x), iz = (long)Math.Floor(z);
        double fx = x - ix, fz = z - iz, u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
        double a = Hash(ix, iz, seed), b = Hash(ix + 1, iz, seed), c = Hash(ix, iz + 1, seed), d = Hash(ix + 1, iz + 1, seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }
    static double Fbm(double x, double z, int octaves, int seed)
    {
        double sum = 0, amp = 0.5, total = 0;
        for (int o = 0; o < octaves; o++) { sum += amp * Value(x, z, seed + o); total += amp; x *= 2.03; z *= 2.03; amp *= 0.5; }
        return sum / total;
    }
    static MapSample Sample(double x, double z)
    {
        double coast = (x - (CamX - 2500)) / 400 + 2 * (Fbm(x / 1200, z / 1200, 3, 40) - 0.5);
        if (coast < 0)
        {
            int bed = (int)(Sea - 3 - 25 * Math.Min(1, -coast / 4));
            return new MapSample(bed, Sea - bed, TileData.Water);
        }
        double h = Sea + 2 + 26 * Fbm(x / 2400, z / 2400, 4, 10) + 7 * Fbm(x / 160, z / 160, 3, 20) + 1.5 * Fbm(x / 20, z / 20, 2, 30);
        return new MapSample((int)Math.Round(Math.Min(h, Sea + 2 + 40 * Math.Min(1, coast))), 0, TileData.Land);
    }
    static BiomeSample Biome(double x, double z) =>
        new((byte)Math.Clamp((Fbm(x / 700, z / 700, 3, 50) - 0.35) * 600, 0, 255), 150, 165);
    static TileResponse Answer(Topology t, TileRequest r, double heightRange)
    {
        var key = new TileKey(0, r.Level, r.X, r.Z);
        var response = new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = 1, Content = r.Content };
        if (TileContent.HasSemantic(r.Content)) response.Data = SemanticTile.Pack(SemanticTile.Sample(t, key, Sample, Sea, CancellationToken.None, null, heightRange));
        if (TileContent.HasBiome(r.Content)) response.Biome = BiomeTile.Pack(BiomeTile.Sample(t, key, Biome, CancellationToken.None));
        return response;
    }

    // ---- drawing ----

    enum Passes { Three, Two, TerraceDepth }

    sealed record Result(double Total, string Parts, float[] Color, float[] Depth);

    sealed class Way
    {
        public readonly string Name; readonly Programs programs; readonly Passes passes; public readonly bool Terraces; readonly bool terracesFirst;
        readonly bool trim;
        public Way(string name, Programs programs, Passes passes, bool terraces = true, bool terracesFirst = false, bool trim = true)
        { Name = name; this.programs = programs; this.passes = passes; Terraces = terraces; this.terracesFirst = terracesFirst; this.trim = trim; }

        long stamp = 1000, cellSignature = long.MinValue;
        readonly List<(string Name, int Query)> parts = new();
        int nextQuery;
        readonly List<int> queries = new();

        int Query(string name)
        {
            if (nextQuery == queries.Count) queries.Add(GL.GenQuery());
            int q = queries[nextQuery++];
            parts.Add((name, q));
            GL.BeginQuery(QueryTarget.TimeElapsed, q);
            return q;
        }
        static void End() => GL.EndQuery(QueryTarget.TimeElapsed);

        public Result Measure(FarFrame f, FarTerrainDraw draw, MidDistanceDraw mid, FarMesh mesh, Target target, int frames)
        {
            double terraceRadius = f.TerraceRadius;
            if (!Terraces) f.TerraceRadius = 0;
            draw.Trim = trim;
            cellSignature = long.MinValue;
            var samples = new Dictionary<string, List<double>>();
            var totals = new List<double>();
            try
            {
                for (int n = 0; n < 30 + frames; n++)
                {
                    parts.Clear(); nextQuery = 0;
                    Check("before the frame");
                    target.Clear();
                    Check("clear");
                    DrawFrame(f, draw, mid, mesh);
                    Check("frame");
                    GL.Finish();
                    if (n < 30) continue;
                    double total = 0;
                    var sums = new Dictionary<string, double>();
                    foreach (var (name, q) in parts)
                    {
                        GL.GetQueryObject(q, GetQueryObjectParam.QueryResult, out long ns);
                        sums[name] = sums.GetValueOrDefault(name) + ns / 1e6;
                        total += ns / 1e6;
                    }
                    foreach (var (name, ms) in sums) (samples.TryGetValue(name, out var l) ? l : samples[name] = new()).Add(ms);
                    totals.Add(total);
                }
                var (color, depth) = target.Read();
                string text = string.Join(", ", samples.Select(s => $"{s.Key} {Median(s.Value):0.000}"));
                return new Result(Median(totals), text, color, depth);
            }
            finally { f.TerraceRadius = terraceRadius; }
        }

        /// <summary>FarTerrain.DrawFrame, with this way's passes.</summary>
        void DrawFrame(FarFrame f, FarTerrainDraw draw, MidDistanceDraw mid, FarMesh mesh)
        {
            f.Stamp = ++stamp;
            draw.BeginFrame();
            try
            {
                if (f.TerraceRadius > f.DrawFrom)
                {
                    mid.PlaceCellMap(f, mesh);
                    long signature = mid.CellMapSignature(f, mesh);
                    // The cell map is drawn again only when it moved (FarTerrain): once here, timed on its own.
                    if (signature != cellSignature)
                    {
                        GL.UseProgram(programs.Map);
                        if (mid.DrawCellMap(programs.Map, draw, f)) cellSignature = signature;
                    }
                }
                GL.Enable(EnableCap.DepthTest); GL.Enable(EnableCap.CullFace); GL.CullFace(CullFaceMode.Back); GL.Disable(EnableCap.Blend);
                if (passes == Passes.Three)
                {
                    GL.ColorMask(false, false, false, false); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    Pass(1, f, draw, mid, programs.Terrain, programs.Cells, "depth");
                    GL.ColorMask(true, true, true, true); GL.DepthMask(false); GL.DepthFunc(DepthFunction.Lequal);
                    Pass(2, f, draw, mid, programs.Terrain, programs.Cells, "colour");
                }
                else if (passes == Passes.Two)
                {
                    GL.ColorMask(true, true, true, true); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    Pass(0, f, draw, mid, programs.Terrain, programs.Cells, "colour+depth");
                }
                else
                {
                    // The terraces' depth first; then the smooth mesh's colour and depth, and the terraces' colour without depth
                    // writes (with them and the shaders' discard, every terrace fragment would be shaded before its depth test).
                    GL.ColorMask(false, false, false, false); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    f.Pass = 1;
                    if (f.TerraceRadius > f.Start) { Query("depth terraces"); GL.UseProgram(programs.Cells); mid.DrawTerraces(programs.Cells, draw, f, MapHeight); End(); }
                    GL.ColorMask(true, true, true, true); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                    f.Pass = 2;
                    Query("colour mesh"); GL.UseProgram(programs.Terrain); draw.Draw(programs.Terrain, f); End();
                    GL.DepthMask(false);
                    if (f.TerraceRadius > f.Start) { Query("colour terraces"); GL.UseProgram(programs.Cells); mid.DrawTerraces(programs.Cells, draw, f, MapHeight); End(); }
                }
                GL.ColorMask(false, false, false, false); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Less);
                Pass(3, f, draw, mid, programs.CoverTerrain, programs.CoverCells, "cover");
                GL.ColorMask(true, true, true, true); GL.DepthMask(true); GL.DepthFunc(DepthFunction.Lequal);
                f.Pass = 0;
            }
            finally { draw.EndFrame(); GL.UseProgram(0); }
        }

        /// <summary>FarTerrain.DrawPass: the smooth mesh, then the terraces (or the terraces first).</summary>
        void Pass(int pass, FarFrame f, FarTerrainDraw draw, MidDistanceDraw mid, int terrain, int cells, string name)
        {
            f.Pass = pass;
            void Mesh() { Query($"{name} mesh"); GL.UseProgram(terrain); draw.Draw(terrain, f); End(); }
            void Terraces()
            {
                if (f.TerraceRadius <= f.Start) return;
                Query($"{name} terraces"); GL.UseProgram(cells); mid.DrawTerraces(cells, draw, f, MapHeight); End();
            }
            if (terracesFirst) { Terraces(); Mesh(); }
            else { Mesh(); Terraces(); }
        }
    }

    static void Check(string where) { var e = GL.GetError(); if (e != ErrorCode.NoError) throw new Exception($"GL error {e} at {where}"); }

    static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }

    static string Compare(float[] a, float[] ad, float[] b, float[] bd)
    {
        int color = 0, depth = 0, worstAt = -1; double worst = 0;
        for (int i = 0; i < W * H; i++)
        {
            double d = 0;
            for (int c = 0; c < 3; c++) d = Math.Max(d, Math.Abs(a[i * 4 + c] - b[i * 4 + c]));
            if (d > 1.5 / 255) { color++; if (d > worst) { worst = d; worstAt = i; } }
            if (Math.Abs(ad[i] - bd[i]) > 1e-6) depth++;
        }
        return color == 0 && depth == 0 ? "same picture and depth" : $"{color} pixels of another colour (worst {worst * 255:0} of 255 at {worstAt % W},{worstAt / W}), {depth} of another depth";
    }

    // ---- shaders ----

    sealed class Programs
    {
        public int Terrain, Cells, Map, Lights, CoverTerrain, CoverCells;
        /// <summary>Per program, the game samplers pinned below Wmap's units.</summary>
        public readonly Dictionary<int, int> GameSamplers = new();

        /// <summary>The far programs from an assets/wmap folder, with the game's includes and the author's settings; the cover
        /// programs with WMAP_COVER (shaders before it ignore it). Game samplers pinned to the units below Wmap's.</summary>
        public static Programs Compile(string assets, FarTerrainDraw draw)
        {
            var pinned = new Dictionary<int, int>();
            string gameIncludes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory", "assets", "game", "shaderincludes");
            var includes = new[] { gameIncludes, Path.Combine(assets, "shaderincludes") };
            int Link(string name, string extra)
            {
                string prefix = "#define FXAA 1\n#define SSAOLEVEL 1\n#define NORMALVIEW 0\n#define BLOOM 1\n#define GODRAYS 0\n#define FOAMEFFECT 1\n#define SHINYEFFECT 1\n" +
                    "#define SHADOWQUALITY 2\n#define DYNLIGHTS 50\n#define USEOIT 1\n#define USESSBO 0\n#define WAVINGSTUFF 1\n#define MINBRIGHT 0\n#define MAXANIMATEDELEMENTS 46\n" + extra;
                string vertex = Expand(File.ReadAllText(Path.Combine(assets, "shaders", name + ".vsh")), includes, prefix);
                string fragment = Expand(File.ReadAllText(Path.Combine(assets, "shaders", name + ".fsh")), includes, prefix);
                int Shader(ShaderType type, string source)
                {
                    int s = GL.CreateShader(type);
                    GL.ShaderSource(s, source); GL.CompileShader(s);
                    GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
                    if (ok == 0) throw new Exception($"{name} {type}: {GL.GetShaderInfoLog(s)}");
                    return s;
                }
                int vs = Shader(ShaderType.VertexShader, vertex), fs = Shader(ShaderType.FragmentShader, fragment);
                int p = GL.CreateProgram();
                GL.AttachShader(p, vs); GL.AttachShader(p, fs);
                GL.LinkProgram(p);
                GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0) throw new Exception($"{name}: {GL.GetProgramInfoLog(p)}");
                // The game's samplers in declaration order on units 0, 1, ... (FarTerrainDraw.PinGameSamplers), Wmap's above.
                var units = new Dictionary<string, int>();
                foreach (Match m in Regex.Matches(vertex + "\n" + fragment, "(\\s)uniform\\s*(?<type>sampler2DShadow|sampler2D|samplerCube)\\s*(?<var>[\\w]+)"))
                    if (!units.ContainsKey(m.Groups["var"].Value)) units[m.Groups["var"].Value] = units.Count;
                int game = FarTerrainDraw.PinGameSamplers(units);
                if (!draw.PlanUnits(p, game)) throw new Exception($"{name}: texture units do not fit");
                pinned[p] = game;
                GL.UseProgram(p);
                foreach (var (sampler, unit) in units) if (!sampler.StartsWith("wmap", StringComparison.Ordinal)) Set(p, sampler, unit);
                // The game's uniforms its shader system sets when a program is used (clear noon, no point lights).
                foreach (var (n, v) in new (string, float)[] { ("flatFogDensity", 0), ("flatFogStart", -1000), ("zNear", 0.1f), ("zFar", 9216), ("viewDistance", ViewDistance),
                    ("playerToSealevelOffset", 40), ("sunsetMod", 0), ("fogWaveCounter", 0), ("shadowIntensity", 1), ("seaNightFloor", 0) })
                    Set(p, n, v);
                foreach (var (n, v) in new (string, int)[] { ("fogSphereQuantity", 0), ("pointLightQuantity", 0), ("ditherSeed", 0), ("horizontalResolution", W) })
                    Set(p, n, v);
                int lp = GL.GetUniformLocation(p, "lightPosition"); if (lp >= 0) GL.Uniform3(lp, 0.25f, 0.92f, 0.3f);
                GL.UseProgram(0);
                Check(name + " uniforms");
                return p;
            }
            var programs = new Programs
            {
                Terrain = Link("farterrain", ""), Cells = Link("farcells", ""), Map = Link("farcellmap", ""), Lights = Link("farlights", ""),
                CoverTerrain = Link("farterrain", "#define WMAP_COVER 1\n"), CoverCells = Link("farcells", "#define WMAP_COVER 1\n")
            };
            foreach (var (p, n) in pinned) programs.GameSamplers[p] = n;
            return programs;
        }
        /// <summary>Sets a scalar uniform as the type the program declares it (int, bool, sampler or float).</summary>
        static void Set(int p, string n, double v)
        {
            int l = GL.GetUniformLocation(p, n);
            if (l < 0) return;
            GL.GetProgram(p, GetProgramParameterName.ActiveUniforms, out int count);
            for (int i = 0; i < count; i++)
            {
                string name = GL.GetActiveUniform(p, i, out _, out ActiveUniformType type);
                if (name != n) continue;
                if (type == ActiveUniformType.Float) GL.Uniform1(l, (float)v);
                else if (type is ActiveUniformType.Int or ActiveUniformType.Bool || type.ToString().StartsWith("Sampler", StringComparison.Ordinal)) GL.Uniform1(l, (int)v);
                return;
            }
        }

        static string Expand(string code, string[] includes, string prefix)
        {
            var seen = new HashSet<string>();
            string Resolve(string text) => Regex.Replace(text, "^#include\\s+(.*)", m =>
            {
                string name = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (!seen.Add(name)) return "";
                string path = includes.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists) ?? throw new Exception("Missing shader include " + name);
                return Resolve(File.ReadAllText(path));
            }, RegexOptions.Multiline);
            code = Resolve(code);
            int at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
            return code.Insert(at, prefix);
        }
    }

    // ---- the target: the Opaque stage's framebuffer ----

    sealed class Target
    {
        readonly int fbo, depth;
        readonly int[] color = new int[4];
        public Target()
        {
            fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            var formats = new[] { PixelInternalFormat.Rgba16f, PixelInternalFormat.Rgba8, PixelInternalFormat.Rgba16f, PixelInternalFormat.Rgba16f };
            for (int i = 0; i < 4; i++)
            {
                color[i] = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, color[i]);
                GL.TexImage2D(TextureTarget.Texture2D, 0, formats[i], W, H, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D, color[i], 0);
            }
            depth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, W, H);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            GL.DrawBuffers(4, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 });
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete) throw new Exception("framebuffer: " + status);
        }
        public void Clear()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.Viewport(0, 0, W, H);
            GL.ColorMask(true, true, true, true); GL.DepthMask(true);
            GL.ClearColor(0.55f, 0.7f, 0.9f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }
        public (float[] Color, float[] Depth) Read()
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            var c = new float[W * H * 4];
            GL.ReadPixels(0, 0, W, H, PixelFormat.Rgba, PixelType.Float, c);
            var d = new float[W * H];
            GL.ReadPixels(0, 0, W, H, PixelFormat.DepthComponent, PixelType.Float, d);
            var error = GL.GetError();
            if (error != ErrorCode.NoError) throw new Exception("GL error " + error);
            return (c, d);
        }
    }

    // ---- helpers ----

    static (Matrix4 Projection, Matrix4 View) Camera(double yaw, double pitch)
    {
        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, 0.1f, 9216);
        double y = MathHelper.DegreesToRadians(yaw), p = MathHelper.DegreesToRadians(pitch);
        var look = new Vector3((float)(Math.Sin(y) * Math.Cos(p)), (float)Math.Sin(p), (float)(-Math.Cos(y) * Math.Cos(p)));
        return (projection, Matrix4.LookAt(Vector3.Zero, look, Vector3.UnitY));
    }
    static void Copy(Matrix4 m, float[] to) { for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) to[r * 4 + c] = m[r, c]; }
    static float[] Normal(float x, float y, float z) { float l = MathF.Sqrt(x * x + y * y + z * z); return new[] { x / l, y / l, z / l }; }
    static int Gradient(int size, float r, float g, float b)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4; float f = 0.7f + 0.6f * x / size;
            px[i] = (byte)Math.Clamp(255 * r * f, 0, 255); px[i + 1] = (byte)Math.Clamp(255 * g * f, 0, 255); px[i + 2] = (byte)Math.Clamp(255 * b * f, 0, 255); px[i + 3] = 255;
        }
        int id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return id;
    }
    static void Save(string path, float[] rgba)
    {
        // No PNG writer without the game's Cairo: a binary PPM next to the report.
        path = Path.ChangeExtension(path, ".ppm");
        using var s = File.Create(path);
        var header = Encoding.ASCII.GetBytes($"P6\n{W} {H}\n255\n");
        s.Write(header);
        var row = new byte[W * 3];
        for (int y = H - 1; y >= 0; y--)
        {
            for (int x = 0; x < W; x++) for (int c = 0; c < 3; c++) row[x * 3 + c] = (byte)Math.Clamp(rgba[(y * W + x) * 4 + c] * 255, 0, 255);
            s.Write(row);
        }
    }
    static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    static string Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Wmap.csproj"))) return d.FullName;
        throw new Exception("Wmap.csproj not found above " + AppContext.BaseDirectory);
    }

    /// <summary>An OpenGL context on a hidden window (WGL, as tests/PlaytestTests' MapGpu): the highest the driver offers.</summary>
    sealed class Context : IDisposable, IBindingsContext
    {
        readonly IntPtr window, dc, rc;
        static readonly IntPtr gl = NativeLibrary.Load("opengl32.dll");
        public Context()
        {
            window = CreateWindowEx(0, "STATIC", "Wmap far terrain bench", 0, 0, 0, 64, 64, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            dc = GetDC(window);
            var p = new PixelFormatDescriptor { Size = 40, Version = 1, Flags = 0x24, ColorBits = 32, DepthBits = 24 };
            int format = ChoosePixelFormat(dc, ref p);
            if (format == 0 || !SetPixelFormat(dc, format, ref p)) throw new Exception("No pixel format");
            rc = wglCreateContext(dc);
            if (!wglMakeCurrent(dc, rc)) throw new Exception("Cannot create an OpenGL context");
            GL.LoadBindings(this);
        }
        public IntPtr GetProcAddress(string name)
        {
            var address = wglGetProcAddress(name);
            if (address != IntPtr.Zero && address.ToInt64() != -1 && address.ToInt64() > 3) return address;
            return NativeLibrary.TryGetExport(gl, name, out address) ? address : IntPtr.Zero;
        }
        public void Dispose() { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); ReleaseDC(window, dc); DestroyWindow(window); }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PixelFormatDescriptor
    {
        public ushort Size, Version; public uint Flags;
        public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift,
            AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor p);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor p);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
}
