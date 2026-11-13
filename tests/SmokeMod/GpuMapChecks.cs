using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Wmap;
using Wmap.Client;
using Wmap.Core;
using Wmap.Integrations;

/// <summary>
/// Renders the production map shader through MapRenderer and TileAtlas in a hidden OpenGL 3.3 core
/// context, with the world raster baked from the real terrain provider, and checks the pixels.
/// </summary>
public static class GpuMapChecks
{
    const int W = 1600, H = 900;

    public static string Run(string output, WorldSource source, BaseRaster raster, Func<double, double, GeoPosition> geography, Func<double, double, (double X, double Z)?> locate, float[] winds = null, (float[] Field, double X, double Z, double Range)? barometer = null, IReadOnlyList<SunChecks.Map> sunMaps = null)
    {
        bool oldThreadCheck = GLFWProvider.CheckForMainThread;
        var t = source.Info.Topology();
        double heightRange = source.Info.ChartHeightRange;
        if (raster == null)
        {
            heightRange = Semantic.DefaultHeightRange;
            // Plain worlds have no world chart: still compile and exercise the shader with a synthetic island.
            t = new Topology { Width = 8192, Height = 4096, Wraps = true };
            raster = BaseRaster.Bake(t, 1024, 512, 110, () => (x, z) => Math.Abs(x - 4096) < 1500 && Math.Abs(z - 2048) < 900 ? new MapSample(150, 0, TileData.Land) : new MapSample(80, 29, TileData.Water), null, 2, CancellationToken.None);
            geography = null;
        }
        try
        {
            GLFWProvider.CheckForMainThread = false;
            using var window = new NativeWindow(new NativeWindowSettings {
                StartVisible = false, StartFocused = false, ClientSize = new Vector2i(64, 64),
                APIVersion = new Version(3, 3), Profile = ContextProfile.Core, Flags = ContextFlags.ForwardCompatible
            });
            window.Context.MakeCurrent();
            string gpu = GL.GetString(StringName.Renderer);
            using var renderer = new MapRenderer();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            renderer.SetBase(raster, t);
            if (renderer.Error != null) throw new Exception("Map shader: " + renderer.Error);
            int pumps = 1;
            while (!renderer.PumpBase()) pumps++;
            double uploadMs = watch.Elapsed.TotalMilliseconds;
            if (geography != null) renderer.SetGeography(GeoGrid.Build(geography, t), GeoGrid.Width, GeoGrid.Height, t.Wraps);
            int fbo = GL.GenFramebuffer(), target = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, target);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, W, H, 0, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("Incomplete GPU test framebuffer");
            // GUI pixels: origin top-left, like the game's orthographic GUI projection.
            float[] projection = { 2f / W, 0, 0, 0, 0, -2f / H, 0, 0, 0, 0, -0.01f, 0, -1, 1, 0, 1 };
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

            var channel = DispatchProxy.Create<IClientNetworkChannel, FakeChannel>();
            var info = new AtlasInfo { Session = "gpu", Width = t.Width, Height = t.Height, StartX = t.StartX, Wraps = t.Wraps, CanDevelop = true, CanPredict = true, HasOverview = true, SeaLevel = source.Info.SeaLevel, HeightRange = heightRange };
            string cache = Path.Combine(output, "gpu-tiles");
            using var tiles = new TileClient(channel, info, new WmapConfig(), cache, Path.Combine(output, "gpu-observed"));
            using var atlas = new TileAtlas(tiles, 4);
            var mask = new ExploredMask(t);
            var results = new List<string>();
            results.Add(DetailZoomChecks.Run(output, W, H));

            if (winds != null && t.Width != source.Info.Width) winds = null;   // synthetic world: the wind grid belongs to the real one
            if (winds != null) renderer.SetWind(winds, WindGrid.Width, WindGrid.Height, t.Wraps);
            int[] Render(string name, double x, double z, double scale, int style, bool development, bool detail, bool wind = false, bool dayNight = false)
            {
                var camera = new MapCamera { X = x, Z = z, Scale = scale };
                var frame = new MapFrame { X = 0, Y = 0, Width = W, Height = H, Camera = camera, Topology = t, Style = style, Development = development, Gui = 1,
                    HeightScale = source.Info.MapHeight > source.Info.SeaLevel ? source.Info.MapHeight - source.Info.SeaLevel : 200, HeightRange = heightRange,
                    DayNight = dayNight, MeridianStep = GeoGrid.Step(t.Width / 360 * scale, 110), ParallelStep = GeoGrid.Step((info.PoleDistance > 0 ? info.PoleDistance / 90 : t.Height / 180) * scale, 110) };
                var visible = new List<TileInstance>();
                if (detail && source.Sample != null)
                {
                    int level = ViewTiles.Select(visible, t, camera, W, H, new ViewPolicy(renderer.BaseTexel, true, false, _ => false, 600));
                    if (level < 0 || visible.Count == 0) throw new Exception("No detail tiles at local zoom");
                    var keys = visible.Select(v => v.Key).Distinct().ToArray();
                    // Seed the client cache as the server would, then load through the real client and atlas.
                    // One sampler per worker, as the server does: providers keep per-thread caches.
                    Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 8 }, source.Samplers, (key, _, sample) =>
                    {
                        string file = Path.Combine(cache, key.FileName);
                        if (!File.Exists(file)) TileCodec.WriteAtomic(file, SemanticTile.Pack(SemanticTile.Sample(t, key, sample, info.SeaLevel, CancellationToken.None, null, info.ChartHeightRange)));
                        return sample;
                    }, _ => { });
                    tiles.SetView(keys);
                    for (int n = 0; n < 6000 && keys.Any(k => tiles.Get(k) == null || tiles.Get(k).Busy); n++) { tiles.Tick(); Thread.Sleep(2); }
                    atlas.Upload(keys, 10000);
                    if (keys.Count(atlas.Has) < keys.Length * 0.9) throw new Exception($"Detail atlas incomplete: {keys.Count(atlas.Has)}/{keys.Length}");
                }
                renderer.UpdateMask(mask);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.Viewport(0, 0, W, H);
                GL.Disable(EnableCap.DepthTest);
                GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
                if (!renderer.Begin(projection, identity, frame, !development)) throw new Exception("Renderer did not start: " + renderer.Error);
                renderer.DrawWorld(50);
                int drawn = 0;
                float fade = (float)Math.Clamp(renderer.BaseTexel / 2 * scale - 1, 0, 1);
                foreach (var tile in ViewTiles.Display(visible, atlas.Has)) { atlas.Draw(tile, renderer, fade, 50.05f + 0.01f * drawn++); }
                if (wind) renderer.DrawWinds(57, true);
                renderer.DrawMarker(W / 2.0, H / 2.0, 0.7, 9, 58);
                renderer.DrawCompass(18 + 64, H - 18 - 64, 64, 59);
                renderer.End();
                var error = GL.GetError();
                if (error != ErrorCode.NoError) throw new Exception($"OpenGL error in {name}: {error}");
                var pixels = Read(W, H);
                Save(Path.Combine(output, $"gpu-{name}.png"), pixels);
                results.Add(detail ? $"{name} ({drawn} detail tiles)" : name);
                return pixels;
            }

            static (int r, int g, int b, int a) Rgb(int p) => ((p >> 16) & 255, (p >> 8) & 255, p & 255, (p >> 24) & 255);
            double fitScale = Math.Min(W / t.Width, H / t.Height) * 0.96;
            var center = (X: t.StartX + t.Width / 2, Z: t.Height / 2);
            var world = Render("world-chart", center.X, center.Z, fitScale, 0, true, false);
            if (world.Count(p => Rgb(p).a == 255) < W * H * 0.5) throw new Exception("Whole-world chart largely transparent");
            var terrain = Render("world-terrain", center.X, center.Z, fitScale, 1, true, false);
            if (geography != null && t.Wraps) results.Add(SeamGraticule(Render, renderer, geography, t));
            if (source.Biome != null && locate != null)
            {
                // Terrain colours by climate (Earth Map): the Sahara's lowlands are sand, the Congo's forest is green. By
                // height alone (0.4.2) both were the green of low land.
                var (cw, ch) = ClimateRaster.SizeFor(raster.Width, raster.Height);
                var climateWatch = System.Diagnostics.Stopwatch.StartNew();
                var climate = ClimateRaster.Unpack(ClimateRaster.Pack(ClimateRaster.Bake(t, cw, ch, source.Biome, 8, CancellationToken.None), cw, ch));
                double climateMs = climateWatch.Elapsed.TotalMilliseconds;
                // The same raster baked on one thread: identical, or the biome source is not safe to share between threads
                // (rows of a parallel bake came out wrong: green lines across Africa and Siberia, reported in game).
                var single = ClimateRaster.Bake(t, cw, ch, source.Biome, 1, CancellationToken.None);
                int differ = 0; var rows = new HashSet<int>();
                for (int i = 0; i < single.Length; i++) if (single[i] != climate.Rgba[i]) { differ++; rows.Add(i / 4 / cw); }
                if (differ > 0) throw new Exception($"Climate raster: a parallel bake differs from a single-threaded one in {differ} bytes on {rows.Count} rows (first rows {string.Join(", ", rows.OrderBy(r => r).Take(8))})");
                // For the browser shader preview (tools/shader-preview), next to the world raster.
                using (var file = File.Create(Path.Combine(output, $"climate-{cw}x{ch}.bin.gz")))
                using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest)) gz.Write(climate.Rgba);
                (int r, int g, int b) Before(double lat, double lon) => At(terrain, lat, lon);
                (int r, int g, int b) At(int[] img, double lat, double lon)
                {
                    var p = locate(lat, lon).Value;
                    int px = (int)(W / 2 + (t.Nearest(p.X, center.X) - center.X) * fitScale), py = (int)(H / 2 + (p.Z - center.Z) * fitScale);
                    int r = 0, g = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var c = Rgb(img[(py + dy) * W + px + dx]); r += c.r; g += c.g; b += c.b; }
                    return (r / 9, g / 9, b / 9);
                }
                renderer.SetClimate(climate, t.Wraps);
                var climateTerrain = Render("world-terrain-climate", center.X, center.Z, fitScale, 1, true, false);
                // Low desert (Sirte in Libya, the Grand Erg Oriental, the Western Desert): green by height, sand by climate.
                var lowDeserts = new[] { (30.0, 17.0), (30.5, 7.5), (27.0, 25.0) };
                int greenBefore = 0;
                var sahara = (r: 0, g: 0, b: 0); var saharaBefore = sahara;
                foreach (var (lat, lon) in lowDeserts)
                {
                    var before = Before(lat, lon); var after = At(climateTerrain, lat, lon);
                    if (before.g <= before.r) continue;
                    greenBefore++; sahara = after; saharaBefore = before;
                    if (after.r < after.g || after.b > after.g - 20) throw new Exception($"Climate colours: low desert at {lat} N {lon} E is not sand-coloured ({after}, was {before})");
                }
                if (greenBefore == 0) throw new Exception("Climate colours: none of the check's low deserts was green by height; pick other places");
                var congo = At(climateTerrain, 0.5, 22.0);
                if (congo.g < congo.r + 15) throw new Exception($"Climate colours: the Congo is not green ({congo})");
                Render("region-terrain-climate", locate(33, 3)?.X ?? center.X, locate(33, 3)?.Z ?? center.Z, 1 / 18.0, 1, true, false);
                results.Add($"climate colours ({cw}x{ch} raster in {climateMs:0} ms; {greenBefore} low deserts turned from green to sand, e.g. {sahara} was {saharaBefore}; Congo {congo})");
            }
            {
                // Day and night: the western half of the world in daylight, the eastern half in night. The night half
                // darkens, the day half keeps its colours, and the terminator between them is drawn.
                var day = new byte[DayGrid.Width * DayGrid.Height];
                for (int i = 0; i < day.Length; i++) day[i] = (byte)(i % DayGrid.Width < DayGrid.Width / 2 ? 255 : 0);
                renderer.SetDay(day, DayGrid.Width, DayGrid.Height, t.Wraps);
                var shaded = Render("world-daynight", center.X, center.Z, fitScale, 0, true, false, dayNight: true);
                int dayW = (int)(t.Width * fitScale), left = (W - dayW) / 2;
                double Brightness(int[] img, double from, double to)
                {
                    double sum = 0; int n = 0;
                    for (int y = H / 2 - 100; y < H / 2 + 100; y += 2)
                    for (int x = left + (int)(dayW * from); x < left + (int)(dayW * to); x += 3) { var c = Rgb(img[y * W + x]); sum += c.r + c.g + c.b; n++; }
                    return sum / Math.Max(1, n);
                }
                double dayBefore = Brightness(world, 0.05, 0.45), dayAfter = Brightness(shaded, 0.05, 0.45);
                double nightBefore = Brightness(world, 0.55, 0.95), nightAfter = Brightness(shaded, 0.55, 0.95);
                if (Math.Abs(dayAfter - dayBefore) > 3) throw new Exception($"Day and night: the day half changed ({dayBefore:0} -> {dayAfter:0})");
                if (nightAfter > nightBefore * 0.85) throw new Exception($"Day and night: the night half is not darker ({nightBefore:0} -> {nightAfter:0})");
                int terminator = 0, mid = left + dayW / 2;
                for (int y = H / 2 - 100; y < H / 2 + 100; y++) for (int x = mid - 6; x <= mid + 6; x++)
                    if (Math.Abs(Rgb(shaded[y * W + x]).r - Rgb(world[y * W + x]).r) > 40) terminator++;
                if (terminator < 40) throw new Exception($"Day and night: no terminator drawn ({terminator} pixels)");
                results.Add($"day and night (day half {dayBefore:0} -> {dayAfter:0}, night half {nightBefore:0} -> {nightAfter:0}, terminator {terminator} px)");
                // The real calendar at an equinox and both solstices (sampled by SunChecks).
                foreach (var map in sunMaps ?? Array.Empty<SunChecks.Map>())
                {
                    renderer.SetDay(map.Grid, DayGrid.Width, DayGrid.Height, t.Wraps);
                    Render(map.Name, center.X, center.Z, fitScale, 0, true, false, dayNight: true);
                }
                if (sunMaps?.Count > 0) results.Add($"{sunMaps.Count} real-calendar day and night maps");
            }
            // Minification must sample real mip levels: the rendered sea share matches the raster's.
            int mapW = (int)(t.Width * fitScale), mapH = (int)(t.Height * fitScale), x0 = (W - mapW) / 2, y0 = (H - mapH) / 2, sea = 0, all = 0;
            for (int y = y0 + 4; y < y0 + mapH - 4; y += 3)
            for (int x = x0 + 4; x < x0 + mapW - 4; x += 3)
            {
                var c = Rgb(terrain[y * W + x]); all++;
                if (c.b > c.r + 25) sea++;
            }
            int rasterSea = 0;
            for (int i = 0; i < raster.Width * raster.Height; i++) if (raster.Rgba[i * 4] < 128) rasterSea++;
            double rendered = (double)sea / all, expected = (double)rasterSea / (raster.Width * raster.Height);
            if (Math.Abs(rendered - expected) > 0.12) throw new Exception($"Minified sea share {rendered:P0} differs from world chart {expected:P0} (stale or empty mip levels)");
            if (winds != null)
            {
                // Wind arrows: pilot-chart ink over the chart, white over terrain, at world and regional scale.
                var windy = Render("winds-world-chart", center.X, center.Z, fitScale, 0, true, false, true);
                int changed = 0, ink = 0;
                for (int i = 0; i < windy.Length; i++)
                {
                    if (windy[i] != world[i]) changed++;
                    var c = Rgb(windy[i]);
                    if (c.b > c.r + 40 && c.r < 70) ink++;
                }
                if (changed < W * H / 200 || ink < 200) throw new Exception($"Wind arrows not drawn ({changed} pixels changed, {ink} ink pixels)");
                Render("winds-world-terrain", center.X, center.Z, fitScale, 1, true, false, true);
                var atlantic = locate?.Invoke(30, -40) ?? (center.X, center.Z);
                Render("winds-region-chart", atlantic.X, atlantic.Z, 1 / 60.0, 0, true, false, true);
                results.Add($"winds ({changed} pixels, {ink} ink)");
            }
            var region = locate?.Invoke(52, 3) ?? (center.X, center.Z);
            Render("region-chart", region.X, region.Z, 1 / 18.0, 0, true, false);
            Render("region-terrain", region.X, region.Z, 1 / 18.0, 1, true, false);
            var local = locate?.Invoke(50.9, 1.4) ?? (center.X, center.Z);
            Render("local-chart", local.X, local.Z, 1 / 3.0, 0, true, true);
            Render("local-terrain", local.X, local.Z, 1 / 3.0, 1, true, true);
            if (locate?.Invoke(37.25, -6.07) is { } guadalquivir && source.Info.Source.Contains("Earth"))
            {
                // Full zoom over the Guadalquivir: level-0 tiles sample every block and hold the river's own water (light).
                // The world chart's river channel, interpolated from 25-block texels, lies a few blocks off it; 0.4.2 painted
                // it beside the tile's river as a second, darker blue band (reported in game).
                var river = Render("river-terrain", guadalquivir.X, guadalquivir.Z, 2, 1, true, true);
                int chartRiver = 0, tileWater = 0;
                foreach (int p in river)
                {
                    var c = Rgb(p);
                    if (Math.Abs(c.r - 60) + Math.Abs(c.g - 118) + Math.Abs(c.b - 164) < 45) chartRiver++;
                    else if (c.b > c.r + 40 && c.r > 110) tileWater++;
                }
                if (tileWater < 2000) throw new Exception($"Full-zoom river view shows no river water ({tileWater} pixels)");
                if (chartRiver > 50) throw new Exception($"Full-zoom river drawn twice: {chartRiver} pixels of the world chart's river channel beside {tileWater} of the tile's water");
                results.Add($"full-zoom river from its tiles only ({tileWater} water pixels, {chartRiver} chart-channel pixels)");
            }
            if (locate?.Invoke(45.0, -85.0) is { } greatLakes && source.Info.Source.Contains("Earth"))
            {
                // Earth's river courses run through the lakes they cross (the Saint Lawrence through Superior to Ontario).
                // 0.4.9's terrain style drew them across the Great Lakes at far zoom (reported in game); the chart style
                // and the detail tiles left them out on water. A river pixel with lake on both sides lies on the lake.
                var lakes = Render("great-lakes-terrain", greatLakes.X, greatLakes.Z, 1 / 16.0, 1, true, false);
                bool Lake(int x, int y) { if (x < 0 || y < 0 || x >= W || y >= H) return false; var c = Rgb(lakes[y * W + x]); return c.b > c.r + 40 && c.r > 110; }
                int onLake = 0, lakePixels = 0;
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (Lake(x, y)) { lakePixels++; continue; }
                    var c = Rgb(lakes[y * W + x]);
                    if (Math.Abs(c.r - 60) + Math.Abs(c.g - 118) + Math.Abs(c.b - 164) >= 45) continue;
                    if (Lake(x - 6, y) && Lake(x + 6, y) || Lake(x, y - 6) && Lake(x, y + 6)) onLake++;
                }
                if (lakePixels < 20000) throw new Exception($"Great Lakes view shows only {lakePixels} lake pixels");
                // Shore ink along narrow water (the Saint Lawrence estuary, chains of small lakes) matches a few: 85 with the fix, 4417 without.
                if (onLake > 500) throw new Exception($"River courses drawn across the Great Lakes: {onLake} river pixels with lake on both sides");
                results.Add($"no river courses across the Great Lakes ({lakePixels} lake pixels, {onLake} river pixels on them)");
            }
            // The seam: imagery continues across the ring's ends without a visible edge.
            if (t.Wraps)
            {
                var seam = Render("seam-chart", t.StartX, locate?.Invoke(64, 180)?.Z ?? center.Z, 1 / 30.0, 0, true, false);
                double Column(int x) { double d = 0; for (int y = 0; y < H; y += 2) { var a = Rgb(seam[y * W + x]); var b = Rgb(seam[y * W + x + 1]); d += Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b); } return d; }
                double across = Column(W / 2 - 1), typical = Enumerable.Range(0, 40).Select(i => Column(W / 4 + i * 17)).OrderBy(v => v).ElementAt(30);
                if (across > typical * 1.5 + 2000) throw new Exception($"Visible discontinuity at the seam ({across:0} vs {typical:0})");
                results.Add("seam continuous");
            }
            // Gameplay: only the charted voyage is drawn; the rest is blank parchment with the graticule.
            var voyage = locate?.Invoke(50.0, -4.0) ?? (center.X - 4000, center.Z);
            var end = locate?.Invoke(43.0, -9.5) ?? (center.X + 4000, center.Z + 3000);
            for (double f = 0; f <= 1; f += 0.002) mask.Reveal(voyage.X + (end.X - voyage.X) * f, voyage.Z + (end.Z - voyage.Z) * f, 600);
            var played = Render("gameplay-chart", (voyage.X + end.X) / 2, (voyage.Z + end.Z) / 2, 1 / 24.0, 0, false, false);
            var corner = Rgb(played[(H / 8) * W + W - W / 8]);
            if (source.Info.Source.Contains("Earth") && locate?.Invoke(43.0, 15.6) is { } adriatic)
            {
                // Game charting by line of sight: from a ship in the Adriatic, a 64-block circle and what the deck can see.
                var sightMask = new ExploredMask(t);
                sightMask.Reveal(adriatic.X, adriatic.Z, 64);
                sightMask.RevealCells(Viewshed.Cast(HeightGrid.FromRaster(raster, t), adriatic.X, adriatic.Z, 3, 4096, sightMask.Texel / 2));
                renderer.UpdateMask(sightMask);
                Render("gameplay-sight", adriatic.X - 800, adriatic.Z + 300, 1 / 6.0, 0, false, false);
                renderer.UpdateMask(mask);
                results.Add("game chart by line of sight");
            }
            if (Math.Abs(corner.r - corner.g) > 40 || corner.b > corner.r) throw new Exception("Unexplored gameplay area is not blank parchment");
            if (barometer is { } b && t.Width == source.Info.Width)
            {
                // Gameplay with a Jonas barometer: the weather now within its reach, over the explored and unexplored chart.
                for (double f = -1; f <= 1; f += 0.01) mask.Reveal(b.X + f * 1500, b.Z + f * 600, 500);
                renderer.SetLocalWind(b.Field, WindGrid.LocalSize, b.X - b.Range, b.Z - b.Range, 2 * b.Range);
                Render("barometer-gameplay-chart", b.X, b.Z, 0.12, 0, false, false, true);
                if (winds != null) renderer.SetWind(winds, WindGrid.Width, WindGrid.Height, t.Wraps);
                results.Add("barometer field");
            }
            results.Add(GuiRenderChecks.Run(output, raster, t, W, H, fbo));
            results.Add(FarTerrainChecks.Run(output, renderer, t, source.Info, locate, source));
            results.Add(CacheReuseChecks.TileViews(output, t, W, H));
            results.Add(CacheReuseChecks.BiomelessFarCache(output, t));
            // Replaces the renderer's world chart: keep it last.
            results.Add(BoundedEdge(output, renderer, projection, identity, fbo));
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(target);
            atlas.Dispose();
            return $"GPU map: PASS ({gpu}; production shader in an OpenGL 3.3 core context; world chart {raster.Width}x{raster.Height} uploaded with full mipmaps in {pumps} steps, {uploadMs:0} ms; " +
                $"minified sea share {rendered:P0} vs chart {expected:P0}; views: {string.Join(", ", results)})";
        }
        finally { GLFWProvider.CheckForMainThread = oldThreadCheck; }
    }

    /// <summary>A map that does not wrap, viewed from its east edge (where the camera clamps): the map's
    /// east lies west of the camera, and nothing lies east of it. 0.3.7 drew the west edge there.</summary>
    delegate int[] RenderView(string name, double x, double z, double scale, int style, bool development, bool detail, bool wind = false, bool dayNight = false);

    /// <summary>
    /// 0.5.0-pre.8, reported in game on a world without Earth's gutter: a red-brown band one geography texel wide down the
    /// whole chart at the jump line. The geography texture's longitude jumps by 360 degrees where the ring's ends meet, and
    /// the hardware's filter blended across it, so the graticule's ink covered the texel. The chart at the jump line, with
    /// and without the graticule (every texel outside geography): the strip one texel wide at the jump line may differ no
    /// more than the strips beside it.
    /// </summary>
    static string SeamGraticule(RenderView render, MapRenderer renderer, Func<double, double, GeoPosition> geography, Topology t)
    {
        double scale = 1, texel = t.Width / GeoGrid.Width;
        double z = t.Height * 0.38;
        var lined = render("seam-graticule", t.StartX, z, scale, 0, true, false);
        var none = new float[GeoGrid.Width * GeoGrid.Height * 4];
        for (int i = 2; i < none.Length; i += 4) none[i] = 1;
        renderer.SetGeography(none, GeoGrid.Width, GeoGrid.Height, t.Wraps);
        var plain = render("seam-graticule-none", t.StartX, z, scale, 0, true, false);
        renderer.SetGeography(GeoGrid.Build(geography, t), GeoGrid.Width, GeoGrid.Height, t.Wraps);
        // the share of pixels the graticule changes in the columns a blocks to b blocks east of the jump line (screen centre)
        double Inked(double a, double b)
        {
            int x0 = (int)Math.Round(W / 2 + a * scale), x1 = (int)Math.Round(W / 2 + b * scale), n = 0, inked = 0;
            for (int y = 40; y < H - 40; y++)
                for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
                {
                    int p = lined[y * W + x], q = plain[y * W + x];
                    int d = Math.Abs((p >> 16 & 255) - (q >> 16 & 255)) + Math.Abs((p >> 8 & 255) - (q >> 8 & 255)) + Math.Abs((p & 255) - (q & 255));
                    n++; if (d > 12) inked++;
                }
            return (double)inked / Math.Max(1, n);
        }
        double seam = Inked(-texel, texel), west = Inked(-5 * texel, -3 * texel), east = Inked(3 * texel, 5 * texel);
        string text = $"graticule at the jump line: {seam:P1} of pixels inked within a geography texel ({texel:0} blocks), {west:P1} and {east:P1} beside it";
        if (seam > Math.Max(west, east) * 2 + 0.02) throw new Exception("Seam graticule: " + text);
        return text;
    }

    static string BoundedEdge(string output, MapRenderer renderer, float[] projection, float[] identity, int fbo)
    {
        var b = new Topology { Width = 8192, Height = 4096, Wraps = false };
        // Land only along the west edge.
        var raster = BaseRaster.Bake(b, 1024, 512, 110, () => (x, z) => x < 1024 ? new MapSample(150, 0, TileData.Land) : new MapSample(80, 29, TileData.Water), null, 2, CancellationToken.None);
        renderer.SetBase(raster, b);
        while (!renderer.PumpBase()) { }
        int[] Draw(string name, double x)
        {
            var frame = new MapFrame { X = 0, Y = 0, Width = W, Height = H, Camera = new MapCamera { X = x, Z = b.Height / 2, Scale = 1 }, Topology = b, Style = 1, Development = true, Gui = 1,
                MeridianStep = GeoGrid.Step(b.Width / 360, 110), ParallelStep = GeoGrid.Step(b.Height / 180, 110) };
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.Viewport(0, 0, W, H);
            GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
            if (!renderer.Begin(projection, identity, frame, false)) throw new Exception("Renderer did not start: " + renderer.Error);
            renderer.DrawWorld(50);
            renderer.End();
            var pixels = Read(W, H);
            Save(Path.Combine(output, $"gpu-{name}.png"), pixels);
            return pixels;
        }
        static int Distance(int p, int q) => Math.Abs((p >> 16 & 255) - (q >> 16 & 255)) + Math.Abs((p >> 8 & 255) - (q >> 8 & 255)) + Math.Abs((p & 255) - (q & 255));
        int at(int[] pixels, int x) => pixels[H / 2 * W + x];
        int land = at(Draw("bounded-west", 512), W / 2), sea = at(Draw("bounded-middle", 4096), W / 2);
        if (Distance(land, sea) < 60) throw new Exception("Bounded edge setup: land and sea look alike");
        var east = Draw("bounded-east", b.EndX);
        if (Distance(at(east, W / 8), sea) > 30) throw new Exception("East edge of a bounded map does not show the map's east");
        if (Distance(at(east, W * 7 / 8), land) < 60) throw new Exception("Bounded map wraps at its east edge: the west coast is drawn beyond it");
        return "bounded east edge: map to the west, nothing beyond";
    }

    static int[] Read(int w, int h)
    {
        var raw = new int[w * h]; GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, raw);
        var rows = new int[raw.Length]; for (int y = 0; y < h; y++) Array.Copy(raw, y * w, rows, (h - y - 1) * w, w); return rows;
    }

    static void Save(string path, int[] pixels)
    {
        using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, W, H);
        Marshal.Copy(pixels, 0, surface.DataPtr, pixels.Length); surface.MarkDirty();
        surface.WriteToPng(path);
    }
}
