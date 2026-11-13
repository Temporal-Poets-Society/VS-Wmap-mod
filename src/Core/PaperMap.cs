using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wmap.Core;

/// <summary>What a paper map holds; the drawer ticks the parts to draw and the reader the parts to copy (WM-P05).</summary>
[Flags]
public enum MapParts
{
    None = 0,
    /// <summary>The charted area (the exploration mask).</summary>
    Charted = 1,
    /// <summary>Recorded terrain tiles, every level.</summary>
    Terrain = 2,
    /// <summary>The drawer's own waypoints.</summary>
    Waypoints = 4,
    /// <summary>Automatic markers (waypoints tagged <see cref="AutoMarkCatalog.Tag"/>).</summary>
    AutoMarks = 8,
    /// <summary>Prospecting readings.</summary>
    Readings = 16,
    All = 31,
    /// <summary>The parts only the drawer's client has, uploaded with the drawing.</summary>
    Client = Charted | Terrain,
}

/// <summary>A waypoint as a paper map holds it: what the game's waypoint shows, without its owner, id or pin (a reader's
/// copies are not pinned: a map's worth of pins would crowd the atlas's edge).</summary>
public sealed record MapWaypoint(double X, double Y, double Z, string Title, string Text, string Icon, int Color, bool ShowInWorld)
{
    public bool Auto => AutoMarkCatalog.IsAuto(Text);
}

/// <summary>A prospecting reading as a paper map holds it: its place and the game's own serialized reading.</summary>
public sealed record MapReading(double X, double Y, double Z, byte[] Data);

/// <summary>
/// A paper map's content: an immutable snapshot of what its drawer had discovered (docs/paper-maps.md). The server keeps
/// it in the world save under <see cref="SaveKey"/>; the item carries only its id. The charted area and terrain come from
/// the drawer's client and are checked for format only; waypoints and readings come from the server's own lists.
/// </summary>
public sealed class PaperMap
{
    const uint Magic = 0x4D50_4D57; // "WMPM"
    const int Version = 1;
    public const int MaxTitle = 60, MaxText = 512, MaxReading = 64 * 1024, MaxMarkers = 100_000;
    public static string SaveKey(string id) => "wmap-map-" + id;

    public string Title = "", Author = "", Drawn = "";
    /// <summary>The parts the drawer ticked.</summary>
    public MapParts Parts;
    /// <summary>The charted area as <c>ExploredMask.Pack</c> writes it; null without one.</summary>
    public byte[] Mask;
    /// <summary>Recorded tiles (layer 1) as <see cref="TileCodec"/> encodes them.</summary>
    public readonly List<(TileKey Key, byte[] Data)> Tiles = new();
    public readonly List<MapWaypoint> Waypoints = new();
    public readonly List<MapReading> Readings = new();
    /// <summary>The charted area unpacked and its charted texels, when <see cref="Read"/> checked it (else null and -1);
    /// not written.</summary>
    public byte[] MaskTexels;
    public long ChartedTexels = -1;

    /// <summary>The parts this map holds content for (a ticked part with nothing in it is not held).</summary>
    public MapParts Holds =>
        (Mask != null ? MapParts.Charted : 0) | (Tiles.Count > 0 ? MapParts.Terrain : 0)
        | (Waypoints.Any(w => !w.Auto) ? MapParts.Waypoints : 0) | (Waypoints.Any(w => w.Auto) ? MapParts.AutoMarks : 0)
        | (Readings.Count > 0 ? MapParts.Readings : 0);

    /// <summary>A copy holding only these parts' content (what a reader asked for), sharing the content's arrays.</summary>
    public PaperMap Only(MapParts parts)
    {
        var copy = new PaperMap { Title = Title, Author = Author, Drawn = Drawn, Parts = Parts & parts };
        if (parts.HasFlag(MapParts.Charted)) copy.Mask = Mask;
        if (parts.HasFlag(MapParts.Terrain)) copy.Tiles.AddRange(Tiles);
        copy.Waypoints.AddRange(Waypoints.Where(w => parts.HasFlag(w.Auto ? MapParts.AutoMarks : MapParts.Waypoints)));
        if (parts.HasFlag(MapParts.Readings)) copy.Readings.AddRange(Readings);
        return copy;
    }

    public byte[] Write()
    {
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic); w.Write(Version);
            w.Write(Title ?? ""); w.Write(Author ?? ""); w.Write(Drawn ?? ""); w.Write((int)Parts);
            w.Write(Mask?.Length ?? -1);
            if (Mask != null) w.Write(Mask);
            w.Write(Tiles.Count);
            foreach (var (key, data) in Tiles) { w.Write(key.Level); w.Write(key.X); w.Write(key.Z); w.Write(data.Length); w.Write(data); }
            w.Write(Waypoints.Count);
            foreach (var p in Waypoints)
            {
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.Title ?? ""); w.Write(p.Text ?? ""); w.Write(p.Icon ?? "");
                w.Write(p.Color); w.Write(p.ShowInWorld);
            }
            w.Write(Readings.Count);
            foreach (var r in Readings) { w.Write(r.X); w.Write(r.Y); w.Write(r.Z); w.Write(r.Data.Length); w.Write(r.Data); }
        }
        return output.ToArray();
    }

    /// <summary>Reads a map and checks everything in it: every tile decodes and has a key in this world, the charted area
    /// unpacks to this world's mask size, text and counts are bounded and positions finite. Throws
    /// <see cref="InvalidDataException"/> otherwise. Decoding every tile takes time: call it off the main thread. Without
    /// <paramref name="check"/> the tiles and charted area are not decoded (a map the server checked when it was drawn).</summary>
    public static PaperMap Read(byte[] bytes, Topology t, int maskWidth, int maskHeight, bool check = true)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes ?? throw new InvalidDataException("No map")));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) throw new InvalidDataException("Not a paper map, or another version");
            var map = new PaperMap { Title = Text(r, MaxTitle), Author = Text(r, MaxText), Drawn = Text(r, MaxText) };
            int parts = r.ReadInt32();
            if ((parts & ~(int)MapParts.All) != 0) throw new InvalidDataException("Unknown map parts");
            map.Parts = (MapParts)parts;
            int maskBytes = r.ReadInt32();
            if (maskBytes >= 0)
            {
                map.Mask = Bytes(r, maskBytes, bytes.Length);
                // Unpacks to the world's size, or throws.
                if (check) map.ChartedTexels = Charted(map.MaskTexels = Client.ExploredMask.Unpack(map.Mask, maskWidth, maskHeight));
            }
            int tiles = Count(r, bytes.Length / 48);
            var seen = new HashSet<TileKey>();
            for (int i = 0; i < tiles; i++)
            {
                var key = new TileKey(1, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                if (!t.Valid(key) || !seen.Add(key)) throw new InvalidDataException($"Tile {key.FileName} is not in this world, or twice");
                byte[] data = Bytes(r, r.ReadInt32(), TileCodec.MaxCompressedBytes);
                if (check) TileCodec.Decode(data);
                map.Tiles.Add((key, data));
            }
            int waypoints = Count(r, MaxMarkers);
            for (int i = 0; i < waypoints; i++)
                map.Waypoints.Add(new MapWaypoint(Finite(r), Finite(r), Finite(r), Text(r, MaxText), Text(r, MaxText), Text(r, 64), r.ReadInt32(), r.ReadBoolean()));
            int readings = Count(r, MaxMarkers);
            for (int i = 0; i < readings; i++) map.Readings.Add(new MapReading(Finite(r), Finite(r), Finite(r), Bytes(r, r.ReadInt32(), MaxReading)));
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Data after the map");
            return map;
        }
        catch (EndOfStreamException) { throw new InvalidDataException("The map is cut short"); }
    }

    static string Text(BinaryReader r, int max)
    {
        string s = r.ReadString();
        if (s.Length > max) throw new InvalidDataException("Text too long");
        return s;
    }
    static int Count(BinaryReader r, int max)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > max) throw new InvalidDataException("Count out of range");
        return n;
    }
    static byte[] Bytes(BinaryReader r, int length, int max)
    {
        if (length < 0 || length > max) throw new InvalidDataException("Length out of range");
        byte[] data = r.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException();
        return data;
    }
    static double Finite(BinaryReader r)
    {
        double v = r.ReadDouble();
        if (!double.IsFinite(v)) throw new InvalidDataException("Position not finite");
        return v;
    }

    /// <summary>Whether a reader already has this waypoint: one with the same title and icon within 2 blocks, so reading a
    /// map twice, or a map of one's own, adds nothing.</summary>
    public static bool Has(IEnumerable<MapWaypoint> own, MapWaypoint w) =>
        own.Any(o => o.Title == w.Title && o.Icon == w.Icon && Distance2(o.X - w.X, o.Y - w.Y, o.Z - w.Z) <= 4);

    /// <summary>Whether a reader already has a reading within a block of this one.</summary>
    public static bool Has(IEnumerable<(double X, double Y, double Z)> own, MapReading r) =>
        own.Any(o => Distance2(o.X - r.X, o.Y - r.Y, o.Z - r.Z) <= 1);

    static double Distance2(double x, double y, double z) => x * x + y * y + z * z;

    /// <summary>Copies a map's recorded cells into a reader's tile where the reader has recorded nothing: what the reader
    /// saw themself wins over a copy (the user's choice). True when a cell changed.</summary>
    public static bool MergeCells(TileData own, TileData map)
    {
        bool changed = false;
        // Colours keep their format (RecordedColor): a marked map marks the reader's tile; an older map's colours are not
        // copied into a marked tile, where they would read as this format (false lights).
        if (RecordedColor.Marked(map) && !RecordedColor.Current(own)) { RecordedColor.Mark(own); changed = true; }
        bool colours = RecordedColor.Marked(map) || !RecordedColor.Marked(own);
        for (int i = 0; i < TileData.Count; i++)
        {
            if (own.Kinds[i] != TileData.Unknown || map.Kinds[i] == TileData.Unknown) continue;
            own.Set(i, new MapSample(map.Heights[i], map.Depths[i], map.Kinds[i],
                colours ? RecordedColor.Marked(map) ? RecordedColor.Of(map, i) : map.ColorAt(i) : 0));
            changed = true;
        }
        return changed;
    }

    /// <summary>The charted area in square kilometres: texels charted at all, at their size in blocks.</summary>
    public static double ChartedKm2(byte[] mask, double texelX, double texelZ) => mask == null ? 0 : Charted(mask) * texelX * texelZ / 1e6;
    /// <summary>Texels charted at all.</summary>
    public static long Charted(byte[] mask) => mask.Length - MemoryExtensions.Count(mask.AsSpan(), (byte)0);
}

/// <summary>Markers bucketed by place and by a group (for waypoints, their title and icon, which a duplicate must share),
/// so that checking many against many (<see cref="PaperMap.Has(IEnumerable{MapWaypoint}, MapWaypoint)"/>) compares only
/// those nearby in the same group. <see cref="Around"/> holds every marker of the group within <c>reach</c> blocks of a
/// point, and a few more.</summary>
public sealed class Nearby<T>
{
    readonly double reach;
    readonly Func<T, (double X, double Y, double Z)> at;
    readonly Func<T, string> group;
    readonly Dictionary<(string, long, long, long), List<T>> cells = new();
    public Nearby(double reach, Func<T, (double X, double Y, double Z)> at, Func<T, string> group = null) { this.reach = reach; this.at = at; this.group = group; }
    long Cell(double v) => (long)Math.Floor(Math.Clamp(v / reach, -1e15, 1e15));
    public void Add(T item)
    {
        var (x, y, z) = at(item);
        var cell = (group?.Invoke(item) ?? "", Cell(x), Cell(y), Cell(z));
        if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<T>();
        list.Add(item);
    }
    /// <summary>The markers of <paramref name="probe"/>'s group near it.</summary>
    public IEnumerable<T> Around(T probe)
    {
        var (x, y, z) = at(probe);
        string g = group?.Invoke(probe) ?? "";
        long cx = Cell(x), cy = Cell(y), cz = Cell(z);
        for (long dy = -1; dy <= 1; dy++)
        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
            if (cells.TryGetValue((g, cx + dx, cy + dy, cz + dz), out var list))
                foreach (var item in list) yield return item;
    }
}
