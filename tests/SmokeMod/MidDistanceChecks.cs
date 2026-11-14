using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Wmap;
using Wmap.Client;
using Wmap.Core;
using Wmap.Integrations;

/// <summary>
/// The mid-distance view (WM-P06 prototype) against generated terrain. Every profile: its four production programs compile
/// with the game's includes in every shadow, SSAO, godray and dynamic-light setting. Earth: a 6 × 6 stone tower with a lit
/// torch on top is built in the generated chunks at Nice, the 3 × 3 chunks around it are recorded with the recorder's own
/// code (ObservedTerrain.Classify and BlockLight; four-block samples, cells recorded block by block where they differ from
/// the prediction), and far terrain is rendered from them through the real RecordedTiles, FarTerrainDraw and
/// MidDistanceDraw from 600 blocks (terraces) and 1200 blocks (the smooth band and structures) out at sea. Heights read back
/// from the depth buffer are compared with the generated rain heights. At night the torch is a light from 1200 blocks, and
/// is hidden 2900 blocks away by the planet's curve (shown on a flat planet). GPU time and GL state are measured.
/// </summary>
public static class MidDistanceChecks
{
    const int W = 1280, H = 720;
    const float NearPlane = 1f, FarPlane = 9216f;

    public static void Run(ICoreServerAPI api, WorldSource source, BaseRaster raster, System.Func<double, double, (double X, double Z)?> locate, string output, Action<string> done)
    {
        bool finished = false;
        void End(string text) { if (finished) return; finished = true; done(text); }
        void Background(System.Func<string> work) => Task.Run(() =>
        {
            string result;
            try { result = work(); } catch (Exception e) { result = "FAIL mid-distance view: " + e; }
            api.Event.EnqueueMainThreadTask(() => End(result), "wmap-mid-distance-check");
        });
        var t = source.Info.Topology();
        var nice = raster != null && source.Samplers != null && !source.Info.FlatPrediction ? locate?.Invoke(43.70, 7.262) : null;
        if (nice == null) { Background(() => "Mid-distance view: " + Compile(null) + "; renders not applicable (no Earth prediction)"); return; }
        int cx = (int)nice.Value.X / 32, cz = (int)nice.Value.Z / 32, loaded = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            api.WorldManager.LoadChunkColumnPriority(cx + dx, cz + dz, new ChunkLoadOptions { OnLoaded = () => api.Event.EnqueueMainThreadTask(() => { if (++loaded == 9) Build(); }, "wmap-mid-distance-load") });
        api.Event.RegisterCallback(_ => { if (loaded < 9) End($"FAIL mid-distance view: only {loaded} of 9 chunk columns loaded"); }, 120000);

        void Build()
        {
            if (finished) return;
            Scene scene;
            try { scene = Tower(api, source, cx, cz); }
            catch (Exception e) { End("FAIL mid-distance view (building): " + e); return; }
            // The server spreads the torch's light after the block is set: record a little later.
            api.Event.RegisterCallback(_ =>
            {
                try { Record(api, source, t, scene); }
                catch (Exception e) { End("FAIL mid-distance view (recording): " + e); return; }
                Background(() => Render(output, raster, source, t, scene));
            }, 3000);
        }
    }

    /// <summary>The recorded area and what was built in it.</summary>
    sealed class Scene
    {
        public Dictionary<TileKey, TileData> Tiles = new();
        public int X0, Z0, Size;
        public int TowerX, TowerZ, TowerBase, TowerTop, Light;
        /// <summary>Per column of the area: the generated rain height's top face, and whether trees or plants stand there.</summary>
        public int[] Top; public bool[] Growth;
        public int FineCells, Samples, RecordedTop;
        public double ClassifyBytes;
        public int TopAt(int x, int z) => Top[(z - Z0) * Size + x - X0];
        public bool GrowthAt(int x, int z) => Growth[(z - Z0) * Size + x - X0];
    }

    static Scene Tower(ICoreServerAPI api, WorldSource source, int cx, int cz)
    {
        var ba = api.World.BlockAccessor;
        int sea = source.Info.SeaLevel;
        var s = new Scene { X0 = (cx - 1) * 32, Z0 = (cz - 1) * 32, Size = 96 };
        // The tower: on dry ground near the middle chunk's centre, 28 blocks above the highest ground under it.
        (int X, int Z)? site = null;
        for (int r = 0; r < 40 && site == null; r++)
        for (int dz = -r; dz <= r && site == null; dz++)
        for (int dx = -r; dx <= r && site == null; dx++)
        {
            int x = cx * 32 + 13 + dx, z = cz * 32 + 13 + dz;
            int top = ba.GetRainMapHeightAt(x, z);
            var block = ba.GetBlock(new BlockPos(x, top, z));
            if (top > sea + 1 && !block.IsLiquid() && ba.GetBlock(new BlockPos(x, top, z), BlockLayersAccess.Fluid)?.Id is null or 0) site = (x, z);
        }
        if (site == null) throw new Exception("no dry ground near Nice in the generated chunks");
        s.TowerX = site.Value.X - 2; s.TowerZ = site.Value.Z - 2;
        var stone = api.World.GetBlock(new AssetLocation("game:rock-granite")) ?? throw new Exception("no granite");
        var torch = api.World.GetBlock(new AssetLocation("game:torch-basic-lit-up")) ?? throw new Exception("no torch");
        int ground = 0;
        for (int x = s.TowerX; x < s.TowerX + 6; x++) for (int z = s.TowerZ; z < s.TowerZ + 6; z++) ground = Math.Max(ground, ba.GetRainMapHeightAt(x, z));
        s.TowerBase = ground; s.TowerTop = ground + 28;
        var pos = new BlockPos(0, 0, 0);
        for (int x = s.TowerX; x < s.TowerX + 6; x++)
        for (int z = s.TowerZ; z < s.TowerZ + 6; z++)
        for (int y = ground - 6; y <= s.TowerTop; y++) { pos.Set(x, y, z); ba.SetBlock(stone.BlockId, pos); }
        pos.Set(s.TowerX + 2, s.TowerTop + 1, s.TowerZ + 2);
        ba.SetBlock(torch.BlockId, pos);
        return s;
    }

    static void Record(ICoreServerAPI api, WorldSource source, Topology t, Scene s)
    {
        var ba = api.World.BlockAccessor;
        int sea = source.Info.SeaLevel;
        s.Light = ObservedTerrain.BlockLight(ba, new BlockPos(s.TowerX + 2, ba.GetRainMapHeightAt(s.TowerX + 2, s.TowerZ + 2), s.TowerZ + 2));
        // What the recorder allocates per sample (0.5.0-pre.2): classifying every column of the area and reading its light,
        // as ObservedTerrain.Record does with its reused position (the game's own accessor calls included).
        {
            var at = new BlockPos(0, 0, 0);
            for (int pass = 0; pass < 2; pass++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int z = s.Z0; z < s.Z0 + s.Size; z++)
                for (int x = s.X0; x < s.X0 + s.Size; x++)
                {
                    at.Set(x, ba.GetRainMapHeightAt(x, z), z);
                    var map = ba.GetMapChunk(x / 32, z / 32);
                    ObservedTerrain.Classify(ba, map.WorldGenTerrainHeightMap, (z % 32) * 32 + x % 32, at, out _);
                    ObservedTerrain.BlockLight(ba, at);
                }
                s.ClassifyBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(s.Size * s.Size);
            }
        }

        // The truth: generated rain heights (their top faces), and where trees or plants stand.
        s.Top = new int[s.Size * s.Size]; s.Growth = new bool[s.Size * s.Size];
        for (int z = 0; z < s.Size; z++)
        for (int x = 0; x < s.Size; x++)
        {
            int wx = s.X0 + x, wz = s.Z0 + z, top = ba.GetRainMapHeightAt(wx, wz);
            var block = ba.GetBlock(new BlockPos(wx, top, wz));
            s.Top[z * s.Size + x] = top + 1;
            s.Growth[z * s.Size + x] = ObservedTerrain.IsGrowth(block) || block.BlockMaterial == EnumBlockMaterial.Snow;
        }

        // Recorded as the recorder records: a four-block sample at each 4 x 4 cell's centre (four-block level); the cell
        // block by block (two-block level) where that sample differs from the prediction, or where blocks were set (the
        // game reports the tower's blocks as changed). Colours stand in for the game's (the server has no Block.GetColor);
        // light is read as the recorder reads it.
        MapSample Sample(int wx, int wz)
        {
            int top = ba.GetRainMapHeightAt(wx, wz);
            var map = ba.GetMapChunk(wx / 32, wz / 32);
            var at = new BlockPos(wx, top, wz);
            var sample = ObservedTerrain.Classify(ba, map.WorldGenTerrainHeightMap, (wz % 32) * 32 + wx % 32, at, out _);
            bool tower = wx >= s.TowerX && wx < s.TowerX + 6 && wz >= s.TowerZ && wz < s.TowerZ + 6;
            int color = tower ? 0x909090 : Semantic.IsWater(sample.Kind) ? 0x804020 : 0x30A050;
            return sample with { Color = RecordedColor.Pack(color, ObservedTerrain.BlockLight(ba, at),
                RecordingRules.Structure(sample, map.WorldGenTerrainHeightMap[(wz % 32) * 32 + wx % 32])) };
        }
        void Observe(int wx, int wz, MapSample sample, bool fine)
        {
            double x = t.Canonical(wx) - t.StartX;
            int bx = (int)Math.Floor(x);
            var (first, last) = RecordingRules.Levels(fine, t.MaxLevel);
            for (int level = first; level <= Math.Min(last, 2); level++)
            {
                if (level == 0 || !RecordingRules.Keeps(level, bx, wz, t.Width, t.Height)) continue;
                double step = 1 << level, span = TileData.Size * step;
                var key = new TileKey(1, level, (int)(x / span), (int)(wz / span));
                if (!s.Tiles.TryGetValue(key, out var tile)) { s.Tiles[key] = tile = new TileData(); RecordedColor.Mark(tile); }
                int ix = 1 + (int)(x / step) % TileData.Size, iz = 1 + (int)(wz / step) % TileData.Size, i = iz * TileData.Padded + ix;
                tile.Set(i, sample);
                if (ix == 1) tile.Set(i - 1, sample); if (ix == TileData.Size) tile.Set(i + 1, sample);
                if (iz == 1) tile.Set(i - TileData.Padded, sample); if (iz == TileData.Size) tile.Set(i + TileData.Padded, sample);
            }
        }
        for (int z = s.Z0 + 2; z < s.Z0 + s.Size; z += 4)
        for (int x = s.X0 + 2; x < s.X0 + s.Size; x += 4)
        {
            var sample = Sample(x, z);
            Observe(x, z, sample, false);
            s.Samples++;
            var p = source.Sample(t.Canonical(x + 0.5), z + 0.5);
            double predicted = Semantic.RelativeHeight(p.Height, p.Depth, p.Kind, sea);
            bool changed = x + 2 > s.TowerX && x - 2 < s.TowerX + 6 && z + 2 > s.TowerZ && z - 2 < s.TowerZ + 6;
            if (!changed && !RecordingRules.Differs(sample, sea, predicted, Semantic.IsWater(p.Kind))) continue;
            s.FineCells++;
            for (int dz = -2; dz < 2; dz++) for (int dx = -2; dx < 2; dx++) Observe(x + dx, z + dz, Sample(x + dx, z + dz), true);
        }
        // What the recordings hold at the tower.
        s.RecordedTop = int.MinValue;
        foreach (var (key, tile) in s.Tiles)
            for (int i = 0; i < TileData.Count; i++)
                if (tile.Kinds[i] != TileData.Unknown) s.RecordedTop = Math.Max(s.RecordedTop, tile.Heights[i]);
    }

    sealed class Source : RecordedTiles.ISource
    {
        readonly Dictionary<TileKey, TileData> tiles;
        public readonly HashSet<TileKey> Hidden = new();
        public int Version { get; set; }
        /// <summary>Each read of a resident tile reports a new version (a recorder changing it all the time).</summary>
        public bool Changing;
        long reads;
        public Source(Dictionary<TileKey, TileData> tiles) { this.tiles = tiles; }
        public bool Has(TileKey key) => tiles.ContainsKey(key) && !Hidden.Contains(key);
        public TileData Resident(TileKey key, out long version) { version = Changing ? ++reads : 1; return Has(key) ? tiles[key] : null; }
        public string PathFor(TileKey key) => null;
    }

    static string[] Programs = { "farterrain", "farcells", "farcellmap", "farlights" };

    /// <summary>Compiles the four programs in every setting; returns the plain variants' ids in <paramref name="plain"/>.</summary>
    static string Compile(int[] plain)
    {
        bool oldThreadCheck = GLFWProvider.CheckForMainThread;
        NativeWindow window = null;
        try
        {
            if (plain == null)
            {
                GLFWProvider.CheckForMainThread = false;
                window = Window();
            }
            var (shaders, includes) = Paths();
            int variants = 0;
            var plans = new Dictionary<string, string>();
            foreach (int shadow in new[] { 0, 1, 2 })
            foreach (int ssao in new[] { 0, 2 })
            foreach (int godrays in new[] { 0, 1 })
            foreach (int lights in new[] { 0, 10 })
            {
                string prefix = Prefix(shadow, ssao, godrays, lights);
                for (int k = 0; k < Programs.Length; k++)
                {
                    string vertex = FarTerrainChecks.Expand(File.ReadAllText(Path.Combine(shaders, Programs[k] + ".vsh")), includes, prefix);
                    string fragment = FarTerrainChecks.Expand(File.ReadAllText(Path.Combine(shaders, Programs[k] + ".fsh")), includes, prefix);
                    int p = FarTerrainChecks.Link(vertex, fragment);
                    plans[Programs[k]] = UnitPlan(Programs[k], vertex, fragment, p);
                    variants++;
                    if (plain != null && shadow == 0 && ssao == 0 && godrays == 0 && lights == 0) plain[k] = p; else GL.DeleteProgram(p);
                }
            }
            return $"{variants} program variants linked (farterrain, farcells, farcellmap, farlights); texture units: {string.Join("; ", plans.Values)}";
        }
        finally
        {
            window?.Dispose();
            if (plain == null) GLFWProvider.CheckForMainThread = oldThreadCheck;
        }
    }

    /// <summary>The game's sampler units for a program (ShaderProgram.collectUniformNames over the expanded vertex code, then
    /// the fragment code: each sampler gets the count so far), as Wmap's own system receives them.</summary>
    static Dictionary<string, int> GameUnits(params string[] stages)
    {
        var units = new Dictionary<string, int>();
        foreach (string code in stages)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code,
                "(\\s|\\r\\n)uniform\\s*(?<type>float|int|ivec2|ivec3|ivec4|vec2|vec3|vec4|sampler2DShadow|sampler2D|samplerCube|mat3|mat4x3|mat4)\\s*(\\[[\\d\\w]+\\])?\\s*(?<var>[\\d\\w]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture))
                if (m.Groups["type"].Value.Contains("sampler")) units[m.Groups["var"].Value] = units.Count;
        return units;
    }

    /// <summary>Each sampler's unit as drawn: the game's pinned by <see cref="FarTerrainDraw.PinGameSamplers"/> (the production
    /// code), Wmap's from its plan, or (<paramref name="pre3"/>) as 0.5.0-pre.3 did: five names pinned, Wmap's at 4 to 15.</summary>
    static Dictionary<string, int> Drawn(Dictionary<string, int> game, bool pre3, List<string> failures, string program)
    {
        var units = new Dictionary<string, int>(game);
        int shift;
        if (pre3)
        {
            int unit = 0;
            foreach (string name in new[] { "shadowMapFar", "shadowMapNear", "sky", "glow", "liquidDepth" })
                if (units.ContainsKey(name)) units[name] = unit++;
            shift = 0;
        }
        else shift = FarTerrainDraw.UnitBase(FarTerrainDraw.PinGameSamplers(units)) - FarTerrainDraw.LoadedUnit;
        foreach (var name in units.Keys.ToList())
        {
            if (!name.StartsWith("wmap", StringComparison.Ordinal)) continue;
            int i = Array.IndexOf(FarTerrainDraw.SamplerNames, name);
            if (i < 0) { failures?.Add($"{program}: Wmap's sampler {name} has no unit of its own"); continue; }
            units[name] = FarTerrainDraw.Units[i] + shift;
        }
        return units;
    }

    static List<string> Clashes(Dictionary<string, int> units) =>
        units.GroupBy(u => u.Value).Where(g => g.Count() > 1).Select(g => $"unit {g.Key}: {string.Join(", ", g.Select(u => u.Key))}").ToList();

    /// <summary>(0.5.0-pre.4, review round 2, A) No texture unit holds two samplers in the real program: its samplers as the game
    /// numbers them, pinned and planned by the production code, and every sampler GL finds active among them. With all five
    /// of the game's samplers (an include adding liquidDepth) and one unknown to Wmap, 0.5.0-pre.3's units clash and these do
    /// not.</summary>
    static string UnitPlan(string program, string vertex, string fragment, int linked)
    {
        var failures = new List<string>();
        var game = GameUnits(vertex, fragment);
        var drawn = Drawn(game, false, failures, program);
        foreach (var clash in Clashes(drawn)) failures.Add($"{program}: {clash}");
        GL.GetProgram(linked, GetProgramParameterName.ActiveUniforms, out int count);
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            string name = GL.GetActiveUniform(linked, i, out _, out ActiveUniformType type);
            if (!type.ToString().StartsWith("Sampler", StringComparison.Ordinal)) continue;
            active++;
            if (!drawn.ContainsKey(name)) failures.Add($"{program}: GL's active sampler {name} is not among the game's");
        }
        // Every sampler the game's includes can declare, and one more: pre.3 must clash, the plan must not.
        var all = new Dictionary<string, int>(game);
        foreach (string name in new[] { "shadowMapFar", "shadowMapNear", "sky", "glow", "liquidDepth", "someModSampler" })
            if (!all.ContainsKey(name)) all[name] = all.Count;
        var allNow = Clashes(Drawn(all, false, null, program));
        var allPre3 = Clashes(Drawn(all, true, null, program));
        if (allNow.Count > 0) failures.Add($"{program} with every game sampler: {string.Join("; ", allNow)}");
        if ((program == "farterrain" || program == "farcells") && allPre3.Count == 0) failures.Add($"{program}: 0.5.0-pre.3's units do not clash with every game sampler: the check would not catch it");
        if (failures.Count > 0) throw new Exception("Mid-distance view: texture units: " + string.Join("; ", failures));
        int pinned = drawn.Count(u => !u.Key.StartsWith("wmap", StringComparison.Ordinal));
        return $"{program} {pinned} game and {drawn.Count - pinned} Wmap samplers ({active} active), no unit shared; with every game sampler none shared (0.5.0-pre.3: {(allPre3.Count > 0 ? string.Join(", ", allPre3) : "none")})";
    }

    static NativeWindow Window()
    {
        var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false, StartFocused = false, ClientSize = new Vector2i(64, 64),
            APIVersion = new Version(3, 3), Profile = ContextProfile.Core, Flags = ContextFlags.ForwardCompatible
        });
        window.Context.MakeCurrent();
        return window;
    }

    static (string Shaders, string[] Includes) Paths()
    {
        string dir = AppContext.BaseDirectory, root = null;
        for (var d = new DirectoryInfo(Path.GetDirectoryName(typeof(MidDistanceChecks).Assembly.Location)); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Wmap.csproj"))) { root = d.FullName; break; }
        if (root == null) throw new Exception("Mid-distance check: project root not found");
        string includes = Path.Combine(AppContext.BaseDirectory, "assets", "game", "shaderincludes");
        if (!Directory.Exists(includes)) includes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory", "assets", "game", "shaderincludes");
        return (Path.Combine(root, "assets", "wmap", "shaders"), new[] { includes, Path.Combine(root, "assets", "wmap", "shaderincludes") });
    }

    static string Prefix(int shadow, int ssao, int godrays, int lights) =>
        $"#define FXAA 1\n#define SSAOLEVEL {ssao}\n#define NORMALVIEW 0\n#define BLOOM 1\n#define GODRAYS {godrays}\n#define FOAMEFFECT 1\n#define SHINYEFFECT 1\n" +
        $"#define SHADOWQUALITY {shadow}\n#define DYNLIGHTS {lights}\n#define USEOIT 1\n#define USESSBO 0\n#define WAVINGSTUFF 1\n#define MINBRIGHT 0\n#define MAXANIMATEDELEMENTS 46\n";

    static string Render(string output, BaseRaster raster, WorldSource source, Topology t, Scene scene)
    {
        bool oldThreadCheck = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        var info = source.Info;
        try
        {
            using var window = Window();
            var programs = new int[4];
            string compiled = Compile(programs);
            int terrain = programs[0], cells = programs[1], map = programs[2], glow = programs[3];
            using var renderer = new MapRenderer();
            renderer.SetBase(raster, t);
            while (!renderer.PumpBase()) { }
            int fbo = GL.GenFramebuffer(), color = GL.GenTexture(), depth = GL.GenRenderbuffer(), sky = GL.GenTexture(), skyGlow = GL.GenTexture(), query = GL.GenQuery();
            var mesh = FarMesh.Build(8192, FarMesh.NearBandCell);
            using var draw = new FarTerrainDraw();
            draw.Upload(mesh);
            using var mid = new MidDistanceDraw();
            FarTiles tiles = null;
            RecordedTiles rec = null;
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, color);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, W, H, 0, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
                GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
                GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent32f, W, H);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("incomplete framebuffer");
                var skyPixels = new byte[4 * 64];
                for (int y = 0; y < 64; y++) { float f = y / 63f; skyPixels[y * 4] = (byte)(70 + 140 * f); skyPixels[y * 4 + 1] = (byte)(120 + 100 * f); skyPixels[y * 4 + 2] = (byte)(200 + 40 * f); skyPixels[y * 4 + 3] = 255; }
                Texture(sky, 1, 64, skyPixels); Texture(skyGlow, 1, 1, new byte[4]);
                foreach (int p in programs)
                {
                    GL.UseProgram(p);
                    void U(string n, float v) { int l = GL.GetUniformLocation(p, n); if (l >= 0) GL.Uniform1(l, v); }
                    void I(string n, int v) { int l = GL.GetUniformLocation(p, n); if (l >= 0) GL.Uniform1(l, v); }
                    U("flatFogDensity", 0); U("flatFogStart", -1000); U("zNear", NearPlane); U("zFar", FarPlane); U("viewDistance", 512);
                    U("playerToSealevelOffset", 0); U("sunsetMod", 0); U("fogWaveCounter", 0);
                    I("fogSphereQuantity", 0); I("pointLightQuantity", 0); I("ditherSeed", 0); I("horizontalResolution", W);
                    I("sky", 1); I("glow", 2);
                }
                GL.ActiveTexture(TextureUnit.Texture1); GL.BindTexture(TextureTarget.Texture2D, sky);
                GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, skyGlow);
                GL.ActiveTexture(TextureUnit.Texture0);
                mid.Create();

                // Near band and climate tiles from Earth's samplers, as the atlas server would send them.
                var pending = new Queue<TileRequest>();
                var sample = source.Samplers();
                TileResponse Answer(TileRequest r)
                {
                    var key = new TileKey(0, r.Level, r.X, r.Z);
                    var response = new TileResponse { Session = r.Session, View = r.View, Level = r.Level, X = r.X, Z = r.Z, Stream = 1, Content = r.Content };
                    if (TileContent.HasSemantic(r.Content)) response.Data = SemanticTile.Pack(SemanticTile.Sample(t, key, sample, info.SeaLevel, CancellationToken.None, null, info.ChartHeightRange));
                    if (TileContent.HasBiome(r.Content)) response.Biome = BiomeTile.Pack(BiomeTile.Sample(t, key, source.Biome, CancellationToken.None));
                    return response;
                }
                tiles = new FarTiles(t, info.Session, r => pending.Enqueue(r), null, 512, 1536, 8192, true, source.Biome != null, info.BiomeId ?? "000000000000") { HeightRange = info.ChartHeightRange };
                tiles.CreateTextures();
                rec = new RecordedTiles(t, new Source(scene.Tiles));
                rec.CreateTextures();
                const int View = 512;
                double start = FarTerrain.StartFor(View), sea = info.SeaLevel - FarTerrain.WaterBelowSeaLevel;
                var frame = new FarFrame
                {
                    Topology = t, SeaLevel = info.SeaLevel, HeightScale = info.MapHeight > info.SeaLevel ? info.MapHeight - info.SeaLevel : 200, HeightRange = info.ChartHeightRange,
                    ChartTexture = renderer.BaseTexture, BlocksPerTexel = renderer.BaseTexel, Start = start, Far = 8192, Canopy = 0,
                    PlanetRadius = info.HasGeography ? t.Width / (2 * Math.PI) : 0, CurveFrom = View, Tiles = tiles, Recorded = rec, BandRadius = 1536, BandFadeStart = 1200,
                    TerraceRadius = 768, Push = 2, PushFade = 256,
                    FogColor = new[] { 0.75f, 0.82f, 0.9f, 1f }, SunPosition = new[] { 0.3f, 0.8f, -0.5f }, SunColor = new[] { 1f, 0.95f, 0.85f }, DayLight = 1
                };
                void Fill(double x, double z)
                {
                    var (ox, _) = FarTerrainDraw.Origin(mesh, x, z);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 120000)
                    {
                        tiles.Update(x, z, ox, start, 64);
                        while (pending.Count > 0) tiles.Receive(Answer(pending.Dequeue()));
                        rec.Update(x, z, ox, 1536, 768, 1536, 3072, 64);
                        if (tiles.Layers.All(l => l.Ready == l.Wanted && l.Wanted > 0) && rec.CoarseReady == rec.CoarseWanted && rec.FineReady == rec.FineWanted && (scene.Light < RecordedColor.Lit || rec.LightCount > 0)) break;
                        Thread.Sleep(10);
                    }
                    if (tiles.Layers.Any(l => l.Ready < l.Wanted) || rec.CoarseReady < rec.CoarseWanted || rec.FineReady < rec.FineWanted)
                        throw new Exception($"tiles did not load: band {string.Join("/", tiles.Layers.Select(l => $"{l.Ready}/{l.Wanted}"))}, recorded {rec.CoarseReady}/{rec.CoarseWanted} + {rec.FineReady}/{rec.FineWanted} {rec.LastError}");
                    Thread.Sleep((int)SlotTable.FadeMs + 100);
                    tiles.Update(x, z, ox, start, 64);
                    rec.Update(x, z, ox, 1536, 768, 1536, 3072, 64);
                }

                var depths = new float[W * H];
                Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), W / (float)H, NearPlane, FarPlane);
                Matrix4 viewMatrix = Matrix4.Identity;
                double eyeY = 0;
                bool withMid = true, withLights = false;
                // The production order: the cell map, the smooth mesh, terraces, structures; the lights after the scene.
                void Scene(FarFrame f)
                {
                    var saved = (f.Recorded, f.TerraceRadius);
                    if (!withMid) { f.Recorded = null; f.TerraceRadius = 0; }
                    if (f.TerraceRadius > f.Start) { mid.PlaceCellMap(f, mesh); GL.UseProgram(map); mid.DrawCellMap(map, draw, f); }
                    GL.UseProgram(terrain); draw.Draw(terrain, f);
                    if (f.TerraceRadius > f.Start || f.Recorded != null)
                    {
                        GL.UseProgram(cells);
                        mid.DrawTerraces(cells, draw, f, info.MapHeight > 0 ? info.MapHeight : 384);
                        if (f.Recorded != null) mid.DrawStructures(cells, draw, f, f.SmoothStart);
                    }
                    (f.Recorded, f.TerraceRadius) = saved;
                }
                int[] Render(string name, double x, double z, double eye, Vector3 look)
                {
                    eyeY = eye;
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                    GL.Viewport(0, 0, W, H);
                    GL.ClearColor(1f, 0f, 1f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); GL.DepthMask(true);
                    viewMatrix = Matrix4.LookAt(Vector3.Zero, look, Vector3.UnitY);
                    FarTerrainChecks.Copy(projection, frame.Projection); FarTerrainChecks.Copy(viewMatrix, frame.View);
                    frame.CameraX = x; frame.CameraY = eye; frame.CameraZ = z;
                    Scene(frame);
                    if (withLights) { GL.UseProgram(glow); mid.DrawLights(glow, frame, rec, frame.Start, 3072, W, H); }
                    GL.UseProgram(0);
                    GL.Disable(EnableCap.DepthTest);
                    var raw = new int[W * H];
                    GL.ReadPixels(0, 0, W, H, PixelFormat.Bgra, PixelType.UnsignedByte, raw);
                    var rows = new int[raw.Length]; for (int y = 0; y < H; y++) Array.Copy(raw, y * W, rows, (H - y - 1) * W, W);
                    var rawDepth = new float[W * H];
                    GL.ReadPixels(0, 0, W, H, PixelFormat.DepthComponent, PixelType.Float, rawDepth);
                    for (int y = 0; y < H; y++) Array.Copy(rawDepth, y * W, depths, (H - y - 1) * W, W);
                    using (var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, W, H))
                    {
                        Marshal.Copy(rows, 0, surface.DataPtr, rows.Length); surface.MarkDirty();
                        surface.WriteToPng(Path.Combine(output, $"mid-{name}.png"));
                    }
                    var error = GL.GetError();
                    if (error != ErrorCode.NoError) throw new Exception($"{name}: GL error {error}");
                    return rows;
                }
                const int Clear = unchecked((int)0xFFFF00FF);
                double Drop(double d) => frame.PlanetRadius > 0 ? Math.Pow(Math.Max(0, d - frame.CurveFrom), 2) / (2 * frame.PlanetRadius) : 0;
                // A frame drawn as FarTerrain draws it (cell map by its signature, smooth mesh, terraces, structures), read back.
                long productionSignature = long.MinValue;
                int[] ProductionFrame(double x, double z, double eye, Vector3 look, string name)
                {
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); GL.Viewport(0, 0, W, H);
                    GL.ClearColor(1f, 0f, 1f, 1f); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); GL.DepthMask(true);
                    viewMatrix = Matrix4.LookAt(Vector3.Zero, look, Vector3.UnitY);
                    FarTerrainChecks.Copy(projection, frame.Projection); FarTerrainChecks.Copy(viewMatrix, frame.View);
                    frame.CameraX = x; frame.CameraY = eye; frame.CameraZ = z; eyeY = eye;
                    if (frame.TerraceRadius > frame.DrawFrom)
                    {
                        mid.PlaceCellMap(frame, mesh);
                        long signature = mid.CellMapSignature(frame, mesh);
                        if (signature != productionSignature) { GL.UseProgram(map); if (mid.DrawCellMap(map, draw, frame)) productionSignature = signature; }
                    }
                    GL.UseProgram(terrain); draw.Draw(terrain, frame);
                    GL.UseProgram(cells); mid.DrawTerraces(cells, draw, frame, 384); mid.DrawStructures(cells, draw, frame, frame.SmoothStart);
                    GL.UseProgram(0); GL.Disable(EnableCap.DepthTest);
                    var raw = new int[W * H];
                    GL.ReadPixels(0, 0, W, H, PixelFormat.Bgra, PixelType.UnsignedByte, raw);
                    var px = new int[raw.Length]; for (int y = 0; y < H; y++) Array.Copy(raw, y * W, px, (H - y - 1) * W, W);
                    var rawDepth = new float[W * H];
                    GL.ReadPixels(0, 0, W, H, PixelFormat.DepthComponent, PixelType.Float, rawDepth);
                    for (int y = 0; y < H; y++) Array.Copy(rawDepth, y * W, depths, (H - y - 1) * W, W);
                    using (var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, W, H)) { Marshal.Copy(px, 0, surface.DataPtr, px.Length); surface.MarkDirty(); surface.WriteToPng(Path.Combine(output, $"mid-{name}.png")); }
                    return px;
                }
                // Blocks from a point to the nearest point of a chunk column.
                static double ColumnDistance(int cx, int cz, double x, double z)
                {
                    double ex = Math.Max(0, Math.Max(cx * 32 - x, x - (cx * 32 + 32))), ez = Math.Max(0, Math.Max(cz * 32 - z, z - (cz * 32 + 32)));
                    return Math.Sqrt(ex * ex + ez * ez);
                }
                // The world position of each drawn pixel, with the planet's drop added back.
                IEnumerable<(int I, double X, double Y, double Z)> Points(int[] px, double camX, double camZ)
                {
                    var inverse = Matrix4.Invert(viewMatrix * projection);
                    for (int py = 0; py < H; py++)
                    for (int pxl = 0; pxl < W; pxl++)
                    {
                        int i = py * W + pxl;
                        if (px[i] == Clear) continue;
                        var ndc = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 2 * depths[i] - 1, 1);
                        var w = ndc * inverse; w /= w.W;
                        double d = Math.Sqrt(w.X * w.X + w.Z * w.Z);
                        yield return (i, camX + w.X, eyeY + w.Y + Drop(d), camZ + w.Z);
                    }
                }
                int towerTopFace = scene.TowerTop + 1;
                double towerCx = scene.TowerX + 3, towerCz = scene.TowerZ + 3;
                // Heights against the generated terrain, from `distance` blocks south over the sea, looking at the tower.
                (double TowerTop, int TowerPixels, int Ground, int GroundWithin, double GroundWorst) Heights(string name, double distance)
                {
                    // From above the tower's top, so its top face is seen (from below only its walls' upper edge, a pixel thick).
                    double camZ = towerCz + distance, eye = Math.Max(sea + 60, scene.TowerTop + 40);
                    var look = Vector3.Normalize(new Vector3(0, (float)((scene.TowerTop - 10 - eye - Drop(distance)) / distance), -1));
                    var px = Render(name, towerCx, camZ, eye, look);
                    double towerTop = double.MinValue, worst = 0; int towerPixels = 0, ground = 0, within = 0;
                    foreach (var p in Points(px, towerCx, camZ))
                    {
                        if (p.X >= scene.TowerX - 0.75 && p.X < scene.TowerX + 6.75 && p.Z >= scene.TowerZ - 0.75 && p.Z < scene.TowerZ + 6.75)
                        {
                            towerPixels++; towerTop = Math.Max(towerTop, p.Y);
                            continue;
                        }
                        int bx = (int)Math.Floor(p.X), bz = (int)Math.Floor(p.Z);
                        if (bx < scene.X0 + 4 || bz < scene.Z0 + 4 || bx >= scene.X0 + scene.Size - 4 || bz >= scene.Z0 + scene.Size - 4) continue;
                        if (bx >= scene.TowerX - 3 && bx < scene.TowerX + 9 && bz >= scene.TowerZ - 3 && bz < scene.TowerZ + 9) continue;
                        int lo = int.MaxValue, hi = int.MinValue; bool growth = false;
                        for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { int top = scene.TopAt(bx + dx, bz + dz); lo = Math.Min(lo, top); hi = Math.Max(hi, top); growth |= scene.GrowthAt(bx + dx, bz + dz); }
                        // Water: the recorder keeps its surface (7/8 of a block), the rain height its top block.
                        if (growth) continue;
                        ground++;
                        double off = p.Y < lo - 1 ? lo - 1 - p.Y : p.Y > hi + 1 ? p.Y - hi - 1 : 0;
                        if (off == 0) within++;
                        worst = Math.Max(worst, off);
                    }
                    return (towerTop, towerPixels, ground, within, worst);
                }

                Fill(towerCx, towerCz + 600);
                var near = Heights("tower-600", 600);
                withMid = false;
                var nearPredicted = Heights("tower-600-predicted", 600);
                withMid = true;
                // GPU time at 600 (median of 9): everything, and each part after the parts before it.
                double Time(Action part)
                {
                    var samples = new List<double>();
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); GL.Viewport(0, 0, W, H);
                    GL.Enable(EnableCap.DepthTest);
                    // The GPU raises its clocks under load: warm up first, so each part is timed at the same clocks.
                    for (int k = 0; k < 60; k++) { GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit); part(); }
                    GL.Finish();
                    for (int k = 0; k < 9; k++)
                    {
                        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                        GL.BeginQuery(QueryTarget.TimeElapsed, query);
                        part();
                        GL.EndQuery(QueryTarget.TimeElapsed);
                        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns);
                        samples.Add(ns / 1e6);
                    }
                    GL.Disable(EnableCap.DepthTest);
                    return samples.OrderBy(v => v).ElementAt(4);
                }
                // Looking along the coast at 600 blocks out: terraces fill the view's lower part.
                Render("timing", towerCx, towerCz + 600, sea + 60, Vector3.Normalize(new Vector3(0, -0.08f, -1)));
                double gpuAll = Time(() => Scene(frame));
                // The far water's contract with Sea: both far programs declare its uniforms, and Sea's own text patch of a program
                // named farterrain (Sea 0.4.x, loaded in this world when its release is next door) leaves them alone.
                string seaContract;
                {
                    foreach (var (p, name) in new[] { (terrain, "farterrain"), (cells, "farcells") })
                        foreach (string u in new[] { "seaNightFloor", "seaSkyReflect", "seaWaveCount" })
                            if (GL.GetUniformLocation(p, u) < 0) throw new Exception($"Mid-distance view: {name} does not take Sea's {u}");
                    var (seaShaders, seaIncludes) = Paths();
                    string expanded = FarTerrainChecks.Expand(File.ReadAllText(Path.Combine(seaShaders, "farterrain.fsh")), seaIncludes, Prefix(0, 0, 0, 0));
                    var celestial = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Sea.CelestialShader")).FirstOrDefault(c => c != null);
                    var patch = celestial?.GetMethod("FarFragment");
                    if (patch == null) seaContract = "Sea's far uniforms live in both programs (Sea not loaded here)";
                    else
                    {
                        object patched = patch.Invoke(null, new object[] { expanded });
                        if (patched != null) throw new Exception("Mid-distance view: the installed Sea still text-patches the far terrain shader that declares its uniforms");
                        seaContract = $"Sea's far uniforms live in both programs; {celestial.Assembly.GetName().Name} {celestial.Assembly.GetName().Version} leaves the shader unpatched";
                    }
                }
                double gpuFar = Time(() => { withMid = false; Scene(frame); withMid = true; });
                double gpuMap = Time(() => { mid.PlaceCellMap(frame, mesh); GL.UseProgram(map); mid.DrawCellMap(map, draw, frame); });
                double gpuSmooth = Time(() => { GL.UseProgram(terrain); draw.Draw(terrain, frame); });
                double gpuTerraces = Time(() => { GL.UseProgram(cells); mid.DrawTerraces(cells, draw, frame, info.MapHeight > 0 ? info.MapHeight : 384); });
                double gpuStructures = Time(() => { GL.UseProgram(cells); mid.DrawStructures(cells, draw, frame, frame.SmoothStart); });
                int patches = mid.Patches, boxes = mid.Boxes;
                // With Sea's far water on (0.5.0-pre.2): its sky reflection and 24 wave trains, as Sea sets them.
                double gpuSea;
                {
                    var waves = new float[24 * 4];
                    for (int k = 0; k < 24; k++) { double a = k * 2.4, kk = 0.15 + 0.08 * k; waves[k * 4] = (float)(kk * Math.Cos(a)); waves[k * 4 + 1] = (float)(kk * Math.Sin(a)); waves[k * 4 + 2] = k; waves[k * 4 + 3] = 0.2f; }
                    void Sea(float reflect, int count)
                    {
                        foreach (int p in new[] { terrain, cells })
                        {
                            GL.UseProgram(p);
                            GL.Uniform1(GL.GetUniformLocation(p, "seaSkyReflect"), reflect);
                            GL.Uniform1(GL.GetUniformLocation(p, "seaWaveCount"), count);
                            if (count > 0) GL.Uniform4(GL.GetUniformLocation(p, "seaWave"), count, waves);
                        }
                    }
                    Sea(1, 24);
                    gpuSea = Time(() => Scene(frame));
                    Sea(0, 0);
                }

                // What a frame allocates (0.5.0-pre.2; the play profile found 8.3 KB per frame in far terrain): the tiles' and
                // recordings' updates, the cell map's signature and the draws, after warming up, without the test's own lists.
                double frameBytes, walkBytes, rebuildBytes;
                {
                    var (ox, _) = FarTerrainDraw.Origin(mesh, frame.CameraX, frame.CameraZ);
                    void Frame()
                    {
                        ox = FarTerrainDraw.Origin(mesh, frame.CameraX, frame.CameraZ).X;
                        tiles.Update(frame.CameraX, frame.CameraZ, ox, frame.Start);
                        rec.Update(frame.CameraX, frame.CameraZ, ox, 1536, 768, 1536, 3072);
                        mid.PlaceCellMap(frame, mesh);
                        mid.CellMapSignature(frame, mesh);
                        GL.UseProgram(terrain); draw.Draw(terrain, frame);
                        GL.UseProgram(cells); mid.DrawTerraces(cells, draw, frame, 384); mid.DrawStructures(cells, draw, frame, frame.SmoothStart);
                        GL.UseProgram(glow); mid.DrawLights(glow, frame, rec, frame.Start, 3072, W, H);
                    }
                    for (int k = 0; k < 30; k++) Frame();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int k = 0; k < 100; k++) Frame();
                    frameBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
                    // (0.5.0-pre.4) Walking: half a block a frame for 400 frames (200 blocks: the windows are worked out again every
                    // 16), as in the play profile, where far terrain allocated 6.5 KB a frame.
                    double startX = frame.CameraX;
                    before = GC.GetAllocatedBytesForCurrentThread();
                    for (int k = 0; k < 400; k++) { frame.CameraX += 0.5; Frame(); }
                    walkBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 400.0;
                    frame.CameraX = startX;
                    for (int k = 0; k < 5; k++) Frame();
                    // (0.5.0-pre.4) The boxes and lights put together again, as each time a recording nearby is taken again: into the
                    // same arrays (pre.3: a new list and array each time, the size of all boxes).
                    var rebuild = (Action)Delegate.CreateDelegate(typeof(Action), rec, typeof(RecordedTiles).GetMethod("RebuildBoxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
                    var relight = (Action)Delegate.CreateDelegate(typeof(Action), rec, typeof(RecordedTiles).GetMethod("RebuildLights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
                    rebuild(); relight();
                    int boxesBefore = rec.BoxCount, lightsBefore = rec.LightCount;
                    before = GC.GetAllocatedBytesForCurrentThread();
                    for (int k = 0; k < 20; k++) { rebuild(); relight(); }
                    rebuildBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 20.0;
                    if (rec.BoxCount != boxesBefore || rec.LightCount != lightsBefore || boxesBefore == 0) throw new Exception($"Mid-distance view: rebuilding boxes and lights changed them or found none ({boxesBefore} → {rec.BoxCount} boxes, {lightsBefore} → {rec.LightCount} lights)");
                    if (rebuildBytes > 0) throw new Exception($"Mid-distance view: putting {rec.BoxCount} boxes and {rec.LightCount} lights together again allocates {rebuildBytes:0} bytes each time");
                }

                // The tile client while a recorder keeps it full (0.5.0-pre.2; the profile found 262 KB per client tick): 128
                // resident tiles, then 256 four-block samples and a tick at a time across new ground, as the recorder sends them.
                double tickBytes;
                {
                    var channel = System.Reflection.DispatchProxy.Create<Vintagestory.API.Client.IClientNetworkChannel, FakeChannel>();
                    var tinfo = new AtlasInfo { Session = "alloc", Width = t.Width, Height = t.Height, StartX = t.StartX, Wraps = t.Wraps, CanDevelop = true, CanPredict = false, SeaLevel = info.SeaLevel };
                    string dir = Path.Combine(output, "alloc-tiles");
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    using var client = new TileClient(channel, tinfo, new WmapConfig { ResidentTiles = 128 }, dir, Path.Combine(dir, "observed"));
                    double x0 = t.StartX + 20000, z0 = 30000; int n = 0;
                    void Ticks(int count)
                    {
                        for (int k = 0; k < count; k++)
                        {
                            for (int s2 = 0; s2 < 256; s2++, n++)
                            {
                                double x = x0 + (n % 2048) * 4, z = z0 + (n / 2048) * 4;
                                client.Observe(x, z, new MapSample(120 + n % 7, 0, TileData.Land, RecordedColor.Pack(0x30A050, n % 3 == 0 ? 12 : 0)), n % 5 == 0);
                            }
                            client.Tick();
                            Thread.Sleep(1);
                        }
                    }
                    Ticks(300);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    Ticks(200);
                    tickBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 200.0;
                }

                // Changing the view distance (reported in game: after switching to 700 a pale band of sky lay between the real sea
                // and far terrain, and stayed). Over open sea south of Nice: 512, then 700 with chunk columns loaded only to 400
                // blocks (as right after the switch), 512, 256 and 512 again; far terrain starts by the production rule
                // (FarStart) and the cell map is drawn again only when its signature changes (MidDistanceDraw). Sea pixels from
                // where the real water ends (0.75 of the view distance, or the last loaded column) to the terraces' edge must
                // be drawn, and the last 512 must look like the first.
                string viewChanges;
                {
                    double sx = towerCx, sz = towerCz + 1500, eye = sea + 60;
                    Fill(sx, sz);
                    var look = Vector3.Normalize(new Vector3(0.2f, -0.12f, 1f));
                    long mapSignature = long.MinValue; int mapDraws = 0;
                    var columns = new byte[FarStart.MaskSide * FarStart.MaskSide];
                    // As Sea sets them when the game uses these programs: its sky reflection on (no waves, no night floor).
                    foreach (int p in new[] { terrain, cells }) { GL.UseProgram(p); GL.Uniform1(GL.GetUniformLocation(p, "seaSkyReflect"), 1f); }
                    int steps = 0, stepPixels = 0, worstStep = 0;
                    var saved = (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius);
                    int[] first = null; var lines = new List<string>(); int worstGap = 0;
                    foreach (var (view, loaded) in new[] { (512, double.PositiveInfinity), (700, 400.0), (512, double.PositiveInfinity), (256, double.PositiveInfinity), (512, double.PositiveInfinity) })
                    {
                        // Columns loaded out to `loaded` blocks (all, without it), as FarTerrain finds them: the mask and the fill.
                        frame.Start = FarStart.For(view); frame.CurveFrom = view;
                        frame.FillStart = FarStart.Scan((cx, cz) => ColumnDistance(cx, cz, sx, sz) < loaded, sx, sz, frame.Start, columns);
                        frame.LoadedMask = draw.UploadLoaded(columns); (frame.MaskX, frame.MaskZ) = FarStart.MaskOffset(sx, sz, sx, sz);
                        frame.TerraceRadius = 768 > frame.Start ? 768 : 0;
                        frame.PushFade = Math.Max(64, Math.Min(256, 768 - frame.Start));
                        var (ox, _) = FarTerrainDraw.Origin(mesh, sx, sz);
                        tiles.Update(sx, sz, ox, frame.DrawFrom, 64); rec.Update(sx, sz, ox, 1536, 768, 1536, 3072, 64);
                        while (pending.Count > 0) tiles.Receive(Answer(pending.Dequeue()));
                        // The production frame, through Render's own drawing but with the cell map left to its signature.
                        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo); GL.Viewport(0, 0, W, H);
                        GL.ClearColor(1f, 0f, 1f, 1f); GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                        GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Lequal); GL.DepthMask(true);
                        viewMatrix = Matrix4.LookAt(Vector3.Zero, look, Vector3.UnitY);
                        FarTerrainChecks.Copy(projection, frame.Projection); FarTerrainChecks.Copy(viewMatrix, frame.View);
                        frame.CameraX = sx; frame.CameraY = eye; frame.CameraZ = sz; eyeY = eye;
                        if (frame.TerraceRadius > frame.DrawFrom)
                        {
                            mid.PlaceCellMap(frame, mesh);
                            long signature = mid.CellMapSignature(frame, mesh);
                            if (signature != mapSignature) { GL.UseProgram(map); if (mid.DrawCellMap(map, draw, frame)) { mapSignature = signature; mapDraws++; } }
                        }
                        GL.UseProgram(terrain); draw.Draw(terrain, frame);
                        GL.UseProgram(cells); mid.DrawTerraces(cells, draw, frame, 384); mid.DrawStructures(cells, draw, frame, frame.SmoothStart);
                        GL.UseProgram(0); GL.Disable(EnableCap.DepthTest);
                        var raw = new int[W * H];
                        GL.ReadPixels(0, 0, W, H, PixelFormat.Bgra, PixelType.UnsignedByte, raw);
                        var px = new int[raw.Length]; for (int y = 0; y < H; y++) Array.Copy(raw, y * W, px, (H - y - 1) * W, W);
                        var rawDepth = new float[W * H];
                        GL.ReadPixels(0, 0, W, H, PixelFormat.DepthComponent, PixelType.Float, rawDepth);
                        for (int y = 0; y < H; y++) Array.Copy(rawDepth, y * W, depths, (H - y - 1) * W, W);
                        // Pixels drawn at the sea surface (from the depth buffer, the planet's drop added back).
                        var atSea = new bool[W * H];
                        foreach (var p in Points(px, sx, sz)) atSea[p.I] = Math.Abs(p.Y - sea) < 1.5;
                        using (var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, W, H)) { Marshal.Copy(px, 0, surface.DataPtr, px.Length); surface.MarkDirty(); surface.WriteToPng(Path.Combine(output, $"mid-view-{lines.Count}-{view}.png")); }
                        // Each pixel's ray against the sea surface: how far out it meets it.
                        var inverse = Matrix4.Invert(viewMatrix * projection);
                        // A gap: a sea pixel where far terrain should draw (past 0.75 of the view distance, or over a column not
                        // loaded: FarStart.Draws) and nothing is drawn. Loaded columns nearer are the real terrain's.
                        double edge = frame.DrawFrom + 8; int band = 0, gap = 0; var where = new List<string>();
                        var columnOffset = FarStart.MaskOffset(sx, sz, sx, sz);
                        for (int py = 0; py < H; py += 2)
                        for (int pxl = 0; pxl < W; pxl += 2)
                        {
                            var a = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), -1, 1) * inverse; a /= a.W;
                            var b = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 1, 1) * inverse; b /= b.W;
                            var dir = Vector3.Normalize(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z));
                            if (dir.Y >= -1e-4f) continue;
                            double along = (sea - eye) / dir.Y, d = along * Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
                            if (d < edge || d > 760 || !FarStart.Draws(columns, columnOffset, along * dir.X, along * dir.Z, d, frame.Start)) continue;
                            band++;
                            if (px[py * W + pxl] == Clear) { gap++; if (where.Count < 6) where.Add($"({pxl},{py}) at {d:0} blocks"); }
                        }
                        worstGap = Math.Max(worstGap, gap);
                        // The far sea is one surface across the terraces' edge: no step in colour between rows of sea from where the
                        // real water ends out to 1400 blocks (a ring showed where the terraces' water lacked Sea's reflection). The right
                        // part of this view is open sea to the horizon; the left has Nice's coast.
                        for (int py = 1; py < H; py++)
                        for (int pxl = W * 9 / 20; pxl < W; pxl += 4)
                        {
                            int c0 = px[(py - 1) * W + pxl], c1 = px[py * W + pxl];
                            if (c0 == Clear || c1 == Clear || !atSea[(py - 1) * W + pxl] || !atSea[py * W + pxl]) continue;
                            var a = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), -1, 1) * inverse; a /= a.W;
                            var b = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 1, 1) * inverse; b /= b.W;
                            var dir = Vector3.Normalize(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z));
                            if (dir.Y >= -1e-4f) continue;
                            double d = (sea - eye) / dir.Y * Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
                            if (d < edge || d > 1400) continue;
                            int step = Math.Max(Math.Abs((c0 >> 16 & 255) - (c1 >> 16 & 255)), Math.Max(Math.Abs((c0 >> 8 & 255) - (c1 >> 8 & 255)), Math.Abs((c0 & 255) - (c1 & 255))));
                            stepPixels++; worstStep = Math.Max(worstStep, step);
                            if (step > 8) steps++;
                        }
                        lines.Add($"{view}{(double.IsInfinity(loaded) ? "" : $" (loaded to {loaded})")}: start {frame.Start:0}, {gap} of {band} sea pixels undrawn{(where.Count > 0 ? " " + string.Join(", ", where) : "")}");
                        if (first == null) first = px;
                        else if (view == 512 && double.IsInfinity(loaded) && lines.Count == 5)
                        {
                            int differ = 0;
                            for (int i = 0; i < px.Length; i++) if (Math.Abs((px[i] & 255) - (first[i] & 255)) + Math.Abs((px[i] >> 8 & 255) - (first[i] >> 8 & 255)) + Math.Abs((px[i] >> 16 & 255) - (first[i] >> 16 & 255)) > 6) differ++;
                            lines.Add($"back at 512: {differ} pixels differ from the first");
                            if (differ > W * H / 500) throw new Exception($"Mid-distance view: after changing the view distance and back, {differ} pixels differ from before ({string.Join("; ", lines)})");
                        }
                    }
                    (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius) = saved;
                    frame.FillStart = double.NaN; frame.LoadedMask = 0;
                    foreach (int p in new[] { terrain, cells }) { GL.UseProgram(p); GL.Uniform1(GL.GetUniformLocation(p, "seaSkyReflect"), 0f); }
                    GL.UseProgram(0);
                    if (steps > stepPixels / 2000) throw new Exception($"Mid-distance view: the far sea has steps in colour ({steps} of {stepPixels} row pairs over 8/255, at most {worstStep})");
                    lines.Add($"the far sea with Sea's sky reflection: no row step over 8/255 in {stepPixels - steps} of {stepPixels} pairs (at most {worstStep})");
                    if (worstGap > 0) throw new Exception("Mid-distance view: sky between the real sea and far terrain after a view distance change: " + string.Join("; ", lines));
                    viewChanges = string.Join("; ", lines) + $"; the cell map drawn {mapDraws} times";
                    Fill(towerCx, towerCz + 600);
                }

                // 0.5.0-pre.3 review checks. Each fails on 0.5.0-pre.2's behaviour.
                var review = new List<string>(); var reviewFailures = new List<string>();
                // (1) One chunk column missing east of the camera: far terrain fills it, and nowhere else nearer than 0.75 of the
                // view distance. 0.5.0-pre.2 pulled far terrain's start in all round (emulated: the start at the fill, no mask).
                {
                    double sx = towerCx, sz = towerCz + 1500, eye = sea + 60;
                    Fill(sx, sz);
                    int missingX = (int)Math.Floor((sx + 250) / 32), missingZ = (int)Math.Floor(sz / 32);
                    var columns = new byte[FarStart.MaskSide * FarStart.MaskSide];
                    var saved = (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius);
                    frame.Start = FarStart.For(512); frame.CurveFrom = 512; frame.TerraceRadius = 768; frame.PushFade = 256;
                    double fill = FarStart.Scan((cx, cz) => cx != missingX || cz != missingZ, sx, sz, frame.Start, columns);
                    var offset = FarStart.MaskOffset(sx, sz, sx, sz);
                    (int Over, int Filled) Look(Vector3 look, bool pre2)
                    {
                        if (pre2) { frame.FillStart = double.NaN; frame.Start = Math.Max(48, fill); frame.LoadedMask = 0; }
                        else { frame.Start = FarStart.For(512); frame.FillStart = fill; frame.LoadedMask = draw.UploadLoaded(columns); (frame.MaskX, frame.MaskZ) = offset; }
                        var px = ProductionFrame(sx, sz, eye, look, pre2 ? "column-pre2" : "column");
                        var inverse = Matrix4.Invert(viewMatrix * projection);
                        int over = 0, filled = 0;
                        for (int py = 0; py < H; py += 2)
                        for (int pxl = 0; pxl < W; pxl += 2)
                        {
                            var a = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), -1, 1) * inverse; a /= a.W;
                            var b = new Vector4((2f * (pxl + 0.5f) / W) - 1, 1 - (2f * (py + 0.5f) / H), 1, 1) * inverse; b /= b.W;
                            var dir = Vector3.Normalize(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z));
                            if (dir.Y >= -1e-4f || px[py * W + pxl] == Clear) continue;
                            double along = (sea - eye) / dir.Y, rx = along * dir.X, rz = along * dir.Z, d = Math.Sqrt(rx * rx + rz * rz);
                            if (d >= FarStart.For(512) - 8) continue;
                            // Drawn nearer than 0.75 of the view distance: over a loaded column it covers the real terrain.
                            if (FarStart.Draws(columns, offset, rx, rz, d, FarStart.For(512))) filled++; else over++;
                        }
                        return (over, filled);
                    }
                    var west = Look(Vector3.Normalize(new Vector3(-1f, -0.15f, 0.05f)), false);
                    var east = Look(Vector3.Normalize(new Vector3(1f, -0.15f, 0.05f)), false);
                    var westPre2 = Look(Vector3.Normalize(new Vector3(-1f, -0.15f, 0.05f)), true);
                    (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius) = saved;
                    frame.FillStart = double.NaN; frame.LoadedMask = 0;
                    if (west.Over > W / 20 || east.Over > W / 20) throw new Exception($"Mid-distance view: far terrain drawn over loaded columns nearer than 0.75 of the view distance ({west.Over} looking west, {east.Over} east)");
                    if (east.Filled < 20) throw new Exception($"Mid-distance view: the missing column is not filled ({east.Filled} pixels)");
                    if (westPre2.Over < 1000) throw new Exception($"Mid-distance view: the pre.2 emulation did not cover loaded columns ({westPre2.Over}): the check would not catch it");
                    review.Add($"one column missing at {fill:0} blocks: filled with {east.Filled} pixels, {west.Over + east.Over} pixels over loaded columns (pre.2's single radius: {westPre2.Over})");
                }
                // (3) A recording that appears while the camera stands still is taken into the window (pre.2: only after moving 16 blocks).
                {
                    var hideSource = new Source(scene.Tiles);
                    var coarseKey = scene.Tiles.Keys.First(k => k.Level == RecordedTiles.CoarseLevel);
                    hideSource.Hidden.Add(coarseKey);
                    using var late = new RecordedTiles(t, hideSource);
                    var (ox, _) = FarTerrainDraw.Origin(mesh, towerCx, towerCz + 600);
                    for (int k = 0; k < 20; k++) { late.Update(towerCx, towerCz + 600, ox, 1536, 768, 1536, 3072, 64); Thread.Sleep(20); }
                    int before = late.CoarseWanted;
                    hideSource.Hidden.Clear(); hideSource.Version++;
                    late.Update(towerCx, towerCz + 600, ox, 1536, 768, 1536, 3072, 64);
                    if (late.CoarseWanted != before + 1) reviewFailures.Add($"Mid-distance view: a recording that appeared was not taken into the window ({before} then {late.CoarseWanted})");
                    review.Add($"a recording appearing with the camera still: window {before} → {late.CoarseWanted} tiles");
                }
                // (4) Recordings the recorder keeps changing are copied at most RecordedTiles.CopiesPerFrame a frame (pre.2: all at once).
                {
                    var many = new Dictionary<TileKey, TileData>();
                    var w2 = new FarTileWindow(t, RecordedTiles.CoarseLevel);
                    double camRel = t.Canonical(towerCx) - t.StartX;
                    foreach (var c in w2.Window(camRel, towerCz + 600, 3072).Take(60)) { var tile = new TileData(); RecordedColor.Mark(tile); many[c.Key with { Layer = 1 }] = tile; }
                    var changing = new Source(many) { Changing = true };
                    using var busy = new RecordedTiles(t, changing);
                    var (ox, _) = FarTerrainDraw.Origin(mesh, towerCx, towerCz + 600);
                    int most = 0, frames = 0;
                    for (; frames < 200; frames++)
                    {
                        busy.Update(towerCx, towerCz + 600, ox, 1536, 768, 1536, 3072, 64);
                        most = Math.Max(most, busy.LastCopies);
                        if (busy.LightVersion > 0 && frames > 30) break;
                        Thread.Sleep(5);
                    }
                    if (most > RecordedTiles.CopiesPerFrame) reviewFailures.Add($"Mid-distance view: {most} recordings copied in one frame");
                    review.Add($"{many.Count} changing recordings: at most {most} copied a frame");
                }
                // (Review round 2, C, 0.5.0-pre.4) New recordings while the camera stands still: the windows are worked out again at
                // most every RecordedTiles.VersionReselectMs (pre.3: at every new tile, all windows).
                {
                    var growing = new Source(scene.Tiles);
                    using var steady = new RecordedTiles(t, growing);
                    var (ox, _) = FarTerrainDraw.Origin(mesh, towerCx, towerCz + 600);
                    steady.Update(towerCx, towerCz + 600, ox, 1536, 768, 1536, 3072, 64);
                    int first = steady.Reselections;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    int updates = 0;
                    for (; updates < 150; updates++) { growing.Version++; steady.Update(towerCx, towerCz + 600, ox, 1536, 768, 1536, 3072, 64); Thread.Sleep(10); }
                    int again = steady.Reselections - first, allowed = (int)(watch.ElapsedMilliseconds / RecordedTiles.VersionReselectMs) + 2;
                    if (again > allowed) reviewFailures.Add($"Mid-distance view: {again} reselections for {updates} new recordings in {watch.ElapsedMilliseconds} ms (at most {allowed})");
                    review.Add($"a new recording every frame for {watch.ElapsedMilliseconds} ms: windows worked out {again} times (pre.3: {updates})");
                }
                // (5) The cell map is allocated with a pixel-unpack buffer left bound by another renderer (pre.2: it failed and the
                // mid-distance view switched itself off).
                {
                    int pbo = GL.GenBuffer();
                    GL.BindBuffer(BufferTarget.PixelUnpackBuffer, pbo); GL.BufferData(BufferTarget.PixelUnpackBuffer, 64, IntPtr.Zero, BufferUsageHint.StreamDraw);
                    using var fresh = new MidDistanceDraw();
                    string error = null;
                    try { fresh.Create(); } catch (Exception e) { error = e.Message; }
                    GL.GetInteger(GetPName.PixelUnpackBufferBinding, out int after);
                    GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0); GL.DeleteBuffer(pbo);
                    if (error != null || !fresh.Ready || after != pbo) reviewFailures.Add($"Mid-distance view: the cell map under a bound unpack buffer: {error ?? "not ready"}, buffer {after}/{pbo}");
                    review.Add("the cell map allocates under a bound unpack buffer and keeps it bound");
                }
                // (6) Far terrain's status line is not rebuilt every frame (pre.2: "on" every frame, then the near band's line anew).
                {
                    string status = "on"; int counts = 0;
                    for (int k = 0; k < 10; k++) { status = FarTerrain.OnStatus(status); status = FarTerrain.BandStatus(status, ref counts, 49, 49, 9, 9); }
                    string kept = status;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int k = 0; k < 1000; k++) { status = FarTerrain.OnStatus(status); status = FarTerrain.BandStatus(status, ref counts, 49, 49, 9, 9); }
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (bytes > 0 || !ReferenceEquals(kept, status)) reviewFailures.Add($"Mid-distance view: the status line is built again every frame ({bytes} bytes over 1000 frames)");
                    review.Add("the status line: 0 bytes over 1000 frames");
                }
                if (reviewFailures.Count > 0) throw new Exception(string.Join("; ", reviewFailures));
                // Cylinder World's seam: a recorded pillar just east of the seam, seen from the lap west of it (the recorded windows
                // and structures across the seam), and from the east side.
                if (t.Wraps)
                {
                    var pillar = new Dictionary<TileKey, TileData>();
                    double pz = t.Height / 2 + 1000.5; int top = info.SeaLevel + 80;
                    for (int bz = (int)pz; bz < (int)pz + 10; bz++)
                    for (int bx = 100; bx < 110; bx++)
                    for (int level = 1; level <= 2; level++)
                    {
                        if (!RecordingRules.Keeps(level, bx, bz, t.Width, t.Height)) continue;
                        double step = 1 << level, span = TileData.Size * step;
                        var key = new TileKey(1, level, (int)(bx / span), (int)(bz / span));
                        if (!pillar.TryGetValue(key, out var tile)) { pillar[key] = tile = new TileData(); RecordedColor.Mark(tile); }
                        int ix = 1 + (int)(bx / step) % TileData.Size, iz = 1 + (int)(bz / step) % TileData.Size;
                        tile.Set(iz * TileData.Padded + ix, new MapSample(top, 0, TileData.Land, RecordedColor.Pack(0x2020C0, 0, true)));
                    }
                    using var seamRec = new RecordedTiles(t, new Source(pillar));
                    seamRec.CreateTextures();
                    var saved = (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius, frame.Recorded);
                    frame.Start = FarStart.For(256); frame.CurveFrom = 256; frame.TerraceRadius = 768; frame.PushFade = 256; frame.Recorded = seamRec;
                    int Seen(double camX, Vector3 look, string name)
                    {
                        double camZ = pz + 5, eye = sea + 120;
                        Fill(camX, camZ);
                        var (ox, _) = FarTerrainDraw.Origin(mesh, camX, camZ);
                        for (int k = 0; k < 400 && (seamRec.FineReady < seamRec.FineWanted || seamRec.FineWanted == 0); k++) { seamRec.Update(camX, camZ, ox, 1536, 768, 1536, 3072, 64); Thread.Sleep(5); }
                        Thread.Sleep((int)SlotTable.FadeMs + 100);
                        seamRec.Update(camX, camZ, ox, 1536, 768, 1536, 3072, 64);
                        var px = ProductionFrame(camX, camZ, eye, look, name);
                        int seen = 0;
                        foreach (var p in Points(px, camX, camZ))
                        {
                            double rel = t.Canonical(p.X) - t.StartX;
                            if (rel >= 99.5 && rel < 110.5 && p.Z >= pz - 0.5 && p.Z < pz + 10.5 && p.Y >= top - 1.5) seen++;
                        }
                        return seen;
                    }
                    int fromWest = Seen(t.StartX + t.Width - 250, Vector3.Normalize(new Vector3(1f, -0.1f, 0f)), "seam-west");
                    int fromEast = Seen(t.StartX + 450, Vector3.Normalize(new Vector3(-1f, -0.1f, 0f)), "seam-east");
                    (frame.Start, frame.CurveFrom, frame.PushFade, frame.TerraceRadius, frame.Recorded) = saved;
                    Fill(towerCx, towerCz + 600);
                    if (fromWest < 20 || fromEast < 20) throw new Exception($"Mid-distance view: a recorded pillar by the seam is missing ({fromWest} pixels from the lap west of the seam, {fromEast} from the east)");
                    review.Add($"a recorded pillar 100 blocks past the seam: {fromWest} pixels at its top from the lap west of it, {fromEast} from the east");
                }

                // The widest ring of terraces: view distance 256, terraces from 192 blocks.
                double gpu256, gpu256Far; int patches256;
                {
                    var saved = (frame.Start, frame.CurveFrom, frame.PushFade);
                    frame.Start = FarTerrain.StartFor(256); frame.CurveFrom = 256; frame.PushFade = 256;
                    gpu256 = Time(() => Scene(frame));
                    patches256 = mid.Patches;
                    gpu256Far = Time(() => { withMid = false; frame.PushFade = 512; Scene(frame); frame.PushFade = 256; withMid = true; });
                    (frame.Start, frame.CurveFrom, frame.PushFade) = saved;
                }
                // Optional, while working on the shaders: the smooth mesh alone against another build's far terrain shaders
                // (farterrain.vsh/.fsh and wmapfartiles.ash copied to .local/ab-shaders), same view and data.
                string ab = "";
                var (shaderDir, includeDirs) = Paths();
                string abDir = Path.Combine(Path.GetDirectoryName(shaderDir), "..", "..", ".local", "ab-shaders");
                if (Directory.Exists(abDir))
                {
                    string prefix = Prefix(0, 0, 0, 0);
                    string[] abIncludes = { includeDirs[0], abDir };
                    int old = FarTerrainChecks.Link(FarTerrainChecks.Expand(File.ReadAllText(Path.Combine(abDir, "farterrain.vsh")), abIncludes, prefix), FarTerrainChecks.Expand(File.ReadAllText(Path.Combine(abDir, "farterrain.fsh")), abIncludes, prefix));
                    GL.UseProgram(old);
                    void U(string n, float v) { int l = GL.GetUniformLocation(old, n); if (l >= 0) GL.Uniform1(l, v); }
                    void I(string n, int v) { int l = GL.GetUniformLocation(old, n); if (l >= 0) GL.Uniform1(l, v); }
                    U("flatFogDensity", 0); U("flatFogStart", -1000); U("zNear", NearPlane); U("zFar", FarPlane); U("viewDistance", 512);
                    I("fogSphereQuantity", 0); I("pointLightQuantity", 0); I("horizontalResolution", W); I("sky", 1); I("glow", 2);
                    var saved = (frame.Recorded, frame.TerraceRadius, frame.Push, frame.PushFade);
                    frame.Recorded = null; frame.TerraceRadius = 0; frame.Push = 12; frame.PushFade = 512;
                    double oldTime = Time(() => { GL.UseProgram(old); draw.Draw(old, frame); });
                    double newTime = Time(() => { GL.UseProgram(terrain); draw.Draw(terrain, frame); });
                    frame.Recorded = rec;
                    double newRecorded = Time(() => { GL.UseProgram(terrain); draw.Draw(terrain, frame); });
                    var savedTiles = frame.Tiles; frame.Tiles = null; frame.Recorded = null;
                    double oldChart = Time(() => { GL.UseProgram(old); draw.Draw(old, frame); });
                    double newChart = Time(() => { GL.UseProgram(terrain); draw.Draw(terrain, frame); });
                    frame.Tiles = savedTiles;
                    (frame.Recorded, frame.TerraceRadius, frame.Push, frame.PushFade) = saved;
                    GL.DeleteProgram(old);
                    ab = $"; A/B smooth mesh alone (far terrain from 384): these shaders {newTime:0.00} ms ({newRecorded:0.00} with recordings, {newChart:0.00} chart only), .local/ab-shaders {oldTime:0.00} ms ({oldChart:0.00} chart only)";
                }

                // GL state: what the draws found is what they leave.
                int dummy = GL.GenTexture(), dummyVao = GL.GenVertexArray();
                GL.ActiveTexture(TextureUnit.Texture0 + FarTerrainDraw.CellColorUnit); GL.BindTexture(TextureTarget.Texture2D, dummy);
                GL.ActiveTexture(TextureUnit.Texture3); GL.BindVertexArray(dummyVao);
                GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); GL.DepthMask(true); GL.Viewport(3, 4, 500, 400);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                Scene(frame);
                GL.UseProgram(glow); frame.DayLight = 0.05f; mid.DrawLights(glow, frame, rec, frame.Start, 3072, W, H); frame.DayLight = 1;
                GL.GetInteger(GetPName.ActiveTexture, out int active); GL.GetInteger(GetPName.VertexArrayBinding, out int vaoAfter);
                GL.GetInteger(GetPName.BlendSrcRgb, out int src); GL.GetInteger(GetPName.BlendDstRgb, out int dst); GL.GetBoolean(GetPName.DepthWritemask, out bool mask);
                bool blendOn = GL.IsEnabled(EnableCap.Blend);
                var viewport = new int[4]; GL.GetInteger(GetPName.Viewport, viewport);
                GL.GetInteger(GetPName.DrawFramebufferBinding, out int fboAfter);
                GL.ActiveTexture(TextureUnit.Texture0 + FarTerrainDraw.CellColorUnit); GL.GetInteger(GetPName.TextureBinding2D, out int boundAfter);
                GL.ActiveTexture(TextureUnit.Texture0); GL.BindVertexArray(0); GL.Disable(EnableCap.Blend); GL.DeleteTexture(dummy); GL.DeleteVertexArray(dummyVao);
                if (active != (int)TextureUnit.Texture3 || vaoAfter != dummyVao || boundAfter != dummy || src != (int)BlendingFactor.SrcAlpha || dst != (int)BlendingFactor.OneMinusSrcAlpha || !mask || !blendOn
                    || viewport[0] != 3 || viewport[2] != 500 || fboAfter != fbo)
                    throw new Exception($"GL state changed by the mid-distance view's draws (unit {active - (int)TextureUnit.Texture0}, VAO {vaoAfter}/{dummyVao}, texture {boundAfter}/{dummy}, blend {blendOn} {src}/{dst}, depth mask {mask}, viewport {string.Join(",", viewport)}, framebuffer {fboAfter}/{fbo})");

                Fill(towerCx, towerCz + 1200);
                var far = Heights("tower-1200", 1200);
                withMid = false;
                var farPredicted = Heights("tower-1200-predicted", 1200);
                withMid = true;

                // Lights at night: the torch from 1200 blocks out at sea (5 blocks up), then 2900 blocks out, where the planet's
                // curve hides it (on a flat planet it shows). Brightest change near where the light projects, lights on - off.
                float savedDay = frame.DayLight;
                frame.DayLight = FarTerrain.SkyDaylight(0, 0.3f, 0);
                double Glow(string name, double distance)
                {
                    double camZ = towerCz + distance, eye = sea + 5, top = scene.TowerTop + 1.5 - Drop(distance);
                    var look = Vector3.Normalize(new Vector3(0, (float)((top - eye) / distance), -1));
                    withLights = false; var off = Render(name + "-off", towerCx, camZ, eye, look);
                    withLights = true; var on = Render(name, towerCx, camZ, eye, look);
                    withLights = false;
                    var clip = new Vector4((float)(scene.TowerX + 2.5 - towerCx), (float)(top - eye), (float)(scene.TowerZ + 2.5 - camZ), 1) * viewMatrix * projection;
                    int sx = (int)((clip.X / clip.W * 0.5 + 0.5) * W), sy = (int)((0.5 - clip.Y / clip.W * 0.5) * H);
                    int best = 0;
                    for (int y = Math.Max(0, sy - 8); y < Math.Min(H, sy + 9); y++)
                    for (int x = Math.Max(0, sx - 8); x < Math.Min(W, sx + 9); x++)
                    {
                        int a = on[y * W + x], b = off[y * W + x];
                        best = Math.Max(best, Math.Max((a >> 16 & 255) - (b >> 16 & 255), (a >> 8 & 255) - (b >> 8 & 255)));
                    }
                    return best;
                }
                double seen = Glow("light-1200", 1200);
                Fill(towerCx, towerCz + 2900);
                double hidden = Glow("light-2900-planet", 2900);
                // Whether the curve hides it at all: the sight line from the eye to the torch against the curved sea under it
                // (0.5.0-pre.11). On Earth's 512-block world the tower stands 92 blocks above the sea and the curve hides only up
                // to about 72 at 2900 blocks: the torch then rightly shows. Hidden where the sea rises over the line, shown where the line
                // clears it by more than 2 blocks.
                double clearance = double.MaxValue;
                {
                    double eye0 = sea + 5, top0 = scene.TowerTop + 1.5 - Drop(2900);
                    for (double x = 0; x <= 2900; x += 10) clearance = Math.Min(clearance, eye0 + (top0 - eye0) * x / 2900 - (sea - Drop(x)));
                }
                bool curveHides = clearance < 0;
                double planet = frame.PlanetRadius;
                frame.PlanetRadius = 0;
                double flat = Glow("light-2900-flat", 2900);
                frame.PlanetRadius = planet; frame.DayLight = savedDay;

                var failures = new List<string>();
                if (scene.Light < RecordedColor.Lit) failures.Add($"the torch's light read {scene.Light} at the tower's top");
                if (frameBytes > 1024) failures.Add($"far terrain allocates {frameBytes:0} bytes a frame");
                if (walkBytes > 4096) failures.Add($"far terrain allocates {walkBytes:0} bytes a frame while walking");
                if (tickBytes > 16384) failures.Add($"the tile client allocates {tickBytes:0} bytes a tick under the recorder"); 
                if (scene.RecordedTop < scene.TowerTop) failures.Add($"the recordings reach only y {scene.RecordedTop}, the tower's top block is {scene.TowerTop}");
                if (near.TowerPixels < 50 || Math.Abs(near.TowerTop - towerTopFace) > 1) failures.Add($"at 600 the tower's top is drawn at {near.TowerTop:0.0}, generated {towerTopFace} ({near.TowerPixels} pixels)");
                if (far.TowerPixels < 10 || Math.Abs(far.TowerTop - towerTopFace) > 1) failures.Add($"at 1200 the tower's top is drawn at {far.TowerTop:0.0}, generated {towerTopFace} ({far.TowerPixels} pixels)");
                if (nearPredicted.TowerPixels > 0 && nearPredicted.TowerTop > towerTopFace - 10) failures.Add($"without recordings the tower still shows at 600 ({nearPredicted.TowerTop:0.0})");
                if (near.Ground < 500 || near.GroundWithin < 0.9 * near.Ground) failures.Add($"at 600 only {near.GroundWithin} of {near.Ground} recorded ground pixels lie within a block of the generated terrain");
                if (seen < 40) failures.Add($"at night the torch shows only {seen}/255 from 1200 blocks");
                if (!curveHides && clearance > 2 && hidden < 40) failures.Add($"2900 blocks out the torch shows only {hidden}/255 though its sight line clears the curved sea by {clearance:0} blocks");
                if (curveHides && hidden > 8) failures.Add($"2900 blocks out the torch shows {hidden}/255 though the planet's curve hides it ({-clearance:0} blocks under the sea's curve)");
                if (flat < 40) failures.Add($"2900 blocks out on a flat planet the torch shows only {flat}/255");
                string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.0}%";
                string report = $"{compiled}; {seaContract}; review: {string.Join("; ", review)}; allocations: {scene.ClassifyBytes:0} bytes per recorded sample, {frameBytes:0} per far terrain frame standing and {walkBytes:0} walking, {rebuildBytes:0} per rebuild of boxes and lights, {tickBytes:0} per tile client tick with 256 samples; view distance changes: {viewChanges}; tower on ground at y {scene.TowerBase}, top face {towerTopFace}, at {scene.TowerX},{scene.TowerZ} with a torch (light {scene.Light}), {scene.Samples} four-block samples, {scene.FineCells} cells block by block, {scene.Tiles.Count} recording tiles; " +
                    $"tower top drawn at 600: {near.TowerTop:0.00} ({near.TowerPixels} px; prediction alone {(nearPredicted.TowerPixels > 0 ? nearPredicted.TowerTop.ToString("0.0") : "none")}), at 1200: {far.TowerTop:0.00} ({far.TowerPixels} px; prediction alone {(farPredicted.TowerPixels > 0 ? farPredicted.TowerTop.ToString("0.0") : "none")}), generated {towerTopFace}; " +
                    $"recorded ground within a block of the generated rain heights: at 600 {Pct(near.GroundWithin, near.Ground)} of {near.Ground} px (worst {near.GroundWorst:0.0} beyond), prediction alone {Pct(nearPredicted.GroundWithin, nearPredicted.Ground)}; at 1200 {Pct(far.GroundWithin, far.Ground)} of {far.Ground} px (worst {far.GroundWorst:0.0}), prediction alone {Pct(farPredicted.GroundWithin, farPredicted.Ground)}; " +
                    $"torch at night: {seen}/255 from 1200 blocks, {hidden}/255 from 2900 " + (curveHides ? $"behind the curve ({-clearance:0} blocks under it)" : $"in sight over the curve (the line clears the curved sea by {clearance:0} blocks)") + $", {flat}/255 there on a flat planet; " +
                    $"GPU at 600 (median of 9): {gpuAll:0.00} ms with the mid-distance view ({patches} terrace patches, {boxes} structure blocks; cell map {gpuMap:0.00}, smooth mesh {gpuSmooth:0.00}, terraces {gpuTerraces:0.00}, structures {gpuStructures:0.00} ms), {gpuSea:0.00} ms with Sea's reflection and 24 wave trains, {gpuFar:0.00} ms far terrain alone; at view distance 256 (terraces from 192): {gpu256:0.00} ms ({patches256} patches), far terrain alone {gpu256Far:0.00} ms{ab}; GL state kept";
                return failures.Count == 0 ? "Mid-distance view: PASS (" + report + ")" : "FAIL mid-distance view: " + string.Join("; ", failures) + " (" + report + ")";
            }
            finally
            {
                GL.UseProgram(0);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GL.DeleteFramebuffer(fbo); GL.DeleteTexture(color); GL.DeleteRenderbuffer(depth); GL.DeleteTexture(sky); GL.DeleteTexture(skyGlow); GL.DeleteQuery(query);
                foreach (int p in programs) if (p != 0) GL.DeleteProgram(p);
                tiles?.Dispose(); rec?.Dispose();
            }
        }
        finally { GLFWProvider.CheckForMainThread = oldThreadCheck; }
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
}
