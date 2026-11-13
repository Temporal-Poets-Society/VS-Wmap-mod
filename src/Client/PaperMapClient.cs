using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Wmap.Core;

namespace Wmap.Client;

/// <summary>The client's side of paper maps (WM-P05, docs/paper-maps.md): packs what only this client has (the charted
/// area, recorded terrain) for a drawing and uploads it; takes a read map's charted area and terrain from the server and
/// merges them into this player's own, where they have recorded nothing. Main thread, except the packing and checking
/// done on workers.</summary>
public sealed class PaperMapClient
{
    readonly TileClient tiles;
    readonly ExploredMask mask;
    readonly IClientNetworkChannel channel;
    readonly Action<string> tell;
    /// <summary>Upload pieces sent a tick: 256 KiB, 5 MiB a second.</summary>
    public const int PiecesPerTick = 4;
    // Drawing: packing, then the upload.
    Task<byte[]> packing;
    MapDraw drawing;
    byte[] upload;
    int uploadOffset;
    // Reading: the download, checking it, then merging.
    string readingId;
    byte[] download;
    int downloaded;
    readonly HashSet<int> pieces = new();
    Task<(byte[] Mask, List<(TileKey, byte[])> Tiles)> checking;
    // Checked tiles wait compressed and are decoded a batch at a time, just before their merge: decoded, a tile takes
    // about 150 KB whatever its compressed size.
    readonly Queue<(TileKey Key, byte[] Data)> encoded = new();
    Task<List<(TileKey, TileData)>> decoding;
    readonly Queue<(TileKey Key, TileData Data)> merges = new();
    /// <summary>Tiles decoded at once for merging.</summary>
    public const int DecodeBatch = 32;

    public PaperMapClient(TileClient tiles, ExploredMask mask, IClientNetworkChannel channel, Action<string> tell)
    {
        this.tiles = tiles; this.mask = mask; this.channel = channel; this.tell = tell;
    }

    /// <summary>Whether a drawing is being packed or uploaded.</summary>
    public bool Drawing => packing != null || upload != null;
    /// <summary>Recorded tiles still to merge from a read map.</summary>
    public int Merging => merges.Count + encoded.Count;
    /// <summary>Recorded tiles merged from read maps.</summary>
    public int Merged { get; private set; }
    /// <summary>Whether a read map is still arriving, being checked or merged.</summary>
    public bool Reading => readingId != null || checking != null || decoding != null || Merging > 0;

    /// <summary>Draws onto the held blank map: packs the ticked parts only this client has, then asks the server.</summary>
    public void Draw(string title, MapParts parts)
    {
        if (Drawing) { tell("Wmap: still drawing the last map."); return; }
        var map = new PaperMap { Parts = parts };
        byte[] maskData = parts.HasFlag(MapParts.Charted) ? (byte[])mask.Data.Clone() : null;
        int w = mask.Width, h = mask.Height;
        var recordings = parts.HasFlag(MapParts.Terrain) ? tiles.SnapshotRecordings() : null;
        drawing = new MapDraw { Id = Guid.NewGuid().ToString("N"), Title = title, Parts = (int)parts };
        packing = Task.Run(() =>
        {
            if (maskData != null && Array.Exists(maskData, v => v > 0)) map.Mask = ExploredMask.Pack(maskData, w, h);
            if (recordings != null) map.Tiles.AddRange(recordings());
            return map.Mask == null && map.Tiles.Count == 0 ? Array.Empty<byte>() : map.Write();
        });
    }

    /// <summary>Copies the ticked parts of a held drawn map; the server makes the waypoints and readings and sends the rest.</summary>
    public void Read(string id, MapParts parts)
    {
        // A read that has received nothing may have been refused (the server says why in chat): this one replaces it.
        if (download != null || checking != null || decoding != null || Merging > 0) { tell("Wmap: still copying the last map."); return; }
        readingId = parts.HasFlag(MapParts.Charted) || parts.HasFlag(MapParts.Terrain) ? id : null;
        download = null; downloaded = 0; pieces.Clear();
        channel.SendPacket(new MapRead { Id = id, Parts = (int)parts });
    }

    /// <summary>A piece of the map being read.</summary>
    public void Receive(MapPiece piece)
    {
        // The server will not take this drawing (it says why in chat): stop uploading it.
        if (piece?.Id != null && piece.Total < 0 && piece.Id == drawing?.Id) { upload = null; return; }
        if (piece?.Id == null || piece.Id != readingId || piece.Data == null) return;
        if (download == null)
        {
            if (piece.Total <= 0 || piece.Total > 256 << 20) { readingId = null; return; }
            download = new byte[piece.Total];
        }
        int length = Math.Min(MapPiece.Size, download.Length - piece.Offset);
        if (piece.Total != download.Length || piece.Offset < 0 || piece.Offset % MapPiece.Size != 0 || length <= 0 || piece.Data.Length != length || !pieces.Add(piece.Offset)) return;
        Buffer.BlockCopy(piece.Data, 0, download, piece.Offset, length);
        downloaded += length;
        if (downloaded < download.Length) return;
        byte[] bytes = download; download = null; readingId = null;
        var t = tiles.Topology; int w = mask.Width, h = mask.Height;
        checking = Task.Run(() =>
        {
            // Read checks that every tile decodes, keeping only the compressed tiles, and unpacks the charted area.
            var map = PaperMap.Read(bytes, t, w, h);
            return (map.MaskTexels, map.Tiles);
        });
    }

    public void Tick()
    {
        if (packing?.IsCompleted == true)
        {
            if (packing.Status == TaskStatus.RanToCompletion)
            {
                upload = packing.Result; uploadOffset = 0;
                drawing.Total = upload.Length;
                channel.SendPacket(drawing);
            }
            else tell("Wmap: the map could not be drawn: " + packing.Exception?.InnerException?.Message);
            packing = null;
        }
        if (upload != null)
        {
            for (int i = 0; i < PiecesPerTick && uploadOffset < upload.Length; i++)
            {
                int length = Math.Min(MapPiece.Size, upload.Length - uploadOffset);
                channel.SendPacket(new MapPiece { Id = drawing.Id, Offset = uploadOffset, Total = upload.Length, Data = upload.AsSpan(uploadOffset, length).ToArray() });
                uploadOffset += length;
            }
            if (uploadOffset >= upload.Length) upload = null;
        }
        if (checking?.IsCompleted == true)
        {
            if (checking.Status == TaskStatus.RanToCompletion)
            {
                var (maskData, checkedTiles) = checking.Result;
                if (maskData != null) mask.Merge(maskData);
                foreach (var tile in checkedTiles) encoded.Enqueue(tile);
            }
            else tell("Wmap: the map's terrain could not be read: " + checking.Exception?.InnerException?.Message);
            checking = null;
        }
        if (decoding?.IsCompleted == true)
        {
            // Every tile decoded when the map was checked.
            if (decoding.Status == TaskStatus.RanToCompletion) foreach (var tile in decoding.Result) merges.Enqueue(tile);
            decoding = null;
        }
        if (decoding == null && merges.Count < DecodeBatch && encoded.Count > 0)
        {
            var batch = new List<(TileKey Key, byte[] Data)>();
            while (batch.Count < DecodeBatch && encoded.Count > 0) batch.Add(encoded.Dequeue());
            decoding = Task.Run(() => batch.ConvertAll(tile => (tile.Key, TileCodec.Decode(tile.Data))));
        }
        // A few tiles a tick, each once the player's own recording has loaded; the rest wait their turn. Merged tiles stay
        // in the tile cache until saved: while many are waiting to be saved, merging waits too, so the atlas and the
        // recorder keep room in the cache.
        if (tiles.SaveBacklog) return;
        for (int i = 0, n = Math.Min(merges.Count, 32); i < n; i++)
        {
            var next = merges.Dequeue();
            if (tiles.MergeRecording(next.Key, next.Data)) Merged++;
            else merges.Enqueue(next);
        }
    }
}
