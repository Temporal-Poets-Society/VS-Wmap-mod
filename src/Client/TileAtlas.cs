using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenTK.Graphics.OpenGL;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>Detail tiles in semantic texels, packed into a few large GL pages without mipmaps.</summary>
public sealed class TileAtlas : IDisposable
{
    public const int PageSize = 2048, Across = PageSize / TileData.Padded, SlotsPerPage = Across * Across;
    sealed class Slot { public TileKey Key; public int Number; public long Used; public byte[] Fingerprint; }
    readonly TileClient tiles;
    readonly int pageLimit;
    readonly List<int> pages = new();
    readonly Dictionary<TileKey, Slot> slots = new();
    readonly Stack<int> free = new();
    long clock;
    public int Count => slots.Count;
    /// <summary>Changes whenever the slots or the tiles' data change, so a caller can keep what it worked out from them.</summary>
    public int Version { get; private set; }
    public int MiB => pages.Count * PageSize * PageSize * 4 / 1048576;
    public bool Has(TileKey key) => slots.ContainsKey(key);
    public TileAtlas(TileClient tiles, int pageLimit) { this.tiles = tiles; this.pageLimit = pageLimit; tiles.Invalidated += Invalidate; }

    void Invalidate(TileKey key)
    {
        Version++;
        // Reloading an identical disk tile must not blank its surviving GPU copy.
        // Content edits still invalidate it; the hash costs 32 bytes per slot.
        if (slots.TryGetValue(key, out var present) && tiles.Get(key)?.Rgba is { } rgba &&
            SHA256.HashData(rgba).AsSpan().SequenceEqual(present.Fingerprint)) return;
        if (slots.Remove(key, out var slot)) free.Push(slot.Number);
    }

    /// <summary>Keep uploaded neighbouring levels visible while the new zoom's tiles load.</summary>
    public IEnumerable<TileInstance> Display(IReadOnlyList<TileInstance> requested, Topology topology, MapCamera camera, double width, double height,
        double baseTexel = 0, bool observations = true)
    {
        if (requested.Count == 0) yield break;
        int finest = requested.Min(tile => tile.Key.Level);
        int maxLevel = ViewTiles.MaxDetailLevel(topology, baseTexel);
        var layers = requested.Select(tile => tile.Key.Layer).ToHashSet();
        var wanted = requested.Select(tile => tile.Key).ToHashSet();
        // Parents fill zoom-in gaps; one finer level fills zoom-out gaps without excessive minification. Gather both
        // sources before sorting: an observed fallback must stay above a newly arrived finer prediction.
        var fallback = new List<TileInstance>();
        foreach (var group in slots.Keys.Where(key => key.Level != finest && key.Level >= finest - 1
                         && (key.Layer == 1 || key.Level <= maxLevel) && (key.Layer == 1 ? observations : layers.Contains(key.Layer)))
                     .Select(key => (key.Level, key.Layer)).Distinct().OrderByDescending(group => group.Level).ThenBy(group => group.Layer))
            foreach (var tile in TileSelection.Visible(topology, camera, width, height, group.Level, group.Layer))
                if (!wanted.Contains(tile.Key) && Has(tile.Key)) fallback.Add(tile);
        foreach (var tile in ViewTiles.Display(requested, Has, fallback)) yield return tile;
    }

    /// <summary>Uploads a few missing visible tiles per frame, coarse first; evicts the least recently drawn. True when
    /// it stopped at <paramref name="perFrame"/> with ready tiles still to upload: call again next frame.</summary>
    public bool Upload(IEnumerable<TileKey> visible, int perFrame = 4)
    {
        bool more = false;
        var keys = visible.ToHashSet();
        int remaining = perFrame;
        // Unpack state as the uploads need it (another mod may have left a buffer bound), only once there is one.
        GlUnpack? unpack = null;
        foreach (var key in keys.OrderByDescending(k => k.Level).ThenBy(k => k.Layer))
        {
            if (slots.TryGetValue(key, out var present)) { present.Used = ++clock; continue; }
            var e = tiles.Get(key);
            if (e?.Rgba == null) continue;
            if (remaining <= 0) { more = true; break; }
            unpack ??= new GlUnpack();
            if (free.Count == 0 && pages.Count < pageLimit)
            {
                int page = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, page);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, PageSize, PageSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
                int start = pages.Count * SlotsPerPage; pages.Add(page);
                for (int n = SlotsPerPage - 1; n >= 0; n--) free.Push(start + n);
            }
            if (free.Count == 0)
            {
                var victim = slots.Values.Where(s => !keys.Contains(s.Key)).OrderBy(s => s.Used).FirstOrDefault();
                if (victim == null) continue;
                // This is capacity eviction, regardless of unchanged CPU contents.
                slots.Remove(victim.Key); free.Push(victim.Number); Version++;
            }
            int number = free.Pop(), index = number % SlotsPerPage;
            GL.BindTexture(TextureTarget.Texture2D, pages[number / SlotsPerPage]);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            var handle = GCHandle.Alloc(e.Rgba, GCHandleType.Pinned);
            try
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, index % Across * TileData.Padded, index / Across * TileData.Padded,
                    TileData.Padded, TileData.Padded, PixelFormat.Rgba, PixelType.UnsignedByte, handle.AddrOfPinnedObject());
            }
            finally { handle.Free(); }
            slots[key] = new Slot { Key = key, Number = number, Used = ++clock, Fingerprint = SHA256.HashData(e.Rgba) };
            remaining--; Version++;
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
        unpack?.Dispose();
        return more;
    }

    /// <param name="reference">For a prediction tile: the coarser recording drawn under it, which decides the cells the
    /// prediction may refine (<see cref="ViewTiles.Reference"/>). Ignored while that recording is not uploaded.</param>
    public void Draw(TileInstance tile, MapRenderer renderer, float alpha, float depth, TileInstance? reference = null)
    {
        if (!slots.TryGetValue(tile.Key, out var slot)) return;
        slot.Used = ++clock;
        int index = slot.Number % SlotsPerPage;
        MapRenderer.TileReference? check = null;
        if (reference is { } r && slots.TryGetValue(r.Key, out var under))
        {
            int at = under.Number % SlotsPerPage;
            check = new MapRenderer.TileReference(pages[under.Number / SlotsPerPage], at % Across * TileData.Padded, at / Across * TileData.Padded, r.X, r.Z, r.Key.Step);
        }
        renderer.DrawTile(pages[slot.Number / SlotsPerPage], PageSize, index % Across * TileData.Padded, index / Across * TileData.Padded,
            tile.X, tile.Z, tile.Width, tile.Height, tile.Key.Step, alpha, depth, tile.Key.Layer == 1, check);
    }

    public void Clear()
    {
        slots.Clear(); free.Clear(); Version++;
        foreach (int p in pages) GL.DeleteTexture(p);
        pages.Clear();
    }

    public void Dispose() { tiles.Invalidated -= Invalidate; Clear(); }
}
