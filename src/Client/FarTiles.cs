using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// Tiles far terrain keeps around the camera, in toroidal texture atlases (<see cref="FarTileWindow"/>):
/// the near band (level-2 semantic and biome tiles out to <see cref="WmapConfig.NearBandDistance"/>) and the
/// climate (level-6 biome tiles out to the far terrain's horizon). Requested on their own stream, nearest
/// first, through a disk cache shared with the atlas. Render thread only, except the decode worker.
/// See docs/far-terrain-near-band.md.
/// </summary>
public sealed class FarTiles : IDisposable
{
    public const int BandLevel = 2, ClimateLevel = 6;
    /// <summary>Requests in flight per layer; the server allows 8 per player for both.</summary>
    public const int InFlightPerLayer = 4;
    /// <summary>Decoded tiles kept per layer, so moving back or crossing the seam needs no request.</summary>
    public const int CachedPerLayer = 160;

    public sealed class Layer
    {
        public readonly FarTileWindow Window;
        public readonly int Content;
        public readonly double Radius;
        /// <summary>GL textures: semantic (band only), biome, and the biome tiles' grass season (climate tiles only, 0.5.3-rc.5).
        /// 0 when unused.</summary>
        public int SemanticTexture, BiomeTexture, SeasonTexture;
        /// <summary>Per slot (x + 8·z): the unwrapped tile id and row it holds, as the shader checks them, the tile, and how far
        /// it has faded in since it arrived, so new detail does not pop. The recorded tiles' table (0.5.3, WM-0036: these
        /// layers kept their own copy of it).</summary>
        public readonly SlotTable Slots = new();
        internal readonly Dictionary<TileKey, Decoded> Cache = new();
        internal readonly Dictionary<TileKey, long> InFlight = new();
        internal readonly Dictionary<TileKey, long> RetryAt = new();
        internal readonly HashSet<TileKey> Loading = new();
        /// <summary>Tiles the disk cache did not have: asked from the server, not from disk again.</summary>
        internal readonly HashSet<TileKey> NotOnDisk = new();
        /// <summary>Tiles in their slots to ask for again (a survival client, where land became known since they came): their
        /// texels stand until the new tile replaces them in place, so nothing goes missing for a moment.</summary>
        internal readonly HashSet<TileKey> Stale = new();
        /// <summary>Stale tiles that became stale again while on their way: the answer, made before, is not the new one.</summary>
        internal readonly HashSet<TileKey> Again = new();
        /// <summary>Tiles the window wants, and how many of them are in their slots (as of the last update).</summary>
        public int Wanted, Ready;
        // The window's tiles as of the last move (see Update): kept while the camera stays within a few blocks.
        internal List<FarTileWindow.Cell> Cells;
        internal readonly HashSet<TileKey> WantedKeys = new();
        internal double CellsX = double.NaN, CellsZ, CellsOrigin, CellsInner;
        internal int CellsShift;
        public Layer(FarTileWindow window, int content, double radius)
        {
            Window = window; Content = content; Radius = radius;
        }
        public bool Semantic => TileContent.HasSemantic(Content);
    }
    // Replace: a tile asked for again while the old one stands in its slot: written over it in place, keeping its fade.
    internal sealed class Decoded { public byte[] Semantic, Biome, Season; public long Used; public bool Replace; }
    // Network: from the server (else from disk); Save: write it to the disk cache.
    sealed record Work(TileKey Key, Layer Layer, byte[] Semantic, byte[] Biome, bool Network, bool Save = false);
    sealed record Result(TileKey Key, Layer Layer, byte[] Semantic, byte[] Biome, bool Missing, string Error, bool Network);

    public readonly Layer Band, Climate;
    public readonly Topology Topology;
    /// <summary>The server's <see cref="AtlasInfo.ChartHeightRange"/>, for decoding the tiles' heights.</summary>
    public double HeightRange = Semantic.DefaultHeightRange;
    readonly Action<TileRequest> send;
    readonly string session, folder, biomeId;
    /// <summary>Seconds a newly arrived tile takes to fade in.</summary>
    public const double FadeSeconds = SlotTable.FadeMs / 1000.0;
    readonly int diskCacheMiB;
    readonly BlockingCollection<Work> work = new(64);
    readonly BlockingCollection<Result> results = new(64);
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    long clock;
    bool disposed;
    /// <summary>This instance's view, larger than any before it in this game: the server drops answers still on their way
    /// to an earlier instance (far terrain started again for another mode, whose tiles were cut to other land).</summary>
    public readonly int View = Interlocked.Increment(ref views);
    static int views;
    public string LastError;
    /// <summary>The far-terrain origin's lap in tile ids. Kept across seam crossings: crossing the seam moves the
    /// origin's position by a whole circumference but not this count, so resident tiles keep their ids and slots
    /// (when tiles per lap is not a multiple of 8, a new count would move them all). The shader adds it too.</summary>
    public int OriginLap => lap.Lap;
    readonly LapCounter lap = new();
    public long Received, DiskHits, Uploads;

    /// <param name="send">Sends a far-terrain tile request (Stream 1) to the server.</param>
    /// <param name="folder">The session's client cache folder (the atlas's), or null for no disk cache.</param>
    /// <param name="biomeId">The server's biome identity (<see cref="AtlasInfo.BiomeId"/>) for cache file names; null: no biome cache.</param>
    public FarTiles(Topology topology, string session, Action<TileRequest> send, string folder, int diskCacheMiB, double bandRadius, double climateRadius, bool band, bool climate, string biomeId)
    {
        Topology = topology; this.session = session; this.send = send; this.folder = folder; this.diskCacheMiB = diskCacheMiB;
        // A server-supplied name part: accept only the hex identity BiomeTile.Identity makes.
        this.biomeId = biomeId != null && System.Text.RegularExpressions.Regex.IsMatch(biomeId, "^[a-f0-9]{12}$") ? biomeId : null;
        if (band) Band = new Layer(new FarTileWindow(topology, BandLevel), TileContent.SemanticAndBiome, bandRadius);
        if (climate && topology.MaxLevel >= ClimateLevel)
        {
            // Level 6 tiles are 8192 blocks: a window to 16384 blocks fits the slots.
            var window = new FarTileWindow(topology, ClimateLevel);
            Climate = new Layer(window, TileContent.Biome, Math.Min(climateRadius, 16384));
        }
        if (Band != null && !Band.Window.Fits(Band.Radius)) Band = null;
        if (Climate != null && !Climate.Window.Fits(Climate.Radius)) Climate = null;
        layers = new[] { Band, Climate }.Where(l => l != null).ToArray();
        worker = Task.Run(Worker);
    }

    readonly Layer[] layers;
    public IReadOnlyList<Layer> Layers => layers;
    /// <summary>The camera moves this far (blocks) before the windows are worked out again. Far less than the band's
    /// 336-block fade at its edge, the fog at the climate's, and the 64-block margin of the inner edge.</summary>
    public const double Reselect = 16;
    long expireAt;
    /// <summary>Whether a tile of this world rectangle (blocks east of the world's start) may be asked for: a survival client
    /// asks only for tiles of land the server has sent it (0.5.4, WM-D63). Null: any.</summary>
    public Func<double, double, double, double, bool> Requestable;
    readonly List<TileKey> expired = new();

    /// <summary>The prediction at a world point from a decoded near-band tile (4-block texels), main thread: its stored
    /// height (<see cref="Semantic"/>) and whether it is water. False where no band tile is decoded.</summary>
    public bool Predicted(double x, double z, out double height, out bool water)
    {
        height = 0; water = false;
        if (Band == null || z < 0 || z >= Topology.Height) return false;
        double rx = Topology.Canonical(x) - Topology.StartX;
        if (rx < 0 || rx >= Topology.Width) return false;
        var key = new TileKey(0, BandLevel, (int)(rx / (TileData.Size << BandLevel)), (int)(z / (TileData.Size << BandLevel)));
        if (!Band.Cache.TryGetValue(key, out var decoded) || decoded.Semantic == null) return false;
        int ix = 1 + (int)(rx / (1 << BandLevel)) % TileData.Size, iz = 1 + (int)(z / (1 << BandLevel)) % TileData.Size;
        int o = (iz * TileData.Padded + ix) * 4;
        if (decoded.Semantic[o + 3] == 0) return false;
        height = Semantic.DecodeSigned(decoded.Semantic[o + 1], HeightRange);
        water = decoded.Semantic[o] < 128;
        return true;
    }

    /// <summary>Creates the atlas textures. Render thread, once. Leaves the GL state as it found it.</summary>
    public void CreateTextures()
    {
        using var unpack = new GlUnpack();
        var empty = new byte[FarTileWindow.AtlasTexels * FarTileWindow.AtlasTexels * 4];
        int Create()
        {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, FarTileWindow.AtlasTexels, FarTileWindow.AtlasTexels, 0, PixelFormat.Rgba, PixelType.UnsignedByte, empty);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
            return id;
        }
        foreach (var layer in Layers)
        {
            if (layer.Semantic && layer.SemanticTexture == 0) layer.SemanticTexture = Create();
            if (layer.BiomeTexture == 0) layer.BiomeTexture = Create();
            if (layer == Climate && layer.SeasonTexture == 0) layer.SeasonTexture = Create();
        }
    }

    /// <summary>Keeps the windows around the camera: fills slots from decoded tiles (at most
    /// <paramref name="uploads"/> per call), and requests what is missing. Render thread.</summary>
    /// <param name="originX">The far-terrain mesh origin's world x: slots are keyed in its lap.</param>
    /// <param name="inner">Far terrain starts this far from the camera: tiles wholly inside it are not needed.</param>
    public void Update(double cameraX, double cameraZ, double originX, double inner, int uploads = 2)
    {
        if (disposed) return;
        long now = Environment.TickCount64;
        foreach (var layer in layers) layer.Slots.UpdateFades(now);
        for (int i = 0; i < 16 && results.TryTake(out var r); i++) Take(r, now);
        lap.Update(Topology, originX);
        bool expire = now >= expireAt;
        if (expire) expireAt = now + 1000;
        foreach (var layer in layers)
        {
            var w = layer.Window;
            double originRel = w.Relative(originX), cameraRel = originRel + Topology.Delta(originX, cameraX);
            int shift = OriginLap * w.LapTiles;
            // The window again only when the camera moved, the mesh origin or the seam lap changed, or the inner edge moved:
            // standing still costs no lists, sorts or sets per frame.
            if (layer.Cells == null || layer.CellsShift != shift || layer.CellsOrigin != originX || Math.Abs(layer.CellsInner - inner) > 1
                || Math.Abs(Topology.Delta(layer.CellsX, cameraX)) > Reselect || Math.Abs(layer.CellsZ - cameraZ) > Reselect)
            {
                // Tiles whose farthest corner is inside the real terrain are never drawn.
                var cells = w.Window(cameraRel, cameraZ, layer.Radius);
                cells.RemoveAll(c => FarthestCorner(w, c, cameraRel, cameraZ) <= inner - 64);
                for (int i = 0; i < cells.Count; i++) cells[i] = cells[i] with { Id = cells[i].Id + shift };
                layer.Cells = cells;
                layer.WantedKeys.Clear();
                foreach (var c in cells) layer.WantedKeys.Add(c.Key);
                layer.CellsX = cameraX; layer.CellsZ = cameraZ; layer.CellsOrigin = originX; layer.CellsInner = inner; layer.CellsShift = shift;
            }
            layer.Wanted = layer.Cells.Count; layer.Ready = 0;
            var wanted = layer.WantedKeys;
            // Retries that are due are forgotten (0.5.3, WM-0042: the list only grew).
            if (expire && layer.RetryAt.Count > 0)
            {
                expired.Clear();
                foreach (var p in layer.RetryAt) if (now >= p.Value) expired.Add(p.Key);
                foreach (var k in expired) layer.RetryAt.Remove(k);
            }
            if (expire && layer.InFlight.Count > 0)
                foreach (var key in layer.InFlight.Where(p => now - p.Value > 15000).Select(p => p.Key).ToArray()) layer.InFlight.Remove(key);
            int requested = layer.InFlight.Count;
            var slots = layer.Slots;
            foreach (var cell in layer.Cells)
            {
                int slot = SlotTable.SlotOf(cell.Id, cell.Z);
                if (layer.Cache.TryGetValue(cell.Key, out var decoded)) decoded.Used = ++clock;
                if (slots.Keys[slot] == cell.Key)
                {
                    // In its slot, perhaps under the id of another lap: the texels stand, also when the decoded copy is gone.
                    if (!slots.Holds(cell)) slots.Rekey(cell);
                    layer.Ready++;
                    if (decoded?.Replace == true)
                    {
                        if (uploads <= 0) continue;
                        Upload(layer, slot, decoded);
                        decoded.Replace = false;
                        uploads--; Uploads++;
                    }
                    else if (layer.Stale.Contains(cell.Key) && !layer.InFlight.ContainsKey(cell.Key) && !layer.Loading.Contains(cell.Key) && requested < InFlightPerLayer
                        && !(layer.RetryAt.TryGetValue(cell.Key, out long due) && now < due))
                    {
                        // Stale until the new tile is taken: a request lost or refused is made again.
                        Request(layer, cell.Key, now); requested++;
                    }
                    continue;
                }
                if (decoded != null)
                {
                    if (uploads <= 0) continue;
                    Upload(layer, slot, decoded);
                    decoded.Replace = false;
                    slots.Place(cell, 0, now);
                    layer.Ready++;
                    uploads--; Uploads++;
                    continue;
                }
                if (layer.Loading.Contains(cell.Key) || layer.InFlight.ContainsKey(cell.Key)) continue;
                if (Requestable != null && !Requestable(cell.Key.X * cell.Key.Span, cell.Key.Z * cell.Key.Span, (cell.Key.X + 1) * cell.Key.Span, (cell.Key.Z + 1) * cell.Key.Span)) continue;
                if (layer.RetryAt.TryGetValue(cell.Key, out long retry) && now < retry) continue;
                // The disk cache first; the worker asks the network when it has nothing.
                if (folder != null && !layer.NotOnDisk.Contains(cell.Key))
                {
                    if (work.TryAdd(new(cell.Key, layer, null, null, false))) layer.Loading.Add(cell.Key);
                    continue;
                }
                if (requested >= InFlightPerLayer) continue;
                Request(layer, cell.Key, now); requested++;
            }
            // Keep the cache bounded; never drop what the window holds.
            if (layer.Cache.Count > CachedPerLayer)
                foreach (var key in layer.Cache.Where(p => !wanted.Contains(p.Key)).OrderBy(p => p.Value.Used).Take(layer.Cache.Count - CachedPerLayer).Select(p => p.Key).ToArray())
                    layer.Cache.Remove(key);
        }
    }

    /// <summary>Land became known in a world rectangle (blocks east of the world's start; a survival client, WM-D63): tiles
    /// there came blank where it was not, or not at all, and are asked for again. One in its slot stays drawn until the new
    /// one replaces it (<see cref="Layer.Stale"/>): the near band does not flicker as land is seen. Its coast distances next
    /// to that land (<see cref="RevealGate.CoastReach"/>) are not asked for again: far terrain uses them only within ten
    /// blocks of the shore.</summary>
    public void Refresh(double rel0, double z0, double rel1, double z1)
    {
        bool Overlaps(TileKey k)
        {
            double x0 = k.X * k.Span, x1 = x0 + k.Span, kz0 = k.Z * k.Span, kz1 = kz0 + k.Span;
            if (kz1 <= z0 || kz0 >= z1) return false;
            if (!Topology.Wraps) return x1 > rel0 && x0 < rel1;
            for (int lap = -1; lap <= 1; lap++)
                if (x1 + lap * Topology.Width > rel0 && x0 + lap * Topology.Width < rel1) return true;
            return false;
        }
        foreach (var layer in layers)
        {
            foreach (var key in layer.Cache.Keys.Where(Overlaps).ToArray()) layer.Cache.Remove(key);
            foreach (var key in layer.RetryAt.Keys.Where(Overlaps).ToArray()) layer.RetryAt.Remove(key);
            for (int s = 0; s < SlotTable.Count; s++)
                if (layer.Slots.Keys[s] is { } held && Overlaps(held)) layer.Stale.Add(held);
            // A tile on its way, in a slot or not yet, was cut to the land known when it was asked for: once it is placed it is
            // asked for again.
            foreach (var key in layer.InFlight.Keys.Concat(layer.Loading).Where(Overlaps)) { layer.Stale.Add(key); layer.Again.Add(key); }
            // Tiles that left their slots (and are not on their way) need no refresh: asked for again, they come new anyway.
            if (layer.Stale.Count > SlotTable.Count)
            {
                var keep = new HashSet<TileKey>(layer.InFlight.Keys.Concat(layer.Loading));
                for (int s = 0; s < SlotTable.Count; s++) if (layer.Slots.Keys[s] is { } k) keep.Add(k);
                layer.Stale.IntersectWith(keep);
                layer.Again.IntersectWith(keep);
            }
        }
    }

    static double FarthestCorner(FarTileWindow w, FarTileWindow.Cell c, double cameraRel, double cameraZ)
    {
        var (west, width) = w.Extent(c.Id);  // ids here are still relative to the origin's lap
        double dx = Math.Max(Math.Abs(west - cameraRel), Math.Abs(west + width - cameraRel)), dz = Math.Max(Math.Abs(c.Z * w.Span - cameraZ), Math.Abs((c.Z + 1) * w.Span - cameraZ));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    void Request(Layer layer, TileKey key, long now)
    {
        send(new TileRequest { Session = session, View = View, Level = key.Level, X = key.X, Z = key.Z, Stream = 1, Content = layer.Content });
        layer.InFlight[key] = now;
    }

    void Take(Result r, long now)
    {
        var layer = r.Layer;
        layer.Loading.Remove(r.Key);
        if (r.Error != null) LastError = r.Error;
        if (r.Missing)
        {
            // Not on disk (or damaged there): Update asks the server, nearest first.
            layer.NotOnDisk.Add(r.Key);
            // A server answer that does not decode: wait before asking again.
            if (r.Network) layer.RetryAt[r.Key] = now + 10000;
            if (layer.NotOnDisk.Count > 4 * CachedPerLayer) layer.NotOnDisk.Clear();
            return;
        }
        // A biome tile carries its grass season below the biome (BiomeTile format 102).
        byte[] biome = r.Biome, season = null;
        if (biome?.Length == 2 * BiomeTile.HalfBytes) { season = biome[BiomeTile.HalfBytes..]; biome = biome[..BiomeTile.HalfBytes]; }
        layer.Cache[r.Key] = new Decoded { Semantic = r.Semantic, Biome = biome, Season = season, Used = ++clock, Replace = true };
        // Fresh unless land became known while it was on its way: then it is drawn and asked for again.
        if (!layer.Again.Remove(r.Key)) layer.Stale.Remove(r.Key);
    }

    static readonly byte[] Blank = new byte[FarTileWindow.SlotTexels * FarTileWindow.SlotTexels * 4];

    /// <summary>Writes a tile into its slot. Leaves the GL state as it found it: the bound texture, the unpack
    /// alignment and buffer, and the row length.</summary>
    static void Upload(Layer layer, int slot, Decoded decoded)
    {
        int x = slot % FarTileWindow.Slots * FarTileWindow.SlotTexels, z = slot / FarTileWindow.Slots * FarTileWindow.SlotTexels;
        using var unpack = new GlUnpack();
        void Put(int texture, byte[] rgba)
        {
            if (texture == 0) return;
            GL.BindTexture(TextureTarget.Texture2D, texture);
            // A missing part (no biome from this server) is written blank: unknown, not the previous tile's.
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, z, FarTileWindow.SlotTexels, FarTileWindow.SlotTexels, PixelFormat.Rgba, PixelType.UnsignedByte, rgba ?? Blank);
        }
        Put(layer.SemanticTexture, decoded.Semantic);
        Put(layer.BiomeTexture, decoded.Biome);
        Put(layer.SeasonTexture, decoded.Season);
    }

    /// <summary>A far-terrain response from the server. Main thread.</summary>
    public void Receive(TileResponse r)
    {
        if (disposed || r.Session != session || r.View != View || r.Stream != 1) return;
        var layer = layers.FirstOrDefault(l => l.Window.Level == r.Level && l.Content == r.Content);
        if (layer == null) return;
        var key = new TileKey(0, r.Level, r.X, r.Z);
        if (!layer.InFlight.Remove(key) || !Topology.Valid(key)) return;
        // Land this player has not seen (WM-D63): asked again once some of it is known (Refresh), or after a while.
        if (r.Error == TileResponse.Unexplored) { layer.RetryAt[key] = Environment.TickCount64 + 30000; return; }
        if (r.Error != null || (layer.Semantic && r.Data == null) || r.Data?.Length > SemanticTile.MaxCompressedBytes || r.Biome?.Length > BiomeTile.MaxCompressedBytes)
        {
            LastError = r.Error ?? "Invalid far-terrain tile";
            layer.RetryAt[key] = Environment.TickCount64 + 10000;
            return;
        }
        // A tile cut to the land this player has seen is kept in memory only (WM-D63): never in the disk cache.
        if (work.TryAdd(new(key, layer, r.Data, r.Biome, true, !r.Gated))) { layer.Loading.Add(key); Received++; }
    }

    void Worker()
    {
        try
        {
            foreach (var w in work.GetConsumingEnumerable(stop.Token))
            {
                byte[] semantic = null, biome = null; string error = null; bool missing = false;
                try
                {
                    string semanticPath = folder == null ? null : Path.Combine(folder, w.Key.FileName), biomePath = folder == null || biomeId == null ? null : Path.Combine(folder, BiomeTile.FileName(w.Key, biomeId));
                    byte[] packedSemantic = w.Semantic, packedBiome = w.Biome;
                    if (!w.Network)
                    {
                        packedSemantic = w.Layer.Semantic ? Read(semanticPath, SemanticTile.MaxCompressedBytes) : null;
                        packedBiome = Read(biomePath, BiomeTile.MaxCompressedBytes);
                    }
                    try { if (packedSemantic != null) semantic = SemanticTile.Unpack(packedSemantic); } catch (InvalidDataException) { packedSemantic = null; }
                    try { if (packedBiome != null) biome = BiomeTile.Unpack(packedBiome); } catch (InvalidDataException) { packedBiome = null; }
                    // From disk, both parts must be there (a server without biome sends none: then the semantic part alone).
                    // A server without biome tiles (no biome identity) has semantic tiles alone, on disk too.
                    missing = (w.Layer.Semantic && semantic == null) || (!w.Network && biomeId != null && biome == null) || (!w.Layer.Semantic && biome == null);
                    if (w.Save && !missing && folder != null)
                    {
                        try
                        {
                            if (packedSemantic != null) TileCodec.WriteAtomic(semanticPath, packedSemantic);
                            if (packedBiome != null && biomePath != null) TileCodec.WriteAtomic(biomePath, packedBiome);
                            TileCache.Wrote(folder, (packedSemantic?.Length ?? 0) + (biomePath != null ? packedBiome?.Length ?? 0 : 0), diskCacheMiB);
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { error = "Far terrain cache write: " + e.Message; }
                    }
                    if (!w.Network && !missing) Interlocked.Increment(ref DiskHits);
                }
                catch (Exception e) when (e is not OperationCanceledException) { error = e.Message; missing = true; }
                results.Add(new(w.Key, w.Layer, semantic, biome, missing, error, w.Network), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    static byte[] Read(string path, int maxBytes)
    {
        if (path == null) return null;
        try { return new FileInfo(path).Length <= maxBytes ? File.ReadAllBytes(path) : null; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel(); work.CompleteAdding();
        foreach (var layer in Layers)
        {
            if (layer.SemanticTexture != 0) GL.DeleteTexture(layer.SemanticTexture);
            if (layer.BiomeTexture != 0) GL.DeleteTexture(layer.BiomeTexture);
            if (layer.SeasonTexture != 0) GL.DeleteTexture(layer.SeasonTexture);
            layer.SemanticTexture = layer.BiomeTexture = layer.SeasonTexture = 0;
        }
    }
}
