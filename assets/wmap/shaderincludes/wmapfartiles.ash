// Wmap far terrain: the world chart, the near band and the climate tiles, shared by farterrain.vsh and .fsh.
// Tile addressing mirrors Core/FarTileWindow.cs: unwrapped tile ids (lap x tiles per lap + index, laps counted from
// a lap the client keeps across seam crossings), slots modulo 8, 130-texel padded tiles whose texel p is centred
// (p - 0.5) steps from the tile's corner. See docs/far-terrain-near-band.md.

// C#'s constants, by name (0.5.3, WM-0039: they were written out as numbers); the core tests check them against
// FarTileWindow.Slots, .SlotTexels, .AtlasTexels and .SeamReach. The uniform arrays below hold WmapSlots * WmapSlots slots.
const int WmapSlots = 8;
const float WmapSlotTexels = 130.0;
const float WmapAtlasTexels = 1040.0;
const float WmapSeamReach = 48.0;

uniform sampler2D wmapChart;
// A survival client (0.5.4, WM-D63): 1 where the chart holds only land the server has sent, which wmapKnown marks (R 1); the
// rest of the chart is empty and is not drawn.
uniform float wmapKnownOn;
uniform sampler2D wmapKnown;
uniform vec4 wmapChartInfo;               // blocks per texel, ring 0 radius, ring 0 cell, height range
uniform vec4 wmapOrigin;                  // snapped origin - camera (x, z), camera y, sea surface y
uniform vec4 wmapWorld;                   // world width, world height, 1 when it wraps east-west, unused
uniform vec2 wmapOriginWorld;             // snapped origin: blocks east of its lap's west edge, z
uniform int wmapOriginLap;                // the snapped origin's lap in the ids' count (FarTiles.OriginLap)
uniform vec4 wmapBand;                    // near band: tile span, texel step, tiles per lap; 0 off, 1 on, 2 on with a seam tile under 48 blocks
uniform ivec2 wmapBandKeys[WmapSlots * WmapSlots];   // per slot: the unwrapped tile id and row it holds
uniform float wmapBandFade[WmapSlots * WmapSlots];   // per slot: 0..1 faded in since it arrived
uniform vec4 wmapBandRange;               // fade start, band radius, forest canopy height, unused
uniform sampler2D wmapBandTex;            // semantic texels, as the chart (A: coverage)
uniform sampler2D wmapBandBiome;          // R forest, G stored temperature, B stored rain, A known
uniform vec4 wmapClimate;                 // climate tiles: as wmapBand
uniform ivec2 wmapClimateKeys[WmapSlots * WmapSlots];
uniform float wmapClimateFade[WmapSlots * WmapSlots];
uniform sampler2D wmapClimateBiome;
// The climate tiles' grass season (0.5.3-rc.5, Core/BiomeTile.cs SeasonSample): R Earth's grass map as its growing season
// (0: the game's own map), G 1 on the summer-dry and steppe maps, B and A the wet season's yearly wave (0.5 = none).
uniform sampler2D wmapClimateSeason;
// Recorded terrain, the mid-distance view (WM-P06 prototype; Client/RecordedTiles.cs): two 8 x 8-slot atlases side by side,
// four-block tiles (level 2) on the left and two-block tiles (level 1) on the right, addressed like the near band.
uniform vec4 wmapRec;                     // 1 when recorded terrain is drawn; unused; the ids' lap (RecordedTiles.Lap); 1 when the two-block window is on
uniform vec4 wmapRecCoarse;               // level 2: tile span, texel step, tiles per lap, unused
uniform vec4 wmapRecFine;                 // level 1: as wmapRecCoarse
uniform sampler2D wmapRecHeight;          // R top of the cell (world y: land's top face, water's surface), G coverage, B 1 where its colour was recorded, A water
uniform sampler2D wmapRecColor;           // RGB the game's colour
uniform sampler2D wmapRecSlots;           // 16 x 8, per slot (four-block left, two-block right): tile id, row, fade in

float wmapDecode(float v) { float e = v * (255.0 / 127.5) - 1.0; return sign(e) * e * e; }

// The tile under a point local blocks from the snapped origin: slot (x, z) and texel position inside it, x < 0 outside
// the world; its unwrapped id and row; and the point in blocks east of the origin lap's west edge (rel) and z.
vec4 wmapTileAtLap(vec2 local, vec4 layer, int originLap, out ivec2 key, out vec2 at)
{
    float span = layer.x, step = layer.y;
    int n = int(layer.z + 0.5);
    float rel = wmapOriginWorld.x + local.x, z = wmapOriginWorld.y + local.y, width = wmapWorld.x;
    key = ivec2(0);
    at = vec2(rel, z);
    if ((wmapWorld.z < 0.5 && (rel < 0.0 || rel >= width)) || z < 0.0 || z >= wmapWorld.y) return vec4(-1.0);
    float lap = wmapWorld.z > 0.5 ? floor(rel / width) : 0.0;
    float inLap = rel - lap * width;
    int i = clamp(int(floor(inLap / span)), 0, n - 1);
    int id = (originLap + int(lap)) * n + i, j = int(floor(z / span));
    key = ivec2(id, j);
    int sx = id - WmapSlots * int(floor(float(id) / float(WmapSlots))), sz = j - WmapSlots * (j / WmapSlots);
    return vec4(float(sx), float(sz), (inLap - float(i) * span) / step + 1.0, (z - float(j) * span) / step + 1.0);
}

vec4 wmapTileAt(vec2 local, vec4 layer, out ivec2 key, out vec2 at) { return wmapTileAtLap(local, layer, wmapOriginLap, key, at); }

vec2 wmapAtlasUV(vec4 s) { return (s.xy * WmapSlotTexels + s.zw) / WmapAtlasTexels; }

float wmapCameraDistance(vec2 local) { return length(local + wmapOrigin.xy); }

// A tile's readiness, 0..1: its slot holds it (faded in so far), else 0. layer 0 the near band, 1 the climate.
float wmapSlotReady(int layer, int id, int j)
{
    if (j < 0) return 0.0;
    int i = (j - WmapSlots * (j / WmapSlots)) * WmapSlots + (id - WmapSlots * int(floor(float(id) / float(WmapSlots))));
    if (layer == 0) return wmapBandKeys[i] == ivec2(id, j) ? wmapBandFade[i] : 0.0;
    return wmapClimateKeys[i] == ivec2(id, j) ? wmapClimateFade[i] : 0.0;
}

// How ready a layer is at a point, 0..1. Every tile within 48 blocks of the point, its own included (at distance 0),
// scales it by mix(smoothstep(0, 48, distance to that tile), 1, the tile's readiness). The same tiles are within
// reach from either side of any edge or corner, so the field is continuous everywhere: detail fades out over 48
// blocks toward a tile that is missing or still fading in, also diagonally and across a short seam tile.
float wmapReadyAt(int layer, vec4 l, vec2 at, ivec2 key)
{
    float span = l.x;
    int n = int(l.z + 0.5), reach = l.w > 1.5 ? 2 : 1;
    float ready = 1.0;
    for (int k = -reach; k <= reach; k++)
    {
        int id = key.x + k, rid = id - wmapOriginLap * n;
        int lap = int(floor(float(rid) / float(n))), index = rid - lap * n;
        float west = float(lap) * wmapWorld.x + float(index) * span, width = min(span, wmapWorld.x - float(index) * span);
        float dx = max(0.0, max(west - at.x, at.x - (west + width)));
        if (dx >= WmapSeamReach) continue;
        for (int m = -1; m <= 1; m++)
        {
            float north = float(key.y + m) * span;
            float d = length(vec2(dx, max(0.0, max(north - at.y, at.y - (north + span)))));
            if (d >= WmapSeamReach) continue;
            ready *= mix(smoothstep(0.0, WmapSeamReach, d), 1.0, wmapSlotReady(layer, id, key.y + m));
            if (ready <= 0.0) return 0.0;
        }
    }
    return ready;
}

// The near band at a point: its semantic texel and biome, and its weight: coverage x readiness x the fade toward
// the band's outer edge. 0 where not loaded, unknown or past the band.
// tile: where the point lies in the band's atlas (wmapTileAt), for the detail normal (wmapDetailNormalFrom), so a pixel looks
// the band up once (0.5.3, WM-0032: the detail normal looked it up twice more).
float wmapBandLookup(vec2 local, out vec4 semantic, out vec4 biome, out vec4 tile)
{
    semantic = vec4(0.0); biome = vec4(0.0); tile = vec4(-1.0);
    if (wmapBand.w < 0.5) return 0.0;
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAt(local, wmapBand, key, at);
    tile = s;
    if (s.x < 0.0) return 0.0;
    float ready = wmapReadyAt(0, wmapBand, at, key);
    if (ready <= 0.0) return 0.0;
    vec2 uv = wmapAtlasUV(s);
    semantic = textureLod(wmapBandTex, uv, 0.0);
    biome = textureLod(wmapBandBiome, uv, 0.0);
    return semantic.a * ready * (1.0 - smoothstep(wmapBandRange.x, wmapBandRange.y, wmapCameraDistance(local)));
}
float wmapBandAt(vec2 local, out vec4 semantic, out vec4 biome) { vec4 tile; return wmapBandLookup(local, semantic, biome, tile); }

// Forest and climate at a point, A = how known (0..1): the climate tiles and the near band, each weighted by its
// own readiness, so neither tile arrival nor the band's edge changes colours in a step.
// The climate tile at a point: its readiness (0 where there is none) and its atlas uv. One lookup serves the biome and the
// grass season (0.5.3, WM-0032: each looked the tile up and walked the readiness loop on its own).
float wmapClimateTile(vec2 local, out vec2 uv)
{
    uv = vec2(0.0);
    if (wmapClimate.w < 0.5) return 0.0;
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAt(local, wmapClimate, key, at);
    if (s.x < 0.0) return 0.0;
    uv = wmapAtlasUV(s);
    return wmapReadyAt(1, wmapClimate, at, key);
}

vec4 wmapBiomeFrom(float climateReady, vec2 climateUV, float bandWeight, vec4 bandBiome)
{
    vec4 climate = vec4(0.0);
    float climateWeight = 0.0;
    if (climateReady > 0.0) { climate = textureLod(wmapClimateBiome, climateUV, 0.0); climateWeight = climateReady * climate.a; }
    // The band's finer biome over the climate tiles', each by its own weight, normalised: continuous as either
    // weight goes to zero.
    float b = bandWeight * bandBiome.a, c = climateWeight * (1.0 - b), total = b + c;
    if (total <= 0.0) return vec4(0.0);
    return vec4((bandBiome.rgb * b + climate.rgb * c) / total, total);
}
vec4 wmapBiomeAt(vec2 local, float bandWeight, vec4 bandBiome) { vec2 uv; float ready = wmapClimateTile(local, uv); return wmapBiomeFrom(ready, uv, bandWeight, bandBiome); }

// The grass season at a point from the climate tiles (SeasonSample), by their readiness: none (the game's own season map,
// no wet season) where they are not there or carry none (wmapSeason.w: the tiles have a season texture).
uniform vec4 wmapSeason;                  // the game's seasonRel; the calendar's year (0..1); 1 when the season maps are loaded; 1 when the tiles carry seasons
vec4 wmapSeasonFrom(float climateReady, vec2 climateUV)
{
    vec4 none = vec4(0.0, 0.0, 128.0 / 255.0, 128.0 / 255.0);
    if (wmapSeason.w < 0.5 || climateReady <= 0.0) return none;
    return mix(none, textureLod(wmapClimateSeason, climateUV, 0.0), climateReady * textureLod(wmapClimateBiome, climateUV, 0.0).a);
}
vec4 wmapSeasonAt(vec2 local) { vec2 uv; float ready = wmapClimateTile(local, uv); return wmapSeasonFrom(ready, uv); }

// The water's level among the four texels a filtered lookup at uv blends (0.5.0-pre.11): the highest water texel's height
// (coast distance below zero), at least the sea surface (0). Filtering blends a sea texel's bed with the cliff beside it,
// so where the filtered coast distance still says water the filtered height reads several blocks above the sea (up to 9
// on Earth's 512-block world above Nice), and water drawn up to it climbed the coast; a lake keeps its own surface.
float wmapWaterLevel(sampler2D tex, vec2 uv)
{
    ivec2 size = textureSize(tex, 0);
    ivec2 p = ivec2(floor(uv * vec2(size) - 0.5));
    float level = 0.0;
    for (int i = 0; i < 4; i++)
    {
        ivec2 q = p + ivec2(i & 1, i >> 1);
        q = ivec2((q.x % size.x + size.x) % size.x, clamp(q.y, 0, size.y - 1));
        vec4 t = texelFetch(tex, q, 0);
        if (t.r < 0.5) level = max(level, wmapDecode(t.g) * wmapChartInfo.w);
    }
    return level;
}

// The water's level at a point from the near band's tile (see wmapWaterLevel); 0 outside it. From the tile the band's lookup
// found (wmapBandLookup; 0.5.3, WM-0032), or looked up here.
float wmapBandWaterLevelFrom(vec4 s) { return s.x < 0.0 ? 0.0 : wmapWaterLevel(wmapBandTex, wmapAtlasUV(s)); }
float wmapBandWaterLevel(vec2 local)
{
    ivec2 key;
    vec2 at;
    return wmapBandWaterLevelFrom(wmapTileAt(local, wmapBand, key, at));
}

// The near band's ground with its forest canopy at a texel offset from a tile position s (wmapTileAt), in blocks above the
// sea: what the detail normal is taken from. Offsets stay inside the tile's one-texel padding, which holds its neighbours'.
float wmapBandTop(vec4 s, vec2 offset)
{
    vec2 p = clamp(s.zw + offset, vec2(0.5), vec2(129.5));
    vec2 uv = (s.xy * WmapSlotTexels + p) / WmapAtlasTexels;
    vec4 t = textureLod(wmapBandTex, uv, 0.0);
    vec4 b = textureLod(wmapBandBiome, uv, 0.0);
    float h = max(0.0, wmapDecode(t.g) * wmapChartInfo.w);
    float coast = wmapDecode(t.r) * 128.0 * wmapBand.y;
    float land = smoothstep(2.0, 10.0, coast) * (1.0 - t.b) * smoothstep(0.5, 1.5, h);
    return h + wmapBandRange.z * b.r * b.a * land;
}

// The near band's own relief at a point as a normal (0.5.3-rc.4): from its four-block texels on either side, with the forest
// canopy on land, so slopes, valleys and forest edges finer than a mesh cell (8 to 32 blocks in the band) shade each pixel.
// xyz the normal, w how much it counts: the band's weight, faded out where a pixel spans several texels (no shimmer).
vec4 wmapDetailNormalFrom(vec4 s, float w, float footprint)
{
    if (w <= 0.0 || s.x < 0.0) return vec4(0.0);
    float step = wmapBand.y;
    float hx = wmapBandTop(s, vec2(1.0, 0.0)) - wmapBandTop(s, vec2(-1.0, 0.0));
    float hz = wmapBandTop(s, vec2(0.0, 1.0)) - wmapBandTop(s, vec2(0.0, -1.0));
    return vec4(normalize(vec3(-hx, 2.0 * step, -hz)), w * (1.0 - smoothstep(step, 3.0 * step, footprint)));
}

// Coast distance in blocks (land positive), height above the sea surface in blocks, ice 0..1 at a point,
// the chart and the near band blended by the band's weight.
vec3 wmapSurface(vec4 chart, vec4 band, float bandWeight)
{
    vec3 c = vec3(wmapDecode(chart.r) * 128.0 * wmapChartInfo.x, wmapDecode(chart.g) * wmapChartInfo.w, chart.b);
    if (bandWeight <= 0.0) return c;
    vec3 b = vec3(wmapDecode(band.r) * 128.0 * wmapBand.y, wmapDecode(band.g) * wmapChartInfo.w, band.b);
    return mix(c, b, bandWeight);
}

// Recorded terrain at a point: weight 0..1 (coverage x the tile's fade in), and its texels: height (R top, G coverage, B colour
// recorded, A water) and colour (RGB the game's). level 0: four-block tiles, 1: two-block tiles. filtered: between texels
// (the smooth mesh); else the texel the point lies in (terraces). 0 where nothing is recorded or its tile is not in its slot.
// A recording's tile at a point: xy the point's texel in the recorded atlas, z the tile's fade in; z 0 where nothing is
// recorded there or its tile is not in its slot. One lookup serves the height and the canopy (0.5.3, WM-0032).
vec3 wmapRecordedTexel(vec2 local, int level)
{
    if (wmapRec.x < 0.5 || (level == 1 && wmapRec.w < 0.5)) return vec3(0.0);
    vec4 layer = level == 0 ? wmapRecCoarse : wmapRecFine;
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAtLap(local, layer, int(wmapRec.z), key, at);
    if (s.x < 0.0) return vec3(0.0);
    vec4 slot = texelFetch(wmapRecSlots, ivec2(int(s.x) + WmapSlots * level, int(s.y)), 0);
    if (slot.x != float(key.x) || slot.y != float(key.y) || slot.z <= 0.0) return vec3(0.0);
    return vec3(s.x * WmapSlotTexels + float(level) * WmapAtlasTexels + s.z, s.y * WmapSlotTexels + s.w, slot.z);
}

// How much the four-block recordings count at a point toward the near band's edge: 1 inside it, fading out to its radius.
float wmapRecordedFade(vec2 local) { return 1.0 - smoothstep(wmapBandRange.x, wmapBandRange.y, wmapCameraDistance(local)); }

float wmapRecordedFrom(vec3 t, bool filtered, out vec4 height, out vec4 color)
{
    height = vec4(0.0); color = vec4(0.0);
    if (t.z <= 0.0) return 0.0;
    vec2 texel = t.xy;
    if (filtered)
    {
        vec2 uv = texel / vec2(2.0 * WmapAtlasTexels, WmapAtlasTexels);
        height = textureLod(wmapRecHeight, uv, 0.0);
        color = textureLod(wmapRecColor, uv, 0.0);
    }
    else
    {
        ivec2 p = ivec2(floor(texel));
        height = texelFetch(wmapRecHeight, p, 0);
        color = texelFetch(wmapRecColor, p, 0);
    }
    return height.g * t.z;
}
float wmapRecordedAt(vec2 local, int level, bool filtered, out vec4 height, out vec4 color)
{
    return wmapRecordedFrom(wmapRecordedTexel(local, level), filtered, height, color);
}

// The recorded forest canopy at a point (0.5.3-rc.4, WM-0020): the blocks from the ground up to the treetops the recorder
// saw over land (the colour texture's alpha, a quarter block a step; 255 where not known: water, recordings from before),
// the mean of the four-block texels within radius texels of the point's; y how much it counts: the share of those texels that
// know it, by the tile's fade in and toward the band's edge. 0 where nothing recorded knows it.
// From a four-block texel (wmapRecordedTexel, level 0) and the band's fade (wmapRecordedFade).
vec2 wmapRecordedCanopyFrom(vec3 t, float fade, int radius)
{
    if (t.z <= 0.0 || fade <= 0.0) return vec2(0.0);
    ivec2 base = ivec2(floor(t.xy / WmapSlotTexels)) * int(WmapSlotTexels), p = ivec2(floor(t.xy)) - base;
    float sum = 0.0, known = 0.0, count = 0.0;
    for (int dz = -radius; dz <= radius; dz++)
    for (int dx = -radius; dx <= radius; dx++)
    {
        ivec2 q = base + clamp(p + ivec2(dx, dz), ivec2(0), ivec2(129));
        count += 1.0;
        if (texelFetch(wmapRecHeight, q, 0).g < 0.5) continue;
        float a = texelFetch(wmapRecColor, q, 0).a;
        if (a > 0.995) continue;
        sum += a * 255.0 / 4.0;
        known += 1.0;
    }
    if (known <= 0.0) return vec2(0.0);
    return vec2(sum / known, t.z * fade * known / count);
}
vec2 wmapRecordedCanopy(vec2 local, int radius)
{
    if (wmapRec.x < 0.5) return vec2(0.0);
    float fade = wmapRecordedFade(local);
    if (fade <= 0.0) return vec2(0.0);
    return wmapRecordedCanopyFrom(wmapRecordedTexel(local, 0), fade, radius);
}

// The ground with the four-block recordings over it, as the smooth mesh draws it, fading out toward the band's edge. The
// recordings decide land or water and the colour. Heights: the sea keeps the prediction's bed (its depth colours the water),
// a lake its recorded surface, land its recorded height (each four-block sample holds its cell's mean generated ground,
// WM-D61). Where a recording ends the height steps from the recording to the prediction over one texel: the prediction's
// own error there. color: RGB the game's colour, A how much of it was recorded (none in recordings made before 0.5).
// From a four-block texel (wmapRecordedTexel, level 0) and the band's fade (wmapRecordedFade).
vec3 wmapWithRecordedFrom(vec3 t, float fade, vec3 surface, out float weight, out vec4 color)
{
    vec4 h;
    weight = wmapRecordedFrom(t, true, h, color) * fade;
    color.a = h.b;
    if (weight <= 0.0) return surface;
    float rel = h.r - wmapOrigin.w;
    bool water = h.a > 0.5;
    // Recorded land at its recorded height (0.5.3, WM-0021, WM-D61): each four-block sample now holds its cell's mean
    // generated ground; before, land took the band's predicted height wherever the band predicted land.
    float y = water ? (rel < 0.5 ? min(surface.y, rel) : rel) : rel;
    return vec3(mix(surface.x, (0.5 - h.a) * 32.0, weight), mix(surface.y, y, weight), surface.z * (1.0 - weight));
}
vec3 wmapWithRecorded(vec2 local, vec3 surface, float bandWeight, out float weight, out vec4 color)
{
    return wmapWithRecordedFrom(wmapRecordedTexel(local, 0), wmapRecordedFade(local), surface, weight, color);
}
