using System;

namespace Wmap;

public sealed class WmapConfig
{
    /// <summary>Apply server world choices after loading wmap.json. Missing keys retain that file's values.
    /// The reader is TreeAttribute.GetBool in game; keeping it injectable allows offline compatibility checks.
    /// Never store this effective config back to wmap.json.</summary>
    public void ApplyWorldOptions(Func<string, bool, bool> readBool)
    {
        ExploreBySight = readBool("wmapExploreBySight", ExploreBySight);
    }

    // Server
    /// <summary>Allow every player to use development mode (unexplored terrain visible).</summary>
    public bool AllowAllPlayers = false;
    /// <summary>Legacy local wind reach (500..12000). Retained for config/protocol compatibility; barometers now show worldwide winds.</summary>
    public int BarometerRange = 3000;
    /// <summary>No longer used (0.5.4, WM-D63): players without the full map get the prediction only for land they have
    /// seen, and the world option "shared world chart" is gone. <see cref="AllowAllPlayers"/> gives everyone the full map.</summary>
    public bool ShareWorldChart = true;
    /// <summary>Predict terrain from Earth Map or Cylinder World. False charts only terrain players have
    /// recorded: use it when another terrain generator (Terra Prety, …) makes the prediction wrong.</summary>
    public bool PredictTerrain = true;
    /// <summary>Largest side of the baked world raster, in texels (512..8192).</summary>
    public int WorldChartSize = 8192;
    /// <summary>Worker threads for the one-time bake and detail tiles; 0 chooses half the processors.</summary>
    public int BakeThreads = 0;
    public int DiskCacheMiB = 512;
    public int CacheRevision = 1;
    /// <summary>Game charting by line of sight (0.4.3): what can be seen from the player's eye over the ground, all around,
    /// out to <see cref="SightDistance"/>, plus a <see cref="SightCircle"/> around them. False charts the
    /// <see cref="ExploreRadius"/> circle. The server's setting applies to every player. On by default from 0.4.5.</summary>
    public bool ExploreBySight = true;
    /// <summary>Blocks line of sight reaches at most (512..16384). On a world with geography the planet's curve usually
    /// hides the ground sooner (<see cref="PlanetScale"/>). 2048 from 0.4.9: 4096 charted coasts far beyond anything the
    /// player could make out.</summary>
    public int SightDistance = 2048;
    /// <summary>The planet's curve, for line of sight and far terrain (0.4.11), on worlds with geography (Earth Map, Cylinder
    /// World's planet layout): its radius is the world's east-west circumference / 2π times this (0..16). Ground sinks below
    /// the horizon by distance² / (2 × radius), so how far a coast or a mountain shows scales with the world's size. Larger
    /// is a flatter planet that shows farther; 0 is flat, with no horizon but <see cref="SightDistance"/>.</summary>
    public double PlanetScale = 1;
    /// <summary>Blocks always charted around the player with line of sight (16..1536).</summary>
    public int SightCircle = 64;
    /// <summary>Hidden patches that what the player sees encloses (the far side of a hill, a hollow) are charted too, up to
    /// the area of a square this many blocks wide; 0 charts only what is seen (0..1024; 0.4.9).</summary>
    public int SightHoleSize = 256;
    /// <summary>Make waypoints for players who harvest, or look at while sneaking, berries, mushrooms, surface ore,
    /// traders and the like (0.4.6). Each player chooses the kinds on the atlas.</summary>
    public bool AutoMarks = true;
    /// <summary>Automatic markers a player may have; more are not made until some are deleted (10..10000).</summary>
    public int AutoMarkLimit = 1000;
    /// <summary>Players draw what they have discovered onto paper maps and copy maps they are given into their atlas (0.4.8).</summary>
    public bool PaperMaps = true;
    /// <summary>Largest paper map, in MiB (1..256); the drawer is told to leave out recorded terrain above it.</summary>
    public int PaperMapMaxMiB = 16;

    // Client
    public bool DevelopmentMode = true;
    /// <summary>0 nautical chart, 1 topographic terrain.</summary>
    public int DefaultStyle = 0;
    public bool DecimalDegrees = false;
    /// <summary>Without line of sight, gameplay charts what lies within this many blocks of the player. Read by the server,
    /// which charts for every player from 0.5.4 (WM-D63); the same for every player regardless of their view distance; the
    /// default matches the game's maximum view distance.</summary>
    public int ExploreRadius = 1536;
    public int ResidentTiles = 384;
    public int AtlasPages = 2;
    /// <summary>1 draws detail tiles at up to one texel per pixel; 0 halves detail work.</summary>
    public int DetailBias = 1;
    public int ObservedRadiusChunks = 4;
    public bool RecordObservedTerrain = true;
    /// <summary>Draw Wmap terrain in the game's HUD minimap; keeps its toggle, position and markers.</summary>
    public bool ReplaceMinimap = true;
    /// <summary>In creative mode, right-click on the atlas teleports there (the server also requires the tp privilege; 0.4.3+).</summary>
    public bool RightClickTeleport = true;
    /// <summary>Shade the night side of the world on the atlas (the Day/Night button; 0.4.3+).</summary>
    public bool DayNightOverlay = false;
    /// <summary>Winds: 0 off, 1 weather now, 2 prevailing now, 3–14 prevailing in January–December.
    /// Gameplay requires a Jonas barometer; every active choice covers the whole world.</summary>
    public int WindLayer = 1;
    /// <summary>Draw the world chart as 3D terrain past the view distance (needs Earth Map: a chart with relief).</summary>
    public bool FarTerrain = true;
    /// <summary>Blocks from the player to the far terrain's horizon (2048..16384).</summary>
    public int FarTerrainDistance = 8192;
    /// <summary>Far terrain mesh detail: 0 low (64-block cells near), 1 normal (32), 2 high (16).</summary>
    public int FarTerrainDetail = 1;
    /// <summary>Near band: out to <see cref="NearBandDistance"/>, far terrain uses 4-block detail tiles instead of the
    /// world chart (Earth Map; client and server 0.4.0+). See docs/far-terrain-near-band.md.</summary>
    public bool FarTerrainNearBand = true;
    /// <summary>Blocks from the player to the near band's outer edge (768..1536: its tiles must fit the 8 × 8 texture slots).</summary>
    public int NearBandDistance = 1536;
    /// <summary>Colour far terrain with the game's climate tints and raise forests (Earth Map; 0.4.0+).</summary>
    public bool FarTerrainClimate = true;
    /// <summary>Ore heat maps on the full map and `/wmapore` (WM-D47). Off by default from 0.5.3: a tool for checking ore
    /// generation. Its mismatch with the pick on vanilla's deposits was fixed in 0.5.3 (WM-0025); the default stays off until
    /// the author decides.</summary>
    public bool OreHeatMaps = false;
    /// <summary>Mid-distance view (WM-P06 prototype): record every loaded chunk out to the view distance, with the game's
    /// colours and block light, and draw recorded terrain over the prediction in far terrain out to
    /// <see cref="NearBandDistance"/>, with recorded structures as blocks. See docs/mid-distance-view.md.</summary>
    public bool MidDistanceRecorded = true;
    /// <summary>Mid-distance view: terraces (flat cells at whole-block heights with walls) from far terrain's start to
    /// <see cref="TerraceDistance"/>.</summary>
    public bool MidDistanceTerraces = true;
    /// <summary>Mid-distance view: recorded lights drawn as glows past the real chunks, out to <see cref="LightDistance"/>.</summary>
    public bool MidDistanceLights = true;
    /// <summary>Blocks from the player to the terraces' outer edge (256..768: their two-block tiles must fit the 8 × 8 slots).</summary>
    public int TerraceDistance = 768;
    /// <summary>Blocks from the player within which recorded lights are drawn (1024..3584).</summary>
    public int LightDistance = 3072;
    /// <summary>Show latitude, longitude and heading beside the HUD minimap on worlds with geography (Ctrl+Shift+V; 0.4.9).</summary>
    public bool CoordinatesHud = true;
    /// <summary>Show the local time (HH:MM) beside the coordinates, or alone in the box on worlds without geography: Astra
    /// Terra's local solar time where it is installed, else the solar time at Earth's or the map's longitude, else the
    /// game's hour (0.5.3, WM-0010).</summary>
    public bool CoordinatesHudTime = true;
    /// <summary>Mark things when you start harvesting or breaking them (0.4.6).</summary>
    public bool AutoMarkOnUse = true;
    /// <summary>Mark things you look at while sneaking (0.4.6).</summary>
    public bool AutoMarkOnSneak = true;
    /// <summary>The kinds of things marked automatically (<see cref="Core.AutoMarkCatalog"/> codes).</summary>
    public string[] AutoMarkKinds = { "berries", "mushrooms", "ores", "beehives", "resin", "fruittrees", "clay", "translocators", "traders" };
    /// <summary>What the atlas shows: your own waypoints, automatic markers, prospecting readings, other mods' layers.</summary>
    public bool ShowWaypoints = true, ShowAutoMarks = true, ShowProspecting = true, ShowOtherLayers = true;
    /// <summary>Show only prospecting readings that mention this ore (the game's ore code); empty shows all.</summary>
    public string ProspectingOre = "";

    public const int StyleCount = 2;
    public static string StyleName(int style) => style == 1 ? "Terrain" : "Chart";

    public void Validate()
    {
        WorldChartSize = Math.Clamp(WorldChartSize, 512, 8192);
        BakeThreads = Math.Clamp(BakeThreads, 0, 64);
        ResidentTiles = Math.Clamp(ResidentTiles, 128, 768);
        AtlasPages = Math.Clamp(AtlasPages, 1, 4);
        DiskCacheMiB = Math.Clamp(DiskCacheMiB, 64, 4096);
        ObservedRadiusChunks = Math.Clamp(ObservedRadiusChunks, 1, 8);
        ExploreRadius = Math.Clamp(ExploreRadius, 32, 4096);
        DefaultStyle = Math.Clamp(DefaultStyle, 0, StyleCount - 1);
        DetailBias = Math.Clamp(DetailBias, 0, 1);
        WindLayer = Math.Clamp(WindLayer, 0, Core.WindGrid.Choices - 1);
        BarometerRange = Math.Clamp(BarometerRange, 500, 12000);
        FarTerrainDistance = Math.Clamp(FarTerrainDistance, 2048, 16384);
        FarTerrainDetail = Math.Clamp(FarTerrainDetail, 0, 2);
        NearBandDistance = Math.Clamp(NearBandDistance, 768, 1536);
        TerraceDistance = Math.Clamp(TerraceDistance, 256, 768);
        LightDistance = Math.Clamp(LightDistance, 1024, 3584);
        SightDistance = Math.Clamp(SightDistance, 512, 16384);
        PlanetScale = double.IsFinite(PlanetScale) ? Math.Clamp(PlanetScale, 0, 16) : 1;
        SightCircle = Math.Clamp(SightCircle, 16, 1536);
        SightHoleSize = Math.Clamp(SightHoleSize, 0, 1024);
        AutoMarkLimit = Math.Clamp(AutoMarkLimit, 10, 10000);
        PaperMapMaxMiB = Math.Clamp(PaperMapMaxMiB, 1, 256);
        AutoMarkKinds ??= Array.Empty<string>();
        ProspectingOre ??= "";
    }

    public int Threads => BakeThreads > 0 ? BakeThreads : Math.Max(1, Environment.ProcessorCount / 2);
}
