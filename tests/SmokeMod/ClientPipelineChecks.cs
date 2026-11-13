using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Client;
using Wmap;
using Wmap.Client;
using Wmap.Core;

public class FakeChannel : DispatchProxy
{
    public readonly ConcurrentQueue<TileRequest> Requests = new();
    public readonly ConcurrentQueue<BaseRequest> ChartRequests = new();
    public readonly ConcurrentQueue<TeleportRequest> Teleports = new();
    public readonly ConcurrentQueue<OreHeatRequest> OreHeat = new();
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "SendPacket" && args[0] is TileRequest r) Requests.Enqueue(r);
        if (method.Name == "SendPacket" && args[0] is BaseRequest b) ChartRequests.Enqueue(b);
        if (method.Name == "SendPacket" && args[0] is TeleportRequest t) Teleports.Enqueue(t);
        if (method.Name == "SendPacket" && args[0] is OreHeatRequest o) OreHeat.Enqueue(o);
        if (method.ReturnType == typeof(bool)) return true;
        return null;
    }
}
public static class ClientPipelineChecks
{
    public static string Run(string path)
    {
        var channel = DispatchProxy.Create<IClientNetworkChannel, FakeChannel>();
        var fake = (FakeChannel)(object)channel;
        var info = new AtlasInfo { Session = "smoke", Width = 2048, Height = 1024, Allowed = true, CanDevelop = true, HasOverview = true, CanPredict = true, SeaLevel = 110 };
        var config = new WmapConfig(); config.Validate();
        string observations = Path.Combine(path, "observations");
        var key = new TileKey(0, 2, 0, 0);
        byte[] data = SemanticTile.Pack(SemanticTile.Sample(info.Topology(), key, (x, z) => new MapSample(125, 0, TileData.Land), 110, CancellationToken.None));
        void Wait(TileClient c, Func<bool> condition)
        {
            for (int n = 0; n < 1000; n++) { c.Tick(); if (condition()) return; Thread.Sleep(5); }
            throw new Exception("Client pipeline timed out: " + c.LastError);
        }
        using (var c = new TileClient(channel, info, config, path, observations))
        {
            c.SetView(new[] { key });
            Wait(c, () => !fake.Requests.IsEmpty);
            fake.Requests.TryDequeue(out var request);
            c.SetView(new[] { key, new TileKey(0, 2, 1, 0) });
            if (c.View != request.View) throw new Exception("Zoom/pan cancelled useful in-flight terrain");
            c.Receive(new TileResponse { Session = "wrong-world", View = request.View, Level = key.Level, X = 0, Z = 0, Data = data });
            if (c.ReceivedTiles != 0) throw new Exception("Accepted wrong-world data");
            c.Receive(new TileResponse { Session = info.Session, View = request.View, Level = key.Level, X = 0, Z = 0, Data = data });
            Wait(c, () => c.Get(key)?.Rgba != null);
            if (Semantic.DecodeSigned(c.Get(key).Rgba[1000 * 4 + 1], Semantic.DefaultHeightRange) < 10) throw new Exception("Network terrain lost");
            var observed = new TileKey(1, 2, 0, 0);
            c.SetView(new[] { observed });
            Wait(c, () => c.Get(observed)?.Data != null);
            c.Observe(10, 10, new MapSample(222, 0, TileData.Land));
            Wait(c, () => c.Get(observed)?.RenderedRevision == c.Get(observed)?.Revision && File.Exists(Path.Combine(observations, observed.FileName)));
            if (c.Get(observed).Rgba == null || c.Get(observed).Rgba[(3 * TileData.Padded + 3) * 4 + 3] != 255) throw new Exception("Recorded terrain not encoded");
            // 0.4.3: every recorded block goes into the one-block level; a coarser level keeps only the sample at its cell's centre.
            var fine = new TileKey(1, 0, 0, 0);
            c.SetView(new[] { observed, fine });
            Wait(c, () => c.Get(fine)?.Data != null);
            // A four-block sample stays out of the one-block level; a block of a cell recorded block by block reaches it.
            c.Observe(14, 14, new MapSample(240, 0, TileData.Land));
            if (c.Get(fine).Data.Heights[(1 + 14) * TileData.Padded + 1 + 14] == 240) throw new Exception("A four-block sample reached the one-block level");
            c.Observe(11, 10, new MapSample(250, 0, TileData.Land), true);
            if (c.Get(fine).Data.Heights[(1 + 10) * TileData.Padded + 1 + 11] != 250 || !c.HasObservation(fine)) throw new Exception("A recorded block is missing from the one-block level");
            if (c.Get(observed).Data.Heights[3 * TileData.Padded + 3] != 222) throw new Exception("An off-centre block changed a four-block cell");
            // Colour alone is no change: the tile is not written again.
            int revision = c.Get(observed).Revision;
            c.Observe(10, 10, new MapSample(222, 0, TileData.Land, 12345));
            if (c.Get(observed).Revision != revision) throw new Exception("A colour-only change was recorded as a change");
            // 0.4.4: coarser levels keep one four-block sample per cell (0.4.3 kept none above four blocks), and a sample for
            // a recording still loading is reported as not kept, so the recorder takes it again.
            var coarse = new TileKey(1, 3, 0, 0);
            Wait(c, () => c.Loaded(10, 10));
            if (!c.Observe(10, 10, new MapSample(260, 0, TileData.Land))) throw new Exception("A sample with every recording loaded was not kept");
            if (c.Get(coarse)?.Data?.Heights[2 * TileData.Padded + 2] != 260 || !c.HasObservation(coarse)) throw new Exception("A four-block sample did not reach the eight-block level");
            c.Observe(14, 14, new MapSample(270, 0, TileData.Land));
            if (c.Get(coarse).Data.Heights[2 * TileData.Padded + 2] != 260) throw new Exception("A second four-block sample changed an eight-block cell");
            double farX = 1100, farZ = 300;
            var farKey = new TileKey(1, 2, (int)(farX / 512), 0);
            if (c.Get(farKey) != null) throw new Exception("Deferred-sample setup: the tile is already resident");
            if (c.Observe(farX, farZ, new MapSample(280, 0, TileData.Land))) throw new Exception("A sample for a recording still loading was reported as kept");
            Wait(c, () => c.Loaded(farX, farZ));
            int farCell = (1 + (int)(farZ / 4)) * TileData.Padded + 1 + (int)(farX % 512 / 4);
            if (c.Get(farKey).Data.Heights[farCell] == 280) throw new Exception("Deferred-sample check: the dropped sample was kept after all");
            if (!c.Observe(farX, farZ, new MapSample(280, 0, TileData.Land)) || c.Get(farKey).Data.Heights[farCell] != 280) throw new Exception("A sample taken again after loading was not kept");
            // A cell with blocks recorded one by one is known, so the recorder records it again (its change may be gone).
            if (c.HasFineRecording(8, 8) != true || c.HasFineRecording(40, 40) != false) throw new Exception("Fine recordings of a cell not reported");
            // Dispose with a dirty observation, then immediately reconnect before its final writer finishes.
            c.Observe(10, 10, new MapSample(333, 0, TileData.Land));
        }
        using (var c = new TileClient(channel, info, config, path, observations))
        {
            var observed = new TileKey(1, 2, 0, 0); var fine = new TileKey(1, 0, 0, 0);
            c.SetView(new[] { key, observed, fine });
            Wait(c, () => c.Get(key)?.Rgba != null && c.Get(observed)?.Data != null && c.Get(fine)?.Data != null);
            if (c.DiskHits < 2 || c.Get(observed).Data.Heights[3 * TileData.Padded + 3] != 333) throw new Exception("Warm cache/observed persistence failed");
            if (!c.HasObservation(observed)) throw new Exception("Recorded tile index not restored");
            // The first session in a new folder keeps its one-block recordings (0.4.11 took them for 0.4.3's and deleted them).
            if (!c.HasObservation(fine) || c.Get(fine).Data.Heights[(1 + 10) * TileData.Padded + 1 + 11] != 250) throw new Exception("The first session's one-block recordings were lost on reconnecting");
        }
        Wmap.Server.AtlasServer.TrimCache(observations, 0);
        using (var c = new TileClient(channel, info, config, Path.Combine(path, "new-prediction-revision"), observations))
        {
            var observed = new TileKey(1, 2, 0, 0);
            c.SetView(new[] { observed });
            Wait(c, () => c.Get(observed)?.Data != null);
            if (c.Get(observed).Data.Heights[3 * TileData.Padded + 3] != 333) throw new Exception("Cache revision or quota erased observations");
        }
        // A recording that cannot be read for a while (locked by another process) is neither edited nor
        // replaced by an empty tile; it loads once readable.
        using (var c = new TileClient(channel, info, config, Path.Combine(path, "locked"), observations))
        {
            var observed = new TileKey(1, 2, 0, 0);
            var hold = new FileStream(Path.Combine(observations, observed.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                c.SetView(new[] { observed });
                Wait(c, () => c.Get(observed) is { Busy: false } && c.LastError?.StartsWith("Observation read") == true);
                c.Observe(10, 10, new MapSample(444, 0, TileData.Land));
                if (c.Get(observed).Data != null || c.Get(observed).Dirty) throw new Exception("An unreadable recording was loaded as empty and edited");
            }
            finally { hold.Dispose(); }
            for (int n = 0; n < 2000 && c.Get(observed)?.Data == null; n++) { c.Tick(); Thread.Sleep(5); }
            if (c.Get(observed)?.Data?.Heights[3 * TileData.Padded + 3] != 333) throw new Exception("A recording that became readable again was not reloaded intact");
        }
        // 0.4.4: a session that closes while the previous one is still writing its recordings writes after it, not before.
        {
            var flushes = (ConcurrentDictionary<string, System.Threading.Tasks.Task>)typeof(TileClient).GetField("flushes", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string orderFolder = Path.Combine(path, "order");
            var slow = new System.Threading.Tasks.TaskCompletionSource();
            flushes[orderFolder] = slow.Task;   // the previous session, still writing
            var quick = new TileClient(channel, info, config, Path.Combine(path, "order-cache"), orderFolder);
            quick.Dispose();
            Thread.Sleep(300);
            if (!flushes.TryGetValue(orderFolder, out var after) || after == slow.Task || after.IsCompleted)
                throw new Exception("A session closed during the previous session's final writes did not wait for them");
            slow.SetResult();
            if (!after.Wait(5000)) throw new Exception("Final writes did not finish after the previous session's");
        }
        // A truncated recording is damage, not a read failure: it starts empty and can be recorded again.
        var truncated = new TileKey(1, 2, 1, 0);
        byte[] whole = TileCodec.Encode(new TileData());
        File.WriteAllBytes(Path.Combine(observations, truncated.FileName), whole.AsSpan(0, Math.Min(whole.Length, 40)).ToArray());
        using (var c = new TileClient(channel, info, config, Path.Combine(path, "truncated"), observations))
        {
            c.SetView(new[] { truncated });
            Wait(c, () => c.Get(truncated)?.Data != null);
        }
        // A folder without the recorder's marker: one-block recordings of the 0.4.3 test builds (written before the marker
        // existed) are dropped, later ones (a first session of 0.4.4 to 0.4.11) stay.
        {
            string legacy = Path.Combine(path, "legacy");
            Directory.CreateDirectory(legacy);
            var old = new TileKey(1, 0, 0, 0); var later = new TileKey(1, 0, 1, 0);
            var cell = new TileData(); cell.Set(TileData.Padded + 1, new MapSample(150, 0, TileData.Land));
            TileCodec.WriteAtomic(Path.Combine(legacy, old.FileName), TileCodec.Encode(cell));
            TileCodec.WriteAtomic(Path.Combine(legacy, later.FileName), TileCodec.Encode(cell));
            File.SetLastWriteTimeUtc(Path.Combine(legacy, old.FileName), new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
            using (var c = new TileClient(channel, info, config, Path.Combine(path, "legacy-cache"), legacy))
            {
                Wait(c, () => File.Exists(Path.Combine(legacy, TileClient.RecorderMarker)) && c.HasObservation(later));
                if (File.Exists(Path.Combine(legacy, old.FileName)) || c.HasObservation(old)) throw new Exception("A 0.4.3 test build's one-block recording was kept");
            }
        }
        File.WriteAllBytes(Path.Combine(path, key.FileName), new byte[] { 7, 3, 1 });
        while (fake.Requests.TryDequeue(out _)) { }
        using (var c = new TileClient(channel, info, config, path))
        {
            c.SetView(new[] { key }); Wait(c, () => !fake.Requests.IsEmpty);
        }
        // Without shared predictions the client never asks for predicted terrain.
        var closed = new AtlasInfo { Session = "smoke", Width = 2048, Height = 1024, Allowed = true, SeaLevel = 110 };
        while (fake.Requests.TryDequeue(out _)) { }
        using (var c = new TileClient(channel, closed, config, Path.Combine(path, "closed")))
        {
            c.SetView(new[] { new TileKey(0, 2, 2, 0) });
            for (int n = 0; n < 5; n++) { c.Tick(); Thread.Sleep(5); }
            if (!fake.Requests.IsEmpty) throw new Exception("Requested predictions the server does not share");
            c.SetDevelopment(true);
            if (c.Development) throw new Exception("Client enabled development without permission");
        }
        // World chart transfer: chunked, verified, cached, then reused without a download.
        var raster = new BaseRaster { Width = 64, Height = 32, Rgba = new byte[64 * 32 * 4] };
        new Random(3).NextBytes(raster.Rgba);
        byte[] packed = raster.Pack();
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packed)).ToLowerInvariant();
        string chartFolder = Path.Combine(path, "chart");
        var manifest = new AtlasInfo { BaseId = id, BaseBytes = packed.Length, BaseWidth = 64, BaseHeight = 32, CanPredict = true };
        using (var chart = new BaseMapClient(channel, chartFolder))
        {
            chart.Update(manifest); chart.Tick();
            if (!fake.ChartRequests.TryDequeue(out var first) || first.BaseId != id || first.Offset != 0) throw new Exception("World chart not requested");
            var damaged = (byte[])packed.Clone(); damaged[^1] ^= 1;
            chart.Receive(new BaseChunk { BaseId = id, Offset = 0, Data = damaged });
            BaseRaster got = null;
            for (int n = 0; n < 400 && got == null; n++) { got = chart.Take(); Thread.Sleep(5); }
            if (got != null) throw new Exception("Damaged world chart accepted");
            chart.Tick();
            if (!fake.ChartRequests.TryDequeue(out _)) throw new Exception("Damaged world chart was not fetched again");
            chart.Receive(new BaseChunk { BaseId = id, Offset = 0, Data = packed });
            for (int n = 0; n < 400 && got == null; n++) { got = chart.Take(); Thread.Sleep(5); }
            if (got == null || !got.Rgba.SequenceEqual(raster.Rgba)) throw new Exception("World chart transfer failed: " + chart.Status);
        }
        while (fake.ChartRequests.TryDequeue(out _)) { }
        using (var chart = new BaseMapClient(channel, chartFolder))
        {
            chart.Update(manifest); chart.Tick();
            BaseRaster got = null;
            for (int n = 0; n < 400 && got == null; n++) { got = chart.Take(); Thread.Sleep(5); }
            if (got == null || !fake.ChartRequests.IsEmpty) throw new Exception("Cached world chart not reused");
        }
        return "Client pipeline: PASS (coarse levels from four-block samples, deferred samples while loading, fine-cell refresh, final-write order across quick reconnects, semantic network tiles, zoom/pan request reuse, unshared prediction isolation, development permission, recorded tile encoding/index, stale-world rejection, warm cache, reconnect persistence, quota preservation, corruption recovery, world chart transfer/verification/retry/disk reuse)";
    }
}
