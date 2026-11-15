using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Wmap;
using Wmap.Core;
using Wmap.Integrations;
using Wmap.Server;

/// <summary>
/// The unexplored map only in creative (0.5.4, WM-D63): a survival client receives nothing of the prediction for land it has not
/// seen. Through a real atlas server with its workers and a stand-in survival player: the manifest offers no chart; chart
/// chunks are not sent; tiles of unseen land come back empty; once a patch of land is charted, its tiles come with only that
/// patch's texels, and the chart's blocks carry exactly the charted texels. Their distances say nothing of land not seen: a
/// tile's coast distance points at a seen texel of the other kind or lies within seen land, the chart's is the one the
/// client's own seen texels give, and a river distance reaches only over seen land.
/// </summary>
public static class SurvivalChecks
{
    sealed class Survivor : ServerPlayer
    {
        public string Uid;
        public Survivor() : base(null, null) { }
        public override string PlayerUID => Uid;
        public override EnumClientState ConnectionState => EnumClientState.Playing;
        public override bool HasPrivilege(string code) => false;
    }

    public static string Run(ICoreServerAPI real, WorldSource source, BaseRaster chart, System.Func<double, double, (double X, double Z)?> locate)
    {
        if (source.Samplers == null || chart == null) return "Survival exploration: PASS (no prediction provider: nothing to send)";
        var player = (Survivor)RuntimeHelpers.GetUninitializedObject(typeof(Survivor));
        player.Uid = "survivor";
        // A connected survival player as the server keeps one (its world data), without an entity: no sight is cast for it.
        var dataFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var dataField = typeof(ServerPlayer).GetField("worlddata", dataFlags);
        object worldData = RuntimeHelpers.GetUninitializedObject(dataField.FieldType);
        worldData.GetType().GetField("GameMode", dataFlags).SetValue(worldData, EnumGameMode.Survival);
        worldData.GetType().GetField("connected", dataFlags).SetValue(worldData, true);
        dataField.SetValue(player, worldData);
        var save = GuiServiceProxy.Make<ISaveGame>((m, args) => GuiServiceProxy.Default(m));
        var manager = GuiServiceProxy.Make<IWorldManagerAPI>((m, args) => m.Name == "get_SaveGame" ? save : GuiServiceProxy.Default(m));
        var world = GuiServiceProxy.Make<IServerWorldAccessor>((m, args) => m.Name == "PlayerByUid" ? player : GuiServiceProxy.Default(m));
        var api = GuiServiceProxy.Make<ICoreServerAPI>((m, args) => m.Name switch
        {
            "get_World" => world, "get_WorldManager" => manager, "get_Logger" => real.Logger, _ => GuiServiceProxy.Default(m)
        });
        AtlasInfo manifest = null;
        var tiles = new ConcurrentQueue<TileResponse>();
        var patches = new ConcurrentQueue<ChartPatch>();
        int chunks = 0, updates = 0;
        var channel = GuiServiceProxy.Make<IServerNetworkChannel>((m, args) =>
        {
            if (m.Name == "SendPacket")
                switch (args[0])
                {
                    case AtlasInfo info: manifest = info; break;
                    case TileResponse r: tiles.Enqueue(r); break;
                    case ChartPatch p: patches.Enqueue(p); break;
                    case BaseChunk: Interlocked.Increment(ref chunks); break;
                    case ExploreUpdate: Interlocked.Increment(ref updates); break;
                }
            return GuiServiceProxy.Default(m);
        });
        string cache = Path.Combine(Path.GetTempPath(), "wmap-survival-" + Guid.NewGuid().ToString("N"));
        using var server = new AtlasServer(api, channel, new WmapConfig());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object value) => typeof(AtlasServer).GetField(name, flags).SetValue(server, value);
        Field("source", source); Field("cache", cache);
        string id = new string('d', 64);
        Field("baseId", id); Field("basePacked", new byte[4 * BaseChunk.Size]);
        // What Start sets on a real server: the chart's size, which a survival client's manifest carries.
        Field("baseWidth", chart.Width); Field("baseHeight", chart.Height);
        typeof(AtlasServer).GetMethod("StartWorkers", flags).Invoke(server, new object[] { 2 });
        server.Exploration.Start(source.Info, 0, true);
        server.Exploration.SetChart(chart);
        var t = source.Info.Topology();
        string session = source.Info.Session;
        var at = locate?.Invoke(46.0, 8.0) ?? (t.StartX + t.Width / 2, t.Height / 2);
        double rel = t.Canonical(at.X) - t.StartX;
        var grid = server.Exploration.Grid;
        try
        {
            server.Hello(player, new AtlasHello());
            if (manifest == null || !manifest.Explored || manifest.CanDevelop || manifest.HasOverview || manifest.BaseId != null || manifest.ClimateId != null
                || manifest.BaseWidth != chart.Width || manifest.BaseHeight != chart.Height || !manifest.CanPredict)
                throw new Exception($"Survival: the manifest offers more than land seen (explored {manifest?.Explored}, base {manifest?.BaseId}, climate {manifest?.ClimateId}, full {manifest?.HasOverview})");
            // A modified client asking for the whole chart gets none of it.
            for (int n = 0; n < 4; n++) server.RequestBase(player, new BaseRequest { BaseId = id, Offset = n * BaseChunk.Size });
            for (int n = 0; n < 5; n++) server.Tick();
            if (chunks != 0) throw new Exception($"Survival: {chunks} chunks of the whole chart were sent to a survival player");

            // Nothing charted yet: the near band's tile (level 2), its far tile and an atlas tile there come back empty.
            int bx = (int)(rel / 512), bz = (int)(at.Z / 512);
            TileRequest Band(int view, int stream) => new() { Session = session, View = view, Level = 2, X = bx, Z = bz, Stream = stream, Content = stream == 1 ? TileContent.SemanticAndBiome : TileContent.Semantic };
            server.Request(player, Band(1, 0)); server.Request(player, Band(1, 1));
            var before = Wait(server, tiles, 2);
            if (before.Count != 2 || before.Any(r => r.Data != null || r.Biome != null || r.Error != TileResponse.Unexplored))
                throw new Exception("Survival: tiles of land never seen were sent: " + string.Join(", ", before.Select(r => $"stream {r.Stream} data {r.Data?.Length} biome {r.Biome?.Length} error {r.Error}")));

            // The player charts a disc of 300 blocks there (as a read paper map would chart it).
            var texels = new byte[grid.Count];
            foreach (int cell in grid.Disc(at.X, at.Z, 300)) texels[cell] = 255;
            var charted = new HashSet<int>(Enumerable.Range(0, texels.Length).Where(i => texels[i] == 255));
            server.Exploration.Merge(player, server.Exploration.CellsOf(texels));
            server.Request(player, Band(2, 0)); server.Request(player, Band(2, 1));
            var after = Wait(server, tiles, 2);
            var atlas = after.FirstOrDefault(r => r.Stream == 0);
            var far = after.FirstOrDefault(r => r.Stream == 1);
            if (atlas?.Data == null || far?.Data == null) throw new Exception("Survival: tiles of charted land were not sent: " + string.Join(", ", after.Select(r => $"stream {r.Stream} {r.Error}")));
            var key = new TileKey(0, 2, bx, bz);
            if (!atlas.Gated || !far.Gated) throw new Exception("Survival: tiles cut to seen land were not marked so (a client would cache them)");
            int sent = 0, outside = 0, blanked = 0, pointing = 0, coasts = 0;
            bool Seen(int x, int z) => charted.Contains(grid.CellRelative(key.X * key.Span + (x - 0.5) * key.Step, key.Z * key.Span + (z - 0.5) * key.Step));
            foreach (var data in new[] { atlas.Data, far.Data })
            {
                var rgba = SemanticTile.Unpack(data);
                for (int z = 0; z < TileData.Padded; z++)
                for (int x = 0; x < TileData.Padded; x++)
                {
                    int o = (z * TileData.Padded + x) * 4;
                    // Coverage 0: blanked. It may hold a seen neighbour's values two texels out (filtering at the edge), nothing else.
                    if (rgba[o + 3] == 0)
                    {
                        blanked++;
                        if ((rgba[o] | rgba[o + 1] | rgba[o + 2]) == 0) continue;
                        bool copied = false;
                        for (int dz = -2; dz <= 2 && !copied; dz++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= TileData.Padded || nz >= TileData.Padded) continue;
                            int no = (nz * TileData.Padded + nx) * 4;
                            if (rgba[no + 3] != 0 && rgba[no] == rgba[o] && rgba[no + 1] == rgba[o + 1] && rgba[no + 2] == rgba[o + 2]) { copied = true; break; }
                        }
                        if (!copied) outside++;
                        continue;
                    }
                    sent++;
                    if (!Seen(x, z)) { outside++; continue; }
                    // A coast distance short of the far value: a seen texel of the other kind lies that far, or all land that near is seen.
                    if (rgba[o] is 0 or 255) continue;
                    coasts++;
                    double d = Math.Abs(Semantic.DecodeSigned(rgba[o], Semantic.CoastRange)) + 0.5;
                    bool water = rgba[o] < 128, shown = false, allSeen = true;
                    int r = (int)Math.Ceiling(d + 1.5);
                    for (int dz = -r; dz <= r && !shown; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        double dist = Math.Sqrt(dx * dx + dz * dz);
                        if (dist > d + 1.25) continue;
                        if (nx >= 0 && nz >= 0 && nx < TileData.Padded && nz < TileData.Padded)
                        {
                            int no = (nz * TileData.Padded + nx) * 4;
                            if (rgba[no + 3] != 0 && Seen(nx, nz) && (rgba[no] < 128) != water && dist <= d + 1.25) { shown = true; break; }
                        }
                        if (dist < d && !Seen(nx, nz)) allSeen = false;
                    }
                    if (!shown && !allSeen) pointing++;
                }
            }
            if (sent == 0 || outside != 0 || blanked == 0 || pointing != 0)
                throw new Exception($"Survival: the charted tiles carried {sent} texels ({blanked} blanked), {outside} of them outside the charted land, {pointing} coast distances pointing into land not seen");
            if (far.Biome != null)
            {
                var biome = BiomeTile.Unpack(far.Biome);
                for (int z = 0; z < TileData.Padded; z++)
                for (int x = 0; x < TileData.Padded; x++)
                {
                    int o = (z * TileData.Padded + x) * 4;
                    if (biome[o + 3] != 0
                        && !charted.Contains(grid.CellRelative(key.X * key.Span + (x - 0.5) * key.Step, key.Z * key.Span + (z - 0.5) * key.Step))) outside++;
                }
                if (outside != 0) throw new Exception($"Survival: the biome tile carried {outside} texels outside the charted land");
            }

            // The chart's blocks: exactly the charted texels.
            var watch = Stopwatch.StartNew();
            while (patches.IsEmpty && watch.ElapsedMilliseconds < 10000) { server.Tick(); Thread.Sleep(10); }
            for (int n = 0; n < 20; n++) { server.Tick(); Thread.Sleep(10); }
            var client = new BaseRaster { Width = chart.Width, Height = chart.Height, Rgba = new byte[chart.Rgba.Length] };
            var known = new byte[chart.Width * chart.Height];
            int blocks = 0, chartTexels = 0, chartOutside = 0, mismatch = 0;
            while (patches.TryDequeue(out var patch))
            {
                if (patch.Climate) continue;
                blocks++;
                RevealGate.ApplyBlock(patch.Data, patch.BlockX, patch.BlockZ, client, known);
            }
            double sx = t.Width / chart.Width, sz = t.Height / chart.Height;
            // The coast distances the client's own seen texels give, to the coast reach (RevealGate.CoastReach).
            var seenCoast = new byte[client.Rgba.Length];
            Semantic.CoastField(seenCoast, chart.Width, chart.Height, i => known[i] == 0 ? (sbyte)-1 : client.Rgba[i * 4] < 128 ? (sbyte)1 : (sbyte)0, t.Wraps);
            int coastWrong = 0, coastKept = 0, riverTexels = 0, riverPast = 0;
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i] == 0) continue;
                chartTexels++;
                int x = i % chart.Width, z = i / chart.Width;
                if (!charted.Contains(grid.CellRelative((x + 0.5) * sx, (z + 0.5) * sz))) chartOutside++;
                if (client.Rgba[i * 4 + 1] != chart.Rgba[i * 4 + 1] || client.Rgba[i * 4 + 2] != chart.Rgba[i * 4 + 2]) mismatch++;
                byte s = seenCoast[i * 4];
                byte expected = Math.Abs(Semantic.DecodeSigned(s, Semantic.CoastRange)) + 0.5 < RevealGate.CoastReach ? s : s < 128 ? (byte)0 : (byte)255;
                if (client.Rgba[i * 4] != expected) coastWrong++;
                if (client.Rgba[i * 4] == chart.Rgba[i * 4]) coastKept++;
                byte a = client.Rgba[i * 4 + 3];
                if (a == 255) continue;
                riverTexels++;
                double reach = Math.Abs(Semantic.DecodeSigned(a, Semantic.RiverRange)) + 1;
                int r = (int)Math.Ceiling(reach);
                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nz = z + dz, nx = t.Wraps ? ((x + dx) % chart.Width + chart.Width) % chart.Width : x + dx;
                    if (nz < 0 || nz >= chart.Height || nx < 0 || nx >= chart.Width || Math.Sqrt(dx * dx + dz * dz) >= reach) continue;
                    if (known[nz * chart.Width + nx] == 0) { riverPast++; dz = r; break; }
                }
            }
            if (blocks == 0 || chartTexels == 0 || chartOutside != 0 || mismatch != 0 || coastWrong != 0 || riverPast != 0)
                throw new Exception($"Survival: chart blocks {blocks}, {chartTexels} texels, {chartOutside} outside the charted land, {mismatch} not the chart's, {coastWrong} coast distances not those of seen land, {riverPast} of {riverTexels} river distances reaching past it");
            return $"Survival exploration: PASS (manifest without the chart; {chunks} chart chunks for 4 requests; tiles of unseen land empty; after charting a 300-block disc ({charted.Count} cells) {sent} tile texels ({blanked} blanked) and {chartTexels} chart texels in {blocks} blocks, none outside it; coast distances from seen land only ({coasts} tile texels checked; {coastKept} of {chartTexels} chart texels as the chart has them), {riverTexels} river texels within seen land; {updates} exploration updates)";
        }
        finally
        {
            server.Leave(player.PlayerUID);
            try { if (Directory.Exists(cache)) Directory.Delete(cache, true); } catch (IOException) { }
        }
    }

    static List<TileResponse> Wait(AtlasServer server, ConcurrentQueue<TileResponse> sent, int count, int ms = 30000)
    {
        var got = new List<TileResponse>();
        var watch = Stopwatch.StartNew();
        while (got.Count < count && watch.ElapsedMilliseconds < ms)
        {
            server.Tick();
            while (sent.TryDequeue(out var r)) got.Add(r);
            if (got.Count < count) Thread.Sleep(5);
        }
        return got;
    }
}
