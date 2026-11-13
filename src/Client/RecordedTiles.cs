using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// The player's recordings around the camera for the mid-distance view (WM-P06 prototype; docs/mid-distance-view.md):
/// four-block tiles (level 2) out to the near band's edge and two-block tiles (level 1) out to the terraces' edge, in two
/// 8 × 8-slot texture atlases side by side, addressed like the near band (<see cref="FarTileWindow"/>, laps kept across the
/// seam). From the same tiles, on a worker: boxes for every two-block recording out to the band's edge (structures past the
/// terraces) and merged lights (<see cref="LightPoints"/>) out to <see cref="WmapConfig.LightDistance"/>. A recording in
/// memory (newer than its file) is taken from the tile client; others are read from disk. Render thread, except the worker.
/// </summary>
public sealed class RecordedTiles : IDisposable
{
    public const int CoarseLevel = 2, FineLevel = 1;
    public const int AtlasWidth = 2 * FarTileWindow.AtlasTexels, AtlasHeight = FarTileWindow.AtlasTexels;
    /// <summary>Floats per box: west edge (blocks east of the ring's start), north edge, top (world y), size, colour (RGBA bytes).</summary>
    public const int BoxFloats = 5;
    /// <summary>Floats per light: blocks east of the ring's start, world y, z, brightness.</summary>
    public const int LightFloats = 4;
    /// <summary>Boxes drawn at most, nearest tiles first.</summary>
    public const int MaxBoxes = 1 << 17;

    /// <summary>Where recordings come from: the tile client's, or a test's.</summary>
    public interface ISource
    {
        /// <summary>Whether a recording tile exists (in memory or on disk).</summary>
        bool Has(TileKey key);
        /// <summary>Changes whenever a recording tile appears (<see cref="TileClient.ObservationVersion"/>): the windows are
        /// worked out again then, also while the camera stands still.</summary>
        int Version { get; }
        /// <summary>The recording in memory, if any, and a number that changes whenever it does. Must not count as a use.</summary>
        TileData Resident(TileKey key, out long version);
        /// <summary>Its file, or null.</summary>
        string PathFor(TileKey key);
    }

    sealed class Derived
    {
        public long Version, DerivedAt;
        public Half[] Heights;
        public byte[] Rgba;
        public float[] Boxes;
        public LightPoint[] Lights;
    }
    sealed record Work(TileKey Key, TileData Data, string Path, long Version, bool Texels);
    sealed record Result(TileKey Key, Derived Derived, string Error);

    public readonly Topology Topology;
    readonly ISource source;
    public readonly FarTileWindow Coarse, Fine;
    public readonly SlotTable CoarseSlots = new(), FineSlots = new();
    readonly LapCounter laps = new();
    /// <summary>The far-terrain origin's lap in tile ids, kept across seam crossings (shader: wmapRec.z).</summary>
    public int Lap => laps.Lap;
    public int HeightTexture, ColorTexture, SlotTexture;
    readonly Dictionary<TileKey, Derived> derived = new();
    readonly HashSet<TileKey> loading = new();
    readonly BlockingCollection<Work> work = new(256);
    readonly BlockingCollection<Result> results = new(256);
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    bool disposed;
    public string LastError;

    // The windows as of the last move, and lists reused to work them out.
    readonly List<FarTileWindow.Cell> coarseCells = new(), fineCells = new(), nearest = new();
    readonly List<TileKey> tracked = new(), dropped = new();
    readonly HashSet<TileKey> texelKeys = new(), trackedSet = new();
    double cellsX = double.NaN, cellsZ, cellsOrigin;
    int cellsShift = int.MinValue, sourceVersion = int.MinValue;
    long versionAt;
    /// <summary>A new recording tile makes the windows be worked out again at most this often (milliseconds; 0.5.0-pre.4:
    /// the recorder makes tiles while the player walks, and each time all windows were worked out again).</summary>
    public const long VersionReselectMs = 1000;
    /// <summary>How often the windows have been worked out.</summary>
    public int Reselections;
    static readonly Comparison<FarTileWindow.Cell> ByDistance = (a, b) => a.Distance.CompareTo(b.Distance);
    (double Coarse, double Fine, double Boxes, double Lights) radii;
    long checkAt;
    bool boxesDirty, lightsDirty;

    /// <summary>Boxes (<see cref="BoxFloats"/> each) and lights (<see cref="LightFloats"/> each) around the camera, the first
    /// <see cref="BoxCount"/> and <see cref="LightCount"/> of them (the arrays are reused and may be longer); their versions
    /// change when they do.</summary>
    public float[] BoxData = Array.Empty<float>(), LightData = Array.Empty<float>();
    public int BoxCount, LightCount;
    public long BoxVersion, LightVersion;
    public long Uploads, DiskReads;
    /// <summary>Recorded tiles in the four-block and two-block windows, and how many are in their slots.</summary>
    public int CoarseWanted, CoarseReady, FineWanted, FineReady;

    public RecordedTiles(Topology topology, ISource source)
    {
        Topology = topology; this.source = source;
        Coarse = new FarTileWindow(topology, CoarseLevel);
        Fine = new FarTileWindow(topology, FineLevel);
        worker = Task.Run(Worker);
    }

    /// <summary>Creates the atlases and the slot table. Render thread, once. Leaves the GL state as it found it.</summary>
    public void CreateTextures()
    {
        using var unpack = new GlUnpack();
        int Create(PixelInternalFormat format, int width, int height, PixelFormat layout, PixelType type, Array empty, bool linear)
        {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            // The atlases start undefined: a slot is read only once the slot table names the tile it holds (0.5.0-pre.2: filling
            // 25 MiB with zeros made the first frame take 30 ms).
            if (empty is float[] f) GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, layout, type, f);
            else GL.TexImage2D(TextureTarget.Texture2D, 0, format, width, height, 0, layout, type, IntPtr.Zero);
            var filter = linear ? (int)TextureMinFilter.Linear : (int)TextureMinFilter.Nearest;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return id;
        }
        if (HeightTexture == 0) HeightTexture = Create(PixelInternalFormat.Rgba16f, AtlasWidth, AtlasHeight, PixelFormat.Rgba, PixelType.HalfFloat, null, true);
        if (ColorTexture == 0) ColorTexture = Create(PixelInternalFormat.Rgba8, AtlasWidth, AtlasHeight, PixelFormat.Rgba, PixelType.UnsignedByte, null, true);
        if (SlotTexture == 0) SlotTexture = Create(PixelInternalFormat.Rgba32f, 16, 8, PixelFormat.Rgba, PixelType.Float, SlotTexels(), false);
    }

    /// <summary>GPU bytes of the atlases.</summary>
    public static long TextureBytes => (long)AtlasWidth * AtlasHeight * (8 + 4) + 16 * 8 * 16;

    readonly float[] slotTexels = new float[16 * 8 * 4];
    float[] SlotTexels()
    {
        for (int level = 0; level < 2; level++)
        {
            var slots = level == 0 ? CoarseSlots : FineSlots;
            for (int s = 0; s < SlotTable.Count; s++)
            {
                int x = s % FarTileWindow.Slots + 8 * level, z = s / FarTileWindow.Slots, o = (z * 16 + x) * 4;
                slotTexels[o] = slots.Ids[s]; slotTexels[o + 1] = slots.Rows[s]; slotTexels[o + 2] = slots.Fade[s]; slotTexels[o + 3] = 0;
            }
        }
        return slotTexels;
    }

    /// <summary>Keeps the windows around the camera: takes worker results, places at most <paramref name="uploads"/> tiles,
    /// checks every half second whether recordings in memory changed, and rebuilds the boxes and lights when they did.
    /// A radius of 0 turns that part off. Render thread.</summary>
    /// <param name="originX">The far-terrain mesh origin's world x: slots are keyed in its lap.</param>
    public void Update(double cameraX, double cameraZ, double originX, double coarseRadius, double fineRadius, double boxRadius, double lightRadius, int uploads = 2)
    {
        if (disposed) return;
        long now = Environment.TickCount64;
        laps.Update(Topology, originX);
        for (int i = 0; i < 32 && results.TryTake(out var r); i++)
        {
            loading.Remove(r.Key);
            if (r.Error != null) LastError = r.Error;
            if (r.Derived == null) continue;
            // The texels it replaces go back to the pool: nothing else holds them (uploads read them on this thread).
            if (derived.TryGetValue(r.Key, out var old) && old.Heights != null && old.Heights != r.Derived.Heights) texelPool.Add((old.Heights, old.Rgba));
            r.Derived.DerivedAt = now;
            derived[r.Key] = r.Derived;
            if (r.Key.Level == FineLevel) boxesDirty = true; else lightsDirty = true;
        }
        if (!Fine.Fits(fineRadius)) fineRadius = 0;
        if (!Coarse.Fits(coarseRadius)) coarseRadius = 0;
        var wanted = (coarseRadius, fineRadius, boxRadius, lightRadius);
        int shift = Lap * Coarse.LapTiles, fineShift = Lap * Fine.LapTiles;
        // Also when recordings appeared (0.5.0-pre.3): the index of recordings on disk loads after the first frames, and the
        // recorder makes new tiles while the player stands still; that alone at most every VersionReselectMs (0.5.0-pre.4).
        bool moved = wanted != radii || cellsShift != shift || cellsOrigin != originX || double.IsNaN(cellsX)
            || Math.Abs(Topology.Delta(cellsX, cameraX)) > FarTiles.Reselect || Math.Abs(cellsZ - cameraZ) > FarTiles.Reselect;
        bool appeared = sourceVersion != source.Version && now >= versionAt;
        if (moved || appeared)
        {
            if (!moved) versionAt = now + VersionReselectMs;
            Reselections++;
            radii = wanted; cellsShift = shift; cellsOrigin = originX; cellsX = cameraX; cellsZ = cameraZ; sourceVersion = source.Version;
            double originRel = Coarse.Relative(originX), cameraRel = originRel + Topology.Delta(originX, cameraX);
            void Recorded(List<FarTileWindow.Cell> into, FarTileWindow w, double radius, int lapShift)
            {
                if (radius <= 0) return;
                foreach (var c in w.Window(cameraRel, cameraZ, radius))
                {
                    var cell = c with { Id = c.Id + lapShift, Key = c.Key with { Layer = 1 } };
                    if (source.Has(cell.Key)) into.Add(cell);
                }
            }
            coarseCells.Clear(); fineCells.Clear();
            Recorded(coarseCells, Coarse, coarseRadius, shift);
            Recorded(fineCells, Fine, fineRadius, fineShift);
            texelKeys.Clear();
            foreach (var c in coarseCells) texelKeys.Add(c.Key);
            foreach (var c in fineCells) texelKeys.Add(c.Key);
            tracked.Clear(); trackedSet.Clear();
            // Nearest first, so the nearest boxes and lights come first when there are too many.
            nearest.Clear();
            Recorded(nearest, Fine, Math.Min(boxRadius, 1536), fineShift);
            Recorded(nearest, Coarse, Math.Max(coarseRadius, Math.Min(lightRadius, 3584)), shift);
            nearest.Sort(ByDistance);
            foreach (var c in nearest) if (trackedSet.Add(c.Key)) tracked.Add(c.Key);
            foreach (var key in texelKeys) if (trackedSet.Add(key)) tracked.Add(key);
            dropped.Clear();
            foreach (var key in derived.Keys) if (!trackedSet.Contains(key)) dropped.Add(key);
            foreach (var key in dropped) { derived.Remove(key); boxesDirty = lightsDirty = true; }
            checkAt = 0;
        }
        LastCopies = 0;
        if (now >= checkAt)
        {
            checkAt = now + 500;
            foreach (var key in tracked)
            {
                if (loading.Contains(key)) continue;
                bool texels = texelKeys.Contains(key);
                derived.TryGetValue(key, out var d);
                var data = source.Resident(key, out long version);
                // A recording the recorder keeps changing is taken again at most every RederiveMs: each time copies the tile.
                bool stale = d == null || (texels && d.Heights == null) || (data != null && d.Version != version && now - d.DerivedAt >= RederiveMs);
                if (!stale) continue;
                // In memory: a copy now (the recorder keeps changing it), at most CopiesPerFrame a frame, the rest next frame
                // (0.5.0-pre.3: a hundred at once allocated 15 MiB in one frame); otherwise its file, read on the worker.
                if (data != null && LastCopies >= CopiesPerFrame) { checkAt = now; continue; }
                var item = data != null ? new Work(key, Copy(data), null, version, texels) : new Work(key, null, source.PathFor(key), d?.Version ?? 0, texels);
                if (!work.TryAdd(item)) { if (item.Data != null) tilePool.Add(item.Data); checkAt = now; break; }
                if (data != null) LastCopies++;
                loading.Add(key);
            }
        }
        CoarseSlots.UpdateFades(now); FineSlots.UpdateFades(now);
        (CoarseWanted, CoarseReady) = Place(CoarseSlots, coarseCells, 0, now, ref uploads);
        (FineWanted, FineReady) = Place(FineSlots, fineCells, 1, now, ref uploads);
        if (SlotTexture != 0)
        {
            using var unpack = new GlUnpack();
            GL.BindTexture(TextureTarget.Texture2D, SlotTexture);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 16, 8, PixelFormat.Rgba, PixelType.Float, SlotTexels());
        }
        if (boxesDirty) RebuildBoxes();
        if (lightsDirty) RebuildLights();
    }

    (int Wanted, int Ready) Place(SlotTable slots, List<FarTileWindow.Cell> cells, int level, long now, ref int uploads)
    {
        int ready = 0;
        foreach (var cell in cells)
        {
            if (!derived.TryGetValue(cell.Key, out var d) || d.Heights == null) continue;
            int s = SlotTable.SlotOf(cell.Id, cell.Z);
            if (slots.Keys[s] == cell.Key && slots.Versions[s] == d.Version)
            {
                if (!slots.Holds(cell)) slots.Rekey(cell);
                ready++;
                continue;
            }
            if (uploads <= 0) continue;
            Upload(s, level, d);
            slots.Place(cell, d.Version, now);
            uploads--; Uploads++; ready++;
        }
        return (cells.Count, ready);
    }

    void Upload(int slot, int level, Derived d)
    {
        if (HeightTexture == 0) return;
        int x = slot % FarTileWindow.Slots * FarTileWindow.SlotTexels + level * FarTileWindow.AtlasTexels, z = slot / FarTileWindow.Slots * FarTileWindow.SlotTexels;
        using var unpack = new GlUnpack();
        GL.BindTexture(TextureTarget.Texture2D, HeightTexture);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, z, FarTileWindow.SlotTexels, FarTileWindow.SlotTexels, PixelFormat.Rgba, PixelType.HalfFloat, d.Heights);
        GL.BindTexture(TextureTarget.Texture2D, ColorTexture);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, z, FarTileWindow.SlotTexels, FarTileWindow.SlotTexels, PixelFormat.Rgba, PixelType.UnsignedByte, d.Rgba);
    }

    // The boxes and lights are put together again into the same arrays (0.5.0-pre.4: a new list and array each time a
    // recording nearby was taken again, every few seconds while the recorder works, up to 2.6 MB each, were most of what far
    // terrain allocated in play). An array grows only, by half again.
    static float[] Room(float[] data, int floats) => data.Length >= floats ? data : new float[Math.Max(floats, data.Length + data.Length / 2)];

    void RebuildBoxes()
    {
        boxesDirty = false;
        int n = 0;
        foreach (var key in tracked)
        {
            if (key.Level != FineLevel || !derived.TryGetValue(key, out var d) || d.Boxes == null) continue;
            if (n / BoxFloats + d.Boxes.Length / BoxFloats > MaxBoxes) break;
            n += d.Boxes.Length;
        }
        BoxData = Room(BoxData, n);
        int at = 0;
        foreach (var key in tracked)
        {
            if (at >= n) break;
            if (key.Level != FineLevel || !derived.TryGetValue(key, out var d) || d.Boxes == null) continue;
            Array.Copy(d.Boxes, 0, BoxData, at, d.Boxes.Length);
            at += d.Boxes.Length;
        }
        BoxCount = n / BoxFloats; BoxVersion++;
    }

    void RebuildLights()
    {
        lightsDirty = false;
        int n = 0;
        foreach (var key in tracked)
            if (key.Level == CoarseLevel && derived.TryGetValue(key, out var d) && d.Lights != null) n += d.Lights.Length * LightFloats;
        LightData = Room(LightData, n);
        int at = 0;
        foreach (var key in tracked)
        {
            if (key.Level != CoarseLevel || !derived.TryGetValue(key, out var d) || d.Lights == null) continue;
            foreach (var p in d.Lights) { LightData[at++] = (float)p.X; LightData[at++] = (float)p.Y; LightData[at++] = (float)p.Z; LightData[at++] = p.Brightness; }
        }
        LightCount = n / LightFloats; LightVersion++;
    }

    void Worker()
    {
        try
        {
            foreach (var w in work.GetConsumingEnumerable(stop.Token))
            {
                Derived d = null; string error = null;
                try
                {
                    var data = w.Data;
                    if (data == null && w.Path != null)
                    {
                        try
                        {
                            if (new FileInfo(w.Path).Length <= TileCodec.MaxCompressedBytes) { data = TileCodec.Decode(File.ReadAllBytes(w.Path)); Interlocked.Increment(ref DiskReads); }
                        }
                        catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException) { }
                    }
                    d = Derive(w.Key, data ?? new TileData(), w.Version, w.Texels, texelPool);
                    if (w.Data != null) tilePool.Add(w.Data);
                }
                catch (Exception e) when (e is not OperationCanceledException) { error = "Recorded tile " + w.Key.FileName + ": " + e.Message; d = new Derived { Version = w.Version }; }
                results.Add(new(w.Key, d, error), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>What the mid-distance view draws from one recording tile: its texels (when its window wants them), its boxes
    /// (two-block tiles: cells with structure evidence) and its lights (four-block tiles).</summary>
    static Derived Derive(TileKey key, TileData data, long version, bool texels, ConcurrentBag<(Half[], byte[])> pool)
    {
        var d = new Derived { Version = version };
        if (texels)
        {
            if (pool.TryTake(out var reuse)) (d.Heights, d.Rgba) = reuse;
            else { d.Heights = new Half[TileData.Count * RecordedTexels.Halves]; d.Rgba = new byte[TileData.Count * 4]; }
            RecordedTexels.Build(data, d.Heights, d.Rgba);
        }
        if (key.Level == FineLevel)
        {
            var boxes = new List<float>();
            double step = key.Step;
            for (int iz = 1; iz <= TileData.Size; iz++)
            for (int ix = 1; ix <= TileData.Size; ix++)
            {
                int i = iz * TileData.Padded + ix;
                byte kind = data.Kinds[i];
                if (kind == TileData.Unknown || Semantic.IsWater(kind)) continue;
                int c = RecordedColor.Of(data, i);
                // Fine recording means a prediction mismatch, not a building: coast cliffs, glaciers and edited ground
                // also get fine cells. Only the recorder's worldgen comparison can authorize a structure box.
                if (!RecordedColor.IsStructure(c)) continue;
                int rgba = RecordedColor.Has(c) ? (c & 0xFFFFFF) | unchecked((int)0xFF000000) : unchecked((int)0xFF808080);
                boxes.Add((float)((key.X * TileData.Size + ix - 1) * step)); boxes.Add((float)((key.Z * TileData.Size + iz - 1) * step));
                boxes.Add((float)RecordedTexels.Top(data.Heights[i], data.Depths[i], kind)); boxes.Add((float)step);
                boxes.Add(BitConverter.Int32BitsToSingle(rgba));
            }
            d.Boxes = boxes.ToArray();
        }
        else d.Lights = LightPoints.Merge(LightPoints.Of(data, key)).ToArray();
        return d;
    }

    /// <summary>A recording in memory that keeps changing is taken again at most this often (milliseconds).</summary>
    public const long RederiveMs = 10_000;
    /// <summary>Recordings in memory copied for the worker per frame at most, and how many the last update copied.</summary>
    public const int CopiesPerFrame = 4;
    public int LastCopies;
    // Reused buffers (0.5.0-pre.2): tile copies for the worker, and texels replaced by newer ones.
    readonly ConcurrentBag<TileData> tilePool = new();
    readonly ConcurrentBag<(Half[], byte[])> texelPool = new();
    TileData Copy(TileData data)
    {
        if (!tilePool.TryTake(out var copy)) return data.Copy();
        data.CopyTo(copy);
        return copy;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); work.CompleteAdding();
        if (HeightTexture != 0) GL.DeleteTexture(HeightTexture);
        if (ColorTexture != 0) GL.DeleteTexture(ColorTexture);
        if (SlotTexture != 0) GL.DeleteTexture(SlotTexture);
        HeightTexture = ColorTexture = SlotTexture = 0;
    }
}

/// <summary>The tile client's recordings for <see cref="RecordedTiles"/>: those in memory are newer than their files.</summary>
public sealed class TileClientRecordings : RecordedTiles.ISource
{
    readonly TileClient tiles;
    public TileClientRecordings(TileClient tiles) { this.tiles = tiles; }
    public bool Has(TileKey key) => tiles.HasObservation(key);
    public int Version => tiles.ObservationVersion;
    public TileData Resident(TileKey key, out long version)
    {
        var e = tiles.Peek(key);
        version = e == null ? 0 : ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e) << 32) | (uint)e.Revision;
        return e?.Data;
    }
    public string PathFor(TileKey key) => tiles.RecordingPath(key);
}
