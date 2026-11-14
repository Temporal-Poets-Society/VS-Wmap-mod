using ProtoBuf;
using Wmap.Core;

namespace Wmap;

public static class WmapProtocol
{
    public const string Channel = "wmap-v2";
    public const int Version = 2;
}

[ProtoContract]
public sealed class AtlasHello { [ProtoMember(1)] public int Protocol = WmapProtocol.Version; }

[ProtoContract]
public sealed class AtlasInfo
{
    [ProtoMember(1)] public string Session;
    [ProtoMember(2)] public string CacheId;
    [ProtoMember(3)] public double StartX;
    [ProtoMember(4)] public double Width;
    [ProtoMember(5)] public double Height;
    [ProtoMember(6)] public bool Wraps;
    [ProtoMember(7)] public bool Allowed;
    /// <summary>With <see cref="Allowed"/> false: why the server declined (0.5.3, WM-0034), and its protocol.</summary>
    [ProtoMember(42)] public string Declined;
    [ProtoMember(43)] public int ServerProtocol;
    [ProtoMember(8)] public bool HasOverview;
    [ProtoMember(9)] public string Source;
    [ProtoMember(10)] public int SeaLevel;
    [ProtoMember(11)] public double EquatorZ;
    [ProtoMember(12)] public double PoleDistance;
    [ProtoMember(13)] public bool HasGeography;
    [ProtoMember(14)] public string ObservationId;
    [ProtoMember(15)] public bool CanDevelop;
    /// <summary>Predicted detail tiles may be requested (development, or a shared world chart).</summary>
    [ProtoMember(16)] public bool CanPredict;
    /// <summary>SHA-256 of the packed world raster once it is baked; null while unavailable.</summary>
    [ProtoMember(17)] public string BaseId;
    [ProtoMember(18)] public int BaseWidth;
    [ProtoMember(19)] public int BaseHeight;
    [ProtoMember(20)] public int BaseBytes;
    /// <summary>0..1 while the server bakes the world raster.</summary>
    [ProtoMember(21)] public float BaseProgress;
    /// <summary>World height in blocks; with SeaLevel it scales relief shading.</summary>
    [ProtoMember(22)] public int MapHeight;
    /// <summary>Blocks around a player a Jonas barometer senses the weather (the server's setting).</summary>
    [ProtoMember(23)] public int BarometerRange;
    /// <summary>The prediction is a land/sea mask with invented heights (no relief): no far terrain, and
    /// recorded terrain draws over it at every zoom. Absent (false) from servers before 0.3.8.</summary>
    [ProtoMember(24)] public bool FlatPrediction;
    /// <summary>The server takes far-terrain tile requests (<see cref="TileRequest.Stream"/> 1) apart from the
    /// atlas's. Absent (false) from servers before 0.4.0, which would treat them as atlas requests.</summary>
    [ProtoMember(25)] public bool FarTiles;
    /// <summary>The server can sample biome tiles (forest and climate; Earth Map).</summary>
    [ProtoMember(26)] public bool Biome;
    /// <summary>Names the biome tiles' inputs in cache file names (see <see cref="Core.BiomeTile.Identity"/>).</summary>
    [ProtoMember(27)] public string BiomeId;
    /// <summary>The server teleports creative players to a map position (<see cref="TeleportRequest"/>). Absent (false)
    /// from servers before 0.4.3, which do not know the message.</summary>
    [ProtoMember(28)] public bool Teleport;
    /// <summary>The climate raster (<see cref="Core.ClimateRaster"/>), fetched with <see cref="BaseRequest"/> under this id
    /// like the world chart; null while unavailable and from servers before 0.4.3.</summary>
    [ProtoMember(29)] public string ClimateId;
    [ProtoMember(30)] public int ClimateBytes;
    /// <summary>Game charting by line of sight, and its reach and circle (the server's settings; absent before 0.4.3: the circle).</summary>
    [ProtoMember(31)] public bool ExploreBySight;
    [ProtoMember(32)] public int SightDistance;
    [ProtoMember(33)] public int SightCircle;
    /// <summary>The server makes automatic markers (<see cref="AutoMarkRequest"/>). Absent (false) from servers before 0.4.6,
    /// which do not know the message.</summary>
    [ProtoMember(34)] public bool AutoMarks;
    /// <summary>The server takes paper maps (<see cref="MapDraw"/>, <see cref="MapRead"/>). Absent (false) from servers
    /// before 0.4.8.</summary>
    [ProtoMember(35)] public bool PaperMaps;
    /// <summary>Hidden patches line of sight encloses are charted up to a square this many blocks wide (the server's
    /// <c>SightHoleSize</c>; absent before 0.4.9: none).</summary>
    [ProtoMember(36)] public int SightHoleSize;
    /// <summary>The planet's radius in blocks: ground sinks below the horizon by distance² / (2 × radius) for line of sight
    /// and far terrain. 0 is flat (no geography, the server's <c>PlanetScale</c> 0, or a server before 0.4.11).</summary>
    [ProtoMember(37)] public double PlanetRadius;
    /// <summary>Whose geography the latitudes and longitudes follow: <see cref="EarthGeography"/> when Earth Map's, which
    /// the client waits for instead of using the linear fallback. Null without one, or from an older server (the
    /// client then goes by <see cref="Source"/>).</summary>
    [ProtoMember(38)] public string GeographyProvider;
    /// <summary>Blocks the chart's and tiles' heights span (<see cref="Semantic.HeightRangeFor"/>); 0 from servers before
    /// 0.5.0-pre.11, which always used <see cref="Semantic.DefaultHeightRange"/>. Read it through <see cref="ChartHeightRange"/>.</summary>
    [ProtoMember(39)] public double HeightRange;
    /// <summary>Ore codes the server draws heat maps of (<see cref="OreHeatRequest"/>), for players with the full map;
    /// null otherwise and from servers before 0.5.2 (first as 0.5.2-ore.1), which do not know the message (WM-D47).</summary>
    [ProtoMember(40)] public string[] OreHeatCodes;
    /// <summary>How the heat maps are worked out: <see cref="OreHeatRequest.Reading"/> (the world's deposit generators
    /// answer the pick's question anywhere) or <see cref="OreHeatRequest.OreMap"/> (vanilla's region ore maps, loaded regions only).</summary>
    [ProtoMember(41)] public string OreHeatMethod;
    /// <summary>A copy, for one player's info (AtlasServer.SendInfo): the shared one is never changed per player.</summary>
    public AtlasInfo Copy() => (AtlasInfo)MemberwiseClone();
    public double ChartHeightRange => HeightRange > 0 ? HeightRange : Semantic.DefaultHeightRange;
    public const string EarthGeography = "earth";
    public Topology Topology() => new() { StartX = StartX, Width = Width, Height = Height, Wraps = Wraps };
}

[ProtoContract]
public sealed class TileRequest
{
    [ProtoMember(1)] public string Session;
    [ProtoMember(2)] public int View;
    [ProtoMember(3)] public int Level;
    [ProtoMember(4)] public int X;
    [ProtoMember(5)] public int Z;
    /// <summary>0 the atlas, 1 far terrain. Each stream has its own <see cref="View"/> and request cap.</summary>
    [ProtoMember(6)] public int Stream;
    /// <summary>0 a semantic tile, 1 semantic and biome tiles, 2 a biome tile.</summary>
    [ProtoMember(7)] public int Content;
}

public static class TileContent
{
    public const int Semantic = 0, SemanticAndBiome = 1, Biome = 2;
    public static bool HasSemantic(int content) => content != Biome;
    public static bool HasBiome(int content) => content is SemanticAndBiome or Biome;
}

[ProtoContract]
public sealed class TileResponse
{
    [ProtoMember(1)] public string Session;
    [ProtoMember(2)] public int View;
    [ProtoMember(3)] public int Level;
    [ProtoMember(4)] public int X;
    [ProtoMember(5)] public int Z;
    [ProtoMember(6)] public byte[] Data;
    [ProtoMember(7)] public string Error;
    /// <summary><see cref="Error"/> when the server's workers were all busy: ask again shortly (servers from 0.4.4).</summary>
    public const string Busy = "busy";
    [ProtoMember(8)] public int Stream;
    [ProtoMember(9)] public int Content;
    /// <summary>A packed <see cref="Core.BiomeTile"/> when <see cref="Content"/> asks for one.</summary>
    [ProtoMember(10)] public byte[] Biome;
}

/// <summary>A creative player's right-click on the atlas: teleport to this world position (blocks).</summary>
[ProtoContract]
public sealed class TeleportRequest
{
    [ProtoMember(1)] public double X;
    [ProtoMember(2)] public double Z;
}

/// <summary>A player harvested, or looked at while sneaking, something of an automatic marker's kind: a block, or a
/// creature when <see cref="EntityId"/> is set. The server checks it and makes the waypoint (0.4.6).</summary>
[ProtoContract]
public sealed class AutoMarkRequest
{
    [ProtoMember(1)] public string Category;
    [ProtoMember(2)] public int X;
    [ProtoMember(3)] public int Y;
    [ProtoMember(4)] public int Z;
    [ProtoMember(5)] public long EntityId;
}

/// <summary>A player holding a blank paper map draws one: the ticked parts, and the upload of what only their client has
/// (charted area, recorded terrain) that follows in <see cref="MapPiece"/>s under <see cref="Id"/> (0.4.8).</summary>
[ProtoContract]
public sealed class MapDraw
{
    [ProtoMember(1)] public string Id;
    [ProtoMember(2)] public string Title;
    [ProtoMember(3)] public int Parts;
    [ProtoMember(4)] public int Total;
}

/// <summary>Part of a paper map: a drawing's upload (client to server), or what a reader copies (server to client). From
/// the server with <see cref="Total"/> -1 and no data: stop uploading the drawing <see cref="Id"/>, it is not taken.</summary>
[ProtoContract]
public sealed class MapPiece
{
    // As the world chart's pieces.
    public const int Size = 64 * 1024;
    [ProtoMember(1)] public string Id;
    [ProtoMember(2)] public int Offset;
    [ProtoMember(3)] public int Total;
    [ProtoMember(4)] public byte[] Data;
}

/// <summary>A player holding a drawn paper map copies these parts of it into their atlas (0.4.8).</summary>
[ProtoContract]
public sealed class MapRead
{
    [ProtoMember(1)] public string Id;
    [ProtoMember(2)] public int Parts;
}

/// <summary>A player with the full map asks for ore heat tiles (<see cref="Core.OreHeat"/>; level, x, z per tile, at most
/// <see cref="Core.OreHeat.MaxTilesPerRequest"/>, the most wanted first) of one ore, or of
/// <see cref="Core.OreHeat.Listed"/> for the number of ores the pick would list. A newer request replaces the tiles of one
/// not yet answered (WM-D47, WM-D48).</summary>
[ProtoContract]
public sealed class OreHeatRequest
{
    public const string Reading = "reading", OreMap = "oremap";
    [ProtoMember(1)] public string Code;
    [ProtoMember(2, IsPacked = true)] public int[] Level;
    [ProtoMember(3, IsPacked = true)] public int[] X;
    [ProtoMember(4, IsPacked = true)] public int[] Z;
}

/// <summary>One ore heat tile: per cell, row-major from the north-west, the ore blocks a chunk column there is expected to
/// hold and the pick's factor (or the ores it lists, and the strongest factor). NaN where there is no answer (vanilla's
/// ore maps of regions not loaded) or off the world.</summary>
[ProtoContract]
public sealed class OreHeatResponse
{
    [ProtoMember(1)] public string Code;
    [ProtoMember(2)] public int Level;
    [ProtoMember(3)] public int X;
    [ProtoMember(4)] public int Z;
    [ProtoMember(5, IsPacked = true)] public float[] Blocks;
    [ProtoMember(6, IsPacked = true)] public float[] Factors;
    [ProtoMember(7)] public string Error;
    /// <summary>Milliseconds the server worked the tile out for; 0 when it was stored.</summary>
    [ProtoMember(8)] public int Millis;
    /// <summary>Some cells have no answer yet (vanilla's ore maps of regions not loaded): worth asking again later.</summary>
    [ProtoMember(9)] public bool Partial;
    /// <summary>The coarse tiles the server stores ahead of time, and how many of them it has.</summary>
    [ProtoMember(10)] public int Stored;
    [ProtoMember(11)] public int StoredOf;
}

[ProtoContract]
public sealed class BaseRequest
{
    [ProtoMember(1)] public string BaseId;
    [ProtoMember(2)] public int Offset;
}

[ProtoContract]
public sealed class BaseChunk
{
    // Small enough for any transport; a 12.5 MiB chart is about 200 messages.
    public const int Size = 64 * 1024;
    [ProtoMember(1)] public string BaseId;
    [ProtoMember(2)] public int Offset;
    [ProtoMember(3)] public byte[] Data;
}
