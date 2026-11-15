using Wmap.Core;
using System.IO.Compression;
using System.Security.Cryptography;

int tests = 0;
// A failed check is collected with its line and the run goes on, so one run shows every failure (0.5.3, WM-0041: the
// first one stopped it). An exception still stops it; what failed before is printed with it.
var failures = new List<string>();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    foreach (string f in failures) Console.WriteLine("FAIL " + f);
    Console.WriteLine($"Stopped after {tests} assertions ({failures.Count} failed) by: {e.ExceptionObject}");
};
void Check(bool test, string name, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { tests++; if (!test) failures.Add($"line {line}: {name}"); }
void Near(double actual, double expected, string name, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => Check(Math.Abs(actual - expected) < 1e-7, name + $": {actual} != {expected}", line);
void Reject(Action action, string name, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
{
    try { action(); }
    catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException) { tests++; return; }
    Check(false, name + " (not rejected)", line);
}
// World choices override independently; absent keys preserve either global value, including legacy false.
foreach (bool shared in new[] { false, true })
foreach (bool sight in new[] { false, true })
foreach (bool? worldShared in new bool?[] { null, false, true })
foreach (bool? worldSight in new bool?[] { null, false, true })
{
    var config = new Wmap.WmapConfig { ShareWorldChart = shared, ExploreBySight = sight, SightDistance = 4096, AllowAllPlayers = true };
    var world = new Dictionary<string, bool>();
    if (worldShared.HasValue) world["wmapSharedChart"] = worldShared.Value;
    if (worldSight.HasValue) world["wmapExploreBySight"] = worldSight.Value;
    config.ApplyWorldOptions((key, fallback) => world.TryGetValue(key, out bool value) ? value : fallback);
    // The shared world chart is no longer a world choice (0.5.4, WM-D63): a world that still has the key changes nothing.
    Check(config.ShareWorldChart == shared, "a world's old shared-chart choice is ignored");
    Check(config.ExploreBySight == (worldSight ?? sight), "world sight override / old-world fallback");
    Check(config.SightDistance == 4096 && config.AllowAllPlayers, "world choices preserve other server settings");
    Check(world.Count == (worldShared.HasValue ? 1 : 0) + (worldSight.HasValue ? 1 : 0), "world choices do not populate missing keys");
}
using (var options = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "worldconfig.json"))))
using (var lang = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world-options-en.json"))))
{
    Check(options.RootElement.GetProperty("playStyles").GetArrayLength() == 0, "world options retain non-null empty playStyles");
    var attributes = options.RootElement.GetProperty("worldConfigAttributes").EnumerateArray().ToArray();
    Check(attributes.Length == 1, "only the one Wmap world choice (the shared chart left in 0.5.4)");
    foreach (string key in new[] { "wmapExploreBySight" })
    {
        var attribute = attributes.Single(a => a.GetProperty("code").GetString() == key);
        Check(attribute.GetProperty("category").GetString() == "earth", "world choice in Earth tab");
        Check(attribute.GetProperty("dataType").GetString() == "Bool" && attribute.GetProperty("default").GetString() == "true", "world choice defaults on");
        Check(!attribute.GetProperty("onlyDuringWorldCreate").GetBoolean(), "world choice editable later");
        foreach (string entry in new[] { "worldattribute-" + key, "worldattribute-" + key + "-desc", "worldconfig-" + key + "-true", "worldconfig-" + key + "-false" })
            Check(!string.IsNullOrWhiteSpace(lang.RootElement.GetProperty(entry).GetString()), "world choice English label: " + entry);
    }
}
var t = new Topology { StartX = 25600, Width = 204800, Height = 102400, Wraps = true };
Near(t.Canonical(t.EndX), t.StartX, "east edge canonical");
Near(t.Canonical(t.StartX - .5), t.EndX - .5, "west crossing");
Near(t.Canonical(t.StartX - t.Width * 100 + 17), t.StartX + 17, "negative repeated laps");
Near(t.Delta(t.EndX - 3, t.StartX + 4), 7, "east movement");
Near(t.Delta(t.StartX + 3, t.EndX - 4), -7, "west movement");
Near(t.Delta(0, t.Width / 2), -t.Width / 2, "deterministic half-ring tie");
var c = new MapCamera(); c.Fit(t, 1200, 700);
Check(t.Width * c.Scale <= 1200 && t.Height * c.Scale <= 700, "full-world fit");
double wx = c.X + (137 - 600) / c.Scale, wz = c.Z + (111 - 350) / c.Scale;
c.Zoom(t, 1200, 700, 1.35, 137, 111);
Near(c.X + (137 - 600) / c.Scale, wx, "cursor anchor x");
Near(c.Z + (111 - 350) / c.Scale, wz, "cursor anchor z");

var uneven = new Topology { StartX = 17, Width = 1001, Height = 607, Wraps = true };
c = new MapCamera { X = uneven.EndX, Z = 303, Scale = 1 };
var tiles = TileSelection.Visible(uneven, c, 800, 600, 1).ToList();
Check(tiles.Any(v => v.X < uneven.EndX) && tiles.Any(v => v.X >= uneven.EndX), "both sides visible");
Check(tiles.All(v => uneven.Valid(v.Key)), "tile addresses remain canonical");
Check(tiles.Any(v => v.Width == 233), "partial edge has exact width");
foreach (var row in tiles.GroupBy(v => v.Key.Z))
{
    var ordered = row.OrderBy(v => v.X).ToArray();
    for (int i = 1; i < ordered.Length; i++) Near(ordered[i - 1].X + ordered[i - 1].Width, ordered[i].X, "no gap at partial tile seam");
}
var flat = new Topology { Width = 1001, Height = 607 };
Check(TileSelection.Visible(flat, new MapCamera { X = -500, Z = 100, Scale = 1 }, 200, 200, 1).Count() == 0, "planar view never repeats");
c.Fit(t, 1200, 700);
Check(TileSelection.Visible(t, c, 1200, 700, c.Level(t)).Select(v => v.Key).Distinct().Count() < 100, "world view bounded tile count");
Check(GeoFormat.Angle(12 + 59.9999 / 60, true, false) == "13° 00.0′ N", "minute carry");
Check(GeoFormat.Angle(-24.25, false, true) == "24.2500° W", "hemisphere");
Check(GeoFormat.Format(new(0, 0, true, false)).Contains("approximate"), "gutter uncertainty");
// Grid labels follow the readout's format: with decimal degrees a 169°45′W line beside a readout of 169.4513° W read as
// the player's own longitude, 0.3° away (St Lawrence Island at 1:50, 0.5 blocks a pixel).
Check(GeoFormat.GridLabel(-169.75, false, 0.25, false) == "169°45′W", "grid label minutes");
Check(GeoFormat.GridLabel(-169.75, false, 0.25, true) == "169.75°W", "grid label decimals");
Check(GeoFormat.GridLabel(63.5, true, 0.5, true) == "63.5°N" && GeoFormat.GridLabel(63.0000001, true, 0.25, true) == "63°N", "grid label decimals trimmed");
Check(GeoFormat.GridLabel(-170, false, 1, true) == "170°W" && GeoFormat.GridLabel(0, true, 0.25, true) == "0°", "grid label whole degrees");
Check(GeoFormat.GridLabel(12 + 59.9999 / 60, true, 0.25, false) == "13°00′N", "grid label minute carry");
// The HUD clock (WM-0010): Astra Terra's local solar time, the universal hour moved by longitude/360 of a day.
Near(LocalTime.UniversalHours(10.5, 24), 12, "universal noon");
Near(LocalTime.SolarHours(10.5, 24, 0), 12, "solar noon on the prime meridian");
Near(LocalTime.SolarHours(10.5, 24, 24.1), 12 + 24.1 / 15, "Riga is 1 h 36 min ahead");
Near(LocalTime.SolarHours(10.5, 24, -122.4), 12 - 122.4 / 15, "San Francisco is 8 h 10 min behind");
Near(LocalTime.SolarHours(10.1, 24, -90), 24 * 0.1 - 6 + 24, "west of midnight wraps to the previous evening");
Near(LocalTime.SolarHours(10.9, 24, 180), 24 * 0.9 + 12 - 24, "east of midnight wraps to the next morning");
Near(LocalTime.SolarHours(3.25, 16, 90), 8, "a 16-hour day: a quarter turn is four hours");
Near(LocalTime.SolarHours(-0.25, 24, 0), 18, "before day zero");
Check(LocalTime.Format(13 + 59.99 / 60) == "13:59" && LocalTime.Format(0) == "00:00" && LocalTime.Format(9.5) == "09:30", "clock cuts minutes like the character panel");
Check(LocalTime.Format(16 - 1e-12) == "16:00" && LocalTime.Format(24 - 1e-12) == "00:00" && LocalTime.Format(LocalTime.UniversalHours(16.0 / 24 + 3, 24)) == "16:00", "clock forgives rounding below the minute");
Check(LocalTime.Clocks(16.77, 12.25, false) == ("16:46 local", "12:15 UTC"), "local and universal clocks");
Check(LocalTime.Clocks(12.25, 12.25, true) == ("12:15", null), "one clock shown once");
Check(LocalTime.Clocks(LocalTime.SolarHours(10.5, 24, 24.1), LocalTime.UniversalHours(10.5, 24), false) == ("13:36 local", "12:00 UTC"), "Riga at universal noon");
Check(LocalTime.Format(24) == "00:00" && LocalTime.Format(-0.5) == "23:30" && LocalTime.Format(double.NaN) == "--:--", "clock wraps");
Check(LocalTime.Format(LocalTime.SolarHours(10.5, 24, 24.1)) == "13:36", "Riga's noon reads 13:36");
Check(new Wmap.WmapConfig().CoordinatesHudTime, "HUD clock on by default");

int samples = 0;
SemanticTile.Sample(uneven, new(0, 1, 3, 0), (x, z) => { Check(x >= uneven.StartX && x < uneven.EndX, "sampling wraps correctly"); samples++; return new(150, 8, 2); }, 110, CancellationToken.None);
Check(samples <= SemanticTile.Grid * SemanticTile.Grid && samples > 0, "fixed sampling work");
// Neighbouring tiles reuse each other's overlapping samples (W1-11): every tile is the same as sampled alone, on a world
// whose start is not a round number, across the seam and on a bounded world's edge, and a 3 x 3 block filled centre first
// takes each position once.
{
    MapSample Hills(double x, double z)
    {
        double h = 110 + 40 * Math.Sin(x / 37.3) * Math.Cos(z / 53.1) + 5 * Math.Sin((x + z) / 7.7);
        return h < 110 ? new MapSample((int)Math.Floor(h), 110 - (int)Math.Floor(h), TileData.Water) : new MapSample((int)Math.Round(h), 0, TileData.Land);
    }
    foreach (var world in new[] { new Topology { StartX = 1000.3, Width = 512 * 7 + 300, Height = 4096, Wraps = true }, new Topology { StartX = 1000.3, Width = 512 * 7 + 300, Height = 4096 } })
    {
        var cache = new SemanticTile.SampleCache();
        int calls = 0;
        var keys = new List<TileKey> { new(0, 2, 3, 3) };
        foreach (int dz in new[] { -1, 0, 1 }) foreach (int dx in new[] { -1, 0, 1 }) if (dx != 0 || dz != 0) keys.Add(new(0, 2, 3 + dx, 3 + dz));
        bool same = true;
        foreach (var key in keys)
            same &= SemanticTile.Sample(world, key, (x, z) => { calls++; return Hills(x, z); }, 110, CancellationToken.None, cache)
                .SequenceEqual(SemanticTile.Sample(world, key, Hills, 110, CancellationToken.None));
        int unique = (3 * TileData.Size + SemanticTile.Grid - TileData.Size) * (3 * TileData.Size + SemanticTile.Grid - TileData.Size);
        Check(same && calls == unique, $"neighbouring tiles reuse their samples ({calls} calls for {keys.Count} tiles, {keys.Count * SemanticTile.Grid * SemanticTile.Grid} alone)");
        // The last tile of a lap (partial, its margin across the seam or past a bounded edge) beside the first and its west neighbour.
        int last = (int)Math.Ceiling(world.Width / 512) - 1;
        foreach (var key in new TileKey[] { new(0, 2, 0, 0), new(0, 2, last, 0), new(0, 2, last - 1, 0), new(0, 2, last, 1) })
            same &= SemanticTile.Sample(world, key, Hills, 110, CancellationToken.None, cache).SequenceEqual(SemanticTile.Sample(world, key, Hills, 110, CancellationToken.None));
        Check(same, "tiles at the seam or a bounded edge are the same with reused samples");
    }
}
var data = new TileData();
for (int i = 0; i < TileData.Count; i += 7) data.Set(i, new MapSample(100 + i % 50, i % 3, (byte)(1 + i % 3), i));
byte[] encoded = TileCodec.Encode(data); var decoded = TileCodec.Decode(encoded);
Check(decoded.Heights.SequenceEqual(data.Heights) && decoded.Kinds.SequenceEqual(data.Kinds), "compressed tile round trip");
Check(Enumerable.Range(0, TileData.Count).All(i => decoded.ColorAt(i) == data.ColorAt(i)) && decoded.ColorAt(7) == 7, "colours round trip");
// Colours are made only when one is not 0; a tile without any encodes as before and copies without them.
var uncoloured = new TileData(); uncoloured.Set(5, new MapSample(120, 0, TileData.Land));
Check(TileCodec.Decode(TileCodec.Encode(uncoloured)).ColorAt(5) == 0 && uncoloured.Copy().ColorAt(5) == 0 && TileCodec.Encode(uncoloured).AsSpan(4, 32).SequenceEqual(SHA256.HashData(Enumerable.Range(0, TileData.Count).SelectMany(i => i == 5 ? new byte[] { 120, 0, 0, 0, 1, 0, 0, 0, 0 } : new byte[9]).ToArray())), "a tile without colours encodes zeros for them");
var damaged = (byte[])encoded.Clone(); damaged[10] ^= 1;
Reject(() => TileCodec.Decode(damaged), "checksum must reject damage");
Reject(() => TileCodec.Decode(encoded[..40]), "truncation must fail");
Reject(() => TileCodec.Decode(new byte[TileCodec.MaxCompressedBytes + 1]), "input length bound");
using (var bomb = new MemoryStream())
{
    using (var writer = new BinaryWriter(bomb, System.Text.Encoding.UTF8, true)) { writer.Write(1); writer.Write(new byte[32]); }
    using (var deflate = new DeflateStream(bomb, CompressionLevel.Fastest, true)) deflate.Write(new byte[TileData.Count * 9 + 1]);
    Reject(() => TileCodec.Decode(bomb.ToArray()), "decompression length bound");
}
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    try { SemanticTile.Sample(t, new(0, 0, 0, 0), (x, z) => new(1, 0, 1), 110, stop.Token); throw new Exception("uncancelled sample"); }
    catch (OperationCanceledException) { tests++; }
}
// Semantic encodings keep sign and are most precise near zero.
foreach (double v in new[] { -128.0, -40, -3, -0.5, 0.5, 3, 40, 128 })
{
    double back = Semantic.DecodeSigned(Semantic.EncodeSigned(v, Semantic.CoastRange), Semantic.CoastRange);
    Check(Math.Sign(back) == Math.Sign(v), $"signed encoding keeps sign {v}");
    Check(Math.Abs(back - v) <= Math.Max(0.12, Math.Abs(v) * 0.03), $"signed encoding precision {v}: {back}");
}
Check(Semantic.EncodeSigned(1000, 10) == 255 && Semantic.EncodeSigned(-1000, 10) == 0, "signed encoding clamps");

// Exact distance transform against brute force, bounded and periodic.
var rng = new Random(7);
foreach (bool periodic in new[] { false, true })
{
    int w = 61, h = 37, range = 20;
    var sites = Enumerable.Range(0, w * h).Select(_ => rng.NextDouble() < 0.01).ToArray();
    sites[5 * w + 3] = true;
    var got = new double[w * h];
    Semantic.Distance(w, h, i => sites[i], periodic, range, (i, d) => got[i] = d);
    for (int i = 0; i < w * h; i++)
    {
        double best = range;
        for (int j = 0; j < w * h; j++)
        {
            if (!sites[j]) continue;
            double dx = Math.Abs(i % w - j % w), dz = i / w - j / w;
            if (periodic) dx = Math.Min(dx, w - dx);
            best = Math.Min(best, Math.Sqrt(dx * dx + dz * dz));
        }
        Near(got[i], best, $"distance transform periodic={periodic} cell {i}");
    }
}

// A round island: coast distance positive inside, negative outside, zero crossing at the shore.
{
    int w = 96, h = 64;
    var rgba = new byte[w * h * 4];
    Semantic.CoastField(rgba, w, h, i => (sbyte)(Math.Sqrt(Math.Pow(i % w - 48, 2) + Math.Pow(i / w - 32, 2)) < 20 ? 0 : 1), false);
    double D(int x, int z) => Semantic.DecodeSigned(rgba[(z * w + x) * 4], Semantic.CoastRange);
    Check(D(48, 32) > 18 && D(48, 32) < 21, "island centre distance");
    Check(D(2, 2) < -20, "open sea negative");
    Check(D(67, 32) > 0 && D(68, 32) < 0, "coast between the last land and first water cell");
    // Unknown cells are neither land nor water: no coast is drawn at the edge of recorded terrain.
    Semantic.CoastField(rgba, w, h, i => i % w < 40 ? (sbyte)-1 : (sbyte)1, false);
    Check(D(60, 30) < -100, "recorded sea next to unknown cells has no false coast");
}

// Periodic coast distance continues across the cylinder seam.
{
    int w = 64, h = 8;
    var rgba = new byte[w * h * 4];
    Semantic.CoastField(rgba, w, h, i => (sbyte)(i % w < 4 ? 0 : 1), true);
    double D(int x) => Semantic.DecodeSigned(rgba[(4 * w + x) * 4], Semantic.CoastRange);
    Near2(D(w - 1), -0.5, 0.1, "water at the east edge is next to land at the west edge");
    Check(D(w - 6) < D(w - 2), "distance grows away from the wrapped coast");
}

// Raster codec: exact round trip, damage detection and bounds.
{
    var pixels = new byte[33 * 17 * 4]; rng.NextBytes(pixels);
    var packed = RasterCodec.Pack(pixels, 33, 17, 9, CompressionLevel.Fastest);
    Check(RasterCodec.Unpack(packed, 9, 33 * 17).Rgba.SequenceEqual(pixels), "raster round trip");
    var broken = (byte[])packed.Clone(); broken[^3] ^= 0x40;
    Check(Throws(() => RasterCodec.Unpack(broken, 9, 33 * 17)), "raster damage rejected");
    Check(Throws(() => RasterCodec.Unpack(packed, 8, 33 * 17)), "raster version checked");
    Check(Throws(() => RasterCodec.Unpack(packed, 9, 100)), "raster size bound");
}

// Whole-world bake: wrapped island across the seam, river channel and sizing.
{
    var ring = new Topology { StartX = 1000, Width = 4096, Height = 2048, Wraps = true };
    Check(BaseRaster.SizeFor(ring, 1024) == (1024, 512), "raster keeps square texels");
    Check(BaseRaster.SizeFor(new Topology { Width = 204800, Height = 102400 }, 8192) == (8192, 4096), "earth ring raster size");
    var huge = BaseRaster.SizeFor(new Topology { Width = 1024000, Height = 1024000 }, 8192);
    Check((long)huge.Width * huge.Height <= BaseRaster.MaxTexels, "texel budget");
    int calls = 0;
    var rivers = new List<RiverSegment> { new(1000 + 2000, 300, 1000 + 2400, 300, 6) };
    var raster = BaseRaster.Bake(ring, 512, 256, 110, () =>
    {
        Interlocked.Increment(ref calls);
        return (x, z) =>
        {
            if (x < ring.StartX || x >= ring.EndX) throw new Exception("bake samples canonical x");
            double dx = ring.Delta(ring.StartX, x);   // island centred on the seam
            bool land = Math.Abs(dx) < 400 && Math.Abs(z - 1024) < 400;
            return land ? new MapSample(150, 0, TileData.Land) : new MapSample(80, 29, TileData.Water);
        };
    }, rivers, 4, CancellationToken.None);
    Check(calls >= 1 && calls <= 4, "one sampler per worker");
    byte At(int x, int z, int ch) => raster.Rgba[(z * 512 + x) * 4 + ch];
    Check(Semantic.DecodeSigned(At(0, 128, 0), Semantic.CoastRange) > 0 && Semantic.DecodeSigned(At(511, 128, 0), Semantic.CoastRange) > 0, "island spans the seam");
    Check(Semantic.DecodeSigned(At(200, 128, 0), Semantic.CoastRange) < 0, "sea away from the island");
    Near2(Semantic.DecodeSigned(At(0, 128, 1), Semantic.DefaultHeightRange), 41, 1, "land height above the sea surface");
    Check(Semantic.DecodeSigned(At(200, 128, 1), Semantic.DefaultHeightRange) < -25, "sea bed below the surface");
    Check(Semantic.DecodeSigned(At(275, 37, 3), Semantic.RiverRange) < 0, "river channel inside the river");
    Check(At(275, 120, 3) == 255, "no river far from it");
    var copy = BaseRaster.Unpack(raster.Pack());
    Check(copy.Width == 512 && copy.Rgba.SequenceEqual(raster.Rgba), "world raster codec round trip");
}

// Detail tiles sample a margin, so coast distances agree across tile edges.
{
    var plane = new Topology { Width = 4096, Height = 4096 };
    Func<double, double, MapSample> coast = (x, z) => x < 1000 ? new MapSample(130, 0, TileData.Land) : new MapSample(90, 19, TileData.Water);
    var left = SemanticTile.Sample(plane, new TileKey(0, 2, 1, 0), coast, 110, CancellationToken.None);
    var right = SemanticTile.Sample(plane, new TileKey(0, 2, 2, 0), coast, 110, CancellationToken.None);
    // Texel 129 of the left tile is the same location as texel 1 of the right tile.
    Check(left[(60 * TileData.Padded + 129) * 4] == right[(60 * TileData.Padded + 1) * 4], "coast distance continuous across tile edge");
    Check(Semantic.DecodeSigned(right[(60 * TileData.Padded + 40) * 4], Semantic.CoastRange) < -20, "distance to a coast in the neighbouring tile");
    Check(SemanticTile.Unpack(SemanticTile.Pack(left)).SequenceEqual(left), "tile codec round trip");
    var outside = SemanticTile.Sample(plane, new TileKey(0, 5, 0, 0), (x, z) => new MapSample(130, 0, TileData.Land), 110, CancellationToken.None);
    Check(outside[3] == 0 && outside[(5 * TileData.Padded + 5) * 4 + 3] == 255, "outside a bounded world is unknown");
    var recorded = new TileData();
    recorded.Set(20 * TileData.Padded + 20, new MapSample(120, 0, TileData.Land));
    // Sea ice belongs to the sea on a chart; a glacier is land with a coast.
    var floes = SemanticTile.Sample(plane, new TileKey(0, 2, 0, 0), (x, z) => x < 200 ? new MapSample(111, 0, TileData.Ice) : new MapSample(150, 0, TileData.Ice), 110, CancellationToken.None);
    Check(Semantic.DecodeSigned(floes[(60 * TileData.Padded + 20) * 4], Semantic.CoastRange) < 0 && floes[(60 * TileData.Padded + 20) * 4 + 2] == 255, "sea ice is ice-covered sea");
    Check(Semantic.DecodeSigned(floes[(60 * TileData.Padded + 100) * 4], Semantic.CoastRange) > 0, "glacier is land");
    var recordedRgba = SemanticTile.Encode(recorded, 110);
    Check(SemanticTile.Any(recordedRgba) && recordedRgba[(20 * TileData.Padded + 20) * 4 + 3] == 255 && recordedRgba[(80 * TileData.Padded + 80) * 4 + 3] == 0, "recorded coverage");
    // A lake recorded alone (its shore matched the prediction, so was not recorded): its coast lies at its edge when the
    // prediction under it says land, not halfway to the nearest recorded land (0.4.6 drew such lakes without a shore).
    var lake = new TileData();
    for (int z = 40; z < 48; z++) for (int x = 40; x < 48; x++) lake.Set(z * TileData.Padded + x, new MapSample(149, 1, TileData.Water));
    lake.Set(44 * TileData.Padded + 70, new MapSample(160, 0, TileData.Land));
    var dry = SemanticTile.Sample(plane, new TileKey(0, 0, 3, 3), (x, z) => new MapSample(155, 0, TileData.Land), 110, CancellationToken.None);
    double Coast(byte[] rgba, int x, int z) => Semantic.DecodeSigned(rgba[(z * TileData.Padded + x) * 4], Semantic.CoastRange);
    var alone = SemanticTile.Encode(lake, 110);
    var overPrediction = SemanticTile.Encode(lake, 110, dry);
    Check(Coast(alone, 50, 44) < 0, "a lake without its prediction reaches into the unknown");
    Check(Coast(overPrediction, 47, 44) < 0 && Coast(overPrediction, 48, 44) > 0 && Math.Abs(Coast(overPrediction, 48, 44) - 0.5) < 0.1,
        "a recorded lake's shore lies at its edge over predicted land");
    int unknownTexel = (80 * TileData.Padded + 80) * 4;
    Check(overPrediction[unknownTexel + 3] == 0 && overPrediction[unknownTexel + 1] == dry[unknownTexel + 1], "unknown cells keep no coverage and take the prediction's height");
}

// Detail selection: none while the world raster is sharp, bounded tiles beyond it.
{
    var view = new List<TileInstance>();
    var cam = new MapCamera(); cam.Fit(t, 1600, 900);
    var policy = new ViewPolicy(25, true, false, _ => false, 368);
    Check(ViewTiles.Select(view, t, cam, 1600, 900, policy) == -1 && view.Count == 0, "whole world needs no tiles");
    Check(ViewTiles.MaxDetailLevel(t, 25) == 3, "detail only finer than the world raster");
    cam.Scale = 1 / 6.0;
    int level = ViewTiles.Select(view, t, cam, 1600, 900, policy);
    Check(level == 2 && view.Count > 0 && view.All(v => v.Key.Layer == 0 && v.Key.Level == 2), "predicted detail at local zoom");
    Check(view.Select(v => v.Key).Distinct().Count() <= 368, "detail budget");
    cam.Scale = 1 / 11.0;
    Check(ViewTiles.Select(view, t, cam, 1600, 900, policy) == 3, "regional zoom uses the coarsest useful detail");
    cam.Scale = 1 / 6.0;
    var observed = new TileKey(1, 2, (int)((cam.X - t.StartX) / 512), (int)(cam.Z / 512));
    ViewTiles.Select(view, t, cam, 1600, 900, policy with { Predictions = false, Observations = true, HasObservation = k => k == observed });
    Check(view.Count == 1 && view[0].Key == observed, "gameplay without shared predictions draws recordings only");
    cam.Fit(t, 1600, 900);
    ViewTiles.Select(view, t, cam, 1600, 900, new ViewPolicy(0, false, true, k => k.Level == t.MaxLevel, 368));
    Check(view.Count > 0 && view.All(v => v.Key.Layer == 1), "worlds without a raster show recordings at any zoom");
    var ordered = ViewTiles.Display(new[] { new TileInstance(new(1, 2, 0, 0), 0, 0, 1, 1), new TileInstance(new(0, 4, 0, 0), 0, 0, 1, 1), new TileInstance(new(0, 2, 0, 0), 0, 0, 1, 1) }, _ => true).ToArray();
    Check(ordered[0].Key.Level == 4 && ordered[1].Key.Layer == 0 && ordered[2].Key.Layer == 1, "coarse first, recordings above predictions");
    // Recorded terrain corrects a shared prediction: a wrong coast is redrawn where the player has been.
    var both = policy with { Observations = true, HasObservation = k => k == observed };
    cam.Scale = 1 / 6.0;
    ViewTiles.Select(view, t, cam, 1600, 900, both);
    Check(view.Any(v => v.Key.Layer == 0) && view.Count(v => v.Key.Layer == 1) == 1 && view.Single(v => v.Key.Layer == 1).Key == observed, "recordings selected with predictions");
    var drawn = ViewTiles.Display(view, _ => true).ToArray();
    Check(drawn[^1].Key == observed, "recording drawn over the prediction of its level");
    cam.Scale = 2;
    int close = ViewTiles.Select(view, t, cam, 1600, 900, both);
    Check(close == 0 && view.Where(v => v.Key.Layer == 1).All(v => v.Key.Level == 2) && view.Any(v => v.Key == observed), "close zoom keeps four-block recordings");
    drawn = ViewTiles.Display(view, _ => true).ToArray();
    Check(drawn[0].Key == observed && drawn.Skip(1).All(v => v.Key.Layer == 0 && v.Key.Level == 0), "one-block predictions draw over the four-block recordings (WM-D41)");
    Check(drawn.Skip(1).All(v => ViewTiles.Reference(v, k => k == observed, t.MaxLevel)?.Key == (v.Key.X >> 2 == observed.X && v.Key.Z >> 2 == observed.Z ? observed : null))
        && drawn.Skip(1).Any(v => ViewTiles.Reference(v, k => k == observed, t.MaxLevel) != null),
        "each one-block prediction is checked against the four-block recording under it (WM-D41)");
    // One-block recordings (cells that differ from the prediction) at close zoom, over everything.
    var fine = new TileKey(1, 0, (int)((cam.X - t.StartX) / 128), (int)(cam.Z / 128));
    ViewTiles.Select(view, t, cam, 1600, 900, both with { HasObservation = k => k == observed || k == fine });
    drawn = ViewTiles.Display(view, _ => true).ToArray();
    Check(drawn[^1].Key == fine && drawn[0].Key == observed, "one-block recordings over everything, four-block ones under the one-block prediction");
    Check(ViewTiles.Reference(drawn[^1], _ => true, t.MaxLevel) == null, "recordings are never refined by a prediction");
    // Recording block by block where the world differs from the prediction: land against water, or more than 3 blocks
    // (plus 3 % of the height) off; the sea compares its bed, a lake its surface.
    Check(!RecordingRules.Differs(new MapSample(122, 0, TileData.Land), 110, 12, false) && !RecordingRules.Differs(new MapSample(124, 0, TileData.Land), 110, 12, false), "recording: within 3 blocks is the prediction");
    Check(RecordingRules.Differs(new MapSample(125, 0, TileData.Land), 110, 12, false) && RecordingRules.Differs(new MapSample(117, 0, TileData.Land), 110, 12, false), "recording: 4 blocks off differs");
    Check(!RecordingRules.Differs(new MapSample(205, 0, TileData.Land), 110, 100, false) && RecordingRules.Differs(new MapSample(216, 0, TileData.Land), 110, 100, false), "recording: high up the margin grows with the height");
    Check(RecordingRules.Differs(new MapSample(110, 0, TileData.Land), 110, 0, true) && !RecordingRules.Differs(new MapSample(90, 19, TileData.Water), 110, -19, true), "recording: land against the sea differs, the sea against itself does not");
    Check(RecordingRules.Differs(new MapSample(150, 9, TileData.Water), 110, 50, false) && !RecordingRules.Differs(new MapSample(150, 9, TileData.Water), 110, 50, true), "recording: a lake compares its surface");
    Check(RecordingRules.Shore(new MapSample(150, 9, TileData.Water), 110, false) && RecordingRules.Shore(new MapSample(110, 0, TileData.Land), 110, true)
        && !RecordingRules.Shore(new MapSample(125, 0, TileData.Land), 110, false), "recording: water against land is a shore, a height alone is not");
    // The recorded canopy (0.5.3-rc.4) rides in a land sample's depth: 0 not known, 1 + blocks up to the treetops.
    Check(RecordingRules.CanopyDepth(130, 120) == 11 && RecordingRules.CanopyDepth(120, 120) == 1 && RecordingRules.CanopyDepth(119, 120) == 1
        && RecordingRules.CanopyDepth(400, 120) == 1 + RecordingRules.MaxCanopy, "canopy depth: 1 + treetops over the ground, open ground 1, capped");
    Check(RecordingRules.Canopy(TileData.Land, 11) == 10 && RecordingRules.Canopy(TileData.Land, 1) == 0 && RecordingRules.Canopy(TileData.Land, 0) == -1
        && RecordingRules.Canopy(TileData.Water, 5) == -1 && RecordingRules.Canopy(TileData.Ice, 3) == -1, "canopy read back: land only, older land samples unknown");
    Check(RecordedTexels.CanopyAlpha(TileData.Land, 11) == 40 && RecordedTexels.CanopyAlpha(TileData.Land, 1) == 0 && RecordedTexels.CanopyAlpha(TileData.Land, 0) == 255
        && RecordedTexels.CanopyAlpha(TileData.Water, 7) == 255 && RecordedTexels.CanopyAlpha(TileData.Land, 1 + RecordingRules.MaxCanopy) == 252, "canopy alpha: a quarter block a step, 255 unknown");
    // A canopy in the depth leaves land's height, top and the semantic tiles as they were.
    Check(Semantic.RelativeHeight(130, 11, TileData.Land, 110) == Semantic.RelativeHeight(130, 0, TileData.Land, 110)
        && RecordedTexels.Top(130, 11, TileData.Land) == RecordedTexels.Top(130, 0, TileData.Land)
        && !RecordingRules.Differs(new MapSample(122, 9, TileData.Land), 110, 12, false), "a canopy depth changes no land height");
    {
        var canopyTile = new TileData();
        for (int i = 0; i < TileData.Count; i++) canopyTile.Set(i, new MapSample(130, i % 2 == 0 ? 9 : 0, TileData.Land));
        var canopyHeights = new Half[TileData.Count * RecordedTexels.Halves];
        var canopyRgba = new byte[TileData.Count * 4];
        RecordedTexels.Build(canopyTile, canopyHeights, canopyRgba);
        Check(canopyRgba[3] == 32 && canopyRgba[7] == 255 && (float)canopyHeights[0] == 131f, "recorded texels carry the canopy in the colour's alpha");
    }
    // A flat land/sea mask has nothing finer than four blocks to sample.
    var flatPolicy = both with { FlatPrediction = true };
    close = ViewTiles.Select(view, t, cam, 1600, 900, flatPolicy);
    Check(close == 2 && view.All(v => v.Key.Level == 2), "flat predictions start at level 2");
    drawn = ViewTiles.Display(view, _ => true).ToArray();
    Check(drawn.SkipWhile(v => v.Key.Layer == 0).All(v => v.Key.Layer == 1), "recordings over flat predictions at close zoom");
    // Observations correct either a relief prediction or a flat mask beyond detail zoom too.
    cam.Scale = 1 / 40.0;
    var coarseRecording = new TileKey(1, 5, (int)((cam.X - t.StartX) / (128 << 5)), (int)(cam.Z / (128 << 5)));
    Check(ViewTiles.Select(view, t, cam, 1600, 900, both with { HasObservation = k => k == coarseRecording }) == -1 && view.Count == 1 && view[0].Key == coarseRecording, "observations correct Earth relief predictions at regional zoom");
    Check(ViewTiles.Select(view, t, cam, 1600, 900, flatPolicy with { HasObservation = k => k == coarseRecording }) == -1 && view.Count == 1 && view[0].Key == coarseRecording, "recordings over a flat mask at regional zoom");
    ViewTiles.Select(view, t, cam, 1600, 900, both with { HasObservation = _ => true, Budget = 3 });
    Check(view.Count > 0 && view.All(v => v.Key.Layer == 1) && view.Select(v => v.Key).Distinct().Count() <= 3
        && view.All(v => v.Key.Level > 5), "overview recordings coarsen to fit their budget instead of disappearing");
}
// Existing generated/recorded chunks are authoritative even when a newer generator predicts a river there.
{
    var actualLand = new TileInstance(new TileKey(1, 2, 0, 0), 0, 0, 512, 512);
    var predictedRiver = new TileInstance(new TileKey(0, 0, 0, 0), 0, 0, 128, 128);
    // The finer prediction draws above, checked cell by cell against the recording under it (the map shader; the GPU
    // play-test checks render it): an old dry chunk still hides a newer predicted river (WM-D40, WM-D41).
    Check(ViewTiles.Display(new[] { actualLand, predictedRiver }, _ => true).First().Key == actualLand.Key
        && ViewTiles.Reference(predictedRiver, k => k == actualLand.Key, 8)?.Key == actualLand.Key,
        "old generated dry land is the reference of a newer one-block river prediction");
    Check(ViewTiles.Display(new[] { predictedRiver }, _ => true, new[] { actualLand }).First().Key == actualLand.Key,
        "uploaded observed fallback stays under, and checks, a newly arrived finer prediction");
    Check(ViewTiles.Reference(predictedRiver, _ => false, 8) == null, "no reference where no recording is drawn");
    // Positions follow the prediction tile's lap; the finest drawn recording of four blocks or coarser is the reference.
    var lapped = new TileInstance(new TileKey(0, 0, 5, 3), 1000 + 5 * 128, 3 * 128, 128, 128);
    var refAt = ViewTiles.Reference(lapped, k => k.Level >= 2, 8).Value;
    Check(refAt.Key == new TileKey(1, 2, 1, 0) && refAt.X == 1000 + 512 && refAt.Z == 0, "reference: the containing four-block recording on the same lap");
    Check(ViewTiles.Reference(lapped, k => k.Level == 3 || k.Level == 1, 8).Value.Key == new TileKey(1, 3, 0, 0), "reference: two-block recordings are skipped");
    Check(ViewTiles.Reference(new TileInstance(new TileKey(0, 2, 1, 0), 512, 0, 512, 512), _ => true, 8).Value.Key.Level == 3, "reference: only coarser recordings");
    var actualFine = new TileInstance(new TileKey(1, 0, 0, 0), 0, 0, 128, 128);
    var ordered = ViewTiles.Display(new[] { predictedRiver, actualLand, actualFine }, _ => true).ToArray();
    Check(ordered[^1].Key == actualFine.Key && ordered[0].Key == actualLand.Key,
        "finer observed edits override earlier coarse observations");
    foreach (float fade in new[] { 0f, .25f, .5f, 1f })
    {
        Near(ViewTiles.Opacity(actualLand.Key, fade), 1, "recorded terrain cannot reveal a predicted river during zoom fade");
        Near(ViewTiles.Opacity(predictedRiver.Key, fade), fade, "prediction detail retains its zoom fade");
    }
    var world = new Topology { Width = 512, Height = 512 };
    MapSample Prediction(double x, double z) => x < 64 ? new MapSample(140, 9, TileData.River) : new MapSample(155, 0, TileData.Land);
    var key = actualLand.Key;
    var predicted = SemanticTile.Sample(world, key, Prediction, 110, CancellationToken.None);
    int dry = 10 * TileData.Padded + 10, ice = dry + 4, lake = dry + 14, unknown = dry - 4;
    var recorded = new TileData();
    recorded.Set(dry, new MapSample(155, 0, TileData.Land));
    recorded.Set(ice, new MapSample(160, 0, TileData.Ice));
    recorded.Set(lake, new MapSample(150, 9, TileData.Water));
    // Round-trip a recording with no new colour metadata: existing saves must get precedence too.
    var persisted = TileCodec.Decode(TileCodec.Encode(recorded));
    var observedSemantic = SemanticTile.Encode(persisted, 110, predicted);
    Check(observedSemantic[dry * 4] >= 128 && observedSemantic[dry * 4 + 3] == 255, "old recorded dry land covers predicted water with full coverage");
    Check(observedSemantic[ice * 4 + 2] == 255 && observedSemantic[ice * 4 + 3] == 255, "recorded glacier covers predicted river or land");
    Check(observedSemantic[lake * 4] < 128 && observedSemantic[lake * 4 + 3] == 255, "recorded real water survives predicted dry land");
    Check(observedSemantic[unknown * 4 + 3] == 0 && predicted[unknown * 4] < 128, "unknown recording cells leave the predicted river visible");
    byte[] snapshot = TileCodec.Encode(persisted);
    var differentPrediction = SemanticTile.Sample(world, key, (_, _) => new MapSample(180, 0, TileData.Land), 110, CancellationToken.None);
    var reencoded = SemanticTile.Encode(persisted, 110, differentPrediction);
    Check(snapshot.SequenceEqual(TileCodec.Encode(persisted)) && reencoded[dry * 4] >= 128
        && reencoded[lake * 4] < 128 && reencoded[ice * 4 + 2] == 255, "changing Earth prediction preserves recorded land, lake and glacier samples");
}
// Exploration: a fixed radius, the same on every machine, continuous across the seam.
{
    var ring = new Topology { StartX = 25600, Width = 204800, Height = 102400, Wraps = true };
    var mask = new Wmap.Client.ExploredMask(ring);
    var defaults = new Wmap.WmapConfig(); defaults.Validate();
    Check(defaults.ExploreRadius == 1536, "default exploration radius is the maximum view distance");
    var wide = new Wmap.WmapConfig { ExploreRadius = 99999 }; wide.Validate();
    Check(wide.ExploreRadius == 4096, "exploration radius is bounded");
    mask.Reveal(ring.StartX + 100, 50000, 1536);
    Check(mask.At(ring.StartX + 100 + 1450, 50000) > 0.99, "charted well inside the radius");
    Check(mask.At(ring.StartX + 100, 50000 + 1650) == 0, "nothing charted beyond the radius");
    Check(mask.At(ring.EndX - 1300, 50000) > 0.99, "charting wraps across the seam");
    Check(mask.TakeDirty(out int dx0, out _, out int dx1, out _) && dx0 == 0 && dx1 == mask.Width, "a reveal across the seam uploads whole rows");
    // Saved exploration: a file that cannot be read now is not "empty"; a damaged one is.
    string folder = Path.Combine(Path.GetTempPath(), "wmap-mask-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    string file = Path.Combine(folder, Wmap.Client.ExploredMask.FileName);
    try
    {
        Check(Wmap.Client.ExploredMask.TryLoad(ring, file, out var none) && none.Data.All(b => b == 0), "a missing mask loads as empty");
        File.WriteAllBytes(file, Wmap.Client.ExploredMask.Pack(mask.Snapshot(), mask.Width, mask.Height));
        Check(Wmap.Client.ExploredMask.TryLoad(ring, file, out var saved) && saved.Data.AsSpan().SequenceEqual(mask.Data), "a saved mask round-trips");
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!Wmap.Client.ExploredMask.TryLoad(ring, file, out _), "a locked mask reports a failed read");
        byte[] whole = File.ReadAllBytes(file);
        File.WriteAllBytes(file, whole.AsSpan(0, 24).ToArray());
        Check(Wmap.Client.ExploredMask.TryLoad(ring, file, out var cut) && cut.Data.All(b => b == 0), "a truncated mask is damage and loads as empty");
    }
    finally { Directory.Delete(folder, true); }
}
// the player's arrow points where they look: (sin yaw, cos yaw) in world X/Z (level pitch π), as vanilla's arrow
foreach (double yaw in new[] { 0, .4, Math.PI / 2, 2.5, Math.PI, 4.2, 1.5 * Math.PI, 6 })
{
    var (tx, tz) = Heading.Tip(Heading.OfPlayer(yaw));
    Near2(tx, Math.Sin(yaw), 1e-9, $"player arrow x at yaw {yaw}");
    Near2(tz, Math.Cos(yaw), 1e-9, $"player arrow z at yaw {yaw}");
}
var (northX, northZ) = Heading.Tip(0);
Check(Math.Abs(northX) < 1e-12 && northZ < -0.999, "heading 0 points north (-Z, up the map)");
// wind layer: months land mid-month in the current year, compass points name where the wind comes from
{
    foreach (int choice in Enumerable.Range(WindGrid.Off, WindGrid.Choices))
    {
        Check(WindGrid.Visible(choice, false, true) == (choice != WindGrid.Off), "barometer unlocks each worldwide wind choice");
        Check(!WindGrid.Visible(choice, false, false), "gameplay without an instrument hides winds");
        Check(WindGrid.Visible(choice, true, false) == (choice != WindGrid.Off), "creative keeps each worldwide wind choice");
    }
    const double days = 108, perDay = 24, year = days * perDay;
    double now = 5 * year + 40 * perDay + 7;
    for (int m = 0; m < 12; m++)
    {
        double h = WindGrid.Hours(WindGrid.FirstMonth + m, now, days, perDay);
        Near2((h - 5 * year) / year, (m + 0.5) / 12, 1e-12, $"month {m + 1} is mid-month of this year");
    }
    Near(WindGrid.Hours(WindGrid.WeatherNow, now, days, perDay), now, "weather now is now");
    Near(WindGrid.Hours(WindGrid.PrevailingNow, now, days, perDay), now, "prevailing now is now");
    Check(WindGrid.Name(WindGrid.FirstMonth + 6) == "July" && WindGrid.Name(WindGrid.Off) == "off", "wind choice names");
    Check(WindGrid.From(-1, 0) == "E" && WindGrid.From(0, 1) == "N" && WindGrid.From(1, -1) == "SW" && WindGrid.From(-0.3, 0.15) == "ENE", "wind from compass points");
    var ring = new Topology { StartX = 1000, Width = 2048, Height = 1024, Wraps = true };
    var grid = WindGrid.Build((x, z) => (x, z), ring, CancellationToken.None);
    Check(grid.Length == WindGrid.Width * WindGrid.Height * 2, "wind grid size");
    Near2(grid[0], 1000 + 0.5 * 2048 / WindGrid.Width, 1e-3, "wind grid samples texel centres east");
    Near2(grid[(WindGrid.Height - 1) * WindGrid.Width * 2 + 1], 1024 - 0.5 * 1024 / WindGrid.Height, 1e-3, "wind grid rows run south");
    // the barometer's local field: a square round the player, wrapping over the seam, calm beyond the poles
    int n = WindGrid.LocalSize;
    var local = WindGrid.BuildLocal((x, z) => (x, z), ring, 2900, -100, 600, CancellationToken.None);
    Check(local.Length == n * n * 2, "local field size");
    double cell = 600.0 / n;
    int northmost = (int)Math.Ceiling(100 / cell - 0.5), row = northmost * n * 2;
    Check(local[row - n * 2] == 0 && local[row - n * 2 + 1] == 0, "beyond the pole is calm");
    Near2(local[row], 2900 + cell / 2, 1e-3, "local field starts at its west edge");
    Near2(local[row + (n - 1) * 2], 2900 + (n - 0.5) * cell - 2048, 1e-3, "local field wraps east over the seam");
    Near2(local[row + 1], -100 + (northmost + 0.5) * cell, 1e-3, "local field rows run south");
    Near2(WindGrid.Knots(1), 48.596, 1e-3, "one vanilla unit is 25 m/s");
    Check(WindGrid.Toward(1, 0) == "E" && WindGrid.Toward(0, -1) == "N" && WindGrid.Toward(-1, 1) == "SW", "compass point toward a direction");
}
// far terrain mesh: rings tile the square exactly, and stitched edges match the coarser ring (no cracks)
foreach (var (distance, cell0) in new[] { (8192.0, 32.0), (16384.0, 16.0), (2048.0, 64.0), (5000.0, 32.0), (8192.0, FarMesh.NearBandCell), (16384.0, FarMesh.NearBandCell) })
{
    var mesh = FarMesh.Build(distance, cell0);
    Check(mesh.Radius >= distance && mesh.RingRadius(Math.Max(0, mesh.Rings - 2)) < distance || mesh.Rings == 1, "far mesh reaches the distance with no spare ring");
    var v = mesh.Vertices; var idx = mesh.Indices;
    double area = 0;
    for (int i = 0; i < idx.Length; i += 3)
    {
        int a = idx[i] * 4, b = idx[i + 1] * 4, c3 = idx[i + 2] * 4;
        area += Math.Abs((v[b] - v[a]) * (v[c3 + 1] - v[a + 1]) - (v[c3] - v[a]) * (v[b + 1] - v[a + 1])) / 2;
    }
    Near2(area, 4 * mesh.Radius * mesh.Radius, 1e-3 * area, "far mesh rings tile the square without gaps or overlaps");
    // Heights depend on the ring through its mip level, as in the shader.
    double H(double x, double z, int ring) => Math.Sin(x * 0.0013) * 90 + Math.Cos(z * 0.0021 + x * 0.0004) * 60 + ring * 7.5;
    double Displaced(int vertex)
    {
        double x = v[vertex * 4], z = v[vertex * 4 + 1], sx = v[vertex * 4 + 2], sz = v[vertex * 4 + 3];
        int ring = mesh.RingAt(Math.Max(Math.Abs(x), Math.Abs(z)));
        return sx == 0 && sz == 0 ? H(x, z, ring) : (H(x - sx, z - sz, ring) + H(x + sx, z + sz, ring)) / 2;
    }
    var at = new Dictionary<(float, float), List<int>>();
    for (int i = 0; i < mesh.VertexCount; i++) { var key = (v[i * 4], v[i * 4 + 1]); if (!at.TryGetValue(key, out var list)) at[key] = list = new(); list.Add(i); }
    int stitched = 0;
    for (int ring = 1; ring < mesh.Rings; ring++)
    {
        double r = mesh.RingRadius(ring - 1), coarse = mesh.Cell0 * (1 << ring), fine = coarse / 2;
        for (double s = -r; s < r - 1e-6; s += fine)
        foreach (var (x, z) in new[] { (s, -r), (s, r), (-r, s), (r, s) })
        {
            Check(at.TryGetValue(((float)x, (float)z), out var here), "fine ring edge vertex exists");
            // The coarse edge through this point, between the coarse vertices either side of it.
            double along = (x == -r || x == r) ? z : x, lo = Math.Floor((along + r) / coarse) * coarse - r, hi = lo + coarse;
            double t0 = (along - lo) / coarse;
            (float, float) P(double a) => (x == -r || x == r) ? ((float)x, (float)a) : ((float)a, (float)z);
            double edge = (1 - t0) * Displaced(at[P(lo)][0]) + t0 * Displaced(at[P(hi)][0]);
            foreach (int i in here) { Near2(Displaced(i), edge, 1e-6, "far mesh stitch lies on the coarse edge"); if (v[i * 4 + 2] != 0 || v[i * 4 + 3] != 0) stitched++; }
        }
    }
    Check(mesh.Rings == 1 || stitched > 0, "far mesh has stitched vertices");
}
Check(FarMesh.Build(8192, FarMesh.NearBandCell).Snap == FarMesh.Build(8192, FarMesh.CellFor(1)).Snap, "the near band's finer rings keep the normal mesh's snap");

// far terrain tile windows: toroidal slots, unwrapped ids across the seam, texel centres as tiles are sampled
foreach (var (width, level, radius) in new[] { (204800.0, 2, 1536.0), (204800.0, 6, 8192.0), (204800.0 + 300, 2, 1536.0), (100000.0 + 77, 6, 16384.0), (5000.0, 2, 1024.0) })
{
    var topo = new Topology { StartX = 25600, Width = width, Height = 102400, Wraps = true };
    var win = new FarTileWindow(topo, level);
    Check(win.Fits(radius), $"window of {radius} fits in {FarTileWindow.Slots} slots at level {level}");
    var random = new Random(level * 1000 + (int)width);
    foreach (double origin in new[] { topo.StartX + 3, topo.EndX - 5, topo.StartX + topo.Width / 2, topo.EndX - win.Span / 3, topo.StartX + radius / 2 }.Concat(Enumerable.Range(0, 40).Select(_ => topo.StartX + random.NextDouble() * topo.Width)))
    {
        double rel0 = win.Relative(origin);
        Check(rel0 >= 0 && rel0 < topo.Width, "origin lies in its own lap");
        double camRel = rel0 + (random.NextDouble() - 0.5) * 256, camZ = 300 + random.NextDouble() * (topo.Height - 600);
        var cells = win.Window(camRel, camZ, radius);
        Check(cells.Count > 0, "window has tiles");
        Check(cells.Select(c => (FarTileWindow.Slot(c.Id), FarTileWindow.Slot(c.Z))).Distinct().Count() == cells.Count, "window tiles use distinct slots, also across the seam");
        Check(cells.Select(c => (c.Id, c.Z)).Distinct().Count() == cells.Count, "window lists each tile once");
        Check(cells.Zip(cells.Skip(1)).All(p => p.First.Distance <= p.Second.Distance), "window is nearest first");
        Check(cells.All(c => topo.Valid(c.Key)), "window tiles have canonical keys");
        Check(cells[0].Distance == 0, "the camera's own tile comes first");
        // Points around the camera map to a window tile whose canonical key covers their canonical position.
        for (int k = 0; k < 30; k++)
        {
            double px = camRel + (random.NextDouble() * 2 - 1) * radius * 0.99, pz = Math.Clamp(camZ + (random.NextDouble() * 2 - 1) * radius * 0.99, 0, topo.Height - 1);
            bool inX = win.TileX(px, out int id, out int index, out double inside), inZ = win.TileZ(pz, out int j, out double insideZ);
            Check(inX && inZ, "point inside the world");
            var cell = cells.FirstOrDefault(c => c.Id == id && c.Z == j);
            Check(cell.Key.Level == level && cell.Key.X == index, "point's tile is in the window");
            double worldX = topo.Canonical(topo.StartX + px);
            Near2(topo.StartX + index * win.Span + inside, worldX, 1e-6, "tile index and inside give back the position");
            Check(inside >= 0 && inside < win.Extent(id).Width + 1e-9 && insideZ >= 0 && insideZ < win.Span, "position lies inside its tile");
            // The texel coordinate puts texel p's centre where SemanticTile.Sample samples it: (p - 0.5) steps from the corner.
            double tx = win.Texel(inside);
            Check(tx >= 1 - 1e-9 && tx <= TileData.Padded - 1 + 1e-9, "texel coordinate stays inside the padded tile");
            int p = (int)Math.Floor(tx - 0.5);
            Near2(topo.StartX + index * win.Span + (p - 0.5) * win.Step, worldX - (tx - 0.5 - p) * win.Step, 1e-6, "texel centre matches the sampled position");
        }
    }
}
{
    // Crossing the seam keeps neighbours apart: ids either side of the jump differ by one.
    var topo = new Topology { StartX = 25600, Width = 204800 + 300, Height = 102400, Wraps = true };
    var win = new FarTileWindow(topo, 2);
    win.TileX(topo.Width - 1, out int last, out int lastIndex, out _);
    win.TileX(topo.Width + 1, out int next, out int nextIndex, out _);
    Check(next == last + 1 && nextIndex == 0 && lastIndex == win.LapTiles - 1, "seam tiles are consecutive ids");
    Check(win.Extent(last).Width == 300, "the seam's partial tile has the remaining width");
    Check(!new FarTileWindow(topo, 2).Fits(2048) && new FarTileWindow(topo, 2).Fits(1536), "a partial seam tile counts against the slots");
    var bounded = new Topology { StartX = 0, Width = 3000, Height = 3000, Wraps = false };
    var bw = new FarTileWindow(bounded, 2);
    Check(!bw.TileX(-1, out _, out _, out _) && !bw.TileX(3000, out _, out _, out _) && bw.TileX(2999, out _, out _, out _), "bounded worlds have no tiles beyond their edges");
    Check(bw.Window(100, 100, 1536).All(c => bounded.Valid(c.Key)), "bounded window stays inside the world");
}
// biome tiles: interpolated between source samples, exact at them, cheap to sample
{
    var topo = new Topology { StartX = 0, Width = 204800, Height = 102400, Wraps = true };
    int calls = 0;
    BiomeSample Field(double x, double z) { calls++; return new((byte)Math.Clamp(x / 4 % 256, 0, 255), (byte)Math.Clamp(z / 8 % 256, 0, 255), 77); }
    var key = new TileKey(0, 2, 10, 20);
    var rgba = BiomeTile.Sample(topo, key, Field, CancellationToken.None);
    Check(calls <= 34 * 34, $"biome tile samples every 16 blocks ({calls} calls)");
    var whole = BiomeTile.Unpack(BiomeTile.Pack(rgba));
    Check(whole.Length == 2 * BiomeTile.HalfBytes && whole.AsSpan(0, BiomeTile.HalfBytes).SequenceEqual(rgba), "biome tile round trip");
    bool noSeason = true;
    for (int i = BiomeTile.HalfBytes; i < whole.Length; i += 4)
        noSeason &= whole[i] == 0 && whole[i + 1] == 0 && whole[i + 2] == 128 && whole[i + 3] == 128;
    Check(noSeason, "a biome tile without a season source carries none (the game's own season map, no wet season)");
    Reject(() => SemanticTile.Unpack(BiomeTile.Pack(rgba)), "a biome tile is not a semantic tile");
    // 0.5.3-rc.5: the grass season below the biome, interpolated as the biome is; a 0.5.3-rc.4 server's tile (format 101,
    // no season) still reads, with none.
    SeasonSample Season(double x, double z) => SeasonSample.FromEarth(x < key.X * key.Span + key.Span / 2 ? 3 : 5,
        Enumerable.Range(0, 24).Select(i => (float)(1.5 * Math.Cos(2 * Math.PI * (i + 0.5) / 24 - 2 * Math.PI * 0.6))).ToArray());
    var seasoned = BiomeTile.Unpack(BiomeTile.Pack(BiomeTile.Sample(topo, key, Field, Season, CancellationToken.None)));
    int first = BiomeTile.HalfBytes + (4 * TileData.Padded + 4) * 4, last = BiomeTile.HalfBytes + (4 * TileData.Padded + 124) * 4;
    var wave = new SeasonSample(seasoned[first], seasoned[first + 1], seasoned[first + 2], seasoned[first + 3]);
    Check(seasoned.Length == 2 * BiomeTile.HalfBytes && seasoned[first] == 133 && seasoned[first + 1] == 0 && seasoned[last] == 255 && seasoned[last + 1] == 255,
        $"biome tiles carry Earth's grass season map below the biome (season 52: {seasoned[first]}, summer-dry: {seasoned[last]}/{seasoned[last + 1]})");
    Check(Math.Abs(wave.WetnessAt(0.6) - 1.5) < 0.05 && Math.Abs(wave.WetnessAt(0.1) + 1.5) < 0.05,
        $"a place's wet season survives as its yearly wave ({wave.WetnessAt(0.6):0.00} at its wettest, {wave.WetnessAt(0.1):0.00} half a year on)");
    var old = RasterCodec.Pack(rgba, TileData.Padded, TileData.Padded, 101, System.IO.Compression.CompressionLevel.Fastest);
    var fromOld = BiomeTile.Unpack(old);
    Check(fromOld.Length == 2 * BiomeTile.HalfBytes && fromOld.AsSpan(0, BiomeTile.HalfBytes).SequenceEqual(rgba) && fromOld[BiomeTile.HalfBytes + 2] == 128,
        "a biome tile from a server before 0.5.3-rc.5 reads, without a season");
    Check(SeasonSample.FromEarth(-1, null) == SeasonSample.None && SeasonSample.FromEarth(6, null).Share == 97 && SeasonSample.FromEarth(6, null).Dry == 255,
        "places without Earth's map get none; the steppe map is a short, dry season");
    for (int p = 0; p < TileData.Padded; p += 4)
    {
        double x = key.X * key.Span + (p - 0.5) * key.Step, z = key.Z * key.Span + (p - 0.5) * key.Step;
        var s = Field(x, z);
        int o = (p * TileData.Padded + p) * 4;
        Check(Math.Abs(rgba[o] - s.Forest) <= 1 && Math.Abs(rgba[o + 1] - s.Temperature) <= 1 && rgba[o + 2] == 77 && rgba[o + 3] == 255, "biome tile is exact at its samples");
    }
    calls = 0;
    BiomeTile.Sample(topo, new TileKey(0, 4, 1, 1), Field, CancellationToken.None);
    Check(calls == TileData.Padded * TileData.Padded, "level-4 biome tiles sample every texel (16 blocks)");
    calls = 0;
    BiomeTile.Sample(topo, new TileKey(0, 6, 1, 1), Field, CancellationToken.None);
    Check(calls == 66 * 66, $"climate biome tiles (level 6) sample every second texel ({calls} calls)");
    // Neighbouring tiles share their overlapping texels exactly: the same anchors on both sides.
    var west = BiomeTile.Sample(topo, new TileKey(0, 2, 10, 20), Field, CancellationToken.None);
    var east = BiomeTile.Sample(topo, new TileKey(0, 2, 11, 20), Field, CancellationToken.None);
    for (int z = 0; z < TileData.Padded; z++)
    for (int k = 0; k < 2; k++)
    {
        int a = (z * TileData.Padded + 128 + k) * 4, b = (z * TileData.Padded + k) * 4;
        Check(west[a] == east[b] && west[a + 1] == east[b + 1] && west[a + 2] == east[b + 2], "neighbouring biome tiles agree where they overlap");
    }
}
// The season maps' rows (0.5.3-rc.5): each column the map's mean over its rows and over the noise's shift, as far terrain
// sees a field; the vanilla grass map's summer green and late-autumn straw where the game has them.
{
    var strip = new byte[GrassSeasons.Rows * GrassSeasons.Columns * 4];
    int w = 128, h = 16;
    var map = new int[w * h];
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
        map[y * w + x] = unchecked((int)0xFF000000) | (x < 64 ? (40 << 16) | (200 << 8) | 20 : (220 << 16) | (180 << 8) | 60) + (y % 2);
    GrassSeasons.Row(strip, GrassSeasons.VanillaGrass, map, w, h);
    int Col(int c) => (GrassSeasons.VanillaGrass * GrassSeasons.Columns + c) * 4;
    Check(strip[Col(10)] == 40 && strip[Col(10) + 1] == 200 && strip[Col(100)] == 220 && strip[Col(100) + 1] == 180, "season rows keep each season's colour");
    Check(strip[Col(60)] > 40 && strip[Col(60)] < 220, "season rows blend across the noise's shift where the season turns");
    GrassSeasons.CopyRow(strip, GrassSeasons.VanillaGrass, 3);
    Check(strip.AsSpan(3 * GrassSeasons.Columns * 4, GrassSeasons.Columns * 4).SequenceEqual(strip.AsSpan(Col(0), GrassSeasons.Columns * 4)), "a missing Earth map takes the game's own row");
    // The climate's altitude row (0.5.3-rc.14, WM-0022): written from the game's adjustments and read back between columns.
    GrassSeasons.AltitudeRow(strip, above => (int)(above / 1.5f), 274);
    double cool200 = GrassSeasons.ReadAltitude(strip, 200, 274);
    Check(Math.Abs(cool200 - 200 / 1.5) < 1.6, $"vanilla's cooling at 200 blocks read back ({cool200:0.00})");
    double coolSea = GrassSeasons.ReadAltitude(strip, 0, 274);
    Check(Math.Abs(coolSea) < 1.1 && GrassSeasons.ReadAltitude(strip, -64, 274) < -40, "no cooling at the sea, warmer below it");
    GrassSeasons.AltitudeRow(strip, above => (int)(0.0065 * above * 33 * 4.25), 274);
    double coolReal = GrassSeasons.ReadAltitude(strip, 200, 274);
    Check(Math.Abs(coolReal - 0.0065 * 200 * 33 * 4.25) < 1.6, $"a real lapse (33 m a block) read back ({coolReal:0.0})");
    Check(strip.AsSpan(Col(0), GrassSeasons.Columns * 4).SequenceEqual(strip.AsSpan((GrassSeasons.VanillaGrass * GrassSeasons.Columns) * 4, GrassSeasons.Columns * 4)), "the altitude row leaves the colour rows alone");
}
// GPU drivers reject shader source with non-ASCII characters, even in comments (AMD: "unexpected end of file").
{
    string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var shaders = new[] { "shaders", "shaderincludes" }.Select(d => Path.Combine(root, "assets", "wmap", d)).Where(Directory.Exists).SelectMany(Directory.EnumerateFiles).ToList();
    Check(shaders.Count >= 3, $"shader sources found under {root}");
    foreach (string file in shaders) Check(File.ReadAllBytes(file).All(b => b < 128), "shader source is ASCII: " + Path.GetFileName(file));
}
// Right-click teleport: the column on the ring's own lap, inside the world; nothing beyond the poles or a bounded map's ends.
var teleportRing = new Topology { StartX = 25600, Width = 204800, Height = 102400, Wraps = true };
Check(Teleport.Column(teleportRing, teleportRing.EndX + 5.7, 300.2, 256000, 102400) == (25605, 300), "teleport across the seam lands on the ring's own lap");
Check(Teleport.Column(teleportRing, teleportRing.StartX - 0.25, 50, 256000, 102400) == (230399, 50), "teleport just west of the seam");
Check(Teleport.Column(teleportRing, 100000.9, 0.3, 256000, 102400) == (100000, 1), "teleport at the north edge stays inside the world");
Check(Teleport.Column(teleportRing, 100000, -0.01, 256000, 102400) == null && Teleport.Column(teleportRing, 100000, 102400, 256000, 102400) == null, "no teleport beyond the poles");
Check(Teleport.Column(teleportRing, double.NaN, 5, 256000, 102400) == null && Teleport.Column(teleportRing, 5, double.PositiveInfinity, 256000, 102400) == null, "no teleport to a non-number");
var boundedMap = new Topology { StartX = 0, Width = 1000, Height = 800 };
Check(Teleport.Column(boundedMap, 1000, 10, 1000, 800) == null && Teleport.Column(boundedMap, -0.5, 10, 1000, 800) == null, "no teleport past a bounded map's ends");
Check(Teleport.Column(boundedMap, 999.5, 799.5, 1000, 800) == (998, 798), "bounded map corner stays inside the world");
// Day and night grid: cell centres across the ring from its start, rows north to south, clamped bytes.
var dayGrid = new byte[DayGrid.Width * DayGrid.Height];
var sampled = new List<(double X, double Z)>();
DayGrid.BuildRows(teleportRing, (x, z) => { sampled.Add((x, z)); return x < teleportRing.StartX + teleportRing.Width / 2 ? 50.0 : z < 500 ? double.NaN : -10.0; }, dayGrid, 0, DayGrid.Height);
Check(sampled.Count == DayGrid.Width * DayGrid.Height, "day grid samples every cell once");
Near(sampled[0].X, teleportRing.StartX + teleportRing.Width / DayGrid.Width / 2, "day grid first cell centre x");
Near(sampled[0].Z, teleportRing.Height / DayGrid.Height / 2, "day grid first cell centre z");
Check(sampled.Max(p => p.X) < teleportRing.EndX && sampled.Max(p => p.Z) < teleportRing.Height, "day grid stays inside the ring");
Check(dayGrid[0] == 255 && dayGrid[DayGrid.Width - 1] == 0 && dayGrid[DayGrid.Width * 5 - 1] == 88 && DayGrid.Decode(88) == -10, "day grid clamps the sun's elevation, maps NaN to night and keeps quarter degrees");
var partial = new byte[DayGrid.Width * DayGrid.Height];
DayGrid.BuildRows(teleportRing, (x, z) => 1, partial, 120, 400);
Check(partial[119 * DayGrid.Width] == 0 && partial[120 * DayGrid.Width] == 132 && partial[^1] == 132, "day grid fills only the asked rows, clipped to the grid");
// Line of sight over the ground: open sea to the reach, a ridge's face but not the land behind it, unless the eye is higher.
{
    var world = new Topology { StartX = 0, Width = 8192, Height = 4096, Wraps = true };
    int gw = 512, gh = 256;   // 16-block cells
    var hs = new ushort[gw * gh];
    for (int z = 0; z < gh; z++) for (int x = 0; x < gw; x++)
    {
        double cx = (x + 0.5) * 16;
        hs[z * gw + x] = (ushort)(cx >= 4800 && cx < 4832 ? 100 : cx >= 4832 && cx < 6000 ? 10 : 0);
    }
    var grid = new HeightGrid(world, gw, gh, hs);
    bool Seen(List<(double X, double Z)> pts, double x, double z) => pts.Any(p => Math.Abs(world.Delta(p.X, x)) < 24 && Math.Abs(p.Z - z) < 24);
    var low = Viewshed.Cast(grid, 4000, 2048, 3, 3000);
    Check(Seen(low, 4400, 2048) && Seen(low, 4000 - 2900, 2048), "line of sight: open sea out to the reach");
    Check(Seen(low, 4816, 2048), "line of sight: the ridge's face");
    Check(!Seen(low, 5500, 2048) && !Seen(low, 5900, 2100), "line of sight: nothing behind the ridge");
    Check(!Seen(low, 4000 - 3200, 2048), "line of sight: nothing beyond the reach");
    var high = Viewshed.Cast(grid, 4000, 2048, 400, 3000);
    Check(Seen(high, 5500, 2048), "line of sight: a high eye sees over the ridge");
    var seam = Viewshed.Cast(grid, 100, 2048, 3, 1000);
    Check(Seen(seam, 8192 - 500, 2048), "line of sight across the seam");
    Check(low.Count < 3000 / 16 * 2 * Math.PI * 3000 / 16 + 100, "line of sight: one sample per cell along each ray");
    // 0.4.11: over a planet's curve (the radius of a 256,000-block ring) a low island 1600 blocks off sinks under the
    // horizon (it needs 15 blocks from an eye 3 above the sea), a 40-block one still shows; on a flat world both do.
    {
        var sea = new ushort[gw * gh];
        for (int z = 0; z < gh; z++) for (int x = 0; x < gw; x++)
        {
            double cx = (x + 0.5) * 16;
            sea[z * gw + x] = (ushort)(cx >= 5600 && cx < 5664 ? 5 : cx >= 2336 && cx < 2400 ? 40 : 0);
        }
        var planet = new HeightGrid(world, gw, gh, sea);
        double radius = 256000 / (2 * Math.PI);
        var curved = Viewshed.Cast(planet, 4000, 2048, 3, 3000, 0, radius);
        var flatSight = Viewshed.Cast(planet, 4000, 2048, 3, 3000);
        Check(!Seen(curved, 5632, 2048) && Seen(flatSight, 5632, 2048), "line of sight: a low island past the horizon sinks under it");
        Check(Seen(curved, 2368, 2048), "line of sight: a high island past the horizon still shows");
        Check(Seen(curved, 4300, 2048) && !Seen(curved, 4000 + 1200, 2048), "line of sight: the sea itself ends at the horizon");
        // Rays stop where not even the highest cell could rise to their steepest angle: the points seen are exactly those
        // of rays sampled to the reach (on the curve and flat, low and high eyes).
        static List<(double, double)> Full(HeightGrid g, double x, double z, double eye, double distance, double radius)
        {
            var seen = new List<(double, double)>();
            double step = g.Texel, curve = radius > 0 ? 1 / (2 * radius) : 0;
            int steps = (int)Math.Ceiling(distance / step), rays = Math.Max(8, (int)Math.Ceiling(2 * Math.PI * steps));
            for (int r = 0; r < rays; r++)
            {
                double a = 2 * Math.PI * r / rays, steepest = double.NegativeInfinity;
                for (int s = 1; s <= steps; s++)
                {
                    double d = s * step, px = x + Math.Cos(a) * d, pz = z + Math.Sin(a) * d;
                    int h = g.At(px, pz);
                    if (h < 0) break;
                    double slope = (h - curve * d * d - eye) / d;
                    if (slope >= steepest) seen.Add((px, pz));
                    steepest = Math.Max(steepest, slope);
                }
            }
            return seen;
        }
        foreach (var (g, eye, r) in new[] { (planet, 3.0, radius), (planet, 3.0, 0.0), (grid, 3.0, radius), (grid, 400.0, 0.0), (grid, 150.0, radius) })
            Check(Viewshed.Cast(g, 4000, 2048, eye, 3000, 0, r).SequenceEqual(Full(g, 4000, 2048, eye, 3000, r)), $"line of sight: stopping rays early changes nothing (eye {eye}, radius {r:0})");
    }
    // The grid from a chart: the highest of each 2 x 2, water at the sea surface.
    var chart = new BaseRaster { Width = 4, Height = 2, Rgba = new byte[4 * 2 * 4] };
    for (int i = 0; i < 8; i++) chart.Rgba[i * 4 + 1] = Semantic.EncodeSigned(-30, Semantic.DefaultHeightRange);
    chart.Rgba[(1 * 4 + 1) * 4 + 1] = Semantic.EncodeSigned(80, Semantic.DefaultHeightRange);
    var fromChart = HeightGrid.FromRaster(chart, new Topology { Width = 400, Height = 200, Wraps = true });
    Check(fromChart.Width == 2 && fromChart.Height == 1 && Math.Abs(fromChart.Heights[0] - 80) <= 1 && fromChart.Heights[1] == 0, "height grid keeps the highest point; water lies at the sea");
}
// 0.5.0-pre.11: a 512-block world with its sea at 110 keeps heights up to its top (a fixed 256-block range cut everything
// above 366 flat: the Tibetan plateau and the Himalayas); worlds up to 256 above the sea keep 256.
{
    Check(Semantic.HeightRangeFor(256, 110) == Semantic.DefaultHeightRange && Semantic.HeightRangeFor(0, 110) == Semantic.DefaultHeightRange
        && Semantic.HeightRangeFor(512, 110) == 403, "height range: 256, or the world's height above the sea");
    double range = Semantic.HeightRangeFor(512, 110);
    var plane = new Topology { Width = 2048, Height = 1024 };
    // A plateau 300 above the sea with a peak 380 above it; the old range drew both at 256.
    MapSample Tall(double x, double z) => x < 1024 ? new MapSample(110 - 1 + 300, 0, TileData.Land) : new MapSample(110 - 1 + 380, 0, TileData.Land);
    var tall = BaseRaster.Bake(plane, 64, 32, 110, () => Tall, null, 1, CancellationToken.None, null, range);
    double plateau = Semantic.DecodeSigned(tall.Rgba[(16 * 64 + 8) * 4 + 1], range), peak = Semantic.DecodeSigned(tall.Rgba[(16 * 64 + 56) * 4 + 1], range);
    Near2(plateau, 300, 4, "tall world chart: a plateau 300 above the sea");
    Near2(peak, 380, 4, "tall world chart: a peak 380 above the sea");
    Check(Semantic.DecodeSigned(Semantic.EncodeSigned(380, Semantic.DefaultHeightRange), Semantic.DefaultHeightRange) == 256, "the old range cut it at 256");
    var tile = SemanticTile.Sample(plane, new TileKey(0, 2, 3, 0), Tall, 110, CancellationToken.None, null, range);
    Near2(Semantic.DecodeSigned(tile[(60 * TileData.Padded + 60) * 4 + 1], range), 380, 4, "tall world prediction tile");
    var grid = HeightGrid.FromRaster(tall, plane, range);
    Check(grid.Highest >= 376 && grid.Highest <= 384, $"tall world line of sight keeps the peak ({grid.Highest})");
    // A standard world's chart is byte for byte as before.
    Check(Semantic.EncodeSigned(146, Semantic.HeightRangeFor(256, 110)) == Semantic.EncodeSigned(146, 256), "standard world unchanged");
}
// 0.4.4: each recording cell keeps exactly one sample, the four-block ones wherever the ring starts against their lattice
// (0.4.3 kept none above the four-block level: no four-block sample lies on a coarser cell's centre block).
{
    for (int ox = 0; ox < 4; ox++)
    for (int oz = 0; oz < 4; oz += 3)
    for (int level = 2; level <= 7; level++)
    {
        int cell = 1 << level;
        for (int k = 0; k < 2; k++)
        {
            int kept = 0;
            for (int bz = k * cell; bz < (k + 1) * cell; bz++)
            for (int bx = k * cell; bx < (k + 1) * cell; bx++)
                // Four-block samples lie at world x, z = 2 (mod 4); the ring starts ox, oz blocks past a multiple of 4.
                if ((bx + ox) % 4 == 2 && (bz + oz) % 4 == 2 && RecordingRules.Keeps(level, bx, bz)) kept++;
            Check(kept == 1, $"one four-block sample per level-{level} cell (ring offset {ox}, {oz}): {kept}");
        }
    }
    // A cell cut off by the world's east and south edges keeps a sample from its part inside the world (0.4.4 review).
    foreach (int extent in new[] { 4, 5, 7, 9, 13, 100, 200 })
    for (int ox = 0; ox < 4; ox++)
    for (int level = 3; level <= 8; level++)
    {
        int cell = 1 << level;
        if (extent >= cell) continue;
        double size = 3 * cell + extent;
        int kept = 0;
        for (int bz = 3 * cell; bz < size; bz++)
        for (int bx = 3 * cell; bx < size; bx++)
            if ((bx + ox) % 4 == 2 && (bz + ox) % 4 == 2 && RecordingRules.Keeps(level, bx, bz, size, size)) kept++;
        Check(kept == 1, $"one four-block sample in a level-{level} cell cut to {extent} blocks (offset {ox}): {kept}");
    }
    Check(RecordingRules.Keeps(1, 4, 6, 5, 7) && !RecordingRules.Keeps(1, 5, 6, 5, 7), "a two-block cell cut to one block keeps that block");
    int fineKept = 0;
    for (int bz = 0; bz < 2; bz++) for (int bx = 0; bx < 2; bx++) if (RecordingRules.Keeps(1, bx, bz)) fineKept++;
    Check(fineKept == 1 && RecordingRules.Keeps(0, 5, 7), "two-block cells keep one block, one-block cells every block");
    Check(RecordingRules.Levels(true, 9) == (0, 1) && RecordingRules.Levels(false, 9) == (2, 9), "fine samples go to the one- and two-block levels, four-block samples above");
}
// 0.4.4: the exploration mask spans the ring exactly, as the shader draws it, also when the ring is not a whole number of texels.
{
    var odd = new Topology { StartX = 1000, Width = 205000, Height = 102400, Wraps = true };
    var om = new Wmap.Client.ExploredMask(odd);
    Check(om.Width == 6407 && Math.Abs(om.TexelX * om.Width - odd.Width) < 1e-6 && Math.Abs(om.TexelZ * om.Height - odd.Height) < 1e-6, "mask texels span the ring exactly");
    foreach (double x in new[] { 1000.0, 1031.99, 1000 + 204999.9, 1000 + 102345.6 })
        Check(om.Cell(x, 5000) % om.Width == (int)Math.Floor((x - odd.StartX) / odd.Width * om.Width), "mask cell is the texel the shader samples");
    Check(om.Cell(odd.EndX + 0.01, 5000) == om.Cell(odd.StartX + 0.01, 5000), "mask wraps at the circumference");
    double cx = odd.EndX - 10, cz = 50000, radius = 600;
    om.Reveal(cx, cz, radius);
    int iz = (int)Math.Floor(cz / om.TexelZ), worst = 0;
    for (int ix = om.Width - 30; ix < om.Width + 30; ix++)
    {
        int column = ix % om.Width;
        double centreX = odd.StartX + (column + 0.5) * om.TexelX, centreZ = (iz + 0.5) * om.TexelZ;
        double d = Math.Sqrt(Math.Pow(odd.Delta(cx, centreX), 2) + Math.Pow(centreZ - cz, 2)) / om.Texel;
        int expected = (int)Math.Round(255 * Math.Clamp((radius / om.Texel + 1 - d) / 2, 0, 1));
        worst = Math.Max(worst, Math.Abs(om.Data[iz * om.Width + column] - expected));
    }
    Check(worst <= 2, $"a disc across the seam is charted by the true distance on both sides (worst {worst}/255)");
}
// 0.4.4: line of sight collects each mask cell once on the worker, the same cells as every visible sample.
{
    var world = new Topology { StartX = 0, Width = 8192, Height = 4096, Wraps = true };
    int gw = 512, gh = 256;
    var hs = new ushort[gw * gh];
    for (int z = 0; z < gh; z++) for (int x = 0; x < gw; x++) { double px = (x + 0.5) * 16; hs[z * gw + x] = (ushort)(px >= 4800 && px < 4832 ? 100 : px >= 4832 && px < 6000 ? 10 : 0); }
    var grid = new HeightGrid(world, gw, gh, hs);
    var sightMask = new Wmap.Client.ExploredMask(world);
    foreach (var (x, z, eye, reach) in new[] { (4000.0, 2048.0, 3.0, 3000.0), (100.0, 2048.0, 3.0, 1000.0), (4000.0, 100.0, 400.0, 3000.0) })
    {
        int[] cells = sightMask.SightCells(grid, x, z, eye, reach, 16);
        var all = Viewshed.Cast(grid, x, z, eye, reach, 16).Select(p => sightMask.Cell(p.X, p.Z)).Where(i => i >= 0).ToHashSet();
        Check(cells.Length == cells.Distinct().Count() && cells.ToHashSet().SetEquals(all), $"sight cells are the distinct cells of every visible sample ({x}, {z})");
        Check(cells.Length < Viewshed.Cast(grid, x, z, eye, reach, 16).Count / 4, "sight cells are far fewer than the samples");
    }
}
// 0.4.4: other mods' map layers (waypoints, ships) are drawn on every copy of the ring the atlas shows.
{
    var ring = new Topology { StartX = 1000, Width = 10000, Height = 5000, Wraps = true };
    // Three laps east, near the seam: the view (800 px, 8000 blocks) crosses into the next copy.
    var cam = new MapCamera { X = 1000 + 3 * 10000 + 9900, Z = 2500, Scale = 0.1 };
    Check(RingCopies.Laps(ring, cam, 800) == (0, 1), "the view shows its own copy and the next one");
    var next = RingCopies.View(ring, cam, 800, 600, 1);
    Near((1100 - next.X1) * cam.Scale, 420, "a marker just past the seam lies east of the camera on the next copy");
    var own = RingCopies.View(ring, cam, 800, 600, 0);
    Near((10500 - own.X1) * cam.Scale, 360, "a marker west of the camera on its own copy, laps away from the ring");
    Near(own.HalfHeight, 3000, "half the view's height in blocks");
    Check(RingCopies.LapAt(ring, cam, 800, 420) == 1 && RingCopies.LapAt(ring, cam, 800, 300) == 0, "the copy under the cursor");
    var bounded = new Topology { Width = 10000, Height = 5000 };
    var flatCam = new MapCamera { X = 5000, Z = 2500, Scale = 0.1 };
    Check(RingCopies.Laps(bounded, flatCam, 800) == (0, 0), "a bounded map has one copy");
    Near(RingCopies.View(bounded, flatCam, 800, 600, 0).X1, 1000, "a bounded map's view is the camera's");
    // 0.4.6: vanilla sizes its waypoint and ore markers as 1.5 * max(1, 1/zoom) px under the icon's width, for zoom 0.25..6.
    foreach (double scale in new[] { 0.0002, 0.1, 0.25, 1, 6, 40 })
    {
        float zoom = RingCopies.LayerZoom(scale);
        Check(zoom >= 0.25f && zoom <= 6, $"layers are told a zoom within vanilla's range at scale {scale}");
        Check(32 - 1.5 * Math.Max(1, 1 / zoom) > 0, $"a vanilla marker keeps a positive size at scale {scale}");
    }
    Near(RingCopies.LayerZoom(2), 2, "within vanilla's range the zoom is the atlas's own");
}
// 0.4.6: markers by zoom, and the automatic markers' kinds.
{
    Check(MarkerZoom.Detail(1) == 1 && MarkerZoom.Detail(MarkerZoom.DetailFull) == 1, "detail markers are whole up close");
    Check(MarkerZoom.Detail(MarkerZoom.DetailGone) == 0 && MarkerZoom.Detail(0.0002) == 0 && MarkerZoom.Detail(0) == 0, "detail markers are gone far out");
    Check(MarkerZoom.WaypointSize(1) == 1 && Math.Abs(MarkerZoom.WaypointSize(0.0002) - MarkerZoom.SmallestSize) < 1e-6, "own waypoints shrink to their smallest");
    double previous = 2, previousSize = 2;
    for (double scale = 2; scale > 1e-4; scale /= 1.1)
    {
        float detail = MarkerZoom.Detail(scale), size = MarkerZoom.WaypointSize(scale);
        Check(detail <= previous + 1e-6 && size <= previousSize + 1e-6 && size >= MarkerZoom.SmallestSize - 1e-6, $"markers fade and shrink steadily as the view widens ({scale})");
        previous = detail; previousSize = size;
    }
    Check(AutoMarkCatalog.Matches("looseores-*", "looseores-nativecopper-granite-free") && !AutoMarkCatalog.Matches("looseores-*", "looseboulders-granite"), "a trailing wildcard");
    Check(AutoMarkCatalog.Matches("mushroom-*-normal", "mushroom-fieldmushroom-normal") && !AutoMarkCatalog.Matches("mushroom-*-normal", "mushroom-fieldmushroom-harvested"), "a wildcard in the middle");
    Check(!AutoMarkCatalog.Matches("a-*-b", "a-b") && AutoMarkCatalog.Matches("a-*-b", "a--b") && AutoMarkCatalog.Matches("log", "log") && !AutoMarkCatalog.Matches("log", "logs"), "wildcard edges");
    Check(AutoMarkCatalog.ForBlock("fruitingbush-wild-blueberry-free")?.Code == "berries" && AutoMarkCatalog.ForBlock("fruitingbush-grown-blueberry-free") == null, "wild bushes, not the player's planted ones");
    Check(AutoMarkCatalog.ForBlock("bigberrybush-redcurrant-ripe")?.Code == "berries" && AutoMarkCatalog.ForBlock("statictranslocator-normal-north")?.Code == "translocators", "legacy bushes and translocators");
    Check(AutoMarkCatalog.ForBlock("log-resin-pine-ud")?.Code == "resin" && AutoMarkCatalog.ForBlock("log-placed-pine-ud") == null, "resin logs, not plain logs");
    Check(AutoMarkCatalog.ForEntity("trader-foods-male")?.Code == "traders" && AutoMarkCatalog.ForEntity("wolf-male") == null && AutoMarkCatalog.ForBlock(null) == null, "traders by entity");
    Check(AutoMarkCatalog.All.Select(c => c.Code).Distinct().Count() == AutoMarkCatalog.All.Length && AutoMarkCatalog.All.All(c => c.Radius > 0), "kinds are distinct");
    Check(new Wmap.WmapConfig().AutoMarkKinds.All(k => AutoMarkCatalog.Find(k) != null), "the default kinds exist");
    Check(AutoMarkCatalog.Title("Blueberry bush (ripe)") == "Blueberry bush" && AutoMarkCatalog.Title("Field mushroom") == "Field mushroom", "titles drop the state");
    Check(AutoMarkCatalog.IsAuto(AutoMarkCatalog.TagFor(AutoMarkCatalog.All[0])) && !AutoMarkCatalog.IsAuto(null) && !AutoMarkCatalog.IsAuto("My base"), "automatic markers are told by their text");
    var berries = AutoMarkCatalog.Find("berries");
    var existing = new[] { (100.0, 100.0, AutoMarkCatalog.TagFor(berries), "Blueberry bush") };
    Check(AutoMarkCatalog.Known(existing, berries, "Blueberry bush", 108, 100), "the same patch is marked once");
    Check(!AutoMarkCatalog.Known(existing, berries, "Cranberry bush", 108, 100), "another berry beside it is marked");
    Check(!AutoMarkCatalog.Known(existing, berries, "Blueberry bush", 100, 100 + berries.Radius + 1), "the same berry further on is marked");
    Check(!AutoMarkCatalog.Known(new[] { (100.0, 100.0, (string)null, "Blueberry bush") }, berries, "Blueberry bush", 100, 100), "a player's own waypoint does not stop a marker");
}
// 0.4.8: paper maps (WM-P05).
{
    var world = new Topology { StartX = 0, Width = 8192, Height = 4096 };
    var (mw, mh) = Wmap.Client.ExploredMask.Dimensions(world);
    var chart = new byte[mw * mh];
    chart[5] = 255; chart[mw * 3 + 7] = 128;
    var drawn = new TileData();
    drawn.Set(TileData.Padded + 1, new MapSample(130, 0, TileData.Land, 0x123456));
    drawn.Set(TileData.Padded + 2, new MapSample(100, 12, TileData.Water));
    var map = new PaperMap { Title = "Home", Author = "A", Drawn = "1. May, Year 1", Parts = MapParts.All, Mask = Wmap.Client.ExploredMask.Pack(chart, mw, mh) };
    map.Tiles.Add((new TileKey(1, 2, 3, 1), TileCodec.Encode(drawn)));
    map.Tiles.Add((new TileKey(1, 0, 10, 4), TileCodec.Encode(drawn)));
    map.Waypoints.Add(new MapWaypoint(100.5, 120, 200.5, "Base", "", "home", -1, true));
    map.Waypoints.Add(new MapWaypoint(300.5, 118, 40.5, "Blueberry bush", AutoMarkCatalog.TagFor(AutoMarkCatalog.Find("berries")), "berries", 5, false));
    map.Readings.Add(new MapReading(500, 90, 600, new byte[] { 1, 2, 3 }));
    byte[] bytes = map.Write();
    var back = PaperMap.Read(bytes, world, mw, mh);
    Check(back.Title == "Home" && back.Author == "A" && back.Drawn == map.Drawn && back.Parts == MapParts.All, "a paper map keeps its description");
    Check(Wmap.Client.ExploredMask.Unpack(back.Mask, mw, mh).SequenceEqual(chart), "a paper map keeps its charted area");
    Check(back.Tiles.Count == 2 && back.Tiles[0].Key == new TileKey(1, 2, 3, 1) && TileCodec.Decode(back.Tiles[1].Data).Heights[TileData.Padded + 1] == 130, "a paper map keeps its terrain");
    Check(back.Waypoints.SequenceEqual(map.Waypoints) && back.Readings.Count == 1 && back.Readings[0].Data.SequenceEqual(new byte[] { 1, 2, 3 }), "a paper map keeps its markers");
    Check(back.Holds == MapParts.All && back.Waypoints.Count(w => w.Auto) == 1, "a paper map holds all five parts, one marker automatic");
    var only = back.Only(MapParts.Terrain | MapParts.AutoMarks);
    Check(only.Mask == null && only.Tiles.Count == 2 && only.Waypoints.Count == 1 && only.Waypoints[0].Auto && only.Readings.Count == 0 && only.Holds == (MapParts.Terrain | MapParts.AutoMarks), "a reader takes only the parts they ticked");
    Check(new PaperMap().Holds == MapParts.None && PaperMap.Read(new PaperMap().Write(), world, mw, mh).Holds == MapParts.None, "an empty map holds nothing");
    // Damaged or foreign content is refused whole.
    Reject(() => PaperMap.Read(bytes[..^1], world, mw, mh), "a cut map");
    Reject(() => PaperMap.Read(bytes.Concat(new byte[] { 0 }).ToArray(), world, mw, mh), "data after a map");
    Reject(() => PaperMap.Read(bytes, world, mw + 1, mh), "a charted area of another world's size");
    Reject(() => PaperMap.Read(bytes, new Topology { StartX = 0, Width = 1024, Height = 512 }, mw, mh), "terrain outside this world");
    Reject(() => PaperMap.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, world, mw, mh), "not a map");
    var twice = new PaperMap(); twice.Tiles.Add(map.Tiles[0]); twice.Tiles.Add(map.Tiles[0]);
    Reject(() => PaperMap.Read(twice.Write(), world, mw, mh), "a tile twice");
    var broken = new PaperMap(); broken.Tiles.Add((new TileKey(1, 2, 0, 0), map.Tiles[0].Data.Select((v, i) => i == 40 ? (byte)(v ^ 1) : v).ToArray()));
    Reject(() => PaperMap.Read(broken.Write(), world, mw, mh), "a damaged tile");
    Check(PaperMap.Read(broken.Write(), world, mw, mh, check: false).Tiles.Count == 1, "a stored map is read without decoding its tiles");
    var nowhere = new PaperMap(); nowhere.Waypoints.Add(new MapWaypoint(double.NaN, 0, 0, "x", "", "circle", 0, false));
    Reject(() => PaperMap.Read(nowhere.Write(), world, mw, mh), "a waypoint nowhere");
    var tooLong = new PaperMap { Title = new string('x', PaperMap.MaxTitle + 1) };
    Reject(() => PaperMap.Read(tooLong.Write(), world, mw, mh), "a title too long");
    // Duplicates: reading a map twice, or one's own, adds nothing.
    var own = new List<MapWaypoint> { new(100, 120, 201, "Base", "", "home", 7, false) };
    Check(PaperMap.Has(own, map.Waypoints[0]), "a waypoint of the same title and icon a block away is had");
    Check(!PaperMap.Has(own, map.Waypoints[0] with { Icon = "x" }) && !PaperMap.Has(own, map.Waypoints[0] with { X = 104 }) && !PaperMap.Has(own, map.Waypoints[0] with { Title = "Camp" }), "another icon, place or title is new");
    Check(PaperMap.Has(new[] { (500.5, 90.0, 600.5) }, map.Readings[0]) && !PaperMap.Has(new[] { (502.0, 90.0, 600.0) }, map.Readings[0]), "readings within a block are had");
    // The server compares markers only with those bucketed near them in their group: every one within reach is among them,
    // across cells too, and none of another group.
    {
        var near = new Nearby<(double X, double Y, double Z, string G)>(2, p => (p.X, p.Y, p.Z), p => p.G);
        var random = new Random(11);
        var points = Enumerable.Range(0, 3000).Select(_ => (X: random.NextDouble() * 200 - 100, Y: random.NextDouble() * 20, Z: random.NextDouble() * 200 - 100, G: random.Next(2) == 0 ? "a" : "b")).ToList();
        foreach (var p in points) near.Add(p);
        bool all = true;
        for (int i = 0; i < 300; i++)
        {
            var probe = (X: random.NextDouble() * 200 - 100, Y: random.NextDouble() * 20, Z: random.NextDouble() * 200 - 100, G: "a");
            var around = near.Around(probe).ToHashSet();
            all &= around.All(p => p.G == "a") && points.Where(p => p.G == "a" && (p.X - probe.X) * (p.X - probe.X) + (p.Y - probe.Y) * (p.Y - probe.Y) + (p.Z - probe.Z) * (p.Z - probe.Z) <= 4).All(around.Contains);
        }
        Check(all, "markers within reach are among those bucketed nearby");
    }
    // Terrain: the reader's own cells win; only unknown ones are filled.
    var reader = new TileData();
    reader.Set(TileData.Padded + 1, new MapSample(140, 0, TileData.Land));
    Check(PaperMap.MergeCells(reader, drawn), "a map fills a reader's unknown cells");
    Check(reader.Heights[TileData.Padded + 1] == 140 && reader.Kinds[TileData.Padded + 2] == TileData.Water && reader.Depths[TileData.Padded + 2] == 12 && reader.Any, "the reader's own cell stays, the unknown one is filled");
    Check(!PaperMap.MergeCells(reader, drawn) && !PaperMap.MergeCells(reader, new TileData()), "merging again changes nothing");
    Near(PaperMap.ChartedKm2(chart, 32, 32), 2 * 32 * 32 / 1e6, "charted area in square kilometres");
}
// 0.4.9: hidden patches that line of sight encloses are charted, up to a size; the heading readout.
{
    var world = new Topology { StartX = 0, Width = 8192, Height = 4096, Wraps = true };
    int gw = 512, gh = 256;
    var hs = new ushort[gw * gh];
    // Flat land at 10 with pits at 0: a small one (96 blocks), a large one (600 blocks) and one across the sight's edge.
    bool Pit(double x, double z) => x >= 2000 && x < 2096 && z >= 2048 && z < 2144 || x >= 3000 && x < 3600 && z >= 1800 && z < 2400
        || x >= 3940 && x < 4100 && z >= 2048 && z < 2144;
    for (int z = 0; z < gh; z++) for (int x = 0; x < gw; x++) hs[z * gw + x] = (ushort)(Pit((x + 0.5) * 16, (z + 0.5) * 16) ? 0 : 10);
    var grid = new HeightGrid(world, gw, gh, hs);
    var mask = new Wmap.Client.ExploredMask(world);
    int small = mask.Cell(2048, 2096), large = mask.Cell(3300, 2100), edge = mask.Cell(3970, 2100);
    var plain = mask.SightCells(grid, 1000, 2100, 12, 3000, 16).ToHashSet();
    var filled = mask.SightCells(grid, 1000, 2100, 12, 3000, 16, 64).ToHashSet();
    Check(!plain.Contains(small) && !plain.Contains(large) && !plain.Contains(edge), "the pits' floors are hidden from the eye");
    Check(plain.Contains(mask.Cell(2120, 2096)) && plain.Contains(mask.Cell(1500, 2100)), "the ground around them is seen");
    Check(filled.Contains(small), "a small enclosed patch is charted");
    Check(!filled.Contains(large), "a patch larger than the limit stays hidden");
    Check(!filled.Contains(edge), "a patch across the sight's edge stays hidden");
    Check(plain.IsSubsetOf(filled) && filled.Count == filled.Distinct().Count(), "filling only adds, each cell once");
    // A patch next to charted ground counts as enclosed by it.
    mask.RevealCells(plain.ToArray());
    Check(mask.SightCells(grid, 1000, 2100, 12, 3000, 16, 64).Contains(small), "charted cells enclose a patch too");
    // Heading: yaw 0 looks south (+Z), π/2 east (+X).
    Check(Heading.Bearing(0) == 180 && Heading.Bearing(Math.PI / 2) == 90 && Heading.Bearing(Math.PI) == 0 && Heading.Bearing(-Math.PI / 2) == 270, "bearings from yaw");
    Check(Heading.Point(0) == "N" && Heading.Point(359) == "N" && Heading.Point(45) == "NE" && Heading.Point(202.5) == "SSW" && Heading.Point(-90) == "W", "compass points");
}

// The mid-distance view (WM-P06 prototype).
{
    // Colour and block light share the colour int; older recordings (no flag) have neither, and a colour alone is no change.
    int lit = RecordedColor.Pack(0x123456, 14), dark = RecordedColor.Pack(0x123456, 0);
    Check(RecordedColor.Has(lit) && RecordedColor.Light(lit) == 14 && RecordedColor.R(lit) == 0x56 && RecordedColor.B(lit) == 0x12, "colour and light packed");
    Check(!RecordedColor.Has(12345) && RecordedColor.Light(12345) == 0 && RecordedColor.Light(RecordedColor.Pack(0, 99)) == 31, "no colour before; light clamped");
    Check(RecordedColor.Changed(0, dark) && RecordedColor.Changed(dark, lit) && RecordedColor.Changed(lit, dark), "a first colour, a light coming on or going out is a change");
    Check(!RecordedColor.Changed(dark, RecordedColor.Pack(0x1A3C5E, 0)) && !RecordedColor.Changed(lit, RecordedColor.Pack(0x123456, 13)) && !RecordedColor.Changed(lit, 12345), "a small colour change alone, a small light change or no colour is not");
    Check(RecordedColor.Changed(dark, RecordedColor.Pack(0x654321, 0)), "a colour that moved far is a change (grass recorded again, 0.5.3-rc.6)");
    // Grassy soil is recorded as grass, which far terrain draws in its own seasonal grass colours (0.5.3-rc.7).
    int soilColor = (0x30 << 16) | (0x48 << 8) | 0x60;
    int grass = RecordedColor.Pack(RecordingRules.TopColor("sparse", () => soilColor), 14);
    Check(RecordedColor.IsGrass(grass) && RecordedColor.Light(grass) == 14 && RecordedColor.G(grass) == 134,
        "grassy soil is recorded as grass, a plain green, with its light");
    Check(RecordingRules.TopColor("none", () => soilColor) == soilColor && RecordingRules.TopColor(null, () => soilColor) == soilColor
        && !RecordedColor.IsGrass(RecordedColor.Pack(soilColor, 0)), "bare soil and other blocks keep the game's colour");
    var grassTile = new TileData(); RecordedColor.Mark(grassTile);
    grassTile.Set(200, new MapSample(100, 1, TileData.Land, grass));
    grassTile.Set(201, new MapSample(100, 1, TileData.Land, RecordedColor.Pack(soilColor, 0)));
    var grassTexels = new Half[TileData.Count * RecordedTexels.Halves]; var grassRgba = new byte[TileData.Count * 4];
    RecordedTexels.Build(grassTile, grassTexels, grassRgba);
    Check((float)grassTexels[200 * RecordedTexels.Halves + 2] == 0 && (float)grassTexels[201 * RecordedTexels.Halves + 2] == 1,
        "recorded grass is drawn in far terrain's colours, other ground in its recorded colour");
    Check(RecordedColor.Changed(RecordedColor.Pack(0x59A0AD, 0), RecordedColor.Pack(RecordedColor.GrassMarker, 0)), "grass recorded tan before is recorded again as grass");

    // Recorded over predicted, per cell. Texels: recorded cells covered, at their top (land's top face, water's surface);
    // the cells around take a neighbour's top and colour with no coverage.
    var rec = new TileData();
    RecordedColor.Mark(rec);
    int land = 40 * TileData.Padded + 40, sea = 40 * TileData.Padded + 60;
    rec.Set(land, new MapSample(150, 0, TileData.Land, RecordedColor.Pack(0x0000FF, 0)));
    rec.Set(sea, new MapSample(90, 20, TileData.Water, RecordedColor.Pack(0x00FF00, 0)));
    int uncolored = 80 * TileData.Padded + 80;
    rec.Set(uncolored, new MapSample(120, 0, TileData.Land));
    const int q = RecordedTexels.Halves;
    var heights = new Half[TileData.Count * q]; var texels = new byte[TileData.Count * 4];
    RecordedTexels.Build(rec, heights, texels);
    Check((float)heights[land * q] == 151 && (float)heights[land * q + 1] == 1 && (float)heights[land * q + 2] == 1 && (float)heights[land * q + 3] == 0 && texels[land * 4] == 255, "recorded land: top face, covered, its colour");
    Check((float)heights[sea * q] == 110.875f && (float)heights[sea * q + 3] == 1, "recorded water: its surface, water");
    Check((float)heights[uncolored * q] == 121 && (float)heights[uncolored * q + 1] == 1 && (float)heights[uncolored * q + 2] == 0, "a recording from before 0.5: covered, no colour");
    Check((float)heights[(land + 1) * q] == 151 && (float)heights[(land + 1) * q + 1] == 0 && texels[(land + 1) * 4] == 255, "a neighbour takes the top and colour, without coverage");
    Check((float)heights[5 * q + 1] == 0 && (float)heights[5 * q] == 0, "far from any recording: nothing");
    Check(RecordedTexels.Smooth(0, 30, false, 12) == 12, "not recorded: the prediction");
    Check(RecordedTexels.Smooth(1, 14, false, 12) == 14 && RecordedTexels.Smooth(1, 6, false, -20) == 6, "recorded land: the recorded height (WM-D61), also where the prediction is sea");
    Check(RecordedTexels.Smooth(1, 0, true, -25) == -25 && RecordedTexels.Smooth(1, 30, true, 30) == 30 && RecordedTexels.Smooth(1, 30, true, 26) == 30, "the sea keeps its predicted bed, a lake its recorded surface");
    Check(Math.Abs(RecordedTexels.Smooth(0.25, 16, false, 12) - 13) < 1e-9, "fading in: between");
    Check(TerracePatch.CellTop(160, 151, false, 150.4, false) == 160, "terraces: a two-block recording wins");
    Check(TerracePatch.CellTop(null, 152, false, 150.4, false) == 152 && TerracePatch.CellTop(null, 151.6, false, 150.4, false) == 152, "terraces: four-block land at its recorded height (WM-D61), at a whole block");
    Check(TerracePatch.CellTop(null, 110.875, true, 150.4, false) == 110.875 && TerracePatch.CellTop(null, 151, false, 109.875, true) == 151, "terraces: recorded water, or land where water is predicted, is recorded");
    Check(TerracePatch.CellTop(null, null, false, 150.4, false) == 150 && TerracePatch.CellTop(null, null, false, 109.875, true) == 109.875, "terraces: the prediction at a whole block, water at its surface");

    // Terraces: tops at whole blocks, and the walls close every edge between cells (each edge, split into one-block
    // pieces, is used an even number of times inside the patch), whatever the heights.
    var patch = TerracePatch.Build(8);
    Check(patch.Vertices.Length == 8 * 8 * TerracePatch.VerticesPerCell * TerracePatch.Floats && patch.Indices.Length == 8 * 8 * TerracePatch.IndicesPerCell, "terrace template size");
    Check(patch.Indices.All(i => i >= 0 && i < patch.Vertices.Length / TerracePatch.Floats), "terrace indices in range");
    var random = new Random(7);
    for (int trial = 0; trial < 20; trial++)
    {
        var tops = new int[10, 10];
        for (int i = 0; i < 10; i++) for (int j = 0; j < 10; j++) tops[i, j] = trial == 0 ? 5 : random.Next(0, 6) * (trial % 3 == 0 ? 7 : 1);
        var edges = new Dictionary<(int, int, int, int, int, int), int>();
        void Edge((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        {
            if (a.X == b.X && a.Z == b.Z)
            {
                // Vertical: one piece per block of its height.
                for (double y = Math.Min(a.Y, b.Y); y < Math.Max(a.Y, b.Y); y++) Count(((int)a.X, (int)y, (int)a.Z, (int)a.X, (int)y + 1, (int)a.Z));
                return;
            }
            var (p, q) = (a.X, a.Z).CompareTo((b.X, b.Z)) < 0 ? (a, b) : (b, a);
            Count(((int)p.X, (int)p.Y, (int)p.Z, (int)q.X, (int)q.Y, (int)q.Z));
        }
        void Count((int, int, int, int, int, int) key) { var (x0, _, z0, x1, _, z1) = key; if (x0 is 0 or 8 && x1 == x0 || z0 is 0 or 8 && z1 == z0) return; edges[key] = edges.GetValueOrDefault(key) + 1; }
        int quads = 0;
        foreach (var quad in patch.Quads((i, j) => tops[i, j]))
        {
            quads++;
            if (quad.Take(4).Any(v => v.Y != Math.Floor(v.Y))) throw new Exception("terrace vertex off a whole block");
            Edge(quad[0], quad[1]); Edge(quad[1], quad[3]); Edge(quad[3], quad[2]); Edge(quad[2], quad[0]);
        }
        Check(quads == 8 * 8 * 3, "three quads per cell");
        var open = edges.Where(p => p.Value % 2 != 0 && p.Key.Item1 < 8 && p.Key.Item3 < 8 && p.Key.Item4 <= 8 && p.Key.Item6 <= 8 && p.Key.Item1 > 0 && p.Key.Item3 > 0).ToList();
        Check(open.Count == 0, $"terraces closed (trial {trial}: {open.Count} open edges, first {open.FirstOrDefault().Key})");
    }

    // Lights: lit samples merge into at most four points per chunk; samples in another chunk stay apart; dim ones are left out.
    var litSamples = new List<LightSample>
    {
        new(10, 100, 10, 14), new(12, 101, 10, 12), new(14, 100, 13, 10),   // one lamp post's light, around (12, 11)
        new(30, 120, 30, 24),                                               // a lighthouse in the same chunk's corner
        new(33, 90, 30, 14),                                                // next chunk, 3 blocks east of it
        new(20, 100, 20, 3),                                                // too dim
    };
    for (int k = 0; k < 9; k++) litSamples.Add(new(64 + (k % 3) * 9.5, 100, 64 + (k / 3) * 9.5, 9));    // a town: nine lamps 9.5 apart in one chunk
    var lights = LightPoints.Merge(litSamples);
    var post = lights.Single(p => p.X < 20 && p.Z < 20);
    Check(post.Count == 3 && post.Y == 101 && Math.Abs(post.X - (10 * 14 + 12 * 12 + 14 * 10) / 36.0) < 1e-9, "a cluster merges at its light-weighted centre and highest top");
    Check(lights.Count(p => p.Count == 1 && p.Y == 120) == 1 && lights.Single(p => p.Y == 120).Brightness == 1f, "the lighthouse is its own bright point");
    Check(lights.Count(p => p.X >= 32 && p.X < 64 && p.Z < 32) == 1, "a light across a chunk's edge is not merged into it");
    Check(lights.Count(p => p.X >= 64) == LightPoints.PerChunk && lights.All(p => p.Brightness > 0 && p.Brightness <= 1), "at most four points per chunk");
    Check(!lights.Any(p => Math.Abs(p.X - 20) < 1 && Math.Abs(p.Z - 20) < 1 && p.Count == 1), "dim light left out");
    var lightTile = new TileData();
    RecordedColor.Mark(lightTile);
    lightTile.Set(5 * TileData.Padded + 3, new MapSample(100, 0, TileData.Land, RecordedColor.Pack(0, 15)));
    lightTile.Set(6 * TileData.Padded + 3, new MapSample(100, 0, TileData.Land, RecordedColor.Pack(0, 2)));
    var fromTile = LightPoints.Of(lightTile, new TileKey(1, 2, 3, 4)).ToList();
    Check(fromTile.Count == 1 && fromTile[0].X == (3 * 128 + 2 + 0.5) * 4 && fromTile[0].Z == (4 * 128 + 4 + 0.5) * 4 && fromTile[0].Y == 101, "lit samples of a tile at their cells, in blocks east of the ring's start");

    // Far terrain starts where the game's chunks fade, or nearer at the first chunk column not loaded (0.5.0-pre.2: a band of sky
    // after the view distance grew, while those columns loaded).
    Check(FarStart.For(512) == 384 && FarStart.For(40) == 48, "far terrain from 0.75 of the view distance, at least 48");
    // 0.5.3-rc.5: far terrain covers the game's blocks from where they fade (0.8 of the view distance); the loaded-columns
    // mask reaches that far at the game's largest view distance, a column to spare.
    // 0.5.3-rc.8: predicted forest stands at the recorded forest's middle treetop height once enough is recorded.
    var canopyTile = new TileData();
    for (int k = 0; k < 400; k++) canopyTile.Set((1 + k / 128) * TileData.Padded + 1 + k % 128, new MapSample(100, RecordingRules.CanopyDepth(100 + (k < 300 ? 15 : 3), 100), TileData.Land));
    canopyTile.Set(5 * TileData.Padded + 5, new MapSample(100, RecordingRules.CanopyDepth(100, 100), TileData.Land));
    var hist = RecordedTexels.CanopyHistogram(canopyTile);
    int median = RecordedTexels.MedianCanopy(new[] { hist, null }, out int treeCells);
    Check(hist[15] == 300 && hist[3] == 100 && hist[0] == 0 && treeCells == 400 && median == 15, $"recorded treetops counted by height (median {median} of {treeCells})");
    Check(FarStart.CanopyFor(15, 400) == 15 && FarStart.CanopyFor(15, 100) == 8 && FarStart.CanopyFor(60, 400) == 40 && FarStart.CanopyFor(0, 0) == 8,
        "predicted forest at the recorded height once enough is recorded, held to 4..40, else 8");
    Check(FarStart.GameFade(512) == 409.6 && FarStart.GameFade(0) == 0 && FarStart.MaskReach * 32 >= FarStart.GameFade(1536) + 32,
        "far terrain covers the game's fading blocks from 0.8 of the view distance, inside the loaded-columns mask");
    double loadedTo = FarStart.LoadedRadius((cx, cz) => Math.Abs(cx - 100) <= 12 && Math.Abs(cz - 100) <= 12, 100 * 32 + 16, 100 * 32 + 16, 0.75 * 700);
    Check(loadedTo == 12 * 32 + 16, $"loaded to the 12th column around ({loadedTo})");
    Check(FarStart.For(700, loadedTo) == 400, "after growing the view distance, far terrain starts where the loaded columns end");
    Check(FarStart.LoadedRadius((_, _) => true, 5000, 5000, 525) == 525 && FarStart.LoadedRadius((cx, _) => cx >= 0, 10, 5000, 300) == 300, "all loaded (or off the world's edge): the reach");
    // The loaded-columns mask is made again at once when the camera moves a column, jumps across Cylinder World's seam or is
    // teleported, or far terrain's start changes (0.5.0-pre.4; pre.3 only every 250 ms).
    Check(FarStart.ScanDue(double.NaN, 0, 384, 100, 100, 384, 0, long.MaxValue), "no mask yet: scan");
    Check(!FarStart.ScanDue(1000, 2000, 384, 1031, 1969, 384, 100, 250), "within a column, before it is due: the same mask");
    Check(FarStart.ScanDue(1000, 2000, 384, 1000, 2000, 384, 250, 250), "due after 250 ms");
    Check(FarStart.ScanDue(1000, 2000, 384, 1032, 2000, 384, 100, 250) && FarStart.ScanDue(1000, 2000, 384, 1000, 1968, 384, 100, 250), "a column's move: at once (pre.3: after up to 250 ms)");
    var seamRing = new Topology { StartX = 1184, Width = 250_000 - 2 * 1184, Height = 102_400, Wraps = true };
    double beforeJump = seamRing.StartX + 3, afterJump = beforeJump + seamRing.Width;
    Check(Math.Abs(seamRing.Delta(beforeJump, afterJump)) < 1 && FarStart.ScanDue(beforeJump, 50_000, 384, afterJump, 50_000, 384, 100, 250), "a jump across the seam (the same place on the ring, other chunks): at once");
    Check(FarStart.ScanDue(1000, 2000, 384, 1000, 2000, 525, 100, 250), "the view distance changed: at once");
    // Stale versus fresh after a teleport: the old mask has the column under the camera's new place as loaded or not by
    // where the camera was; the new one by where it is.
    var gapMask = new byte[FarStart.MaskSide * FarStart.MaskSide];
    System.Func<int, int, bool> gapAt = (cx, cz) => !(cx == 500 && cz == 500);
    FarStart.Scan(gapAt, 300 * 32 + 16, 500 * 32 + 16, 384, gapMask);
    var staleOffset = FarStart.MaskOffset(300 * 32 + 16, 500 * 32 + 16, 490 * 32 + 16, 500 * 32 + 16);
    bool staleDraws = FarStart.Draws(gapMask, staleOffset, 10 * 32, 0, 10 * 32, 384);
    FarStart.Scan(gapAt, 490 * 32 + 16, 500 * 32 + 16, 384, gapMask);
    bool freshDraws = FarStart.Draws(gapMask, FarStart.MaskOffset(490 * 32 + 16, 500 * 32 + 16, 490 * 32 + 16, 500 * 32 + 16), 10 * 32, 0, 10 * 32, 384);
    Check(!staleDraws && freshDraws, $"after a teleport the missing column is filled only with a new mask (old {staleDraws}, new {freshDraws})");

    // Recordings made before 0.4.4 hold Block.GetColor raw (alpha 0xFF in the top byte); 0.4.4 to 0.4.11 hold 0. Only a marked
    // tile's colours are this format (0.5.0-pre.3; pre.2 read 0xFF as "colour, light 31": false lights at night).
    var legacy = new TileData();
    int legacyCell = 20 * TileData.Padded + 20;
    legacy.Set(legacyCell, new MapSample(100, 0, TileData.Land, unchecked((int)0xFF336699)));
    legacy.Set(legacyCell + 1, new MapSample(100, 0, TileData.Land, unchecked((int)0xFFFFFFFF)));
    Check(!RecordedColor.Marked(legacy) && RecordedColor.Of(legacy, legacyCell) == 0, "a recording from before is not this format");
    // A recorder from before 0.4.4 run on a marked tile (a rollback) writes raw colours into it: the mark stays, so each colour
    // must be told apart too (0.5.0-pre.4; pre.3 read 0xFF as light 31).
    var rolledBack = new TileData(); RecordedColor.Mark(rolledBack);
    rolledBack.Set(legacyCell, new MapSample(100, 0, TileData.Land, unchecked((int)0xFF336699)));
    rolledBack.Set(legacyCell + 1, new MapSample(100, 0, TileData.Land, RecordedColor.Pack(0x336699, 31)));
    rolledBack.Set(legacyCell + 2, new MapSample(100, 0, TileData.Land, RecordedColor.Pack(0x336699, 0)));
    Check(!RecordedColor.Has(RecordedColor.Of(rolledBack, legacyCell)) && RecordedColor.Light(RecordedColor.Of(rolledBack, legacyCell)) == 0, "a raw colour in a marked tile (a rollback): no colour, no light");
    Check(RecordedColor.Light(RecordedColor.Of(rolledBack, legacyCell + 1)) == 31 && RecordedColor.Has(RecordedColor.Of(rolledBack, legacyCell + 2)), "this format's colours at light 31 and 0");
    Check(LightPoints.Of(rolledBack, new TileKey(1, 2, 0, 0)).Count() == 1, "one light: this format's, not the rollback's");
    for (int top = 0; top < 256; top++)
        if (RecordedColor.Has(top << 24) != (top >= 0x80 && top <= 0xBF)) { Check(false, $"top byte {top:X2}"); break; }
    Check(!RecordingRules.Structure(new MapSample(220, 0, TileData.Land), 220), "natural cliff is not a structure even if prediction is low");
    Check(!RecordingRules.Structure(new MapSample(400, 0, TileData.Ice), 400), "natural glacier is not a structure");
    Check(!RecordingRules.Structure(new MapSample(221, 0, TileData.Land), 220), "snow on natural ground is not a structure");
    Check(!RecordingRules.Structure(new MapSample(250, 0, TileData.Land), 0), "missing worldgen baseline cannot authorize a box");
    Check(RecordingRules.Structure(new MapSample(248, 0, TileData.Land), 220), "tower above generated terrain is a structure");
    int structure = RecordedColor.Pack(0x123456, 14, true);
    Check(RecordedColor.IsStructure(structure) && RecordedColor.Light(structure) == 14, "structure provenance keeps colour and light");
    Check(RecordedColor.Changed(structure, lit) && RecordedColor.Changed(lit, structure), "structure removal and addition rewrite the cell");
    var v5 = new TileData(); v5.Colors[0] = RecordedColor.LegacyMarkValue;
    v5.Set(legacyCell, new MapSample(220, 0, TileData.Land, lit));
    v5.Colors[legacyCell + 1] = structure;
    Check(RecordedColor.Of(v5, legacyCell) == lit && RecordedColor.Of(v5, legacyCell + 1) == 0, "v5 cannot supply structure provenance");
    RecordedColor.Mark(v5);
    Check(RecordedColor.Current(v5) && RecordedColor.Of(v5, legacyCell) == lit && RecordedColor.Of(v5, legacyCell + 1) == 0, "v5 upgrade keeps valid light but no boxes");
    Check(!LightPoints.Of(legacy, new TileKey(1, 2, 0, 0)).Any(), "no lights from an older recorder's colours");
    var legacyTexels = new Half[TileData.Count * RecordedTexels.Halves]; var legacyRgba = new byte[TileData.Count * 4];
    RecordedTexels.Build(legacy, legacyTexels, legacyRgba);
    Check((float)legacyTexels[legacyCell * RecordedTexels.Halves + 1] == 1 && (float)legacyTexels[legacyCell * RecordedTexels.Halves + 2] == 0, "an older recording: its height, not its colour");
    // Marking clears the older colours; the mark sits where no sample or merge writes (the padding corner).
    var marked = legacy.Copy();
    RecordedColor.Mark(marked);
    Check(RecordedColor.Marked(marked) && marked.Colors[legacyCell] == 0 && marked.Heights[legacyCell] == 100 && !marked.Any == !legacy.Any, "marking clears older colours, keeps the terrain");
    Check(TileCodec.Decode(TileCodec.Encode(marked)) is var codec && RecordedColor.Marked(codec) && codec.Kinds[RecordedColor.MarkIndex] == TileData.Unknown, "the mark survives the tile codec as an unknown cell (older clients read it as nothing)");
    // Paper maps: an older map's colours are not copied into a marked tile; a marked map marks the reader's tile.
    var reader = new TileData(); RecordedColor.Mark(reader);
    PaperMap.MergeCells(reader, legacy);
    Check(reader.Heights[legacyCell] == 100 && RecordedColor.Of(reader, legacyCell) == 0, "an older map's colours stay out of a marked tile");
    var oldReader = new TileData(); oldReader.Set(5 * TileData.Padded + 5, new MapSample(90, 0, TileData.Land, unchecked((int)0xFF112233)));
    var newMap = new TileData(); RecordedColor.Mark(newMap); newMap.Set(legacyCell, new MapSample(100, 0, TileData.Land, RecordedColor.Pack(0x123456, 14)));
    PaperMap.MergeCells(oldReader, newMap);
    Check(RecordedColor.Marked(oldReader) && RecordedColor.Of(oldReader, 5 * TileData.Padded + 5) == 0 && RecordedColor.Light(RecordedColor.Of(oldReader, legacyCell)) == 14, "a marked map marks the reader's tile, clearing its older colours");

    // Window slots: a tile placed once keeps its slot while the camera moves, and across the seam (a ring that is not a
    // whole number of tiles, so the seam tile is partial) when the lap is counted on.
    foreach (int level in new[] { 1, 2 })
    {
        var ring = new Topology { StartX = 1000, Width = 10240 + 300, Height = 40960, Wraps = true };
        var window = new FarTileWindow(ring, level);
        double radius = level == 1 ? 768 : 1536;
        Check(window.Fits(radius), $"level {level}: the window fits the slots");
        var table = new SlotTable(); var laps = new LapCounter();
        var previous = new HashSet<TileKey>();
        int kept = 0, moved = 0, crossings = 0, lastLap = 0;
        void Step(double x, double z)
        {
            double snap = 256, originX = Math.Floor(x / snap + 0.5) * snap;
            laps.Update(ring, originX);
            if (laps.Lap != lastLap) { crossings++; lastLap = laps.Lap; }
            double originRel = window.Relative(originX), cameraRel = originRel + ring.Delta(originX, x);
            var cells = window.Window(cameraRel, z, radius).Select(c => c with { Id = c.Id + laps.Lap * window.LapTiles }).ToList();
            Check(cells.Select(c => SlotTable.SlotOf(c.Id, c.Z)).Distinct().Count() == cells.Count, $"level {level}: distinct slots");
            // A tile in the window before and now is still in its slot, as RecordedTiles.Place finds it (at most re-keyed).
            foreach (var c in cells.Where(c => previous.Contains(c.Key))) { if (table.Keys[SlotTable.SlotOf(c.Id, c.Z)] == c.Key) kept++; else moved++; }
            foreach (var c in cells)
            {
                if (table.Keys[SlotTable.SlotOf(c.Id, c.Z)] == c.Key) { if (!table.Holds(c)) table.Rekey(c); }
                else table.Place(c, 1, 0);
                Check(table.Holds(c), $"level {level}: tile held");
            }
            previous = cells.Select(c => c.Key).ToHashSet();
        }
        double x0 = ring.StartX + 3000, zc = 20000;
        for (int k = 0; k < 40; k++) Step(x0 + k * 37, zc + k * 11);
        Check(moved == 0 && kept > 500, $"level {level}: moving never moves a resident tile ({moved} moved, {kept} kept)");
        // Across the seam both ways: the camera steps from just east of the ring's start to just west of it, and back.
        previous.Clear();
        for (int k = 0; k < 30; k++) Step(ring.StartX + 400 - k * 30, zc);
        for (int k = 0; k < 30; k++) Step(ring.StartX - 500 + k * 30, zc);
        Check(crossings == 2 && laps.Lap == 0 && moved == 0, $"level {level}: across the seam and back, tiles keep their slots ({crossings} crossings, lap {laps.Lap}, {moved} moved)");
    }
}
// The climate raster on Earth's built-in heights (EarthHeights): Earth's per-thread switch is on during each sample and
// back as it was after it, also after a failed sample; the pool threads that baked are left as they were; no switch
// without Earth (here a stand-in, EarthMap.Core.FineElevation in this assembly).
{
    var earth = typeof(EarthMap.Core.FineElevation).Assembly;
    bool During() => EarthMap.Core.FineElevation.BuiltInOnly;
    var wrapped = Wmap.Integrations.EarthHeights.OnBuiltInHeights(earth, (x, z) => During() ? (int)(x + z) : -1);
    Check(wrapped != null, "Earth's switch found");
    EarthMap.Core.FineElevation.BuiltInOnly = false;
    Check(wrapped(2, 3) == 5 && !During(), "switch on during a sample and off after it");
    EarthMap.Core.FineElevation.BuiltInOnly = true;
    Check(wrapped(2, 3) == 5 && During(), "a thread already on built-in heights stays on them");
    EarthMap.Core.FineElevation.BuiltInOnly = false;
    var failing = Wmap.Integrations.EarthHeights.OnBuiltInHeights<int>(earth, (x, z) => throw new InvalidOperationException());
    Check(Throws(() => failing(0, 0)) && !During(), "switch put back after a failed sample");
    Check(Wmap.Integrations.EarthHeights.OnBuiltInHeights(typeof(string).Assembly, (x, z) => 1) == null, "no switch in an assembly without Earth's");
    Check(Wmap.Integrations.EarthHeights.OnBuiltInHeights(null, (x, z) => 1) == null, "no switch without Earth");
    Check(Wmap.Integrations.EarthHeights.OnBuiltInHeights<int>(earth, null) == null, "no sampler without a biome source");
    int off = 0, on = 0;
    var biome = Wmap.Integrations.EarthHeights.OnBuiltInHeights(earth, (x, z) =>
    {
        if (During()) Interlocked.Increment(ref on); else Interlocked.Increment(ref off);
        return new BiomeSample(1, 2, 3);
    });
    var world = new Topology { Width = 40000, Height = 20000, Wraps = true };
    var rgba = ClimateRaster.Bake(world, 64, 32, biome, 8, CancellationToken.None);
    Check(on == 64 * 32 && off == 0 && rgba[4 * 100] == 1 && rgba[4 * 100 + 3] == 255, $"every climate sample on built-in heights ({on} on, {off} off)");
    int leaked = 0;
    Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => { if (During()) Interlocked.Increment(ref leaked); Thread.SpinWait(2000); });
    Check(leaked == 0, $"pool threads left on GLO-90 after the raster ({leaked} on built-in heights)");
}
// A four-block sample's height is its cell's mean generated ground (WM-0021, WM-D61).
{
    var generated = new ushort[1024];
    for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) generated[z * 32 + x] = (ushort)(100 + x);
    // Cell columns 0..3 (sample column 2): heights 100..103, mean 101.5, rounded 102.
    Check(RecordingRules.CellMean(generated, 2, 2, 102) == 102, "a slope's cell takes its mean ground");
    Check(RecordingRules.CellMean(generated, 2, 2, 103) == 103, "a block of snow on the sample column stays on the mean");
    Check(RecordingRules.CellMean(generated, 2, 2, 110) == null, "a sample far off the generated ground (a ruin, a pond) keeps its own height");
    Check(RecordingRules.CellMean(null, 2, 2, 102) == null && RecordingRules.CellMean(generated, 1, 2, 101) == null, "no height map or no whole cell: no mean");
    // A bank beside a river: half the cell is the bed 6 blocks down, under water; the bank keeps its own surface.
    var bank = new ushort[1024];
    for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) bank[z * 32 + x] = (ushort)(x < 2 ? 105 : 111);
    Check(RecordingRules.CellMean(bank, 2, 2, 111) == 111, "a bank's cell leaves the river bed out of its mean");
    // A ledge: 8 columns at 100 and 8 at 104, the sample on the high side, stays on its side.
    Check(RecordingRules.CellMean(bank.Select(h => (ushort)(h == 105 ? 100 : 104)).ToArray(), 2, 2, 104) == 104, "a ledge's high side keeps its height");
    // Open ground only: a canopy measured from the column's own ground is 1 + nothing.
    Check(RecordingRules.CanopyDepth(0, 0) == 1 && RecordingRules.CanopyDepth(111, 111) == 1, "open ground's canopy depth is 1");
}

// The pick's share of host blocks (WM-0025): only host blocks within the deposit's band count, over the whole column.
{
    var hostRock = new HashSet<int> { 7, 9 };
    int[] rockColumn = new int[100];
    for (int y = 0; y < 100; y++) rockColumn[y] = y < 40 ? 7 : y < 60 ? 9 : 3;
    Check(OreHeat.HostShare(rockColumn, null, 0, 0, 0, 80) == 1, "no host list: every block hosts the ore");
    Check(Math.Abs(OreHeat.HostShare(rockColumn, hostRock, 0, 0, 0, 80) - 0.60) < 1e-9, "whole column: 60 host blocks of 100");
    Check(Math.Abs(OreHeat.HostShare(rockColumn, hostRock, 1, 0.5, 0, 80) - 0.20) < 1e-9, "following the surface: from half the surface (40) up to it, hosts at 40-59");
    Check(Math.Abs(OreHeat.HostShare(rockColumn, hostRock, 2, 10, 19, 80) - 0.10) < 1e-9, "a fixed band: 10 to 19");
    Check(Math.Abs(OreHeat.HostShare(rockColumn, hostRock, 3, 0, 30, 80) - 0.10) < 1e-9, "below the surface: 50 to 80, hosts at 50-59");
    Check(OreHeat.HostShare(rockColumn, new HashSet<int> { 5 }, 0, 0, 0, 80) == 0, "no host rock in the column: 0");
}

// Ore heat maps (WM-D47, tiled in WM-D48): levels, the view's oreTiles, cell samples, stand-ins from coarser oreTiles, stored oreTiles; colours and words.
{
    var ring = new Topology { StartX = 0, Width = 5009920, Height = 2003968, Wraps = true };
    var oreFlat = new Topology { StartX = 1000, Width = 64000, Height = 32000, Wraps = false };
    var tiny = new Topology { StartX = -300, Width = 900, Height = 700, Wraps = false };
    Check(OreHeat.CellSize(OreHeat.MinLevel) == OreHeat.ChunkSize && OreHeat.Span(7) == OreHeat.CellSize(7) * OreHeat.TileCells && OreHeat.Span(7) == new TileKey(0, 7, 0, 0).Span, "ore oreTiles are the atlas's oreTiles, finest cells one chunk");
    foreach (var (topo, label) in new[] { (ring, "ring"), (oreFlat, "flat"), (tiny, "tiny") })
    {
        int max = OreHeat.MaxLevel(topo);
        Check(max >= OreHeat.MinLevel && OreHeat.Span(max) >= Math.Max(topo.Width, topo.Height), $"top ore level holds the world ({label})");
        foreach (double scale in new[] { 8.0, 1, 0.25, 1.0 / 32, 1.0 / 400, 1e-5 })
        {
            int level = OreHeat.LevelFor(topo, scale);
            Check(level >= OreHeat.MinLevel && level <= max, $"ore level in range ({label}, {scale})");
            Check(OreHeat.CellSize(level) * scale >= OreHeat.MinCellPixels || level == max, $"ore cells large enough on screen ({label}, {scale})");
            Check(level == OreHeat.MinLevel || OreHeat.CellSize(level - 1) * scale < OreHeat.MinCellPixels, $"ore level the finest allowed ({label}, {scale})");
            double halfW = 900 / scale, halfH = 500 / scale, cz = topo.Height / 2;
            // The camera's zoom limit keeps a view within a few laps of the ring.
            if (topo.Wraps && 2 * halfW > 4 * topo.Width) continue;
            foreach (double cx in new[] { topo.StartX + 7, topo.StartX + topo.Width / 3, topo.EndX - 5 })
            {
                var oreTiles = OreHeat.Visible(topo, cx - halfW, cz - halfH, cx + halfW, cz + halfH, level);
                Check(oreTiles.Count > 0 && oreTiles.All(v => OreHeat.ValidKey(topo, level, v.Key.X, v.Key.Z)), $"ore oreTiles of the view valid ({label}, {scale}, {cx})");
                foreach (double fx in new[] { 0.01, 0.5, 0.99 }) foreach (double fz in new[] { 0.01, 0.5, 0.99 })
                {
                    double hx = cx - halfW + 2 * halfW * fx, hz = cz - halfH + 2 * halfH * fz;
                    if (hz < 0 || hz >= topo.Height || !topo.Wraps && (hx < topo.StartX || hx >= topo.EndX)) continue;
                    Check(oreTiles.Any(v => hx >= v.X && hx < v.X + OreHeat.Span(level) && hz >= v.Z && hz < v.Z + OreHeat.Span(level)), $"ore oreTiles cover the view ({label}, {scale}, {fx}, {fz})");
                    var (kx, kz) = OreHeat.KeyAt(topo, level, hx, hz);
                    Check(oreTiles.Any(v => v.Key.X == kx && v.Key.Z == kz && hx >= v.X && hx < v.X + OreHeat.Span(level)), $"ore tile lookup matches the view's ({label}, {scale})");
                    int cell = OreHeat.CellAt(topo, level, kx, kz, hx, hz);
                    Check(cell >= 0, $"ore cell lookup ({label}, {scale})");
                    var (sx, sz) = OreHeat.Sample(topo, level, kx, kz, cell % OreHeat.TileCells, cell / OreHeat.TileCells);
                    if (double.IsNaN(sx)) continue;
                    Check((sx - OreHeat.ChunkSize / 2.0) % OreHeat.ChunkSize == 0 && (sz - OreHeat.ChunkSize / 2.0) % OreHeat.ChunkSize == 0, $"ore samples chunk centres ({label}, {scale})");
                    // Cells are whole chunks where the ring starts on a chunk; elsewhere the finest cells may sample a neighbour's chunk.
                    bool aligned = topo.StartX % OreHeat.ChunkSize == 0 || level > OreHeat.MinLevel;
                    if (aligned) Check(OreHeat.CellAt(topo, level, kx, kz, sx, sz) == cell, $"ore sample inside its cell ({label}, {scale})");
                }
            }
        }
    }
    // The coarse oreTiles stored ahead: whole levels from the top, coarsest first, as deep as the budget allows.
    var ahead = OreHeat.StoreAheadKeys(ring, 400);
    Check(ahead.Count <= 400 && ahead[0] == (OreHeat.MaxLevel(ring), 0, 0) && ahead.Zip(ahead.Skip(1)).All(p => p.First.Level >= p.Second.Level), "ore store-ahead coarsest first, within the budget");
    int lowest = ahead[^1].Level;
    double TilesAt(Topology t, int level) => Math.Ceiling(t.Width / OreHeat.Span(level)) * Math.Ceiling(t.Height / OreHeat.Span(level));
    Check(ahead.Count(k => k.Level == lowest) == TilesAt(ring, lowest) && ahead.Count + TilesAt(ring, lowest - 1) > 400, $"ore store-ahead whole levels, as deep as allowed (to level {lowest}, {ahead.Count} oreTiles)");
    Check(ahead.All(k => OreHeat.ValidKey(ring, k.Level, k.X, k.Z)) && ahead.Distinct().Count() == ahead.Count, "ore store-ahead keys valid and distinct");
    Check(OreHeat.StoreAheadKeys(tiny, 400).Count == 1 && OreHeat.StoreAheadKeys(ring, 0).Count == 1, "store-ahead keeps at least the top tile");
    Check(!OreHeat.ValidKey(ring, OreHeat.MinLevel - 1, 0, 0) && !OreHeat.ValidKey(ring, OreHeat.MaxLevel(ring) + 1, 0, 0) && !OreHeat.ValidKey(ring, 5, -1, 0)
        && !OreHeat.ValidKey(ring, 5, (int)Math.Ceiling(ring.Width / OreHeat.Span(5)), 0) && !OreHeat.ValidKey(ring, 5, 0, (int)Math.Ceiling(ring.Height / OreHeat.Span(5)))
        && OreHeat.ValidKey(ring, 5, 0, 0) && !OreHeat.ValidKey(null, 5, 0, 0), "ore tile validation");
    // Samples past the ring's end or the south edge have no answer; the last tile's first cells do.
    int lastX = (int)Math.Ceiling(ring.Width / OreHeat.Span(10)) - 1, lastZ = (int)Math.Ceiling(ring.Height / OreHeat.Span(10)) - 1;
    Check(double.IsNaN(OreHeat.Sample(ring, 10, lastX, 0, OreHeat.TileCells - 1, 0).X) && !double.IsNaN(OreHeat.Sample(ring, 10, lastX, 0, 0, 0).X)
        && double.IsNaN(OreHeat.Sample(ring, 10, 0, lastZ, 0, OreHeat.TileCells - 1).X), "ore samples stop at the ring's end and the south edge");
    // Stand-ins: a tile drawn from a coarser one shows, at each cell's centre, the coarser cell there.
    var oreRng = new Random(5);
    var coarse = Enumerable.Range(0, OreHeat.TileCount).Select(_ => (float)oreRng.NextDouble()).ToArray();
    foreach (int up in new[] { 1, 2, 5, 7 })
    {
        int fine = 6, ax = 3, az = 2, n = 1 << up;
        foreach (var (ox, oz) in new[] { (0, 0), (n - 1, n / 2), (n / 3, n - 1) })
        {
            int tx = (ax << up) + ox, tz = (az << up) + oz;
            var derived = OreHeat.Derive(coarse, up, tx, tz);
            bool same = true;
            for (int r = 0; r < OreHeat.TileCells; r++) for (int col = 0; col < OreHeat.TileCells; col++)
            {
                double x = ring.StartX + tx * OreHeat.Span(fine) + (col + 0.5) * OreHeat.CellSize(fine), z = tz * OreHeat.Span(fine) + (r + 0.5) * OreHeat.CellSize(fine);
                int ac = OreHeat.CellAt(ring, fine + up, ax, az, x, z);
                same &= ac >= 0 && derived[r * OreHeat.TileCells + col] == coarse[ac];
            }
            Check(same, $"ore stand-in from {up} levels up ({ox}, {oz})");
        }
    }
    Check(OreHeat.Derive(null, 1, 0, 0).All(float.IsNaN) && OreHeat.Derive(coarse, 0, 0, 0).All(float.IsNaN), "no stand-in without a coarser tile");
    // Stored oreTiles: half floats, NaN kept, large counts capped; refused for another catalogue or damaged.
    var stored = new OreHeat.OreTile(3);
    for (int i = 0; i < OreHeat.TileCount; i++) { stored.Set(0, i, i, i % 7 / 100.0); stored.Set(1, i, i % 5 == 0 ? double.NaN : 3.5, 0.03); stored.Set(2, i, 1e6, 0.5); }
    for (int ore = 0; ore < 3; ore++) stored.Set(ore, 7, double.NaN, double.NaN);
    var packedOre = stored.Pack();
    var back = OreHeat.OreTile.Unpack(packedOre, 3);
    static IEnumerable<ushort> Bits(Half[] values) => values.Select(BitConverter.HalfToUInt16Bits);
    Check(back.Complete && Bits(back.Blocks).SequenceEqual(Bits(stored.Blocks)) && Bits(back.Factors).SequenceEqual(Bits(stored.Factors)), $"ore tile round trip ({packedOre.Length} bytes for 3 ores)");
    var (b0, f0) = back.One(0);
    Check(b0[100] == 100 && Math.Abs(f0[100] - 0.02f) < 1e-4 && float.IsNaN(b0[7]) && float.IsNaN(f0[7]) && back.One(2).Blocks[0] == 65504, "ore tile values: half floats, NaN kept, large counts capped");
    var (counts, strongest) = back.Listed();
    Check(counts[1] == 2 && Math.Abs(strongest[1] - 0.5f) < 1e-3 && counts[0] == 2 && float.IsNaN(counts[7]) && float.IsNaN(strongest[7]), "listed ores from a stored tile: the pick's threshold");
    var wrongMagic = (byte[])packedOre.Clone(); wrongMagic[0] ^= 1;
    Check(Throws(() => OreHeat.OreTile.Unpack(packedOre, 4)) && Throws(() => OreHeat.OreTile.Unpack(packedOre[..^8], 3)) && Throws(() => OreHeat.OreTile.Unpack(wrongMagic, 3))
        && Throws(() => OreHeat.OreTile.Unpack(null, 3)) && Throws(() => OreHeat.OreTile.Unpack(new byte[OreHeat.OreTile.MaxPackedBytes(3) + 1], 3)), "ore tile of another catalogue or damaged is refused");
    // Colours: transparent without ore or answer; brighter and more opaque with more ore; premultiplied.
    Check(OreHeat.Color(OreHeat.RampOf(0)) == 0 && OreHeat.Color(OreHeat.RampOf(float.NaN)) == 0 && OreHeat.Color(OreHeat.RampOf(0.4)) == 0, "no ore, no colour");
    int previous = -1;
    foreach (double blocks in new[] { 1.0, 10, 100, 1000, 10000, 1e6 })
    {
        int col = OreHeat.Color(OreHeat.RampOf(blocks)), alpha = col >>> 24 & 0xff;
        Check(alpha > previous && (col & 0xff) <= alpha && (col >> 8 & 0xff) <= alpha && (col >> 16 & 0xff) <= alpha, $"ore colour premultiplied, opacity rises ({blocks})");
        previous = blocks >= 10000 ? alpha - 1 : alpha;
    }
    Near(OreHeat.RampOf(100), 0.5, "100 blocks mid-ramp");
    int none = OreHeat.Color(OreHeat.RampOfListed(0), true);
    Check((none & 0xff) == (none >> 8 & 0xff) && (none >> 8 & 0xff) == (none >> 16 & 0xff) && none != 0, "nothing listed is grey, not transparent");
    Check(OreHeat.Pixels(new[] { 0f, float.NaN, 50f }, false) is var px && px[0] == 0 && px[1] == 0 && px[2] != 0, "pixels from values");
    Check((OreHeat.Argb(1) >>> 24) == 0xff && OreHeat.Argb(1) != OreHeat.Argb(0), "legend colours opaque");
    // The pick's words: listed above 0.025, then very poor .. ultra high by factor × 7.5.
    Check(OreHeat.WordIndex(0.025) == -1 && OreHeat.WordIndex(0.026) == 0 && OreHeat.WordIndex(0.14) == 1 && OreHeat.WordIndex(0.3) == 2 && OreHeat.WordIndex(5) == 5, "pick words");
    Check(OreHeat.Short(0.44) == "0.4" && OreHeat.Short(130.2) == "130" && OreHeat.Short(2400) == "2.4k" && OreHeat.Short(18000) == "18k", "short block counts");
}
// What a survival client may be sent (0.5.4, WM-D63): the server's record of exploration and its gate.
{
    var world = new Topology { StartX = 25600, Width = 204800, Height = 102400, Wraps = true };
    var grid = new MaskGrid(world);
    Check(grid.Width == 6400 && grid.Height == 3200 && grid.TexelX == 32 && grid.TexelZ == 32, "mask grid: 32-block cells on a 204,800-block ring");
    Check(grid.Cell(world.StartX + 40, 70) == 2 * grid.Width + 1 && grid.CellRelative(40, 70) == 2 * grid.Width + 1, "mask grid: world and relative positions find the same cell");
    Check(grid.CellRelative(world.Width + 40, 70) == grid.CellRelative(40, 70) && grid.CellRelative(-world.Width + 40, 70) == grid.CellRelative(40, 70), "mask grid: any lap of the ring");
    Check(grid.CellRelative(40, -1) == -1 && grid.CellRelative(40, world.Height) == -1, "mask grid: off the world's north and south edges");
    var disc = grid.Disc(world.StartX + 1000, 1000, 100);
    Check(disc.Count > 20 && disc.Count < 50 && disc.Contains(grid.Cell(world.StartX + 1000, 1000)), "mask grid: a 100-block disc holds about 31 cells and its own");
    var ring = grid.WithNeighbours(new[] { 5 * grid.Width, 5 * grid.Width + 1, 0 });
    Check(ring.Count == ring.Distinct().Count() && ring.Contains(4 * grid.Width + grid.Width - 1) && ring.Contains(6 * grid.Width + 2)
        && ring.Contains(grid.Width - 1) && ring.Contains(grid.Width) && !ring.Any(c => c < 0) && ring.Count == 12 + 6,
        $"mask grid: cells and those beside them, once each, across the seam and not past the north edge ({ring.Count})");
    var seam = grid.Disc(world.StartX + 10, 1000, 64);
    Check(seam.Any(cell => cell % grid.Width == grid.Width - 1) && seam.Any(cell => cell % grid.Width == 0), "mask grid: a disc on the seam wraps around the ring");

    var set = new CellSet(grid.Width, grid.Height);
    var added = new List<int>();
    Check(set.Add(5) && !set.Add(5) && set.Contains(5) && !set.Contains(6) && set.Count == 1, "cell set: add once, contains");
    Check(set.AddRange(new[] { 6, 7, 5, grid.Width * 100 + 4000 }, added) == 3 && added.Count == 3 && !added.Contains(5), "cell set: the new cells of a range");
    Check(!set.Add(-1) && !set.Add(grid.Width * grid.Height) && set.Count == 4, "cell set: cells outside the grid are ignored");
    Check(set.AnyIn(4, 0, 6, 1) && !set.AnyIn(8, 0, 20, 1) && set.AnyIn(4000, 100, 4001, 101), "cell set: any in a rectangle");
    Check(new CellSet(10, 4) is var small && small.Add(9) && small.AnyIn(-1, 0, 0, 1) && small.AnyIn(9, 0, 11, 1), "cell set: a rectangle past the edge wraps");
    Check(set.AnyIn(-100000, 0, 100000, grid.Height) && set.AnyIn(3990, 90, 4010, 110) && !set.AnyIn(3990, 90, 4000, 100) && !set.AnyIn(4000, 100, 4000, 101),
        "cell set: a rectangle wider than the grid, around a cell, beside it, and empty");
    Check(new CellSet(200, 4) is var wide && wide.Add(199) && wide.Add(64) && wide.AnyIn(198, 0, 202, 1) && wide.AnyIn(-2, 0, 0, 1) && !wide.AnyIn(0, 0, 64, 4) && wide.AnyIn(60, 0, 65, 4),
        "cell set: rectangles across the seam and across a block edge");
    var copy = set.Clone();
    copy.Add(9);
    Check(copy.Count == 5 && set.Count == 4 && !set.Contains(9) && copy.Contains(grid.Width * 100 + 4000), "cell set: a copy is its own");
    Check(set.Missing(new[] { 5, 9, 9, 8, -3, 5 }).SequenceEqual(new[] { 9, 8 }), "cell set: the cells not in it, each once");
    var boxes = set.Boxes();
    Check(boxes.Count == 2 && boxes.Contains((5, 0, 8, 1)) && boxes.Contains((4000, 100, 4001, 101)), "cell set: one rectangle per block round its cells");
    var round = CellSet.Unpack(set.Pack(), grid.Width, grid.Height);
    Check(round.Count == 4 && round.Contains(5) && round.Contains(7) && round.Contains(grid.Width * 100 + 4000) && !round.Contains(8), "cell set: packed and unpacked");
    Check(round.Cells().OrderBy(cell => cell).SequenceEqual(set.Cells().OrderBy(cell => cell)), "cell set: the same cells after packing");
    Reject(() => CellSet.Unpack(set.Pack(), grid.Width + 1, grid.Height), "cell set: another grid's set is refused");
    Reject(() => CellSet.Unpack(set.Pack()[..20], grid.Width, grid.Height), "cell set: a cut set is refused");
    var edge = new CellSet(100, 70);
    edge.Add(99); edge.Add(69 * 100 + 99);
    var edgeRound = CellSet.Unpack(edge.Pack(), 100, 70);
    Check(edgeRound.Count == 2 && edgeRound.Contains(99) && edgeRound.Contains(69 * 100 + 99), "cell set: partial blocks at the grid's edges");

    // A semantic tile of level 2 (4-block texels, 512 blocks) at the world's start: 16 x 16 cells of 32 blocks.
    var key = new TileKey(0, 2, 0, 0);
    var semantic = new byte[TileData.Padded * TileData.Padded * 4];
    for (int i = 0; i < semantic.Length; i++) semantic[i] = 200;
    var none = (byte[])semantic.Clone();
    Check(!RevealGate.Semantic(none, key, grid, _ => false) && none.All(b => b == 0), "survival: a tile of land never seen goes back with nothing in it");
    var known = new CellSet(grid.Width, grid.Height);
    known.Add(grid.CellRelative(100, 100));
    var one = (byte[])semantic.Clone();
    Check(RevealGate.Semantic(one, key, grid, known.Contains), "survival: a tile with one seen cell is sent");
    int kept = 0, wrong = 0, spread = 0;
    for (int z = 0; z < TileData.Padded; z++)
    for (int x = 0; x < TileData.Padded; x++)
    {
        int o = (z * TileData.Padded + x) * 4;
        bool inCell = known.Contains(grid.CellRelative((x - 0.5) * 4, (z - 0.5) * 4));
        if (one[o + 3] != 0) kept++;
        if ((one[o + 3] != 0) != inCell) wrong++;
        // A blanked texel holds nothing, or a seen texel's values two texels out at most (filtering at the edge).
        if (!inCell && (one[o] | one[o + 1] | one[o + 2]) != 0)
        {
            bool copied = false;
            for (int dz = -2; dz <= 2 && !copied; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= TileData.Padded || nz >= TileData.Padded) continue;
                int no = (nz * TileData.Padded + nx) * 4;
                if (one[no + 3] != 0 && one[no] == one[o] && one[no + 1] == one[o + 1] && one[no + 2] == one[o + 2]) { copied = true; break; }
            }
            if (copied) spread++; else wrong++;
        }
    }
    Check(kept == 64 && wrong == 0 && spread == 12 * 12 - 64, $"survival: only the seen cell's 8 x 8 texels are sent, its values spread two texels into the blank ({kept} kept, {spread} spread, {wrong} wrong)");
    var biome = new byte[2 * BiomeTile.HalfBytes];
    for (int i = 0; i < biome.Length; i++) biome[i] = 77;
    Check(!RevealGate.Biome(biome, key, grid, _ => false), "survival: a biome tile of land never seen is not sent");
    // Biome tiles interpolate between samples (stride 2 from level 6): a texel goes only when all its samples are seen.
    var coarseKey = new TileKey(0, 6, 1, 1);
    Func<int, bool> westHalf = cell => cell >= 0 && (cell % grid.Width + 0.5) * grid.TexelX < coarseKey.X * coarseKey.Span + 63 * coarseKey.Step;
    var coarseBiome = new byte[2 * BiomeTile.HalfBytes];
    for (int i = 0; i < coarseBiome.Length; i++) coarseBiome[i] = 99;
    var cw = RevealGate.Window(coarseKey, grid, westHalf);
    RevealGate.Biome(coarseBiome, cw);
    int keptOdd = 0, keptEdge = 0;
    for (int z = 1; z < TileData.Padded; z++)
    for (int x = 0; x < TileData.Padded; x++)
    {
        int o = (z * TileData.Padded + x) * 4;
        if (coarseBiome[o + 3] == 0) continue;
        // Texel x is interpolated from samples at texels 2 * (x / 2) and the next: both must lie west of texel 63.5.
        double east = (Math.Min(x / 2, (BiomeTile.Coarse(2) - 2)) + 1) * 2 - 0.5;
        if (x % 2 == 1 && east >= 63) keptOdd++;
        if (x >= 63) keptEdge++;
    }
    Check(cw.Any && keptOdd == 0 && keptEdge == 0, $"survival: no coarse biome texel is made from a sample not seen ({keptOdd}, {keptEdge})");
    // At every level's stride (16 at level 0 down to 1, then 2): two worlds that differ only in land not seen give the same
    // gated biome and season bytes.
    var levelsSame = new List<string>();
    for (int level = 0; level <= 8; level++)
    {
        var lk = new TileKey(0, level, 3, 2);
        double tileEdge = lk.X * lk.Span + 0.6 * lk.Span;   // seen west of 60% of the tile, every row
        Func<int, bool> seenPart = cell => cell >= 0 && (cell % grid.Width + 0.5) * grid.TexelX < tileEdge;
        BiomeSample Plain(double x, double z) => new(100, 120, 140);
        BiomeSample Other(double x, double z) => seenPart(grid.Cell(x, z)) ? new(100, 120, 140) : new(220, 20, 250);
        SeasonSample PlainSeason(double x, double z) => new(200, 0, 128, 128);
        SeasonSample OtherSeason(double x, double z) => seenPart(grid.Cell(x, z)) ? new(200, 0, 128, 128) : new(97, 255, 10, 240);
        var b1 = BiomeTile.Sample(world, lk, Plain, PlainSeason, default);
        var b2 = BiomeTile.Sample(world, lk, Other, OtherSeason, default);
        var lw = RevealGate.Window(lk, grid, seenPart);
        RevealGate.Biome(b1, lw); RevealGate.Biome(b2, lw);
        int sentTexels = 0;
        for (int i = 3; i < BiomeTile.HalfBytes; i += 4) if (b1[i] != 0) sentTexels++;
        if (!b1.SequenceEqual(b2) || sentTexels == 0) levelsSame.Add($"level {level} (stride {BiomeTile.Stride(level)}, {sentTexels} sent)");
    }
    Check(levelsSame.Count == 0, "survival: biome tiles at every stride say nothing of land not seen: " + string.Join(", ", levelsSame));
    // A seen cell's biome and season spread two texels into the blank, coverage 0; farther out no biome and no season.
    var spreadBiome = new byte[2 * BiomeTile.HalfBytes];
    for (int i = 0; i < BiomeTile.HalfBytes; i += 4) { spreadBiome[i] = 40; spreadBiome[i + 1] = 50; spreadBiome[i + 2] = 60; spreadBiome[i + 3] = 255; }
    for (int i = BiomeTile.HalfBytes; i < spreadBiome.Length; i += 4) { spreadBiome[i] = 204; spreadBiome[i + 1] = 0; spreadBiome[i + 2] = 99; spreadBiome[i + 3] = 150; }
    RevealGate.Biome(spreadBiome, RevealGate.Window(key, grid, known.Contains));
    int seasonSpread = 0, seasonWrong = 0;
    for (int i = 0; i < TileData.Padded * TileData.Padded; i++)
    {
        int o = i * 4, s = BiomeTile.HalfBytes + o;
        if (spreadBiome[o + 3] != 0) continue;
        bool copied = spreadBiome[o] == 40 && spreadBiome[s] == 204 && spreadBiome[s + 2] == 99 && spreadBiome[s + 3] == 150;
        // A copy lies within two texels of a texel sent whole.
        if (copied)
        {
            int cx = i % TileData.Padded, cz = i / TileData.Padded;
            bool near = false;
            for (int dz = -2; dz <= 2 && !near; dz++)
            for (int dx = -2; dx <= 2 && !near; dx++)
            {
                int nx = cx + dx, nz = cz + dz;
                near = nx >= 0 && nz >= 0 && nx < TileData.Padded && nz < TileData.Padded && spreadBiome[(nz * TileData.Padded + nx) * 4 + 3] != 0;
            }
            if (!near) { seasonWrong++; continue; }
        }
        bool nothing = (spreadBiome[o] | spreadBiome[o + 1] | spreadBiome[o + 2]) == 0 && spreadBiome[s] == SeasonSample.None.Share && spreadBiome[s + 2] == SeasonSample.None.WetCos;
        if (copied) seasonSpread++; else if (!nothing) seasonWrong++;
    }
    Check(seasonSpread > 0 && seasonWrong == 0, $"survival: a biome tile's season spreads with its biome, the rest none ({seasonSpread}, {seasonWrong} wrong)");
    Check(biome.Take(BiomeTile.HalfBytes).All(b => b == 0)
        && biome[BiomeTile.HalfBytes] == SeasonSample.None.Share && biome[BiomeTile.HalfBytes + 2] == SeasonSample.None.WetCos && biome[BiomeTile.HalfBytes + 3] == SeasonSample.None.WetSin,
        "survival: a blanked biome texel has no biome and no season of its own");

    // A survival client's chart extends its known texels into those it lacks: the nearest known within the reach, past it a
    // neutral texel; wrapping east and west.
    var holey = new BaseRaster { Width = 100, Height = 40, Rgba = new byte[100 * 40 * 4] };
    var holeKnown = new byte[100 * 40];
    void Know(int x, int z, byte r) { int i = z * 100 + x; holeKnown[i] = 255; holey.Rgba[i * 4] = r; holey.Rgba[i * 4 + 1] = 77; holey.Rgba[i * 4 + 3] = 255; }
    for (int z = 10; z < 30; z++) { Know(2, z, 11); Know(50, z, 22); }
    byte[] neutralTexel = { 255, 128, 0, 255 };
    RevealGate.Extend(holey, holeKnown, 0, 0, 100, 40, true, 16, neutralTexel);
    byte R(int x, int z) => holey.Rgba[(z * 100 + x) * 4];
    Check(R(5, 20) == 11 && R(45, 20) == 22 && R(97, 20) == 11 && R(2, 20) == 11 && R(50, 20) == 22,
        "survival: unknown texels take the nearest known one, across the seam too, and known ones stay");
    Check(R(30, 20) == 255 && holey.Rgba[(20 * 100 + 30) * 4 + 1] == 128 && R(80, 20) == 255 && R(26, 0) == 255 && R(5, 0) == 11 && R(10, 20) == 11,
        "survival: past the reach a texel is neutral land at the sea surface");
    // As the client calls it: a block's rectangle and the reach round it, on a chart 5000 texels wide (its last block 8 texels),
    // at the north edge and across the seam; texels made known later replace their extension.
    var strip = new BaseRaster { Width = 5000, Height = 70, Rgba = new byte[5000 * 70 * 4] };
    var stripKnown = new byte[5000 * 70];
    void KnowStrip(int x, int z, byte r) { int i = z * 5000 + x; stripKnown[i] = 255; strip.Rgba[i * 4] = r; strip.Rgba[i * 4 + 3] = 255; }
    for (int z = 0; z < 64; z++) KnowStrip(2, z, 33);
    RevealGate.Extend(strip, stripKnown, -16, -16, 80, 80, true, 16, neutralTexel, out bool stripChanged);
    byte S(int x, int z) => strip.Rgba[(z * 5000 + x) * 4];
    Check(stripChanged && S(4995, 0) == 33 && S(4987, 5) == 33 && S(4980, 5) == 0 && S(10, 63) == 33 && S(40, 10) == 255,
        "survival: a block's extension reaches across the seam into the block before the narrow last one, and stops at the reach");
    RevealGate.Extend(strip, stripKnown, -16, -16, 80, 80, true, 16, neutralTexel, out bool again2);
    KnowStrip(30, 10, 44);
    RevealGate.Extend(strip, stripKnown, -16, -16, 80, 80, true, 16, neutralTexel, out bool newer);
    Check(!again2 && newer && S(30, 10) == 44 && S(31, 10) == 44 && S(40, 10) == 44 && S(17, 10) == 44 && S(15, 10) == 33,
        "survival: extending again changes nothing; a texel made known later takes over the texels nearer it");
    var bounded = new BaseRaster { Width = 100, Height = 40, Rgba = new byte[100 * 40 * 4] };
    var flatKnown = new byte[100 * 40];
    flatKnown[20 * 100 + 1] = 255; bounded.Rgba[(20 * 100 + 1) * 4] = 9;
    RevealGate.Extend(bounded, flatKnown, 0, 0, 100, 40, false, 16);
    Check(bounded.Rgba[(20 * 100 + 3) * 4] == 9 && bounded.Rgba[(20 * 100 + 99) * 4] == 0, "survival: a bounded world does not wrap, and without a neutral texel the rest stays");

    // The chart in blocks: 6400 x 3200 texels over this world (one per cell), a block of 64 x 64 texels.
    var chart = new BaseRaster { Width = 6400, Height = 3200, Rgba = new byte[6400 * 3200 * 4] };
    for (int i = 0; i < chart.Rgba.Length; i++) chart.Rgba[i] = (byte)(i % 251 + 1);
    Check(RevealGate.PackBlock(chart, 0, 0, (x, z) => false) == null, "survival: a chart block of land never seen is not sent");
    var seen = new CellSet(grid.Width, grid.Height);
    foreach (int cell in new[] { 3 * grid.Width + 3, 3 * grid.Width + 4, 70 * grid.Width + 65 }) seen.Add(cell);
    var client = new BaseRaster { Width = 6400, Height = 3200, Rgba = new byte[6400 * 3200 * 4] };
    var clientKnown = new byte[6400 * 3200];
    byte[] block00 = RevealGate.PackBlock(chart, 0, 0, (x, z) => RevealGate.Texel(x, z, 6400, 3200, grid, seen.Contains, false));
    var change1 = RevealGate.ApplyBlock(block00, 0, 0, client, clientKnown);
    int set1 = change1.Fresh;
    int set2 = RevealGate.ApplyBlock(RevealGate.PackBlock(chart, 1, 1, (x, z) => RevealGate.Texel(x, z, 6400, 3200, grid, seen.Contains, false)), 1, 1, client, clientKnown).Fresh;
    Check(change1.Changed && change1.X0 == 3 && change1.Z0 == 3 && change1.X1 == 5 && change1.Z1 == 4, "survival: a block's newly known texels and their bounds");
    var again = RevealGate.ApplyBlock(block00, 0, 0, client, clientKnown);
    Check(again.Fresh == 0 && !again.Changed, "survival: the same block again changes nothing");
    client.Rgba[(3 * 6400 + 3) * 4] ^= 1;
    var changed = RevealGate.ApplyBlock(block00, 0, 0, client, clientKnown);
    Check(changed.Fresh == 0 && changed.Changed && client.Rgba[(3 * 6400 + 3) * 4] == chart.Rgba[(3 * 6400 + 3) * 4], "survival: a block with other values for known texels changes them, none newly known");
    int leaked = 0;
    for (int i = 0; i < clientKnown.Length; i++)
    {
        bool shouldKnow = seen.Contains(i);
        if ((clientKnown[i] != 0) != shouldKnow) leaked++;
        if (!shouldKnow && (client.Rgba[i * 4] | client.Rgba[i * 4 + 1] | client.Rgba[i * 4 + 2] | client.Rgba[i * 4 + 3]) != 0) leaked++;
        if (shouldKnow && client.Rgba[i * 4] != chart.Rgba[i * 4]) leaked++;
    }
    Check(set1 == 2 && set2 == 1 && leaked == 0, $"survival: the chart's blocks carry exactly the seen texels ({set1}, {set2}, {leaked} wrong)");
    Reject(() => RevealGate.ApplyBlock(new byte[] { 1, 2, 3 }, 0, 0, client, clientKnown), "survival: a damaged chart block is refused");
    var blocks = new List<(int, int)>();
    RevealGate.BlocksOfCell(70 * grid.Width + 65, grid, 6400, 3200, (bx, bz) => blocks.Add((bx, bz)));
    Check(blocks.Contains((1, 1)), "survival: a cell's chart block is found");
    // A chart 6420 texels wide: its last block holds 20 texels. A coast reach of 33 texels from the first cell reaches back
    // over the seam into the block before it (L3 of the re-review).
    var seamBlocks = new HashSet<(int, int)>();
    RevealGate.BlocksOfCells(0, 10, 1, 11, grid, 6420, 3200, (bx, bz) => seamBlocks.Add((bx, bz)), 33);
    Check(seamBlocks.Contains((100, 0)) && seamBlocks.Contains((99, 0)) && seamBlocks.Contains((0, 0)) && !seamBlocks.Contains((1, 0)) && !seamBlocks.Contains((98, 0)),
        $"survival: blocks within reach across the seam ({string.Join(" ", seamBlocks.OrderBy(b => b))})");
    var wideBlocks = new HashSet<(int, int)>();
    RevealGate.BlocksOfCells(0, 10, 6400, 11, grid, 6420, 3200, (bx, bz) => wideBlocks.Add((bx, bz)), 33);
    Check(wideBlocks.Count == 101, "survival: a rectangle round the world marks every block of its rows once");
    // The climate raster is coarser than the cells: a texel goes once any part of it is seen.
    Check(RevealGate.Texel(0, 0, 800, 400, grid, cell => cell == 7 * grid.Width + 7, true) && !RevealGate.Texel(0, 0, 800, 400, grid, cell => cell == 7 * grid.Width + 7, false),
        "survival: a climate texel goes once one of its corners is seen; a chart texel only by its centre");

    // Distances say nothing of land not seen (the review of 15 Nov 2026): a coast or river past seen land changes no byte sent.
    // Tiles of level 2 (4-block texels, 8 to a 32-block cell) with a coast running north-south.
    byte[] CoastTile(int coastX)
    {
        var kinds = new byte[TileData.Padded * TileData.Padded];
        var heights = new short[kinds.Length];
        for (int i = 0; i < kinds.Length; i++)
        {
            bool water = i % TileData.Padded < coastX;
            kinds[i] = water ? TileData.Water : TileData.Land;
            heights[i] = (short)(water ? -20 : 15);
        }
        return SemanticTile.Encode(kinds, heights, TileData.Padded, 0);
    }
    // Seen: the cells whose texels lie west of texel 57 (cells 0..6 east-west), every row.
    Func<int, bool> west = cell => cell >= 0 && cell % grid.Width <= 6 && cell / grid.Width < 20;
    var near1 = CoastTile(70);
    var near2 = CoastTile(90);
    bool r1 = RevealGate.Semantic(near1, key, grid, west), r2 = RevealGate.Semantic(near2, key, grid, west);
    Check(r1 && r2 && near1.SequenceEqual(near2), "survival: a tile's coast distances do not tell where an unseen coast lies");
    int farWater = 0, seenTexels = 0;
    for (int i = 0; i < TileData.Padded * TileData.Padded; i++) if (near1[i * 4 + 3] != 0) { seenTexels++; if (near1[i * 4] == 0) farWater++; }
    Check(seenTexels > 0 && farWater == seenTexels, $"survival: seen water whose coast is unseen reads as open sea ({farWater} of {seenTexels})");
    // Seen on both sides of the coast: the distances near it are the true ones.
    Func<int, bool> most = cell => cell >= 0 && cell % grid.Width <= 14 && cell / grid.Width < 20;
    var whole = CoastTile(70);
    var gated = (byte[])whole.Clone();
    RevealGate.Semantic(gated, key, grid, most);
    int same = 0, differ = 0;
    for (int z = 0; z < TileData.Padded; z++)
    for (int x = 45; x < 95; x++)
    {
        int o = (z * TileData.Padded + x) * 4;
        if (gated[o + 3] == 0) continue; // the row north of the world's edge
        if (gated[o] == whole[o]) same++; else differ++;
    }
    Check(differ == 0 && same == 50 * (TileData.Padded - 1), $"survival: coast distances of seen land near a seen coast are kept ({differ} differ, {same} same)");

    // The chart: a block's coast and river distances are the same whatever lies in land not seen (texels x < 80 seen).
    BaseRaster CoastChart(int coastX, bool river)
    {
        const int n = 256;
        var r = new BaseRaster { Width = n, Height = n, Rgba = new byte[n * n * 4] };
        Semantic.CoastField(r.Rgba, n, n, i => i % n < coastX ? (sbyte)1 : (sbyte)0, false);
        for (int i = 0; i < n * n; i++)
        {
            int x = i % n;
            r.Rgba[i * 4 + 1] = (byte)(x < coastX ? 100 : 160);
            // A river 2 texels wide down x = 85, its bank distance within 12 texels of it.
            double d = Math.Abs(x - 85) - 1;
            r.Rgba[i * 4 + 3] = river && d < Semantic.RiverRange ? Semantic.EncodeSigned(d, Semantic.RiverRange) : (byte)255;
        }
        return r;
    }
    var chartA = CoastChart(100, true);
    var chartB = CoastChart(120, false);
    Func<int, int, bool> seenWest = (x, z) => x < 80;
    bool blocksSame = true;
    foreach (var (bx, bz) in new[] { (0, 0), (1, 0), (1, 1) })
        blocksSame &= RevealGate.PackBlock(chartA, bx, bz, seenWest, chart: true).SequenceEqual(RevealGate.PackBlock(chartB, bx, bz, seenWest, chart: true));
    Check(blocksSame, "survival: chart blocks do not tell where an unseen coast or river lies");
    // Seen everywhere: distances within the coast reach are the chart's own, the river's too, the far value past the reach.
    var full = new BaseRaster { Width = 256, Height = 256, Rgba = new byte[256 * 256 * 4] };
    var fullKnown = new byte[256 * 256];
    for (int bz = 0; bz < 4; bz++)
    for (int bx = 0; bx < 4; bx++)
        RevealGate.ApplyBlock(RevealGate.PackBlock(chartA, bx, bz, (x, z) => true, chart: true), bx, bz, full, fullKnown);
    int coastWrong = 0, riverWrong = 0, saturated = 0;
    for (int i = 0; i < 256 * 256; i++)
    {
        double d = Math.Abs(Semantic.DecodeSigned(chartA.Rgba[i * 4], Semantic.CoastRange));
        if (d + 0.5 < RevealGate.CoastReach - 1) { if (full.Rgba[i * 4] != chartA.Rgba[i * 4]) coastWrong++; }
        else if (d + 0.5 >= RevealGate.CoastReach + 1) { if (full.Rgba[i * 4] is not (0 or 255)) coastWrong++; else saturated++; }
        if (full.Rgba[i * 4 + 3] != chartA.Rgba[i * 4 + 3]) riverWrong++;
    }
    Check(coastWrong == 0 && riverWrong == 0 && saturated > 0, $"survival: a chart seen whole keeps its coast within reach and its rivers ({coastWrong}, {riverWrong} wrong; {saturated} past the reach)");
    // Seen up to the river: the river stops short of the unseen land.
    var westChart = new BaseRaster { Width = 256, Height = 256, Rgba = new byte[256 * 256 * 4] };
    var westKnown = new byte[256 * 256];
    RevealGate.ApplyBlock(RevealGate.PackBlock(chartA, 1, 0, (x, z) => x < 90, chart: true), 1, 0, westChart, westKnown);
    int riverTexels = 0, riverPast = 0;
    for (int z = 0; z < 64; z++)
    for (int x = 64; x < 90; x++)
    {
        byte a = westChart.Rgba[(z * 256 + x) * 4 + 3];
        if (a == 255) continue;
        riverTexels++;
        // Every seen texel within the bank's distance and a texel more must be seen: x + distance + 1 < 90.
        if (x + Math.Abs(Semantic.DecodeSigned(a, Semantic.RiverRange)) + 1 >= 90) riverPast++;
    }
    Check(riverTexels > 0 && riverPast == 0, $"survival: river distances only where seen land shows the bank ({riverTexels} texels, {riverPast} reaching past)");
}
// The far shaders' constants against C#'s (0.5.3, WM-0039): the shaders name them, and a change on one side fails here.
{
    double ShaderConst(string file, string name)
    {
        string text = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "wmap", "shaderincludes", file)));
        var m = System.Text.RegularExpressions.Regex.Match(text, @"const\s+(?:int|float)\s+" + name + @"\s*=\s*([0-9.]+)\s*;");
        if (!m.Success) throw new Exception($"{file}: no constant {name}");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
    Check(ShaderConst("wmapfartiles.ash", "WmapSlots") == FarTileWindow.Slots, "shader WmapSlots is FarTileWindow.Slots");
    Check(ShaderConst("wmapfartiles.ash", "WmapSlotTexels") == FarTileWindow.SlotTexels, "shader WmapSlotTexels is FarTileWindow.SlotTexels");
    Check(ShaderConst("wmapfartiles.ash", "WmapAtlasTexels") == FarTileWindow.AtlasTexels, "shader WmapAtlasTexels is FarTileWindow.AtlasTexels");
    Check(ShaderConst("wmapfartiles.ash", "WmapSeamReach") == FarTileWindow.SeamReach, "shader WmapSeamReach is FarTileWindow.SeamReach");
    Check(ShaderConst("wmapfarheight.ash", "JoinOverlap") == TerracePatch.JoinOverlap, "shader JoinOverlap is TerracePatch.JoinOverlap");
    Check(SlotTable.Count == FarTileWindow.Slots * FarTileWindow.Slots, "slot table holds WmapSlots * WmapSlots slots");
}
if (failures.Count > 0)
{
    foreach (string f in failures) Console.WriteLine("FAIL " + f);
    Console.WriteLine($"FAIL: {failures.Count} of {tests} assertions");
    return 1;
}
Console.WriteLine($"PASS: {tests} assertions (topology, camera, partial edges, coordinates, codecs, distance fields, world raster, rivers, detail selection, player arrow, wind layer, far terrain mesh, teleport columns, day grid, line of sight, recording levels, mask geometry, sight cells, ring copies, markers, paper maps, sight holes, heading, climate raster heights, ore heat maps, shader constants, survival exploration).");
return 0;

bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
void Near2(double actual, double expected, double tolerance, string name, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => Check(Math.Abs(actual - expected) <= tolerance, name + $": {actual} != {expected}", line);
