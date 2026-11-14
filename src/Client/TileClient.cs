using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Main-thread resident detail tiles with one bounded disk/decode worker. Layer 0 holds predicted
/// tiles, already in semantic texels from the server. Layer 1 holds recorded observations, which are
/// editable and encoded to semantic texels on the worker. GPU work is separate.
/// </summary>
public sealed class TileClient : IDisposable
{
    public sealed class Entry
    {
        static long next;
        /// <summary>This entry's own number: an entry evicted and loaded again is another.</summary>
        public readonly long Id = Interlocked.Increment(ref next);
        public TileData Data;      // recorded tiles only
        public byte[] Rgba;        // semantic texels, null while empty or loading
        public long Used;
        public int Revision, RenderedRevision = -1;
        public bool Busy, Missing, Dirty;
        public long RequestAt;     // predicted: next request; recorded: next save retry after a failed write
        public long QueuedAt;      // recorded: when it was last copied for encoding and saving
        public byte[] Prediction;  // recorded: the prediction's texels it was last encoded over
    }
    // Texels: a recording is encoded to semantic texels only while a view wants it (0.5.3, WM-0035: every save and load of
    // the recorder's tiles, map closed, encoded two 67 KB arrays nobody drew); Result.Texels false leaves the texels as
    // they were, and the view encodes the tile when it wants it (RenderedRevision stays behind).
    sealed record Work(TileKey Key, TileData Snapshot, byte[] Network, int Revision, bool Save, byte[] Prediction = null, bool Texels = true);
    sealed record Result(TileKey Key, TileData Data, byte[] Rgba, int Revision, bool Missing, string Error, bool SaveFailed = false, bool ReadFailed = false, bool Texels = true);
    readonly BlockingCollection<Work> work = new(64);
    readonly BlockingCollection<Result> results = new(64);
    readonly CancellationTokenSource stop = new();
    readonly IClientNetworkChannel channel;
    readonly WmapConfig config;
    readonly string folder;
    readonly string observations;
    static readonly ConcurrentDictionary<string, Task> flushes = new();
    readonly Task worker;
    // The previous session's final writes to these observations: this session's final writes run after them, also when
    // it closes before they finish (its worker then stops waiting, but the order of writes must hold).
    readonly Task previousFlush;
    readonly Dictionary<TileKey, Entry> entries = new();
    readonly HashSet<TileKey> desired = new();
    readonly ConcurrentDictionary<TileKey, byte> observedKeys = new();
    readonly Dictionary<TileKey, long> inFlight = new();
    public AtlasInfo Info { get; private set; }
    public readonly Topology Topology;
    public int View = 1;
    public bool Development { get; private set; }
    public string LastError;
    public int Pending => work.Count + inFlight.Count;
    public long DiskHits, ReceivedTiles;
    public event Action<TileKey> Invalidated;
    long clock;
    bool disposed;
    public TileClient(IClientNetworkChannel channel, AtlasInfo info, WmapConfig config, string folder, string observationFolder = null)
    {
        this.channel = channel; Info = info; Topology = info.Topology(); this.config = config; this.folder = folder;
        observations = observationFolder ?? folder;
        Development = info.CanDevelop && config.DevelopmentMode;
        Task previous = previousFlush = flushes.GetOrAdd(observations, Task.CompletedTask);
        worker = Task.Run(() =>
        {
            try
            {
                previous.Wait(stop.Token);
                try { IndexObservations(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { LastError = "Observation index: " + e.Message; }
                Worker();
            }
            catch (OperationCanceledException) { }
        });
    }
    /// <summary>Same session, newer manifest (world chart progress or permissions).</summary>
    public void Update(AtlasInfo info)
    {
        Info = info;
        // Permission can also arrive later (a player switching to creative): the wanted mode then applies.
        if (!info.CanDevelop && Development) SetDevelopment(false);
        else if (info.CanDevelop && config.DevelopmentMode && !Development) SetDevelopment(true);
    }
    /// <summary>Marks recordings made by the recorder of WM-D29 (block by block only where the world differs).</summary>
    public const string RecorderMarker = "recorder-v2";
    /// <summary>When the recorder of WM-D29 and its marker were built (the 0.4.3 commit "recorder by difference",
    /// 2026-10-21 17:19 +03:00): recordings written before it, in a folder without the marker, are the test builds'.</summary>
    static readonly DateTime RecorderSince = new(2026, 10, 21, 14, 19, 25, DateTimeKind.Utc);
    void IndexObservations()
    {
        // Once: drop the one- and two-block recordings of the 0.4.3 test builds, which recorded every block, treetops
        // included, and would draw over the prediction everywhere they were made. A new folder gets the marker before
        // anything is recorded in it, so this session's own fine recordings are not taken for theirs next time. Only
        // files written before the marker existed are theirs: a folder of 0.4.4 to 0.4.11 whose first session never
        // got its marker keeps its recordings.
        string marker = Path.Combine(observations, RecorderMarker);
        if (!File.Exists(marker))
        {
            Directory.CreateDirectory(observations);
            foreach (string pattern in new[] { "1-0-*.wmt", "1-1-*.wmt" })
                foreach (string file in Directory.EnumerateFiles(observations, pattern))
                    try { if (File.GetLastWriteTimeUtc(file) < RecorderSince) File.Delete(file); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            try { File.WriteAllText(marker, "Recordings below four blocks are cells that differ from the prediction (Wmap 0.4.3)."); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        foreach (string file in Directory.EnumerateFiles(observations, "1-*.wmt"))
        {
            stop.Token.ThrowIfCancellationRequested();
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length == 4 && int.TryParse(parts[1], out int level) && int.TryParse(parts[2], out int x) && int.TryParse(parts[3], out int z))
            {
                var key = new TileKey(1, level, x, z);
                if (Topology.Valid(key) && observedKeys.TryAdd(key, 0)) Interlocked.Increment(ref observationVersion);
            }
        }
    }
    public bool HasObservation(TileKey key) => observedKeys.ContainsKey(key);
    int observationVersion;
    /// <summary>Changes when a recording tile appears, so a view selected by <see cref="HasObservation"/> can be kept until then.</summary>
    public int ObservationVersion => Volatile.Read(ref observationVersion);
    public void SetDevelopment(bool enabled)
    {
        bool next = enabled && Info.CanDevelop;
        if (Development == next) return;
        Development = next; CloseView();
    }
    string PathFor(TileKey key) => Path.Combine(key.Layer == 1 ? observations : folder, key.FileName);
    /// <summary>Where a recording tile is kept on disk (the mid-distance view reads those not in memory).</summary>
    public string RecordingPath(TileKey key) => PathFor(key with { Layer = 1 });
    public void SetView(IEnumerable<TileKey> keys)
    {
        var next = keys.Where(k => k.Layer == 1 || Info.CanPredict).ToHashSet();
        // Panning/zooming must not cancel useful work already in flight. Finished tiles
        // remain reusable in the bounded cache. Only closing/switching mode changes View.
        if (!next.SetEquals(desired)) { desired.Clear(); desired.UnionWith(next); WantedVersion++; ordered = null; }
    }
    /// <summary>Changes whenever the wanted tiles change (<see cref="SetView"/>, <see cref="CloseView"/>): a view that set
    /// them sees when another view (or closing) replaced them.</summary>
    public int WantedVersion { get; private set; }
    public Entry Get(TileKey key)
    {
        if (entries.TryGetValue(key, out var e)) { e.Used = ++clock; return e; }
        return null;
    }
    /// <summary>The entry, if resident, without counting it as a use (the mid-distance view looks often and must not keep
    /// tiles from eviction).</summary>
    public Entry Peek(TileKey key) => entries.TryGetValue(key, out var e) ? e : null;
    Entry Ensure(TileKey key)
    {
        if (entries.TryGetValue(key, out var found)) { found.Used = ++clock; return found; }
        if (entries.Count >= config.ResidentTiles)
        {
            // The least recently used tile not busy, unsaved or wanted. A loop, not a sort: with the mid-distance view the
            // recorder fills the cache and this runs often (0.5.0-pre.1 allocated kilobytes per call here).
            TileKey? victim = null; long oldest = long.MaxValue;
            foreach (var p in entries)
                if (p.Value.Used < oldest && !p.Value.Busy && !p.Value.Dirty && !desired.Contains(p.Key)) { oldest = p.Value.Used; victim = p.Key; }
            if (victim == null) return null;
            // Uploaded GPU copies have their own bounded cache. Keep them available as
            // zoom fallbacks when the decoded CPU copy is evicted; changed data still
            // raises Invalidated when its worker result arrives.
            entries.Remove(victim.Value);
        }
        var e = new Entry { Used = ++clock };
        if (!work.TryAdd(new(key, null, null, 0, false, Texels: desired.Contains(key)))) return null;
        e.Busy = true; entries[key] = e;
        return e;
    }
    public void Tick()
    {
        long now = Environment.TickCount64;
        for (int i = 0; i < 16 && results.TryTake(out var result); i++)
        {
            if (!entries.TryGetValue(result.Key, out var e)) continue;
            e.Busy = false;
            if (result.Error != null) LastError = result.Error;
            // Dirty was cleared when the save was queued. A failed write keeps the recording unsaved
            // (never evicted, flushed on exit) and retries later.
            if (result.SaveFailed) { e.Dirty = true; e.RequestAt = now + 10000; }
            // Not loaded: Observe leaves the tile alone (Data stays null) until a retry reads it.
            if (result.ReadFailed) { e.RequestAt = now + 5000; continue; }
            // A recording's snapshot that came back: it replaces the tile's data (same revision) or is not needed (edited since);
            // either way one copy is spare for the next snapshot (0.5.0-pre.2: each was a new 150 KB tile).
            if (result.Revision != e.Revision) { if (result.Key.Layer == 1 && result.Data != null && result.Data != e.Data) Spare(result.Data); continue; }
            if (result.Key.Layer == 1) { if (e.Data != null && e.Data != result.Data) Spare(e.Data); e.Data = result.Data; }
            if (!result.Texels) continue;
            e.Rgba = result.Rgba; e.RenderedRevision = result.Revision;
            e.Missing = result.Missing;
            Invalidated?.Invoke(result.Key);
        }
        if (inFlight.Count > 0)
        {
            scratch.Clear();
            foreach (var pair in inFlight) if (now - pair.Value > 15000) scratch.Add(pair.Key);
            foreach (var key in scratch) inFlight.Remove(key);
        }
        // Coarse tiles first: they cover the view sooner. Sorted once per change of the wanted tiles.
        ordered ??= desired.OrderByDescending(k => k.Level).ThenBy(k => k.Layer).ToArray();
        int loads = 0;
        foreach (var key in ordered)
        {
            var e = Get(key);
            if (e == null) { if (loads++ >= 12) continue; e = Ensure(key); }
            if (e == null || e.Busy) continue;
            if (e.Missing && key.Layer == 0 && Info.CanPredict && inFlight.Count < 8 && !inFlight.ContainsKey(key) && now - e.RequestAt > 1500)
            {
                channel.SendPacket(new TileRequest { Session = Info.Session, View = View, Level = key.Level, X = key.X, Z = key.Z });
                inFlight[key] = now; e.RequestAt = now;
            }
            // A recording is encoded over its prediction: again when the prediction arrives or changes.
            // At most once a second while the recorder keeps changing it: each encoding copies the whole tile.
            if (key.Layer == 1 && e.Data != null && now - e.QueuedAt >= RedrawMs && (e.RenderedRevision != e.Revision || PredictionOf(key) is { } p && p != e.Prediction)) QueueEncode(key, e);
        }
        // Flush edits even while the map window is closed, at bounded throughput: two tiles a tick, each at most every
        // SaveMs (0.5.0-pre.2: the mid-distance view's recorder keeps many tiles changing, and every flush copies a tile);
        // up to eight while more than a quarter of the resident tiles wait (W2: SaveBacklog). Retry recordings whose file
        // could not be read.
        int unsaved = 0;
        scratch.Clear(); retries.Clear();
        foreach (var pair in entries)
        {
            var e = pair.Value;
            if (e.Dirty || e.Busy) unsaved++;
            if (e.Busy || now < e.RequestAt) continue;
            if (e.Dirty && scratch.Count < 8 && now - e.QueuedAt >= SaveMs) scratch.Add(pair.Key);
            else if (retries.Count < 2 && pair.Key.Layer == 1 && e.Data == null && e.RequestAt != 0) retries.Add(pair.Key);
        }
        SaveBacklog = unsaved > config.ResidentTiles / 4;
        for (int i = 0; i < scratch.Count && i < (SaveBacklog ? 8 : 2); i++) QueueEncode(scratch[i], entries[scratch[i]]);
        foreach (var key in retries)
            if (work.TryAdd(new(key, null, null, entries[key].Revision, false, Texels: desired.Contains(key)))) entries[key].Busy = true;
    }
    /// <summary>A recording shown in a view is encoded again at most this often while it changes, and saved at most this
    /// often otherwise (milliseconds). Its first encoding is at once.</summary>
    public const long RedrawMs = 1000, SaveMs = 5000;
    TileKey[] ordered;
    // Recordings' copies no longer used, for the next snapshot. Main thread.
    readonly Stack<TileData> spare = new();
    void Spare(TileData data) { if (spare.Count < 8) spare.Push(data); }
    readonly List<TileKey> scratch = new(), retries = new();
    /// <summary>Whether more than a quarter of the resident tiles wait to be saved: tiles that cannot be evicted, so the
    /// atlas and the recorder may find no room. Bulk changes (a read paper map) wait while it holds.</summary>
    public bool SaveBacklog { get; private set; }
    void QueueEncode(TileKey key, Entry e)
    {
        if (e.Data == null || e.Busy) return;
        // An evicted prediction leaves the texels it was last encoded over: they still hold.
        byte[] prediction = PredictionOf(key) ?? e.Prediction;
        var snapshot = spare.Count > 0 ? spare.Pop() : new TileData();
        e.Data.CopyTo(snapshot);
        bool texels = desired.Contains(key);
        if (work.TryAdd(new(key, snapshot, null, e.Revision, e.Dirty, prediction, texels)))
        {
            e.Busy = true; e.Dirty = false; e.QueuedAt = Environment.TickCount64;
            // Only a job that encodes texels encodes them over this prediction (the re-review of 14 Nov 2026: a save with the
            // map closed marked a recording as encoded over a prediction it never saw, and it was not encoded again).
            if (texels) e.Prediction = prediction;
        }
        else Spare(snapshot);
    }
    /// <summary>The texels of the prediction under a recording, while resident (they are replaced, never changed in place).</summary>
    byte[] PredictionOf(TileKey key) => entries.TryGetValue(new TileKey(0, key.Level, key.X, key.Z), out var p) ? p.Rgba : null;
    public void Receive(TileResponse r)
    {
        if (disposed || r.Session != Info.Session || r.View != View || r.Stream != 0) return;
        var key = new TileKey(0, r.Level, r.X, r.Z);
        if (!inFlight.Remove(key) || !Topology.Valid(key) || !entries.TryGetValue(key, out var e)) return;
        // A busy server asks for patience, not a long back-off: the tile is asked for again after the usual 1.5 s.
        if (r.Error == TileResponse.Busy) { e.RequestAt = Environment.TickCount64; return; }
        if (r.Error != null) { LastError = r.Error; e.RequestAt = Environment.TickCount64 + 10000; return; }
        if (r.Data == null || r.Data.Length > SemanticTile.MaxCompressedBytes) { LastError = "Invalid tile response"; return; }
        if (work.TryAdd(new(key, null, r.Data, e.Revision, true))) { e.Busy = true; ReceivedTiles++; }
    }
    /// <summary>Asks for the recordings a sample at x, z goes into (the levels whose cells keep it); true once all of
    /// them are loaded, so <see cref="Observe"/> keeps it.</summary>
    public bool Loaded(double x, double z, bool fine = false)
    {
        x = Topology.Canonical(x) - Topology.StartX;
        if (x < 0 || x >= Topology.Width || z < 0 || z >= Topology.Height) return true;
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        bool loaded = true;
        var (first, last) = RecordingRules.Levels(fine, Topology.MaxLevel);
        for (int level = first; level <= last; level++)
        {
            if (!RecordingRules.Keeps(level, bx, bz, Topology.Width, Topology.Height)) continue;
            double span = TileData.Size * (double)(1 << level);
            if (Ensure(new TileKey(1, level, (int)(x / span), (int)(z / span)))?.Data == null) loaded = false;
        }
        return loaded;
    }

    /// <summary>Whether a block of a cell recorded block by block exists in this 4 x 4-block cell (x, z its north-west
    /// block): the recorder records such a cell again, so a recording cannot outlast the change it showed. Null while
    /// that recording is still loading.</summary>
    public bool? HasFineRecording(int x, int z)
    {
        double rx = Topology.Canonical(x) - Topology.StartX;
        if (rx < 0 || rx >= Topology.Width || z < 0 || z >= Topology.Height) return false;
        var key = new TileKey(1, 0, (int)(rx / TileData.Size), z / TileData.Size);
        if (!observedKeys.ContainsKey(key)) return false;
        var e = Ensure(key);
        if (e?.Data == null) return null;
        int ix = 1 + (int)rx % TileData.Size, iz = 1 + z % TileData.Size;
        for (int dz = 0; dz < 4; dz++)
        for (int dx = 0; dx < 4; dx++)
        {
            int cx = ix + dx, cz = iz + dz;
            if (cx <= TileData.Size && cz <= TileData.Size && e.Data.Kinds[cz * TileData.Padded + cx] != TileData.Unknown) return true;
        }
        return false;
    }

    /// <param name="fine">A block of a cell recorded block by block (it differs from the prediction): it goes into the
    /// one- and two-block levels. Four-block samples go into the four-block level and the coarser ones.</param>
    /// <returns>False when a recording it belongs in was still loading: the sample was not kept and must be taken again.</returns>
    public bool Observe(double x, double z, MapSample sample, bool fine = false)
    {
        x = Topology.Canonical(x) - Topology.StartX;
        if (x < 0 || x >= Topology.Width || z < 0 || z >= Topology.Height) return true;
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z);
        bool kept = true;
        var (first, last) = RecordingRules.Levels(fine, Topology.MaxLevel);
        for (int level = first; level <= last; level++)
        {
            if (!RecordingRules.Keeps(level, bx, bz, Topology.Width, Topology.Height)) continue;
            double step = 1 << level, span = TileData.Size * step;
            var key = new TileKey(1, level, (int)(x / span), (int)(z / span));
            var e = Ensure(key);
            // Merge only after old disk data has arrived.
            if (e?.Data == null) { kept = false; continue; }
            int ix = 1 + (int)(x / step) % TileData.Size, iz = 1 + (int)(z / step) % TileData.Size, i = iz * TileData.Padded + ix;
            // Colour alone is no change: it varies (climate tints, seasons) without the terrain changing. A first colour or a
            // light coming on or going out is (the mid-distance view draws them; RecordedColor).
            // The first colour this format writes into a tile marks it (clearing an older recorder's colour field).
            if (RecordedColor.Has(sample.Color) && !RecordedColor.Current(e.Data)) { RecordedColor.Mark(e.Data); e.Revision++; e.Dirty = true; }
            if (e.Data.Heights[i] == sample.Height && e.Data.Depths[i] == sample.Depth && e.Data.Kinds[i] == sample.Kind
                && !RecordedColor.Changed(RecordedColor.Of(e.Data, i), sample.Color)) continue;
            e.Data.Set(i, sample);
            if (observedKeys.TryAdd(key, 0)) Interlocked.Increment(ref observationVersion);
            // Replicate edge pixels into the padding, avoiding dark filtering edges in sparse observations.
            if (ix == 1) e.Data.Set(i - 1, sample); if (ix == TileData.Size) e.Data.Set(i + 1, sample);
            if (iz == 1) e.Data.Set(i - TileData.Padded, sample); if (iz == TileData.Size) e.Data.Set(i + TileData.Padded, sample);
            e.Revision++; e.Dirty = true;
        }
        return kept;
    }
    public void CloseView() { desired.Clear(); View++; WantedVersion++; inFlight.Clear(); ordered = null; }

    /// <summary>Recording tiles on disk or in memory.</summary>
    public int RecordingCount => observedKeys.Count;

    /// <summary>Everything recorded, for a paper map (WM-P05): on the main thread, copies of the tiles in memory (newer than
    /// their files); the returned function, run on a worker, encodes them and reads the rest from disk, leaving out empty
    /// tiles and damaged files.</summary>
    public Func<List<(TileKey Key, byte[] Data)>> SnapshotRecordings()
    {
        var loaded = entries.Where(p => p.Key.Layer == 1 && p.Value.Data != null).ToDictionary(p => p.Key, p => p.Value.Data.Copy());
        var keys = observedKeys.Keys.Where(k => k.Layer == 1).ToArray();
        return () =>
        {
            var tiles = new List<(TileKey, byte[])>();
            foreach (var key in keys.Union(loaded.Keys))
            {
                byte[] data = null;
                if (loaded.TryGetValue(key, out var tile)) { if (tile.Any) data = TileCodec.Encode(tile); }
                else
                {
                    try
                    {
                        string path = PathFor(key);
                        if (new FileInfo(path).Length <= TileCodec.MaxCompressedBytes && TileCodec.Decode(data = File.ReadAllBytes(path)) is { Any: false }) data = null;
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException) { data = null; }
                }
                if (data != null) tiles.Add((key, data));
            }
            return tiles;
        };
    }

    /// <summary>Copies a paper map's recorded tile into this player's recordings where they have recorded nothing
    /// (<see cref="PaperMap.MergeCells"/>). False while the player's own tile is still loading: try again later.</summary>
    public bool MergeRecording(TileKey key, TileData map)
    {
        if (key.Layer != 1 || !Topology.Valid(key)) return true;
        var e = Ensure(key);
        if (e?.Data == null) return false;
        if (!PaperMap.MergeCells(e.Data, map)) return true;
        if (observedKeys.TryAdd(key, 0)) Interlocked.Increment(ref observationVersion);
        e.Revision++; e.Dirty = true;
        return true;
    }
    void Worker()
    {
        try
        {
            foreach (var w in work.GetConsumingEnumerable(stop.Token))
            {
                TileData data = w.Snapshot; byte[] rgba = null; bool missing = false, saveFailed = false, readFailed = false; string error = null;
                try
                {
                    string path = PathFor(w.Key);
                    if (w.Key.Layer == 0)
                    {
                        byte[] packed = w.Network;
                        if (packed == null && File.Exists(path))
                        {
                            try
                            {
                                if (new FileInfo(path).Length > SemanticTile.MaxCompressedBytes) throw new InvalidDataException("Oversized cache tile");
                                packed = File.ReadAllBytes(path); Interlocked.Increment(ref DiskHits);
                            }
                            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { packed = null; }
                        }
                        if (packed != null)
                        {
                            try { rgba = SemanticTile.Unpack(packed); }
                            catch (InvalidDataException) { rgba = null; packed = null; /* Cache is recoverable. */ }
                        }
                        missing = rgba == null;
                        if (w.Save && rgba != null)
                        {
                            try { TileCodec.WriteAtomic(path, packed); TileCache.Wrote(folder, packed.Length, config.DiskCacheMiB); }
                            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { error = "Cache write: " + e.Message; }
                        }
                        if (rgba != null && !SemanticTile.Any(rgba)) rgba = null;
                    }
                    else
                    {
                        if (data == null)
                        {
                            // A damaged recording starts empty. One that could not be read (locked, a disk error)
                            // must not: edits to an empty tile would be saved over the cells on disk.
                            // No File.Exists check: it also answers false when the file cannot be reached.
                            byte[] bytes = null;
                            try
                            {
                                if (new FileInfo(path).Length <= TileCodec.MaxCompressedBytes) bytes = File.ReadAllBytes(path);
                            }
                            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException) { }
                            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { readFailed = true; error = "Observation read: " + e.Message; }
                            // Decoding reads from memory: any failure here, a truncated stream included, is damage.
                            try { if (bytes != null) { data = TileCodec.Decode(bytes); Interlocked.Increment(ref DiskHits); } }
                            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException) { }
                        }
                        if (readFailed) { results.Add(new(w.Key, null, null, w.Revision, false, error, false, true), stop.Token); continue; }
                        // A new recording's colours are made here on the worker: the mid-distance recorder colours every
                        // recording, and its first colour would otherwise make the 67 KB array on the main thread.
                        if (data == null) { data = new TileData(); _ = data.Colors; }
                        if (w.Save)
                        {
                            try { TileCodec.WriteAtomic(path, TileCodec.Encode(data)); }
                            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { error = "Observation write: " + e.Message; saveFailed = true; }
                        }
                        rgba = w.Texels && data.Any ? SemanticTile.Encode(data, Info.SeaLevel, w.Prediction, Info.ChartHeightRange) : null;
                    }
                    results.Add(new(w.Key, data, rgba, w.Revision, missing, error, saveFailed, Texels: w.Key.Layer == 0 || w.Texels), stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { results.Add(new(w.Key, data ?? (w.Key.Layer == 1 ? new TileData() : null), null, w.Revision, w.Key.Layer == 0, e.Message, w.Key.Layer == 1 && w.Save), stop.Token); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Snapshot unsaved observations before cancelling the reader. No game state is touched by this final writer.
        var final = entries.Where(p => p.Key.Layer == 1 && p.Value.Data != null && (p.Value.Dirty || p.Value.Busy))
            .Select(p => (p.Key, Data: p.Value.Data.Copy())).ToArray();
        stop.Cancel(); work.CompleteAdding();
        Task flush = Task.WhenAll(worker, previousFlush).ContinueWith(_ =>
        {
            foreach (var p in final) { try { TileCodec.WriteAtomic(PathFor(p.Key), TileCodec.Encode(p.Data)); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } }
        }, TaskScheduler.Default);
        flushes[observations] = flush;
        _ = flush.ContinueWith(_ => flushes.TryRemove(new KeyValuePair<string, Task>(observations, flush)), TaskScheduler.Default);
        entries.Clear();
    }
}
