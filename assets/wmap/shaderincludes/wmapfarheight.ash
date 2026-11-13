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
// FarFrame.JoinOverlap. terraces: 1 for the terraces, 0 for the smooth mesh.
const float JoinOverlap = 16.0;
float wmapJoinPush(float dist, float terraces)
{
    if (wmapPush.w <= 0.0) return 0.0;
    float past = (dist - wmapPush.w) / JoinOverlap;
    return wmapRange.z * clamp(terraces > 0.5 ? past : -past, 0.0, 1.0);
}

// The ground at a point (chart, near band and the four-block recordings): coast distance in blocks (land positive), height
// above the sea surface, ice; and the band's weight and biome for the forest.
vec3 wmapGroundAt(vec2 local, float lod, out float bandWeight, out vec4 bandBiome)
{
    vec4 chart = textureLod(wmapChart, wmapChartUV.xy + local * wmapChartUV.zw, lod);
    vec4 band;
    bandWeight = wmapBandAt(local, band, bandBiome);
    float recorded;
    vec4 color;
    return wmapWithRecorded(local, wmapSurface(chart, band, bandWeight), bandWeight, recorded, color);
}

// Forest canopy over the ground at a point, in blocks: on land away from the coast and off the ice.
float wmapCanopy(vec2 local, vec3 surface, float h, float bandWeight, vec4 bandBiome)
{
    vec4 biome = wmapBiomeAt(local, bandWeight, bandBiome);
    float land = smoothstep(2.0, 10.0, surface.x) * (1.0 - surface.z) * smoothstep(0.5, 1.5, h);
    return wmapBandRange.z * biome.r * biome.a * land;
}

// The displaced surface's height relative to the camera at a point: the ground, forest canopy on land, the push-down near
// the start and the planet's curve. Stitched vertices average this final value at the ends of their coarse edge, so every
// term is a function of position only and edges cannot crack.
float wmapFinalY(vec2 local, float lod)
{
    float w;
    vec4 bandBiome;
    vec3 surface = wmapGroundAt(local, lod, w, bandBiome);
    // The sea is a flat plane at the sea surface (its stored height is its bed); land keeps its height above it, and so
    // does water standing above the sea, which is stored at its surface (Core/Semantic.cs).
    float h = max(0.0, surface.y);
    h += wmapCanopy(local, surface, h, w, bandBiome);
    float dist = wmapCameraDistance(local);
    float push = wmapPushAt(dist, h, 1.0 - smoothstep(-2.0, 2.0, surface.x)) + wmapJoinPush(dist, 0.0);
    return wmapOrigin.w + h - push - wmapDrop(dist) - wmapOrigin.z;
}

// The ground alone, for shading: canopy and push-down are smooth and leave it as it is. From the prediction only: recorded
// ground differs from it by a few blocks at most (more is a structure, drawn as blocks), and four lookups of the
// recordings per vertex would cost more than they show.
float wmapGround(vec2 local, float lod)
{
    vec4 band, bandBiome;
    float w = wmapBandAt(local, band, bandBiome);
    return max(0.0, wmapSurface(textureLod(wmapChart, wmapChartUV.xy + local * wmapChartUV.zw, lod), band, w).y);
}
