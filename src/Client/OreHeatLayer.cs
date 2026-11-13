using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>
/// The full map's ore heat map (WM-D47, tiled in WM-D48): at every zoom the view shows the chosen ore's tiles at the level
/// whose cells are at least <see cref="OreHeat.MinCellPixels"/> on screen. Tiles it has are drawn at once; a missing tile
/// is drawn from the nearest coarser one it has until it arrives, and the server is asked for the missing tiles (a coarse
/// one first where nothing covers them) once the view has settled. Tiles of every ore asked for are kept, so panning,
/// zooming back and switching ores redraw from memory. Only offered when the server sends ore codes (players with the
/// full map) and only drawn on the full map, never on the game chart.
/// </summary>
public sealed class OreHeatLayer : IDisposable
{
    readonly ICoreClientAPI api;
    readonly Action<OreHeatRequest> send;
    readonly Func<Topology> topology;
    sealed class Tile
    {
        public float[] Blocks, Factors;
        public LoadedTexture Texture;
        public bool Partial;
        /// <summary>Drawn from the coarser tile at level <see cref="From"/> (received at <see cref="FromAt"/>) until this one arrives.</summary>
        public bool Derived;
        public int From;
        public long FromAt, At, Used;
    }
    readonly record struct Key(string Code, int Level, int X, int Z);
    readonly Dictionary<Key, Tile> tiles = new();
    // Tiles asked for and not yet answered, by when they were first asked.
    readonly Dictionary<Key, long> asked = new();
    List<TileInstance> visible = new();
    int level = -1, viewHash, lastMillis;
    long viewSince, sentAt;
    /// <summary>An answer not back after this long (dropped, or replaced on the server by a newer view) is asked for again.</summary>
    public const int AnswerMillis = 30000;
    /// <summary>A tile with cells the server could not answer yet (vanilla regions not loaded) is asked for again after this long.</summary>
    public const int PartialMillis = 20000;
    /// <summary>Milliseconds the view has to stay put before missing tiles are asked for (and between requests).</summary>
    public const int SettleMillis = 150;
    /// <summary>Tiles kept (with their textures, 32 × 32 texels each); the least recently drawn go first.</summary>
    public const int MaxTiles = 1024;
    /// <summary>Stand-in tiles worked out from coarser ones per frame, so a big zoom does not stall one frame.</summary>
    const int DerivedPerFrame = 24;
    /// <summary>The ore drawn: "" for none, <see cref="OreHeat.Listed"/>, or an ore code.</summary>
    public string Code { get; private set; } = "";
    /// <summary>The level the view shows (-1 before the first view).</summary>
    public int Level => level;
    /// <summary>The coarse tiles the server stores ahead of time, and how many it has, from its latest answer.</summary>
    public int Stored { get; private set; }
    public int StoredOf { get; private set; }
    /// <summary>Tiles kept now, for the smoke checks.</summary>
    public int Count => tiles.Count;

    public OreHeatLayer(ICoreClientAPI api, Action<OreHeatRequest> send, Func<Topology> topology)
    {
        this.api = api; this.send = send; this.topology = topology;
    }

    public static string Name(string code) => string.IsNullOrEmpty(code) ? "off" : code == OreHeat.Listed ? "ores the pick lists" : Lang.Get("ore-" + code);

    public void Select(string code)
    {
        code ??= "";
        if (code == Code) return;
        Code = code;
        // A settled view asks for the new ore's tiles at once.
        sentAt = 0;
        asked.Clear();
    }

    /// <summary>A line for the atlas's status: how much of the view is drawn at its own cells, and what the server is doing.</summary>
    public string Status
    {
        get
        {
            if (Code.Length == 0 || level < 0) return "";
            int have = 0;
            foreach (var v in visible) if (tiles.TryGetValue(new Key(Code, level, v.Key.X, v.Key.Z), out var tile) && !tile.Derived) have++;
            string cells = string.Format(CultureInfo.InvariantCulture, "{0:0} blocks", OreHeat.CellSize(level));
            string status = have < visible.Count
                ? $"Ore heat map: {Name(Code)}, {have} of {visible.Count} tiles of {cells} cells, working out the rest…"
                : $"Ore heat map: {Name(Code)}, cells of {cells}" + (lastMillis > 0 ? string.Format(CultureInfo.InvariantCulture, " (last tile {0:0.0} s)", lastMillis / 1000.0) : "");
            if (StoredOf > 0 && Stored < StoredOf) status += $" · storing coarse tiles {Stored} of {StoredOf}";
            return status;
        }
    }

    /// <summary>Every frame the full map is drawn, with the view in world blocks (on the camera's copy of the ring): picks
    /// the level and the tiles shown, and once the view has stayed the same for <see cref="SettleMillis"/>, asks for the
    /// tiles missing (replacing the server's list from the previous view).</summary>
    public void View(Topology t, double x1, double z1, double x2, double z2, double pixelsPerBlock)
    {
        if (Code.Length == 0 || t == null) return;
        level = OreHeat.LevelFor(t, pixelsPerBlock);
        visible = OreHeat.Visible(t, x1, z1, x2, z2, level);
        // Nearest the middle of the view first.
        double cx = (x1 + x2) / 2, cz = (z1 + z2) / 2;
        visible.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        double Distance(TileInstance v) { double dx = v.X + v.Width / 2 - cx, dz = v.Z + v.Height / 2 - cz; return dx * dx + dz * dz; }
        long now = Environment.TickCount64;
        var hash = new HashCode();
        hash.Add(level);
        foreach (var v in visible) { hash.Add(v.Key.X); hash.Add(v.Key.Z); }
        int h = hash.ToHashCode();
        if (h != viewHash) { viewHash = h; viewSince = now; }
        if (now - viewSince < SettleMillis || now - sentAt < SettleMillis) return;
        var want = Wanted(t, now);
        if (want.Count == 0) return;
        // Nothing new: every tile wanted is already on its way.
        if (want.All(k => asked.TryGetValue(k, out long at) && now - at < AnswerMillis)) return;
        foreach (var k in asked.Keys.Where(k => now - asked[k] >= AnswerMillis || !want.Contains(k)).ToList()) asked.Remove(k);
        foreach (var k in want) asked.TryAdd(k, now);
        sentAt = now;
        send(new OreHeatRequest { Code = Code, Level = want.Select(k => k.Level).ToArray(), X = want.Select(k => k.X).ToArray(), Z = want.Select(k => k.Z).ToArray() });
    }

    /// <summary>The tiles to ask for: for each tile of the view without its own answer (nearest the middle first), and
    /// none coarser to stand in for it, a tile two levels up first, so the whole view fills quickly; then the view's own.</summary>
    List<Key> Wanted(Topology t, long now)
    {
        int max = OreHeat.MaxLevel(t);
        var coarse = new List<Key>();
        var own = new List<Key>();
        foreach (var v in visible)
        {
            var k = new Key(Code, level, v.Key.X, v.Key.Z);
            if (Have(k, now)) continue;
            own.Add(k);
            if (Ancestor(k, max).Tile == null && level < max)
            {
                int up = Math.Min(2, max - level);
                var a = new Key(Code, level + up, k.X >> up, k.Z >> up);
                if (!Have(a, now) && !coarse.Contains(a)) coarse.Add(a);
            }
        }
        return coarse.Concat(own).Take(OreHeat.MaxTilesPerRequest).ToList();
    }

    bool Have(Key k, long now) => tiles.TryGetValue(k, out var tile) && !tile.Derived && !(tile.Partial && now - tile.At > PartialMillis);

    /// <summary>The nearest coarser tile with its own answer.</summary>
    (Tile Tile, int Up) Ancestor(Key k, int max)
    {
        for (int up = 1; k.Level + up <= max && up < 31; up++)
            if (tiles.TryGetValue(new Key(k.Code, k.Level + up, k.X >> up, k.Z >> up), out var a) && !a.Derived) return (a, up);
        return (null, 0);
    }

    public void Receive(OreHeatResponse r)
    {
        var t = topology();
        if (r == null || t == null) return;
        if (r.StoredOf > 0) { Stored = r.Stored; StoredOf = r.StoredOf; }
        var k = new Key(r.Code ?? "", r.Level, r.X, r.Z);
        asked.Remove(k);
        if (r.Error != null) { api.Logger.Warning("[wmap] Ore heat tile {0} {1}/{2}/{3}: {4}", r.Code, r.Level, r.X, r.Z, r.Error); return; }
        if (!OreHeat.ValidKey(t, r.Level, r.X, r.Z) || r.Blocks?.Length != OreHeat.TileCount || r.Factors?.Length != OreHeat.TileCount) return;
        long now = Environment.TickCount64;
        if (!tiles.TryGetValue(k, out var tile)) tiles[k] = tile = new Tile();
        tile.Blocks = r.Blocks; tile.Factors = r.Factors; tile.Partial = r.Partial; tile.Derived = false; tile.At = now; tile.Used = now;
        Upload(tile, k.Code == OreHeat.Listed);
        if (r.Millis > 0) lastMillis = r.Millis;
        Trim();
    }

    void Upload(Tile tile, bool listed)
    {
        tile.Texture ??= new LoadedTexture(api, 0, OreHeat.TileCells, OreHeat.TileCells);
        var texture = tile.Texture;
        api.Render.LoadOrUpdateTextureFromRgba(OreHeat.Pixels(tile.Blocks, listed), false, 0, ref texture);
        tile.Texture = texture;
    }

    /// <summary>Drops the tiles drawn least recently once there are more than <see cref="MaxTiles"/>.</summary>
    void Trim()
    {
        if (tiles.Count <= MaxTiles) return;
        foreach (var (key, tile) in tiles.OrderBy(p => p.Value.Used).Take(tiles.Count - MaxTiles * 9 / 10).ToList())
        {
            tile.Texture?.Dispose();
            tiles.Remove(key);
        }
    }

    /// <summary>The tile drawn for a key of the view: its own, or one worked out from the nearest coarser tile (again when a
    /// nearer one arrives), or null.</summary>
    Tile Shown(Key k, int max, long now, ref int derived)
    {
        tiles.TryGetValue(k, out var tile);
        if (tile != null && !tile.Derived) { tile.Used = now; return tile; }
        var (a, up) = Ancestor(k, max);
        if (a == null) return tile;
        if (tile != null && tile.From == k.Level + up && tile.FromAt == a.At) { tile.Used = now; return tile; }
        if (derived >= DerivedPerFrame) return tile;
        derived++;
        if (tile == null) tiles[k] = tile = new Tile { Derived = true };
        tile.From = k.Level + up; tile.FromAt = a.At; tile.Used = now; a.Used = now;
        tile.Blocks = OreHeat.Derive(a.Blocks, up, k.X, k.Z);
        tile.Factors = OreHeat.Derive(a.Factors, up, k.X, k.Z);
        Upload(tile, k.Code == OreHeat.Listed);
        return tile;
    }

    /// <summary>Draws the view's tiles (render pixels), at <paramref name="depth"/>.</summary>
    public void Draw(Topology t, MapCamera camera, double left, double top, double width, double height, float depth)
    {
        if (Code.Length == 0 || level < 0 || t == null) return;
        int max = OreHeat.MaxLevel(t), derived = 0;
        long now = Environment.TickCount64;
        double scale = camera.Scale, span = OreHeat.Span(level) * scale;
        foreach (var v in visible)
        {
            var tile = Shown(new Key(Code, level, v.Key.X, v.Key.Z), max, now, ref derived);
            if (tile?.Texture == null || tile.Texture.TextureId == 0) continue;
            double x = left + width / 2 + (v.X - camera.X) * scale, y = top + height / 2 + (v.Z - camera.Z) * scale;
            api.Render.Render2DTexturePremultipliedAlpha(tile.Texture.TextureId, x, y, span, span, depth);
        }
        Trim();
    }

    /// <summary>What the map says at world position x, z, for the atlas's hover line; null off the map.</summary>
    public string Describe(Topology t, double x, double z, string method)
    {
        if (Code.Length == 0 || level < 0 || t == null || z < 0 || z >= t.Height) return null;
        var (kx, kz) = OreHeat.KeyAt(t, level, x, z);
        if (!tiles.TryGetValue(new Key(Code, level, kx, kz), out var tile) || tile.Blocks == null) return Name(Code) + ": working out…";
        int i = OreHeat.CellAt(t, level, kx, kz, x, z);
        if (i < 0) return null;
        float blocks = tile.Blocks[i], factor = tile.Factors[i];
        string coarse = tile.Derived ? string.Format(CultureInfo.InvariantCulture, " (from {0:0}-block cells until this tile arrives)", OreHeat.CellSize(tile.From)) : "";
        if (float.IsNaN(blocks)) return Name(Code) + ": no answer here (the region is not loaded)" + coarse;
        if (Code == OreHeat.Listed)
            return string.Format(CultureInfo.InvariantCulture, "The pick would list {0:0} ores here (strongest factor {1:0.000}){2}", blocks, factor, coarse);
        int word = OreHeat.WordIndex(factor);
        string pick = word < 0 ? "pick: miniscule amounts" : "pick: " + Lang.Get(OreHeat.WordKeys[word]);
        string bound = method == OreHeatRequest.OreMap ? " (ore map, host rock not counted)" : "";
        return string.Format(CultureInfo.InvariantCulture, "{0}: about {1} ore blocks per chunk{2} · {3} (factor {4:0.000}){5}", Name(Code), OreHeat.Short(blocks), bound, pick, factor, coarse);
    }

    public void Dispose()
    {
        foreach (var tile in tiles.Values) tile.Texture?.Dispose();
        tiles.Clear(); asked.Clear(); visible = new();
    }
}
