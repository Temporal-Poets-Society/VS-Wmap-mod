// Wmap far terrain: the world chart, the near band and the climate tiles, shared by farterrain.vsh and .fsh.
// Tile addressing mirrors Core/FarTileWindow.cs: unwrapped tile ids (lap x tiles per lap + index, laps counted from
// a lap the client keeps across seam crossings), slots modulo 8, 130-texel padded tiles whose texel p is centred
// (p - 0.5) steps from the tile's corner. See docs/far-terrain-near-band.md.

uniform sampler2D wmapChart;
uniform vec4 wmapChartInfo;               // blocks per texel, ring 0 radius, ring 0 cell, height range
uniform vec4 wmapOrigin;                  // snapped origin - camera (x, z), camera y, sea surface y
uniform vec4 wmapWorld;                   // world width, world height, 1 when it wraps east-west, unused
uniform vec2 wmapOriginWorld;             // snapped origin: blocks east of its lap's west edge, z
uniform int wmapOriginLap;                // the snapped origin's lap in the ids' count (FarTiles.OriginLap)
uniform vec4 wmapBand;                    // near band: tile span, texel step, tiles per lap; 0 off, 1 on, 2 on with a seam tile under 48 blocks
uniform ivec2 wmapBandKeys[64];           // per slot: the unwrapped tile id and row it holds
uniform float wmapBandFade[64];           // per slot: 0..1 faded in since it arrived
uniform vec4 wmapBandRange;               // fade start, band radius, forest canopy height, unused
uniform sampler2D wmapBandTex;            // semantic texels, as the chart (A: coverage)
uniform sampler2D wmapBandBiome;          // R forest, G stored temperature, B stored rain, A known
uniform vec4 wmapClimate;                 // climate tiles: as wmapBand
uniform ivec2 wmapClimateKeys[64];
uniform float wmapClimateFade[64];
uniform sampler2D wmapClimateBiome;
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
    int sx = id - 8 * int(floor(float(id) / 8.0)), sz = j - 8 * (j / 8);
    return vec4(float(sx), float(sz), (inLap - float(i) * span) / step + 1.0, (z - float(j) * span) / step + 1.0);
}

vec4 wmapTileAt(vec2 local, vec4 layer, out ivec2 key, out vec2 at) { return wmapTileAtLap(local, layer, wmapOriginLap, key, at); }

vec2 wmapAtlasUV(vec4 s) { return (s.xy * 130.0 + s.zw) / 1040.0; }

float wmapCameraDistance(vec2 local) { return length(local + wmapOrigin.xy); }

// A tile's readiness, 0..1: its slot holds it (faded in so far), else 0. layer 0 the near band, 1 the climate.
float wmapSlotReady(int layer, int id, int j)
{
    if (j < 0) return 0.0;
    int i = (j - 8 * (j / 8)) * 8 + (id - 8 * int(floor(float(id) / 8.0)));
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
        if (dx >= 48.0) continue;
        for (int m = -1; m <= 1; m++)
        {
            float north = float(key.y + m) * span;
            float d = length(vec2(dx, max(0.0, max(north - at.y, at.y - (north + span)))));
            if (d >= 48.0) continue;
            ready *= mix(smoothstep(0.0, 48.0, d), 1.0, wmapSlotReady(layer, id, key.y + m));
            if (ready <= 0.0) return 0.0;
        }
    }
    return ready;
}

// The near band at a point: its semantic texel and biome, and its weight: coverage x readiness x the fade toward
// the band's outer edge. 0 where not loaded, unknown or past the band.
float wmapBandAt(vec2 local, out vec4 semantic, out vec4 biome)
{
    semantic = vec4(0.0); biome = vec4(0.0);
    if (wmapBand.w < 0.5) return 0.0;
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAt(local, wmapBand, key, at);
    if (s.x < 0.0) return 0.0;
    float ready = wmapReadyAt(0, wmapBand, at, key);
    if (ready <= 0.0) return 0.0;
    vec2 uv = wmapAtlasUV(s);
    semantic = textureLod(wmapBandTex, uv, 0.0);
    biome = textureLod(wmapBandBiome, uv, 0.0);
    return semantic.a * ready * (1.0 - smoothstep(wmapBandRange.x, wmapBandRange.y, wmapCameraDistance(local)));
}

// Forest and climate at a point, A = how known (0..1): the climate tiles and the near band, each weighted by its
// own readiness, so neither tile arrival nor the band's edge changes colours in a step.
vec4 wmapBiomeAt(vec2 local, float bandWeight, vec4 bandBiome)
{
    vec4 climate = vec4(0.0);
    float climateWeight = 0.0;
    if (wmapClimate.w > 0.5)
    {
        ivec2 key;
        vec2 at;
        vec4 s = wmapTileAt(local, wmapClimate, key, at);
        if (s.x >= 0.0)
        {
            climateWeight = wmapReadyAt(1, wmapClimate, at, key);
            if (climateWeight > 0.0) { climate = textureLod(wmapClimateBiome, wmapAtlasUV(s), 0.0); climateWeight *= climate.a; }
        }
    }
    // The band's finer biome over the climate tiles', each by its own weight, normalised: continuous as either
    // weight goes to zero.
    float b = bandWeight * bandBiome.a, c = climateWeight * (1.0 - b), total = b + c;
    if (total <= 0.0) return vec4(0.0);
    return vec4((bandBiome.rgb * b + climate.rgb * c) / total, total);
}

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

// The water's level at a point from the near band's tile (see wmapWaterLevel); 0 outside it.
float wmapBandWaterLevel(vec2 local)
{
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAt(local, wmapBand, key, at);
    return s.x < 0.0 ? 0.0 : wmapWaterLevel(wmapBandTex, wmapAtlasUV(s));
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
float wmapRecordedAt(vec2 local, int level, bool filtered, out vec4 height, out vec4 color)
{
    height = vec4(0.0); color = vec4(0.0);
    if (wmapRec.x < 0.5 || (level == 1 && wmapRec.w < 0.5)) return 0.0;
    vec4 layer = level == 0 ? wmapRecCoarse : wmapRecFine;
    ivec2 key;
    vec2 at;
    vec4 s = wmapTileAtLap(local, layer, int(wmapRec.z), key, at);
    if (s.x < 0.0) return 0.0;
    vec4 slot = texelFetch(wmapRecSlots, ivec2(int(s.x) + 8 * level, int(s.y)), 0);
    if (slot.x != float(key.x) || slot.y != float(key.y) || slot.z <= 0.0) return 0.0;
    vec2 texel = vec2(s.x * 130.0 + float(level) * 1040.0, s.y * 130.0) + s.zw;
    if (filtered)
    {
        vec2 uv = texel / vec2(2080.0, 1040.0);
        height = textureLod(wmapRecHeight, uv, 0.0);
        color = textureLod(wmapRecColor, uv, 0.0);
    }
    else
    {
        ivec2 p = ivec2(floor(texel));
        height = texelFetch(wmapRecHeight, p, 0);
        color = texelFetch(wmapRecColor, p, 0);
    }
    return height.g * slot.z;
}

// The ground with the four-block recordings over it, as the smooth mesh draws it, fading out toward the band's edge. The
// recordings decide land or water and the colour. Heights: the sea keeps the prediction's bed (its depth colours the water),
// a lake its recorded surface; land keeps the near band's predicted height where it predicts land, since the recorder
// records a cell block by block wherever the world differs from it by more than 3 blocks (WM-D29, WM-D33): a four-block
// sample stands for its 4 x 4 cell and only adds noise on slopes, and what does differ (structures, edits) is drawn as
// blocks (farcells.vsh). Elsewhere the recorded height. color: RGB the game's colour, A how much of it was recorded (none
// in recordings made before 0.5).
vec3 wmapWithRecorded(vec2 local, vec3 surface, float bandWeight, out float weight, out vec4 color)
{
    vec4 h;
    weight = wmapRecordedAt(local, 0, true, h, color) * (1.0 - smoothstep(wmapBandRange.x, wmapBandRange.y, wmapCameraDistance(local)));
    color.a = h.b;
    if (weight <= 0.0) return surface;
    float rel = h.r - wmapOrigin.w;
    bool water = h.a > 0.5;
    float y = water ? (rel < 0.5 ? min(surface.y, rel) : rel) : (bandWeight > 0.5 && surface.x >= 0.0 ? surface.y : rel);
    return vec3(mix(surface.x, (0.5 - h.a) * 32.0, weight), mix(surface.y, y, weight), surface.z * (1.0 - weight));
}
