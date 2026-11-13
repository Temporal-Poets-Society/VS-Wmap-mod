using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Wmap;
using Wmap.Core;
using Wmap.Integrations;
using Wmap.Server;

/// <summary>
/// Ore heat maps on the server (WM-D47, tiled in WM-D48): the ores offered are the pick's; a chunk-cell tile and the
/// whole-world tile have an answer in every cell on the world where the generators answer for themselves; a complete tile
/// is stored and read back the same by a new server without working it out again; the listed-ores map counts exactly the
/// ores above the pick's threshold; a player without the full map is refused, a newer request replaces the tiles of an
/// older one, and the first request starts storing the coarse levels ahead.
/// </summary>
static class OreHeatChecks
{
    sealed class HeatPlayer : ServerPlayer
    {
        public string Uid;
        public HeatPlayer() : base(null, null) { }
        public override string PlayerUID => Uid;
    }

    public static string Run(ICoreServerAPI api, WorldSource source, System.Func<double, double, (double X, double Z)?> locate)
    {
        var t = source.Info.Topology();
        string folder = Path.Combine(Path.GetTempPath(), "wmap-oreheat-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        OreHeatServer Make() => new(api, GuiServiceProxy.Make<IServerNetworkChannel>((method, args) => GuiServiceProxy.Default(method)),
            p => p.PlayerUID == "full", () => t, source.Samplers, 4, () => folder, 512);
        var heat = Make();
        OreHeatServer again = null;
        try
        {
            string[] codes = heat.Codes;
            string method = heat.Method;
            if (codes.Length == 0) throw new Exception("no ores offered");
            // The pick's ore codes: every deposit variant with an ore map, children included.
            var pick = new HashSet<string>();
            foreach (var v in api.ModLoader.GetModSystem<Vintagestory.ServerMods.GenDeposits>().Deposits)
            {
                if (v.WithOreMap) pick.Add(v.Code);
                if (v.ChildDeposits != null) foreach (var c in v.ChildDeposits) if (c.WithOreMap) pick.Add(c.Code);
            }
            if (codes.Any(c => !pick.Contains(c))) throw new Exception("offers an ore the pick does not know: " + string.Join(",", codes.Where(c => !pick.Contains(c))));
            bool earth = api.ModLoader.IsModEnabled("earthmap") || api.ModLoader.IsModEnabled("earth");
            if (earth && method != OreHeatRequest.Reading) throw new Exception($"Earth world answered by {method}, not the generators' own readings");
            bool reading = method == OreHeatRequest.Reading;

            var at = locate?.Invoke(51.2, 7.9) ?? (t.StartX + t.Width / 2, t.Height / 2);
            // Vanilla's ore maps answer only in loaded regions: look where a chunk is loaded.
            if (!reading && api.WorldManager.AllLoadedMapchunks.Keys.Cast<long>().FirstOrDefault() is long index and > 0)
            {
                var chunk = api.WorldManager.MapChunkPosFromChunkIndex2D(index);
                at = (chunk.X * 32 + 16, chunk.Y * 32 + 16);
            }
            string code = codes.Contains("nativecopper") ? "nativecopper" : codes[0];
            // Chunk cells around the spot: worked out, then stored.
            int fine = OreHeat.MinLevel;
            var (fx, fz) = OreHeat.KeyAt(t, fine, at.X, at.Z);
            var watch = Stopwatch.StartNew();
            var one = heat.Serve(code, fine, fx, fz);
            long fineMs = watch.ElapsedMilliseconds;
            Check(t, one, reading, "chunk cells " + code);
            if (!reading && api.WorldManager.AllLoadedMapchunks.Count > 0 && one.Blocks.All(float.IsNaN)) throw new Exception("no answer around a loaded chunk");
            int files = Directory.GetFiles(folder, "o-*.wmt").Length;
            if (reading && (one.Partial || files != 1 || one.Millis <= 0)) throw new Exception($"complete tile not stored ({files} files, partial {one.Partial}, {one.Millis} ms)");
            if (one.Partial && files != 0) throw new Exception("a partial tile was stored");
            var memory = heat.Serve(code, fine, fx, fz);
            again = Make();
            watch.Restart();
            var disk = again.Serve(code, fine, fx, fz);
            long diskMs = watch.ElapsedMilliseconds;
            if (reading && (memory.Millis != 0 || disk.Millis != 0 || !Same(one, memory) || !Same(one, disk))) throw new Exception("stored tile answered differently or worked out again");
            // The listed count is the number of ores above the pick's threshold, cell by cell.
            var listed = heat.Serve(OreHeat.Listed, fine, fx, fz);
            var sum = new float[OreHeat.TileCount];
            var strongest = new float[OreHeat.TileCount];
            var known = new bool[OreHeat.TileCount];
            var tops = new List<string>();
            foreach (string c in codes)
            {
                var r = heat.Serve(c, fine, fx, fz);
                for (int i = 0; i < OreHeat.TileCount; i++)
                {
                    if (float.IsNaN(r.Factors[i])) continue;
                    known[i] = true;
                    if (r.Factors[i] > OreHeat.TraceFactor) sum[i]++;
                    strongest[i] = Math.Max(strongest[i], r.Factors[i]);
                }
                double mean = r.Blocks.Where(b => !float.IsNaN(b)).DefaultIfEmpty(0).Average();
                tops.Add($"{c} {mean:0}");
            }
            for (int i = 0; i < OreHeat.TileCount; i++)
                if (float.IsNaN(listed.Blocks[i]) != !known[i]) throw new Exception($"listed map cell {i}: answered {!float.IsNaN(listed.Blocks[i])}, ores answered {known[i]}");
                else if (known[i] && (listed.Blocks[i] != sum[i] || Math.Abs(listed.Factors[i] - strongest[i]) > 1e-6))
                    throw new Exception($"listed map cell {i}: {listed.Blocks[i]} ores, factor {listed.Factors[i]}; one by one {sum[i]}, {strongest[i]}");
            // The whole world in one tile, every ore.
            int top = OreHeat.MaxLevel(t);
            watch.Restart();
            var world = heat.Serve(OreHeat.Listed, top, 0, 0);
            long worldMs = watch.ElapsedMilliseconds;
            Check(t, world, reading, "world listed");
            if (world.Blocks.Any(b => !float.IsNaN(b) && (b < 0 || b > codes.Length || b != MathF.Round(b)))) throw new Exception("listed counts are not whole ore counts");
            if (heat.Serve("no-such-ore", fine, fx, fz).Error == null || heat.Serve(code, top + 1, 0, 0).Error == null) throw new Exception("an unknown ore or tile was answered");

            // Access and replacement through the request path.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var queue = (ConcurrentQueue<(string, OreHeatResponse)>)typeof(OreHeatServer).GetField("done", flags).GetValue(heat);
            var refused = (HeatPlayer)RuntimeHelpers.GetUninitializedObject(typeof(HeatPlayer)); refused.Uid = "game";
            heat.Request(refused, Request(code, (fine, fx, fz)));
            if (typeof(OreHeatServer).GetField("worker", flags).GetValue(heat) != null) throw new Exception("a player without the full map was answered");
            // Tiles not worked out yet, east and west of the spot.
            var fresh = Enumerable.Range(1, 40).SelectMany(d => new[] { (fine, fx + 2 * d, fz), (fine, fx - 2 * d, fz) })
                .Where(k => OreHeat.ValidKey(t, k.Item1, k.Item2, k.Item3)).Take(5).ToArray();
            if (fresh.Length < 5) throw new Exception("no room for the request checks");
            (int, int, int)[] first = fresh[..3], second = fresh[3..];
            var full = (HeatPlayer)RuntimeHelpers.GetUninitializedObject(typeof(HeatPlayer)); full.Uid = "full";
            heat.Request(full, Request(code, first));
            heat.Request(full, Request(code, second));
            heat.Request(full, Request("no-such-ore", first));
            var wait = Stopwatch.StartNew();
            var answers = new List<OreHeatResponse>();
            bool Answered((int L, int X, int Z) k) => answers.Any(a => a.Level == k.L && a.X == k.X && a.Z == k.Z);
            while (wait.ElapsedMilliseconds < 120000 && !second.All(Answered))
            {
                while (queue.TryDequeue(out var a)) answers.Add(a.Item2);
                System.Threading.Thread.Sleep(50);
            }
            System.Threading.Thread.Sleep(300);
            while (queue.TryDequeue(out var late)) answers.Add(late.Item2);
            if (!second.All(Answered) || Answered(first[2]) || answers.Any(a => a.Code != code || a.Error != null))
                throw new Exception("request path: answered " + string.Join(" ", answers.Select(a => $"{a.Code}/{a.Level}/{a.X}/{a.Z}")) + $" (want {string.Join(" ", second)}, not {first[2]})");
            string ahead = "";
            if (reading)
            {
                // The first request started storing the coarse levels.
                wait.Restart();
                while (wait.ElapsedMilliseconds < 20000 && heat.StoredAhead.Done < 2) System.Threading.Thread.Sleep(100);
                var (doneAhead, of) = heat.StoredAhead;
                if (of != OreHeat.StoreAheadKeys(t, OreHeatServer.StoreAheadTiles).Count || doneAhead < 2) throw new Exception($"store-ahead not running ({doneAhead} of {of})");
                ahead = $"; store-ahead {doneAhead} of {of} coarse tiles after {wait.ElapsedMilliseconds / 1000.0:0.0} s";
            }
            return $"Ore heat maps: PASS ({codes.Length} ores by {method}; {code} over a tile of chunk cells in {fineMs} ms, stored and read back in {diskMs} ms; " +
                $"listed ores over the whole world (level {top}) in {worldMs} ms, " +
                $"{world.Blocks.Count(b => b == 0) * 100 / Math.Max(1, world.Blocks.Count(b => !float.IsNaN(b)))}% of cells list nothing; listed = one by one; " +
                $"refusal and replacement{ahead}; mean blocks/chunk near the spot: {string.Join(", ", tops.Take(12))})";
        }
        finally
        {
            heat.Dispose(); again?.Dispose();
            // Store-ahead threads finish the tile in hand before they stop.
            for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
            {
                try { Directory.Delete(folder, true); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { System.Threading.Thread.Sleep(500); }
            }
        }
    }

    static OreHeatRequest Request(string code, params (int Level, int X, int Z)[] tiles) => new()
    {
        Code = code, Level = tiles.Select(k => k.Level).ToArray(), X = tiles.Select(k => k.X).ToArray(), Z = tiles.Select(k => k.Z).ToArray()
    };

    static bool Same(OreHeatResponse a, OreHeatResponse b) =>
        a.Blocks.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Blocks.Select(BitConverter.SingleToInt32Bits))
        && a.Factors.Select(BitConverter.SingleToInt32Bits).SequenceEqual(b.Factors.Select(BitConverter.SingleToInt32Bits));

    static void Check(Topology t, OreHeatResponse r, bool everywhere, string what)
    {
        if (r == null || r.Error != null) throw new Exception($"{what}: {r?.Error ?? "no answer"}");
        if (r.Blocks?.Length != OreHeat.TileCount || r.Factors?.Length != OreHeat.TileCount) throw new Exception($"{what}: {r.Blocks?.Length} values for {OreHeat.TileCount} cells");
        // Cells off the world (past the ring's end or the south edge) have no answer; every other one does.
        int off = 0, nan = 0;
        for (int row = 0; row < OreHeat.TileCells; row++)
            for (int column = 0; column < OreHeat.TileCells; column++)
            {
                bool outside = double.IsNaN(OreHeat.Sample(t, r.Level, r.X, r.Z, column, row).X);
                float b = r.Blocks[row * OreHeat.TileCells + column];
                if (outside) { off++; if (!float.IsNaN(b)) throw new Exception($"{what}: an answer off the world"); }
                else if (float.IsNaN(b)) nan++;
            }
        if (everywhere && nan > 0) throw new Exception($"{what}: {nan} of {OreHeat.TileCount - off} cells on the world without an answer");
        if (r.Blocks.Any(b => b < 0 || float.IsInfinity(b)) || r.Factors.Any(f => f < 0 || float.IsInfinity(f))) throw new Exception($"{what}: negative or infinite values");
    }
}
