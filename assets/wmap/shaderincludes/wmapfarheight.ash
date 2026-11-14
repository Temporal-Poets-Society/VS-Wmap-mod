// Wmap far terrain: the displaced surface's height, shared by farterrain.vsh (the smooth mesh), farcells.vsh (terraces and
// recorded structures) and farcellmap.fsh (the terraces' cell tops). Include after wmapfartiles.ash.

uniform vec4 wmapChartUV;                 // chart uv of the snapped origin, 1 / world width, 1 / world height
uniform vec4 wmapRange;                   // draw from, draw to (blocks from the camera), land push-down at the start, 1 when the world wraps east-west
uniform vec2 wmapCurve;                   // 1 / (2 x the planet's radius) in 1/blocks (0: flat); blocks where the curve starts
uniform vec4 wmapPush;                    // push-down: blocks over which it fades out; far terrain's far edge; blocks from the camera where far terrain (and its push-down) starts; where the terraces end (0: none)

// The planet's curve (0.4.11): past the view distance the ground sinks by (d - d0)^2 / 2R, so low coasts drop under the
// horizon and only high ground shows from far away. Flat out to d0, like the game's own chunks: they fade into this
// mesh between 75 and 85 % of the view distance, and a sea curving away under fading water would leave a gap there.
// Tangent at d0, so the sea has no crease. A function of position only, like every term of the height.
float wmapDrop(float dist) { float over = max(0.0, dist - wmapCurve.y); return wmapCurve.x * over * over; }

// Where the game's chunks fade out, far terrain sits a little low so real terrain reaching further covers it: land by up to
// wmapRange.z blocks, water by one. The sea then lies just under the real water surface, so fading real water shows this
// sea behind it, not a sea twelve blocks down and far away. Lakes are water too: one block, like the sea.
float wmapPushAt(float dist, float h, float wet)
{
    return mix(min(wmapRange.z, 1.0 + h), 1.0, wet) * (1.0 - clamp((dist - wmapPush.z) / wmapPush.x, 0.0, 1.0));
}

// The join between the terraces and the smooth mesh (0.5.0-pre.10). Each reaches JoinOverlap blocks past the terraces'
// edge into the other and sinks under it toward its own cut, by up to the land push-down: the smooth mesh toward its inner
// edge, the terraces toward their outer one. A cut then always lies below the other surface. Before, both were cut at the
// edge, and where the smooth mesh began higher than the last terrace (a coast, a slope), nothing closed the step and
// single pixels of sky showed through along the ring. At the edge itself neither is lowered. JoinOverlap is
// TerracePatch.JoinOverlap (the core tests check it). terraces: 1 for the terraces, 0 for the smooth mesh.
const float JoinOverlap = 16.0;
float wmapJoinPush(float dist, float terraces)
{
    if (wmapPush.w <= 0.0) return 0.0;
    float past = (dist - wmapPush.w) / JoinOverlap;
    return wmapRange.z * clamp(terraces > 0.5 ? past : -past, 0.0, 1.0);
}

// The ground at a point (chart, near band and the four-block recordings): coast distance in blocks (land positive), height
// above the sea surface, ice; and the band's weight and biome for the forest.
// From the four-block recording's texel there (wmapRecordedTexel, level 0) and the band's fade (wmapRecordedFade).
vec3 wmapGroundFrom(vec2 local, float lod, vec3 recTexel, float recFade, out float bandWeight, out vec4 bandBiome)
{
    vec4 chart = textureLod(wmapChart, wmapChartUV.xy + local * wmapChartUV.zw, lod);
    vec4 band;
    bandWeight = wmapBandAt(local, band, bandBiome);
    float recorded;
    vec4 color;
    return wmapWithRecordedFrom(recTexel, recFade, wmapSurface(chart, band, bandWeight), recorded, color);
}
vec3 wmapGroundAt(vec2 local, float lod, out float bandWeight, out vec4 bandBiome)
{
    return wmapGroundFrom(local, lod, wmapRecordedTexel(local, 0), wmapRecordedFade(local), bandWeight, bandBiome);
}

// Forest canopy over the ground at a point, in blocks: on land away from the coast and off the ice. Predicted, a full forest
// stands wmapBandRange.z blocks over the land (the recorded forest's middle treetop height around the camera, else 8;
// 0.5.3-rc.8); where the recorder saw the treetops (0.5.3-rc.4), their mean height over the texels around, so a discovered
// forest stands at its own height, with its clearings.
// recorded: the recorded canopy there (wmapRecordedCanopy).
float wmapCanopyWith(vec2 local, vec3 surface, float h, float bandWeight, vec4 bandBiome, vec2 recorded)
{
    vec4 biome = wmapBiomeAt(local, bandWeight, bandBiome);
    float land = smoothstep(2.0, 10.0, surface.x) * (1.0 - surface.z) * smoothstep(0.5, 1.5, h);
    // Only forests stand as a canopy (0.5.3-rc.8): where the climate supports less than about half a forest, trees stand
    // scattered and the land shows between them; rc.7 raised them all as a plate of 8 x the share (3 blocks over the open
    // dry land by Catania, a step where the recording ended).
    float predicted = wmapBandRange.z * smoothstep(0.35, 0.75, biome.r) * biome.a * land;
    return mix(predicted, recorded.x, recorded.y);
}
float wmapCanopy(vec2 local, vec3 surface, float h, float bandWeight, vec4 bandBiome)
{
    return wmapCanopyWith(local, surface, h, bandWeight, bandBiome, wmapRecordedCanopy(local, 1));
}

// The displaced surface's height relative to the camera at a point: the ground, forest canopy on land, the push-down near
// the start and the planet's curve. Stitched vertices take it at the end of their coarse edge they collapse onto, so every
// term is a function of position only and edges cannot crack.
float wmapFinalY(vec2 local, float lod)
{
    float w;
    vec4 bandBiome;
    // One lookup of the four-block recording for the ground and the canopy (0.5.3, WM-0032).
    vec3 recTexel = wmapRecordedTexel(local, 0);
    float recFade = wmapRecordedFade(local);
    vec3 surface = wmapGroundFrom(local, lod, recTexel, recFade, w, bandBiome);
    // The sea is a flat plane at the sea surface (its stored height is its bed); land keeps its height above it, and so
    // does water standing above the sea, which is stored at its surface (Core/Semantic.cs).
    float h = max(0.0, surface.y);
    h += wmapCanopyWith(local, surface, h, w, bandBiome, wmapRecordedCanopyFrom(recTexel, recFade, 1));
    float dist = wmapCameraDistance(local);
    float push = wmapPushAt(dist, h, 1.0 - smoothstep(-2.0, 2.0, surface.x)) + wmapJoinPush(dist, 0.0);
    return wmapOrigin.w + h - push - wmapDrop(dist) - wmapOrigin.z;
}

// The ground with its forest canopy, for shading: the push-down is smooth and leaves it as it is. From the prediction only:
// recorded ground differs from it by a few blocks at most (more is a structure, drawn as blocks), and four lookups of the
// recordings per vertex would cost more than they show. The canopy counts since 0.5.3-rc.4: before, forest edges were
// never shaded, and a forest read as a flat plate raised over the land.
float wmapGround(vec2 local, float lod)
{
    vec4 band, bandBiome;
    float w = wmapBandAt(local, band, bandBiome);
    vec3 surface = wmapSurface(textureLod(wmapChart, wmapChartUV.xy + local * wmapChartUV.zw, lod), band, w);
    float h = max(0.0, surface.y);
    return h + wmapCanopy(local, surface, h, w, bandBiome);
}
