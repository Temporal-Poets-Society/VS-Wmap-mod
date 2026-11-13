using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Fetches the server's baked world raster once per world revision and keeps it on disk, keyed by
/// its SHA-256, so later sessions start from the local copy. Download, verification and decoding
/// run off the main thread; the decoded raster is handed to the renderer.
/// </summary>
public sealed class BaseMapClient : IDisposable
{
    const int Window = 16;
    readonly IClientNetworkChannel channel;
    readonly string folder;
    readonly CancellationTokenSource stop = new();
    string id;
    byte[] buffer;
    bool[] arrived;
    long[] requestedAt;
    int received;
    Task<BaseRaster> decoding;
    AtlasInfo last;
    string retried;
    public string Status { get; private set; } = "";
    public float Progress { get; private set; }
    public string Id => id;

    // The climate raster (ClimateRaster) instead of the world chart: its own id, file name and decoder, no status line.
    readonly bool climate;
    public BaseMapClient(IClientNetworkChannel channel, string folder, bool climate = false) { this.channel = channel; this.folder = folder; this.climate = climate; }

    string PathFor(string baseId) => Path.Combine(folder, (climate ? "climate-" : "world-") + baseId + ".wmr");
    string WantedId(AtlasInfo info) => climate ? info?.ClimateId : info?.BaseId;
    BaseRaster Decode(byte[] data) => climate ? ClimateRaster.Unpack(data) : BaseRaster.Unpack(data);
    string Name => climate ? "climate" : "world chart";

    /// <summary>Called with every manifest; starts loading once the server reports a finished raster.</summary>
    public void Update(AtlasInfo info)
    {
        last = info;
        string wanted = WantedId(info);
        int bytes = climate ? info.ClimateBytes : info.BaseBytes;
        if (wanted == null)
        {
            Status = !climate && (info.HasOverview || info.CanPredict) ? (info.BaseProgress > 0 ? $"Charting the world… {info.BaseProgress:P0}" : "Charting the world…") : "";
            return;
        }
        if (wanted == id || !System.Text.RegularExpressions.Regex.IsMatch(wanted, "^[a-f0-9]{64}$")) return;
        if (bytes <= 0 || bytes > 512 << 20) return;
        id = wanted; buffer = null; received = 0; decoding = null; Progress = 0;
        string path = PathFor(id);
        if (File.Exists(path))
        {
            Status = climate ? "" : "Opening world chart…";
            string expected = id;
            decoding = Task.Run(() =>
            {
                byte[] data = File.ReadAllBytes(path);
                if (Hash(data) != expected) throw new InvalidDataException("Cached " + Name + " is damaged");
                return Decode(data);
            }, stop.Token);
            return;
        }
        buffer = new byte[bytes];
        int chunks = (bytes + BaseChunk.Size - 1) / BaseChunk.Size;
        arrived = new bool[chunks]; requestedAt = new long[chunks];
        Status = climate ? "" : "Receiving world chart…";
    }

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public void Receive(BaseChunk chunk)
    {
        if (chunk.BaseId != id || buffer == null || chunk.Data == null || chunk.Offset < 0 || chunk.Offset % BaseChunk.Size != 0) return;
        int index = chunk.Offset / BaseChunk.Size;
        if (index >= arrived.Length || arrived[index] || chunk.Offset + chunk.Data.Length > buffer.Length) return;
        if (chunk.Data.Length != Math.Min(BaseChunk.Size, buffer.Length - chunk.Offset)) return;
        Buffer.BlockCopy(chunk.Data, 0, buffer, chunk.Offset, chunk.Data.Length);
        arrived[index] = true; received++;
        Progress = (float)received / arrived.Length;
        if (!climate) Status = $"Receiving world chart… {Progress:P0}";
        if (received < arrived.Length) return;
        byte[] data = buffer; string expected = id, path = PathFor(id);
        buffer = null;
        if (!climate) Status = "Opening world chart…";
        decoding = Task.Run(() =>
        {
            if (Hash(data) != expected) throw new InvalidDataException("The " + Name + " transfer was damaged");
            try { TileCodec.WriteAtomic(path, data); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { /* It still works without a local copy. */ }
            return Decode(data);
        }, stop.Token);
    }

    public void Tick()
    {
        if (buffer == null || arrived == null) return;
        long now = Environment.TickCount64;
        int open = 0;
        for (int i = 0; i < arrived.Length && open < Window; i++)
        {
            if (arrived[i]) continue;
            if (requestedAt[i] != 0 && now - requestedAt[i] < 8000) { open++; continue; }
            channel.SendPacket(new BaseRequest { BaseId = id, Offset = i * BaseChunk.Size });
            requestedAt[i] = now; open++;
        }
    }

    /// <summary>The decoded raster, once; null while loading.</summary>
    public BaseRaster Take()
    {
        if (decoding == null || !decoding.IsCompleted) return null;
        var task = decoding; decoding = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            Status = (climate ? "Climate" : "World chart") + " unavailable: " + (task.Exception?.InnerException?.Message ?? "cancelled");
            try { File.Delete(PathFor(id)); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            string failed = id;
            id = null;
            // One fresh download per chart; a second failure leaves the status visible.
            if (retried != failed && WantedId(last) == failed) { retried = failed; Update(last); }
            return null;
        }
        Status = "";
        return task.Result;
    }

    public void Dispose() { stop.Cancel(); buffer = null; }
}
