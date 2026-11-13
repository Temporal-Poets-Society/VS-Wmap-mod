using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Wmap;
using Wmap.Core;
using Wmap.Integrations;

public sealed class Smoke : ModSystem
{
    /// <summary>MidDistanceOnly: bake the chart, then run only the mid-distance view's check (for working on it; a full run
    /// is the verification).</summary>
    public sealed class Settings { public int OverviewWidth = 2048; public bool MidDistanceOnly; }
    public override double ExecuteOrder() => 2;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ =>
        {
            WorldSource source;
            try { source = WorldSource.Create(api, new WmapConfig()); }
            catch (Exception e) { Finish(api, "FAIL initialize: " + e); return; }
            var geography = WorldSource.Geography(api, source.Info);
            // Earth places for the rendered previews; other worlds use the map centre.
            System.Func<double, double, (double X, double Z)?> locate = null;
            object earth = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthMapSystem");
            object geometry = WorldSource.Member(WorldSource.Member(earth, "Model"), "Geometry");
            var fromGeo = geometry?.GetType().GetMethod("FromGeo");
            if (fromGeo != null) locate = (lat, lon) => { object[] a = { lat, lon, 0d, 0d }; fromGeo.Invoke(geometry, a); return ((double)a[2], (double)a[3]); };
            string output = api.GetOrCreateDataPath("WmapSmoke");
            int size = Math.Clamp(api.LoadModConfig<Settings>("wmap-smoke.json")?.OverviewWidth ?? 2048, 512, 8192);
            bool midOnly = api.LoadModConfig<Settings>("wmap-smoke.json")?.MidDistanceOnly == true;
            string barometer;
            try { barometer = BarometerChecks.Run(api); }
            catch (Exception e) { Finish(api, "FAIL barometer: " + e); return; }
            // Paper maps use the game's waypoint and prospecting layers: main thread.
            string paperMaps;
            try { paperMaps = PaperMapChecks.Run(api, Path.Combine(output, "paper-maps")); }
            catch (Exception e) { paperMaps = "FAIL paper maps: " + e; }
            // The day and night overlay against the real calendar (moves it on: main thread, before the other checks).
            var sunMaps = new System.Collections.Generic.List<SunChecks.Map>();
            string sun;
            try { sun = SunChecks.Run(api, source.Info.Topology(), earth, geography, sunMaps); }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(output, "report.txt"), "FAIL real-calendar day and night: " + e.Message + "\n" + e);
                Finish(api, "FAIL real-calendar day and night: " + e);
                return;
            }
            Task.Run(() =>
            {
                string report;
                BaseRaster baked = null;
                try
                {
                    var watch = Stopwatch.StartNew();
                    var t = source.Info.Topology();
                    if (source.Info.HasOverview != (source.Samplers != null)) throw new Exception("Provider capability mismatch");
                    string chart = "World chart: none (no prediction provider)";
                    BaseRaster raster = null;
                    if (source.Samplers != null)
                    {
                        var (w, h) = BaseRaster.SizeFor(t, size);
                        int threads = new WmapConfig().Threads;
                        var bake = Stopwatch.StartNew();
                        raster = BaseRaster.Bake(t, w, h, source.Info.SeaLevel, source.Samplers, source.Rivers, threads, CancellationToken.None, null, source.Info.ChartHeightRange);
                        double bakeSeconds = bake.Elapsed.TotalSeconds;
                        bake.Restart();
                        byte[] packed = raster.Pack();
                        double packSeconds = bake.Elapsed.TotalSeconds;
                        bake.Restart();
                        var copy = BaseRaster.Unpack(packed);
                        if (!copy.Rgba.AsSpan().SequenceEqual(raster.Rgba)) throw new Exception("World chart round trip failed");
                        double unpackSeconds = bake.Elapsed.TotalSeconds;
                        int land = 0, water = 0, ice = 0, river = 0; byte highest = 0;
                        for (int i = 0; i < w * h; i++)
                        {
                            if (raster.Rgba[i * 4] >= 128) land++; else water++;
                            if (raster.Rgba[i * 4 + 2] > 127) ice++;
                            if (raster.Rgba[i * 4 + 3] < 128) river++;
                            if (raster.Rgba[i * 4 + 1] > highest) highest = raster.Rgba[i * 4 + 1];
                        }
                        if (land == 0 || water == 0) throw new Exception("World chart has no coastline");
                        // 0.5.0-pre.11: a taller world's chart keeps ground more than 256 above the sea (Earth's Himalayas).
                        double top = Semantic.DecodeSigned(highest, source.Info.ChartHeightRange);
                        if (source.Info.ChartHeightRange > Semantic.DefaultHeightRange && source.Info.MapHeight - source.Info.SeaLevel > 300 && source.Samplers != null && source.Info.GeographyProvider == AtlasInfo.EarthGeography && top <= Semantic.DefaultHeightRange + 8)
                            throw new Exception($"Tall world chart cut: highest {top:0} blocks above the sea, range {source.Info.ChartHeightRange}");
                        // Raw texels for the browser shader prototype.
                        using (var file = File.Create(Path.Combine(output, $"world-{w}x{h}.bin.gz")))
                        using (var gz = new GZipStream(file, CompressionLevel.Fastest)) gz.Write(raster.Rgba);
                        chart = $"World chart: {w}x{h} texels ({t.Width / w:0.#} blocks each), {threads} threads, bake {bakeSeconds:0.0} s, pack {packSeconds:0.0} s ({packed.Length / 1048576.0:0.0} MiB), unpack {unpackSeconds:0.0} s; " +
                            $"heights to {top:0} above sea {source.Info.SeaLevel} (range {source.Info.ChartHeightRange:0}, world {source.Info.MapHeight}), land {100.0 * land / (w * h):0.0}%, ice {100.0 * ice / (w * h):0.0}%, river texels {river}, {source.Rivers?.Count ?? 0} river segments";
                    }
                    baked = raster;
                    string geo = geography == null ? "none (planar world)" : GeoFormat.Format(geography(t.StartX + t.Width / 2, t.Height / 2));
                    if (midOnly)
                    {
                        api.Event.EnqueueMainThreadTask(() => MidDistanceChecks.Run(api, source, raster, locate, output, mid =>
                        {
                            File.WriteAllText(Path.Combine(output, "report.txt"), (mid.StartsWith("FAIL") ? "FAIL" : "PASS") + " (mid-distance view only)\n" + mid + "\n");
                            Finish(api, mid);
                        }), "wmap-smoke-mid");
                        return;
                    }
                    string checks = ClientPipelineChecks.Run(Path.Combine(output, "client-pipeline"));
                    checks += "\n" + ServerAccessChecks.Run(source);
                    checks += "\n" + FarTileServerChecks.Run(source, locate);
                    checks += "\n" + CacheIdentityChecks.Run(api, source);
                    checks += "\n" + PolarChecks.Run(api, source);
                    checks += "\n" + SeamBandChecks.Run(api);
                    checks += "\n" + AutoMarkChecks.Run(api);
                    checks += "\n" + OreHeatChecks.Run(api, source, locate);
                    checks += "\n" + barometer;
                    if (paperMaps.StartsWith("FAIL")) throw new Exception(paperMaps);
                    checks += "\n" + paperMaps;
                    MapKeyChecks.Run();
                    checks += "\nMap key: PASS (atlas dispatch, remapped controls preserved, original-map fallback, cleanup)";
                    checks += "\n" + DialogChecks.Run(output, api.Logger);
                    checks += "\n" + DayChecks.Run(api, t);
                    checks += "\n" + sun;
                    checks += "\n" + SightChecks.Run(raster, t, locate, source.Info.SeaLevel);
                    checks += "\n" + WindChecks.Run(api, SeaWinds.Find(api), t, locate, out var winds, out var barometerField);
                    checks += "\n" + GpuMapChecks.Run(output, source, raster, geography, locate, winds, barometerField.Field == null ? null : barometerField, sunMaps);
                    report = $"PASS\nProvider: {source.Info.Source}\nGeometry: {t.StartX} + {t.Width} x {t.Height}; wraps={t.Wraps}\nCoordinates: {geo}\n{chart}\nElapsed ms: {watch.ElapsedMilliseconds}\n{checks}\n";
                }
                catch (Exception e) { report = "FAIL: " + e; }
                api.Event.EnqueueMainThreadTask(() => {
                    void Complete(string check) {
                        // The header was written before the generated checks ran; their failure fails the run.
                        string final = (check.Split('\n').Any(line => line.StartsWith("FAIL")) && report.StartsWith("PASS") ? "FAIL" + report.Substring(4) : report) + "\n" + check;
                        File.WriteAllText(Path.Combine(output, "report.txt"), final);
                        Finish(api, final);
                    }
                    if (report.StartsWith("FAIL")) Complete("Generated checks skipped after earlier failure");
                    else GeneratedPolarChecks.Run(api, source, polar => TeleportChecks.Run(api, source, teleport => BedChecks.Run(api, source, bed =>
                        MidDistanceChecks.Run(api, source, baked, locate, output, mid => Complete(polar + "\n" + teleport + "\n" + bed + "\n" + mid)))));
                }, "wmap-smoke-finish");
            });
        }, 500));
    }
    void Finish(ICoreServerAPI api, string report) { api.Logger.Notification("WMAP SMOKE " + report); api.Server.ShutDown(); }
}
